using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Nivalis;
using Nivalis.Apartment;
using Nivalis.GhostSystem.CustomerLoop;
using Nivalis.Locale;
using UnityEngine;
using ApartmentAsset = Nivalis.Apartment.Apartment;
using Object = UnityEngine.Object;

namespace NivalisTogether.Dedicated
{
    public sealed class PreparedPropertyChange
    {
        public string AccountId { get; internal set; }
        public string PlayerId { get; internal set; }
        public string PropertyId { get; internal set; }
        public string Kind { get; internal set; }
        public string Operation { get; internal set; }
        public string RequestId { get; internal set; }
        public bool NativeStarted { get; internal set; }
        public bool NativeCompleted { get; internal set; }
        internal BaseProperty Property;
        internal string PriorOwner;
    }

    // Session owns authenticated accounts, account-inventory activation, and the
    // atomic ledger/native-checkpoint commit. No success is emitted until its
    // Committed callback; competing property requests hold a single reservation.
    public static partial class PropertyOwnershipReplication
    {
        private static readonly Dictionary<string,string> Owners = new Dictionary<string,string>(StringComparer.Ordinal);
        private static readonly Dictionary<string,PreparedPropertyChange> Reservations = new Dictionary<string,PreparedPropertyChange>(StringComparer.Ordinal);
        private static readonly Dictionary<string,(PlayerPose Pose,float Time)> Players = new Dictionary<string,(PlayerPose,float)>();
        private static readonly Dictionary<string,WorldEvent> Results = new Dictionary<string,WorldEvent>();
        private static readonly Queue<string> ResultOrder = new Queue<string>();
        private static readonly List<Il2CppSystem.Action> Callbacks = new List<Il2CppSystem.Action>();
        private static bool configured, worker, patched;
        private static string localAccount="", starter="";
        private static float nextNotice;
        public static Action<InteractionRequest> OutboundInteraction { get; set; }
        public static Action<WorldEvent> OutboundWorldEvent { get; set; }
        public static Action<string> Notice { get; set; }
        public static Action<PreparedPropertyChange> BeginTransaction { get; set; }
        public static string StarterPropertyId => starter;
        public static bool HasPendingTransaction => Reservations.Count!=0;

