using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using Nivalis;
using UnityEngine;
using Object = UnityEngine.Object;

namespace NivalisTogether.Dedicated
{
    public sealed class GroupTravelEvent
    {
        public long Sequence { get; set; }
        public string Epoch { get; set; } = "";
        public string Phase { get; set; } = "";
        public string PlayerId { get; set; } = ""; // empty means the your character
        public string RequestId { get; set; } = "";
        public string PortalId { get; set; } = "";
        public string PortalName { get; set; } = "";
        public string TargetLocationId { get; set; } = "";
        public string FromScene { get; set; } = "";
        public string ToScene { get; set; } = "";
        public bool IsCabTravel { get; set; } // derived from the authoritative native terminal
        public bool IsBoatTravel { get; set; }
        public bool IsMapTravel { get; set; }
        public string BoatId { get; set; } = "";
        public string Message { get; set; } = "";
        public string[] Participants { get; set; } = Array.Empty<string>();
        public string[] FailedPlayers { get; set; } = Array.Empty<string>();
    }

    public sealed class GroupTravelIntent
    {
        public string ToScene { get; set; } = "";
        public string PortalName { get; set; } = "";
        public string TargetLocationId { get; set; } = "";
        public bool IsBoatTravel { get; set; }
        public bool IsMapTravel { get; set; }
        public string BoatId { get; set; } = "";
    }

    public sealed class GroupTravelAcknowledgement
    {
        public bool Success { get; set; }
        public string Scene { get; set; } = "";
        public string Message { get; set; } = "";
        public BoatPose Boat { get; set; }
    }

    // This barrier contains no Unity/native dependencies, so replay, membership,
    // phase ordering and bounded timeouts can be tested without launching the game.
    public sealed class GroupTravelBarrier
    {
        public const int MaxParticipants = 32;
        public const double MaxCabCinematicSeconds = 60;
        public const double CabWorkerSeconds = 45 + MaxCabCinematicSeconds;
        public const double CabTotalSeconds = 15 + CabWorkerSeconds * 2;
        private readonly bool cab;
        public string Epoch { get; }
        public string Phase { get; private set; } = "prepare";
        public double Deadline { get; private set; }
        public double FinalDeadline { get; }
        private readonly HashSet<string> members = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> prepared = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> arrived = new HashSet<string>(StringComparer.Ordinal);
        public GroupTravelBarrier(string epoch, IEnumerable<string> participants, double now, bool isCabTravel = false, bool isBoatTravel = false)
        {
            if (!Guid.TryParseExact(epoch, "N", out _) || !double.IsFinite(now)) throw new ArgumentException("Invalid travel epoch/time.");
            if (isCabTravel && isBoatTravel) throw new ArgumentException("A route cannot be both cab and boat travel.");
            cab = isCabTravel || isBoatTravel; Epoch = epoch; Deadline = now + 15; FinalDeadline = now + (cab ? CabTotalSeconds : 90);
            foreach (string participant in participants) Add(participant);
        }
        public void Add(string player)
        {
            if (!Guid.TryParseExact(player, "N", out _) || (!members.Contains(player) && members.Count >= MaxParticipants)) throw new ArgumentException("Invalid travel participant.");
            members.Add(player);
        }
        public void Remove(string player) { members.Remove(player); prepared.Remove(player); arrived.Remove(player); }
        public bool Contains(string player) => members.Contains(player);
        public bool IsPrepared(string player) => prepared.Contains(player);
        public bool Acknowledge(string player, string epoch, bool arrival)
        {
            if (epoch != Epoch || !members.Contains(player)) return false;
            // A client whose snapshot finishes mid-travel still needs its local
            // preflight. TCP preserves its prepared-before-arrived acknowledgement order.
            if (!arrival && (Phase == "prepare" || Phase == "worker" || Phase == "arrive")) return prepared.Add(player);
            if (arrival && Phase == "arrive" && prepared.Contains(player)) return arrived.Add(player);
            return false;
        }
        public bool AllPrepared => prepared.Count == members.Count;
        public bool AllArrived => arrived.Count == members.Count;
        public string[] Participants { get { var value = new string[members.Count]; members.CopyTo(value); Array.Sort(value, StringComparer.Ordinal); return value; } }
        public string[] MissingArrivals
        { get { var value = new List<string>(); foreach (string player in members) if (!arrived.Contains(player)) value.Add(player); return value.ToArray(); } }
        public void BeginWorker(double now)
        {
            if (Phase != "prepare" || !AllPrepared || now > Deadline || !double.IsFinite(now)) throw new InvalidOperationException("Travel preparation barrier is not complete.");
            Phase = "worker"; Deadline = Math.Min(now + (cab ? CabWorkerSeconds : 45), FinalDeadline);
        }
        public void BeginArrivals(double now)
        {
            if (Phase != "worker" || now > Deadline || !double.IsFinite(now)) throw new InvalidOperationException("Worker travel barrier is not complete.");
            Phase = "arrive"; Deadline = FinalDeadline;
        }
    }

    // Pure native-arrival identity/phase gate. Scene readiness alone can occur
    // before a cab cinematic finishes and before the local player is placed.
    public sealed class GroupTravelNativeArrival
    {
        public string Epoch { get; }
        public string PortalId { get; }
        public string Scene { get; }
        public string LocationId { get; }
        public bool IsCabTravel { get; }
        public bool IsBoatTravel { get; }
        public bool ArrivalObserved { get; private set; }
        private bool initialEntered, continuationUsed;
        public GroupTravelNativeArrival(string epoch, string portal, string scene, string location, bool cab, bool boat = false)
        {
            if (!Guid.TryParseExact(epoch, "N", out _) || !DedicatedProtocol.Text(portal, 128) ||
                !DedicatedProtocol.Text(scene, 128) || !DedicatedProtocol.Text(location, 128)) throw new ArgumentException("Invalid native travel identity.");
            if (cab && boat) throw new ArgumentException("A route cannot be both cab and boat travel.");
            Epoch = epoch; PortalId = portal; Scene = scene; LocationId = location; IsCabTravel = cab; IsBoatTravel = boat;
        }
        public bool Matches(string epoch, string portal, string scene, bool cab, bool force, bool boat) =>
            epoch == Epoch && portal == PortalId && scene == Scene && cab == IsCabTravel && !force && boat == IsBoatTravel;
        public bool MatchesNativeBoatArgument(IntPtr expectedBoat, string expectedBoatId, IntPtr suppliedBoat, string suppliedBoatId,
            IntPtr sequenceBoat, string sequenceBoatId, IntPtr expectedPortal, IntPtr sequencePortal)
        {
            if (!IsBoatTravel) return suppliedBoat == IntPtr.Zero && string.IsNullOrEmpty(suppliedBoatId);
            // Native BoatTeleportSequence stores its boat before calling
            // RequestTravel(destination, false, false, null). Journey identity
            // comes from that exact authorized sequence, not the optional arg.
            return expectedBoat != IntPtr.Zero && !string.IsNullOrEmpty(expectedBoatId) &&
                expectedPortal != IntPtr.Zero && sequenceBoat == expectedBoat && sequenceBoatId == expectedBoatId &&
                sequencePortal == expectedPortal && (suppliedBoat == IntPtr.Zero
                    ? string.IsNullOrEmpty(suppliedBoatId)
                    : suppliedBoat == expectedBoat && suppliedBoatId == expectedBoatId);
        }
        public bool AuthorizeInitial(string epoch, string portal, string scene, bool cab, bool force, bool boat, bool consume)
        {
            if (initialEntered || ArrivalObserved || !Matches(epoch, portal, scene, cab, force, boat)) return false;
            if (consume) initialEntered = true;
            return true;
        }
        public bool AuthorizeContinuation(string epoch, string portal, string scene, bool cab, bool force, bool boat, bool consume)
        {
            if (!initialEntered || continuationUsed || ArrivalObserved || !Matches(epoch, portal, scene, cab, force, boat)) return false;
            if (consume) continuationUsed = true;
            return true;
        }
        public bool Observe(string epoch, string scene, string location)
        {
            if (!initialEntered || epoch != Epoch || scene != Scene || location != LocationId || ArrivalObserved) return false;
            ArrivalObserved = true; return true;
        }
        public bool IsComplete(bool worldReady, string scene, bool timeLock, bool shortcutLock) =>
            ArrivalObserved && worldReady && scene == Scene && !timeLock && !shortcutLock;
        public static bool IsBoundedCinematic(double duration) => double.IsFinite(duration) && duration >= 0 && duration <= GroupTravelBarrier.MaxCabCinematicSeconds;
        public static bool SameRoute(GroupTravelEvent a, GroupTravelEvent b) => a != null && b != null &&
            a.Epoch == b.Epoch && a.PortalId == b.PortalId && a.PortalName == b.PortalName && a.TargetLocationId == b.TargetLocationId &&
            a.FromScene == b.FromScene && a.ToScene == b.ToScene && a.IsCabTravel == b.IsCabTravel &&
            a.IsBoatTravel == b.IsBoatTravel && a.IsMapTravel == b.IsMapTravel && a.BoatId == b.BoatId;
    }

