using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Stopwatch = System.Diagnostics.Stopwatch;
using System.Text.Json;
using System.Threading;
using HarmonyLib;
using Nivalis;
using Nivalis.CraftingSystem;
using Nivalis.GhostSystem;
using Nivalis.GhostSystem.Ai;
using Nivalis.GhostSystem.CustomerLoop;
using Nivalis.InventorySystem;
using UnityEngine;

namespace NivalisTogether.Dedicated
{
    // Retain canonical identities, never native wrappers. Meals present in an
    // earlier authoritative roster can become obsolete in a later one, so taking
    // a snapshot must not discard candidates as a one-shot cleanup queue would.
    internal sealed class CraftMealGhostIndex
    {
        private IntPtr manager,registry;
        private string scene;
        private bool initialized;
        private readonly Dictionary<string,IntPtr> candidates=new(StringComparer.Ordinal);
        internal int Count=>candidates.Count;
        internal bool Begin(IntPtr managerIdentity,IntPtr registryIdentity,string sceneIdentity)
        {
            if(managerIdentity==IntPtr.Zero||registryIdentity==IntPtr.Zero){Clear();return false;}
            if(initialized&&manager==managerIdentity&&registry==registryIdentity&&scene==sceneIdentity)return false;
            Clear();manager=managerIdentity;registry=registryIdentity;scene=sceneIdentity;initialized=true;return true;
        }
        internal void Observe(IntPtr managerIdentity,string id,IntPtr pointer,bool isMeal)
        {
            if(!initialized||managerIdentity!=manager||string.IsNullOrEmpty(id))return;
            if(isMeal&&pointer!=IntPtr.Zero)candidates[id]=pointer;else candidates.Remove(id);
        }
        internal void Forget(IntPtr managerIdentity,string id,IntPtr pointer)
        {
            if(managerIdentity==manager&&id!=null&&candidates.TryGetValue(id,out var prior)&&prior==pointer)candidates.Remove(id);
        }
        internal KeyValuePair<string,IntPtr>[] Snapshot()=>new List<KeyValuePair<string,IntPtr>>(candidates).ToArray();
        internal void Invalidate(IntPtr managerIdentity){if(managerIdentity==manager)Clear();}
        internal void Clear(){candidates.Clear();manager=registry=IntPtr.Zero;scene=null;initialized=false;}
    }

    // A grant always precedes an entire entity unit. The callback reports the
    // end of a phase; no row or dependent phase advances after a failed unit.
    internal sealed class CraftApplyCursor
    {
        private int phase,index;
        private bool aborted;
        internal bool Advance(Func<bool> grant,Func<int,int,bool> unit)
        {
            if(aborted)throw new InvalidOperationException("Craft application was aborted.");
            while(phase<8){
                if(!grant())return false;
                if(unit(phase,index)){phase++;index=0;}else index++;
            }
            return true;
        }
        internal void Abort(){aborted=true;}
    }

    public sealed class BusinessMealState
    {
        public string Id { get; set; }="";
        public string PrefabId { get; set; }="";
        public string ItemId { get; set; }="";
        public int Scene { get; set; }
        public bool Active { get; set; }
        public bool Viewless { get; set; }
        public bool Baked { get; set; }
        public bool CanBeHeld { get; set; }
        public bool Held { get; set; }
        public bool Failed { get; set; }
        public int Fill { get; set; }
        public int PlayerAction { get; set; }
        public float Quality { get; set; }
        public float Freshness { get; set; }
        public float[] Pose { get; set; }=Array.Empty<float>();
        public float[] Bounds { get; set; }=Array.Empty<float>();
        public string ParentId { get; set; }="";
        public string ReservedById { get; set; }="";
        public string ProcessorId { get; set; }="";
        public string ChairId { get; set; }="";
        public string VenueId { get; set; }="";
        public string OrderId { get; set; }="";
        public int SpotId { get; set; }
        public int SpotType { get; set; }
        public float[] SpotOffset { get; set; }=Array.Empty<float>();
    }
    public sealed class BusinessProcessorState
    {
        public string Id { get; set; }="";
        public string PrefabId { get; set; }="";
        public string ViewId { get; set; }="";
        public int Scene { get; set; }
        public bool Active { get; set; }
        public bool Viewless { get; set; }
        public bool Baked { get; set; }
        public bool CanBeHeld { get; set; }
        public bool Held { get; set; }
        public float[] Pose { get; set; }=Array.Empty<float>();
        public float[] Bounds { get; set; }=Array.Empty<float>();
        public string ParentId { get; set; }="";
        public string VenueId { get; set; }="";
        public string TypeId { get; set; }="";
        public float ProcessingTime { get; set; }
        public BusinessProcessorSpot InteractionSpot { get; set; }=new BusinessProcessorSpot();
        public BusinessProcessorSpot ServeSpot { get; set; }=new BusinessProcessorSpot();
        public float[] SpawnMealOffset { get; set; }=new float[3];
        public float[] SpawnTrayOffset { get; set; }=new float[3];
        public int[] Recipes { get; set; }=Array.Empty<int>();
        public bool Ingredient { get; set; }
        public bool Busy { get; set; }
        public bool AvailabilityKnown { get; set; }
        public bool CanPrepare { get; set; }
        public string ReservedById { get; set; }="";
        public string[] Meals { get; set; }=Array.Empty<string>();
    }
    public sealed class BusinessProcessorSpot
    {
        public int Id { get; set; }
        public int Type { get; set; }
        public float[] Offset { get; set; }=new float[3];
    }
    public sealed class BusinessOrderPrice
    {
        public string Key { get; set; }="";
        public int Price { get; set; }
    }
    public sealed class BusinessDeliveredMeal
    {
        public string ItemId { get; set; }="";
        public string MealId { get; set; }="";
        public int Price { get; set; }
        public float Quality { get; set; }
        public float Freshness { get; set; }
    }
    public sealed class BusinessOrderState
    {
        public string Id { get; set; }="";
        public bool Valid { get; set; }
        public string CustomerId { get; set; }="";
        public string TableId { get; set; }="";
        public string ChairId { get; set; }="";
        public string[] Ordered { get; set; }=Array.Empty<string>();
        public string[] Prepared { get; set; }=Array.Empty<string>();
        public BusinessOrderPrice[] Prices { get; set; }=Array.Empty<BusinessOrderPrice>();
        public BusinessDeliveredMeal[] Delivered { get; set; }=Array.Empty<BusinessDeliveredMeal>();
        public int Consumed { get; set; }
        public float ServiceQuality { get; set; }
    }
    public sealed class BusinessVenueOrders
    {
        public string GhostId { get; set; }="";
        public string VenueId { get; set; }="";
        public BusinessOrderState[] Orders { get; set; }=Array.Empty<BusinessOrderState>();
    }
    public sealed class BusinessCraftState
    {
        public long Sequence { get; set; }
        public BusinessMealState[] Meals { get; set; }=Array.Empty<BusinessMealState>();
        public BusinessProcessorState[] Processors { get; set; }=Array.Empty<BusinessProcessorState>();
        public BusinessVenueOrders[] Venues { get; set; }=Array.Empty<BusinessVenueOrders>();
    }
    public sealed class BusinessCraftChunk
    {
        public long Sequence { get; set; }
        public string Hash { get; set; }="";
        public string Encoding { get; set; }=BoundedSnapshotCodec.JsonEncoding;
        public int RawLength { get; set; }
        public int Length { get; set; }
        public int Offset { get; set; }
        public byte[] Bytes { get; set; }=Array.Empty<byte>();
    }

    /// <summary>Unity main-thread mirror of meals, processor occupancy and orders; never runs a native craft/serve/eat action.</summary>
    public static class BusinessCraftStateReplication
    {
        private const int MaxMeals=30000,MaxProcessors=20000,MaxVenues=2048,MaxBytes=16*1024*1024,ChunkBytes=24*1024;
        private static Harmony Patches;
        private static IntPtr baseMealViewStart;
        private static Dictionary<string,BusinessProcessorState> Availability=new Dictionary<string,BusinessProcessorState>(StringComparer.Ordinal);
        // Only this mirror's runtime creations may be removed by a later roster.
        // Imported and baked objects remain owned by the native world lifecycle.
        private static readonly Dictionary<string,HoldableGhost> CreatedProcessors=new Dictionary<string,HoldableGhost>(StringComparer.Ordinal);
        private static readonly CraftMealGhostIndex MealGhosts=new CraftMealGhostIndex();
        private sealed class MealDeregisterState
        {internal IntPtr Manager,Pointer;internal string Id;}
        private static bool patched,clientAuthority;
        private static long sequence,applied,incomingSequence;
        private static byte[] incoming;
        private static int incomingOffset,incomingRawLength;
        private static string incomingHash,incomingEncoding;
        private static float incomingStarted;
        private static int preparationGeneration;
        private static long nextProfileReport;
        internal sealed class CraftProfile
        {
            internal long Started=Stopwatch.GetTimestamp(),Validation,Meals,Processors,Orders,Roster,Commit,Presentation;
            internal long Decode,Preparation;
            internal long IncrementalTicks;
            internal int MealCount,ProcessorCount,VenueCount,ChangedMeals,ChangedProcessors,ChangedVenues;
            internal bool Failed,BackgroundPrepared,Incremental;
        }
        public static Action<string> Diagnostic { get; set; }
        public static bool IsApplying { get; private set; }
        public static bool Faulted { get; private set; }
        /// <summary>SHA256 of the last complete encoded transport payload, not a semantic crafting-state digest.</summary>
        public static string LastCaptureHash { get; private set; }="";

