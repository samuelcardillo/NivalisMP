using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Nivalis;

namespace NivalisTogether.Dedicated
{
    /// <summary>
    /// Worker-owned per-player districts. Native worker scene residency is a temporary
    /// execution context and is never read to update an established player assignment.
    /// </summary>
    public static class PlayerMapRuntime
    {
        private sealed class Entry
        {
            internal string AccountId, RestoreEpoch = "";
            internal PlayerMapLocation Location;
            internal PlayerSceneContext Context;
            internal readonly PlayerSceneMembership Membership = new PlayerSceneMembership();
            internal double RestoreDeadline;
            internal bool Dirty, Restored;
        }
        private static readonly Dictionary<string, Entry> Players = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private static readonly Dictionary<int, string> SceneNames = new Dictionary<int, string>();
        private static Action<WorldEvent> emit;
        private static Action<string, string> reject;
        private static bool initialized;
        public static bool HasPendingRestores
        { get { foreach (var entry in Players.Values) if (entry.RestoreEpoch.Length != 0) return true; return false; } }
        public static bool IsPlayerPending(string playerId) => Players.TryGetValue(playerId ?? "", out var entry) && entry.Context.TravelPending;
        public static bool IsSceneOccupied(int sceneIndex)
        {
            if (!initialized || emit == null || reject == null || !DedicatedWorldRuntime.IsDedicatedServer ||
                !DedicatedWorldRuntime.IsIsolated || sceneIndex < 0 || Players.Count == 0 || !SceneNames.TryGetValue(sceneIndex, out string scene)) return false;
            // Includes authenticated restoration/pending travel assignments;
            // only server contexts grant membership, never client pose data.
            foreach (var entry in Players.Values)
                if (entry.Context != null && string.Equals(entry.Context.Scene, scene, StringComparison.Ordinal)) return true;
            return false;
        }
        private static void InitializeSceneNames()
        {
            // Portal metadata belongs to this initialized world. Retain only
            // scalar names/indices, never manager or portal native wrappers.
            SceneNames.Clear();
            if (!TravelUnlockManager.InstanceNotNull() || TravelUnlockManager.Instance._allPortalKeys == null)
                throw new InvalidOperationException("Native scene metadata is unavailable for player map authority.");
            foreach (var portal in TravelUnlockManager.Instance._allPortalKeys)
                if (portal != null && portal.SceneIndex >= 0 && !string.IsNullOrEmpty(portal.SceneName))
                    SceneNames[portal.SceneIndex] = portal.SceneName;
        }