    /// <summary>Independent travel changes only the requesting player's district.
    /// Native nearby cab terminals retain their native arrival behavior; no fare is invented.
    /// Native portals remain authoritative; the worker is focused before request validation.
    /// Call all methods from Unity's main thread. Networking remains in DedicatedSession.</summary>
    public static class TravelReplication
    {
        public const string EventKind = "group-travel";
        private const string RequestKind = "group-travel-request", PreparedKind = "group-travel-prepared", ArrivedKind = "group-travel-arrived";
        private static bool enabled, worker, patched, terminal;
        private static int nativeCallDepth;
        private static string localPlayer = "", authoritativeScene = "";
        private static double now, nextRequest, clientDeadline;
        private static string pendingBoatRequest = "";
        private static double pendingBoatDeadline;
        private static InteractionRequest queuedBoatRequest;
        private static BoatPose queuedBoatPose;
        private static long sequence, lastSequence;
        private static GroupTravelEvent current;
        private static PortalKey destination;
        private static GroupTravelNativeArrival nativeArrival;
        private static IntPtr authorizedBoatGhost;
        private static SafeEvent<WorldLocation> arrivalEvent;
        private static Il2CppSystem.Action<WorldLocation> arrivalListener;
        private static string nativeArrivalError = "";
        private static string clientPhase = "";
        private static readonly HashSet<string> present = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, PoseSample> poses = new Dictionary<string, PoseSample>(StringComparer.Ordinal);
        private static readonly Dictionary<string, GroupTravelEvent> completed = new Dictionary<string, GroupTravelEvent>(StringComparer.Ordinal);
        private static readonly Queue<string> completedOrder = new Queue<string>();
        private sealed class PlayerOperation
        {
            internal GroupTravelBarrier Barrier;
            internal GroupTravelEvent Event;
            internal Vector3 SourcePosition;
            internal DeparturePose Departure;
            internal double NextHeartbeat;
        }
        private sealed class DeparturePose
        {
            internal readonly string PlayerId, AccountId, Scene, PropertyId;
            internal readonly long PendingGeneration;
            internal readonly Vector3 Position;
            internal readonly double ValidatedAt;
            internal DeparturePose(PlayerPose pose, PlayerSceneContext context, double timestamp)
            {
                PlayerId = pose.PlayerId; AccountId = pose.AccountId;
                Scene = pose.Scene; PropertyId = pose.PropertyId;
                Position = new Vector3(pose.X, pose.Y, pose.Z);
                PendingGeneration = checked(context.Generation + 1); ValidatedAt = timestamp;
            }
        }
        private static readonly Dictionary<string, PlayerOperation> operations = new Dictionary<string, PlayerOperation>(StringComparer.Ordinal);
        public static Action<string, string> BeginPlayerTransition { get; set; }
        public static Action<string, string> CancelPlayerTransition { get; set; }
        public static Action<string, string> FailPlayerTransition { get; set; }
        public static Action<string, string, string, string> CommitPlayerContext { get; set; }
        public static Func<BoatPose> CaptureBoatArrival { get; set; }
        public static bool HasPendingOperations => HasAuthority && !terminal && (worker ? operations.Count != 0 : current != null);
        public static bool IsPlayerBusy(string playerId) => HasAuthority && !terminal && (worker ? operations.ContainsKey(playerId) : playerId == localPlayer && current != null);
        private sealed class PoseSample { public PlayerPose Pose; public double Time; }
        private static Action<InteractionRequest> sendIntent;
        private static Action<WorldEvent> emit;
        private static Action<string> notice, fail;
        private static bool HasAuthority => enabled && DedicatedWorldRuntime.IsIsolated && worker == DedicatedWorldRuntime.IsDedicatedServer;
        public static bool IsBusy => HasAuthority && !terminal && !worker && current != null;

        public static void Configure(bool isWorker, string playerId, Action<InteractionRequest> sendInteraction,
            Action<WorldEvent> emitWorldEvent, Action<string> showNotice, Action<string> failSession)
        {
            if (!Guid.TryParseExact(playerId, "N", out _)) throw new ArgumentException("Travel needs the authenticated player identity.");
            ClearNativeArrival();
            ReleaseBoatRequest();
            worker = isWorker; localPlayer = playerId; sendIntent = sendInteraction; emit = emitWorldEvent;
            notice = showNotice; fail = failSession; terminal = false; enabled = false; authoritativeScene = "";
            current = null; destination = null; clientPhase = "";
            sequence = lastSequence = 0; now = nextRequest = clientDeadline = 0;
            present.Clear(); poses.Clear(); completed.Clear(); completedOrder.Clear(); operations.Clear();
        }
        public static void SetEnabled(bool value)
        {
            if (value && (!DedicatedWorldRuntime.IsIsolated || worker != DedicatedWorldRuntime.IsDedicatedServer ||
                !Guid.TryParseExact(localPlayer, "N", out _) || !DedicatedWorldRuntime.IsWorldReady))
                throw new InvalidOperationException("Travel authority requires an authenticated, loaded isolated multiplayer world.");
            if (value && !patched) InstallPatches();
            if (value && !enabled) authoritativeScene = DedicatedWorldRuntime.Scene;
            enabled = value;
            if (!value) { ReleaseBoatRequest(); ClearNativeArrival(); current = null; destination = null; clientPhase = ""; operations.Clear(); }
        }
        // On disconnect keep native authority guards installed until the isolated
        // process returns to title/exits, but discard callbacks from the old epoch.
        public static void Stop()
        {
            ClearNativeArrival();
            ReleaseBoatRequest();
            terminal = true; current = null; destination = null; clientPhase = ""; present.Clear(); poses.Clear(); operations.Clear();
            BeginPlayerTransition = CancelPlayerTransition = FailPlayerTransition = null; CommitPlayerContext = null;
            CaptureBoatArrival = null;
        }
        public static void SetPlayerPresent(string playerId, bool joined)
        {
            if (!worker || !Guid.TryParseExact(playerId, "N", out _)) return;
            if (!joined)
            {
                if (operations.TryGetValue(playerId, out var operation) && operation.Event?.IsBoatTravel == true) BoatAuthority.CancelTravel(playerId);
                present.Remove(playerId); poses.Remove(playerId); operations.Remove(playerId); return;
            }
            if (present.Count >= GroupTravelBarrier.MaxParticipants && !present.Contains(playerId)) throw new InvalidOperationException("Independent travel player limit exceeded.");
            present.Add(playerId);
        }
        public static void UpdatePlayerPose(PlayerPose pose, double timestamp)
        {
            if (!worker || pose == null || !present.Contains(pose.PlayerId) || !DedicatedProtocol.PoseValid(pose) || !double.IsFinite(timestamp)) return;
            poses[pose.PlayerId] = new PoseSample { Pose = pose, Time = timestamp };
        }
        public static bool Handles(InteractionRequest request) => request != null &&
            (request.Kind == RequestKind || request.Kind == PreparedKind || request.Kind == ArrivedKind);