        public static void Reset()
        {
            preparationGeneration++;
            sequence=applied=incomingSequence=0;incoming=null;incomingOffset=incomingRawLength=0;incomingHash=incomingEncoding=null;
            IsApplying=false;Faulted=false;LastCaptureHash="";Availability.Clear();CreatedProcessors.Clear();
            nextProfileReport=0;
            MealGhosts.Clear();
        }
        public static void SetClientAuthority(bool enabled)
        {
            if(enabled&&!patched){
                Patches=new Harmony("local.nivalis.together.craft-visibility");
                try{
                Patch("Start",nameof(BeforeMealStart));
                foreach(string name in new[]{"LateUpdate","OnEatFinish","DoInteraction","CancelInteraction","DeleteTask","CancelPlayerCleanup","CancelPlayerServing"})
                    Patch(name,nameof(AllowMealAction));
                Patch("CanInteract",nameof(BeforeMealCanInteract));
                Patch(typeof(FoodProcessor),"CanInteract",nameof(BeforeFoodCanInteract));
                Patch(typeof(FoodProcessor),"LateUpdate",nameof(BeforeFoodLateUpdate));
                Patch(typeof(IngredientProcessorView),"CanInteract",nameof(BeforeIngredientCanInteract));
                Patch(typeof(IngredientProcessorView),"LateUpdate",nameof(BeforeIngredientLateUpdate));
                PatchLifecycle("RegisterGhostImmediate",null,nameof(MealRegisteredPostfix));
                PatchLifecycle("DeregisterGhostImmediate",nameof(MealDeregisteringPrefix),nameof(MealDeregisteredPostfix));
                PatchLifecycle("InitializeInternal",nameof(MealManagerInvalidatingPrefix),null);
                PatchLifecycle("OnDestroy",nameof(MealManagerInvalidatingPrefix),null);
                patched=true;
                }catch{
                    try{Patches.UnpatchSelf();}finally{MealGhosts.Clear();patched=false;}
                    throw;
                }
            }
            if(clientAuthority!=enabled){preparationGeneration++;incoming=null;incomingOffset=0;}
            clientAuthority=enabled;
            if(!enabled)MealGhosts.Clear();
        }
        private static void PatchLifecycle(string name,string prefix,string postfix)
        {
            var method=typeof(GhostManager).GetMethod(name,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.DeclaredOnly);
            if(method==null)throw new MissingMethodException(typeof(GhostManager).FullName,name);
            HarmonyMethod Hook(string hook)=>hook==null?null:new HarmonyMethod(typeof(BusinessCraftStateReplication).GetMethod(hook,BindingFlags.NonPublic|BindingFlags.Static));
            Patches.Patch(method,prefix:Hook(prefix),postfix:Hook(postfix));
        }
        private static void MealRegisteredPostfix(GhostManager __instance,Ghost __0)
        {
            if(!clientAuthority||__instance==null||__0==null)return;
            // Registration can decline a duplicate. Track the actual canonical
            // object, not merely the argument presented to the native method.
            var actual=__instance.GetGhostById(__0.Id);
            if(actual!=null)MealGhosts.Observe(__instance.Pointer,actual.Id,actual.Pointer,actual.TryCast<MealGhost>()!=null);
        }
        private static void MealDeregisteringPrefix(GhostManager __instance,Ghost __0,out MealDeregisterState __state)
        {
            __state=null;if(!clientAuthority||__instance==null||__0==null)return;
            string id=__0.Id;if(string.IsNullOrEmpty(id))return;
            __state=new MealDeregisterState{Manager=__instance.Pointer,Pointer=__0.Pointer,Id=id};
        }
        private static void MealDeregisteredPostfix(GhostManager __instance,MealDeregisterState __state)
        {
            if(!clientAuthority||__instance==null||__state==null||__instance.Pointer!=__state.Manager)return;
            // A failed/declined deregistration must remain a cleanup candidate.
            // Reentrant replacement registration is protected by exact pointer.
            // The argument may already be pooled: use only prefix-captured IDs.
            if(__instance.GetGhostById(__state.Id)?.Pointer!=__state.Pointer)MealGhosts.Forget(__state.Manager,__state.Id,__state.Pointer);
        }
        private static void MealManagerInvalidatingPrefix(GhostManager __instance)
        {if(clientAuthority&&__instance!=null)MealGhosts.Invalidate(__instance.Pointer);}
        private static void EnsureMealIndex(GhostManager manager)
        {
            string scene=GameSceneManager.InstanceNotNull()?GameSceneManager.Instance.CurrentGameplaySceneName??"":"";
            if(!MealGhosts.Begin(manager.Pointer,manager._ghostsDictionary.Pointer,scene))return;
            try{
                // Hooks may be installed after import or disabled in the menu.
                // One boundary bootstrap captures every preexisting native meal.
                foreach(var ghost in manager.Ghosts)
                    if(ghost!=null)MealGhosts.Observe(manager.Pointer,ghost.Id,ghost.Pointer,ghost.TryCast<MealGhost>()!=null);
            }
            catch{MealGhosts.Clear();throw;}
        }
        private static void Patch(string name,string prefix)
            =>Patch(typeof(MealView),name,prefix);
        private static void Patch(Type type,string name,string prefix)
        {
            var target=type.GetMethod(name,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.DeclaredOnly);
            if(target==null)throw new MissingMethodException(type.FullName,name);
            Patches.Patch(target,prefix:new HarmonyMethod(typeof(BusinessCraftStateReplication).GetMethod(prefix,BindingFlags.NonPublic|BindingFlags.Static)));
        }
        private static bool AllowMealAction()=>!clientAuthority;
        private static bool BeforeMealCanInteract(ref bool __result)
        {
            if(!clientAuthority)return true;__result=false;return false;
        }
        public static bool TryGetProcessorAvailability(string id,out bool ready)
        {
            ready=false;if(id==null||!Availability.TryGetValue(id,out var state)||!state.AvailabilityKnown)return false;
            ready=state.CanPrepare&&!state.Busy;return true;
        }
        private static bool PlayerCanPrepare()
        {
            var manager=PlayerManager._instance;return manager?.LocalPlayer!=null&&!manager.LocalPlayer.IsHoldingObject;
        }
        private static bool BeforeFoodCanInteract(FoodProcessor __instance,ref bool __result)
        {
            if(!clientAuthority)return true;
            var ghost=__instance.MyGhost;__result=ghost!=null&&ghost.IsValid&&__instance.IsActive&&__instance.ShouldBeVisible&&!ghost._isHeld&&
                TryGetProcessorAvailability(ghost.Id,out bool ready)&&ready&&PlayerCanPrepare();return false;
        }
        private static bool BeforeIngredientCanInteract(IngredientProcessorView __instance,ref bool __result)
        {
            if(!clientAuthority)return true;
            var ghost=__instance.MyGhost;__result=ghost!=null&&ghost.IsValid&&__instance.IsActive&&__instance.ShouldBeVisible&&!ghost._isHeld&&
                TryGetProcessorAvailability(ghost.Id,out bool ready)&&ready&&PlayerCanPrepare();return false;
        }
        private static bool BeforeFoodLateUpdate(FoodProcessor __instance)
        {
            if(!clientAuthority)return true;
            var ghost=__instance.MyGhost;bool ready=ghost!=null&&ghost.IsValid&&__instance.ShouldBeVisible&&!ghost._isHeld&&
                TryGetProcessorAvailability(ghost.Id,out bool available)&&available;
            __instance.hasProcessingTask=ready;__instance.SetCanvasGroupState(ready);return false;
        }
        private static bool BeforeIngredientLateUpdate(IngredientProcessorView __instance)
        {
            if(!clientAuthority)return true;
            var ghost=__instance.MyGhost;bool ready=ghost!=null&&ghost.IsValid&&__instance.ShouldBeVisible&&!ghost._isHeld&&
                TryGetProcessorAvailability(ghost.Id,out bool available)&&available;
            __instance.hasProcessingTask=ready;__instance.SetCanvasGroupState(ready);return false;
        }
        private static bool BeforeMealStart(MealView __instance)
        {
            if(!clientAuthority)return true;
            // Native derived Start may resume a saved player cleanup/serving task and
            // attach this remote meal to the local player's hands. The base performs
            // ordinary view registration; presentation below never resumes that task.
            try{
                if(baseMealViewStart==IntPtr.Zero){
                    var field=typeof(GhostView<MealGhost>).GetField("NativeMethodInfoPtr_Start_Protected_Virtual_Void_0",BindingFlags.NonPublic|BindingFlags.Static);
                    if(field==null)throw new MissingFieldException("Native base meal view Start metadata is unavailable.");
                    baseMealViewStart=(IntPtr)field.GetValue(null);
                    if(baseMealViewStart==IntPtr.Zero)throw new InvalidOperationException("Native base meal view Start pointer is unavailable.");
                }
                // The interop Start wrapper performs virtual redispatch and would
                // re-enter this prefix. Invoke the exact base MethodInfo directly.
                InvokeNative(baseMealViewStart,__instance.Pointer,IntPtr.Zero,out IntPtr exception);
                Il2CppInterop.Runtime.Il2CppException.RaiseExceptionIfNecessary(exception);
                SafeRefreshMeal(__instance);
            }
            catch(Exception ex){Report("Remote meal view initialization failed: "+ex.Message);}
            return false;
        }
        [DllImport("GameAssembly.dll",EntryPoint="il2cpp_runtime_invoke",CallingConvention=CallingConvention.Cdecl)]
        private static extern IntPtr InvokeNative(IntPtr method,IntPtr instance,IntPtr arguments,out IntPtr exception);
        public static BusinessCraftState Capture()
        {
            var manager=Ready();var meals=new List<BusinessMealState>();var processors=new List<BusinessProcessorState>();var venues=new List<BusinessVenueOrders>();
            var iterator=manager.Ghosts.GetEnumerator();
            while(iterator.MoveNext()){
                var ghost=iterator.Current;if(ghost==null||!ghost.IsValid)continue;
                var meal=ghost.TryCast<MealGhost>();if(meal!=null){meals.Add(CaptureMeal(meal));continue;}
                var processor=ghost.TryCast<FoodProcessorGhost>();if(processor!=null){
                    processors.Add(ObserveProcessor(processor,processor,null,true).State);continue;
                }
                var ingredient=ghost.TryCast<IngredientProcessorGhost>();if(ingredient!=null){
                    processors.Add(ObserveProcessor(ingredient,null,ingredient,true).State);continue;
                }
                var venue=ghost.TryCast<VenueAreaGhost>();if(venue!=null&&venue.Venue!=null)venues.Add(CaptureOrders(venue));
            }
            if(meals.Count>MaxMeals||processors.Count>MaxProcessors||venues.Count>MaxVenues)throw new InvalidOperationException("Craft world exceeds its snapshot limits.");
            // Native GhostReferenceList can briefly retain invalid destroyed meals;
            // only links that still resolve to members of this complete live roster
            // have presentation meaning. Do not publish stale references as objects.
            var liveMeals=new HashSet<string>();foreach(var meal in meals)liveMeals.Add(meal.Id);
            foreach(var processor in processors){var kept=new List<string>();var seen=new HashSet<string>();foreach(string id in processor.Meals)if(liveMeals.Contains(id)&&seen.Add(id))kept.Add(id);processor.Meals=kept.ToArray();}
            meals.Sort((a,b)=>string.CompareOrdinal(a.Id,b.Id));processors.Sort((a,b)=>string.CompareOrdinal(a.Id,b.Id));venues.Sort((a,b)=>string.CompareOrdinal(a.GhostId,b.GhostId));
            return new BusinessCraftState {Sequence=++sequence,Meals=meals.ToArray(),Processors=processors.ToArray(),Venues=venues.ToArray()};
        }
        // These observations live for one capture/apply only. Native references
        // read for comparison also form that transaction's exact rollback image.
        private readonly struct MealObservation
        {
            internal readonly BusinessMealState State;
            internal readonly ItemType Item;
            internal readonly Venue Venue;
            internal MealObservation(BusinessMealState state,ItemType item,Venue venue)
            {State=state;Item=item;Venue=venue;}
        }
        private static BusinessMealState CaptureMeal(MealGhost meal)=>ObserveMeal(meal).State;
        private static MealObservation ObserveMeal(MealGhost meal)
        {
            var item=meal.Type;if(item==null)throw new InvalidOperationException("Native meal type is not initialized.");
            var p=meal.Position;var q=meal.Rotation;var bounds=meal.Bounds;var spot=meal.Spot;var order=meal.VenueOrderReference;
            var venue=order?.Venue;var offset=spot.PositionOffset;var center=bounds.center;var size=bounds.size;
            var state=new BusinessMealState {Id=meal.Id,PrefabId=meal.PrefabId,ItemId=item.Guid,Scene=meal.SceneIndex,
                Active=meal.IsActive,Viewless=meal.IsViewLess,Baked=meal.IsBakedInstance,CanBeHeld=meal._canBeHeld,Held=meal._isHeld,
                Failed=meal._IsFailed_k__BackingField,Fill=(int)meal._fillState,PlayerAction=(int)meal._playerAction,
                Quality=meal.Quality,Freshness=meal.Freshness,Pose=new[]{p.x,p.y,p.z,q.x,q.y,q.z,q.w},
                Bounds=new[]{center.x,center.y,center.z,size.x,size.y,size.z},
                ParentId=Ref(meal.parent),ReservedById=Ref(meal._reservedBy),ProcessorId=Ref(meal.ProcessorMade),ChairId=Ref(meal.ForChair),
                VenueId=venue==null?"":venue.Guid,OrderId=order?.OrderId??"",
                SpotId=spot.SpotId,SpotType=(int)spot.SpotType,SpotOffset=new[]{offset.x,offset.y,offset.z}};
            return new MealObservation(state,item,venue);
        }
        private readonly struct ProcessorObservation
        {
            internal readonly BusinessProcessorState State;
            internal readonly IngredientProcessorType Type;
            internal readonly Il2CppSystem.Collections.Generic.List<GhostReference<MealGhost>> Meals;
            internal ProcessorObservation(BusinessProcessorState state,IngredientProcessorType type,Il2CppSystem.Collections.Generic.List<GhostReference<MealGhost>> meals)
            {State=state;Type=type;Meals=meals;}
        }
        private static ProcessorObservation ObserveProcessor(HoldableGhost ghost,FoodProcessorGhost food,IngredientProcessorGhost ingredient,bool includeAvailability)
        {
            var p=ghost.Position;var q=ghost.Rotation;var b=ghost.Bounds;var center=b.center;var size=b.size;
            var row=new BusinessProcessorState {Id=ghost.Id,PrefabId=ghost.PrefabId,ViewId=ghost.ViewId??"",Scene=ghost.SceneIndex,
                Active=ghost.IsActive,Viewless=ghost.IsViewLess,Baked=ghost.IsBakedInstance,CanBeHeld=ghost._canBeHeld,Held=ghost._isHeld,
                Pose=new[]{p.x,p.y,p.z,q.x,q.y,q.z,q.w},Bounds=new[]{center.x,center.y,center.z,size.x,size.y,size.z},ParentId=Ref(ghost.parent)};
            IngredientProcessorType type=null;Il2CppSystem.Collections.Generic.List<GhostReference<MealGhost>> backingMeals=null;
            if(food!=null){
                var mealList=food.Meals;backingMeals=mealList?.backingList;var recipes=food.Recipes;var venueReference=food.Venue;
                if(backingMeals==null||recipes==null)throw new InvalidOperationException("Native food processor configuration is not initialized: "+row.Id);
                row.Meals=new string[mealList.Count];for(int i=0;i<row.Meals.Length;i++)row.Meals[i]=Ref(mealList[i]);
                row.Recipes=new int[recipes.Length];for(int i=0;i<row.Recipes.Length;i++)row.Recipes[i]=(int)recipes[i];
                row.Busy=food._isInUse;row.ReservedById=Ref(food._reservedBy);row.VenueId=Ref(venueReference);
                row.InteractionSpot=CaptureSpot(food.InteractionSpot);row.ServeSpot=CaptureSpot(food.ServeSpot);
                row.SpawnMealOffset=Vector(food.SpawnMealPoint.LocalOffset);row.SpawnTrayOffset=Vector(food.SpawnTrayPoint.LocalOffset);
                if(includeAvailability){var venue=venueReference?.Value;var tasks=venue?.TaskQueue;var view=food.HasLoadedView?food.LoadedView.TryCast<FoodProcessor>():null;
                    row.AvailabilityKnown=view!=null&&tasks!=null;row.CanPrepare=row.AvailabilityKnown&&tasks.HasMakeMealTask(view.IsDrinksMachine);}
            }
            else{
                type=ingredient?.Type;if(type==null)throw new InvalidOperationException("Native ingredient processor type is not initialized: "+row.Id);
                var venueReference=ingredient.Venue;
                row.Ingredient=true;row.TypeId=type.Guid;row.ProcessingTime=ingredient.ProcessingTime;
                row.Busy=ingredient._isInUse;row.ReservedById=Ref(ingredient._reservedBy);row.VenueId=Ref(venueReference);row.InteractionSpot=CaptureSpot(ingredient.InteractionSpot);
                if(includeAvailability){var venue=venueReference?.Value;var tasks=venue?.TaskQueue;row.AvailabilityKnown=tasks!=null;
                    row.CanPrepare=row.AvailabilityKnown&&tasks.HasProcessingTask(type.AvailableProcessingTypes);}
            }
            return new ProcessorObservation(row,type,backingMeals);
        }
        private static float[] Vector(Vector3 value)=>new[]{value.x,value.y,value.z};
        private static BusinessProcessorSpot CaptureSpot(GhostAISpotReference spot)=>new BusinessProcessorSpot {Id=spot.SpotId,Type=(int)spot.SpotType,Offset=Vector(spot.PositionOffset)};
        private static BusinessVenueOrders CaptureOrders(VenueAreaGhost venue)
        {
            if(venue.Orders?._active==null)throw new InvalidOperationException("Venue orders are not initialized.");
            var rows=new List<BusinessOrderState>();
            foreach(var order in venue.Orders._active){
                if(order==null)throw new InvalidOperationException("Invalid native order.");
                var prices=new List<BusinessOrderPrice>();
                if(order.OrderedForPrice!=null){var keys=order.OrderedForPrice.Keys.GetEnumerator();while(keys.MoveNext())prices.Add(new BusinessOrderPrice {Key=keys.Current,Price=order.OrderedForPrice[keys.Current]});}
                prices.Sort((a,b)=>string.CompareOrdinal(a.Key,b.Key));
                var delivered=new List<BusinessDeliveredMeal>();
                if(order.DeliveredMeals!=null)foreach(var meal in order.DeliveredMeals)delivered.Add(new BusinessDeliveredMeal {
                    ItemId=meal.Meal?.Guid??"",MealId=Ref(meal.ActualMeal),Price=meal.Price,Quality=meal.MealQuality,Freshness=meal.FreshQuality});
                rows.Add(new BusinessOrderState {Id=order.Id,Valid=order.IsValid,CustomerId=Ref(order.ForCustomer),TableId=Ref(order.ForTable),ChairId=Ref(order.ForChair),
                    Ordered=CaptureTypes(order.OrderedMeals),Prepared=CaptureTypes(order.PreparedMealTypes),Prices=prices.ToArray(),Delivered=delivered.ToArray(),
                    Consumed=order.ConsumedMeals,ServiceQuality=order.ServiceQuality});
            }
            return new BusinessVenueOrders {GhostId=venue.Id,VenueId=venue.Venue.Guid,Orders=rows.ToArray()};
        }
        private static string[] CaptureTypes(Il2CppSystem.Collections.Generic.List<ItemType> items)
        {
            if(items==null)return Array.Empty<string>();var result=new string[items.Count];
            for(int i=0;i<result.Length;i++)result[i]=items[i]?.Guid??"";return result;
        }
        // Native GhostReference == null resolves the ghost, so an intact lazy
        // reference can compare null when its ghost is absent on this client.
        // Capture the stored identity independently of local roster availability.
        private static string Ref<T>(GhostReference<T> reference) where T:Ghost => ReferenceEquals(reference,null)?"":reference.ReferencedId??"";

