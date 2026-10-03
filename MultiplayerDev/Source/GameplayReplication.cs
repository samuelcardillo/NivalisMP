using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using Nivalis;
using Nivalis.GhostSystem;
using Nivalis.Locale.TrashSystem;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace NivalisTogether.Dedicated
{
    public sealed class SharedDoorState
    {
        public string TargetId { get; set; } = "";
        public string Scene { get; set; } = "";
        public bool Open { get; set; }
    }

    public sealed class GameplayState
    {
        public long Sequence { get; set; }
        public string Scene { get; set; } = "";
        public string PlayerId { get; set; } = "";
        public string RequestId { get; set; } = "";
        public bool Success { get; set; } = true;
        public string Message { get; set; } = "";
        public SharedDoorState[] Doors { get; set; } = Array.Empty<SharedDoorState>();
        public string[] RemovedTrash { get; set; } = Array.Empty<string>();
        public string[] CollectedItems { get; set; } = Array.Empty<string>();
    }

    internal static class GameplayStatePolicy
    {
        // Busy/confirmation notices originate outside the world capture counter.
        // They carry no world mutation, so Sequence=0 and an empty Scene are valid.
        // Replicated state and mutating results still require an identified map
        // and positive sequence; relaxing notices must not weaken those frames.
        public static bool ValidateNoticeOnly(string kind, GameplayState state)
        {
            if ((kind != "gameplay-state" && kind != "interaction-result") || state == null ||
                !DedicatedProtocol.Text(state.Scene, 128, true) || state.Doors == null || state.RemovedTrash == null || state.CollectedItems == null ||
                state.Doors.Length > 2048 || state.RemovedTrash.Length > 8192 || state.CollectedItems.Length > 8192)
                throw new ArgumentException("Invalid gameplay state.");
            bool noticeOnly = kind == "interaction-result" && state.Doors.Length == 0 && state.RemovedTrash.Length == 0 && state.CollectedItems.Length == 0;
            if (state.Sequence < 0 || (!noticeOnly && (state.Sequence <= 0 || !DedicatedProtocol.Text(state.Scene, 128))))
                throw new ArgumentException("Invalid gameplay state.");
            foreach (var door in state.Doors) SceneDoorStateCache.Validate(door);
            foreach (string id in state.RemovedTrash) if (!DedicatedProtocol.Text(id, 128)) throw new ArgumentException("Invalid trash state.");
            foreach (string id in state.CollectedItems) if (!DedicatedProtocol.Text(id, 128)) throw new ArgumentException("Invalid collectible state.");
            return noticeOnly;
        }
    }

    /// <summary>Sparse scene-qualified state. A frame from another map never replaces a map's cached doors.</summary>
    internal sealed class SceneDoorStateCache
    {
        private readonly Dictionary<string, SharedDoorState> states = new Dictionary<string, SharedDoorState>();
        private readonly Dictionary<string, long> sequences = new Dictionary<string, long>();
        public IEnumerable<SharedDoorState> Values => states.Values;
        public void Clear() { states.Clear(); sequences.Clear(); }
        public static void Validate(SharedDoorState state)
        {
            if (state == null || !DedicatedProtocol.Text(state.TargetId, 128) || !DedicatedProtocol.Text(state.Scene, 128))
                throw new ArgumentException("Invalid scene-qualified door state.");
        }
        public void Observe(long sequence, SharedDoorState state)
        {
            Validate(state);
            if (sequence <= 0) throw new ArgumentException("Invalid door sequence.");
            if (states.TryGetValue(state.TargetId, out var prior) && prior.Scene != state.Scene)
                throw new ArgumentException("Door identity changed districts.");
            if (sequences.TryGetValue(state.TargetId, out long previous) && previous > sequence) return;
            if (!states.ContainsKey(state.TargetId) && states.Count >= 32768) throw new ArgumentException("Door cache limit exceeded.");
            states[state.TargetId] = state; sequences[state.TargetId] = sequence;
        }
        public bool TryGet(string targetId, string scene, out SharedDoorState state)
        {
            if (states.TryGetValue(targetId, out state) && state.Scene == scene) return true;
            state = null; return false;
        }
    }

    /// <summary>
    /// Main-thread, worker-authoritative doors, trash, pickups and shop routes. All
    /// other world interaction routes are blocked on clients until implemented.
    /// Character movement, district travel and seating remain local. Native
    /// snapshot import must finish before enabling this module on a client.
    /// </summary>
    public static class GameplayReplication
    {
        private static readonly Harmony Harmony = new Harmony("local.nivalis.together.gameplay-authority");
        private static readonly Dictionary<string, Component> Targets = new Dictionary<string, Component>();
        private static readonly Dictionary<string, TimedPlayerPose> Players = new Dictionary<string, TimedPlayerPose>();
        private static readonly Dictionary<string, WorldEvent> CompletedRequests = new Dictionary<string, WorldEvent>();
        private static readonly Queue<string> RequestOrder = new Queue<string>();
        private static readonly SceneDoorStateCache PendingDoors = new SceneDoorStateCache();
        private static readonly SceneDoorStateCache WorkerDoors = new SceneDoorStateCache();
        private static readonly Dictionary<string, int> WorkerDoorInstances = new Dictionary<string, int>();
        private static readonly HashSet<string> RemovedTrash = new HashSet<string>();
        private static readonly HashSet<string> CollectedItems = new HashSet<string>();
        private static bool patched, clientAuthority;
        private static long sequence;
        private static float nextScan, nextNotice, nextIntent;
        public static Action<InteractionRequest> OutboundInteraction { get; set; }
        public static Action<WorldEvent> OutboundWorldEvent { get; set; }
        public static Action<string> Notice { get; set; }
        /// <summary>Return null when unhandled, true for safe local UI, or false after handling an intent.</summary>
        public static Func<IInteractable, bool?> ClientInteractionExtension { get; set; }
        public static string LocalPlayerId { get; set; } = "";
        public static bool ClientAuthority => clientAuthority;
        private sealed class TimedPlayerPose { internal PlayerPose Pose; internal float Time; }

        public static void SetClientAuthority(bool enabled)
        {
            if (!patched) InstallPatches();
            if(enabled) ReplicatedWorldTargets.Initialize();
            clientAuthority = enabled;
            if (!enabled) { PendingDoors.Clear(); RemovedTrash.Clear(); CollectedItems.Clear(); }
            nextScan = 0;
        }

        /// <summary>Call when changing worlds or disconnecting, after disabling client authority.</summary>
        public static void Reset()
        {
            Players.Clear(); CompletedRequests.Clear(); RequestOrder.Clear(); Targets.Clear();
            PendingDoors.Clear(); WorkerDoors.Clear(); WorkerDoorInstances.Clear(); RemovedTrash.Clear(); CollectedItems.Clear();
            nextScan = nextIntent = nextNotice = 0; sequence = 0;
            SharedEconomyReplication.Reset();
        }

        private static void InstallPatches()
        {
            Harmony.Patch(DeclaredMethod(typeof(PlayerInteraction), "OnInteraction"), prefix: Method(nameof(PlayerInteractionPrefix)));
            Harmony.Patch(DeclaredMethod(typeof(Trash), "Cleanup"), postfix: Method(nameof(RecordTrashCleanup)));
            // Native IInteractable calls can come through UnityEvents or UI as
            // well as PlayerInteraction. Guard concrete implementations too.
            foreach (var type in typeof(PlayerManager).Assembly.GetTypes())
            {
                // IL2CPP interfaces are generated as managed wrapper classes;
                // their virtual declarations do not have patchable native bodies.
                if (type.IsAbstract || !typeof(MonoBehaviour).IsAssignableFrom(type)) continue;
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (!method.IsAbstract && (method.Name == "DoInteraction" || method.Name.EndsWith("_DoInteraction", StringComparison.Ordinal)) && method.GetParameters().Length == 0 && method.ReturnType == typeof(void))
                        Harmony.Patch(method, prefix: Method(nameof(DirectInteractionPrefix)));
                }
            }
            // Shop UI contains inlined vendor purchase logic. Guard the actual
            // buttons instead of assuming a patch on Vendor.BuyItem catches it.
            Harmony.Patch(DeclaredMethod(typeof(Nivalis.Locale.UI.ShopItemDetailsDisplay), "Buy"), prefix: Method(nameof(ShopBuyPrefix)));
            Harmony.Patch(DeclaredMethod(typeof(Nivalis.Locale.UI.ShopItemDetailsDisplay), "Sell"), prefix: Method(nameof(ShopSellPrefix)));
            // Do not detour Vendor.BuyItem/SellItem: their byref IL2CPP value
            // arguments (ShopTradeRequest/BasicTemp) are corrupted by the native
            // to managed Harmony trampoline even when a prefix returns true.
            // Player trades are routed at these UI buttons; client NPC simulation
            // is stopped separately. Worker AI must call the original native ABI.
            PatchNamed("Nivalis.Locale.UI.InventoryItemDetailsDisplay", "OnHoldButtonClicked", "OnPlacementButtonClicked", "TransferModeClicked", "OnTransferButtonClicked");
            PatchNamed("Nivalis.Locale.UI.InventoryItemDetailsVenueTransferUi", "IncreaseItemAmount", "DecreaseItemAmount", "Transfer");
            PatchNamed("Nivalis.PlayerObjectHolder", "TryPickupObjectInFront");
            PatchNamed("Nivalis.PropertyManager", "AddOwnedProperty", "RemoveOwnedProperty", "StartRenting", "StopRenting");
            var authorityOnly = Method(nameof(AuthorityOnly));
            foreach (string name in new[] { "Update", "StepSimulation", "UpdateDay" })
                Harmony.Patch(DeclaredMethod(typeof(Nivalis.Economy.EconomyManager), name), prefix: authorityOnly);
            Harmony.Patch(DeclaredMethod(typeof(Nivalis.InventorySystem.InventoriesManager), "DayChangeListener"), prefix: authorityOnly);
            Harmony.Patch(DeclaredMethod(typeof(Nivalis.InventorySystem.ItemContainer), "UpdateItemDecay"), prefix: authorityOnly);
            Harmony.Patch(DeclaredMethod(typeof(Nivalis.InventorySystem.ItemStack), "UpdateItemDecay"), prefix: authorityOnly);
            Harmony.Patch(DeclaredMethod(typeof(Nivalis.InventorySystem.ItemInstanceData), "UpdateDecay"), prefix: authorityOnly);
            foreach (string name in new[] { "OnHourTick", "OnDayUpdate", "OnCurfewStart", "OnCurfewEnd" })
                Harmony.Patch(DeclaredMethod(typeof(Nivalis.GhostSystem.CustomerLoop.VenueManager), name), prefix: authorityOnly);
            foreach (string name in new[] { "OnHourUpdate", "OnDayUpdate", "OnCurfewStart", "OnCurfewEnd", "PayStaff" })
                Harmony.Patch(DeclaredMethod(typeof(Nivalis.GhostSystem.CustomerLoop.VenueAreaGhost), name), prefix: authorityOnly);
            foreach (string name in new[] { "CollectReceipts", "CollectReceipt" })
                Harmony.Patch(DeclaredMethod(typeof(RentManager), name), prefix: authorityOnly);
            foreach (string name in new[] { "OnDayUpdate", "OnCurfewStart" })
                Harmony.Patch(DeclaredMethod(typeof(PlayerManager.Player), name), prefix: authorityOnly);
            foreach (string name in new[] { "Update", "ProgressUpdate", "StartQuest", "ForceCompleteQuest", "ForceCompleteQuestObjectives", "FailQuest", "CompleteQuestImmediate" })
                Harmony.Patch(DeclaredMethod(typeof(QuestManager), name), prefix: authorityOnly);
            patched = true;
        }
        private static HarmonyMethod Method(string name) => new HarmonyMethod(typeof(GameplayReplication).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static));
        private static MethodInfo DeclaredMethod(Type type, string name)
        {
            MethodInfo found = null;
            foreach (var method in type.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                if (method.Name == name) { if (found != null) throw new AmbiguousMatchException(type.FullName + "." + name); found = method; }
            return found ?? throw new MissingMethodException(type.FullName, name);
        }
        private static void PatchNamed(string typeName, params string[] names)
        {
            var type = typeof(PlayerManager).Assembly.GetType(typeName);
            if (type == null) throw new MissingMemberException(typeName);
            foreach (string name in names)
            {
                bool found = false;
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    if (method.Name == name) { Harmony.Patch(method, prefix: Method(nameof(BlockUnsupportedMutation))); found = true; }
                if (!found) throw new MissingMethodException(typeName, name);
            }
        }
        private static bool AuthorityOnly() => !clientAuthority;
        private static void RecordTrashCleanup(Trash __instance)
        {
            if (!clientAuthority && __instance != null && !string.IsNullOrEmpty(__instance.Id)) RemovedTrash.Add(__instance.Id);
        }
        private static bool BlockUnsupportedMutation()
        {
            if (!clientAuthority || SharedEconomyReplication.IsApplying) return true;
            ShowNotice("This transaction is not supported by the shared world yet."); return false;
        }

        private static bool ShopBuyPrefix(Nivalis.Locale.UI.ShopItemDetailsDisplay __instance, int __0) => ShopIntent(__instance, __0, false);
        private static bool ShopSellPrefix(Nivalis.Locale.UI.ShopItemDetailsDisplay __instance, int __0) => ShopIntent(__instance, __0, true);
        private static bool ShopIntent(Nivalis.Locale.UI.ShopItemDetailsDisplay ui, int amount, bool selling)
        {
            if (!clientAuthority) return true;
            try { SendIntent(SharedEconomyReplication.BuildRequest(ui, amount, selling)); }
            catch (Exception ex) { ShowNotice(ex.Message); }
            return false;
        }
        private static void SendIntent(InteractionRequest request)
        {
            if (Time.realtimeSinceStartup < nextIntent) return;
            if (OutboundInteraction == null) { ShowNotice("The shared world connection is not ready."); return; }
            nextIntent = Time.realtimeSinceStartup + 0.2f;
            OutboundInteraction(request);
        }

        private static bool PlayerInteractionPrefix(PlayerInteraction __instance)
        {
            if (!clientAuthority) return true;
            if (__instance == null || !__instance.InteractionActive || CursorModeManager.IsCursorActive) return false;
            return RouteClientInteraction(__instance.CurrentFocus);
        }
        private static bool DirectInteractionPrefix(Il2CppObjectBase __instance)
        {
            if (!clientAuthority) return true;
            return RouteClientInteraction(__instance == null ? null : __instance.TryCast<IInteractable>());
        }
        private static bool RouteClientInteraction(IInteractable interactable)
        {
            try
            {
                var focus = Resolve(interactable);
                if (focus == null) return false;
                bool? extended = ClientInteractionExtension?.Invoke(focus);
                if (extended.HasValue) return extended.Value;
                // Travel/seating changes only this client's controlled character.
                if (focus.TryCast<SceneTransitionInteractableBase>() != null || focus.TryCast<SeatInteraction>() != null || focus.TryCast<VendorInteraction>() != null) return true;
                if (!focus.IsActive || !focus.CanInteract()) return false;
                string kind, target;
                var door = focus.TryCast<DoorInteraction>();
                var hinge = focus.TryCast<HingeInteraction>();
                var trash = focus.TryCast<TrashView>();
                var collectible = focus.TryCast<ItemCollectible>();
                if (door != null) { kind = "door-toggle"; target = GetTargetId(door); }
                else if (hinge != null) { kind = "hinge-toggle"; target = GetTargetId(hinge); }
                else if (trash != null && trash.MyGhost != null) { kind = "trash-clean"; target = trash.MyGhost.Id; }
                else if (collectible != null) { kind = "item-collect"; target = collectible.GUID; }
                else { ShowNotice("This interaction is not supported by the shared world yet."); return false; }
                SendIntent(new InteractionRequest { RequestId = Guid.NewGuid().ToString("N"), Kind = kind, TargetId = target, Data = JsonSerializer.SerializeToElement(new { }, DedicatedProtocol.Json) });
                return false;
            }
            catch (Exception ex) { ShowNotice("Shared interaction failed: " + ex.Message); return false; }
        }
        private static IInteractable Resolve(IInteractable focus)
        {
            for (int i = 0; focus != null && i < 8; i++)
            {
                var wrapper = focus.TryCast<InteractionDelegate>();
                if (wrapper == null) return focus;
                focus = wrapper.FirstActiveInteractable;
            }
            return null;
        }
        private static void ShowNotice(string message)
        {
            if (Time.realtimeSinceStartup < nextNotice) return;
            nextNotice = Time.realtimeSinceStartup + 1;
            Notice?.Invoke(message);
            try { if (clientAuthority && NotificationManager.InstanceNotNull()) NotificationManager.Instance.CreateMessage("Multiplayer", message, null); }
            catch { /* The native HUD can be absent during district transitions. */ }
        }

        /// <summary>Feed only server-authenticated, server-accepted player poses.</summary>
        public static void UpdatePlayerPose(PlayerPose pose)
        {
            if (pose == null || !DedicatedProtocol.PoseValid(pose) || string.IsNullOrEmpty(pose.PlayerId)) return;
            Players[pose.PlayerId] = new TimedPlayerPose { Pose = pose, Time = Time.realtimeSinceStartup };
        }
        public static void RemovePlayer(string id) { if (id != null) Players.Remove(id); }

        /// <summary>Worker-only. Broadcast the returned event to loaded clients.</summary>
        public static WorldEvent HandleInteraction(InteractionRequest request)
        {
            if (!DedicatedWorldRuntime.IsDedicatedServer || clientAuthority) throw new InvalidOperationException("Interactions must execute on the dedicated worker.");
            if (!patched) InstallPatches();
            if (request == null || !DedicatedProtocol.Text(request.PlayerId, 128) || !DedicatedProtocol.Text(request.RequestId, 64) || !DedicatedProtocol.Text(request.TargetId, 128))
                throw new ArgumentException("Invalid interaction request.");
            string key = request.PlayerId + ":" + request.RequestId;
            if (CompletedRequests.TryGetValue(key, out var completed)) return completed;
            var state = new GameplayState { Sequence = ++sequence, Scene = DedicatedWorldRuntime.Scene, PlayerId = request.PlayerId, RequestId = request.RequestId };
            SharedEconomyState economy = null;
            try
            {
                if (!DedicatedWorldRuntime.IsWorldReady) throw new InvalidOperationException("The shared world is loading.");
                if (!Players.TryGetValue(request.PlayerId, out var player) || player.Pose.AccountId != request.AccountId || !player.Pose.Active || Time.realtimeSinceStartup - player.Time > 3)
                    throw new InvalidOperationException("Your character position is not available to the worker.");
                if (request.Kind == "shop-buy" || request.Kind == "shop-sell")
                {
                    SharedEconomyReplication.RequireNearby = (actor, scene, position) =>
                    {
                        if (!Players.TryGetValue(actor.PlayerId, out var current) || !current.Pose.Active || Time.realtimeSinceStartup - current.Time > 3)
                            throw new InvalidOperationException("Your character position is not available to the worker.");
                        RequireNearby(current.Pose, scene, position);
                    };
                    economy = SharedEconomyReplication.Handle(request);
                    state.Message = request.Kind == "shop-buy" ? "Purchased for your inventory." : "Sold from your inventory.";
                }
                else if (request.Kind == "trash-clean")
                {
                    var manager = GhostManager.HasInstance ? GhostManager.Instance : null;
                    var baseGhost = manager == null ? null : manager.GetGhostById(request.TargetId);
                    var trash = baseGhost == null ? null : baseGhost.TryCast<Trash>();
                    if (trash == null || !trash.IsValid || !trash.IsActive) throw new InvalidOperationException("That trash has already been cleaned.");
                    RequireNearby(player.Pose, trash.SceneIndex, trash.Position);
                    trash.Cleanup(); manager.FlushGhostOperationQueues();
                    RemovedTrash.Add(request.TargetId); state.RemovedTrash = new[] { request.TargetId };
                    state.Message = "Trash cleaned in the shared world.";
                }
                else if (request.Kind == "item-collect")
                {
                    ItemCollectible found = null;
                    foreach (var item in ReplicatedWorldTargets.Snapshot<ItemCollectible>()) if (Loaded(item) && item.GUID == request.TargetId) { found = item; break; }
                    if (found == null) throw new InvalidOperationException("This collectible's district is not loaded on the worker.");
                    PropertyOwnershipReplication.RequireTargetInteraction(request.AccountId, found);
                    RequireNearby(player.Pose, found.gameObject.scene.buildIndex, found.transform.position);
                    if (!found.IsActive || found.IsCollected || !found.CanInteract()) throw new InvalidOperationException("That item cannot be collected now.");
                    found.DoInteraction();
                    if (!found.IsCollected) throw new InvalidOperationException("Your inventory could not receive this item.");
                    CollectedItems.Add(found.GUID); state.CollectedItems = new[] { found.GUID };
                    state.Message = "Item added to your inventory.";
                    // Capture after the native pickup commits. Failure to send
                    // its refresh must not make the same pickup run twice.
                    try { economy = SharedEconomyReplication.Capture(false); }
                    catch (Exception ex) { Notice?.Invoke("Shared pickup committed; inventory refresh failed: " + ex.Message); }
                }
                else if (request.Kind == "door-toggle" || request.Kind == "hinge-toggle")
                {
                    RefreshTargets();
                    if (!Targets.TryGetValue(request.TargetId, out var target) || target == null)
                        throw new InvalidOperationException("This door's district is not loaded on the worker.");
                    RequireNearby(player.Pose, target.gameObject.scene.buildIndex, target.transform.position);
                    PropertyOwnershipReplication.RequireTargetInteraction(request.AccountId, target);
                    bool open;
                    if (request.Kind == "door-toggle")
                    {
                        var door = target.TryCast<DoorInteraction>();
                        if (door == null || !door.IsActive || !door.CanInteract()) throw new InvalidOperationException("This door cannot be opened now.");
                        open = !door.Open; door.ForceState(open);
                    }
                    else
                    {
                        var hinge = target.TryCast<HingeInteraction>();
                        if (hinge == null || !hinge.IsActive || !hinge.CanInteract()) throw new InvalidOperationException("This door cannot be opened now.");
                        open = !hinge.Open; hinge.ForceState(open);
                    }
                    var committedDoor = new SharedDoorState { TargetId = request.TargetId, Scene = target.gameObject.scene.name, Open = open };
                    WorkerDoors.Observe(state.Sequence, committedDoor);
                    state.Doors = new[] { committedDoor };
                }
                else throw new InvalidOperationException("The worker does not support this interaction yet.");
            }
            catch (Exception ex) { state.Success = false; state.Message = ex.Message; }
            completed = new WorldEvent { Kind = "interaction-result", Data = JsonSerializer.SerializeToElement(state, DedicatedProtocol.Json) };
            CompletedRequests[key] = completed; RequestOrder.Enqueue(key);
            while (RequestOrder.Count > 4096) CompletedRequests.Remove(RequestOrder.Dequeue());
            // Persist deduplication before transport callbacks. A dropped socket
            // is repaired by the next periodic capture, never by replaying a trade.
            if (economy != null)
                try { foreach (var evt in SharedEconomyReplication.CaptureEvents(economy)) OutboundWorldEvent?.Invoke(evt); }
                catch (Exception ex) { Notice?.Invoke("Shared transaction committed; inventory refresh failed: " + ex.Message); }
            return completed;
        }
        private static void RequireNearby(PlayerPose pose, int buildIndex, Vector3 position)
        {
            string scene = Path.GetFileNameWithoutExtension(SceneUtility.GetScenePathByBuildIndex(buildIndex));
            if (buildIndex < 0 || !string.Equals(scene, pose.Scene, StringComparison.Ordinal)) throw new InvalidOperationException("Your character is in a different district.");
            var offset = new Vector3(pose.X, pose.Y, pose.Z) - position;
            if (offset.sqrMagnitude > 64) throw new InvalidOperationException("Move closer to this object.");
        }

        /// <summary>Worker state for live updates or a joining client's refresh.
        /// Chunk Doors/RemovedTrash if its encoded WorldEvent would exceed 48KB.</summary>
        public static GameplayState Capture()
        {
            if (!DedicatedWorldRuntime.IsWorldReady) return null;
            if (!patched) InstallPatches();
            RefreshTargets();
            long captureSequence = ++sequence;
            foreach (var item in Targets)
            {
                if (item.Value == null) continue;
                var door = item.Value.TryCast<DoorInteraction>(); var hinge = item.Value.TryCast<HingeInteraction>();
                if (door != null) WorkerDoors.Observe(captureSequence, new SharedDoorState { TargetId = item.Key, Scene = door.gameObject.scene.name, Open = door.Open });
                else if (hinge != null) WorkerDoors.Observe(captureSequence, new SharedDoorState { TargetId = item.Key, Scene = hinge.gameObject.scene.name, Open = hinge.Open });
            }
            var states = new List<SharedDoorState>(WorkerDoors.Values);
            var removed = new string[RemovedTrash.Count]; RemovedTrash.CopyTo(removed);
            var collected = new string[CollectedItems.Count]; CollectedItems.CopyTo(collected);
            return new GameplayState { Sequence = captureSequence, Scene = DedicatedWorldRuntime.Scene, Doors = states.ToArray(), RemovedTrash = removed, CollectedItems = collected };
        }

        public static WorldEvent[] CaptureEvents(GameplayState state)
        {
            if (state == null) return Array.Empty<WorldEvent>();
            var events = new List<WorldEvent>();
            int doorAt = 0, trashAt = 0, itemAt = 0;
            do
            {
                int dc = Math.Min(64, state.Doors.Length - doorAt), tc = Math.Min(128, state.RemovedTrash.Length - trashAt), ic = Math.Min(128, state.CollectedItems.Length - itemAt);
                var part = new GameplayState { Sequence = state.Sequence, Scene = state.Scene, Doors = new SharedDoorState[dc], RemovedTrash = new string[tc], CollectedItems = new string[ic] };
                Array.Copy(state.Doors, doorAt, part.Doors, 0, dc); Array.Copy(state.RemovedTrash, trashAt, part.RemovedTrash, 0, tc); Array.Copy(state.CollectedItems, itemAt, part.CollectedItems, 0, ic);
                doorAt += dc; trashAt += tc; itemAt += ic;
                var data = JsonSerializer.SerializeToElement(part, DedicatedProtocol.Json);
                if (CanonicalJson.ByteCount(data) > 48000) throw new InvalidOperationException("Gameplay state chunk exceeded protocol limit.");
                events.Add(new WorldEvent { Kind = "gameplay-state", Data = data });
            } while (doorAt < state.Doors.Length || trashAt < state.RemovedTrash.Length || itemAt < state.CollectedItems.Length);
            return events.ToArray();
        }

        public static void Apply(WorldEvent worldEvent)
        {
            if (!clientAuthority || worldEvent == null) return;
            if (worldEvent.Kind == "shared-economy-chunk") { SharedEconomyReplication.Apply(worldEvent); return; }
            if (worldEvent.Kind != "gameplay-state" && worldEvent.Kind != "interaction-result") return;
            var state = worldEvent.Data.Deserialize<GameplayState>(DedicatedProtocol.Json);
            bool noticeOnly = GameplayStatePolicy.ValidateNoticeOnly(worldEvent.Kind, state);
            if (!string.IsNullOrEmpty(state.Message) && state.PlayerId == LocalPlayerId) ShowNotice(state.Message);
            if (!state.Success || noticeOnly) return;
            foreach (var door in state.Doors)
            {
                PendingDoors.Observe(state.Sequence, door);
            }
            foreach (string id in state.RemovedTrash)
            {
                RemovedTrash.Add(id);
            }
            foreach (string id in state.CollectedItems)
            {
                CollectedItems.Add(id);
            }
            ApplyPending();
        }

        public static void Tick()
        {
            if (!clientAuthority || !DedicatedWorldRuntime.IsWorldReady) return;
            if (Time.realtimeSinceStartup >= nextScan) { nextScan = Time.realtimeSinceStartup + 1; RefreshTargets(); ApplyPending(); }
        }
        private static void ApplyPending()
        {
                foreach (var pair in Targets)
                {
                    var target = pair.Value;
                    if (!Loaded(target) || !PendingDoors.TryGet(pair.Key, target.gameObject.scene.name, out var state)) continue;
                    var door = target.TryCast<DoorInteraction>(); var hinge = target.TryCast<HingeInteraction>();
                    if (door != null && door.Open != state.Open) door.ForceState(state.Open);
                    else if (hinge != null && hinge.Open != state.Open) hinge.ForceState(state.Open);
                }
                var ghosts = GhostManager.HasInstance ? GhostManager.Instance : null;
                if (ghosts == null) return;
                foreach (string id in RemovedTrash)
                {
                    var ghost = ghosts.GetGhostById(id);
                    if (ghost == null || ghost.TryCast<Trash>() == null) continue;
                    ghost.IsActive = false; ghosts.UpdateGhostViewPresence(ghost); ghosts.DeregisterGhostImmediate(ghost);
                }
                var collectibles = CollectibleManager.Instance;
                if (collectibles != null)
                {
                    foreach (string id in CollectedItems) if (!collectibles.IsCollected(id)) collectibles.Collect(id);
                    foreach (var item in ReplicatedWorldTargets.Snapshot<ItemCollectible>()) if (Loaded(item) && CollectedItems.Contains(item.GUID)) item.Refresh();
                }
        }

        private static void RefreshTargets()
        {
            Targets.Clear();
            foreach (var door in ReplicatedWorldTargets.Snapshot<DoorInteraction>()) if (Loaded(door)) Targets[GetTargetId(door)] = door;
            foreach (var hinge in ReplicatedWorldTargets.Snapshot<HingeInteraction>()) if (Loaded(hinge)) Targets[GetTargetId(hinge)] = hinge;
            if (!clientAuthority && DedicatedWorldRuntime.IsDedicatedServer)
            {
                var absent = new List<string>();
                foreach (string id in WorkerDoorInstances.Keys) if (!Targets.ContainsKey(id)) absent.Add(id);
                foreach (string id in absent) WorkerDoorInstances.Remove(id);
                foreach (var pair in Targets)
                {
                    var target = pair.Value; int instance = target.GetInstanceID();
                    bool knownInstance = WorkerDoorInstances.TryGetValue(pair.Key, out int previous) && previous == instance;
                    if (!knownInstance && WorkerDoors.TryGet(pair.Key, target.gameObject.scene.name, out var saved))
                    {
                        var door = target.TryCast<DoorInteraction>(); var hinge = target.TryCast<HingeInteraction>();
                        if (door != null && door.Open != saved.Open) door.ForceState(saved.Open);
                        else if (hinge != null && hinge.Open != saved.Open) hinge.ForceState(saved.Open);
                    }
                    WorkerDoorInstances[pair.Key] = instance;
                }
            }
        }
        private static bool Loaded(Component component) => component != null && component.gameObject.scene.IsValid() && component.gameObject.scene.isLoaded;
        private static string GetTargetId(Component component)
        {
            var serializable = component.GetComponentInParent<SerializableObject>();
            string authored = serializable == null ? "" : serializable.GetGuid() ?? "";
            var anchor = string.IsNullOrEmpty(authored) ? null : serializable.transform;
            var parts = new List<string>();
            for (var transform = component.transform; transform != null && transform != anchor; transform = transform.parent)
                parts.Add(transform.name + "#" + transform.GetSiblingIndex());
            parts.Reverse();
            string kind = component.TryCast<DoorInteraction>() != null ? "door" : "hinge";
            int ordinal = 0;
            if (kind == "door") { foreach (var sibling in component.GetComponents<DoorInteraction>()) { if (sibling.Pointer == component.Pointer) break; ordinal++; } }
            else { foreach (var sibling in component.GetComponents<HingeInteraction>()) { if (sibling.Pointer == component.Pointer) break; ordinal++; } }
            string identity = component.gameObject.scene.path + "|" + authored + "|" + string.Join("/", parts) + "|" + kind + "#" + ordinal;
            return kind + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        }
    }
}