        public static void Handle(InteractionRequest request, double timestamp)
        {
            if (!worker || !HasAuthority || terminal || !Handles(request)) throw new InvalidOperationException("Travel request routed to an unavailable authority.");
            now = timestamp;
            if (!double.IsFinite(now) || !Guid.TryParseExact(request.PlayerId, "N", out _) || !Guid.TryParseExact(request.RequestId, "N", out _) || !present.Contains(request.PlayerId)) return;
            if (request.Kind != RequestKind)
            {
                if (!operations.TryGetValue(request.PlayerId, out var operation) || request.TargetId != operation.Barrier.Epoch || operation.Departure == null ||
                    request.AccountId != operation.Departure.AccountId) return;
                if (now > operation.Barrier.Deadline) { if (operation.Barrier.Phase == "prepare") CancelOperation(operation, "Travel preparation timed out."); else FailOperation(operation, "Your district load timed out. Reconnect to restore your location."); return; }
                GroupTravelAcknowledgement ack;
                try { ack = request.Data.Deserialize<GroupTravelAcknowledgement>(DedicatedProtocol.Json); }
                catch (JsonException) { return; }
                if (ack == null || !Bounded(ack.Message, 160) || !Bounded(ack.Scene, 128)) return;
                bool arrival = request.Kind == ArrivedKind;
                if (arrival ? operation.Barrier.Phase != "arrive" : operation.Barrier.Phase != "prepare") return;
                if (!ack.Success)
                {
                    if (arrival) FailOperation(operation, "Your district load failed. Reconnect to restore your last confirmed location.");
                    else CancelOperation(operation, "Travel canceled: " + Cut(ack.Message, 120));
                    return;
                }
                if (ack.Scene != (arrival ? operation.Event.ToScene : operation.Event.FromScene)) return;
                if (arrival)
                {
                    try
                    {
                        ValidateArrivalBoat(operation.Event, request, ack);
                        if (operation.Event.IsBoatTravel)
                        {
                            BoatAuthority.CommitTravel(request.PlayerId, ack.Boat, operation.Event.ToScene);
                        }
                    }
                    catch (Exception ex) { FailOperation(operation, "Boat arrival could not be confirmed: " + Cut(ex.Message, 100)); return; }
                }
                if (!operation.Barrier.Acknowledge(request.PlayerId, request.TargetId, arrival)) return;
                if (arrival)
                {
                    CommitPlayerContext?.Invoke(request.PlayerId, operation.Event.ToScene, "", operation.Event.PortalId);
                    var complete = OperationEvent(operation, "complete", "Arrived in " + operation.Event.ToScene + ".");
                    Remember(request.PlayerId + ":" + operation.Event.RequestId, complete);
                    operations.Remove(request.PlayerId); PublishCopy(complete);
                }
                else
                {
                    try { RevalidateOperation(operation); }
                    catch (Exception ex) { CancelOperation(operation, "Travel could not depart: " + Cut(ex.Message, 110)); return; }
                    operation.Barrier.BeginWorker(now); operation.Barrier.BeginArrivals(now);
                    PublishCopy(OperationEvent(operation, "depart", "Traveling to " + operation.Event.ToScene + "."));
                }
                return;
            }
            string dedupe = request.PlayerId + ":" + request.RequestId;
            if (completed.TryGetValue(dedupe, out var previous)) { PublishCopy(previous); return; }
            PlayerOperation starting;
            bool boatReserved = false;
            try
            {
                if (BeginPlayerTransition == null || CancelPlayerTransition == null || CommitPlayerContext == null || FailPlayerTransition == null)
                    throw new InvalidOperationException("Player location authority is unavailable.");
                if (operations.ContainsKey(request.PlayerId) || ApartmentReplication.IsPlayerBusy(request.PlayerId)) throw new InvalidOperationException("Your character is already traveling.");
                if (SleepReplication.IsBusy) throw new InvalidOperationException("Wait for the shared day transition to finish.");
                RequireWorldReady();
                if (!poses.TryGetValue(request.PlayerId, out var actor) || !actor.Pose.Active || now - actor.Time > 5 || now < actor.Time || actor.Pose.Scene != DedicatedWorldRuntime.Scene)
                    throw new InvalidOperationException("Your current district position is not available to the worker.");
                var sourceContext = PlayerMapRuntime.GetContext(request.PlayerId);
                if (actor.Pose.PlayerId != request.PlayerId || actor.Pose.AccountId != request.AccountId ||
                    sourceContext == null || sourceContext.TravelPending || sourceContext.PlayerId != request.PlayerId ||
                    sourceContext.Scene != actor.Pose.Scene || sourceContext.PropertyId != actor.Pose.PropertyId)
                    throw new InvalidOperationException("Your departure position does not match your assigned character context.");
                var intent = request.Data.Deserialize<GroupTravelIntent>(DedicatedProtocol.Json);
                ValidateIntent(intent);
                var position = new Vector3(actor.Pose.X, actor.Pose.Y, actor.Pose.Z);
                PortalKey key;
                bool cab = false;
                Vector3 sourcePosition = position;
                if (intent.IsMapTravel)
                {
                    key = FindPortal(request.TargetId, intent.ToScene, intent.PortalName, intent.TargetLocationId);
                    ValidatePublicMapDestination(key);
                }
                else if (intent.IsBoatTravel)
                {
                    var portal = FindBoatPortal(request.TargetId, intent.ToScene, intent.PortalName, intent.TargetLocationId, position);
                    if (portal == null) throw new InvalidOperationException("Your boat is not at an active waterway exit.");
                    key = portal.keyTo; ValidateDestination(key);
                    BoatAuthority.ValidateTravel(request.PlayerId, intent.BoatId, portal, now);
                    boatReserved = true;
                }
                else
                {
                    var portal = FindTransition(request.TargetId, intent.ToScene, intent.PortalName, intent.TargetLocationId, position);
                    if (portal == null) throw new InvalidOperationException("Stand near an active district portal to request travel.");
                    ValidateTransition(portal); key = portal.keyTo; cab = portal.isCabTravel; sourcePosition = portal.transform.position;
                }
                starting = new PlayerOperation {
                    Barrier = new GroupTravelBarrier(Guid.NewGuid().ToString("N"), new[] { request.PlayerId }, now, cab, intent.IsBoatTravel),
                    SourcePosition = sourcePosition, Departure = new DeparturePose(actor.Pose, sourceContext, now), NextHeartbeat = now + 2
                };
                starting.Event = new GroupTravelEvent { Epoch = starting.Barrier.Epoch, PlayerId = request.PlayerId,
                    Participants = new[] { request.PlayerId }, PortalId = key.Guid, PortalName = key.name,
                    TargetLocationId = key.TargetLocation.Guid, FromScene = actor.Pose.Scene,
                    ToScene = key.SceneName, RequestId = request.RequestId, IsCabTravel = cab,
                    IsBoatTravel = intent.IsBoatTravel, IsMapTravel = intent.IsMapTravel, BoatId = intent.BoatId };
            }
            catch (Exception ex)
            {
                if (boatReserved) BoatAuthority.CancelTravel(request.PlayerId);
                var rejected = Standalone("denied", request.PlayerId, request.RequestId, Cut(ex.Message, 160));
                Remember(dedupe, rejected); PublishCopy(rejected); return;
            }
            // From here the location authority changes. Publication or callback errors
            // must stop the session, never turn a partially published transition into denial.
            BeginPlayerTransition(request.PlayerId, starting.Barrier.Epoch);
            operations.Add(request.PlayerId, starting);
            Remember(dedupe, OperationEvent(starting, "accepted", "Travel requested."));
            PublishCopy(OperationEvent(starting, "prepare", "Preparing travel to " + starting.Event.ToScene + "."));
        }
        private static GroupTravelEvent OperationEvent(PlayerOperation operation, string phase, string message)
        {
            var result = Copy(operation.Event, phase, operation.Event.PlayerId, message);
            result.Participants = operation.Barrier.Participants; return result;
        }
        private static void RevalidateOperation(PlayerOperation operation)
        {
            var departure = operation.Departure;
            var context = PlayerMapRuntime.GetContext(operation.Event.PlayerId);
            // Membership deliberately rejects movement poses once preparation
            // begins. Requiring that blocked stream to remain fresh makes a
            // valid departure expire while its ordered prepare event is queued.
            // Retain only this operation's already-validated origin; this does
            // not admit stale ordinary poses or grant another scene membership.
            if (departure == null || !present.Contains(departure.PlayerId) ||
                !operations.TryGetValue(departure.PlayerId, out var active) || !ReferenceEquals(active, operation) ||
                operation.Event.PlayerId != departure.PlayerId || operation.Event.FromScene != departure.Scene ||
                operation.Event.Epoch != operation.Barrier.Epoch || operation.Barrier.Phase != "prepare" ||
                now < departure.ValidatedAt || now > operation.Barrier.Deadline ||
                context == null || !context.TravelPending || context.PlayerId != departure.PlayerId ||
                context.Epoch != operation.Barrier.Epoch || context.Generation != departure.PendingGeneration ||
                context.Scene != departure.Scene || context.PropertyId != departure.PropertyId ||
                (operation.SourcePosition - departure.Position).sqrMagnitude > 64)
                throw new InvalidOperationException("Your validated departure no longer belongs to this active travel preparation.");
            var key = FindPortal(operation.Event.PortalId, operation.Event.ToScene, operation.Event.PortalName, operation.Event.TargetLocationId);
            if (key == null || key.TargetLocation == null || key.SceneName != operation.Event.ToScene ||
                !TravelUnlockManager.Instance.IsLocationUnlocked(key.TargetLocation))
                throw new InvalidOperationException("The authorized destination is no longer available.");
            if (operation.Event.IsMapTravel) ValidatePublicMapDestination(key, departure.Scene);
            // BoatAuthority froze the validated boat/exit at acceptance. Do not
            // resolve scene-local boat triggers here: the native worker can be
            // serving another player's map while this client's ACK arrives.
        }
        private static void CancelOperation(PlayerOperation operation, string message)
        {
            if (operation.Event.IsBoatTravel) BoatAuthority.CancelTravel(operation.Event.PlayerId);
            CancelPlayerTransition?.Invoke(operation.Event.PlayerId, operation.Event.Epoch);
            operations.Remove(operation.Event.PlayerId);
            var value = OperationEvent(operation, "cancel", message);
            Remember(operation.Event.PlayerId + ":" + operation.Event.RequestId, value); PublishCopy(value);
        }
        private static void FailOperation(PlayerOperation operation, string message)
        {
            if (operation.Event.IsBoatTravel) BoatAuthority.CancelTravel(operation.Event.PlayerId);
            operations.Remove(operation.Event.PlayerId);
            FailPlayerTransition?.Invoke(operation.Event.PlayerId, message);
            PublishStandalone("fatal", operation.Event.PlayerId, operation.Event.RequestId, message);
        }
        public static bool Apply(WorldEvent worldEvent, double timestamp)
        {
            if (worldEvent == null || worldEvent.Kind != EventKind) return false;
            if (worker || !HasAuthority || terminal) return true;
            now = timestamp;
            var value = worldEvent.Data.Deserialize<GroupTravelEvent>(DedicatedProtocol.Json);
            ValidateWire(value);
            if (value.Sequence <= lastSequence) return true;
            lastSequence = value.Sequence;
            if (value.PlayerId.Length > 0 && value.PlayerId != localPlayer) return true;
            if (value.Phase == "fatal") { Fail(value.Message); return true; }
            if (value.Phase == "denied" || value.Phase == "accepted")
            {
                if (value.Phase == "denied" && value.RequestId == pendingBoatRequest) ReleaseBoatRequest();
                notice?.Invoke(value.Message); return true;
            }
            if (value.Phase == "prepare")
            {
                if (Array.IndexOf(value.Participants, localPlayer) < 0) return true;
                if (current != null && current.Epoch != value.Epoch) { Fail("Overlapping travel was rejected. Reconnect to the server world."); return true; }
                if (current != null && !GroupTravelNativeArrival.SameRoute(current, value)) { Fail("The travel route changed within an epoch."); return true; }
                if (current != null) return true; // an idempotent preparation message never restarts native travel
                current = value; clientDeadline = now + (value.IsCabTravel || value.IsBoatTravel ? GroupTravelBarrier.CabTotalSeconds + 10 : 100); clientPhase = "prepared";
                try
                {
                    RequireWorldReady();
                    if (DedicatedWorldRuntime.Scene != value.FromScene) throw new InvalidOperationException("Your district does not match your departure district.");
                    destination = FindPortal(value.PortalId, value.ToScene, value.PortalName, value.TargetLocationId);
                    if (destination == null || destination.SceneName != value.ToScene) throw new InvalidOperationException("The destination portal is unavailable in this game build.");
                    if (value.IsBoatTravel)
                    {
                        if (pendingBoatRequest != value.RequestId) throw new InvalidOperationException("This boat request is no longer waiting to depart.");
                        RequireLocalBoatReady(value.BoatId);
                        var departure = FindBoatPortal(value.PortalId, value.ToScene, value.PortalName, value.TargetLocationId,
                            BoatReplication.OwnBoatController.transform.position);
                        if (departure == null || departure.keyTo.Pointer != destination.Pointer)
                            throw new InvalidOperationException("The authorized route does not match your waterway exit.");
                    }
                    else
                    {
                        RequireLocalTravelReady();
                        if (value.IsMapTravel) ValidatePublicMapDestination(destination);
                        else
                        {
                            var departure = FindTransition(value.PortalId, value.ToScene, value.PortalName, value.TargetLocationId, PlayerManager.Instance.LocalPlayer.Character.transform.position);
                            if (departure == null || departure.keyTo.Pointer != destination.Pointer || departure.isCabTravel != value.IsCabTravel)
                                throw new InvalidOperationException("The authorized route does not match your nearby native terminal.");
                            ValidateTransition(departure);
                        }
                    }
                    Acknowledge(false, true, ""); notice?.Invoke(value.Message);
                }
                catch (Exception ex) { clientPhase = "rejected"; Acknowledge(false, false, Cut(ex.Message, 160)); }
                return true;
            }
            if (current == null || current.Epoch != value.Epoch) return true;
            if (!GroupTravelNativeArrival.SameRoute(current, value)) { Fail("The travel route changed within an epoch."); return true; }
            if (value.Phase == "cancel") { notice?.Invoke(value.Message); ClearClient(); return true; }
            if (value.Phase == "depart")
            {
                if (clientPhase != "prepared") return true;
                try { clientPhase = "loading"; InvokeNative(destination, current.IsCabTravel); notice?.Invoke(value.Message); }
                catch (Exception ex) { Acknowledge(true, false, Cut(ex.Message, 160)); Fail("Travel could not start. Restart and reconnect: " + ex.Message); }
                return true;
            }
            if (value.Phase == "complete")
            {
                if (Array.IndexOf(value.FailedPlayers, localPlayer) >= 0 || clientPhase != "arrived" || !NativeArrivalComplete())
                    Fail("Your client did not finish travel. Restart the game and reconnect.");
                else { notice?.Invoke(value.Message); ClearClient(); }
            }
            return true;
        }