        public static void Configure(Action<WorldEvent> emitWorldEvent, Action<string, string> rejectPlayer)
        {
            Reset(); emit = emitWorldEvent ?? throw new ArgumentNullException(nameof(emitWorldEvent));
            reject = rejectPlayer ?? throw new ArgumentNullException(nameof(rejectPlayer));
        }
        public static void InitializeWorker()
        {
            if (initialized || emit == null || reject == null || !DedicatedWorldRuntime.IsDedicatedServer || !DedicatedWorldRuntime.IsWorldReady)
                throw new InvalidOperationException("Player map authority requires a newly loaded dedicated world.");
            if (PlayerAccountRuntime.GetSpawnLocation() == null)
            {
                var character = PlayerManager.Instance.LocalPlayer?.Character;
                if (character?.Controller == null) throw new InvalidOperationException("The initial native character is unavailable.");
                var point = character.transform.position;
                PlayerAccountRuntime.EnsureSpawnLocation(new PlayerMapLocation { Scene = DedicatedWorldRuntime.Scene,
                    PropertyId = WorkerSceneResidency.CurrentProperty, X = point.x, Y = point.y, Z = point.z,
                    Yaw = character.Controller.Rotation.eulerAngles.y, HasPosition = true });
            }
            InitializeSceneNames(); initialized = true;
        }
        public static void Register(PlayerPresence presence, double timestamp)
        {
            RequireWorker(); RequireTime(timestamp);
            if (presence == null || !presence.Joined || !Guid.TryParseExact(presence.PlayerId, "N", out _) ||
                !Guid.TryParseExact(presence.AccountId, "N", out _)) throw new InvalidDataException("Invalid player map registration.");
            if (Players.TryGetValue(presence.PlayerId, out var existing))
            {
                if (existing.AccountId != presence.AccountId) throw new InvalidDataException("A player map identity changed accounts.");
                Publish(existing.Context);
                if (existing.RestoreEpoch.Length != 0) PublishRestore(presence.PlayerId, existing);
                return;
            }
            if (Players.Count >= 32) throw new InvalidOperationException("The map participant limit was reached.");
            foreach (var registered in Players.Values)
                if (registered.AccountId == presence.AccountId) throw new InvalidOperationException("This account already has an active map assignment.");
            var location = PlayerAccountRuntime.GetAccount(presence.AccountId).Location ?? PlayerAccountRuntime.GetSpawnLocation();
            PlayerMapLocation.Validate(location);
            string epoch = Guid.NewGuid().ToString("N");
            var entry = new Entry { AccountId = presence.AccountId, Location = PlayerMapLocation.Copy(location),
                RestoreEpoch = epoch, RestoreDeadline = timestamp + 120,
                Context = new PlayerSceneContext { PlayerId = presence.PlayerId, Scene = location.Scene,
                    PropertyId = location.PropertyId, Generation = 1, TravelPending = true, Epoch = epoch } };
            entry.Membership.Observe(entry.Context); Players.Add(presence.PlayerId, entry);
            // Assignment precedes restoration on the ordered world stream, so stale
            // spawn-map poses cannot acquire authority during native client loading.
            Publish(entry.Context); PublishRestore(presence.PlayerId, entry);
        }
        public static bool HandleRestoreAck(InteractionRequest request, double timestamp)
        {
            RequireWorker(); RequireTime(timestamp);
            if (request == null || request.Kind != PlayerMapRestore.AcknowledgementKind) return false;
            if (!Players.TryGetValue(request.PlayerId ?? "", out var entry) || request.AccountId != entry.AccountId ||
                entry.RestoreEpoch.Length == 0 || !entry.Membership.AllowsInteraction(request)) return true;
            if (timestamp > entry.RestoreDeadline) { Reject(request.PlayerId, "Your saved map could not load before the connection deadline."); return true; }
            PlayerMapRestoreAcknowledgement acknowledgement;
            try { acknowledgement = request.Data.Deserialize<PlayerMapRestoreAcknowledgement>(DedicatedProtocol.Json); }
            catch (JsonException) { Reject(request.PlayerId, "Your client returned an invalid map restoration result."); return true; }
            if (acknowledgement == null || !DedicatedProtocol.Text(acknowledgement.Scene, 128, true) ||
                !DedicatedProtocol.Text(acknowledgement.PropertyId, 128, true) || !DedicatedProtocol.Text(acknowledgement.Message, 240, true))
            { Reject(request.PlayerId, "Your client returned an invalid map restoration result."); return true; }
            if (!acknowledgement.Success || acknowledgement.Scene != entry.Location.Scene || acknowledgement.PropertyId != entry.Location.PropertyId)
            { Reject(request.PlayerId, "Your client could not restore its assigned map. Reconnect to try again."); return true; }
            entry.RestoreEpoch = ""; entry.RestoreDeadline = 0; entry.Restored = true; entry.Dirty = true;
            Assign(entry, entry.Location.Scene, entry.Location.PropertyId, false, "");
        
            return true;
        }
        public static bool TryAcceptPose(PlayerPose pose)
        {
            RequireWorker();
            if (pose == null || !Players.TryGetValue(pose.PlayerId ?? "", out var entry) || !entry.Restored ||
                pose.AccountId != entry.AccountId || !entry.Membership.TryAcceptPose(pose) ||
                (pose.Active && pose.PropertyId != entry.Context.PropertyId)) return false;
            if (pose.Active)
            {
                // Scene and property are copied from the assignment, never from movement.
                var location = new PlayerMapLocation { Scene = entry.Context.Scene, PropertyId = entry.Context.PropertyId,
                    PortalId = entry.Location.PortalId, X = pose.X, Y = pose.Y, Z = pose.Z, Yaw = pose.Yaw, HasPosition = true };
                if (!Same(entry.Location, location)) { entry.Location = location; entry.Dirty = true; }
            }
            return true;
        }
        public static PlayerSceneContext GetContext(string playerId)
        { RequireWorker(); return Players.TryGetValue(playerId ?? "", out var entry) ? PlayerSceneContext.Copy(entry.Context) : null; }
        public static PlayerMapLocation GetLocation(string playerId)
        { RequireWorker(); return Players.TryGetValue(playerId ?? "", out var entry) ? PlayerMapLocation.Copy(entry.Location) : null; }
        public static void BeginTransition(string playerId, string epoch)
        {
            var entry = RequirePlayer(playerId);
            if (!Guid.TryParseExact(epoch, "N", out _) || !entry.Restored || entry.RestoreEpoch.Length != 0)
                throw new InvalidOperationException("The player has not restored an authoritative map.");
            if (entry.Context.TravelPending)
            {
                if (entry.Context.Epoch == epoch) return;
                throw new InvalidOperationException("The player already has an active map transition.");
            }
            Assign(entry, entry.Location.Scene, entry.Location.PropertyId, true, epoch);
        }
        public static void CancelTransition(string playerId, string epoch)
        {
            var entry = RequirePlayer(playerId);
            if (!entry.Restored || entry.RestoreEpoch.Length != 0 || !entry.Context.TravelPending || entry.Context.Epoch != epoch) return;
            Assign(entry, entry.Location.Scene, entry.Location.PropertyId, false, "");
        }
        public static void CommitContext(string playerId, string scene, string propertyId, string portalId)
        {
            var entry = RequirePlayer(playerId);
            if (!entry.Restored || entry.RestoreEpoch.Length != 0 || !entry.Context.TravelPending)
                throw new InvalidOperationException("The player has no authorized map transition to commit.");
            // Arrival acknowledgement establishes membership, not an invented position.
            // The next accepted pose supplies coordinates under the new generation.
            var next = new PlayerMapLocation { Scene = scene, PropertyId = propertyId, PortalId =
                string.IsNullOrEmpty(portalId) && scene == entry.Location.Scene ? entry.Location.PortalId : portalId ?? "", HasPosition = false };
            PlayerMapLocation.Validate(next); entry.Location = next; entry.Dirty = true;
            Assign(entry, scene, propertyId, false, "");
        }
        public static void FlushLocations()
        {
            RequireWorker();
            foreach (var entry in Players.Values) Persist(entry);
        }
        public static void Remove(string playerId)
        {
            RequireWorker();
            if (!Players.TryGetValue(playerId ?? "", out var entry)) return;
            Persist(entry); Players.Remove(playerId);
        }
        public static void Tick(double timestamp)
        {
            RequireWorker(); RequireTime(timestamp);
            var expired = new List<string>();
            foreach (var pair in Players)
                if (pair.Value.RestoreEpoch.Length != 0 && timestamp > pair.Value.RestoreDeadline) expired.Add(pair.Key);
            foreach (string player in expired) Reject(player, "Your saved map could not load before the connection deadline.");
        }
        public static void Reset()
        { Players.Clear(); SceneNames.Clear(); emit = null; reject = null; initialized = false; }
        private static Entry RequirePlayer(string id)
        {
            RequireWorker();
            return Players.TryGetValue(id ?? "", out var entry) ? entry : throw new InvalidOperationException("The player has no map assignment.");
        }
        private static void RequireWorker()
        {
            if (!initialized || !DedicatedWorldRuntime.IsDedicatedServer || emit == null || reject == null)
                throw new InvalidOperationException("Player map authority is not initialized.");
        }
        private static void RequireTime(double timestamp)
        { if (!double.IsFinite(timestamp)) throw new ArgumentOutOfRangeException(nameof(timestamp)); }
        private static void Assign(Entry entry, string scene, string property, bool pending, string epoch)
        {
            var context = new PlayerSceneContext { PlayerId = entry.Context.PlayerId, Scene = scene, PropertyId = property,
                Generation = checked(entry.Context.Generation + 1), TravelPending = pending, Epoch = epoch };
            entry.Membership.Observe(context); entry.Context = context; Publish(context);
        }
        private static void Publish(PlayerSceneContext context)
        {
            PlayerSceneContext.Validate(context);
            emit(new WorldEvent { TargetPlayerId = context.PlayerId, Kind = PlayerSceneContext.EventKind,
                Data = JsonSerializer.SerializeToElement(context, DedicatedProtocol.Json) });
        }
        private static void PublishRestore(string playerId, Entry entry)
        {
            var restore = new PlayerMapRestore { PlayerId = playerId, Epoch = entry.RestoreEpoch, Location = PlayerMapLocation.Copy(entry.Location) };
            PlayerMapRestore.Validate(restore);
            emit(new WorldEvent { TargetPlayerId = playerId, Kind = PlayerMapRestore.EventKind,
                Data = JsonSerializer.SerializeToElement(restore, DedicatedProtocol.Json) });
        }
        private static void Persist(Entry entry)
        {
            if (!entry.Restored || !entry.Dirty) return;
            PlayerAccountRuntime.SetMapLocation(entry.AccountId, entry.Location); entry.Dirty = false;
        }
        private static void Reject(string playerId, string reason)
        {
            // Rejecting a failed restoration never persists unverified spawn/pose data.
            Players.Remove(playerId); reject(playerId, reason);
        }
        private static bool Same(PlayerMapLocation a, PlayerMapLocation b) => a.Scene == b.Scene && a.PropertyId == b.PropertyId &&
            a.PortalId == b.PortalId && a.HasPosition == b.HasPosition && a.X == b.X && a.Y == b.Y && a.Z == b.Z && a.Yaw == b.Yaw;
    }
}
