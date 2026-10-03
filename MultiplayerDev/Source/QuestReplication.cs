using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Nivalis;
using Nivalis.GhostSystem;
using UnityEngine;

namespace NivalisTogether.Dedicated
{
    public sealed class QuestJournalChunk
    {
        public long Sequence { get; set; }
        public int SaveVersion { get; set; }
        public int Part { get; set; }
        public int Parts { get; set; }
        public int RawLength { get; set; }
        public string Hash { get; set; } = "";
        public byte[] Bytes { get; set; } = Array.Empty<byte>();
    }
    public sealed class QuestJournalState
    {
        public int NextQuestNumber { get; set; }
        public QuestReplicaState[] Quests { get; set; } = Array.Empty<QuestReplicaState>();
    }
    public sealed class QuestReplicaState
    {
        public string Id { get; set; } = "";
        public bool CompletedList { get; set; }
        public int State { get; set; }
        public bool Pinned { get; set; }
        public int Number { get; set; }
        public int LastUpdateTime { get; set; }
        public QuestObjectiveState[] Active { get; set; } = Array.Empty<QuestObjectiveState>();
        public QuestObjectiveState[] Completed { get; set; } = Array.Empty<QuestObjectiveState>();
        public QuestObjectiveState[] Paused { get; set; } = Array.Empty<QuestObjectiveState>();
    }
    public sealed class QuestObjectiveState
    {
        public string Id { get; set; } = "";
        public int State { get; set; }
        public bool ReadyToContinue { get; set; }
        public byte[] Tracker { get; set; } = Array.Empty<byte>();
        public QuestProgressValue Progress { get; set; }
        public QuestProgressValue[] Multiple { get; set; }
        public int[] MultipleStates { get; set; }
        public QuestTargetLocation[] Targets { get; set; } = Array.Empty<QuestTargetLocation>();
    }
    public sealed class QuestTargetLocation
    {
        public string Id { get; set; } = "";
        public int SceneIndex { get; set; }
        public bool ActiveDuringCurfew { get; set; }
    }
    public sealed class QuestProgressValue
    {
        public string Label { get; set; } = "";
        public string Type { get; set; } = "null";
        public string Value { get; set; } = "";
    }

    /// <summary>
    /// Worker-owned live quest journal. Call CaptureEvents on the worker (internally throttled
    /// to one full frame per second), route Kind="quests" to Apply on clients, and call Tick
    /// on clients. Enable client authority before loading the shared snapshot. Every entry
    /// point runs on Unity's main thread. This module never starts/completes quests or grants
    /// rewards on clients. It mirrors native tracker saves and authoritative UI counters.
    /// </summary>
    public static class QuestReplication
    {
        private const int PartBytes = 24000, MaxRawBytes = 4 * 1024 * 1024, MaxParts = 192;
        private static readonly Harmony Harmony = new Harmony("local.nivalis.together.quest-journal");
        private static readonly Dictionary<IntPtr, QuestObjectiveState> Displays = new Dictionary<IntPtr, QuestObjectiveState>();
        private static readonly Dictionary<string, string> QuestHashes = new Dictionary<string, string>();
        private static readonly Dictionary<string, Quest> Assets = new Dictionary<string, Quest>();
        private static bool clientAuthority, patched;
        private static float nextCapture, assemblyStarted;
        private static long sequence, appliedSequence, assemblingSequence;
        private static IntPtr managerPointer;
        private static QuestJournalChunk assemblyHeader;
        private static byte[][] parts;
        private static int receivedParts, compressedLength;
        private static QuestJournalState pending;
        private static int pendingVersion;
        public static Action<string> Diagnostic { get; set; }
        public static bool ClientAuthority => clientAuthority;

        public static void SetClientAuthority(bool enabled)
        {
            if (enabled && !patched) InstallDisplayPatches();
            clientAuthority = enabled;
            if (!enabled)
            { Displays.Clear(); QuestHashes.Clear(); Assets.Clear(); pending = null; parts = null; managerPointer = IntPtr.Zero; appliedSequence = assemblingSequence = 0; }
        }