        public static void Validate(BusinessCraftState state)
        {
            if(state==null||state.Sequence<=0||state.Meals==null||state.Meals.Length>MaxMeals||state.Processors==null||state.Processors.Length>MaxProcessors||state.Venues==null||state.Venues.Length>MaxVenues)
                throw new ArgumentException("Invalid craft state.");
            var ids=new HashSet<string>();
            foreach(var row in state.Meals){
                if(row==null||!Text(row.Id,256)||!ids.Add(row.Id)||!Text(row.PrefabId,256)||!Text(row.ItemId,128)||row.Scene< -1||row.Scene>100000||(row.Active&&row.Scene<0)||
                   row.Fill<0||row.Fill>1||row.PlayerAction<0||row.PlayerAction>2||!float.IsFinite(row.Quality)||!float.IsFinite(row.Freshness))throw new ArgumentException("Invalid meal state.");
                Floats(row.Pose,7);Floats(row.Bounds,6);Floats(row.SpotOffset,3);
                for(int i=0;i<3;i++)if(Math.Abs(row.Pose[i])>1000000||row.Bounds[i+3]<0)throw new ArgumentException("Invalid meal spatial state.");
                foreach(string id in new[]{row.ParentId,row.ReservedById,row.ProcessorId,row.ChairId})OptionalId(id,256);
                OptionalId(row.VenueId,128);OptionalId(row.OrderId,256);
                if(!string.IsNullOrEmpty(row.OrderId)&&string.IsNullOrEmpty(row.VenueId))throw new ArgumentException("Meal order has no venue.");
            }
            var processorIds=new HashSet<string>();var processorViews=new HashSet<string>();
            foreach(var row in state.Processors){
                if(row==null||!Text(row.Id,256)||!processorIds.Add(row.Id)||ids.Contains(row.Id)||!Text(row.PrefabId,256)||
                   row.Scene< -1||row.Scene>100000||(row.Active&&row.Scene<0)||row.Meals==null||row.Meals.Length>MaxMeals||(row.Ingredient&&row.Meals.Length!=0)||(!row.AvailabilityKnown&&row.CanPrepare))throw new ArgumentException("Invalid processor state.");
                Floats(row.Pose,7);Floats(row.Bounds,6);BoundedVector(row.SpawnMealOffset);BoundedVector(row.SpawnTrayOffset);
                for(int i=0;i<3;i++)if(Math.Abs(row.Pose[i])>1000000||Math.Abs(row.Bounds[i])>1000000||row.Bounds[i+3]<0||row.Bounds[i+3]>1000000)throw new ArgumentException("Invalid processor spatial state.");
                float norm=0;for(int i=3;i<7;i++)norm+=row.Pose[i]*row.Pose[i];if(!float.IsFinite(norm)||norm<.000001f)throw new ArgumentException("Invalid processor rotation.");
                OptionalId(row.ViewId,256);OptionalId(row.ParentId,256);OptionalId(row.VenueId,256);OptionalId(row.TypeId,128);
                if(row.ViewId.Length!=0&&!processorViews.Add(row.ViewId))throw new ArgumentException("Duplicate processor view identity.");
                ValidateSpot(row.InteractionSpot);ValidateSpot(row.ServeSpot);
                if(row.Recipes==null||row.Recipes.Length>64||!float.IsFinite(row.ProcessingTime)||row.ProcessingTime<0||row.ProcessingTime>86400||
                   (row.Ingredient&&!Text(row.TypeId,128))||(!row.Ingredient&&row.TypeId.Length!=0)||(row.Ingredient&&row.Recipes.Length!=0))throw new ArgumentException("Invalid processor configuration.");
                foreach(int recipe in row.Recipes)if(recipe<0||recipe>9)throw new ArgumentException("Unknown processor recipe type.");
                if(row.ParentId==row.Id||row.VenueId==row.Id)throw new ArgumentException("Processor refers to itself as a structural parent.");
                OptionalId(row.ReservedById,256);var seen=new HashSet<string>();
                foreach(string id in row.Meals)if(!Text(id,256)||!seen.Add(id)||!ids.Contains(id))throw new ArgumentException("Processor refers to a missing meal.");
            }
            var venueIds=new HashSet<string>();
            foreach(var venue in state.Venues){
                if(venue==null||!Text(venue.GhostId,256)||!venueIds.Add(venue.GhostId)||ids.Contains(venue.GhostId)||processorIds.Contains(venue.GhostId)||!Text(venue.VenueId,128)||venue.Orders==null||venue.Orders.Length>10000)throw new ArgumentException("Invalid venue orders.");
                var orderIds=new HashSet<string>();
                foreach(var order in venue.Orders){
                    if(order==null||!Text(order.Id,256)||!orderIds.Add(order.Id)||!float.IsFinite(order.ServiceQuality)||order.Consumed<0||
                        order.Ordered==null||order.Ordered.Length>10000||order.Prepared==null||order.Prepared.Length>10000||order.Prices==null||order.Prices.Length>10000||order.Delivered==null||order.Delivered.Length>10000)
                        throw new ArgumentException("Invalid active order.");
                    foreach(string id in new[]{order.CustomerId,order.TableId,order.ChairId})OptionalId(id,256);
                    foreach(string id in order.Ordered)if(!Text(id,128))throw new ArgumentException("Invalid ordered meal type.");
                    foreach(string id in order.Prepared)if(!Text(id,128))throw new ArgumentException("Invalid prepared meal type.");
                    var prices=new HashSet<string>();foreach(var item in order.Prices)if(item==null||!Text(item.Key,256)||!prices.Add(item.Key))throw new ArgumentException("Invalid order price.");
                    foreach(var meal in order.Delivered){if(meal==null||!Text(meal.ItemId,128)||!float.IsFinite(meal.Quality)||!float.IsFinite(meal.Freshness))throw new ArgumentException("Invalid delivered meal.");OptionalId(meal.MealId,256);}
                }
            }
        }
        private static bool Text(string text,int limit)=>DedicatedProtocol.Text(text,limit);
        private static void OptionalId(string id,int limit){if(id==null||id.Length>limit)throw new ArgumentException("Invalid optional craft identity.");}
        private static void Floats(float[] values,int count){if(values==null||values.Length!=count)throw new ArgumentException("Invalid craft vector.");foreach(float value in values)if(!float.IsFinite(value))throw new ArgumentException("Nonfinite craft vector.");}
        private static void BoundedVector(float[] values){Floats(values,3);foreach(float value in values)if(Math.Abs(value)>1000000)throw new ArgumentException("Craft vector exceeds world bounds.");}
        private static void ValidateSpot(BusinessProcessorSpot spot)
        {
            if(spot==null||(spot.Type!=-1&&(spot.Type<0||(spot.Type&~2047)!=0)))throw new ArgumentException("Invalid processor AI spot.");
            BoundedVector(spot.Offset);
        }