        public static void Tick(double timestamp)
        {
            now = timestamp;
            if (!HasAuthority || terminal) return;
            if (!double.IsFinite(now)) throw new ArgumentException("Invalid travel time.");
            if (worker)
            {
                foreach (var operation in new List<PlayerOperation>(operations.Values))
                {
                    if (now > operation.Barrier.Deadline)
                    {
                        if (operation.Barrier.Phase == "prepare") CancelOperation(operation, "Travel preparation timed out.");
                        else FailOperation(operation, "Your district load timed out. Reconnect to restore your last confirmed location.");
                    }
                    else if (now >= operation.NextHeartbeat)
                    { operation.NextHeartbeat = now + 2; PublishCopy(OperationEvent(operation, "heartbeat", "Your character is traveling.")); }
                }
                return;
            }
            if ((pendingBoatRequest.Length != 0 || queuedBoatRequest != null) && now > pendingBoatDeadline)
            {
                ReleaseBoatRequest();
                if (current != null) { Fail("Your boat departure confirmation timed out. Reconnect to restore your location."); return; }
                notice?.Invoke("Your boat travel request timed out. You can try the waterway exit again.");
            }
            if (WorkerSceneResidency.IsRestoringClient) return;
            if (!IsBusy && DedicatedWorldRuntime.IsWorldReady && DedicatedWorldRuntime.Scene != authoritativeScene)
            { Fail("An unapproved native district transition was detected. Reconnect to restore your location."); return; }
            if (queuedBoatRequest != null) TrySendQueuedBoatRequest();
            if (current == null) return;
            if (now > clientDeadline) { Fail("Your district load timed out. Reconnect to restore your location."); return; }
            try
            {
                if (clientPhase == "loading" && NativeArrivalComplete())
                { clientPhase = "arrived"; Acknowledge(true, true, ""); notice?.Invoke("Arrived; confirming your location."); }
            }
            catch (Exception ex) { Fail("Native arrival failed: " + ex.Message); }
        }
        public static void AcceptTravelContext()
        {
            if (!worker && HasAuthority && !terminal && current == null && DedicatedWorldRuntime.IsWorldReady)
                authoritativeScene = DedicatedWorldRuntime.Scene;
        }
        private static void ClearClient()
        { if (current != null && clientPhase == "arrived") authoritativeScene = current.ToScene; ReleaseBoatRequest(); ClearNativeArrival(); current = null; destination = null; clientPhase = ""; }
        private static void Fail(string reason) { if (terminal) return; terminal = true; ReleaseBoatRequest(); ClearNativeArrival(); notice?.Invoke(reason); fail?.Invoke(reason); }
        private static void ReleaseBoatRequest()
        {
            bool held = pendingBoatRequest.Length != 0 || queuedBoatRequest != null || queuedBoatPose != null;
            pendingBoatRequest = ""; pendingBoatDeadline = 0;
            queuedBoatRequest = null; queuedBoatPose = null;
            if (held) BoatReplication.SetTravelRequestPending(false);
        }
        private static void TrySendQueuedBoatRequest()
        {
            try
            {
                if (queuedBoatPose == null || DedicatedWorldRuntime.Scene != queuedBoatPose.Scene ||
                    BoatReplication.OwnBoatId != queuedBoatPose.BoatId)
                    throw new InvalidOperationException("Your boat departure changed before its position was confirmed.");
                RequireLocalBoatReady(queuedBoatPose.BoatId);
                if (!BoatReplication.IsOwnPoseAcknowledged(queuedBoatPose)) return;
                RequireWorldReady();
                var request = queuedBoatRequest;
                // Issue the correlation identity only when the request is sent.
                // The worker's normal pose admission (including its rate limit)
                // must echo a held pose before geometry is tested for departure.
                request.RequestId = Guid.NewGuid().ToString("N");
                pendingBoatRequest = request.RequestId;
                queuedBoatRequest = null; queuedBoatPose = null;
                sendIntent(request);
                notice?.Invoke("Requesting travel...");
            }
            catch (Exception ex) { ReleaseBoatRequest(); notice?.Invoke(ex.Message); }
        }
        private static void Acknowledge(bool arrival, bool success, string message)
        {
            BoatPose boat = null;
            if (arrival && success && current.IsBoatTravel)
            {
                boat = CaptureBoatArrival?.Invoke();
                BoatProtocol.Validate(boat);
                if (boat.PlayerId != localPlayer || boat.BoatId != current.BoatId || boat.Scene != current.ToScene || !boat.Boarded || !boat.Driving)
                    throw new InvalidOperationException("The arriving personal boat is not ready to confirm.");
            }
            sendIntent?.Invoke(new InteractionRequest { Kind = arrival ? ArrivedKind : PreparedKind, TargetId = current.Epoch,
                RequestId = Guid.NewGuid().ToString("N"), Data = JsonSerializer.SerializeToElement(new GroupTravelAcknowledgement {
                    Success = success, Scene = DedicatedWorldRuntime.Scene, Message = message, Boat = boat }, DedicatedProtocol.Json) });
        }