        public static WorldEvent[] CaptureEvents()
        {
            if (clientAuthority || !DedicatedWorldRuntime.IsDedicatedServer || !DedicatedWorldRuntime.IsWorldReady || !QuestManager.InstanceNotNull())
                return Array.Empty<WorldEvent>();
            if (Time.realtimeSinceStartup < nextCapture) return Array.Empty<WorldEvent>();
            nextCapture = Time.realtimeSinceStartup + 1f;
            var manager = QuestManager.Instance;
            var journal = new QuestJournalState();
            if (!MultiplayerSandboxRuntime.Active)
            {
                var states = new List<QuestReplicaState>();
                CaptureList(manager._activeQuests, false, states);
                CaptureList(manager._completedQuests, true, states);
                if (states.Count > 1024) throw new InvalidDataException("Quest journal exceeds supported quest count.");
                states.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
                journal.NextQuestNumber = manager._nextQuestNumber; journal.Quests = states.ToArray();
            }
            // Keep the ordinary empty frame so connected journals clear. Never
            // serialize imported campaign trackers while sandbox cleanup settles.
            byte[] raw = JsonSerializer.SerializeToUtf8Bytes(journal, DedicatedProtocol.Json);
            if (raw.Length > MaxRawBytes) throw new InvalidDataException("Quest journal exceeds supported size.");
            byte[] compressed;
            using (var output = new MemoryStream())
            {
                using (var gzip = new GZipStream(output, System.IO.Compression.CompressionLevel.Fastest, true)) gzip.Write(raw, 0, raw.Length);
                compressed = output.ToArray();
            }
            int count = Math.Max(1, (compressed.Length + PartBytes - 1) / PartBytes);
            if (count > MaxParts) throw new InvalidDataException("Quest journal exceeds packet count.");
            string hash = Convert.ToHexString(SHA256.HashData(raw));
            int version = SerializationManagerConfiguration.Instance.LatestSaveVersion;
            long frame = ++sequence;
            var result = new WorldEvent[count];
            for (int i = 0; i < count; i++)
            {
                var chunk = new QuestJournalChunk { Sequence = frame, SaveVersion = version, Part = i, Parts = count, RawLength = raw.Length, Hash = hash,
                    Bytes = compressed.Skip(i * PartBytes).Take(PartBytes).ToArray() };
                var data = JsonSerializer.SerializeToElement(chunk, DedicatedProtocol.Json);
                if (CanonicalJson.ByteCount(data) > 48000) throw new InvalidDataException("Quest frame is too large.");
                result[i] = new WorldEvent { Kind = "quests", Data = data };
            }
            return result;
        }

        private static void CaptureList(Il2CppSystem.Collections.Generic.Dictionary<string, RuntimeQuest> quests, bool completed, List<QuestReplicaState> result)
        {
            if (quests == null) return;
            foreach (var pair in quests)
            {
                var runtime = pair.Value;
                if (runtime == null || runtime.Quest == null) continue;
                result.Add(new QuestReplicaState { Id = runtime.Quest.Guid, CompletedList = completed, State = (int)runtime.currentState,
                    Pinned = runtime.pinned, Number = runtime.QuestNumber, LastUpdateTime = runtime.lastUpdateTime,
                    Active = CaptureObjectives(runtime.ActiveSubQuests), Completed = CaptureObjectives(runtime.CompletedSubQuests), Paused = CaptureObjectives(runtime.PausedSubQuests) });
            }
        }

        private static QuestObjectiveState[] CaptureObjectives(Il2CppSystem.Collections.Generic.List<RuntimeQuest.SubQuestTrackerPair> list)
        {
            if (list == null) return Array.Empty<QuestObjectiveState>();
            if (list.Count > 256) throw new InvalidDataException("Quest objective count exceeds supported limit.");
            var result = new List<QuestObjectiveState>();
            foreach (var pair in list)
            {
                if (pair == null || pair.SubQuest == null || pair.Tracker == null) continue;
                var tracker = pair.Tracker;
                var state = new QuestObjectiveState { Id = pair.SubQuest.ArticyId, State = (int)tracker.State, ReadyToContinue = tracker.IsReadyToContinueOn,
                    Tracker = SaveTracker(tracker), Progress = CaptureProgress(tracker.CurrentProgress) };
                if (tracker.TargetLocations != null)
                {
                    if (tracker.TargetLocations.Count > 256) throw new InvalidDataException("Quest target location count exceeds supported limit.");
                    var targets = new List<QuestTargetLocation>();
                    foreach (var location in tracker.TargetLocations)
                        targets.Add(new QuestTargetLocation { Id = location.guid, SceneIndex = location.sceneIndex, ActiveDuringCurfew = location.IsActiveDuringCurfew });
                    state.Targets = targets.ToArray();
                }
                var multiple = tracker.CurrentMultipleProgress;
                if (multiple != null)
                {
                    if (multiple.Length > 256) throw new InvalidDataException("Quest progress row count exceeds supported limit.");
                    state.Multiple = new QuestProgressValue[multiple.Length]; state.MultipleStates = new int[multiple.Length];
                    for (int i = 0; i < multiple.Length; i++) { state.Multiple[i] = CaptureProgress(multiple[i]); state.MultipleStates[i] = (int)tracker.GetMultiProgressState(i); }
                }
                result.Add(state);
            }
            return result.ToArray();
        }

