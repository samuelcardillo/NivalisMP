using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using Nivalis;
using Nivalis.Apartment;
using UnityEngine;
using Object = UnityEngine.Object;

namespace NivalisTogether.Dedicated
{
    /// <summary>Independent entry/exit through canonical native apartment doors.
    /// All APIs run on Unity's thread. Native property, position and events are never synthesized.</summary>
    public static class ApartmentReplication
    {
        public const string EventKind = "group-apartment";
        private const string RequestKind = "group-apartment-request", PreparedKind = "group-apartment-prepared", ArrivedKind = "group-apartment-arrived";
        private static bool worker, enabled, terminal, patched, checkpointRequested, invoking;
        private static string localId = "", expectedProperty = "", expectedScene = "", clientPhase = "", nativeError = "";
        private static double now, nextRequest, clientDeadline;
        private static long sequence, lastSequence;
        private static GroupApartmentEvent current;
        private static NativeRoute route;
        private static ApartmentNativePermit permit;
        private static ActionNonAlloc<ApartmentController, bool> observedBus;
        private static Il2CppSystem.Action<ApartmentController, bool> observedCallback;
        private static Action<InteractionRequest> send;
        private static Action<WorldEvent> emit;
        private static Action<string> notice, fail;
        private static Func<bool> externalBusy;
        private static readonly HashSet<string> present = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, PoseSample> poses = new Dictionary<string, PoseSample>(StringComparer.Ordinal);
        private static readonly Dictionary<string, GroupApartmentEvent> replies = new Dictionary<string, GroupApartmentEvent>(StringComparer.Ordinal);
        private static readonly Queue<string> replyOrder = new Queue<string>();
        private sealed class PlayerOperation
        {
            internal ApartmentBarrier Barrier;
            internal GroupApartmentEvent Event;
            internal Vector3 SourcePosition;
            internal double NextHeartbeat;
        }
        private static readonly Dictionary<string, PlayerOperation> operations = new Dictionary<string, PlayerOperation>(StringComparer.Ordinal);
        public static Action<string, string> BeginPlayerTransition { get; set; }
        public static Action<string, string> CancelPlayerTransition { get; set; }
        public static Action<string, string> FailPlayerTransition { get; set; }
        public static Action<string, string, string, string> CommitPlayerContext { get; set; }
        public static bool HasPendingOperations => Authority && !terminal && (worker ? operations.Count != 0 : current != null);
        public static bool IsPlayerBusy(string playerId) => Authority && !terminal && (worker ? operations.ContainsKey(playerId) : playerId == localId && current != null);
        private sealed class PoseSample { internal PlayerPose Pose; internal double Time; }
        private sealed class NativeRoute
        {
            internal ApartmentController Controller;
            internal ApartmentDoor Door;
            internal Transform Target;
            internal string Id, Scene;
            internal bool Enter;
        }
        private static bool Authority => enabled && DedicatedWorldRuntime.IsIsolated && worker == DedicatedWorldRuntime.IsDedicatedServer;
        public static bool IsBusy => Authority && !terminal && !worker && current != null;