        private static void PublishStandalone(string phase, string player, string request, string message) => PublishCopy(Standalone(phase, player, request, message));
        private static GroupTravelEvent Standalone(string phase, string player, string request, string message) => new GroupTravelEvent {
            Phase = phase, PlayerId = player, RequestId = request, Message = Cut(message, 160) };
        private static GroupTravelEvent Copy(GroupTravelEvent source, string phase, string player, string message) => new GroupTravelEvent {
            Epoch = source.Epoch, PortalId = source.PortalId, PortalName = source.PortalName, TargetLocationId = source.TargetLocationId,
            FromScene = source.FromScene, ToScene = source.ToScene,
            IsCabTravel = source.IsCabTravel, IsBoatTravel = source.IsBoatTravel, IsMapTravel = source.IsMapTravel, BoatId = source.BoatId,
            RequestId = source.RequestId, Participants = source.Participants, Phase = phase, PlayerId = player, Message = Cut(message, 160) };
        private static void PublishCopy(GroupTravelEvent value)
        {
            value.Sequence = ++sequence; ValidateWire(value);
            var worldEvent = new WorldEvent { Kind = EventKind, TargetPlayerId = value.PlayerId, Data = JsonSerializer.SerializeToElement(value, DedicatedProtocol.Json) };
            if (JsonSerializer.SerializeToUtf8Bytes(worldEvent, DedicatedProtocol.Json).Length > 8192) throw new InvalidDataException("Travel message exceeded its bound.");
            emit?.Invoke(worldEvent);
        }
        private static void Remember(string key, GroupTravelEvent value)
        { if (!completed.ContainsKey(key)) completedOrder.Enqueue(key); completed[key] = value; while (completedOrder.Count > 1024) completed.Remove(completedOrder.Dequeue()); }
        public static void ValidateWire(GroupTravelEvent value)
        {
            if (value == null || value.Sequence < 1 || !Bounded(value.Epoch, 32) || !Bounded(value.PlayerId, 32) || !Bounded(value.RequestId, 32) ||
                !Bounded(value.PortalId, 128) || !Bounded(value.PortalName, 128) || !Bounded(value.TargetLocationId, 128) ||
                !ValidTravelMode(value.IsCabTravel, value.IsBoatTravel, value.IsMapTravel, value.BoatId) ||
                !Bounded(value.FromScene, 128) || !Bounded(value.ToScene, 128) || !Bounded(value.Message, 160) ||
                value.Participants == null || value.Participants.Length > GroupTravelBarrier.MaxParticipants || value.FailedPlayers == null || value.FailedPlayers.Length > GroupTravelBarrier.MaxParticipants)
                throw new InvalidDataException("Malformed travel event.");
            if (value.Phase != "prepare" && value.Phase != "depart" && value.Phase != "complete" && value.Phase != "cancel" && value.Phase != "heartbeat" && value.Phase != "fatal" && value.Phase != "denied" && value.Phase != "accepted")
                throw new InvalidDataException("Unknown travel phase.");
            if (value.PlayerId.Length > 0 && !Guid.TryParseExact(value.PlayerId, "N", out _)) throw new InvalidDataException("Invalid targeted player.");
            bool routed = value.Phase == "prepare" || value.Phase == "depart" || value.Phase == "complete" || value.Phase == "cancel" || value.Phase == "heartbeat";
            if (routed && (!Guid.TryParseExact(value.PlayerId, "N", out _) || value.Participants.Length != 1 || value.Participants[0] != value.PlayerId))
                throw new InvalidDataException("Independent travel must target exactly one player.");
            var unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (string player in value.Participants) if (!Guid.TryParseExact(player, "N", out _) || !unique.Add(player)) throw new InvalidDataException("Invalid travel participant list.");
            var failedUnique = new HashSet<string>(StringComparer.Ordinal);
            foreach (string player in value.FailedPlayers) if (!Guid.TryParseExact(player, "N", out _) || !unique.Contains(player) || !failedUnique.Add(player)) throw new InvalidDataException("Invalid failed-player list.");
            if (value.Phase == "prepare" || value.Phase == "depart" || value.Phase == "complete" || value.Phase == "cancel" || value.Phase == "heartbeat")
                if (!Guid.TryParseExact(value.Epoch, "N", out _) || value.PortalId.Length == 0 || value.PortalName.Length == 0 || value.TargetLocationId.Length == 0 ||
                    value.FromScene.Length == 0 || value.ToScene.Length == 0 || value.FromScene == value.ToScene)
                    throw new InvalidDataException("Invalid district travel destination.");
        }
        public static void ValidateIntent(GroupTravelIntent value)
        {
            if (value == null || !DedicatedProtocol.Text(value.ToScene, 128) || !DedicatedProtocol.Text(value.PortalName, 128) || !DedicatedProtocol.Text(value.TargetLocationId, 128))
                throw new InvalidDataException("The travel request must identify the exact native destination asset.");
            if (!ValidTravelMode(false, value.IsBoatTravel, value.IsMapTravel, value.BoatId))
                throw new InvalidDataException("The travel request has conflicting travel modes or boat identity.");
        }
        private static bool ValidTravelMode(bool cab, bool boat, bool map, string boatId) =>
            (cab ? 1 : 0) + (boat ? 1 : 0) + (map ? 1 : 0) <= 1 && boatId != null &&
            (boat ? Guid.TryParseExact(boatId, "N", out _) : boatId.Length == 0);
        public static void ValidateArrivalBoat(GroupTravelEvent route, InteractionRequest request, GroupTravelAcknowledgement ack)
        {
            if (route == null || request == null || ack == null) throw new InvalidDataException("Missing travel arrival identity.");
            if (!route.IsBoatTravel)
            {
                if (ack.Boat != null) throw new InvalidDataException("A walking arrival cannot move a boat.");
                return;
            }
            BoatProtocol.Validate(ack.Boat);
            if (ack.Boat.BoatId != route.BoatId || ack.Boat.PlayerId != request.PlayerId || ack.Boat.AccountId != request.AccountId ||
                ack.Boat.ContextGeneration != request.ContextGeneration || ack.Boat.Scene != route.ToScene || !ack.Boat.Boarded || !ack.Boat.Driving)
                throw new InvalidDataException("Boat arrival changed its owner, boat, scene or pending map generation.");
        }
        private static bool Bounded(string value, int max) => value != null && value.Length <= max && (value.Length == 0 || DedicatedProtocol.Text(value, max));
        private static string Cut(string value, int max) => string.IsNullOrEmpty(value) ? "" : value.Replace('\r', ' ').Replace('\n', ' ').Substring(0, Math.Min(value.Length, max));