        private static byte[] SaveTracker(SubQuest.ProgressTracker tracker)
        {
            var writer = ES2Writer.Create(new ES2Settings(ES2Settings.SaveLocation.Memory));
            try
            {
                var native = new BinarySaveWriter(SerializationManagerConfiguration.Instance.LatestSaveVersion, writer);
                tracker.Save(native.Cast<ISaveWriter>());
                writer.writer.Flush();
                byte[] bytes = writer.stream.ReadAllBytes().ToArray();
                if (bytes.Length > 65536) throw new InvalidDataException("Quest tracker save exceeds supported limit.");
                return bytes;
            }
            finally { writer.Dispose(); }
        }

        private static QuestProgressValue CaptureProgress(SubQuest.ProgressDisplayInfo value)
        {
            if (value == null) return null;
            var result = new QuestProgressValue { Label = value.Label ?? "" };
            if (value.Value == null) return result;
            result.Type = value.Value.GetIl2CppType().FullName;
            switch (result.Type)
            {
                case "System.Int32": result.Value = value.Value.Unbox<int>().ToString(CultureInfo.InvariantCulture); break;
                case "System.Int64": result.Value = value.Value.Unbox<long>().ToString(CultureInfo.InvariantCulture); break;
                case "System.Single": result.Value = value.Value.Unbox<float>().ToString("R", CultureInfo.InvariantCulture); break;
                case "System.Double": result.Value = value.Value.Unbox<double>().ToString("R", CultureInfo.InvariantCulture); break;
                case "System.Boolean": result.Value = value.Value.Unbox<bool>() ? "true" : "false"; break;
                default: result.Type = "System.String"; result.Value = value.Value.ToString(); break;
            }
            if (result.Label.Length > 4096 || result.Value.Length > 4096) throw new InvalidDataException("Quest progress label exceeds supported limit.");
            return result;
        }

        public static void Apply(WorldEvent update)
        {
            if (!clientAuthority || update == null || update.Kind != "quests") return;
            var chunk = update.Data.Deserialize<QuestJournalChunk>(DedicatedProtocol.Json);
            if (chunk == null || chunk.Sequence <= 0 || chunk.Parts < 1 || chunk.Parts > MaxParts || chunk.Part < 0 || chunk.Part >= chunk.Parts ||
                chunk.RawLength < 2 || chunk.RawLength > MaxRawBytes || chunk.Bytes == null || chunk.Bytes.Length < 1 || chunk.Bytes.Length > PartBytes ||
                chunk.Hash == null || chunk.Hash.Length != 64) throw new InvalidDataException("Invalid replicated quest journal frame.");
            if (chunk.Sequence <= appliedSequence || chunk.Sequence < assemblingSequence) return;
            if (parts == null || chunk.Sequence != assemblingSequence)
            {
                parts = new byte[chunk.Parts][]; assemblingSequence = chunk.Sequence; assemblyHeader = chunk;
                receivedParts = compressedLength = 0; assemblyStarted = Time.realtimeSinceStartup;
            }
            if (chunk.Parts != assemblyHeader.Parts || chunk.RawLength != assemblyHeader.RawLength || chunk.Hash != assemblyHeader.Hash || chunk.SaveVersion != assemblyHeader.SaveVersion)
                throw new InvalidDataException("Inconsistent replicated quest journal frame.");
            if (parts[chunk.Part] != null) return;
            parts[chunk.Part] = chunk.Bytes; receivedParts++; compressedLength += chunk.Bytes.Length;
            if (compressedLength > MaxRawBytes + 65536) throw new InvalidDataException("Compressed quest journal exceeds supported limit.");
            if (receivedParts != parts.Length) return;
            using (var compressed = new MemoryStream())
            {
                foreach (var bytes in parts) compressed.Write(bytes, 0, bytes.Length);
                compressed.Position = 0;
                using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
                using var output = new MemoryStream(chunk.RawLength);
                byte[] buffer = new byte[8192]; int read;
                while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (output.Length + read > chunk.RawLength) throw new InvalidDataException("Expanded quest journal length mismatch.");
                    output.Write(buffer, 0, read);
                }
                byte[] raw = output.ToArray();
                if (raw.Length != chunk.RawLength || !DedicatedProtocol.EqualHex(Convert.ToHexString(SHA256.HashData(raw)), chunk.Hash))
                    throw new InvalidDataException("Quest journal checksum mismatch.");
                pending = JsonSerializer.Deserialize<QuestJournalState>(raw, DedicatedProtocol.Json);
                if (pending == null || pending.Quests == null || pending.Quests.Length > 1024) throw new InvalidDataException("Invalid quest journal.");
                pending = JournalForCurrentMode(pending);
                pendingVersion = chunk.SaveVersion; appliedSequence = chunk.Sequence; parts = null;
            }
            Tick();
        }

