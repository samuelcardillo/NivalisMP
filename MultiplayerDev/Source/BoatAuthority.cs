using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Nivalis;
using UnityEngine;

namespace NivalisTogether.Dedicated
{
    /// <summary>Worker-owned account boats. Native scenes supply dock geometry and prices, never ownership.</summary>
    public static class BoatAuthority
    {
        private sealed class Player
        {
            internal string AccountId;
            internal PlayerPose Pose;
            internal double PoseTime;
            internal BoatPose BoatPose;
            internal double BoatTime;
            internal long Sequence;
            internal long RequiredFuelRevision;
        }
        private sealed class Journey
        {
            internal string BoatId, Destination;
            internal IntPtr Portal;
            internal PlayerBoatState Source;
            internal double Started;
        }
        private sealed class Reply { internal string Fingerprint; internal WorldEvent Event; }
        private static readonly Dictionary<string, Player> Players = new Dictionary<string, Player>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Journey> Journeys = new Dictionary<string, Journey>(StringComparer.Ordinal);
        private static readonly Dictionary<string, PlayerBoatState> LiveBoats = new Dictionary<string, PlayerBoatState>(StringComparer.Ordinal);
        private static readonly Dictionary<string, PlayerBoatState> BeforeCommit = new Dictionary<string, PlayerBoatState>(StringComparer.Ordinal);
        private static readonly Dictionary<string, long> RequiredRevisions = new Dictionary<string, long>(StringComparer.Ordinal);
        private static readonly Dictionary<string, BoatDockDescription> Placements = new Dictionary<string, BoatDockDescription>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Reply> Replies = new Dictionary<string, Reply>(StringComparer.Ordinal);
        private static readonly Queue<string> ReplyOrder = new Queue<string>();
        private static Action<WorldEvent> emit;
        private static Action<string, string> reject;
        private static long sequence;
        private static double nextPublish;
        private static bool dirty;
        // These are broad transport sanity limits; native boat controls still govern actual acceleration and fuel consumption.
        private const double PoseFreshness = 3, MaxBoatSpeed = 50, MinimumPoseInterval = 0.045;
        private const float DockPlacementRadius = 24, BoardingRadius = 24;