        private static void RequireWorldReady()
        {
            if (!DedicatedWorldRuntime.IsWorldReady || !TravelManager.InstanceNotNull() || !TravelUnlockManager.InstanceNotNull()) throw new InvalidOperationException("The district is still loading.");
            if (!TravelManager.Instance.CanTravel) throw new InvalidOperationException("Native travel is currently locked.");
        }
        private static void RequireLocalTravelReady()
        {
            if (!PlayerManager.InstanceNotNull() || PlayerManager.Instance.LocalPlayer == null || PlayerManager.Instance.LocalPlayer.IsHoldingObject)
                throw new InvalidOperationException("Put down held objects before the travels.");
            var character = PlayerManager.Instance.LocalPlayer.Character;
            if (character == null || character.Controller == null || character.Controller.boardedBoat != null)
                throw new InvalidOperationException("Leave the boat before the travels.");
        }
        // Native portal GUIDs are reused by cab/train assets and even by portals
        // in different districts. Preserve the exact native asset selected by a
        // real departure terminal instead of choosing the first matching GUID.
        private static PortalKey FindPortal(string guid, string scene, string portalName, string targetLocationId)
        {
            if (!DedicatedProtocol.Text(guid, 128) || !DedicatedProtocol.Text(scene, 128) || !DedicatedProtocol.Text(portalName, 128) ||
                !DedicatedProtocol.Text(targetLocationId, 128) || !TravelUnlockManager.InstanceNotNull()) return null;
            var keys = TravelUnlockManager.Instance._allPortalKeys;
            if (keys == null || keys.Length > 8192) return null;
            PortalKey selected = null;
            for (int i = 0; i < keys.Length; i++)
            {
                var key = keys[i];
                if (!SamePortalIdentity(key, guid, scene, portalName, targetLocationId)) continue;
                if (selected != null && selected.Pointer != key.Pointer) throw new InvalidOperationException("The native destination asset is ambiguous.");
                selected = key;
            }
            return selected;
        }
        private static void RequireLocalBoatReady(string boatId)
        {
            if (boatId != BoatReplication.OwnBoatId || BoatReplication.OwnBoatGhost == null || BoatReplication.OwnBoatController == null ||
                !PlayerManager.InstanceNotNull() || PlayerManager.Instance.LocalPlayer == null || PlayerManager.Instance.LocalPlayer.IsHoldingObject ||
                PlayerManager.Instance.LocalPlayer.Character?.Controller?.boardedBoat == null ||
                PlayerManager.Instance.LocalPlayer.Character.Controller.boardedBoat.Pointer != BoatReplication.OwnBoatController.Pointer)
                throw new InvalidOperationException("Board your own boat before using a waterway exit.");
        }
        private static bool SamePortalIdentity(PortalKey key, string guid, string scene, string portalName, string targetLocationId) =>
            key != null && key.SceneIndex >= 0 && key.Apartment == null && key.TargetLocation != null &&
            key.Guid == guid && key.SceneName == scene && key.name == portalName && key.TargetLocation.Guid == targetLocationId;
        private static SceneTransitionInteractable FindTransition(string guid, string scene, string portalName, string targetLocationId, Vector3 position)
        {
            if (!DedicatedProtocol.Text(guid, 128) || !DedicatedProtocol.Text(scene, 128) || !DedicatedProtocol.Text(portalName, 128) ||
                !DedicatedProtocol.Text(targetLocationId, 128)) return null;
            var transitions = Object.FindObjectsOfType<SceneTransitionInteractable>();
            if (transitions.Length > 2048) throw new InvalidOperationException("Too many loaded travel portals.");
            SceneTransitionInteractable selected = null; float nearest = float.PositiveInfinity;
            foreach (var transition in transitions)
            {
                if (transition == null || !transition.isActiveAndEnabled || !transition.gameObject.scene.isLoaded || transition.gameObject.scene.name != DedicatedWorldRuntime.Scene ||
                    !transition.IsActive || !transition.CanInteract() || !SamePortalIdentity(transition.keyTo, guid, scene, portalName, targetLocationId)) continue;
                float distance = (transition.transform.position - position).sqrMagnitude;
                if (!float.IsFinite(distance) || distance > 64) continue;
                if (selected != null && (selected.keyTo.Pointer != transition.keyTo.Pointer || selected.isCabTravel != transition.isCabTravel))
                    throw new InvalidOperationException("The nearby native departure route is ambiguous.");
                if (distance < nearest) { selected = transition; nearest = distance; }
            }
            return selected;
        }
        private static BoatPortal FindBoatPortal(string guid, string scene, string portalName, string targetLocationId, Vector3 position)
        {
            BoatPortal selected = null; float nearest = float.PositiveInfinity;
            var portals = Object.FindObjectsOfType<BoatPortal>();
            if (portals.Length > 2048) throw new InvalidOperationException("Too many loaded waterway exits.");
            foreach (var portal in portals)
            {
                if (portal == null || !portal.isActiveAndEnabled || !portal.gameObject.scene.isLoaded ||
                    portal.gameObject.scene.name != DedicatedWorldRuntime.Scene || !SamePortalIdentity(portal.keyTo, guid, scene, portalName, targetLocationId)) continue;
                float distance = (portal.transform.position - position).sqrMagnitude;
                if (!float.IsFinite(distance)) continue;
                if (selected != null && selected.keyTo.Pointer != portal.keyTo.Pointer)
                    throw new InvalidOperationException("The native waterway route is ambiguous.");
                if (distance < nearest) { selected = portal; nearest = distance; }
            }
            // The worker BoatAuthority validates the actual trigger volume and
            // most recent owned boat pose. Boat exits can be much wider than a
            // pedestrian terminal; the terminal's eight-metre radius is wrong.
            return selected;
        }
        private static void ValidateTransition(SceneTransitionInteractable transition)
        {
            if (transition == null || !transition.isActiveAndEnabled || transition.gameObject.scene.name != DedicatedWorldRuntime.Scene ||
                !transition.IsActive || !transition.CanInteract()) throw new InvalidOperationException("This district terminal is locked or unavailable.");
            ValidateDestination(transition.keyTo);
            if (transition.isCabTravel && !IsNativeRouteFromCurrentLocation(transition.keyTo))
                throw new InvalidOperationException("This cab terminal is not a native route from the current district.");
        }
        private static void ValidateDestination(PortalKey key, string sourceScene = null)
        {
            if (key == null || !DedicatedProtocol.Text(key.Guid, 128) || !DedicatedProtocol.Text(key.name, 128) || !DedicatedProtocol.Text(key.SceneName, 128) ||
                key.TargetLocation == null || !DedicatedProtocol.Text(key.TargetLocation.Guid, 128) || key.SceneIndex < 0 ||
                key.Apartment != null || key.SceneName == (sourceScene ?? DedicatedWorldRuntime.Scene))
                throw new InvalidOperationException("Only travel between district portals is currently supported.");
            var canonical = FindPortal(key.Guid, key.SceneName, key.name, key.TargetLocation.Guid);
            if (canonical == null || canonical.Pointer != key.Pointer || !TravelUnlockManager.Instance.IsLocationUnlocked(key.TargetLocation))
                throw new InvalidOperationException("The party has not unlocked that district portal.");
        }
        private static void ValidatePublicMapDestination(PortalKey key, string sourceScene = null)
        {
            ValidateDestination(key, sourceScene);
            if (!MultiplayerSandboxRuntime.IsPublicTravelDestination(key) || key.TargetLocation.DefaultLocation == null ||
                key.TargetLocation.DefaultLocation.Pointer != key.Pointer)
                throw new InvalidOperationException("Map travel must use the canonical public district arrival.");
        }
        private static bool IsNativeRouteFromCurrentLocation(PortalKey key)
        {
            var location = GameSceneManager.Instance.CurrentWorldLocation;
            var routes = location == null ? null : location.transitions;
            if (routes == null || routes.Count > 512) return false;
            for (int i = 0; i < routes.Count; i++) { var route = routes[i]; if (route != null && route.ToKey != null && route.ToKey.Pointer == key.Pointer) return true; }
            return false;
        }
        private static void ClearNativeArrival()
        {
            nativeArrival = null; nativeArrivalError = ""; authorizedBoatGhost = IntPtr.Zero;
            if (!ReferenceEquals(arrivalEvent, null) && !ReferenceEquals(arrivalListener, null))
            { try { arrivalEvent.RemoveListener(arrivalListener); } catch { /* discarded epoch cannot affect a later session */ } }
            arrivalEvent = null; arrivalListener = null;
        }
        private static bool NativeArrivalComplete()
        {
            if (nativeArrivalError.Length > 0) throw new InvalidOperationException(nativeArrivalError);
            if (nativeArrival == null || current == null || nativeArrival.Epoch != current.Epoch || !TravelManager.InstanceNotNull()) return false;
            var manager = TravelManager.Instance;
            bool ready = DedicatedWorldRuntime.IsWorldReady;
            string scene = DedicatedWorldRuntime.Scene;
            if (current.IsCabTravel && scene == current.ToScene)
            {
                var portal = manager.GetTravelPortal(destination);
                var cinematic = portal == null ? null : portal.Cinematic;
                if (cinematic != null && !GroupTravelNativeArrival.IsBoundedCinematic(cinematic.duration))
                    throw new InvalidOperationException("The native cab cinematic exceeds the supported 60-second duration.");
            }
            return nativeArrival.IsComplete(ready, scene, !ReferenceEquals(manager._timePauseLock, null), !ReferenceEquals(manager._preventShortcutBool, null)) &&
                (!current.IsBoatTravel || BoatReplication.IsNativeTravelComplete(current.BoatId, destination));
        }
        private static void InvokeNative(PortalKey key, bool cab)
        {
            if (current == null || destination == null || !SamePortalIdentity(key, current.PortalId, current.ToScene, current.PortalName, current.TargetLocationId) ||
                key.Pointer != destination.Pointer || cab != current.IsCabTravel)
                throw new InvalidOperationException("Travel portal changed before departure.");
            ClearNativeArrival();
            var progress = new GroupTravelNativeArrival(current.Epoch, key.Guid, key.SceneName, key.TargetLocation.Guid, cab, current.IsBoatTravel);
            nativeArrival = progress;
            arrivalEvent = TravelManager.OnAreaArrival;
            if (ReferenceEquals(arrivalEvent, null)) throw new InvalidOperationException("Native travel arrival event is unavailable.");
            arrivalListener = (Il2CppSystem.Action<WorldLocation>)(Action<WorldLocation>)(location => {
                if (!HasAuthority || terminal || !ReferenceEquals(nativeArrival, progress) || location == null) return;
                try { progress.Observe(progress.Epoch, location.SceneName, location.Guid); }
                catch (Exception ex) { nativeArrivalError = "Native arrival observation failed: " + Cut(ex.Message, 120); }
            });
            arrivalEvent.AddListener(arrivalListener);
            nativeCallDepth++;
            try
            {
                if (current.IsBoatTravel)
                {
                    ReleaseBoatRequest();
                    RequireLocalBoatReady(current.BoatId);
                    authorizedBoatGhost = BoatReplication.OwnBoatGhost.Pointer;
                    BoatReplication.ExecuteAuthorizedTravel(BoatReplication.OwnBoatController, key);
                }
                else
                {
                    if (current.IsMapTravel)
                    {
                        var character = PlayerManager.Instance.LocalPlayer.Character;
                        character.Initialize();
                        var ghost = character.MyGhost;
                        if (ghost != null && ghost.Sitting) ghost.GetUpFromChair();
                    }
                    TravelManager.Instance.RequestTravel(key, cab, false, null);
                }
            }
            finally { nativeCallDepth--; }
        }
        private static void InstallPatches()
        {
            var harmony = new Harmony("local.nivalis.together.party-travel");
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            var three = typeof(TravelManager).GetMethod("RequestTravel", flags, null, new[] { typeof(PortalKey), typeof(bool), typeof(bool) }, null);
            var four = typeof(TravelManager).GetMethod("RequestTravel", flags, null, new[] { typeof(PortalKey), typeof(bool), typeof(bool), typeof(Nivalis.Boat.BoatGhost) }, null);
            var interaction = typeof(SceneTransitionInteractable).GetMethod("DoInteraction", flags);
            var mapTravel = typeof(MapTileMenu).GetMethod("RequestTravel", flags, null,
                new[] { typeof(PortalKey), typeof(bool), typeof(bool), typeof(Nivalis.Boat.BoatGhost) }, null);
            var loadArea = typeof(GameSceneManager).GetMethod("LoadArea", flags, null,
                new[] { typeof(string), typeof(string), typeof(Il2CppSystem.Action), typeof(bool), typeof(bool), typeof(bool), typeof(bool) }, null);
            if (three == null || four == null || interaction == null || mapTravel == null || loadArea == null) throw new MissingMethodException("Native travel API differs from the inspected build.");
            harmony.Patch(three, prefix: new HarmonyMethod(typeof(TravelReplication), nameof(RequestThreePrefix)));
            harmony.Patch(four, prefix: new HarmonyMethod(typeof(TravelReplication), nameof(RequestFourPrefix)));
            harmony.Patch(interaction, prefix: new HarmonyMethod(typeof(TravelReplication), nameof(TransitionPrefix)));
            harmony.Patch(mapTravel, prefix: new HarmonyMethod(typeof(TravelReplication), nameof(MapTravelPrefix)));
            harmony.Patch(loadArea, prefix: new HarmonyMethod(typeof(TravelReplication), nameof(LoadAreaPrefix)));
            patched = true;
        }
        private static bool LoadAreaPrefix(string __0, string __1, bool __3, bool __6)
        {
            if (!HasAuthority) return true;
            if (WorkerSceneResidency.AllowNativeLoad(__0, __1, __3, __6)) return true;
            if (__3 && (!worker || terminal)) return true;
            if (!worker && !terminal && !__6 && current != null && __0 == current.ToScene && __1 == current.FromScene && clientPhase == "loading") return true;
            if (worker && !terminal) Fail("A native worker scene transition bypassed residency authority; stop before another checkpoint.");
            else if (!worker) notice?.Invoke("Use an authorized district portal to travel.");
            return false;
        }
        private static bool TransitionPrefix(SceneTransitionInteractable __instance)
        {
            if (!HasAuthority || worker || nativeCallDepth > 0) return true;
            if (terminal) return false;
            try { RequireWorldReady(); RequireLocalTravelReady(); ValidateTransition(__instance); RequestClient(__instance.keyTo); }
            catch (Exception ex) { notice?.Invoke(ex.Message); }
            return false;
        }
        private static bool RequestThreePrefix(PortalKey __0, bool __1, bool __2) => InterceptNative(__0, __1, __2, null, false);
        private static bool RequestFourPrefix(PortalKey __0, bool __1, bool __2, Nivalis.Boat.BoatGhost __3) => InterceptNative(__0, __1, __2, __3, true);
        private static bool InterceptNative(PortalKey key, bool cab, bool force, Nivalis.Boat.BoatGhost boat, bool finalOverload)
        {
            if (!HasAuthority) return true;
            if (WorkerSceneResidency.AllowNativeTravel(key, cab, force, boat)) return true;
            if (terminal) return false;
            if (worker) { Fail("A native worker travel request bypassed residency authority."); return false; }
            try
            {
                if (force) throw new InvalidOperationException("Forced travel is unavailable during multiplayer.");
                // RequestTravel may resume from its own native UI callback on a
                // later frame. Authorize only the exact route/flag and at most one
                // post-load continuation, rather than bypassing every nested call.
                if (current != null && key != null && nativeArrival != null &&
                    clientPhase == "loading")
                {
                    var sequence = current.IsBoatTravel ? BoatPortal.sequence : null;
                    bool nativeBoatCall = nativeArrival.MatchesNativeBoatArgument(authorizedBoatGhost, current.BoatId,
                        boat?.Pointer ?? IntPtr.Zero, boat?.Id ?? "", sequence?.boatGhost?.Pointer ?? IntPtr.Zero,
                        sequence?.boatGhost?.Id ?? "", destination?.Pointer ?? IntPtr.Zero, sequence?.destination?.Pointer ?? IntPtr.Zero);
                    if (destination == null || key.Pointer != destination.Pointer ||
                        !SamePortalIdentity(key, current.PortalId, current.ToScene, current.PortalName, current.TargetLocationId) ||
                        !nativeArrival.Matches(current.Epoch, key.Guid, key.SceneName, cab, force, current.IsBoatTravel) || !nativeBoatCall)
                        throw new InvalidOperationException("A native travel callback changed the authorized route.");
                    bool boatSequence = current.IsBoatTravel && nativeBoatCall;
                    if ((nativeCallDepth > 0 || boatSequence) && nativeArrival.AuthorizeInitial(current.Epoch, key.Guid, key.SceneName, cab, force, current.IsBoatTravel, finalOverload)) return true;
                    if (DedicatedWorldRuntime.Scene != current.ToScene ||
                        !nativeArrival.AuthorizeContinuation(current.Epoch, key.Guid, key.SceneName, cab, force, current.IsBoatTravel, finalOverload))
                        throw new InvalidOperationException("An unexpected or repeated native travel continuation was rejected.");
                    return true;
                }
                if (boat != null) throw new InvalidOperationException("Use your boat's authorized waterway exit.");
                if (IsBusy) throw new InvalidOperationException("Your character is already traveling.");
                RequireWorldReady(); ValidateDestination(key);
                RequireLocalTravelReady(); var player = PlayerManager.Instance.LocalPlayer.Character.transform.position;
                var transition = FindTransition(key.Guid, key.SceneName, key.name, key.TargetLocation.Guid, player);
                if (transition == null) throw new InvalidOperationException("Use a nearby district portal to travel.");
                if (transition.isCabTravel != cab) throw new InvalidOperationException("Use the native terminal for this travel route.");
                ValidateTransition(transition); RequestClient(key);
            }
            catch (Exception ex)
            {
                if (worker)
                {
                    Fail("Unsupported authoritative native travel: " + ex.Message);
                }
                else notice?.Invoke(ex.Message);
            }
            return false;
        }
        public static void RequestBoatTravel(Nivalis.Boat.BoatController boat, PortalKey key)
        {
            if (!HasAuthority || worker || terminal || pendingBoatRequest.Length != 0 || queuedBoatRequest != null) return;
            try
            {
                RequireWorldReady(); RequireLocalBoatReady(BoatReplication.OwnBoatId); ValidateDestination(key);
                if (boat == null || boat.Pointer != BoatReplication.OwnBoatController.Pointer)
                    throw new InvalidOperationException("You can travel only in your own boat.");
                var portal = FindBoatPortal(key.Guid, key.SceneName, key.name, key.TargetLocation.Guid, boat.transform.position);
                if (portal == null || portal.keyTo.Pointer != key.Pointer) throw new InvalidOperationException("Use a native waterway exit.");
                RequestClient(key, false, BoatReplication.OwnBoatId);
            }
            catch (Exception ex) { ReleaseBoatRequest(); notice?.Invoke(ex.Message); }
        }
        private static bool MapTravelPrefix(PortalKey __0, bool __1, bool __2, Nivalis.Boat.BoatGhost __3)
        {
            if (!HasAuthority) return true;
            if (worker || terminal) return false;
            try
            {
                if (__1 || __2 || __3 != null) throw new InvalidOperationException("Select the public district arrival for map travel.");
                RequireWorldReady(); RequireLocalTravelReady(); ValidatePublicMapDestination(__0);
                RequestClient(__0, true);
            }
            catch (Exception ex) { notice?.Invoke(ex.Message); }
            return false;
        }
        private static void RequestClient(PortalKey key, bool map = false, string boatId = "")
        {
            if (IsBusy || pendingBoatRequest.Length != 0 || queuedBoatRequest != null)
                throw new InvalidOperationException("Wait for your current travel to finish.");
            if (now < nextRequest) return;
            if (sendIntent == null) throw new InvalidOperationException("The shared-world connection is unavailable.");
            nextRequest = now + 1;
            var request = new InteractionRequest { Kind = RequestKind, TargetId = key.Guid,
                Data = JsonSerializer.SerializeToElement(new GroupTravelIntent { ToScene = key.SceneName, PortalName = key.name,
                    TargetLocationId = key.TargetLocation.Guid, IsMapTravel = map, IsBoatTravel = boatId.Length > 0, BoatId = boatId }, DedicatedProtocol.Json) };
            if (boatId.Length > 0)
            {
                pendingBoatDeadline = now + 120; queuedBoatRequest = request;
                BoatReplication.SetTravelRequestPending(true);
                queuedBoatPose = BoatReplication.PublishOwnPose();
                notice?.Invoke("Preparing boat travel...");
                return;
            }
            request.RequestId = Guid.NewGuid().ToString("N");
            sendIntent(request);
            notice?.Invoke("Requesting travel...");
        }
    }
}