        private static QuestJournalState JournalForCurrentMode(QuestJournalState journal)
            => journal != null && MultiplayerSandboxRuntime.Active ? new QuestJournalState() : journal;

        public static void Tick()
        {
            if (!clientAuthority) return;
            // Also discard a frame decoded before the sandbox gate became active.
            // Native journal cleanup is independently owned by the sandbox runtime.
            pending = JournalForCurrentMode(pending);
            if (parts != null && Time.realtimeSinceStartup - assemblyStarted > 10f) parts = null;
            if (!QuestManager.InstanceNotNull() || !DedicatedWorldRuntime.IsWorldReady) return;
            var manager = QuestManager.Instance;
            if (manager.Pointer != managerPointer)
            {
                managerPointer = manager.Pointer; Assets.Clear(); QuestHashes.Clear(); Displays.Clear();
                foreach (var quest in manager._allQuests) if (quest != null && !string.IsNullOrEmpty(quest.Guid)) Assets[quest.Guid] = quest;
                Quiesce(manager._activeQuests); Quiesce(manager._completedQuests);
            }
            if (pending == null) return;
            if (pendingVersion != SerializationManagerConfiguration.Instance.LatestSaveVersion) throw new InvalidDataException("Quest tracker save version does not match the client game.");
            var journalIds = new HashSet<string>();
            foreach (var state in pending.Quests)
            {
                ValidateQuest(state);
                if (!journalIds.Add(state.Id)) throw new InvalidDataException("Duplicate quest in replicated journal.");
                if (!Assets.ContainsKey(state.Id)) throw new InvalidDataException("Server referenced an unknown quest: " + state.Id);
            }
            var activeIds = new HashSet<string>(); var completedIds = new HashSet<string>(); bool changed = false;
            foreach (var state in pending.Quests)
            {
                var asset = Assets[state.Id];
                var target = state.CompletedList ? manager._completedQuests : manager._activeQuests;
                var other = state.CompletedList ? manager._activeQuests : manager._completedQuests;
                (state.CompletedList ? completedIds : activeIds).Add(state.Id);
                string hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(state, DedicatedProtocol.Json)));
                if (QuestHashes.TryGetValue(state.Id, out var prior) && prior == hash && target.ContainsKey(state.Id)) continue;
                RuntimeQuest runtime = null;
                if (target.ContainsKey(state.Id)) runtime = target[state.Id];
                else if (other.ContainsKey(state.Id)) { runtime = other[state.Id]; other.Remove(state.Id); }
                if (runtime == null) runtime = new RuntimeQuest();
                Quiesce(runtime);
                runtime.Quest = asset; runtime.currentState = (QuestState)state.State; runtime.pinned = state.Pinned;
                runtime._QuestNumber_k__BackingField = state.Number; runtime.lastUpdateTime = state.LastUpdateTime; runtime.QuestIsDirty = false;
                runtime.ActiveSubQuests = BuildObjectives(asset, state.Active);
                runtime.CompletedSubQuests = BuildObjectives(asset, state.Completed);
                runtime.PausedSubQuests = BuildObjectives(asset, state.Paused);
                runtime.ActiveConnectionTrackers = new Il2CppSystem.Collections.Generic.List<RuntimeQuest.ConnectionTrackerPair>();
                target[state.Id] = runtime; QuestHashes[state.Id] = hash;
                runtime.OnProgressUpdated?.Invoke(); changed = true;
            }
            changed |= RemoveAbsent(manager._activeQuests, activeIds);
            changed |= RemoveAbsent(manager._completedQuests, completedIds);
            manager._nextQuestNumber = pending.NextQuestNumber; manager.articyVariablesChanged = false;
            QuestManager.questsToMarkAsCompleted?.Clear(); pending = null;
            if (changed)
            {
                manager.OnQuestsUpdated?.Invoke(); manager.OnQuestsPinnedChanged?.Invoke();
                Diagnostic?.Invoke("Applied authoritative quest journal: " + activeIds.Count + " active, " + completedIds.Count + " completed.");
            }
        }

        private static void ValidateQuest(QuestReplicaState state)
        {
            if (state == null || !DedicatedProtocol.Text(state.Id, 128) || state.State < 0 || state.State > 3 || state.Active == null || state.Completed == null || state.Paused == null ||
                state.Active.Length > 256 || state.Completed.Length > 256 || state.Paused.Length > 256) throw new InvalidDataException("Invalid quest state.");
        }

        private static Il2CppSystem.Collections.Generic.List<RuntimeQuest.SubQuestTrackerPair> BuildObjectives(Quest quest, QuestObjectiveState[] objectives)
        {
            var result = new Il2CppSystem.Collections.Generic.List<RuntimeQuest.SubQuestTrackerPair>();
            foreach (var state in objectives)
            {
                if (state == null || !DedicatedProtocol.Text(state.Id, 128) || state.State < 0 || state.State > 3 || state.Tracker == null || state.Tracker.Length > 65536 ||
                    state.Targets == null || state.Targets.Length > 256 ||
                    (state.Multiple != null && (state.Multiple.Length > 256 || state.MultipleStates == null || state.MultipleStates.Length != state.Multiple.Length)))
                    throw new InvalidDataException("Invalid quest objective state.");
                var subquest = quest.GetSubQuest(state.Id);
                if (subquest == null) throw new InvalidDataException("Server referenced an unknown objective: " + state.Id);
                var tracker = subquest.CreateTracker();
                var reader = ES2Reader.Create(new Il2CppStructArray<byte>(state.Tracker), new ES2Settings(ES2Settings.SaveLocation.Memory));
                try { tracker.Load(new SaveReader(pendingVersion, reader)); }
                finally { reader.Dispose(); }
                tracker._State_k__BackingField = (QuestState)state.State;
                tracker._IsInitialized_k__BackingField = false;
                tracker.TargetLocations = new Il2CppSystem.Collections.Generic.List<WorldPointReference>();
                foreach (var location in state.Targets)
                {
                    if (location == null || location.Id == null || location.Id.Length > 128) throw new InvalidDataException("Invalid quest target location.");
                    NativeValueList.Add(tracker.TargetLocations, new WorldPointReference(location.Id, location.SceneIndex, location.ActiveDuringCurfew));
                }
                Displays[tracker.Pointer] = state;
                NativeValueList.Add(result, new RuntimeQuest.SubQuestTrackerPair { SubQuest = subquest, Tracker = tracker });
            }
            return result;
        }

        private static bool RemoveAbsent(Il2CppSystem.Collections.Generic.Dictionary<string, RuntimeQuest> quests, HashSet<string> keep)
        {
            var remove = new List<string>(); foreach (var pair in quests) if (!keep.Contains(pair.Key)) remove.Add(pair.Key);
            foreach (string id in remove) { Quiesce(quests[id]); quests.Remove(id); QuestHashes.Remove(id); }
            return remove.Count > 0;
        }
        private static void Quiesce(Il2CppSystem.Collections.Generic.Dictionary<string, RuntimeQuest> quests)
        { if (quests != null) foreach (var pair in quests) Quiesce(pair.Value); }
        private static void Quiesce(RuntimeQuest runtime)
        {
            if (runtime == null) return;
            foreach (var list in new[] { runtime.ActiveSubQuests, runtime.CompletedSubQuests, runtime.PausedSubQuests })
                if (list != null) foreach (var pair in list)
                {
                    if (pair?.Tracker == null) continue;
                    Displays.Remove(pair.Tracker.Pointer);
                    if (pair.Tracker.IsInitialized) pair.Tracker.DeInitialize();
                    pair.Tracker.OnTrackerProgressUpdated = null;
                }
            if (runtime.ActiveConnectionTrackers != null)
                foreach (var pair in runtime.ActiveConnectionTrackers) if (pair?.Tracker != null) pair.Tracker.DeInitialize();
            runtime.ActiveConnectionTrackers?.Clear();
            runtime._subquestCacheToActivate?.Clear();
            runtime._pausedSubquestCacheToActivate?.Clear();
            runtime._trackerCacheToActivate?.Clear();
            runtime._invalidatedSubQuests?.Clear();
            runtime.QuestIsDirty = false;
        }

        private static void InstallDisplayPatches()
        {
            foreach (var type in typeof(SubQuest).Assembly.GetTypes())
            {
                if (type.ContainsGenericParameters || !typeof(SubQuest.ProgressTracker).IsAssignableFrom(type)) continue;
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    string prefix = method.Name == "get_CurrentProgress" ? nameof(ProgressPrefix) : method.Name == "get_CurrentMultipleProgress" ? nameof(MultiplePrefix) :
                        method.Name == "GetMultiProgressState" ? nameof(MultiStatePrefix) : method.Name == "get_IsReadyToContinueOn" ? nameof(ReadyPrefix) : null;
                    if (prefix == null) continue;
                    // IL2CPP abstract methods have concrete managed dispatch wrappers,
                    // so MethodInfo.IsAbstract cannot identify their missing native body.
                    var nativeMethod = Il2CppInterop.Common.Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(method);
                    if (nativeMethod == null || nativeMethod.Name.Contains("_Abstract_", StringComparison.Ordinal)) continue;
                    Harmony.Patch(method, prefix: new HarmonyMethod(typeof(QuestReplication).GetMethod(prefix, BindingFlags.NonPublic | BindingFlags.Static)));
                }
            }
            patched = true;
        }
        private static bool ProgressPrefix(SubQuest.ProgressTracker __instance, ref SubQuest.ProgressDisplayInfo __result)
        {
            if (!clientAuthority || !Displays.TryGetValue(__instance.Pointer, out var state)) return true;
            __result = Display(state.Progress); return false;
        }
        private static bool MultiplePrefix(SubQuest.ProgressTracker __instance, ref Il2CppReferenceArray<SubQuest.ProgressDisplayInfo> __result)
        {
            if (!clientAuthority || !Displays.TryGetValue(__instance.Pointer, out var state)) return true;
            if (state.Multiple == null) { __result = null; return false; }
            __result = new Il2CppReferenceArray<SubQuest.ProgressDisplayInfo>(state.Multiple.Length);
            for (int i = 0; i < state.Multiple.Length; i++) __result[i] = Display(state.Multiple[i]);
            return false;
        }
        private static bool MultiStatePrefix(SubQuest.ProgressTracker __instance, int __0, ref QuestState __result)
        {
            if (!clientAuthority || !Displays.TryGetValue(__instance.Pointer, out var state)) return true;
            __result = state.MultipleStates != null && __0 >= 0 && __0 < state.MultipleStates.Length ? (QuestState)state.MultipleStates[__0] : (QuestState)state.State;
            return false;
        }
        private static bool ReadyPrefix(SubQuest.ProgressTracker __instance, ref bool __result)
        {
            if (!clientAuthority || !Displays.TryGetValue(__instance.Pointer, out var state)) return true;
            __result = state.ReadyToContinue; return false;
        }
        private static SubQuest.ProgressDisplayInfo Display(QuestProgressValue progress)
        {
            if (progress == null) return null;
            if (progress.Label == null || progress.Value == null || progress.Label.Length > 4096 || progress.Value.Length > 4096)
                throw new InvalidDataException("Invalid quest progress display.");
            Il2CppSystem.Object value = null;
            switch (progress.Type)
            {
                case "null": break;
                case "System.Int32": value = new Il2CppSystem.Int32 { m_value = int.Parse(progress.Value, CultureInfo.InvariantCulture) }.BoxIl2CppObject(); break;
                case "System.Int64": value = new Il2CppSystem.Int64 { m_value = long.Parse(progress.Value, CultureInfo.InvariantCulture) }.BoxIl2CppObject(); break;
                case "System.Single": value = new Il2CppSystem.Single { m_value = float.Parse(progress.Value, CultureInfo.InvariantCulture) }.BoxIl2CppObject(); break;
                case "System.Double": value = new Il2CppSystem.Double { m_value = double.Parse(progress.Value, CultureInfo.InvariantCulture) }.BoxIl2CppObject(); break;
                case "System.Boolean": value = new Il2CppSystem.Boolean { m_value = bool.Parse(progress.Value) }.BoxIl2CppObject(); break;
                default: value = (Il2CppSystem.String)progress.Value; break;
            }
            return new SubQuest.ProgressDisplayInfo(progress.Label, value);
        }
    }
}