        public static void Apply(BusinessCraftState state)
            =>ApplyProfiled(state,new CraftProfile(),false);
        private static void ApplyProfiled(BusinessCraftState state,CraftProfile profile,bool validated)
        {
            if(!clientAuthority)return;
            try{ApplyState(state,profile,validated);}catch{profile.Failed=true;throw;}
            finally{ReportProfile(state,profile);}
        }
        private static void ApplyState(BusinessCraftState state,CraftProfile profile,bool validated=false)
        {
            if(!clientAuthority)return;if(Faulted)throw new InvalidOperationException("Craft mirror requires a fresh native world import.");
            long stage=Stopwatch.GetTimestamp();
            if(!validated){Validate(state);profile.Validation=Stopwatch.GetTimestamp()-stage;}
            if(state.Sequence<=applied)return;var manager=Ready();EnsureMealIndex(manager);
            profile.MealCount=state.Meals.Length;profile.ProcessorCount=state.Processors.Length;profile.VenueCount=state.Venues.Length;
            var meals=new Dictionary<string,MealGhost>(StringComparer.Ordinal);var newMeals=new List<MealGhost>();
            var processors=new Dictionary<string,HoldableGhost>(StringComparer.Ordinal);var newProcessors=new List<HoldableGhost>();var refreshProcessors=new List<HoldableGhost>();
            var updates=new List<(Action Apply,Action Undo)>();
            // All DTO/reference/prefab validation and new allocations complete before
            // registering, changing or removing anything in the live ghost registry.
            stage=Stopwatch.GetTimestamp();
            foreach(var row in state.Meals){
                var existing=manager.GetGhostById(row.Id);var meal=existing?.TryCast<MealGhost>();
                if(existing!=null&&meal==null)throw new InvalidOperationException("Meal ID collides with another native ghost.");
                var item=ResolveItem(row.ItemId);var venue=string.IsNullOrEmpty(row.VenueId)?null:ResolveVenue(row.VenueId);
                if(meal==null){
                    var serial=SerializationManager.Instance;
                    if(serial?.Prefabs?.prefabDictionary==null||!serial.Prefabs.prefabDictionary.ContainsKey(row.PrefabId))throw new InvalidOperationException("Meal prefab is missing: "+row.PrefabId);
                    meal=new MealGhost(row.Id,row.PrefabId);SetMealRaw(meal,row,item,venue,false);newMeals.Add(meal);profile.ChangedMeals++;
                }
                else{
                    var observation=ObserveMeal(meal);var before=observation.State;var beforeItem=observation.Item;var beforeVenue=observation.Venue;var target=meal;
                    if(before.PrefabId!=row.PrefabId)throw new InvalidOperationException("Meal prefab changed; reload the server world.");
                    if(!meal.IsValid||!SameMeal(row,before)||beforeItem.Pointer!=item.Pointer||
                        (beforeVenue?.Pointer??IntPtr.Zero)!=(venue?.Pointer??IntPtr.Zero)){
                        updates.Add((()=>SetMealRaw(target,row,item,venue,true),()=>SetMealRaw(target,before,beforeItem,beforeVenue,true)));
                        profile.ChangedMeals++;
                    }
                }
                meals.Add(row.Id,meal);
            }
            profile.Meals=Stopwatch.GetTimestamp()-stage;stage=Stopwatch.GetTimestamp();
            foreach(var row in state.Processors)if(StageProcessor(manager,row,meals,processors,newProcessors,refreshProcessors,updates,state.Sequence))profile.ChangedProcessors++;
            profile.Processors=Stopwatch.GetTimestamp()-stage;stage=Stopwatch.GetTimestamp();
            foreach(var row in state.Venues)if(StageOrders(manager,row,updates))profile.ChangedVenues++;
            profile.Orders=Stopwatch.GetTimestamp()-stage;stage=Stopwatch.GetTimestamp();
            var extras=new List<KeyValuePair<string,IntPtr>>();
            foreach(var candidate in MealGhosts.Snapshot()){
                if(meals.ContainsKey(candidate.Key))continue;
                var meal=manager.GetGhostById(candidate.Key)?.TryCast<MealGhost>();
                if(meal==null||meal.Pointer!=candidate.Value){MealGhosts.Forget(manager.Pointer,candidate.Key,candidate.Value);continue;}
                if(meal.IsValid)extras.Add(candidate);
            }
            var removedProcessors=new List<HoldableGhost>();
            foreach(var pair in CreatedProcessors)if(!processors.ContainsKey(pair.Key)){
                var actual=manager.GetGhostById(pair.Key);
                if(actual!=null&&actual.Pointer!=pair.Value.Pointer)throw new InvalidOperationException("Created processor identity was replaced outside the craft mirror: "+pair.Key);
                if(actual!=null){if(actual.IsBakedInstance)throw new InvalidOperationException("A mirrored runtime processor became baked: "+pair.Key);removedProcessors.Add(pair.Value);}
            }
            var registrations=new List<Action>();var rollbackRegistrations=new List<Action>();
            foreach(var ghost in newMeals){var value=ghost;registrations.Add(()=>RegisterPrepared(manager,value));rollbackRegistrations.Add(()=>RemoveIfRegistered(manager,value));}
            foreach(var ghost in newProcessors){var value=ghost;registrations.Add(()=>RegisterPrepared(manager,value));rollbackRegistrations.Add(()=>RemoveIfRegistered(manager,value));}
            profile.Roster=Stopwatch.GetTimestamp()-stage;stage=Stopwatch.GetTimestamp();
            IsApplying=true;
            try{
                CommitPrepared(registrations,rollbackRegistrations,updates);
                profile.Commit=Stopwatch.GetTimestamp()-stage;
            }
            catch{
                IsApplying=false;throw;
            }
            // Only a complete, validated and successfully applied roster may delete
            // obsolete local meal ghosts. An incomplete wire frame never reaches here.
            try{
                stage=Stopwatch.GetTimestamp();
                foreach(var candidate in extras){
                    // Registration/commit callbacks may have replaced this ID
                    // since planning. Never remove an obsolete native wrapper.
                    var meal=manager.GetGhostById(candidate.Key)?.TryCast<MealGhost>();
                    if(meal!=null&&meal.IsValid&&meal.Pointer==candidate.Value&&!meals.ContainsKey(candidate.Key))HideRemove(manager,meal);
                }
                foreach(var processor in removedProcessors)HideRemove(manager,processor);
                var absent=new List<string>();foreach(string id in CreatedProcessors.Keys)if(!processors.ContainsKey(id))absent.Add(id);
                foreach(string id in absent)CreatedProcessors.Remove(id);
                foreach(var processor in newProcessors)CreatedProcessors.Add(processor.Id,processor);
                foreach(var processor in refreshProcessors){
                    manager.UpdateGhostViewPresence(processor);
                    var view=processor.HasLoadedView?processor.LoadedView.TryCast<Component>():null;
                    if(view!=null)view.transform.SetPositionAndRotation(processor.Position,processor.Rotation);
                }
                Availability.Clear();foreach(var row in state.Processors)Availability.Add(row.Id,row);
                applied=state.Sequence;
                if(newProcessors.Count!=0||removedProcessors.Count!=0)Report("CRAFT_LIFECYCLE sequence="+state.Sequence+" created="+newProcessors.Count+" removed="+removedProcessors.Count+" tracked="+CreatedProcessors.Count);
                foreach(var meal in meals.Values){
                    try{manager.UpdateGhostViewPresence(meal);if(meal.HasLoadedView)SafeRefreshMeal(meal.LoadedView.TryCast<MealView>());}
                    catch(Exception ex){Report("Craft state applied, meal view refresh failed: "+ex.Message);}
                }
            }
            catch{Faulted=true;throw;}
            finally{profile.Presentation=Stopwatch.GetTimestamp()-stage;IsApplying=false;}
        }
        // Kept independent of Unity so failures after a native callback/partial
        // registration can be fault-injected without a game process. Rollback is
        // best-effort: a failed native mutation always requires a fresh import.
        internal static void CommitPrepared(IReadOnlyList<Action> registrations,IReadOnlyList<Action> rollbackRegistrations,
            IReadOnlyList<(Action Apply,Action Undo)> updates,Func<bool> sameWorld=null)
        {
            if(registrations.Count!=rollbackRegistrations.Count)throw new ArgumentException("Invalid craft transaction plan.");
            int registered=-1,changed=-1;
            try{
                void CheckWorld(){if(sameWorld!=null&&!sameWorld())throw new InvalidOperationException("Native craft world changed during an entity callback.");}
                for(int i=0;i<registrations.Count;i++){CheckWorld();registered=i;registrations[i]();CheckWorld();}
                for(int i=0;i<updates.Count;i++){CheckWorld();changed=i;updates[i].Apply();CheckWorld();}
            }
            catch{
                if(sameWorld==null||sameWorld())Faulted=true;
                for(int i=changed;i>=0&&(sameWorld==null||sameWorld());i--)try{updates[i].Undo();}catch{}
                for(int i=registered;i>=0&&(sameWorld==null||sameWorld());i--)try{rollbackRegistrations[i]();}catch{}
                throw;
            }
        }
        private static void RemoveIfRegistered(GhostManager manager,Ghost ghost)
        {
            var actual=manager.GetGhostById(ghost.Id);if(actual==null)return;
            if(actual.Pointer!=ghost.Pointer)throw new InvalidOperationException("Cannot roll back a replaced craft ghost identity.");
            HideRemove(manager,ghost);
        }
        private static void RegisterPrepared(GhostManager manager,Ghost ghost)
        {
            manager.RegisterGhostImmediate(ghost);
            var actual=manager.GetGhostById(ghost.Id);
            if(actual==null||actual.Pointer!=ghost.Pointer||!actual.IsValid)throw new InvalidOperationException("Native craft registration did not retain the prepared identity: "+ghost.Id);
        }
        private static void SetMealRaw(MealGhost meal,BusinessMealState row,ItemType type,Venue venue,bool registered)
        {
            var p=new Vector3(row.Pose[0],row.Pose[1],row.Pose[2]);var q=new Quaternion(row.Pose[3],row.Pose[4],row.Pose[5],row.Pose[6]);
            float norm=q.x*q.x+q.y*q.y+q.z*q.z+q.w*q.w;q=norm>.000001f&&float.IsFinite(norm)?Quaternion.Normalize(q):Quaternion.identity;
            meal.Type=type;meal.Quality=row.Quality;meal.Freshness=row.Freshness;
            meal._fillState=(MealGhost.FillStates)row.Fill;meal._playerAction=(MealGhost.PlayerActions)row.PlayerAction;meal._IsFailed_k__BackingField=row.Failed;
            meal._canBeHeld=row.CanBeHeld;meal._isHeld=row.Held;meal._isViewLess=row.Viewless;meal._IsBakedInstance_k__BackingField=row.Baked;
            meal.parent=new GhostReference<Ghost>(row.ParentId);meal._reservedBy=new GhostReference<Ghost>(row.ReservedById);
            meal.ProcessorMade=new GhostReference<FoodProcessorGhost>(row.ProcessorId);meal.ForChair=new GhostReference<ChairGhost>(row.ChairId);
            meal.VenueOrderReference=venue==null?null:new VenueOrderReference(venue,row.OrderId);
            meal.Spot=new GhostAISpotReference(row.SpotId,new Vector3(row.SpotOffset[0],row.SpotOffset[1],row.SpotOffset[2]),(AISpot.SpotType)row.SpotType);
            meal._bounds=new Bounds(new Vector3(row.Bounds[0],row.Bounds[1],row.Bounds[2]),new Vector3(row.Bounds[3],row.Bounds[4],row.Bounds[5]));
            meal._IsActive_k__BackingField=row.Active;
            if(!registered){meal._sceneIndex=row.Scene;meal._transform=new PositionRotation(p,q);}
            else if(meal.SceneIndex!=row.Scene)GhostManager.TransferGhostToScene(meal,row.Scene,new PositionRotation(p,q));
            else{meal.Position=p;meal.Rotation=q;}
        }
        private static bool StageProcessor(GhostManager manager,BusinessProcessorState row,Dictionary<string,MealGhost> meals,
            Dictionary<string,HoldableGhost> processors,List<HoldableGhost> additions,List<HoldableGhost> refresh,List<(Action Apply,Action Undo)> updates,long stateSequence)
        {
            var target=manager.GetGhostById(row.Id);
            string expected=row.Ingredient?typeof(IngredientProcessorGhost).FullName:typeof(FoodProcessorGhost).FullName;
            if(target!=null&&(target.GetIl2CppType().FullName!=expected||!target.IsValid))throw ProcessorMismatch(row,target,stateSequence,"wrong-native-type-or-invalid");
            FoodProcessorGhost food=null;IngredientProcessorGhost ingredient=null;HoldableGhost ghost=null;
            ProcessorObservation observation=default;
            if(target!=null){
                if(row.Ingredient){ingredient=target.TryCast<IngredientProcessorGhost>();ghost=ingredient;}
                else{food=target.TryCast<FoodProcessorGhost>();ghost=food;}
                if(ghost==null)throw ProcessorMismatch(row,target,stateSequence,"wrong-native-type-or-invalid");
                observation=ObserveProcessor(ghost,food,ingredient,false);
                var identity=observation.State;
                if(identity.PrefabId!=row.PrefabId||identity.ViewId!=row.ViewId||identity.Baked!=row.Baked)
                    throw ProcessorMismatch(row,target,stateSequence,"identity-or-baked-state-changed");
            }
            if(target==null&&row.Baked)throw ProcessorMismatch(row,null,stateSequence,"missing-baked-ghost");
            var sameView=row.ViewId.Length==0?null:manager.GetGhostByViewId(row.ViewId);
            if(sameView!=null&&(target==null||sameView.Pointer!=target.Pointer))throw ProcessorMismatch(row,target,stateSequence,"view-identity-collision");
            // These structural references are native world objects, not asset IDs.
            // Occupancy references remain lazy as before: NPC/meal streams have their
            // own ordered publications and can briefly name not-yet-shown actors.
            if(row.ParentId.Length!=0){var parent=manager.GetGhostById(row.ParentId)?.TryCast<TableGhost>();
                if(parent==null||!parent.IsValid||parent.Id!=row.ParentId)throw ProcessorMismatch(row,target,stateSequence,"missing-or-wrong-table-parent:"+row.ParentId);}
            if(row.VenueId.Length!=0){var venue=manager.GetGhostById(row.VenueId)?.TryCast<VenueAreaGhost>();
                if(venue==null||!venue.IsValid||venue.Id!=row.VenueId)throw ProcessorMismatch(row,target,stateSequence,"missing-or-wrong-venue:"+row.VenueId);}
            var prefabs=SerializationManager.Instance?.Prefabs?.prefabDictionary;
            if(prefabs==null||!prefabs.ContainsKey(row.PrefabId))throw ProcessorMismatch(row,target,stateSequence,"missing-prefab");
            var prefab=prefabs[row.PrefabId];if(prefab==null)throw ProcessorMismatch(row,target,stateSequence,"missing-prefab");
            IngredientProcessorType type=null;
            if(row.Ingredient){
                var view=prefab.GetComponent<IngredientProcessorView>();
                type=view?.ProcessorType;
                if(view==null||view.GetIl2CppType().FullName!=typeof(IngredientProcessorView).FullName||type==null||type.Guid!=row.TypeId)
                    throw ProcessorMismatch(row,target,stateSequence,"prefab-ingredient-type-mismatch");
                if(target==null)ghost=view.CreateGhostBaseFromView()?.TryCast<HoldableGhost>();
            }
            else{
                var view=prefab.GetComponent<FoodProcessor>();
                if(view==null||view.GetIl2CppType().FullName!=typeof(FoodProcessor).FullName)throw ProcessorMismatch(row,target,stateSequence,"prefab-food-type-mismatch");
                if(target==null)ghost=view.CreateGhostBaseFromView()?.TryCast<HoldableGhost>();
            }
            if(target==null){
                if(ghost==null||ghost.GetIl2CppType().FullName!=expected)throw ProcessorMismatch(row,ghost,stateSequence,"factory-created-wrong-native-type");
                if(row.Ingredient)ingredient=ghost.TryCast<IngredientProcessorGhost>();else food=ghost.TryCast<FoodProcessorGhost>();
                if(food!=null&&food.Meals?.backingList==null)throw ProcessorMismatch(row,ghost,stateSequence,"missing-meal-list");
            }
            // Identity, prefab and structural references were validated above.
            // Observe native fields again every update; equal incoming DTOs alone
            // cannot hide local drift or an externally replaced native object.
            var before=observation.State;
            var oldType=observation.Type;var oldMeals=observation.Meals;
            if(before!=null&&SameProcessor(row,before)&&(!row.Ingredient||oldType?.Pointer==type?.Pointer)){
                processors.Add(row.Id,ghost);return false;
            }
            var preparedMeals=new Il2CppSystem.Collections.Generic.List<GhostReference<MealGhost>>();
            foreach(string id in row.Meals)NativeValueList.Add(preparedMeals,new GhostReference<MealGhost>(meals[id]));
            if(target==null){
                // Exactly the native save-loader factory, without invoking prefab
                // FillGhostBaseFromViewState (which would copy the asset's identity).
                // The factory is detached; all native fields are populated before
                // registration can create a view or notify listeners.
                ghost._id=row.Id;ghost._viewId=row.ViewId;ghost._prefabId=row.PrefabId;
                SetProcessorRaw(ghost,row,type,preparedMeals,false);additions.Add(ghost);refresh.Add(ghost);
            }
            else{
                updates.Add((()=>SetProcessorRaw(ghost,row,type,preparedMeals,true),()=>SetProcessorRaw(ghost,before,oldType,oldMeals,true)));
                if(before.Scene!=row.Scene||before.Active!=row.Active||before.Viewless!=row.Viewless||!SameVector(before.Pose,row.Pose))refresh.Add(ghost);
            }
            processors.Add(row.Id,ghost);
            return true;
        }
        private static bool SameVector(float[] a,float[] b){if(a.Length!=b.Length)return false;for(int i=0;i<a.Length;i++)if(a[i]!=b[i])return false;return true;}
        private static bool SameValues<T>(T[] a,T[] b)
        {
            if(a==null||b==null||a.Length!=b.Length)return false;
            for(int i=0;i<a.Length;i++)if(!EqualityComparer<T>.Default.Equals(a[i],b[i]))return false;
            return true;
        }
        internal static bool SameMeal(BusinessMealState a,BusinessMealState b,bool appliedPose=false)=>a!=null&&b!=null&&
            a.Id==b.Id&&a.PrefabId==b.PrefabId&&a.ItemId==b.ItemId&&a.Scene==b.Scene&&a.Active==b.Active&&
            a.Viewless==b.Viewless&&a.Baked==b.Baked&&a.CanBeHeld==b.CanBeHeld&&a.Held==b.Held&&a.Failed==b.Failed&&
            a.Fill==b.Fill&&a.PlayerAction==b.PlayerAction&&a.Quality==b.Quality&&a.Freshness==b.Freshness&&
            (appliedPose?SameAppliedPose(a.Pose,b.Pose):SameValues(a.Pose,b.Pose))&&SameValues(a.Bounds,b.Bounds)&&a.ParentId==b.ParentId&&a.ReservedById==b.ReservedById&&
            a.ProcessorId==b.ProcessorId&&a.ChairId==b.ChairId&&a.VenueId==b.VenueId&&a.OrderId==b.OrderId&&
            a.SpotId==b.SpotId&&a.SpotType==b.SpotType&&SameValues(a.SpotOffset,b.SpotOffset);
        private static bool SameSpot(BusinessProcessorSpot a,BusinessProcessorSpot b)=>a!=null&&b!=null&&
            a.Id==b.Id&&a.Type==b.Type&&SameValues(a.Offset,b.Offset);
        internal static bool SameProcessor(BusinessProcessorState a,BusinessProcessorState b,bool appliedPose=false)=>a!=null&&b!=null&&
            a.Id==b.Id&&a.PrefabId==b.PrefabId&&a.ViewId==b.ViewId&&a.Scene==b.Scene&&a.Active==b.Active&&
            a.Viewless==b.Viewless&&a.Baked==b.Baked&&a.CanBeHeld==b.CanBeHeld&&a.Held==b.Held&&
            (appliedPose?SameAppliedPose(a.Pose,b.Pose):SameValues(a.Pose,b.Pose))&&SameValues(a.Bounds,b.Bounds)&&a.ParentId==b.ParentId&&a.VenueId==b.VenueId&&
            a.TypeId==b.TypeId&&a.ProcessingTime==b.ProcessingTime&&SameSpot(a.InteractionSpot,b.InteractionSpot)&&
            SameSpot(a.ServeSpot,b.ServeSpot)&&SameValues(a.SpawnMealOffset,b.SpawnMealOffset)&&SameValues(a.SpawnTrayOffset,b.SpawnTrayOffset)&&
            SameValues(a.Recipes,b.Recipes)&&a.Ingredient==b.Ingredient&&a.Busy==b.Busy&&a.ReservedById==b.ReservedById&&SameValues(a.Meals,b.Meals);
        private static bool SameAppliedPose(float[] desired,float[] actual)
        {
            if(desired==null||actual==null||desired.Length!=7||actual.Length!=7)return false;
            for(int i=0;i<3;i++)if(desired[i]!=actual[i])return false;
            var q=new Quaternion(desired[3],desired[4],desired[5],desired[6]);
            float norm=q.x*q.x+q.y*q.y+q.z*q.z+q.w*q.w;
            q=norm>.000001f&&float.IsFinite(norm)?Quaternion.Normalize(q):Quaternion.identity;
            return actual[3]==q.x&&actual[4]==q.y&&actual[5]==q.z&&actual[6]==q.w;
        }
        // AvailabilityKnown/CanPrepare are derived worker UI permissions, not
        // native processor fields. Apply always refreshes Availability separately.
        internal static bool SameOrders(BusinessVenueOrders a,BusinessVenueOrders b)
        {
            if(a==null||b==null||a.GhostId!=b.GhostId||a.VenueId!=b.VenueId||a.Orders==null||b.Orders==null||a.Orders.Length!=b.Orders.Length)return false;
            for(int i=0;i<a.Orders.Length;i++)if(!SameOrder(a.Orders[i],b.Orders[i]))return false;
            return true;
        }
        private static bool SameOrder(BusinessOrderState a,BusinessOrderState b)
        {
            if(a==null||b==null||a.Id!=b.Id||a.Valid!=b.Valid||a.CustomerId!=b.CustomerId||a.TableId!=b.TableId||a.ChairId!=b.ChairId||
                a.Consumed!=b.Consumed||a.ServiceQuality!=b.ServiceQuality||!SameValues(a.Ordered,b.Ordered)||!SameValues(a.Prepared,b.Prepared)||
                !SamePrices(a.Prices,b.Prices)||a.Delivered==null||b.Delivered==null||a.Delivered.Length!=b.Delivered.Length)return false;
            for(int i=0;i<a.Delivered.Length;i++){
                var x=a.Delivered[i];var y=b.Delivered[i];
                if(x==null||y==null||x.ItemId!=y.ItemId||x.MealId!=y.MealId||x.Price!=y.Price||x.Quality!=y.Quality||x.Freshness!=y.Freshness)return false;
            }
            return true;
        }
        private static bool SamePrices(BusinessOrderPrice[] a,BusinessOrderPrice[] b)
        {
            if(a==null||b==null||a.Length!=b.Length)return false;
            var values=new Dictionary<string,int>(StringComparer.Ordinal);
            foreach(var price in a)if(price==null||price.Key==null||!values.TryAdd(price.Key,price.Price))return false;
            foreach(var price in b)if(price==null||price.Key==null||!values.Remove(price.Key,out int expected)||expected!=price.Price)return false;
            return true;
        }
        private static string DescribeOrdersMismatch(BusinessVenueOrders expected,BusinessVenueOrders actual)
        {
            // Bounded field paths only: do not log order payloads, customers,
            // prices or item IDs when an exact native readback fails.
            if(expected==null||actual==null)return "venue";
            if(expected.GhostId!=actual.GhostId)return "venue.GhostId";
            if(expected.VenueId!=actual.VenueId)return "venue.VenueId";
            if(expected.Orders==null||actual.Orders==null)return "Orders";
            if(expected.Orders.Length!=actual.Orders.Length)return "Orders.Length";
            for(int i=0;i<expected.Orders.Length;i++){
                var a=expected.Orders[i];var b=actual.Orders[i];if(SameOrder(a,b))continue;
                string path="Orders["+i+"].";
                if(a==null||b==null)return path+"row";
                if(a.Id!=b.Id)return path+"Id";if(a.Valid!=b.Valid)return path+"Valid";
                if(a.CustomerId!=b.CustomerId)return path+"CustomerId";
                if(a.TableId!=b.TableId)return path+"TableId";if(a.ChairId!=b.ChairId)return path+"ChairId";
                if(a.Consumed!=b.Consumed)return path+"Consumed";if(a.ServiceQuality!=b.ServiceQuality)return path+"ServiceQuality";
                if(!SameValues(a.Ordered,b.Ordered))return path+"Ordered";if(!SameValues(a.Prepared,b.Prepared))return path+"Prepared";
                if(!SamePrices(a.Prices,b.Prices))return path+"Prices";
                if(a.Delivered==null||b.Delivered==null)return path+"Delivered";
                if(a.Delivered.Length!=b.Delivered.Length)return path+"Delivered.Length";
                for(int j=0;j<a.Delivered.Length;j++){
                    var x=a.Delivered[j];var y=b.Delivered[j];string meal=path+"Delivered["+j+"].";
                    if(x==null||y==null)return meal+"row";
                    if(x.ItemId!=y.ItemId)return meal+"ItemId";if(x.MealId!=y.MealId)return meal+"MealId";
                    if(x.Price!=y.Price)return meal+"Price";if(x.Quality!=y.Quality)return meal+"Quality";
                    if(x.Freshness!=y.Freshness)return meal+"Freshness";
                }
            }
            return "unknown";
        }
        private static GhostAISpotReference PrepareSpot(BusinessProcessorSpot spot)=>new GhostAISpotReference(spot.Id,new Vector3(spot.Offset[0],spot.Offset[1],spot.Offset[2]),(AISpot.SpotType)spot.Type);
        private static void SetProcessorRaw(HoldableGhost ghost,BusinessProcessorState row,IngredientProcessorType type,
            Il2CppSystem.Collections.Generic.List<GhostReference<MealGhost>> meals,bool registered)
        {
            var p=new Vector3(row.Pose[0],row.Pose[1],row.Pose[2]);var q=Quaternion.Normalize(new Quaternion(row.Pose[3],row.Pose[4],row.Pose[5],row.Pose[6]));
            ghost._canBeHeld=row.CanBeHeld;ghost._isHeld=row.Held;ghost._isViewLess=row.Viewless;
            ghost.parent=new GhostReference<Ghost>(row.ParentId);ghost._viewId=row.ViewId;
            ghost._bounds=new Bounds(new Vector3(row.Bounds[0],row.Bounds[1],row.Bounds[2]),new Vector3(row.Bounds[3],row.Bounds[4],row.Bounds[5]));
            ghost._IsActive_k__BackingField=row.Active;ghost._IsBakedInstance_k__BackingField=row.Baked;
            var food=ghost.TryCast<FoodProcessorGhost>();
            if(food!=null){
                food._isInUse=row.Busy;food._reservedBy=new GhostReference<Ghost>(row.ReservedById);food.Venue=new GhostReference<VenueAreaGhost>(row.VenueId);
                food.InteractionSpot=PrepareSpot(row.InteractionSpot);food.ServeSpot=PrepareSpot(row.ServeSpot);
                food.SpawnMealPoint=new RelativeVector3(new Vector3(row.SpawnMealOffset[0],row.SpawnMealOffset[1],row.SpawnMealOffset[2]));
                food.SpawnTrayPoint=new RelativeVector3(new Vector3(row.SpawnTrayOffset[0],row.SpawnTrayOffset[1],row.SpawnTrayOffset[2]));
                var recipes=new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<RecipeTypes>(row.Recipes.Length);
                for(int i=0;i<row.Recipes.Length;i++)recipes[i]=(RecipeTypes)row.Recipes[i];food.Recipes=recipes;food.Meals.backingList=meals;
            }
            else{
                var ingredient=ghost.Cast<IngredientProcessorGhost>();ingredient.Type=type;ingredient.ProcessingTime=row.ProcessingTime;
                ingredient._isInUse=row.Busy;ingredient._reservedBy=new GhostReference<Ghost>(row.ReservedById);
                ingredient.Venue=new GhostReference<VenueAreaGhost>(row.VenueId);ingredient.InteractionSpot=PrepareSpot(row.InteractionSpot);
            }
            if(!registered){ghost._sceneIndex=row.Scene;ghost._transform=new PositionRotation(p,q);}
            else if(ghost.SceneIndex!=row.Scene)GhostManager.TransferGhostToScene(ghost,row.Scene,new PositionRotation(p,q));
            else{ghost.Position=p;ghost.Rotation=q;}
        }
        private static InvalidOperationException ProcessorMismatch(BusinessProcessorState row,Ghost target,long stateSequence,string reason)
        {
            // Keep the validation failure before any native mutation, but distinguish
            // roster drift from a missing list. The former error hid both causes.
            string observed="absent",prefab="",scene="",valid="";
            if(target!=null){
                observed=target.GetIl2CppType().FullName;prefab=target.PrefabId;
                scene=target.SceneIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);valid=target.IsValid.ToString();
            }
            return new InvalidOperationException("Craft processor mismatch: "+reason+" id="+row.Id+" ingredient="+row.Ingredient+
                " sequence="+stateSequence+" lastApplied="+applied+" previouslyPresent="+Availability.ContainsKey(row.Id)+
                " observedType="+observed+" prefab="+prefab+" scene="+scene+" valid="+valid+" authoritativeMeals="+row.Meals.Length+".");
        }
        private static bool StageOrders(GhostManager manager,BusinessVenueOrders row,List<(Action Apply,Action Undo)> updates)
        {
            var venue=manager.GetGhostById(row.GhostId)?.TryCast<VenueAreaGhost>();
            if(venue?.Venue==null||venue.Venue.Guid!=row.VenueId||venue.Orders==null)throw new InvalidOperationException("Order venue is absent from imported world.");
            // Matching wire rows do not prove the native list's secondary index
            // or venue references are sound. Repair those inconsistencies too.
            if(OrderLookupConsistent(venue)&&SameOrders(row,CaptureOrders(venue)))return false;
            var orders=new Il2CppSystem.Collections.Generic.List<Order>();var lookup=new Il2CppSystem.Collections.Generic.Dictionary<string,Order>();
            foreach(var item in row.Orders){
                var order=new Order {Id=item.Id,IsValid=item.Valid,Venue=venue.Venue,ForCustomer=new GhostReference<CustomerGhost>(item.CustomerId),
                    ForTable=new GhostReference<TableGhost>(item.TableId),ForChair=new GhostReference<ChairGhost>(item.ChairId),ConsumedMeals=item.Consumed,ServiceQuality=item.ServiceQuality,
                    OrderedMeals=PrepareTypes(item.Ordered),PreparedMealTypes=PrepareTypes(item.Prepared),
                    OrderedForPrice=new Il2CppSystem.Collections.Generic.Dictionary<string,int>(),DeliveredMeals=new Il2CppSystem.Collections.Generic.List<FoodDeliveredState>()};
                foreach(var price in item.Prices)order.OrderedForPrice.Add(price.Key,price.Price);
                foreach(var meal in item.Delivered)NativeValueList.Add(order.DeliveredMeals,new FoodDeliveredState {Meal=ResolveItem(meal.ItemId),ActualMeal=new GhostReference<MealGhost>(meal.MealId),
                    Price=meal.Price,MealQuality=meal.Quality,FreshQuality=meal.Freshness});
                orders.Add(order);lookup.Add(order.Id,order);
            }
            var previous=venue.Orders._active;var oldLookup=venue.Orders._activeIdLookup;
            updates.Add((()=>{venue.Orders._active=orders;venue.Orders._activeIdLookup=lookup;},()=>{venue.Orders._active=previous;venue.Orders._activeIdLookup=oldLookup;}));
            return true;
        }
        private static bool OrderLookupConsistent(VenueAreaGhost venue)
        {
            var orders=venue.Orders._active;var lookup=venue.Orders._activeIdLookup;
            if(orders==null||lookup==null||orders.Count!=lookup.Count)return false;
            var ids=new HashSet<string>(StringComparer.Ordinal);
            foreach(var order in orders){
                if(order==null||string.IsNullOrEmpty(order.Id)||!ids.Add(order.Id)||order.Venue==null||order.Venue.Pointer!=venue.Venue.Pointer||
                    !lookup.TryGetValue(order.Id,out var indexed)||indexed==null||indexed.Pointer!=order.Pointer)return false;
            }
            return true;
        }
        private static Il2CppSystem.Collections.Generic.List<ItemType> PrepareTypes(string[] types)
        {
            var items=new Il2CppSystem.Collections.Generic.List<ItemType>();foreach(string id in types)items.Add(ResolveItem(id));return items;
        }
        private static ItemType ResolveItem(string id)
        {
            var type=ScriptableObjectDatabase.Instance.Provide<ItemType>(id);if(type==null||type.Guid!=id)throw new ArgumentException("Unknown craft item: "+id);return type;
        }
        private static Venue ResolveVenue(string id)
        {
            var venue=ScriptableObjectDatabase.Instance.Provide<Venue>(id);if(venue==null||venue.Guid!=id)throw new ArgumentException("Unknown craft venue: "+id);return venue;
        }
        private static void HideRemove(GhostManager manager,Ghost meal)
        {
            meal._IsActive_k__BackingField=false;manager.UpdateGhostViewPresence(meal);manager.DeregisterGhostImmediate(meal);
        }
        private static void SafeRefreshMeal(MealView view)
        {
            if(view==null||view.MyGhost==null)return;
            try{
                var meal=view.MyGhost;view.Transform.SetPositionAndRotation(meal.Position,meal.Rotation);
                bool empty=meal._fillState==MealGhost.FillStates.Empty;
                if(empty){
                    if(view.disabledRenderers==null)view.disabledRenderers=new Il2CppSystem.Collections.Generic.List<Renderer>();
                    if(view.disableWhenPlateEmpty!=null)foreach(var renderer in view.disableWhenPlateEmpty)if(renderer!=null&&renderer.enabled){view.disabledRenderers.Add(renderer);renderer.enabled=false;}
                }
                else if(view.disabledRenderers!=null){foreach(var renderer in view.disabledRenderers)if(renderer!=null)renderer.enabled=true;view.disabledRenderers.Clear();}
                var type=empty?view._emptyPlateType:meal.Type;
                if(view.itemEntity!=null&&type!=null){
                    var shown=view.itemEntity._item;
                    // Check the live presentation instead of caching its identity:
                    // new/reused views and local visual drift still get repaired.
                    if(shown==null||shown._type==null||shown._type.Pointer!=type.Pointer||shown._instanceData==null||shown._instanceData._size!=1||
                        view.itemEntity.data==null||view.itemEntity.data.Pointer!=type.Pointer)
                        view.itemEntity.Item=ItemStack.SafeCreate(type,1,-1);
                }
                // No native UpdateDisplay/LateUpdate: those touch local hands/tasks.
                if(view.serveTaskParent!=null)view.serveTaskParent.SetActive(false);
                if(view.cleanupTaskParent!=null)view.cleanupTaskParent.SetActive(false);
                if(view.canvasGroup!=null){view.canvasGroup.alpha=0;view.canvasGroup.interactable=false;view.canvasGroup.blocksRaycasts=false;}
            }
            catch(Exception ex){Report("Could not refresh remote meal presentation: "+ex.Message);}
        }
        private static void Report(string message){try{Diagnostic?.Invoke(message);}catch{}}
        private static void ReportProfile(BusinessCraftState state,CraftProfile profile)
        {
            long now=Stopwatch.GetTimestamp();if(now<nextProfileReport)return;nextProfileReport=now+5*Stopwatch.Frequency;
            string Ms(long ticks)=>(ticks*1000.0/Stopwatch.Frequency).ToString("F1",System.Globalization.CultureInfo.InvariantCulture);
            Report("CRAFT_PROFILE sequence="+(state?.Sequence??0)+" meals="+profile.MealCount+" processors="+profile.ProcessorCount+" venues="+profile.VenueCount+
                " changedMeals="+profile.ChangedMeals+" changedProcessors="+profile.ChangedProcessors+" changedVenues="+profile.ChangedVenues+
                " totalMs="+Ms(now-profile.Started)+" mainThreadMs="+Ms(profile.Incremental?profile.IncrementalTicks:now-profile.Started)+
                " incremental="+profile.Incremental+
                " backgroundPrepared="+profile.BackgroundPrepared+" preparationMs="+Ms(profile.Preparation)+
                " decodeMs="+Ms(profile.Decode)+" validationMs="+Ms(profile.Validation)+" mealsMs="+Ms(profile.Meals)+
                " processorsMs="+Ms(profile.Processors)+" ordersMs="+Ms(profile.Orders)+" rosterMs="+Ms(profile.Roster)+
                " commitMs="+Ms(profile.Commit)+" presentationMs="+Ms(profile.Presentation)+" failed="+profile.Failed);
        }
        private static GhostManager Ready()
        {
            var manager=GhostManager.HasInstance?GhostManager.Instance:null;if(manager==null||!manager.IsSystemInitialized)throw new InvalidOperationException("Native craft world is not initialized.");return manager;
        }
        public static WorldEvent[] CaptureEvents()=>CaptureEvents(Capture());
        public static WorldEvent[] CaptureEvents(BusinessCraftState state)
        {
            Validate(state);var encoded=BoundedSnapshotCodec.EncodeJson(state,MaxBytes);var bytes=encoded.Bytes;
            string hash=encoded.Hash;LastCaptureHash=hash;var events=new List<WorldEvent>();
            for(int offset=0;offset<bytes.Length;offset+=ChunkBytes){
                var part=new byte[Math.Min(ChunkBytes,bytes.Length-offset)];Buffer.BlockCopy(bytes,offset,part,0,part.Length);
                var evt=new WorldEvent {Kind="business-craft-chunk",Data=JsonSerializer.SerializeToElement(new BusinessCraftChunk {
                    Sequence=state.Sequence,Hash=hash,Encoding=encoded.Encoding,RawLength=encoded.RawLength,
                    Length=bytes.Length,Offset=offset,Bytes=part},DedicatedProtocol.Json)};
                if(CanonicalJson.ByteCount(evt)>48000)throw new InvalidOperationException("Craft event exceeds packet limit.");events.Add(evt);
            }
            return events.ToArray();
        }
        internal sealed class EncodedCraftUpdate
        {
            internal readonly byte[] Bytes;
            internal readonly string Encoding,Hash;
            internal readonly int RawLength,Generation;
            internal readonly long Sequence;
            internal EncodedCraftUpdate(byte[] bytes,string encoding,string hash,int rawLength,int generation,long sequence)
            {Bytes=bytes;Encoding=encoding;Hash=hash;RawLength=rawLength;Generation=generation;Sequence=sequence;}
        }
        private sealed class ClientCraftApply
        {
            private readonly BusinessCraftState state;
            private readonly CraftProfile profile;
            private readonly int generation;
            private readonly Dictionary<string,BusinessProcessorState> availability;
            private readonly HashSet<string> mealIds;
            private readonly CraftApplyCursor cursor=new CraftApplyCursor();
            private GhostManager manager;
            private IntPtr managerPointer;
            private IntPtr registryPointer;
            private readonly Dictionary<string,MealGhost> meals=new(StringComparer.Ordinal);
            private readonly Dictionary<string,HoldableGhost> processors=new(StringComparer.Ordinal);
            private readonly Dictionary<string,HoldableGhost> additions=new(StringComparer.Ordinal);
            private readonly List<KeyValuePair<string,HoldableGhost>> refresh=new();
            private readonly Dictionary<IntPtr,string> identities=new();
            private readonly List<string> removed=new();
            private KeyValuePair<string,IntPtr>[] oldMeals;
            private KeyValuePair<string,HoldableGhost>[] oldProcessors;
            private bool mutated;
            internal ClientCraftApply(BusinessCraftState state,CraftProfile profile,int generation,Dictionary<string,BusinessProcessorState> availability,HashSet<string> mealIds)
            {this.state=state;this.profile=profile;this.generation=generation;this.availability=availability;this.mealIds=mealIds;}
            private void CheckLive()
            {
                if(generation!=preparationGeneration||!clientAuthority||Faulted)
                    throw new InvalidOperationException("Craft application lost its client world.");
                if(manager!=null&&(!GhostManager.HasInstance||GhostManager.Instance.Pointer!=managerPointer||manager._ghostsDictionary?.Pointer!=registryPointer))
                    throw new InvalidOperationException("Native craft manager changed during application.");
            }
            private bool SameWorld()=>generation==preparationGeneration&&clientAuthority&&
                GhostManager.HasInstance&&GhostManager.Instance.Pointer==managerPointer&&manager._ghostsDictionary?.Pointer==registryPointer;
            internal bool Advance(ClientMutationBudget budget)
            {
                CheckLive();return cursor.Advance(()=>budget.TryStart(Stopwatch.GetTimestamp()),Unit);
            }
            internal void Abort()
            {
                cursor.Abort();if(mutated&&SameWorld())Faulted=true;
                meals.Clear();processors.Clear();additions.Clear();refresh.Clear();identities.Clear();removed.Clear();oldMeals=null;oldProcessors=null;
            }
            private bool Unit(int phase,int index)
            {
                CheckLive();long started=Stopwatch.GetTimestamp();IsApplying=true;
                try{
                    switch(phase){
                        case 0:
                            manager=Ready();managerPointer=manager.Pointer;registryPointer=manager._ghostsDictionary.Pointer;EnsureMealIndex(manager);CheckLive();
                            var absentMeals=new List<KeyValuePair<string,IntPtr>>();foreach(var candidate in MealGhosts.Snapshot())if(!mealIds.Contains(candidate.Key))absentMeals.Add(candidate);
                            var absentProcessors=new List<KeyValuePair<string,HoldableGhost>>();foreach(var candidate in CreatedProcessors)if(!availability.ContainsKey(candidate.Key))absentProcessors.Add(candidate);
                            oldMeals=absentMeals.ToArray();oldProcessors=absentProcessors.ToArray();
                            profile.MealCount=state.Meals.Length;profile.ProcessorCount=state.Processors.Length;profile.VenueCount=state.Venues.Length;
                            return true;
                        case 1:if(index>=state.Meals.Length)return true;Meal(state.Meals[index]);return index+1==state.Meals.Length;
                        case 2:if(index>=state.Processors.Length)return true;Processor(state.Processors[index]);return index+1==state.Processors.Length;
                        case 3:if(index>=state.Venues.Length)return true;Orders(state.Venues[index]);return index+1==state.Venues.Length;
                        case 4:
                            if(index<oldMeals.Length)RemoveMeal(oldMeals[index]);
                            else if(index-oldMeals.Length<oldProcessors.Length)RemoveProcessor(oldProcessors[index-oldMeals.Length]);
                            return index+1>=oldMeals.Length+oldProcessors.Length;
                        case 5:
                            if(index>=refresh.Count)return true;
                            var processor=refresh[index].Value;RequireIdentity(refresh[index].Key,processor);
                            manager.UpdateGhostViewPresence(processor);
                            RequireIdentity(refresh[index].Key,processor);
                            var view=processor.HasLoadedView?processor.LoadedView.TryCast<Component>():null;
                            if(view!=null)view.transform.SetPositionAndRotation(processor.Position,processor.Rotation);
                            CheckLive();
                            return index+1==refresh.Count;
                        case 6:
                            if(index>=state.Meals.Length)return true;
                            var meal=meals[state.Meals[index].Id];RequireIdentity(state.Meals[index].Id,meal);
                            manager.UpdateGhostViewPresence(meal);RequireIdentity(state.Meals[index].Id,meal);
                            if(meal.HasLoadedView)SafeRefreshMeal(meal.LoadedView.TryCast<MealView>());CheckLive();
                            return index+1==state.Meals.Length;
                        default:
                            // Presentation/deletion succeeded for the entire roster.
                            // Publish no sequence, UI permission table or hash early.
                            CheckLive();foreach(string id in removed)CreatedProcessors.Remove(id);
                            foreach(var added in additions)CreatedProcessors[added.Key]=added.Value;
                            Availability=availability;applied=state.Sequence;return true;
                    }
                }
                catch{profile.Failed=true;if(SameWorld())Faulted=true;throw;}
                finally{
                    long elapsed=Stopwatch.GetTimestamp()-started;profile.IncrementalTicks+=elapsed;
                    if(phase==1)profile.Meals+=elapsed;else if(phase==2)profile.Processors+=elapsed;else if(phase==3)profile.Orders+=elapsed;
                    else if(phase>=4)profile.Presentation+=elapsed;else profile.Roster+=elapsed;
                    IsApplying=false;
                }
            }
            private void RequireIdentity(string id,Ghost expected)
            {
                CheckLive();var actual=manager.GetGhostById(id);
                if(actual==null||actual.Pointer!=expected.Pointer||actual.Id!=id||!actual.IsValid)
                    throw new InvalidOperationException("Craft entity changed between application units: "+id);
            }
            private void Claim(string id,Ghost ghost)
            {
                if(ghost==null)throw new InvalidOperationException("Craft entity is missing: "+id);
                var pointer=ghost.Pointer;
                if(identities.TryGetValue(pointer,out var prior)&&prior!=id)
                    throw new InvalidOperationException("Craft identities alias the same native entity: "+prior+" / "+id);
                identities[pointer]=id;
            }
            private void CommitUnit(List<Action> registrations,List<Action> rollback,List<(Action Apply,Action Undo)> updates)
            {
                mutated=true;
                // A reset/native-world replacement must never undo into its new
                // world. The failed old client mirror is discarded by the session.
                CommitPrepared(registrations,rollback,updates,SameWorld);CheckLive();
            }
            private void Meal(BusinessMealState row)
            {
                var existing=manager.GetGhostById(row.Id);var meal=existing?.TryCast<MealGhost>();
                if(existing!=null&&meal==null)throw new InvalidOperationException("Meal ID collides with another native ghost.");
                var item=ResolveItem(row.ItemId);var venue=row.VenueId.Length==0?null:ResolveVenue(row.VenueId);
                var registrations=new List<Action>();var rollback=new List<Action>();var updates=new List<(Action Apply,Action Undo)>();
                if(meal==null){
                    var prefabs=SerializationManager.Instance?.Prefabs?.prefabDictionary;
                    if(prefabs==null||!prefabs.ContainsKey(row.PrefabId))throw new InvalidOperationException("Meal prefab is missing: "+row.PrefabId);
                    meal=new MealGhost(row.Id,row.PrefabId);SetMealRaw(meal,row,item,venue,false);
                    var created=meal;registrations.Add(()=>RegisterPrepared(manager,created));rollback.Add(()=>RemoveIfRegistered(manager,created));
                }else{
                    var before=ObserveMeal(meal);var target=meal;
                    if(before.State.PrefabId!=row.PrefabId)throw new InvalidOperationException("Meal prefab changed; reload the server world.");
                    if(!meal.IsValid||!SameMeal(row,before.State)||before.Item.Pointer!=item.Pointer||(before.Venue?.Pointer??IntPtr.Zero)!=(venue?.Pointer??IntPtr.Zero))
                        updates.Add((()=>SetMealRaw(target,row,item,venue,true),()=>SetMealRaw(target,before.State,before.Item,before.Venue,true)));
                }
                Claim(row.Id,meal);
                if(registrations.Count!=0||updates.Count!=0){
                    var target=meal;updates.Add((()=>{
                        RequireIdentity(row.Id,target);var observed=ObserveMeal(target);
                        if(!SameMeal(row,observed.State,true)||observed.Item.Pointer!=item.Pointer||(observed.Venue?.Pointer??IntPtr.Zero)!=(venue?.Pointer??IntPtr.Zero))
                            throw new InvalidOperationException("Native meal did not retain its authoritative state: "+row.Id);
                    },()=>{}));
                    CommitUnit(registrations,rollback,updates);profile.ChangedMeals++;
                }
                meals.Add(row.Id,meal);
            }
            private void Processor(BusinessProcessorState row)
            {
                // Meal wrappers were registered in earlier complete units. Resolve
                // every referenced identity again immediately before constructing links.
                foreach(string id in row.Meals)RequireIdentity(id,meals[id]);
                var created=new List<HoldableGhost>();var changedViews=new List<HoldableGhost>();var updates=new List<(Action Apply,Action Undo)>();
                bool changed=StageProcessor(manager,row,meals,processors,created,changedViews,updates,state.Sequence);
                Claim(row.Id,processors[row.Id]);if(!changed)return;
                var target=processors[row.Id];var registrations=new List<Action>();var rollback=new List<Action>();
                foreach(var ghost in created){var value=ghost;registrations.Add(()=>RegisterPrepared(manager,value));rollback.Add(()=>RemoveIfRegistered(manager,value));}
                updates.Add((()=>{
                    RequireIdentity(row.Id,target);
                    var food=row.Ingredient?null:target.TryCast<FoodProcessorGhost>();var ingredient=row.Ingredient?target.TryCast<IngredientProcessorGhost>():null;
                    if(!SameProcessor(row,ObserveProcessor(target,food,ingredient,false).State,true))
                        throw new InvalidOperationException("Native processor did not retain its authoritative state: "+row.Id);
                },()=>{}));
                CommitUnit(registrations,rollback,updates);foreach(var added in created)additions.Add(row.Id,added);
                foreach(var changedView in changedViews)refresh.Add(new KeyValuePair<string,HoldableGhost>(row.Id,changedView));profile.ChangedProcessors++;
            }
            private void Orders(BusinessVenueOrders row)
            {
                var target=manager.GetGhostById(row.GhostId)?.TryCast<VenueAreaGhost>();var updates=new List<(Action Apply,Action Undo)>();
                Claim(row.GhostId,target);
                if(!StageOrders(manager,row,updates))return;
                updates.Add((()=>{
                    RequireIdentity(row.GhostId,target);
                    if(!OrderLookupConsistent(target))throw new InvalidOperationException("Native venue orders did not retain their authoritative state: "+row.GhostId+" field=active-list/index/venue-reference sequence="+state.Sequence);
                    var observed=CaptureOrders(target);
                    if(!SameOrders(row,observed))throw new InvalidOperationException("Native venue orders did not retain their authoritative state: "+row.GhostId+" field="+DescribeOrdersMismatch(row,observed)+" sequence="+state.Sequence);
                },()=>{}));
                CommitUnit(new List<Action>(),new List<Action>(),updates);profile.ChangedVenues++;
            }
            private void RemoveMeal(KeyValuePair<string,IntPtr> candidate)
            {
                if(meals.ContainsKey(candidate.Key))return;
                var meal=manager.GetGhostById(candidate.Key)?.TryCast<MealGhost>();
                if(meal==null||meal.Pointer!=candidate.Value){MealGhosts.Forget(managerPointer,candidate.Key,candidate.Value);return;}
                if(!meal.IsValid)return;mutated=true;RemoveCurrent(meal,candidate.Key);
                if(manager.GetGhostById(candidate.Key)?.Pointer==candidate.Value)throw new InvalidOperationException("Obsolete meal remained registered.");
            }
            private void RemoveProcessor(KeyValuePair<string,HoldableGhost> candidate)
            {
                if(processors.ContainsKey(candidate.Key))return;
                var actual=manager.GetGhostById(candidate.Key);
                if(actual!=null){
                    if(actual.Pointer!=candidate.Value.Pointer||actual.IsBakedInstance)throw new InvalidOperationException("Created processor changed outside the craft mirror: "+candidate.Key);
                    mutated=true;RemoveCurrent(actual,candidate.Key);
                    if(manager.GetGhostById(candidate.Key)?.Pointer==actual.Pointer)throw new InvalidOperationException("Obsolete processor remained registered.");
                }
                removed.Add(candidate.Key);
            }
            private void RemoveCurrent(Ghost ghost,string id)
            {
                RequireIdentity(id,ghost);ghost._IsActive_k__BackingField=false;
                manager.UpdateGhostViewPresence(ghost);RequireIdentity(id,ghost);
                manager.DeregisterGhostImmediate(ghost);CheckLive();
            }
        }
        internal sealed class PreparedCraftUpdate:IIncrementalPreparedWorldUpdate
        {
            private readonly BusinessCraftState state;
            private readonly string hash;
            private readonly int generation;
            private readonly CraftProfile profile;
            private readonly Dictionary<string,BusinessProcessorState> availability=new(StringComparer.Ordinal);
            private readonly HashSet<string> mealIds=new(StringComparer.Ordinal);
            private ClientCraftApply incremental;
            private bool consumed;
            internal PreparedCraftUpdate(BusinessCraftState state,string hash,int generation,CraftProfile profile)
            {this.state=state;this.hash=hash;this.generation=generation;this.profile=profile;foreach(var row in state.Processors)availability.Add(row.Id,row);foreach(var row in state.Meals)mealIds.Add(row.Id);}
            public void Commit()
            {
                if(consumed||incremental!=null||generation!=preparationGeneration||!clientAuthority||state.Sequence<=applied)
                    throw new InvalidOperationException("Discarded craft preparation cannot be applied.");
                consumed=true;
                profile.Started=Stopwatch.GetTimestamp();
                ApplyProfiled(state,profile,true);
                if(generation!=preparationGeneration||!clientAuthority)
                    throw new InvalidOperationException("Craft authority changed during native commit.");
                LastCaptureHash=hash;
            }
            public bool Advance(ClientMutationBudget budget)
            {
                if(consumed||generation!=preparationGeneration||!clientAuthority||state.Sequence<=applied)
                    throw new InvalidOperationException("Discarded craft preparation cannot be advanced.");
                if(budget==null)throw new ArgumentNullException(nameof(budget));
                if(incremental==null){profile.Started=Stopwatch.GetTimestamp();profile.Incremental=true;incremental=new ClientCraftApply(state,profile,generation,availability,mealIds);}
                try{
                    if(!incremental.Advance(budget))return false;
                    if(generation!=preparationGeneration||!clientAuthority)throw new InvalidOperationException("Craft authority changed during native commit.");
                    consumed=true;LastCaptureHash=hash;ReportProfile(state,profile);return true;
                }catch{Abort();throw;}
            }
            public void Abort()
            {
                if(consumed)return;consumed=true;incremental?.Abort();profile.Failed=true;
                if(incremental!=null)ReportProfile(state,profile);
            }
        }
        // Compatibility callers remain synchronous. The session owns the one
        // ordered preparation fence and final-chunk ACK for its detached path.
        public static void Apply(WorldEvent evt)
        {
            var encoded=AcceptChunk(evt);
            if(encoded!=null)PrepareManaged(encoded,CancellationToken.None,false).Commit();
        }
        // Main-thread assembler: transfer ownership of its complete encoded
        // buffer, so background work never reads mutable assembler fields.
        internal static EncodedCraftUpdate AcceptChunk(WorldEvent evt)
        {
            if(!clientAuthority||evt==null||evt.Kind!="business-craft-chunk")return null;
            var part=evt.Data.Deserialize<BusinessCraftChunk>(DedicatedProtocol.Json);
            if(part==null||part.Sequence<=0||part.Length<=0||part.Length>MaxBytes||part.Bytes==null||part.Bytes.Length==0||part.Bytes.Length>ChunkBytes||
               part.Offset<0||part.Offset>part.Length-part.Bytes.Length||part.Hash==null||part.Hash.Length!=64)throw new ArgumentException("Invalid craft chunk.");
            int rawLength=part.RawLength==0&&part.Encoding==BoundedSnapshotCodec.JsonEncoding?part.Length:part.RawLength;
            BoundedSnapshotCodec.ValidateHeader(part.Encoding,rawLength,part.Length,MaxBytes);
            if(part.Sequence<=applied)return null;
            // The relay can admit a late join between chunks of an older batch.
            // Ignore that tail until a header arrives; never skip within a batch.
            if(incoming==null&&part.Offset!=0)return null;
            if(part.Offset==0){incoming=new byte[part.Length];incomingOffset=0;incomingSequence=part.Sequence;incomingHash=part.Hash;
                incomingEncoding=part.Encoding;incomingRawLength=rawLength;incomingStarted=Time.realtimeSinceStartup;}
            if(incoming==null||incomingSequence!=part.Sequence||incomingHash!=part.Hash||incoming.Length!=part.Length||incomingEncoding!=part.Encoding||
                incomingRawLength!=rawLength||incomingOffset!=part.Offset||Time.realtimeSinceStartup-incomingStarted>30){incoming=null;throw new InvalidDataException("Incomplete craft snapshot; reconnect to refresh.");}
            Buffer.BlockCopy(part.Bytes,0,incoming,incomingOffset,part.Bytes.Length);incomingOffset+=part.Bytes.Length;if(incomingOffset!=incoming.Length)return null;
            var bytes=incoming;incoming=null;
            return new EncodedCraftUpdate(bytes,incomingEncoding,incomingHash,incomingRawLength,preparationGeneration,incomingSequence);
        }
        // DTO-only work: checksum/decompression/JSON and bounded pure validation.
        // Native identity, prefab, registry and local drift checks stay in Commit.
        internal static PreparedCraftUpdate PrepareManaged(EncodedCraftUpdate input,CancellationToken cancellation,bool background=true)
        {
            cancellation.ThrowIfCancellationRequested();
            if(input==null||input.Bytes==null||input.Bytes.Length<1||input.Bytes.Length>MaxBytes||input.Sequence<=0)
                throw new InvalidDataException("Invalid craft preparation.");
            var profile=new CraftProfile{BackgroundPrepared=background};
            long started=Stopwatch.GetTimestamp(),stage=started;
            var state=BoundedSnapshotCodec.DecodeJson<BusinessCraftState>(input.Encoding,input.RawLength,input.Hash,input.Bytes,MaxBytes);
            profile.Decode=Stopwatch.GetTimestamp()-stage;
            cancellation.ThrowIfCancellationRequested();
            if(state==null||state.Sequence!=input.Sequence)throw new InvalidDataException("Craft snapshot revision mismatch.");
            stage=Stopwatch.GetTimestamp();Validate(state);profile.Validation=Stopwatch.GetTimestamp()-stage;
            cancellation.ThrowIfCancellationRequested();
            profile.Preparation=Stopwatch.GetTimestamp()-started;
            return new PreparedCraftUpdate(state,input.Hash,input.Generation,profile);
        }
    }
}
