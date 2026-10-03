using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Nivalis;
using UnityEngine;
using Object = UnityEngine.Object;

namespace NivalisTogether.Dedicated
{
    /// <summary>Native area occupancy from connected players, without completing quests directly.</summary>
    public static class SpatialQuestReplication
    {
        private const double MaximumPoseAge = 3, UpdateInterval = .1, ScanInterval = 1;
        private const float NativeInsideDistanceSquared = .1f;
        private static readonly Harmony Patches = new Harmony("local.nivalis.together.spatial-quests");
        private static readonly HashSet<string> Present = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, PoseSample> Poses = new Dictionary<string, PoseSample>(StringComparer.Ordinal);
        private static readonly Dictionary<IntPtr, AreaStateVariable> Tracked = new Dictionary<IntPtr, AreaStateVariable>();
        private static bool patched, configured, worker, running;
        private static GameArea[] areas = Array.Empty<GameArea>();
        private static string scannedScene = "";
        private static double nextScan, nextUpdate;
        private static Func<bool> shouldPause;

        private sealed class PoseSample { public string Scene; public Vector3 Position; public bool Active; public double Time; }
        private sealed class Occupancy { public AreaStateVariable Variable; public bool Inside; }

        public static int LastAreaCount { get; private set; }
        public static int LastOccupiedCount { get; private set; }
        public static int LastFreshPlayerCount { get; private set; }

        // Call after storage isolation, before BeginNewWorld/LoadSnapshot on either role.
        public static void Configure(bool isWorker, Func<bool> pauseRequested = null)
        {
            if (!DedicatedWorldRuntime.IsIsolated || isWorker != DedicatedWorldRuntime.IsDedicatedServer)
                throw new InvalidOperationException("Spatial authority requires isolated multiplayer storage.");
            if (configured && worker != isWorker)
                throw new InvalidOperationException("Changing multiplayer role requires restarting the game.");
            if (!patched)
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
                var update = typeof(GameArea).GetMethod("Update", flags, null, Type.EmptyTypes, null);
                var load = typeof(AreaStateVariable).GetMethod("Load", flags, null, new[] { typeof(SaveReader) }, null);
                if (update == null || load == null) throw new MissingMethodException("Native area occupancy methods do not match this game build.");
                Patches.Patch(update, prefix: new HarmonyMethod(typeof(SpatialQuestReplication).GetMethod(nameof(AreaUpdatePrefix), BindingFlags.NonPublic | BindingFlags.Static)));
                Patches.Patch(load, postfix: new HarmonyMethod(typeof(SpatialQuestReplication).GetMethod(nameof(AreaLoadPostfix), BindingFlags.NonPublic | BindingFlags.Static)));
                patched = true;
            }
            configured = true; worker = isWorker; running = true; shouldPause = pauseRequested;
            Present.Clear(); Poses.Clear(); Tracked.Clear(); areas = Array.Empty<GameArea>();
            scannedScene = ""; nextScan = nextUpdate = 0;
            LastAreaCount = LastOccupiedCount = LastFreshPlayerCount = 0;
        }

        public static void SetPresent(string playerId, bool present)
        {
            if (!configured || !worker || !running || !Guid.TryParseExact(playerId, "N", out _)) return;
            if (!present) { Present.Remove(playerId); Poses.Remove(playerId); return; }
            if (!Present.Contains(playerId) && Present.Count >= 32)
                throw new InvalidOperationException("Spatial authority exceeds the supported party size.");
            Present.Add(playerId);
        }

        public static void Remove(string playerId) => SetPresent(playerId, false);

        // timestamp is the worker's local monotonic receive time, never a client timestamp.
        public static void UpdatePlayerPose(PlayerPose pose, double timestamp)
        {
            if (!configured || !worker || !running || pose == null || !Present.Contains(pose.PlayerId) ||
                !DedicatedProtocol.PoseValid(pose) || !double.IsFinite(timestamp)) return;
            Poses[pose.PlayerId] = new PoseSample
            {
                Scene = pose.Scene, Position = new Vector3(pose.X, pose.Y, pose.Z), Active = pose.Active, Time = timestamp
            };
        }