        public static void Configure(bool isWorker, string playerId, Action<InteractionRequest> sendInteraction,
            Action<WorldEvent> emitWorldEvent, Action<string> showNotice, Action<string> failSession, Func<bool> otherTransitionBusy)
        {
            if (!Guid.TryParseExact(playerId, "N", out _)) throw new ArgumentException("Apartment authority requires an authenticated player.");
            RemoveObserver();
            worker = isWorker; localId = playerId; send = sendInteraction; emit = emitWorldEvent; notice = showNotice; fail = failSession; externalBusy = otherTransitionBusy;
            enabled = terminal = checkpointRequested = invoking = false; current = null; route = null; permit = null;
            expectedProperty = expectedScene = clientPhase = nativeError = "";
            sequence = lastSequence = 0; now = nextRequest = clientDeadline = 0;
            present.Clear(); poses.Clear(); replies.Clear(); replyOrder.Clear(); operations.Clear();
        }
        public static void SetEnabled(bool value)
        {
            if (value && (!DedicatedWorldRuntime.IsIsolated || worker != DedicatedWorldRuntime.IsDedicatedServer || !DedicatedWorldRuntime.IsWorldReady))
                throw new InvalidOperationException("Apartment authority requires a loaded isolated world.");
            if (value && !enabled)
            {
                InstallPatches(); expectedScene = DedicatedWorldRuntime.Scene; expectedProperty = CurrentPropertyId();
                InstallObserver();
            }
            enabled = value;
            if (!value) { RemoveObserver(); current = null; route = null; permit = null; operations.Clear(); }
        }
        public static void Stop()
        {
            RemoveObserver();
            terminal = true; current = null; route = null; permit = null; invoking = false;
            present.Clear(); poses.Clear(); checkpointRequested = false; operations.Clear();
            BeginPlayerTransition = CancelPlayerTransition = FailPlayerTransition = null; CommitPlayerContext = null;
            // Keep the isolated-session native door guard until process/title cleanup.
        }
        // Only Session's completed legitimate district-travel branch may call this.
        // Sleep refreshes the SAME property's controller and needs no context reset.
        public static void AcceptTravelContext()
        {
            if (!Authority || terminal || IsBusy || !DedicatedWorldRuntime.IsWorldReady) return;
            expectedScene = DedicatedWorldRuntime.Scene; expectedProperty = CurrentPropertyId();
            InstallObserver();
        }
        public static bool ConsumeCheckpointRequest()
        { bool value = checkpointRequested; checkpointRequested = false; return value; }
        public static void SetPlayerPresent(string id, bool joined)
        {
            if (!worker || !Guid.TryParseExact(id, "N", out _)) return;
            if (!joined) { present.Remove(id); poses.Remove(id); operations.Remove(id); return; }
            if (present.Count >= ApartmentProtocol.MaxParticipants && !present.Contains(id)) throw new InvalidOperationException("Too many apartment participants.");
            present.Add(id);
        }
        public static void UpdatePlayerPose(PlayerPose pose, double timestamp)
        {
            if (worker && pose != null && present.Contains(pose.PlayerId) && DedicatedProtocol.PoseValid(pose) && double.IsFinite(timestamp))
                poses[pose.PlayerId] = new PoseSample { Pose = pose, Time = timestamp };
        }
        public static bool Handles(InteractionRequest request) => request != null &&
            (request.Kind == RequestKind || request.Kind == PreparedKind || request.Kind == ArrivedKind);
        public static bool CanContinueDuringShutdown(InteractionRequest request) => request != null &&
            Handles(request) && request.Kind != RequestKind && operations.TryGetValue(request.PlayerId, out var operation) &&
            operation.Barrier.CanAcknowledge(request.PlayerId, request.TargetId, request.Kind == ArrivedKind);
        public static bool? RouteClientInteraction(IInteractable focus)
        {
            if (worker || !DedicatedWorldRuntime.IsIsolated) return null;
            var door = focus?.TryCast<ApartmentDoor>(); if (door == null) return null;
            if (!Authority || terminal) return false;
            try { Request(door); } catch (Exception ex) { notice?.Invoke(Cut(ex.Message)); }
            return false;
        }
        private static void Request(ApartmentDoor door)
        {
            RequireIdle(); RequireLocalReady();
            if (now < nextRequest) return;
            var selected = ResolveDoor(door); ValidateRoute(selected, true);
            RequireNear(selected, PlayerManager.Instance.LocalPlayer.Character.transform.position);
            if (send == null) throw new InvalidOperationException("The shared world connection is not ready.");
            nextRequest = now + .5;
            send(new InteractionRequest { Kind = RequestKind, RequestId = Guid.NewGuid().ToString("N"), TargetId = selected.Id,
                Data = JsonSerializer.SerializeToElement(new ApartmentIntent { Enter = selected.Enter }, DedicatedProtocol.Json) });
            notice?.Invoke(selected.Enter ? "Requesting apartment entry for your character..." : "Requesting apartment exit for your character...");
        }
        public static void Handle(InteractionRequest request, double timestamp)
        {
            if (!worker || !Authority || terminal || !Handles(request)) throw new InvalidOperationException("Apartment request has no loaded authority.");
            now = timestamp;
            if (!double.IsFinite(now) || !present.Contains(request.PlayerId) || !Guid.TryParseExact(request.RequestId, "N", out _)) return;
            if (request.Kind != RequestKind)
            {
                bool arrival = request.Kind == ArrivedKind;
                if (!operations.TryGetValue(request.PlayerId, out var operation) || !operation.Barrier.CanAcknowledge(request.PlayerId, request.TargetId, arrival)) return;
                if (now > operation.Barrier.Deadline) { if (operation.Barrier.Phase == "prepare") CancelOperation(operation, "Apartment preparation timed out."); else FailOperation(operation, "Your apartment arrival timed out. Reconnect to restore your location."); return; }
                ApartmentAcknowledgement ack;
                try { ack = request.Data.Deserialize<ApartmentAcknowledgement>(DedicatedProtocol.Json); }
                catch (JsonException) { return; }
                if (ack == null || !ApartmentProtocol.Bounded(ack.Scene, 128) || !ApartmentProtocol.Bounded(ack.PropertyId, 128) || !ApartmentProtocol.Bounded(ack.Message, 240)) return;
                if (!ack.Success)
                {
                    if (!arrival) CancelOperation(operation, "Apartment transition canceled: " + Cut(ack.Message, 150));
                    else FailOperation(operation, "Your apartment transition failed. Reconnect to restore your confirmed location.");
                    return;
                }
                if (ack.Scene != operation.Event.Scene || ack.PropertyId != (arrival ? operation.Event.DestinationProperty : operation.Event.SourceProperty)) return;
                if (!operation.Barrier.Acknowledge(request.PlayerId, request.TargetId, arrival)) return;
                if (arrival)
                {
                    CommitPlayerContext?.Invoke(request.PlayerId, operation.Event.Scene, operation.Event.DestinationProperty, "");
                    operations.Remove(request.PlayerId);
                    var complete = OperationEvent(operation, "complete", "Apartment transition complete.");
                    Remember(request.PlayerId + ":" + operation.Event.RequestId, complete);
                }
                else
                {
                    try
                    {
                        var actor = Actor(request.PlayerId);
                        if (actor.Scene != operation.Event.Scene || (operation.SourcePosition - new Vector3(actor.X, actor.Y, actor.Z)).sqrMagnitude > 16)
                            throw new InvalidOperationException("Stay near your apartment door until the transition starts.");
                    }
                    catch (Exception ex) { CancelOperation(operation, Cut(ex.Message)); return; }
                    operation.Barrier.BeginWorker(now); operation.Barrier.BeginArrivals(now);
                    Publish(OperationEvent(operation, "execute", operation.Event.Enter ? "Entering the apartment." : "Leaving the apartment."));
                }
                return;
            }
            string key = request.PlayerId + ":" + request.RequestId;
            if (replies.TryGetValue(key, out var previous)) { Publish(previous); return; }
            PlayerOperation starting;
            try
            {
                if (BeginPlayerTransition == null || CancelPlayerTransition == null || CommitPlayerContext == null || FailPlayerTransition == null)
                    throw new InvalidOperationException("Player location authority is unavailable.");
                if (operations.ContainsKey(request.PlayerId) || TravelReplication.IsPlayerBusy(request.PlayerId)) throw new InvalidOperationException("Your character is already changing location.");
                RequireOtherTransitionsIdle(); RequireLocalReady();
                if (!Guid.TryParse(request.TargetId, out _)) throw new InvalidOperationException("Unknown apartment identity.");
                var intent = request.Data.Deserialize<ApartmentIntent>(DedicatedProtocol.Json) ?? throw new InvalidOperationException("Invalid apartment intent.");
                var selected = Resolve(request.TargetId, intent.Enter); ValidateRoute(selected, true);
                var actor = Actor(request.PlayerId);
                if (actor.AccountId != request.AccountId || actor.Scene != selected.Scene) throw new InvalidOperationException("Apartment request account or district binding changed.");
                PropertyOwnershipReplication.RequireApartmentInitiator(request.AccountId, selected.Id, intent.Enter);
                RequireNear(selected, new Vector3(actor.X, actor.Y, actor.Z));
                starting = new PlayerOperation {
                    Barrier = new ApartmentBarrier(Guid.NewGuid().ToString("N"), new[] { request.PlayerId }, now),
                    SourcePosition = selected.Door.transform.position, NextHeartbeat = now + 2
                };
                starting.Event = new GroupApartmentEvent { Epoch = starting.Barrier.Epoch, ApartmentId = selected.Id,
                    Enter = selected.Enter, Scene = selected.Scene, PlayerId = request.PlayerId, Participants = new[] { request.PlayerId }, RequestId = request.RequestId };
            }
            catch (Exception ex) { Remember(key, Standalone("denied", request.PlayerId, request.RequestId, Cut(ex.Message))); return; }
            BeginPlayerTransition(request.PlayerId, starting.Event.Epoch);
            operations.Add(request.PlayerId, starting);
            Remember(key, Standalone("accepted", request.PlayerId, request.RequestId, "Apartment transition requested."));
            Publish(OperationEvent(starting, "prepare", "Preparing your apartment transition."));
        }
        private static GroupApartmentEvent OperationEvent(PlayerOperation operation, string phase, string message)
        {
            var result = Copy(operation.Event, phase, message); result.PlayerId = operation.Event.PlayerId;
            result.Participants = operation.Barrier.Participants; return result;
        }
        private static void CancelOperation(PlayerOperation operation, string message)
        {
            CancelPlayerTransition?.Invoke(operation.Event.PlayerId, operation.Event.Epoch);
            operations.Remove(operation.Event.PlayerId);
            Remember(operation.Event.PlayerId + ":" + operation.Event.RequestId, OperationEvent(operation, "cancel", message));
        }
        private static void FailOperation(PlayerOperation operation, string message)
        {
            operations.Remove(operation.Event.PlayerId);
            FailPlayerTransition?.Invoke(operation.Event.PlayerId, message);
            PublishStandalone("fatal", operation.Event.PlayerId, operation.Event.RequestId, message);
        }
        public static bool Apply(WorldEvent update, double timestamp)
        {
            if (update?.Kind != EventKind) return false;
            if (worker || !Authority || terminal) return true;
            now = timestamp; if (!double.IsFinite(now)) throw new InvalidDataException("Invalid apartment time.");
            var value = update.Data.Deserialize<GroupApartmentEvent>(DedicatedProtocol.Json); ApartmentProtocol.Validate(value);
            if (value.Sequence <= lastSequence) return true;
            lastSequence = value.Sequence;
            if (value.PlayerId.Length > 0 && value.PlayerId != localId) return true;
            if (value.Phase == "fatal") { Fail(value.Message); return true; }
            if (value.Phase == "accepted" || value.Phase == "denied") { notice?.Invoke(value.Message); return true; }
            if (value.Phase == "prepare")
            {
                if (current != null)
                {
                    if (!ApartmentProtocol.SameRoute(current, value)) throw new InvalidDataException("Overlapping apartment transition.");
                    if (clientPhase == "prepared") Acknowledge(false, true, "");
                    return true;
                }
                if (Array.IndexOf(value.Participants, localId) < 0) { Fail("A property transition overtook this world load. Restart and reconnect."); return true; }
                current = value; clientDeadline = now + 65; clientPhase = "prepare";
                try
                {
                    RequireLocalReady(); RequireOtherTransitionsIdle(); CheckContext();
                    route = Resolve(value.ApartmentId, value.Enter); ValidateRoute(route, true);
                    if (route.Scene != value.Scene) throw new InvalidOperationException("The native apartment scene differs.");
                    clientPhase = "prepared"; Acknowledge(false, true, ""); notice?.Invoke(value.Message);
                }
                catch (Exception ex) { clientPhase = "rejected"; Acknowledge(false, false, Cut(ex.Message)); }
                return true;
            }
            if (!ApartmentProtocol.SameRoute(current, value)) return true;
            if (value.Phase == "execute")
            {
                if (clientPhase == "arrived") { Acknowledge(true, true, ""); return true; }
                if (clientPhase != "prepared") { Fail("Apartment execution arrived before local preparation."); return true; }
                try { RequireLocalReady(); ValidateRoute(route, true); InvokeNative(); clientPhase = "executing"; }
                catch (Exception ex) { Acknowledge(true, false, Cut(ex.Message)); Fail("Native apartment transition failed: " + Cut(ex.Message)); }
            }
            else if (value.Phase == "complete")
            {
                // A player can walk after acknowledging the actual native arrival.
                // Keep its property/epoch checks, without requiring it to stay at
                // the teleport point while another participant catches up.
                if (Array.IndexOf(value.FailedPlayers, localId) >= 0 || clientPhase != "arrived" || !NativeComplete(false))
                { Fail("Apartment transition could not synchronize this player. Restart and reconnect."); return true; }
                expectedProperty = current.DestinationProperty; expectedScene = current.Scene; ClearOperation(); notice?.Invoke(value.Message);
            }
            else if (value.Phase == "cancel")
            {
                if (clientPhase == "executing" || clientPhase == "arrived") throw new InvalidDataException("Apartment cancellation followed native execution.");
                ClearOperation(); notice?.Invoke(value.Message);
            }
            return true;
        }
        public static void Tick(double timestamp)
        {
            if (!Authority || terminal) return;
            now = timestamp; if (!double.IsFinite(now)) throw new ArgumentException("Invalid apartment clock.");
            if (nativeError.Length > 0) { Fail(nativeError); return; }
            if (worker)
            {
                foreach (var operation in new List<PlayerOperation>(operations.Values))
                {
                    if (now > operation.Barrier.Deadline)
                    {
                        if (operation.Barrier.Phase == "prepare") CancelOperation(operation, "Apartment preparation timed out.");
                        else FailOperation(operation, "Your apartment arrival timed out. Reconnect to restore your confirmed location.");
                    }
                    else if (now >= operation.NextHeartbeat)
                    { operation.NextHeartbeat = now + 2; Publish(OperationEvent(operation, "heartbeat", "Your character is changing apartment context.")); }
                }
                return;
            }
            if (WorkerSceneResidency.IsRestoringClient) return;
            if (TravelReplication.IsBusy) { if (IsBusy) Fail("Apartment and district transitions overlapped."); return; }
            if (current == null) { if (DedicatedWorldRuntime.IsWorldReady) CheckContext(); return; }
            if (now > clientDeadline) { Fail("Apartment transition timed out. Reconnect to restore your location."); return; }
            if (clientPhase == "executing" && NativeComplete())
            { clientPhase = "arrived"; Acknowledge(true, true, ""); notice?.Invoke("Arrived; confirming your apartment location."); }
        }
        private static void ClearOperation()
        { current = null; route = null; permit = null; invoking = false; clientPhase = ""; }
        private static void Fail(string message)
        {
            if (terminal) return;
            terminal = true; fail?.Invoke(message);
        }
        private static void Acknowledge(bool arrival, bool success, string message)
        {
            if (current == null || send == null) return;
            send(new InteractionRequest { Kind = arrival ? ArrivedKind : PreparedKind, RequestId = Guid.NewGuid().ToString("N"), TargetId = current.Epoch,
                Data = JsonSerializer.SerializeToElement(new ApartmentAcknowledgement { Success = success, Scene = DedicatedWorldRuntime.Scene,
                    PropertyId = CurrentPropertyId(), Message = Cut(message) }, DedicatedProtocol.Json) });
        }
        private static GroupApartmentEvent Standalone(string phase, string player, string request, string message)
            => new GroupApartmentEvent { Phase = phase, PlayerId = player, RequestId = request, Message = Cut(message) };
        private static GroupApartmentEvent Copy(GroupApartmentEvent source, string phase, string message)
            => new GroupApartmentEvent { Epoch = source.Epoch, Phase = phase, RequestId = source.RequestId, PlayerId = source.PlayerId,
                ApartmentId = source.ApartmentId, Scene = source.Scene, Enter = source.Enter, Message = Cut(message),
                Participants = source.Participants, FailedPlayers = source.FailedPlayers };
        private static void PublishStandalone(string phase, string player, string request, string message) => Publish(Standalone(phase, player, request, message));
        private static void Publish(GroupApartmentEvent value)
        { value.Sequence = ++sequence; ApartmentProtocol.Validate(value); emit?.Invoke(new WorldEvent { Kind = EventKind, TargetPlayerId = value.PlayerId, Data = JsonSerializer.SerializeToElement(value, DedicatedProtocol.Json) }); }
        private static void Remember(string key, GroupApartmentEvent value)
        {
            if (!replies.ContainsKey(key)) replyOrder.Enqueue(key); replies[key] = value; while (replyOrder.Count > 1024) replies.Remove(replyOrder.Dequeue()); Publish(value);
        }
        private static string Cut(string text, int max = 240)
        { text = (text ?? "").Replace('\r', ' ').Replace('\n', ' '); return text.Length <= max ? text : text.Substring(0, max); }
        private static string CurrentPropertyId()
        {
            if (!PlayerManager.InstanceNotNull() || PlayerManager.Instance.LocalPlayer == null) throw new InvalidOperationException("Native player context is unavailable.");
            return PlayerManager.Instance.LocalPlayer.CurrentProperty?.TryCast<Nivalis.Apartment.Apartment>()?.Guid ?? "";
        }
        private static void CheckContext()
        {
            if (worker || WorkerSceneResidency.IsRestoringClient) return;
            if (DedicatedWorldRuntime.Scene != expectedScene || CurrentPropertyId() != expectedProperty)
                throw new InvalidOperationException("An unsupported native property change occurred. This session must stop before another checkpoint.");
        }
        private static void RequireOtherTransitionsIdle()
        {
            if (TravelReplication.IsBusy || SleepReplication.IsBusy || DialogueReplication.IsConversationActive || (externalBusy?.Invoke() ?? false))
                throw new InvalidOperationException("Finish the current shared transition or conversation before using the apartment door.");
        }
        private static void RequireIdle()
        { if (!Authority || terminal || IsBusy) throw new InvalidOperationException("Wait for your character's current apartment transition."); RequireOtherTransitionsIdle(); }
        private static void RequireLocalReady()
        {
            if (!DedicatedWorldRuntime.IsWorldReady || !PlayerManager.InstanceNotNull() || PlayerManager.Instance.LocalPlayer == null)
                throw new InvalidOperationException("The native world is still loading.");
            var player = PlayerManager.Instance.LocalPlayer; var character = player.Character;
            if (player.IsHoldingObject || character == null || character.Controller == null || character.Controller.boardedBoat != null)
                throw new InvalidOperationException("Put down held objects and leave any boat before using the apartment door.");
        }
        private static PlayerPose Actor(string id)
        {
            if (!present.Contains(id) || !poses.TryGetValue(id, out var value) || !value.Pose.Active || now - value.Time > 3 || now < value.Time)
                throw new InvalidOperationException("A loaded player's fresh position is unavailable.");
            return value.Pose;
        }
        private static void RequireNear(NativeRoute value, Vector3 position)
        { if ((value.Door.transform.position - position).sqrMagnitude > 16) throw new InvalidOperationException("Stand within four metres of the apartment door until your character departs."); }
        private static NativeRoute ResolveDoor(ApartmentDoor door)
        {
            var controllers = Object.FindObjectsOfType<ApartmentController>(true);
            if (controllers.Length > 512) throw new InvalidOperationException("Too many loaded apartment controllers.");
            NativeRoute selected = null;
            foreach (var controller in controllers)
            {
                if (controller == null || !controller.gameObject.scene.isLoaded || controller.apartment == null) continue;
                bool enter = controller.entrance != null && controller.entrance.Pointer == door.Pointer;
                bool exit = controller.exit != null && controller.exit.Pointer == door.Pointer;
                if (!enter && !exit) continue;
                if (enter == exit || selected != null) throw new InvalidOperationException("Ambiguous native apartment door.");
                selected = Resolve(controller.apartment.Guid, enter);
            }
            return selected ?? throw new InvalidOperationException("This is not a canonical loaded apartment door.");
        }
        private static NativeRoute Resolve(string id, bool enter)
        {
            var controllers = Object.FindObjectsOfType<ApartmentController>(true);
            if (controllers.Length > 512) throw new InvalidOperationException("Too many loaded apartment controllers.");
            NativeRoute selected = null;
            foreach (var controller in controllers)
            {
                if (controller == null || !controller.gameObject.scene.isLoaded || controller.apartment == null || controller.apartment.Guid != id) continue;
                if (selected != null) throw new InvalidOperationException("Ambiguous native apartment identity.");
                var door = enter ? controller.entrance : controller.exit;
                selected = new NativeRoute { Controller = controller, Door = door, Target = door?.teleportPoint, Id = id,
                    Enter = enter, Scene = controller.gameObject.scene.name };
            }
            return selected ?? throw new InvalidOperationException("The requested apartment is not loaded.");
        }
        private static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z) &&
            Math.Abs(value.x) <= 100000 && Math.Abs(value.y) <= 100000 && Math.Abs(value.z) <= 100000;
        private static void ValidateRoute(NativeRoute value, bool before)
        {
            if (value == null || value.Controller == null || value.Door == null || value.Target == null || !ApartmentManager.InstanceNotNull())
                throw new InvalidOperationException("The native apartment door is unavailable.");
            var controller = value.Controller; var apartment = controller.apartment;
            var canonical = apartment == null ? null : ApartmentManager.Instance.GetState(apartment)?.controller;
            var door = value.Enter ? controller.entrance : controller.exit;
            if (apartment == null || apartment.Guid != value.Id || canonical == null || canonical.Pointer != controller.Pointer ||
                door == null || door.Pointer != value.Door.Pointer || door.teleportPoint == null || door.teleportPoint.Pointer != value.Target.Pointer ||
                !controller.gameObject.scene.isLoaded || controller.gameObject.scene.name != value.Scene || value.Scene != DedicatedWorldRuntime.Scene ||
                !door.gameObject.scene.isLoaded || door.gameObject.scene.name != value.Scene ||
                !value.Target.gameObject.scene.isLoaded || value.Target.gameObject.scene.name != value.Scene ||
                !Finite(door.transform.position) || !Finite(value.Target.position))
                throw new InvalidOperationException("The native apartment route changed or crosses scenes.");
            if (controller.enterAndExitWithDistance) throw new InvalidOperationException("Distance-based apartment entry is not supported by the shared world yet.");
            if ((!apartment.PlayerOwned && !apartment.Shelter) || apartment.StageAccess)
                throw new InvalidOperationException("Only an already owned, rented, or shelter apartment is supported; finish property offers separately.");
            if (before)
            {
                if (!door.isActiveAndEnabled || !door.IsActive || !door.CanInteract()) throw new InvalidOperationException("That apartment door is not active or available.");
                string expected = value.Enter ? "" : value.Id;
                if (CurrentPropertyId() != expected || controller.Entered == value.Enter)
                    throw new InvalidOperationException("The party's native apartment context does not match this door.");
            }
        }
        private static bool NativeComplete(bool requireArrivalPosition = true)
        {
            if (!DedicatedWorldRuntime.IsWorldReady || route == null || current == null || permit == null || !permit.Consumed || !permit.ContextObserved) return false;
            ValidateRoute(route, false);
            var property = PlayerManager.Instance.LocalPlayer.CurrentProperty;
            bool context = current.Enter ? property != null && property.Pointer == route.Controller.apartment.Pointer : property == null;
            var position = PlayerManager.Instance.LocalPlayer.Character.transform.position;
            context = context && route.Controller.Entered == current.Enter && Finite(position);
            return requireArrivalPosition ? permit.ObserveArrival(context, (position - route.Target.position).sqrMagnitude <= 16) : permit.IsComplete(context);
        }
        private static void InvokeNative()
        {
            permit = new ApartmentNativePermit(current.Epoch, route.Id, route.Enter); invoking = true; nativeError = "";
            try { route.Door.Enter(); } // native DoInteraction is exactly this tail-call
            finally { invoking = false; }
            if (!permit.Consumed || nativeError.Length > 0) throw new InvalidOperationException(nativeError.Length > 0 ? nativeError : "The exact native apartment invocation was not observed.");
        }
        private static void InstallObserver()
        {
            var bus = ApartmentController.OnPlayerEnterExit;
            if (ReferenceEquals(bus, null)) throw new InvalidOperationException("Native apartment context event is unavailable.");
            if (!ReferenceEquals(observedBus, null) && observedBus.Pointer == bus.Pointer) return;
            RemoveObserver();
            observedCallback = (Il2CppSystem.Action<ApartmentController, bool>)(Action<ApartmentController, bool>)ObserveNative;
            observedBus = bus; observedBus.Add(observedCallback);
        }
        private static void RemoveObserver()
        {
            if (!ReferenceEquals(observedBus, null) && !ReferenceEquals(observedCallback, null))
                try { observedBus.Remove(observedCallback); } catch { /* old epoch is terminal and never authorizes another operation */ }
            observedBus = null; observedCallback = null;
        }
        private static void ObserveNative(ApartmentController controller, bool enter)
        {
            // Exit notifications precede Entered=false in native code. Never inspect
            // completion or tear down the session from inside the native callback.
            if (!Authority || terminal || TravelReplication.IsBusy) return;
            if (WorkerSceneResidency.AllowNativeApartmentObservation(controller, enter)) return;
            try
            {
                if (invoking && route != null && controller != null && controller.Pointer == route.Controller.Pointer && enter == route.Enter)
                {
                    if (permit == null || !permit.ObserveContext(current.Epoch, route.Id, route.Enter))
                        nativeError = "Native apartment context did not match exactly one authorized door invocation.";
                    return;
                }
                // Native sleep wakes by re-entering its already-current apartment.
                // This exemption is identity-specific and never permits an exit.
                if (enter && SleepReplication.IsBusy && controller?.apartment != null &&
                    controller.apartment.Guid == (worker ? CurrentPropertyId() : expectedProperty) && CurrentPropertyId() == (worker ? controller.apartment.Guid : expectedProperty)) return;
                nativeError = "An unsupported native apartment context change occurred outside an authorized transition; stop before another checkpoint.";
            }
            catch (Exception ex) { nativeError = "Native apartment context observation failed: " + Cut(ex.Message, 150); }
        }
        private static void InstallPatches()
        {
            if (patched) return;
            var method = typeof(ApartmentDoor).GetMethod("Enter", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            if (method == null || method.ReturnType != typeof(void)) throw new InvalidOperationException("Apartment door native ABI is unsupported.");
            new Harmony("NivalisTogether.ApartmentAuthority").Patch(method, prefix: new HarmonyMethod(typeof(ApartmentReplication).GetMethod(nameof(DoorPrefix), BindingFlags.NonPublic | BindingFlags.Static)));
            patched = true;
        }
        private static bool DoorPrefix(ApartmentDoor __instance)
        {
            if (!Authority) return true;
            if (WorkerSceneResidency.AllowNativeApartment(__instance)) return true;
            if (terminal) return false;
            if (invoking)
            {
                if (current != null && route?.Door != null && __instance != null && __instance.Pointer == route.Door.Pointer &&
                    permit != null && permit.Consume(current.Epoch, route.Id, route.Enter)) return true;
                nativeError = "A reentrant apartment door did not match the authorized transition."; return false;
            }
            if (worker) { nativeError = "An unsupported native apartment door request occurred outside an authorized transition; stop before another checkpoint."; return false; }
            try { Request(__instance); } catch (Exception ex) { notice?.Invoke(Cut(ex.Message)); }
            return false;
        }
    }
}