        public static void Configure(Action<WorldEvent> emitWorldEvent, Action<string, string> rejectPlayer)
        {
            Reset(); emit = emitWorldEvent ?? throw new ArgumentNullException(nameof(emitWorldEvent));
            reject = rejectPlayer ?? throw new ArgumentNullException(nameof(rejectPlayer));
        }
        public static void Reset()
        {
            Players.Clear(); Journeys.Clear(); LiveBoats.Clear(); BeforeCommit.Clear(); RequiredRevisions.Clear(); Placements.Clear(); Replies.Clear(); ReplyOrder.Clear();
            emit = null; reject = null; sequence = 0; nextPublish = 0; dirty = false;
        }
        public static PlayerBoatState EnsureAccountBoat(string accountId)
        {
            RequireWorker();
            return GetLiveBoat(accountId) ?? PlayerAccountRuntime.EnsureBoat(accountId, BoatReplication.CreateStartingState(accountId));
        }
        public static void SetPlayerPresent(PlayerPresence presence)
        {
            RequireWorker();
            if (presence == null || !Guid.TryParseExact(presence.PlayerId, "N", out _) || !Guid.TryParseExact(presence.AccountId, "N", out _))
                throw new InvalidDataException("Invalid boat participant identity.");
            if (Players.TryGetValue(presence.PlayerId, out var existing) && existing.AccountId != presence.AccountId)
                throw new InvalidDataException("Boat participant changed accounts.");
            if (!presence.Joined)
            {
                if (existing == null) return;
                // Reconnection must restore an at-sea character aboard its own
                // boat. Only an observed disembark changes the persisted flags.
                FlushAccount(existing.AccountId);
                Players.Remove(presence.PlayerId); Journeys.Remove(presence.PlayerId); dirty = true; return;
            }
            if (existing != null) return;
            if (Players.Count >= BoatProtocol.MaxBoats) throw new InvalidOperationException("The active boat participant limit was reached.");
            foreach (var player in Players.Values) if (player.AccountId == presence.AccountId)
                throw new InvalidOperationException("This account already has an active boat participant.");
            EnsureAccountBoat(presence.AccountId);
            Players.Add(presence.PlayerId, new Player { AccountId = presence.AccountId }); dirty = true;
        }
        public static void UpdatePlayerPose(PlayerPose pose, double timestamp)
        {
            RequireWorker(); RequireTime(timestamp);
            if (pose == null || !Players.TryGetValue(pose.PlayerId ?? "", out var player) || pose.AccountId != player.AccountId ||
                !DedicatedProtocol.PoseValid(pose)) return;
            var context = PlayerMapRuntime.GetContext(pose.PlayerId);
            if (!Matches(context, pose.Scene, pose.ContextGeneration) || pose.PropertyId != "" || !pose.Active) return;
            player.Pose = new PlayerPose { PlayerId = pose.PlayerId, AccountId = pose.AccountId, Scene = pose.Scene,
                PropertyId = "", ContextGeneration = pose.ContextGeneration, X = pose.X, Y = pose.Y, Z = pose.Z, Yaw = pose.Yaw, Active = true };
            player.PoseTime = timestamp;
        }
        public static bool AcceptPose(BoatPose pose, double timestamp)
        {
            RequireWorker(); RequireTime(timestamp);
            if (!BoatProtocol.PoseValid(pose) || !Players.TryGetValue(pose.PlayerId, out var player) || pose.AccountId != player.AccountId ||
                pose.Sequence <= player.Sequence || Journeys.ContainsKey(pose.PlayerId)) return false;
            var context = PlayerMapRuntime.GetContext(pose.PlayerId);
            if (!Matches(context, pose.Scene, pose.ContextGeneration)) return false;
            var boat = GetLiveBoat(player.AccountId);
            if (boat == null || boat.BoatId != pose.BoatId || !boat.Unlocked || boat.Scene != pose.Scene || BeforeCommit.ContainsKey(boat.BoatId)) return false;
            // Durable publication is earlier than native client application.
            // Reject pre-refuel poses still in flight after its checkpoint so
            // their old lower fuel cannot erase a purchase already charged.
            if (pose.AppliedFuelRevision < player.RequiredFuelRevision || pose.AppliedFuelRevision > boat.Revision) return false;
            double elapsed = player.BoatPose == null ? 0.25 : timestamp - player.BoatTime;
            if (elapsed < MinimumPoseInterval || pose.Fuel > boat.Fuel + 0.001f || !FreshActor(player, context, timestamp)) return false;
            if (pose.Boarded && DistanceSquared(player.Pose.X, player.Pose.Y, player.Pose.Z, pose.X, pose.Y, pose.Z) > BoardingRadius * BoardingRadius) return false;
            double moved = DistanceSquared(boat.X, boat.Y, boat.Z, pose.X, pose.Y, pose.Z);
            if (boat.DockId.Length != 0)
            {
                if (Placements.TryGetValue(boat.BoatId, out var placement))
                {
                    // Recall animates from a point outside the berth. That
                    // transient pose must not consume the placement grant and
                    // strand all later poses behind the docked movement guard.
                    // Native Dock teleports to this exact authored transform.
                    if (placement.DockId != boat.DockId || placement.Scene != boat.Scene ||
                        DistanceSquared(placement.X, placement.Y, placement.Z, pose.X, pose.Y, pose.Z) > 1 ||
                        Math.Abs(placement.Qx * pose.Qx + placement.Qy * pose.Qy + placement.Qz * pose.Qz + placement.Qw * pose.Qw) < 0.98) return false;
                }
                else if (moved > 1 || Math.Abs(boat.Qx * pose.Qx + boat.Qy * pose.Qy + boat.Qz * pose.Qz + boat.Qw * pose.Qw) < 0.98) return false;
            }
            else
            {
                double speed = pose.Driving || boat.PlayerDriving ? MaxBoatSpeed : 4;
                double maximum = 2 + speed * Math.Min(elapsed, 2);
                if (moved > maximum * maximum || (pose.Driving && (!pose.Boarded || boat.Fuel <= 0))) return false;
                // Boarding is proximity to the last authoritative boat position, not a proposed moved position.
                if (pose.Boarded && !boat.PlayerAboard && DistanceSquared(player.Pose.X, player.Pose.Y, player.Pose.Z,
                    boat.X, boat.Y, boat.Z) > BoardingRadius * BoardingRadius) return false;
            }
            var next = ApplyPose(boat, pose); next.Fuel = Math.Min(boat.Fuel, pose.Fuel);
            if (!Same(boat, next)) LiveBoats[player.AccountId] = next;
            Placements.Remove(boat.BoatId); player.BoatPose = BoatProtocol.Copy(pose); player.BoatPose.Fuel = next.Fuel;
            player.BoatTime = timestamp; player.Sequence = pose.Sequence; dirty = true; return true;
        }
        public static bool Handles(InteractionRequest request) => request != null && Handles(request.Kind);
        public static bool Handles(string kind) => kind == "boat-recall" || kind == "boat-dock" || kind == "boat-undock" || kind == "boat-refuel";
        public static WorldEvent Handle(InteractionRequest request, double timestamp)
        {
            RequireWorker(); RequireTime(timestamp);
            bool mutationStarted = false;
            try { return Execute(request, timestamp, ref mutationStarted); }
            catch (Exception ex) when (!mutationStarted && (ex is InvalidOperationException || ex is InvalidDataException || ex is JsonException))
            {
                if (request == null || !Guid.TryParseExact(request.PlayerId, "N", out _) || !Players.ContainsKey(request.PlayerId)) throw;
                var denied = Capture(request.PlayerId, null);
                denied.Success = false; denied.RequestId = Guid.TryParseExact(request.RequestId, "N", out _) ? request.RequestId : "";
                // Denials must address the authenticated owner's control state,
                // even when the rejected payload names another account's boat.
                denied.BoatId = GetLiveBoat(Players[request.PlayerId].AccountId)?.BoatId ?? "";
                denied.Operation = Handles(request) ? request.Kind.Substring(5) : "denied";
                denied.Message = ex.Message.Length > 240 ? ex.Message.Substring(0, 240) : ex.Message;
                return Event(denied);
            }
        }
        private static WorldEvent Execute(InteractionRequest request, double timestamp, ref bool mutationStarted)
        {
            if (!Handles(request)) throw new InvalidOperationException("Unknown boat command.");
            if (!Guid.TryParseExact(request.RequestId, "N", out _)) throw new InvalidDataException("Invalid boat request identity.");
            string key = request.PlayerId + ":" + request.RequestId;
            string fingerprint = request.AccountId + "|" + request.Kind + "|" + request.TargetId + "|" + request.Data.GetRawText();
            if (Replies.TryGetValue(key, out var replay))
            {
                if (replay.Fingerprint != fingerprint) throw new InvalidDataException("A boat request changed after execution.");
                return replay.Event;
            }
            var player = RequirePlayer(request.PlayerId, request.AccountId);
            var context = RequireContext(request.PlayerId, request.ContextGeneration);
            if (!FreshActor(player, context, timestamp)) throw new InvalidOperationException("Stand near the dock until your position is synchronized.");
            var intent = request.Data.Deserialize<BoatInteractionIntent>(DedicatedProtocol.Json); BoatProtocol.Validate(intent);
            if (intent.DockId.Length == 0 || intent.DockId != request.TargetId) throw new InvalidDataException("The boat command does not match its dock.");
            var boat = GetLiveBoat(player.AccountId);
            if (boat == null || boat.BoatId != intent.BoatId || !boat.Unlocked) throw new InvalidOperationException("This boat belongs to another account.");
            if (BeforeCommit.ContainsKey(boat.BoatId) || Journeys.ContainsKey(request.PlayerId)) throw new InvalidOperationException("Wait for your previous boat operation.");
            var dock = BoatReplication.DescribeDock(intent.DockId); ValidateDock(dock, context.Scene);
            bool nearControl = DistanceSquared(player.Pose.X, player.Pose.Y, player.Pose.Z, dock.InteractionX, dock.InteractionY, dock.InteractionZ) <= 144;
            bool nearBoat = boat.Scene == context.Scene && DistanceSquared(player.Pose.X, player.Pose.Y, player.Pose.Z, boat.X, boat.Y, boat.Z) <= BoardingRadius * BoardingRadius;
            var next = BoatProtocol.Copy(boat); int cost = 0;
            if (request.Kind == "boat-recall")
            {
                if (!nearControl || boat.PlayerAboard || boat.PlayerDriving) throw new InvalidOperationException("Leave your boat and use the dock's recall control.");
                RequireDockAvailable(dock, boat.BoatId); Place(next, dock);
            }
            else if (request.Kind == "boat-dock")
            {
                if (!nearBoat || boat.Scene != dock.Scene || boat.DockId.Length != 0 ||
                    DistanceSquared(boat.X, boat.Y, boat.Z, dock.X, dock.Y, dock.Z) > DockPlacementRadius * DockPlacementRadius)
                    throw new InvalidOperationException("Bring your own undocked boat alongside this dock.");
                RequireDockAvailable(dock, boat.BoatId); Place(next, dock);
            }
            else
            {
                if (boat.Scene != dock.Scene || boat.DockId != dock.DockId || (!nearControl && !nearBoat))
                    throw new InvalidOperationException("Your boat must be at this dock before using it.");
                if (request.Kind == "boat-undock") next.DockId = "";
                else
                {
                    if (!float.IsFinite(dock.FuelPricePerLiter) || dock.FuelPricePerLiter <= 0 ||
                        !float.IsFinite(dock.FuelCapacity) || dock.FuelCapacity <= 0 || dock.FuelCapacity < boat.Fuel)
                        throw new InvalidOperationException("This dock has no valid fuel offer.");
                    double total = Math.Floor((dock.FuelCapacity - boat.Fuel) * dock.FuelPricePerLiter);
                    if (!double.IsFinite(total) || total < 0 || total > int.MaxValue) throw new InvalidOperationException("The native fuel price is out of range.");
                    cost = (int)total; next.Fuel = dock.FuelCapacity;
                }
            }
            BoatProtocol.Validate(next);
            if (request.Kind == "boat-refuel" && PlayerAccountRuntime.CurrentAccountId != player.AccountId)
                throw new InvalidOperationException("Refueling requires the owner's active account transaction.");
            var inventory = request.Kind == "boat-refuel" ? PlayerManager.Instance.LocalPlayer.Inventory : null;
            int oldMoney = inventory?.Money ?? 0;
            if (cost > oldMoney) throw new InvalidOperationException("Your wallet has insufficient funds for this fuel.");
            BeforeCommit.Add(boat.BoatId, BoatProtocol.Copy(boat));
            PlayerBoatState saved;
            try
            {
                mutationStarted = true;
                if (cost > 0)
                {
                    inventory.TakeMoney(cost);
                    if (inventory.Money != oldMoney - cost) throw new InvalidOperationException("The native wallet did not complete refueling.");
                }
                saved = PlayerAccountRuntime.SetBoat(player.AccountId, next);
                LiveBoats.Remove(player.AccountId);
                RequiredRevisions[boat.BoatId] = saved.Revision;
                if (request.Kind == "boat-refuel") player.RequiredFuelRevision = saved.Revision;
            }
            catch
            {
                if (inventory != null) inventory._money = oldMoney;
                BeforeCommit.Remove(boat.BoatId); throw;
            }
            if (request.Kind == "boat-recall" || request.Kind == "boat-dock") Placements[boat.BoatId] = dock;
            else Placements.Remove(boat.BoatId);
            player.BoatPose = null; dirty = true;
            var result = Capture(request.PlayerId, saved);
            result.RequestId = request.RequestId; result.Operation = request.Kind.Substring(5); result.BoatId = boat.BoatId;
            result.Message = cost > 0 ? "Your boat has been refueled." : "Your boat operation completed.";
            var worldEvent = Event(result); Replies[key] = new Reply { Fingerprint = fingerprint, Event = worldEvent }; ReplyOrder.Enqueue(key);
            while (ReplyOrder.Count > 1024) Replies.Remove(ReplyOrder.Dequeue());
            return worldEvent;
        }
        public static void MarkCommitted(AccountWorldState ledger)
        {
            RequireWorker();
            if (ledger?.Accounts == null) throw new ArgumentNullException(nameof(ledger));
            foreach (var account in ledger.Accounts)
            {
                var boat = account.Boat;
                if (boat != null && RequiredRevisions.TryGetValue(boat.BoatId, out long required) && boat.Revision >= required &&
                    BeforeCommit.TryGetValue(boat.BoatId, out var previous) && previous.OwnerAccountId == account.AccountId)
                { BeforeCommit.Remove(boat.BoatId); RequiredRevisions.Remove(boat.BoatId); dirty = true; nextPublish = 0; }
            }
        }
        public static void FlushPositions()
        {
            RequireWorker();
            foreach (string account in new List<string>(LiveBoats.Keys)) FlushAccount(account);
        }
        private static void FlushAccount(string account)
        {
            if (!LiveBoats.TryGetValue(account, out var live)) return;
            var saved = PlayerAccountRuntime.GetBoat(account);
            if (saved == null || !Same(saved, live)) PlayerAccountRuntime.SetBoat(account, live);
            LiveBoats.Remove(account);
        }
        public static void Tick(double timestamp)
        {
            RequireWorker(); RequireTime(timestamp);
            if (!dirty || timestamp < nextPublish) return;
            nextPublish = timestamp + 0.2; dirty = false; emit(CaptureSnapshot());
        }
        public static WorldEvent CaptureSnapshot(string targetPlayerId = "")
        { RequireWorker(); return Event(Capture(targetPlayerId, null)); }
        public static PlayerBoatState ValidateTravel(string playerId, string boatId, BoatPortal portal, double timestamp)
        {
            RequireWorker(); RequireTime(timestamp); var player = RequirePlayer(playerId, null);
            if (portal == null || !portal.isActiveAndEnabled || !portal.gameObject.scene.isLoaded || portal.keyTo == null ||
                portal.keyTo.Apartment != null || !portal.IsTravelAllowed) throw new InvalidOperationException("This boat passage is not available.");
            if (Journeys.TryGetValue(playerId, out var journey))
            {
                if (journey.BoatId != boatId || journey.Portal != portal.Pointer || journey.Destination != portal.keyTo.SceneName || timestamp - journey.Started > 120)
                    throw new InvalidOperationException("A different boat passage is already pending.");
                return BoatProtocol.Copy(journey.Source);
            }
            var context = PlayerMapRuntime.GetContext(playerId);
            if (context == null || !Matches(context, portal.gameObject.scene.name, context.Generation) || !FreshActor(player, context, timestamp) ||
                player.BoatPose == null || timestamp < player.BoatTime || timestamp - player.BoatTime > PoseFreshness ||
                !player.BoatPose.Boarded || !player.BoatPose.Driving)
                throw new InvalidOperationException("Drive your own boat into the passage and wait for its position to synchronize.");
            var boat = GetLiveBoat(player.AccountId);
            if (boat == null || boat.BoatId != boatId || boat.Scene != context.Scene || boat.DockId.Length != 0 ||
                !boat.PlayerAboard || !boat.PlayerDriving || BeforeCommit.ContainsKey(boatId))
                throw new InvalidOperationException("Only your own undocked boat can use this passage.");
            var contact = BoatReplication.DescribePortalContact(boat, portal);
            bool touches = false;
            if (contact?.PortalBox != null && contact.HullBoxes != null && contact.HullBoxes.Length > 0 && contact.HullBoxes.Length <= 512)
                foreach (var hull in contact.HullBoxes)
                    touches |= BoatTriggerGeometry.Overlaps(contact.PortalBox, hull);
            if (!touches)
                throw new InvalidOperationException("Your boat is outside this passage's native trigger.");
            Journeys.Add(playerId, new Journey { BoatId = boatId, Destination = portal.keyTo.SceneName, Portal = portal.Pointer,
                Source = BoatProtocol.Copy(boat), Started = timestamp });
            return boat;
        }
        public static void CancelTravel(string playerId) => Journeys.Remove(playerId ?? "");
        public static void CommitTravel(string playerId, BoatPose arrival, string expectedScene)
        {
            RequireWorker(); BoatProtocol.Validate(arrival); var player = RequirePlayer(playerId, arrival.AccountId);
            var context = PlayerMapRuntime.GetContext(playerId);
            if (!Journeys.TryGetValue(playerId, out var journey) || arrival.PlayerId != playerId || arrival.BoatId != journey.BoatId ||
                expectedScene != journey.Destination || arrival.Scene != expectedScene || context == null || !context.TravelPending ||
                arrival.ContextGeneration != context.Generation || !arrival.Boarded || !arrival.Driving || arrival.Sequence <= player.Sequence)
                throw new InvalidOperationException("Boat arrival does not match its authorized passage.");
            var boat = GetLiveBoat(player.AccountId);
            if (boat == null || boat.BoatId != journey.BoatId || boat.PrefabId != journey.Source.PrefabId ||
                arrival.AppliedFuelRevision < player.RequiredFuelRevision || arrival.AppliedFuelRevision > boat.Revision ||
                arrival.Fuel > Math.Min(boat.Fuel, journey.Source.Fuel) + 0.001f)
                throw new InvalidOperationException("Boat arrival changed its ownership, prefab, or fuel allocation.");
            var next = ApplyPose(boat, arrival); next.Scene = expectedScene; next.DockId = ""; next.Fuel = Math.Min(boat.Fuel, arrival.Fuel);
            PlayerAccountRuntime.SetBoat(player.AccountId, next); LiveBoats.Remove(player.AccountId); Journeys.Remove(playerId); Placements.Remove(boat.BoatId);
            player.Sequence = arrival.Sequence; player.BoatPose = null; dirty = true;
            // The caller publishes settled player context and travel-complete
            // immediately after this method returns. Publish the matching boat
            // first on that ordered stream: the periodic Tick can be suspended
            // by an older checkpoint still awaiting its acknowledgement.
            // This is the existing live snapshot channel, not a durable action
            // receipt; the account checkpoint retains its normal commit rules.
            emit(CaptureSnapshot());
        }
        private static BoatWorldState Capture(string target, PlayerBoatState overrideBoat)
        {
            if (target.Length != 0 && !Players.ContainsKey(target)) throw new InvalidOperationException("Boat snapshot target is not present.");
            var boats = new List<PlayerBoatState>(); var poses = new List<BoatPose>();
            foreach (var pair in Players)
            {
                var boat = GetLiveBoat(pair.Value.AccountId); if (boat == null) continue;
                if (overrideBoat?.BoatId == boat.BoatId) boat = BoatProtocol.Copy(overrideBoat);
                else if (BeforeCommit.TryGetValue(boat.BoatId, out var previous)) boat = BoatProtocol.Copy(previous);
                boats.Add(boat);
                if (!BeforeCommit.ContainsKey(boat.BoatId) && pair.Value.BoatPose != null && pair.Value.BoatPose.Scene == boat.Scene && !Journeys.ContainsKey(pair.Key))
                    poses.Add(BoatProtocol.Copy(pair.Value.BoatPose));
            }
            boats.Sort((a, b) => string.CompareOrdinal(a.BoatId, b.BoatId));
            return new BoatWorldState { Sequence = checked(++sequence), TargetPlayerId = target, Boats = boats.ToArray(), RuntimePoses = poses.ToArray() };
        }
        private static WorldEvent Event(BoatWorldState state)
        { BoatProtocol.Validate(state); return new WorldEvent { Kind = BoatProtocol.EventKind, TargetPlayerId = state.TargetPlayerId, Data = JsonSerializer.SerializeToElement(state, DedicatedProtocol.Json) }; }
        private static PlayerBoatState GetLiveBoat(string account) => LiveBoats.TryGetValue(account, out var value)
            ? BoatProtocol.Copy(value) : PlayerAccountRuntime.GetBoat(account);
        private static void RequireDockAvailable(BoatDockDescription dock, string boatId)
        {
            foreach (var boat in PlayerAccountRuntime.ListBoats())
                if (boat.BoatId != boatId && boat.Scene == dock.Scene && boat.DockId == dock.DockId)
                    throw new InvalidOperationException("This dock is reserved by another account's boat.");
            foreach (var boat in BeforeCommit.Values)
                if (boat.BoatId != boatId && boat.Scene == dock.Scene && boat.DockId == dock.DockId)
                    throw new InvalidOperationException("This dock is still reserved while another boat operation commits.");
        }
        private static void ValidateDock(BoatDockDescription dock, string scene)
        {
            if (dock == null || dock.Scene != scene || DedicatedWorldRuntime.Scene != scene || string.IsNullOrEmpty(dock.DockId) ||
                !BoatProtocol.TransformValid(dock.X, dock.Y, dock.Z, dock.Qx, dock.Qy, dock.Qz, dock.Qw) ||
                !float.IsFinite(dock.InteractionX) || !float.IsFinite(dock.InteractionY) || !float.IsFinite(dock.InteractionZ))
                throw new InvalidOperationException("The dock does not belong to your active public district.");
        }
        private static void Place(PlayerBoatState boat, BoatDockDescription dock)
        {
            boat.Scene = dock.Scene; boat.DockId = dock.DockId; boat.X = dock.X; boat.Y = dock.Y; boat.Z = dock.Z;
            boat.Qx = dock.Qx; boat.Qy = dock.Qy; boat.Qz = dock.Qz; boat.Qw = dock.Qw; boat.PlayerAboard = boat.PlayerDriving = false;
        }
        private static PlayerBoatState ApplyPose(PlayerBoatState boat, BoatPose pose)
        {
            var next = BoatProtocol.Copy(boat); next.X = pose.X; next.Y = pose.Y; next.Z = pose.Z;
            next.Qx = pose.Qx; next.Qy = pose.Qy; next.Qz = pose.Qz; next.Qw = pose.Qw;
            next.PlayerAboard = pose.Boarded; next.PlayerDriving = pose.Driving; return next;
        }
        private static Player RequirePlayer(string id, string account)
        {
            if (!Players.TryGetValue(id ?? "", out var player) || (account != null && player.AccountId != account))
                throw new InvalidOperationException("The boat command has no authenticated account participant.");
            return player;
        }
        private static PlayerSceneContext RequireContext(string id, long generation)
        {
            var context = PlayerMapRuntime.GetContext(id);
            if (context == null || !Matches(context, context.Scene, generation)) throw new InvalidOperationException("Wait for your public district to finish loading.");
            return context;
        }
        private static bool Matches(PlayerSceneContext context, string scene, long generation) => context != null &&
            !context.TravelPending && context.PropertyId.Length == 0 && context.Scene == scene && context.Generation == generation;
        private static bool FreshActor(Player player, PlayerSceneContext context, double now) => player.Pose != null &&
            now >= player.PoseTime && now - player.PoseTime <= PoseFreshness && Matches(context, player.Pose.Scene, player.Pose.ContextGeneration);
        private static double DistanceSquared(float x, float y, float z, float a, float b, float c) =>
            (double)(x - a) * (x - a) + (double)(y - b) * (y - b) + (double)(z - c) * (z - c);
        private static bool Same(PlayerBoatState a, PlayerBoatState b) => a.Scene == b.Scene && a.DockId == b.DockId && a.X == b.X && a.Y == b.Y && a.Z == b.Z &&
            a.Qx == b.Qx && a.Qy == b.Qy && a.Qz == b.Qz && a.Qw == b.Qw && a.Fuel == b.Fuel && a.PlayerAboard == b.PlayerAboard && a.PlayerDriving == b.PlayerDriving;
        private static void RequireTime(double value) { if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value)); }
        private static void RequireWorker()
        { if (emit == null || reject == null || !DedicatedWorldRuntime.IsDedicatedServer) throw new InvalidOperationException("Boat authority is unavailable."); }
    }
}