        // Unity main thread only. Call before capturing authoritative quest state.
        public static void Tick(double now, bool travelBusy)
        {
            if (!configured || !worker || !running || !double.IsFinite(now)) return;
            if (travelBusy || shouldPause?.Invoke() == true || !DedicatedWorldRuntime.IsWorldReady) { nextScan = nextUpdate = 0; return; }
            if (now < nextUpdate) return;
            nextUpdate = now + UpdateInterval;
            string scene = DedicatedWorldRuntime.Scene;
            if (scene != scannedScene || now >= nextScan)
            {
                var found = Object.FindObjectsOfType<GameArea>();
                if (found.Length > 4096) throw new InvalidOperationException("Native area count exceeds the supported spatial roster.");
                areas = new GameArea[found.Length];
                for (int i = 0; i < found.Length; i++) areas[i] = found[i];
                scannedScene = scene; nextScan = now + ScanInterval;
            }

            var participants = new List<Vector3>();
            foreach (var pair in Poses)
            {
                PoseSample pose = pair.Value;
                if (Present.Contains(pair.Key) && pose.Active && pose.Scene == scene &&
                    now >= pose.Time && now - pose.Time <= MaximumPoseAge) participants.Add(pose.Position);
            }
            LastFreshPlayerCount = participants.Count;
            var occupancy = new Dictionary<IntPtr, Occupancy>();
            LastAreaCount = LastOccupiedCount = 0;
            foreach (GameArea area in areas)
            {
                if (area == null || !area.isActiveAndEnabled || area.gameObject.scene.name != scene || area.StateVariable == null) continue;
                LastAreaCount++;
                AreaStateVariable variable = area.StateVariable;
                IntPtr id = variable.Pointer;
                if (!occupancy.TryGetValue(id, out var state))
                {
                    state = new Occupancy { Variable = variable };
                    occupancy.Add(id, state); Tracked[id] = variable;
                }
                if (state.Inside) continue;
                foreach (Vector3 position in participants)
                {
                    // Native API handles the area's inverse transform and local Bounds.
                    // GameArea.Update uses this exact strict squared-distance threshold.
                    if (area.ClosestDistanceSq(position) < NativeInsideDistanceSquared) { state.Inside = true; break; }
                }
            }
            var removed = new List<IntPtr>();
            foreach (var pair in Tracked)
            {
                AreaStateVariable variable = pair.Value;
                if (variable == null) { removed.Add(pair.Key); continue; }
                bool hasArea = occupancy.TryGetValue(pair.Key, out var state);
                bool inside = hasArea && state.Inside;
                if (inside) LastOccupiedCount++;
                if (variable.Value != inside)
                {
                    variable.Value = inside;
                    // Native quest listeners can start party travel or terminate the
                    // session synchronously. Stop before advancing this enumeration;
                    // teardown may have cleared Tracked inside the native callback.
                    if (!running || shouldPause?.Invoke() == true || !DedicatedWorldRuntime.IsWorldReady)
                    { nextScan = nextUpdate = 0; return; }
                }
                if (!hasArea) removed.Add(pair.Key);
            }
            foreach (IntPtr id in removed) Tracked.Remove(id);
        }

        public static void Stop()
        {
            running = false; shouldPause = null;
            Present.Clear(); Poses.Clear(); Tracked.Clear(); areas = Array.Empty<GameArea>();
            LastAreaCount = LastOccupiedCount = LastFreshPlayerCount = 0;
            // Keep the native Update guard until process exit; restarting is already
            // required to leave isolated multiplayer storage. Do not reinstate the
            // stationary worker/client as story authority during disconnect/teardown.
        }

        private static bool AreaUpdatePrefix() => !configured || !DedicatedWorldRuntime.IsIsolated;

        private static void AreaLoadPostfix(AreaStateVariable __instance)
        {
            if (!configured || !worker || !DedicatedWorldRuntime.IsIsolated || __instance == null) return;
            // Native Load reads one bool, writes _runtimeValue and marks it initialized,
            // without raising an event. Reset this transient occupancy before Load returns
            // to its caller. Use the existing field wrapper, without a quest event or
            // changing any persisted tracker state. Only fresh received poses can enter.
            __instance._runtimeValue = false;
        }
    }
}