        public static void Configure(bool isWorker,string accountId)
        {
            configured=false; Owners.Clear(); Reservations.Clear(); Players.Clear(); Results.Clear(); ResultOrder.Clear();
            ResetStateAssembly();
            starter=""; worker=isWorker; localAccount=accountId??"";
            if(!worker&&!PropertyOwnershipPolicy.AccountId(localAccount))throw new InvalidDataException("Persistent account required for property access.");
            InstallPatches(); configured=true;
        }
        public static void Stop()
        {
            configured=false; Owners.Clear(); Reservations.Clear(); Players.Clear(); Results.Clear(); ResultOrder.Clear(); Callbacks.Clear();
            ResetStateAssembly();
            starter=localAccount=""; nextNotice=0;
        }
        public static void UpdatePlayerPose(PlayerPose pose)
        { if(pose!=null&&DedicatedProtocol.PoseValid(pose))Players[pose.PlayerId]=(pose,Time.realtimeSinceStartup); }
        public static void RemovePlayer(string playerId) { if(playerId!=null)Players.Remove(playerId); }
        public static string DeriveStarterPropertyId()
        {
            if(!DedicatedWorldRuntime.IsDedicatedServer||!DedicatedWorldRuntime.IsWorldReady)throw new InvalidOperationException("Starter identity requires the authoritative loaded world.");
            var value=TravelManager.Instance?.startingApartment;
            if(value==null)throw new InvalidOperationException("Native starting apartment unavailable.");
            return Canonical(value).Guid;
        }
        public static void InitializeWorkerLedger(AccountWorldState ledger)
        {
            if(!worker||!configured||ledger==null)throw new InvalidOperationException("Worker ownership ledger unavailable.");
            string expected=DeriveStarterPropertyId();
            if(string.IsNullOrEmpty(ledger.StarterPropertyId))
            {
                if(ledger.Owners==null||ledger.Owners.Length!=0)throw new InvalidDataException("Uninitialized property ledger already contains owners.");
                var legacy=new List<PropertyOwner>();var seen=new HashSet<string>(StringComparer.Ordinal);
                foreach(var entry in PropertyManager.Instance._propertyIdLookup)
                {
                    var property=entry.Value;if(property==null||property.Guid==expected||!property.PlayerOwned)continue;
                    if(property.TryCast<ApartmentAsset>()==null&&property.TryCast<Venue>()==null)continue;
                    Canonical(property);if(!seen.Add(property.Guid))continue;
                    legacy.Add(new PropertyOwner {Kind=property.TryCast<ApartmentAsset>()!=null?"apartment":"business",PropertyId=property.Guid,OwnerAccountId=AccountProtocol.LegacyOwnerId});
                }
                ledger.StarterPropertyId=expected;ledger.Owners=legacy.ToArray();
            }
            else if(ledger.StarterPropertyId!=expected)throw new InvalidDataException("Saved starter identity does not match native world metadata.");
            ApplyOwners(ledger.StarterPropertyId,ledger.Owners);
        }
        public static void ApplyOwners(string sharedStarter,PropertyOwner[] entries)
        {
            if(!Guid.TryParse(sharedStarter,out _)||entries==null||entries.Length>8192)throw new InvalidDataException("Invalid property registry.");
            var next=new Dictionary<string,string>(StringComparer.Ordinal);
            foreach(var entry in entries)
                if(entry==null||!Guid.TryParse(entry.PropertyId,out _)||entry.PropertyId==sharedStarter||
                    (entry.Kind!="apartment"&&entry.Kind!="business")||(!PropertyOwnershipPolicy.AccountId(entry.OwnerAccountId)&&entry.OwnerAccountId!=AccountProtocol.LegacyOwnerId)||
                    !next.TryAdd(entry.PropertyId,entry.OwnerAccountId))throw new InvalidDataException("Invalid or duplicated property owner.");
            if(starter.Length!=0&&starter!=sharedStarter)throw new InvalidDataException("Shared starter property changed.");
            starter=sharedStarter; Owners.Clear();foreach(var pair in next)Owners.Add(pair.Key,pair.Value);
        }
        public static string OwnerOf(string id)=>id!=null&&Owners.TryGetValue(id,out var owner)?owner:"";
        // Native ownership changes before its durable/public ledger ACK. Attribute
        // synchronous rent callbacks and subsequent simulation to that reserved
        // account without granting clients any uncommitted management rights.
        public static string NativeOwnerOf(string id)
        {
            if (!worker || !configured) throw new InvalidOperationException("Native owner resolution is worker-only.");
            if (id != null && Reservations.TryGetValue(id, out var pending))
                return pending.NativeStarted ? pending.AccountId : OwnerOf(id);
            string owner = OwnerOf(id);
            if (owner.Length != 0 || string.IsNullOrEmpty(id) || id == starter) return owner;
            // Native story gifts can arrive after initial migration. Until an
            // explicit claim commits, their finances belong to escrow, never an
            // inferred nearby/current account. This does not grant client rights.
            return Resolve(id).PlayerOwned ? AccountProtocol.LegacyOwnerId : "";
        }
        public static bool ReconcileNativeGifts(AccountWorldState pending)
        {
            if (!worker || !configured || pending == null || starter.Length == 0 ||
                pending.StarterPropertyId != starter || pending.Owners == null || pending.Owners.Length > 8192)
                throw new InvalidOperationException("Native gift reconciliation requires the worker ownership ledger.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var owner in pending.Owners)
                if (owner == null || !Guid.TryParse(owner.PropertyId, out _) || !ids.Add(owner.PropertyId))
                    throw new InvalidDataException("Invalid or duplicated pending property owner.");
            var properties = PropertyManager.Instance?._propertyIdLookup;
            if (properties == null) throw new InvalidOperationException("Native property registry unavailable.");
            int count = properties.Count;
            var gifts = new List<PropertyOwner>();
            // Values are native references. The native enumerator rejects a
            // changed dictionary; stage all additions before changing the ledger.
            foreach (var property in properties.Values)
            {
                if (property == null || property.Guid == starter || !property.PlayerOwned ||
                    ids.Contains(property.Guid) || Owners.ContainsKey(property.Guid) || Reservations.ContainsKey(property.Guid)) continue;
                var apartment = property.TryCast<ApartmentAsset>();
                if (apartment == null && property.TryCast<Venue>() == null) continue;
                Canonical(property);
                if (!ids.Add(property.Guid)) continue;
                if (ids.Count > 8192) throw new InvalidDataException("Property ownership limit exceeded.");
                gifts.Add(new PropertyOwner { Kind = apartment != null ? "apartment" : "business",
                    PropertyId = property.Guid, OwnerAccountId = AccountProtocol.LegacyOwnerId });
            }
            if (PropertyManager.Instance?._propertyIdLookup?.Pointer != properties.Pointer || properties.Count != count)
                throw new InvalidOperationException("Native property registry changed during gift reconciliation.");
            if (gifts.Count == 0) return false;
            gifts.Sort((a, b) => StringComparer.Ordinal.Compare(a.PropertyId, b.PropertyId));
            var next = new List<PropertyOwner>(pending.Owners); next.AddRange(gifts);
            pending.Owners = next.ToArray();
            // Owners remains the committed/public registry. Session promotes the
            // pending ledger only after its matching native checkpoint is ACKed.
            return true;
        }
        public static BaseProperty Resolve(string id)
        {
            if(!Guid.TryParse(id,out _)||!PropertyManager.InstanceNotNull())throw new InvalidOperationException("Unknown native property.");
            var found=PropertyManager.Instance.TryProvide(id)?.TryCast<BaseProperty>();
            if(found==null||found.Guid!=id)throw new InvalidOperationException("Unknown native property.");
            return Canonical(found);
        }
        private static BaseProperty Canonical(BaseProperty value)
        {
            if(value==null||!Guid.TryParse(value.Guid,out _)||!PropertyManager.InstanceNotNull())throw new InvalidOperationException("Native property metadata unavailable.");
            var known=PropertyManager.Instance.TryProvide(value.Guid)?.TryCast<BaseProperty>();
            if(known==null||known.Pointer!=value.Pointer||(value.TryCast<ApartmentAsset>()==null&&value.TryCast<Venue>()==null))
                throw new InvalidOperationException("Only canonical apartments and businesses support account ownership.");
            return value;
        }
        private static void RequireRegistry()
        { if(!configured||!DedicatedWorldRuntime.IsIsolated||starter.Length==0)throw new InvalidOperationException("Property ownership is synchronizing."); }
        public static void RequireOwner(string accountId,BaseProperty property)
        {
            RequireRegistry();property=Canonical(property);
            if(!PropertyOwnershipPolicy.CanManage(accountId,property.Guid,starter,OwnerOf(property.Guid)))
                throw new InvalidOperationException(property.Guid==starter?"Only the bed may be used in the shared starter apartment. Other furniture interactions are disabled.":"Only this property's account owner may use or manage it.");
            if(Reservations.ContainsKey(property.Guid))throw new InvalidOperationException("This property is waiting for a transaction checkpoint.");
        }
        public static void RequireBusinessOwner(string accountId,VenueAreaGhost runtime)
        {
            if(runtime==null||runtime.Venue==null||runtime.Venue.RuntimeData?.Pointer!=runtime.Pointer)throw new InvalidOperationException("Business target is not canonical.");
            RequireOwner(accountId,runtime.Venue);
        }
        // Owners and canonical public shelters permit entry. The party barrier may carry
        // guests; their furniture/management permissions remain denied. Exit is
        // never denied by ownership and shared starter entry remains available.
        public static void RequireApartmentInitiator(string accountId,string propertyId,bool enter)
        {
            RequireRegistry();
            if(!enter||propertyId==starter)return;
            var property=Resolve(propertyId);
            if(IsPublicShelter(accountId,property))return;
            RequireOwner(accountId,property);
        }
        private static bool IsSharedStarterBed(string accountId,BaseProperty property)
        {
            property=Canonical(property);
            return property.TryCast<ApartmentAsset>()!=null&&PropertyOwnershipPolicy.CanUseSharedStarterBed(accountId,
                property.Guid,starter,OwnerOf(property.Guid),Reservations.ContainsKey(property.Guid));
        }
        private static bool IsPublicShelter(string accountId,BaseProperty property)
        {
            // Resolve against native PropertyManager identity. RPC data never
            // supplies either the Shelter or PlayerOwned classification.
            property=Canonical(property);
            var apartment=property.TryCast<ApartmentAsset>();
            return apartment!=null&&PropertyOwnershipPolicy.CanUsePublicShelter(accountId,property.Guid,starter,
                OwnerOf(property.Guid),apartment.Shelter,property.PlayerOwned,Reservations.ContainsKey(property.Guid));
        }
        public static FurnitureObject ResolveFurniture(Component target)
        {
            if(target==null)return null;
            var direct=target.TryCast<FurnitureObject>();if(direct!=null)return direct;
            var held=target.TryCast<HoldableEntity>();if(held?.Furniture!=null)return held.Furniture;
            var seat=target.TryCast<SeatInteraction>();if(seat?.venueObject!=null)return seat.venueObject;
            var storage=target.TryCast<StorageInteractable>();if(storage?._furnitureObject!=null)return storage._furnitureObject;
            var air=target.TryCast<FurnitureAirConditioner>();if(air?.furniture!=null)return air.furniture;
            var fan=target.TryCast<FurnitureAirFan>();if(fan?.furniture!=null)return fan.furniture;
            var animation=target.TryCast<FurnitureAnimationToggle>();if(animation?.furniture!=null)return animation.furniture;
            var fitness=target.TryCast<FurnitureFitnessStation>();if(fitness?.furniture!=null)return fitness.furniture;
            var light=target.TryCast<FurnitureLight>();if(light?.furniture!=null)return light.furniture;
            var cabinet=target.TryCast<FurnitureOpenClose>();if(cabinet?.furniture!=null)return cabinet.furniture;
            var sound=target.TryCast<FurniturePlaySound>();if(sound?.furniture!=null)return sound.furniture;
            var shower=target.TryCast<FurnitureShower>();if(shower?.furniture!=null)return shower.furniture;
            var tv=target.TryCast<FurnitureTV>();if(tv?.furniture!=null)return tv.furniture;
            var washer=target.TryCast<FurnitureWashingMachine>();if(washer?.furniture!=null)return washer.furniture;
            var tap=target.TryCast<FurnitureWaterTap>();if(tap?.furniture!=null)return tap.furniture;
            var greenhouse=target.TryCast<GreenhouseConsoleView>();if(greenhouse?.furniture!=null)return greenhouse.furniture;
            var module=target.TryCast<GreenhouseModuleView>();if(module?.furniture!=null)return module.furniture;
            var boatBed=target.TryCast<BoatBed>();if(boatBed?.Furniture!=null)return boatBed.Furniture;
            var parent=target.GetComponentInParent<global::FurnitureParent>();if(parent?.furnitureObject!=null)return parent.furnitureObject;
            return target.GetComponentInParent<FurnitureObject>();
        }
        private static bool IsBoundaryDoor(Component target)
        {
            if(target.TryCast<ApartmentDoor>()==null&&target.TryCast<DoorInteraction>()==null&&target.TryCast<HingeInteraction>()==null)return false;
            foreach(var controller in Object.FindObjectsOfType<ApartmentController>(true))
            {
                if(controller==null||controller.apartment==null||ApartmentManager.Instance.GetState(controller.apartment)?.controller?.Pointer!=controller.Pointer)continue;
                if(controller.entrance?.Pointer==target.Pointer||controller.exit?.Pointer==target.Pointer)return true;
                if(controller.entranceDoorInteractions!=null)foreach(var door in controller.entranceDoorInteractions)if(door?.Pointer==target.Pointer)return true;
                if(controller.entranceHingeInteractions!=null)foreach(var door in controller.entranceHingeInteractions)if(door?.Pointer==target.Pointer)return true;
            }
            return false;
        }
        // Worker RPC handlers must call AFTER resolving the target themselves.
        // Client-supplied property identifiers do not decide target permissions.
        public static void RequireTargetInteraction(string accountId,Component target)
        {
            RequireRegistry();
            if(target==null||!target.gameObject.scene.isLoaded)throw new InvalidOperationException("Interaction target is not loaded.");
            if(IsBoundaryDoor(target))return;
            var furniture=ResolveFurniture(target);if(furniture==null)return;
            if(furniture.CurrentProperty==null)throw new InvalidOperationException("Furniture property metadata unavailable.");
            // Shared sleeping is a Bed-use exception, never furniture ownership.
            // Pickup and placement still require an exclusive property owner.
            if(target.TryCast<Bed>()!=null&&(IsSharedStarterBed(accountId,furniture.CurrentProperty)||
                IsPublicShelter(accountId,furniture.CurrentProperty)))return;
            RequireOwner(accountId,furniture.CurrentProperty);
        }
        public static void RequirePlacement(string accountId,HoldableEntity item,BaseProperty destination)
        {
            RequireRegistry();if(item?.Furniture?.CurrentProperty!=null)RequireOwner(accountId,item.Furniture.CurrentProperty);
            if(destination==null)throw new InvalidOperationException("Place furniture only in your own property.");RequireOwner(accountId,destination);
        }
        public static bool Handles(InteractionRequest request)=>request!=null&&(request.Kind=="property-claim"||request.Kind=="property-rent"||request.Kind=="property-release");
        public static void HandleInteraction(InteractionRequest request)
        {
            if(!worker||!configured||!Handles(request))throw new InvalidOperationException("Property request requires worker authority.");
            string key=request.PlayerId+":"+request.RequestId;
            if(Results.TryGetValue(key,out var old)){OutboundWorldEvent?.Invoke(old);return;}
            foreach(var pending in Reservations.Values)
                if(pending.PlayerId==request.PlayerId&&pending.RequestId==request.RequestId)
                { if(pending.AccountId!=request.AccountId||pending.PropertyId!=request.TargetId||pending.Operation!=request.Kind)throw new InvalidDataException("Pending property request changed.");return; }
            PreparedPropertyChange change=null;
            try
            {
                if(BeginTransaction==null)throw new InvalidOperationException("Account transactions are not ready.");
                change=Prepare(request.AccountId,request);BeginTransaction(change);
            }
            catch(Exception ex)
            {
                if(change!=null&&change.NativeStarted)throw; // No fake rollback after native mutations.
                if(change!=null)Reservations.Remove(change.PropertyId);
                PublishResult(request.PlayerId,request.RequestId,false,ex.Message);
            }
        }
        private static PreparedPropertyChange Prepare(string accountId,InteractionRequest request)
        {
            RequireRegistry();
            if(!DedicatedWorldRuntime.IsWorldReady||!PropertyOwnershipPolicy.AccountId(accountId)||!Guid.TryParseExact(request.RequestId,"N",out _))throw new InvalidOperationException("Invalid property transaction.");
            var property=Resolve(request.TargetId);string owner=OwnerOf(property.Guid);
            string problem=PropertyOwnershipPolicy.Reject(accountId,property.Guid,starter,owner,request.Kind,property.PlayerOwned,Reservations.ContainsKey(property.Guid));
            if(problem!=null)throw new InvalidOperationException(problem);
            RequireNearOffer(request.PlayerId,accountId,property);
            if(request.Kind=="property-rent")RequireRentable(property);
            if(request.Kind=="property-release"&&(!property.AllowUnrenting||property.OwnershipType!=OwnershipType.Rent))throw new InvalidOperationException("This property is not a returnable rental.");
            var change=new PreparedPropertyChange {AccountId=accountId,PlayerId=request.PlayerId,PropertyId=property.Guid,Kind=property.TryCast<ApartmentAsset>()!=null?"apartment":"business",
                Operation=request.Kind,RequestId=request.RequestId,Property=property,PriorOwner=owner};
            Reservations.Add(property.Guid,change);return change;
        }
        private static void RequireNearOffer(string playerId,string accountId,BaseProperty property)
        {
            if(!Players.TryGetValue(playerId,out var actor)||actor.Pose.AccountId!=accountId||!actor.Pose.Active||Time.realtimeSinceStartup-actor.Time>3)throw new InvalidOperationException("Your authoritative account position is unavailable.");
            var point=new Vector3(actor.Pose.X,actor.Pose.Y,actor.Pose.Z);bool found=false;
            foreach(var offer in Object.FindObjectsOfType<ApartmentOfferInteraction>(true))
                if(offer!=null&&offer.apartment?.Pointer==property.Pointer&&Near(offer,point,actor.Pose.Scene))found=true;
            foreach(var sign in Object.FindObjectsOfType<VenueSignInteraction>(true))
                if(sign!=null&&sign.venue?.Pointer==property.Pointer&&Near(sign,point,actor.Pose.Scene))found=true;
            // Existing unassigned apartments may hide their rental offer; their
            // exact canonical entrance provides the explicit claim interaction.
            foreach(var apartment in Object.FindObjectsOfType<ApartmentController>(true))
                if(apartment!=null&&apartment.apartment?.Pointer==property.Pointer&&apartment.entrance!=null&&Near(apartment.entrance,point,actor.Pose.Scene))found=true;
            if(!found)throw new InvalidOperationException("Stand within four metres of this property's native offer, sign or entrance.");
        }
        private static bool Near(Component target,Vector3 point,string scene)=>target.gameObject.scene.isLoaded&&target.gameObject.scene.name==scene&&(target.transform.position-point).sqrMagnitude<=16;
        private static void RequireRentable(BaseProperty property)
        {
            var apartment=property.TryCast<ApartmentAsset>();
            if(!property.IsAcquireable||property.PlayerOwned||property.OwnerInventory!=null||(apartment!=null&&(apartment.Shelter||apartment.dev_apartment||apartment.IsStageApartment)))
                throw new InvalidOperationException("This property is not available to rent.");
        }
        private static void RequireReservation(PreparedPropertyChange change)
        {
            if(change==null||!Reservations.TryGetValue(change.PropertyId,out var current)||!ReferenceEquals(current,change)||Canonical(change.Property).Guid!=change.PropertyId||OwnerOf(change.PropertyId)!=change.PriorOwner)
                throw new InvalidOperationException("Property reservation changed.");
        }
        // Called in root's matching native account-inventory scope. Native rent
        // itself deducts FurnitureValue and consumes RequiredPermit; never charge
        // independently here. Any throw after NativeStarted requires fail-closed
        // worker handling without publishing a mismatched checkpoint.
        public static void ExecuteNative(PreparedPropertyChange change,string activeAccountId)
        {
            RequireReservation(change);
            if(!worker||change.AccountId!=activeAccountId||change.NativeStarted)throw new InvalidOperationException("Property transaction requires its account scope exactly once.");
            var property=change.Property;RequireNearOffer(change.PlayerId,change.AccountId,property);
            if(change.Operation=="property-rent")
            {
                RequireRentable(property);var inventory=PlayerManager.Instance?.LocalPlayer?.Inventory;int price=PropertyManager.Instance.GetPropertyState(property)?.FurnitureValue??-1;
                if(inventory?.Items==null||price<0||inventory.Money<price)throw new InvalidOperationException("Your account cannot afford the native furniture downpayment.");
                if(property.RequiredPermit!=null&&inventory.Items.GetItemCount(property.RequiredPermit)<1)throw new InvalidOperationException("Your account lacks the required property permit.");
                change.NativeStarted=true;PropertyManager.Instance.StartRenting(property);
                if(!property.PlayerOwned||!PlayerManager.Instance.LocalPlayer.DoesOwnProperty(property))throw new InvalidOperationException("Native rent did not establish ownership; stop before committing.");
            }
            else if(change.Operation=="property-release")
            {
                if(!property.PlayerOwned||!property.AllowUnrenting||property.OwnershipType!=OwnershipType.Rent)throw new InvalidOperationException("This rental is no longer returnable.");
                if(PlayerManager.Instance.LocalPlayer.CurrentProperty?.Pointer==property.Pointer)throw new InvalidOperationException("Leave the property together before returning it.");
                change.NativeStarted=true;PropertyManager.Instance.StopRenting(property,false);
                if(property.PlayerOwned)throw new InvalidOperationException("Native return did not release ownership; stop before committing.");
            }
            else {if(!property.PlayerOwned)throw new InvalidOperationException("Only an existing unassigned property can be claimed.");change.NativeStarted=true;}
            change.NativeCompleted=true;
        }
        public static void ApplyToPendingLedger(PreparedPropertyChange change,AccountWorldState pending)
        {
            RequireReservation(change);if(!change.NativeCompleted||pending==null)throw new InvalidOperationException("Native property change is incomplete.");
            var owners=new List<PropertyOwner>();foreach(var value in pending.Owners)if(value.PropertyId!=change.PropertyId)owners.Add(value);
            if(change.Operation!="property-release")owners.Add(new PropertyOwner {Kind=change.Kind,PropertyId=change.PropertyId,OwnerAccountId=change.AccountId});pending.Owners=owners.ToArray();
        }
        public static void Committed(PreparedPropertyChange change)
        {
            RequireReservation(change);if(!change.NativeCompleted)throw new InvalidOperationException("Cannot commit unfinished property transaction.");
            if(change.Operation=="property-release")Owners.Remove(change.PropertyId);else Owners[change.PropertyId]=change.AccountId;
            Reservations.Remove(change.PropertyId);PublishResult(change.PlayerId,change.RequestId,true,change.Operation=="property-release"?"Property returned and saved.":"Property ownership saved to your account.");
        }
        public static void CancelBeforeNative(PreparedPropertyChange change,string message)
        {
            RequireReservation(change);if(change.NativeStarted)throw new InvalidOperationException("Started native property transactions must commit or stop the worker.");
            Reservations.Remove(change.PropertyId);PublishResult(change.PlayerId,change.RequestId,false,message);
        }
        private static void PublishResult(string player,string request,bool success,string message)
        {
            message=message??"";if(message.Length>1024)message=message.Substring(0,1024);
            var value=new WorldEvent {Kind="property-result",Data=JsonSerializer.SerializeToElement(new GameplayState {PlayerId=player,RequestId=request,Success=success,Message=message},DedicatedProtocol.Json)};
            string key=player+":"+request;Results[key]=value;ResultOrder.Enqueue(key);while(ResultOrder.Count>4096)Results.Remove(ResultOrder.Dequeue());OutboundWorldEvent?.Invoke(value);
        }
        public static bool? GuardClientInteraction(IInteractable focus)
        {
            if(!configured||worker||!DedicatedWorldRuntime.IsIsolated||focus==null)return null;
            try
            {
                var component=focus.TryCast<Component>();if(component!=null&&ResolveFurniture(component)!=null)RequireTargetInteraction(localAccount,component);
                var offer=focus.TryCast<ApartmentOfferInteraction>();if(offer?.apartment!=null){RequireRegistry();return true;}
                var sign=focus.TryCast<VenueSignInteraction>();if(sign?.venue!=null)
                { RequireRegistry();if(sign.venue.PlayerOwned){if(OfferClaim(sign.venue))return false;RequireOwner(localAccount,sign.venue);}return true; }
                var door=focus.TryCast<ApartmentDoor>();if(door!=null)foreach(var controller in Object.FindObjectsOfType<ApartmentController>(true))
                    if(controller!=null&&controller.apartment!=null&&controller.entrance?.Pointer==door.Pointer){if(OfferClaim(controller.apartment))return false;RequireApartmentInitiator(localAccount,controller.apartment.Guid,true);}
                return null;
            }
            catch(Exception ex){ShowNotice(ex.Message);return false;}
        }
        private static bool OfferClaim(BaseProperty property)
        {
            RequireRegistry();property=Canonical(property);string owner=OwnerOf(property.Guid);
            if(property.Guid==starter||!property.PlayerOwned||(owner.Length!=0&&owner!=AccountProtocol.LegacyOwnerId))return false;
            if(PopupDialog.IsPopupVisible)throw new InvalidOperationException("Close the current dialog before claiming this property.");
            string id=property.Guid;Callbacks.Clear();Il2CppSystem.Action yes=(Il2CppSystem.Action)(Action)(()=>Send("property-claim",id));Il2CppSystem.Action no=(Il2CppSystem.Action)(Action)(()=>{});
            Callbacks.Add(yes);Callbacks.Add(no);PopupDialog.Display("Claim property?","Claim this existing unassigned property for your persistent account? Only its owner may manage it or use its furniture.",
                new Il2CppStringArray(new[]{"Claim","Cancel"}),new Il2CppReferenceArray<Il2CppSystem.Action>(new[]{yes,no}),true);return true;
        }
        private static void Send(string kind,string id)
        {
            try{RequireRegistry();if(OutboundInteraction==null)throw new InvalidOperationException("Account connection not ready.");OutboundInteraction(new InteractionRequest {Kind=kind,RequestId=Guid.NewGuid().ToString("N"),TargetId=id,Data=JsonSerializer.SerializeToElement(new{},DedicatedProtocol.Json)});}
            catch(Exception ex){ShowNotice(ex.Message);}
        }
        private static void ShowNotice(string message)
        {if(Time.realtimeSinceStartup<nextNotice)return;nextNotice=Time.realtimeSinceStartup+1;Notice?.Invoke(message);}
        private static void InstallPatches()
        {
            if(patched)return;var harmony=new Harmony("NivalisTogether.AccountPropertyAccess");
            foreach(var type in typeof(PlayerManager).Assembly.GetTypes())
            {
                if(type.IsAbstract||!typeof(MonoBehaviour).IsAssignableFrom(type))continue;
                foreach(var method in type.GetMethods(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly))
                    if(!method.IsAbstract&&method.Name=="DoInteraction"&&method.ReturnType==typeof(void)&&method.GetParameters().Length==0)
                        harmony.Patch(method,prefix:new HarmonyMethod(typeof(PropertyOwnershipReplication),nameof(DirectPrefix)){priority=Priority.First});
            }
            Patch(harmony,typeof(HoldableEntity),"PickUp",nameof(PickupPrefix),typeof(bool));
            Patch(harmony,typeof(PropertyConsoleUI),"StartRenting",nameof(RentPrefix),typeof(void));
            Patch(harmony,typeof(PropertyConsoleUI),"StopRenting",nameof(ReleasePrefix),typeof(void));patched=true;
        }
        private static void Patch(Harmony harmony,Type type,string name,string prefix,Type result)
        {
            var method=type.GetMethod(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly,null,Type.EmptyTypes,null);
            if(method==null||method.ReturnType!=result)throw new MissingMethodException(type.FullName,name);
            harmony.Patch(method,prefix:new HarmonyMethod(typeof(PropertyOwnershipReplication),prefix){priority=Priority.First});
        }
        private static bool DirectPrefix(Component __instance)
        {
            if(!configured||worker||!DedicatedWorldRuntime.IsWorldReady)return true;
            try{if(ResolveFurniture(__instance)!=null)RequireTargetInteraction(localAccount,__instance);return true;}catch(Exception ex){ShowNotice(ex.Message);return false;}
        }
        private static bool PickupPrefix(HoldableEntity __instance,ref bool __result)
        {
            if(!configured||worker||!DedicatedWorldRuntime.IsWorldReady)return true;
            // Shared starter/shelter permission is for sleeping, never taking its bed.
            try{if(__instance?.Furniture!=null)RequireOwner(localAccount,__instance.Furniture.CurrentProperty);return true;}catch(Exception ex){__result=false;ShowNotice(ex.Message);return false;}
        }
        private static bool RentPrefix(PropertyConsoleUI __instance)
        {if(!configured||worker)return true;Send("property-rent",__instance?._property?.Guid??"");return false;}
        private static bool ReleasePrefix(PropertyConsoleUI __instance)
        {if(!configured||worker)return true;Send("property-release",__instance?._property?.Guid??"");return false;}
    }
    internal static class PropertyOwnershipPolicy
    {
        internal static bool AccountId(string value)=>Guid.TryParseExact(value,"N",out _);
        internal static bool CanManage(string account,string property,string starter,string owner)=>AccountId(account)&&property!=starter&&account==owner;
        internal static bool CanUseSharedStarterBed(string account,string property,string starter,string owner,bool reserved)=>
            AccountId(account)&&Guid.TryParse(starter,out _)&&property==starter&&string.IsNullOrEmpty(owner)&&!reserved;
        internal static bool CanUsePublicShelter(string account,string property,string starter,string owner,bool shelter,bool nativeOwned,bool reserved)=>
            AccountId(account)&&Guid.TryParse(property,out _)&&Guid.TryParse(starter,out _)&&property!=starter&&
            shelter&&!nativeOwned&&string.IsNullOrEmpty(owner)&&!reserved;
        internal static string Reject(string account,string property,string starter,string owner,string operation,bool nativeOwned,bool reserved)
        {
            if(!AccountId(account)||!Guid.TryParse(property,out _)||!Guid.TryParse(starter,out _))return "Invalid account or property identity.";
            if(property==starter)return "The starter apartment is shared and cannot be claimed, rented, returned or modified.";
            if(reserved)return "Another property transaction is waiting for its checkpoint.";
            bool free=string.IsNullOrEmpty(owner)||owner==AccountProtocol.LegacyOwnerId;
            if(operation=="property-claim")return !free?"Another account already owns this property.":!nativeOwned?"Rent this property through its native offer first.":null;
            if(operation=="property-rent")return !free||nativeOwned?"This property is already owned or awaits an explicit claim.":null;
            if(operation=="property-release")return owner!=account||!nativeOwned?"Only the owning account may return this property.":null;
            return "Unknown property transaction.";
        }
    }
}
