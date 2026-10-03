using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Nivalis;
using Nivalis.GhostSystem;
using Nivalis.GhostSystem.Ai;
using UnityEngine;

namespace NivalisTogether.Dedicated
{
    internal static class NpcLocomotionPolicy
    {
        // Current native NPC_Male / NPC_Female controllers contain these real
        // states with reference T-pose clips. HasState alone accepts them, but
        // they are initialization poses, not authoritative gameplay animation.
        private const int MaleBindPoseState = 305286492, FemaleBindPoseState = 1573150859;
        public static float Speed(float distance, float elapsed, bool continuous) =>
            !continuous || !float.IsFinite(distance) || !float.IsFinite(elapsed) || elapsed <= 0 || distance < .025f || distance > 12 ? 0 : 1;
        public static float FreshSpeed(float speed, float secondsSinceFrame, float intervalSeconds = .5f) =>
            !float.IsFinite(secondsSinceFrame) || secondsSinceFrame < 0 ||
            secondsSinceFrame > Math.Max(1.5f, float.IsFinite(intervalSeconds) ? intervalSeconds * 2 : 1.5f) ? 0 : speed;
        public static bool UsableAnimation(bool initialized, int layerCount, int baseStateHash, bool hasBaseState) =>
            initialized && layerCount > 0 && baseStateHash != 0 && hasBaseState &&
            baseStateHash != MaleBindPoseState && baseStateHash != FemaleBindPoseState;
    }

    // Presentation history only: native ghost identity, presence and position
    // still apply immediately. Playback uses confirmed samples and never runs
    // AI or predicts beyond the newest position. Source-time spacing matters:
    // a large full roster can arrive much less often than its capture timer.
    internal sealed class NpcPlayback<T>
    {
        internal const int MaxSamples = 8;
        // At 15 fps and above presentation follows elapsed time normally. A
        // blocking native/UI operation must not consume seconds of movement
        // in its first rendered frame after returning.
        internal const double MaxAdvanceSeconds = 1d / 15d;
        internal const double MaxPlaybackRate = 1.15;
        private readonly Func<T, T, float, T> interpolate;
        private readonly List<(double Time, T Value)> samples = new List<(double, T)>();
        private double playbackTime, lastSampleTime, secondsSinceSample;
        private bool intervalSeen;
        internal NpcPlayback(Func<T, T, float, T> interpolate) { this.interpolate = interpolate ?? throw new ArgumentNullException(nameof(interpolate)); }
        internal T Current { get; private set; }
        internal double IntervalSeconds { get; private set; } = .5;
        internal double FreshnessSeconds => Math.Max(1.5, IntervalSeconds * 2);
        internal int SampleCount => samples.Count;
        internal void Clear()
        { samples.Clear(); Current = default; playbackTime = lastSampleTime = secondsSinceSample = 0; intervalSeen = false; IntervalSeconds = .5; }
        internal bool Add(double capturedAt, T value, bool reset = false)
        {
            if (!double.IsFinite(capturedAt) || capturedAt < 0) throw new ArgumentException("Invalid NPC capture time.");
            if (reset || samples.Count == 0)
            {
                Clear(); samples.Add((capturedAt, value)); playbackTime = lastSampleTime = capturedAt; Current = value;
                return true;
            }
            if (capturedAt <= lastSampleTime) return false;
            double interval = capturedAt - lastSampleTime;
            IntervalSeconds = intervalSeen ? IntervalSeconds * .75 + interval * .25 : interval;
            intervalSeen = true; lastSampleTime = capturedAt; secondsSinceSample = 0;
            if (samples.Count >= MaxSamples)
            {
                // A catch-up burst can replace old presentation samples, but
                // must start from the pose actually rendered, not teleport to
                // an evicted sample. Logical roster events are never dropped.
                double recoverySeconds = Math.Max(IntervalSeconds, capturedAt - playbackTime);
                samples.Clear(); playbackTime = capturedAt - recoverySeconds;
                samples.Add((playbackTime, Current));
            }
            samples.Add((capturedAt, value)); return true;
        }
        internal T Advance(double deltaSeconds)
        {
            if (samples.Count == 0) return Current;
            double advance = 0;
            if (double.IsFinite(deltaSeconds) && deltaSeconds > 0)
            {
                secondsSinceSample += deltaSeconds;
                advance = Math.Min(deltaSeconds, MaxAdvanceSeconds);
                // Keep approximately one measured source interval buffered.
                // Hitch-clamped time is a debt, not permanently lost playback:
                // repay it gradually, at most 15% faster than source time.
                // The estimated clock stops at the newest confirmed sample;
                // neither recovery nor missing packets can extrapolate motion.
                double desiredTime = lastSampleTime + Math.Min(secondsSinceSample, IntervalSeconds) - IntervalSeconds;
                double debt = Math.Max(0, desiredTime - playbackTime - advance);
                double rate = 1 + Math.Min(MaxPlaybackRate - 1, debt / Math.Max(IntervalSeconds, MaxAdvanceSeconds));
                advance = Math.Min(deltaSeconds * rate, MaxAdvanceSeconds);
            }
            playbackTime = Math.Min(playbackTime + advance, samples[samples.Count - 1].Time);
            while (samples.Count > 2 && samples[1].Time <= playbackTime) samples.RemoveAt(0);
            if (samples.Count == 1) return Current = samples[0].Value;
            var from = samples[0]; var to = samples[1];
            float fraction = (float)Math.Max(0, Math.Min(1, (playbackTime - from.Time) / (to.Time - from.Time)));
            return Current = interpolate(from.Value, to.Value, fraction);
        }
    }
    // Presentation scheduling only. Accumulate hitch-clamped frame time so
    // hidden skeletons retain normal clip speed without advancing through a
    // multi-second pause in one evaluation when the camera turns back.
    internal sealed class NpcAnimationCadence
    {
        internal const float HiddenInterval = .1f, MaxAccumulatedSeconds = .2f;
        private float accumulated, sinceEvaluation;
        private bool evaluated;
        internal void Reset() { accumulated = sinceEvaluation = 0; evaluated = false; }
        internal bool Advance(float elapsed, bool visible, bool force, out float animationDelta)
        {
            float valid = float.IsFinite(elapsed) && elapsed > 0 ? elapsed : 0;
            accumulated = Math.Min(MaxAccumulatedSeconds, accumulated + Math.Min(valid, (float)NpcPlayback<float>.MaxAdvanceSeconds));
            sinceEvaluation = Math.Min(HiddenInterval, sinceEvaluation + valid);
            animationDelta = 0;
            if (evaluated && !visible && !force && sinceEvaluation + .000001f < HiddenInterval) return false;
            animationDelta = accumulated; accumulated = sinceEvaluation = 0; evaluated = true;
            return true;
        }
    }
    internal sealed class NpcDiscontinuityLog
    {
        internal const int Limit = 5;
        internal const double WindowSeconds = 10;
        private double windowStart;
        private int count, suppressed;
        private bool started;
        internal void Reset() { started = false; count = suppressed = 0; windowStart = 0; }
        internal bool TryTake(double now, out int previouslySuppressed)
        {
            previouslySuppressed = 0;
            if (!double.IsFinite(now) || now < 0) return false;
            if (!started || now < windowStart || now - windowStart >= WindowSeconds)
            { started = true; windowStart = now; count = 0; }
            if (count >= Limit) { if (suppressed < int.MaxValue) suppressed++; return false; }
            count++; previouslySuppressed = suppressed; suppressed = 0; return true;
        }
    }
    public sealed class NpcWorldFrame
    {
        public long Sequence { get; set; }
        public double CapturedAt { get; set; }
        public int ChunkIndex { get; set; }
        public int ChunkCount { get; set; } = 1;
        public NpcWorldState[] Npcs { get; set; } = Array.Empty<NpcWorldState>();
    }

    public sealed class NpcWorldState
    {
        public string Id { get; set; } = "";
        public string PrefabId { get; set; } = "";
        public string PersonId { get; set; } = "";
        public string ViewId { get; set; } = "";
        public bool Viewless { get; set; }
        public bool Baked { get; set; }
        public int Scene { get; set; }
        public bool Active { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float Qx { get; set; }
        public float Qy { get; set; }
        public float Qz { get; set; }
        public float Qw { get; set; } = 1;
        public bool HasAnimation { get; set; }
        public float Pose { get; set; }
        public float AnimationOffset { get; set; }
        public AnimatorLayerSnapshot[] Layers { get; set; } = Array.Empty<AnimatorLayerSnapshot>();
        public AnimatorParameterSnapshot[] Parameters { get; set; } = Array.Empty<AnimatorParameterSnapshot>();
    }

    public sealed class AnimatorLayerSnapshot
    {
        public int StateHash { get; set; }
        public float NormalizedTime { get; set; }
        public float Weight { get; set; }
    }

    public sealed class AnimatorParameterSnapshot
    {
        public int Hash { get; set; }
        public int Type { get; set; }
        public float Float { get; set; }
        public int Int { get; set; }
        public bool Bool { get; set; }
    }

    /// <summary>
    /// All methods must run on Unity's main thread. A native server save must be
    /// loaded before enabling client authority. This mirrors agent ghosts and
    /// their rendered motion; transactions and dialogue choices need separate RPCs.
    /// Disconnect must reload an isolated save before returning to solo play.
    /// </summary>
    public static class NpcWorldReplication
    {
        private static readonly HashSet<string> WorkerCaptureRoster = new HashSet<string>();
        private const int MaximumGhosts = 30000, MaximumChunks = 4096;
        private static readonly Harmony Patches = new Harmony("local.nivalis.together.npc-authority");
        private static readonly Dictionary<string, Replica> Replicas = new Dictionary<string, Replica>();
        private static readonly Dictionary<string, long> LastStateSequence = new Dictionary<string, long>();
        private static readonly Dictionary<long, AssemblyRoster> Rosters = new Dictionary<long, AssemblyRoster>();
        private static readonly NpcDiscontinuityLog DiscontinuityLog = new NpcDiscontinuityLog();
        private static bool patched, clientAuthority;
        private static CharacterManager workerCharacters;
        private static bool previousWorkerFullAnimation;
        private static readonly Dictionary<int, WorkerAnimator> WorkerAnimators = new Dictionary<int, WorkerAnimator>();
        private static long captureSequence, lastCompleteSequence = -1;
        private static float nextCleanup;
        private static readonly string[] LocomotionStates = { "Male.Movement.Movement", "Female.Movement.Movement", "Base Layer.Tripod Bot blend tree", "Base Layer.idle", "Base Layer.Bot Hovering" };
        public static Action<string> Diagnostic { get; set; }
        public static bool ClientAuthority => clientAuthority;
        public static int ReplicaCount => Replicas.Count;
        public static long LastCompleteSequence => lastCompleteSequence;
        private sealed class AssemblyRoster
        {
            internal int Expected;
            internal double CapturedAt;
            internal readonly HashSet<int> Chunks = new HashSet<int>();
            internal readonly HashSet<string> Ids = new HashSet<string>();
            internal int Adopted, Created;
        }
        private sealed class WorkerAnimator { internal Animator Animator; internal AnimatorCullingMode CullingMode; }
        private readonly struct RenderPose
        {
            internal readonly Vector3 Position;
            internal readonly Quaternion Rotation;
            internal RenderPose(Vector3 position, Quaternion rotation) { Position = position; Rotation = rotation; }
        }
        private static RenderPose InterpolatePose(RenderPose from, RenderPose to, float fraction) =>
            new RenderPose(Vector3.Lerp(from.Position, to.Position, fraction), Quaternion.Slerp(from.Rotation, to.Rotation, fraction));
        private sealed class Replica
        {
            internal AgentGhost Ghost;
            internal Character Character;
            internal Vector3 Position;
            internal Quaternion Rotation;
            internal bool Placed, OriginalRootMotion, OriginalFireEvents, OriginalNavEnabled, OriginalObstacleEnabled;
            internal AnimatorCullingMode OriginalCullingMode;
            internal bool PoseSeen, HasWorkerAnimation, FallbackInitialized, HasFallbackSpeed;
            internal float LastPoseTime, TargetSpeed, RenderSpeed, RenderMotionSpeed;
            internal double LastSourceTime;
            internal readonly NpcPlayback<RenderPose> Playback = new NpcPlayback<RenderPose>(InterpolatePose);
            internal readonly NpcAnimationCadence AnimationCadence = new NpcAnimationCadence();
            internal IntPtr AnimationAnimator, AnimationController, AnimationPool;
            internal ulong AnimationPoolVersion;
            internal bool AnimationIdentitySeen;
            internal NpcWorldState WorkerAnimation;
            internal int FallbackStateHash, FallbackSpeedHash;
        }

        public static void SetClientAuthority(bool enabled)
        {
            if (enabled && !patched) InstallPatches();
            RestoreWorkerAnimation();
            WorkerCaptureRoster.Clear();
            if (enabled == clientAuthority) return;
            DiscontinuityLog.Reset();
            clientAuthority = enabled;
            if (!enabled)
            {
                foreach (var replica in Replicas.Values) RestoreRendering(replica);
                Replicas.Clear(); LastStateSequence.Clear(); Rosters.Clear();
                lastCompleteSequence = -1;
            }
            else { nextCleanup = 0; Tick(); }
        }

        private static void InstallPatches()
        {
            var prefix = new HarmonyMethod(typeof(NpcWorldReplication).GetMethod(nameof(AllowSimulation), BindingFlags.NonPublic | BindingFlags.Static));
            // CurrentTimeOnValueChanged takes generated Change<TimeStamp>, an
            // eight-byte value passed in a register. Harmony's wrapper treats it
            // as a pointer and cannot detour it safely. The shared clock writes
            // raw fields without emitting that event; its SkipTime callee and
            // the simulator's update/action entry points remain guarded here.
            Patch(typeof(AgentGhostSimulator), prefix, "Update", "OnCurfewEnd", "UpdateAgentActions", "UpdateCurrentAgentAction", "SelectNewAction", "SwitchToDialogueAction", "SwitchAction", "SwitchAgentAction", "SkipTime");
            Patch(typeof(AgentStateUpdateSimulator), prefix, "UpdateSimulator");
            Patch(typeof(AgentViewSimulator), prefix, "UpdateSimulator", "Simulate", "SimulateMoveCommand");
            Patch(typeof(AgentSimulatorPauseHandler), prefix, "DialogueStartListener", "DialogueEndListener");
            Patch(typeof(Agent), prefix, "UpdateViewCommand", "SnapToView", "LateUpdateAll");
            // Character is the NPC class. PlayerManager.Player.Character is a
            // PlayerCharacter; both independently inherit BaseCharacter. Player
            // movement/interaction run through PlayerCharacter.Update and are not
            // covered by these Character-only hooks. Do not widen them to the base.
            var characterPrefix = new HarmonyMethod(typeof(NpcWorldReplication).GetMethod(nameof(AllowCharacterSimulation), BindingFlags.NonPublic | BindingFlags.Static));
            Patch(typeof(Character), characterPrefix, "DoUpdate", "DoLateUpdate", "OnAnimatorMove", "OnAnimatorIK", "ExecuteCommand", "AddCommand", "SetTask");
            // Only ghostless decorative fillers may use native local simulation.
            // Agent simulators and all real/interactive characters stay guarded.
            var fillerPrefix = new HarmonyMethod(typeof(NpcWorldReplication).GetMethod(nameof(AllowFillerPopulation), BindingFlags.NonPublic | BindingFlags.Static));
            Patch(typeof(CharacterManager), fillerPrefix, "OnMinuteUpdate");
            patched = true;
        }

        private static void Patch(Type type, HarmonyMethod prefix, params string[] names)
        {
            foreach (string name in names)
            {
                MethodInfo method = null;
                foreach (var candidate in type.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                    if (candidate.Name == name)
                    {
                        if (method != null) throw new AmbiguousMatchException(type.FullName + "." + name);
                        method = candidate;
                    }
                if (method == null) throw new MissingMethodException(type.FullName, name);
                Patches.Patch(method, prefix: prefix);
            }
        }
        private static bool AllowSimulation() => !clientAuthority;
        private static bool AllowCharacterSimulation(Character __instance) => !clientAuthority || CrowdReplication.IsLocalDecoration(__instance);
        private static bool AllowFillerPopulation() => !clientAuthority || CrowdReplication.ClientUsesLocalDecorations;

        public static NpcWorldFrame Capture() => CaptureState(true);

        /// <summary>
        /// Native scene travel does not stop the persistent ghost simulation.
        /// Keep its authoritative poses observable without dereferencing scene
        /// views while Unity unloads and creates characters and animators.
        /// </summary>
        public static NpcWorldFrame CaptureDuringWorkerResidency()
        {
            if (clientAuthority || !DedicatedWorldRuntime.IsDedicatedServer || !DedicatedWorldRuntime.IsIsolated ||
                !WorkerSceneResidency.IsBusy || WorkerCaptureRoster.Count == 0) return null;
            return CaptureState(false);
        }

        private static NpcWorldFrame CaptureState(bool includeSceneViews)
        {
            var manager = GhostManager.HasInstance ? GhostManager.Instance : null;
            if (manager == null || !manager.IsSystemInitialized) return null;
            bool captureWorkerAnimation = includeSceneViews && !clientAuthority && DedicatedWorldRuntime.IsDedicatedServer;
            if (captureWorkerAnimation)
            {
                var characters = CharacterManager.Instance;
                if (characters != null)
                {
                    if (workerCharacters == null || workerCharacters.Pointer != characters.Pointer)
                    {
                        RestoreWorkerAnimation(); workerCharacters = characters;
                        previousWorkerFullAnimation = characters.FullAnimation;
                    }
                    // This used to be initialized by decorative crowd capture,
                    // which protocol 8 no longer performs. Keep the worker's
                    // native animation update policy independent of that stream.
                    characters.FullAnimation = true;
                }
            }
            var states = new List<NpcWorldState>();
            var seenAnimators = new HashSet<int>();
            foreach (var ghost in manager._agentGhosts)
            {
                if (ghost == null || !ghost.IsValid) continue;
                var state = new NpcWorldState
                {
                    Id = ghost.Id ?? "", PrefabId = ghost.PrefabId ?? "",
                    PersonId = ghost.Person == null ? "" : ghost.Person.Guid ?? "",
                    ViewId = ghost.ViewId ?? "", Viewless = ghost.IsViewLess, Baked = ghost.IsBakedInstance,
                    Scene = ghost.SceneIndex, Active = ghost.IsActive
                };
                var position = ghost.Position; var rotation = ghost.Rotation;
                var view = includeSceneViews && ghost.HasLoadedView ? ghost.LoadedView.TryCast<Agent>() : null;
                var character = view == null ? null : view.Character;
                if (character != null && character.gameObject.activeInHierarchy)
                {
                    position = character.ThisTransform.position; rotation = character.ThisTransform.rotation;
                    state.Pose = character.Pose; state.AnimationOffset = character.AnimationOffset;
                    if (captureWorkerAnimation && character.Animator != null)
                    {
                        var animator = character.Animator; int id = animator.GetInstanceID(); seenAnimators.Add(id);
                        if (!WorkerAnimators.ContainsKey(id)) WorkerAnimators.Add(id, new WorkerAnimator { Animator = animator, CullingMode = animator.cullingMode });
                        animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                    }
                    CaptureAnimation(character.Animator, state);
                }
                state.X = position.x; state.Y = position.y; state.Z = position.z;
                state.Qx = rotation.x; state.Qy = rotation.y; state.Qz = rotation.z; state.Qw = rotation.w;
                states.Add(state);
            }
            if (captureWorkerAnimation)
            {
                var removed = new List<int>();
                foreach (var pair in WorkerAnimators) if (!seenAnimators.Contains(pair.Key))
                {
                    if (pair.Value.Animator != null) pair.Value.Animator.cullingMode = pair.Value.CullingMode;
                    removed.Add(pair.Key);
                }
                foreach (int id in removed) WorkerAnimators.Remove(id);
            }
            if (!includeSceneViews)
            {
                // A complete roster also authorizes client removals. Fail closed
                // if native scene work ever exposes a transiently partial registry;
                // never turn that temporary absence into a replicated deletion.
                var observed = new HashSet<string>();
                foreach (var state in states) observed.Add(state.Id);
                if (!WorkerCaptureRoster.IsSubsetOf(observed)) return null;
            }
            else if (captureWorkerAnimation)
            {
                WorkerCaptureRoster.Clear();
                foreach (var state in states) WorkerCaptureRoster.Add(state.Id);
            }
            return new NpcWorldFrame { Sequence = ++captureSequence, CapturedAt = Time.realtimeSinceStartupAsDouble, Npcs = states.ToArray() };
        }

        private static void RestoreWorkerAnimation()
        {
            if (workerCharacters != null) workerCharacters.FullAnimation = previousWorkerFullAnimation;
            workerCharacters = null;
            foreach (var saved in WorkerAnimators.Values) if (saved.Animator != null) saved.Animator.cullingMode = saved.CullingMode;
            WorkerAnimators.Clear();
        }

        private static void CaptureAnimation(Animator animator, NpcWorldState state)
        {
            if (animator == null || !animator.isInitialized) return;
            int baseState = animator.layerCount > 0 ? animator.GetCurrentAnimatorStateInfo(0).fullPathHash : 0;
            if (!NpcLocomotionPolicy.UsableAnimation(animator.isInitialized, animator.layerCount, baseState, animator.layerCount > 0 && animator.HasState(0, baseState))) return;
            var layers = new AnimatorLayerSnapshot[Math.Min(animator.layerCount, 32)];
            for (int i = 0; i < layers.Length; i++)
            {
                var info = animator.GetCurrentAnimatorStateInfo(i);
                layers[i] = new AnimatorLayerSnapshot { StateHash = info.fullPathHash, NormalizedTime = info.normalizedTime, Weight = animator.GetLayerWeight(i) };
            }
            var parameters = new List<AnimatorParameterSnapshot>();
            foreach (var parameter in animator.parameters)
            {
                var value = new AnimatorParameterSnapshot { Hash = parameter.nameHash, Type = (int)parameter.type };
                if (parameter.type == AnimatorControllerParameterType.Float) value.Float = animator.GetFloat(parameter.nameHash);
                else if (parameter.type == AnimatorControllerParameterType.Int) value.Int = animator.GetInteger(parameter.nameHash);
                else if (parameter.type == AnimatorControllerParameterType.Bool) value.Bool = animator.GetBool(parameter.nameHash);
                else continue; // Trigger events are represented by resulting animator states.
                parameters.Add(value);
                if (parameters.Count >= 256) break;
            }
            state.Layers = layers; state.Parameters = parameters.ToArray(); state.HasAnimation = true;
        }

        /// <summary>
        /// Chunk every captured frame with the same Sequence and ChunkCount. Extras
        /// are removed only after all chunks arrive; an incomplete frame never
        /// causes roster deletion. Do not send an empty frame while loading.
        /// </summary>
        public static void Apply(NpcWorldFrame frame)
        {
            if (!clientAuthority || frame == null || frame.Sequence <= lastCompleteSequence) return;
            var manager = GhostManager.HasInstance ? GhostManager.Instance : null;
            if (manager == null || !manager.IsSystemInitialized) return;
            if (!double.IsFinite(frame.CapturedAt) || frame.CapturedAt < 0 || frame.ChunkCount < 1 || frame.ChunkCount > MaximumChunks || frame.ChunkIndex < 0 || frame.ChunkIndex >= frame.ChunkCount || frame.Npcs == null || frame.Npcs.Length > MaximumGhosts)
                throw new ArgumentException("Invalid NPC snapshot bounds.");
            if (!Rosters.TryGetValue(frame.Sequence, out var roster))
            {
                if (Rosters.Count >= 4) { long oldest = long.MaxValue; foreach (long seq in Rosters.Keys) oldest = Math.Min(oldest, seq); Rosters.Remove(oldest); }
                roster = new AssemblyRoster { Expected = frame.ChunkCount, CapturedAt = frame.CapturedAt }; Rosters.Add(frame.Sequence, roster);
            }
            if (roster.Expected != frame.ChunkCount || roster.CapturedAt != frame.CapturedAt) throw new ArgumentException("NPC snapshot chunk identity changed.");
            if (roster.Chunks.Contains(frame.ChunkIndex)) return;
            foreach (var state in frame.Npcs)
            {
                Validate(state);
                roster.Ids.Add(state.Id);
                if (roster.Ids.Count > MaximumGhosts) throw new ArgumentException("NPC roster exceeds limit.");
                if (LastStateSequence.TryGetValue(state.Id, out long last) && frame.Sequence < last) continue;
                ApplyState(manager, state, roster, frame.CapturedAt, frame.Sequence); LastStateSequence[state.Id] = frame.Sequence;
            }
            roster.Chunks.Add(frame.ChunkIndex);
            if (roster.Chunks.Count == roster.Expected)
            {
                RemoveExtraGhosts(manager, roster.Ids, frame.Sequence);
                if (roster.Adopted + roster.Created > 0)
                    Report("NPC roster " + frame.Sequence + " reconciled: " + roster.Ids.Count + " identities, " + roster.Adopted + " offstage person ghosts adopted, " + roster.Created + " created.");
                lastCompleteSequence = frame.Sequence;
                var old = new List<long>(); foreach (long seq in Rosters.Keys) if (seq <= frame.Sequence) old.Add(seq);
                foreach (long seq in old) Rosters.Remove(seq);
            }
        }

        private static void Validate(NpcWorldState state)
        {
            if (state == null || string.IsNullOrEmpty(state.Id) || state.Id.Length > 256 || (state.PrefabId?.Length ?? 0) > 256 || (state.PersonId?.Length ?? 0) > 256 || (state.ViewId?.Length ?? 0) > 256 || state.Scene < -1 || state.Scene > 100000 || (state.Active && state.Scene < 0))
                throw new ArgumentException("Invalid NPC identity.");
            if (string.IsNullOrEmpty(state.PrefabId) && !state.Baked &&
                (string.IsNullOrEmpty(state.PersonId) || state.Scene != -1 || state.Active || state.HasAnimation))
                throw new ArgumentException("A prefab-less runtime NPC must be an offstage person: " + state.Id);
            if (!Finite(state.X) || !Finite(state.Y) || !Finite(state.Z) || Math.Abs(state.X) > 1000000 || Math.Abs(state.Y) > 1000000 || Math.Abs(state.Z) > 1000000 || !Finite(state.Qx) || !Finite(state.Qy) || !Finite(state.Qz) || !Finite(state.Qw))
                throw new ArgumentException("Invalid NPC transform.");
            if (state.Layers == null || state.Layers.Length > 32 || state.Parameters == null || state.Parameters.Length > 256 || !Finite(state.Pose) || !Finite(state.AnimationOffset)) throw new ArgumentException("Invalid NPC animation.");
            foreach (var layer in state.Layers) if (layer == null || !Finite(layer.NormalizedTime) || !Finite(layer.Weight)) throw new ArgumentException("Invalid NPC animation layer.");
            foreach (var parameter in state.Parameters) if (parameter == null || !Finite(parameter.Float)) throw new ArgumentException("Invalid NPC animation parameter.");
        }
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static void ApplyState(GhostManager manager, NpcWorldState state, AssemblyRoster roster, double capturedAt, long sequence)
        {
            var ghostBase = manager.GetGhostById(state.Id);
            var ghost = ghostBase == null ? null : ghostBase.TryCast<AgentGhost>();
            if (ghostBase != null && ghost == null) throw new InvalidOperationException("Server NPC ID collides with a different ghost type: " + state.Id);
            var person = ResolvePerson(state.PersonId);
            if (ghost == null)
            {
                if (state.Baked)
                {
                    // Authored ghosts must come from the imported native world;
                    // never rekey or replace their saved/view references by person.
                    throw new InvalidOperationException("Authored NPC is absent from the imported world: " + state.Id);
                }
                else if (string.IsNullOrEmpty(state.PrefabId))
                {
                    // Native SpawnPersonAgentGhost deliberately creates (new GUID,
                    // null prefab) at scene -1 for people without CharacterPrefab.
                    // Such runtime ghosts are excluded by IsPartOfSaveFile, so a
                    // client regenerates a different GUID when importing the save.
                    // Person.Guid is the stable identity; preserve that native
                    // person's runtime ghost while adopting the server's registry ID.
                    if (person == null || person.CharacterPrefab != null)
                        throw new InvalidOperationException("Prefab-less NPC does not identify a native offstage person: " + state.Id + " person=" + state.PersonId);
                    ghost = AdoptOffstagePerson(manager, person, state);
                    if (ghost != null) roster.Adopted++;
                    else { ghost = new AgentGhost(state.Id, null); roster.Created++; }
                }
                else
                {
                    var serialization = SerializationManager.Instance;
                    if (serialization == null || serialization.Prefabs == null || !serialization.Prefabs.prefabDictionary.ContainsKey(state.PrefabId))
                        throw new InvalidOperationException("Server NPC prefab is missing locally: " + state.PrefabId + " npc=" + state.Id);
                    ghost = new AgentGhost(state.Id, state.PrefabId); roster.Created++;
                }
                // Initialize unregistered ghosts without invoking spatial-registry
                // mutation setters before RegisterGhostImmediate owns the identity.
                ghost._sceneIndex = state.Scene; ghost._IsActive_k__BackingField = state.Active;
                ghost._isViewLess = state.Viewless; ghost._IsBakedInstance_k__BackingField = state.Baked;
                ghost._viewId = string.IsNullOrEmpty(state.ViewId) ? state.Id : state.ViewId;
                ghost._transform = new PositionRotation(new Vector3(state.X, state.Y, state.Z), Rotation(state));
                LinkPerson(ghost, person);
                manager.RegisterGhostImmediate(ghost);
                if (manager.GetGhostById(state.Id)?.Pointer != ghost.Pointer || !ghost.IsValid)
                    throw new InvalidOperationException("Authoritative NPC registration did not complete: " + state.Id);
            }
            else
            {
                RequireIdentity(ghost, state);
                LinkPerson(ghost, person);
            }
            var position = new Vector3(state.X, state.Y, state.Z); var rotation = Rotation(state);
            int oldScene = ghost.SceneIndex;
            bool oldActive = ghost.IsActive, oldViewless = ghost.IsViewLess;
            bool changeScene = oldScene != state.Scene;
            bool changeActive = oldActive != state.Active;
            bool changeViewless = oldViewless != state.Viewless;
            if (!Replicas.TryGetValue(state.Id, out var replica)) { replica = new Replica(); Replicas.Add(state.Id, replica); }
            bool discontinuity = Vector3.Distance(replica.Position, position) > 12f;
            float now = Time.realtimeSinceStartup;
            // Zero is accepted for older deterministic fixtures. Current
            // worker chunks all carry the same monotonic capture timestamp.
            double sourceTime = capturedAt > 0 ? capturedAt : now;
            string discontinuityDiagnostic = null;
            IntPtr priorViewPointer = IntPtr.Zero;
            if (replica.Placed && replica.PoseSeen && discontinuity && DiscontinuityLog.TryTake(now, out int suppressed))
            {
                // Capture before changing the native ghost or rebinding its
                // pooled view. Diagnostics must distinguish an authoritative
                // route/presence change from a presentation-history problem.
                var priorRender = replica.Character == null ? replica.Playback.Current.Position : replica.Character.ThisTransform.position;
                var priorTarget = replica.Position;
                priorViewPointer = ghost.HasLoadedView ? ghost.LoadedView.Pointer : IntPtr.Zero;
                long previousSequence = LastStateSequence.TryGetValue(state.Id, out long previous) ? previous : -1;
                string visible = replica.Character == null ? "unbound" : replica.Character.CameraVisible.ToString();
                discontinuityDiagnostic = FormattableString.Invariant($"NPC_DISCONTINUITY id={state.Id} scene={oldScene}->{state.Scene} active={oldActive}->{state.Active} viewless={oldViewless}->{state.Viewless} oldTarget=({priorTarget.x:F3},{priorTarget.y:F3},{priorTarget.z:F3}) newTarget=({position.x:F3},{position.y:F3},{position.z:F3}) priorRender=({priorRender.x:F3},{priorRender.y:F3},{priorRender.z:F3}) sourceInterval={sourceTime - replica.LastSourceTime:F3} sequence={previousSequence}->{sequence} cameraVisible={visible} suppressed={suppressed}");
            }
            if (changeActive) ghost.IsActive = state.Active;
            if (changeViewless) ghost._isViewLess = state.Viewless;
            if (changeScene) GhostManager.TransferGhostToScene(ghost, state.Scene, new PositionRotation(position, rotation));
            // Native Ghost.Position updates the spatial registry. Observe the
            // current native values each time, but do not rebuild that index
            // for unchanged offstage and stationary NPCs.
            else ApplyNativePose(ghost, position, rotation);
            if (changeScene || changeActive || changeViewless || !ghost.HasLoadedView) manager.UpdateGhostViewPresence(ghost);
            if (discontinuityDiagnostic != null)
            {
                IntPtr currentViewPointer = ghost.HasLoadedView ? ghost.LoadedView.Pointer : IntPtr.Zero;
                Report(discontinuityDiagnostic + " nativeViewChanged=" + (currentViewPointer != priorViewPointer));
            }
            var offset = position - replica.Position; offset.y = 0;
            // Ghost.IsActive does not express native view visibility. A loaded
            // native character can render and move while
            // its ghost is inactive; worker map residency must not stop its
            // replicated locomotion or reset its placement every snapshot.
            replica.TargetSpeed = NpcLocomotionPolicy.Speed(offset.magnitude, (float)(sourceTime - replica.LastSourceTime), replica.PoseSeen && !changeScene && state.Scene >= 0);
            replica.LastSourceTime = sourceTime;
            replica.LastPoseTime = now; replica.PoseSeen = true; replica.HasWorkerAnimation = false; replica.WorkerAnimation = null;
            replica.Ghost = ghost; replica.Position = position; replica.Rotation = rotation;
            var view = ghost.HasLoadedView ? ghost.LoadedView.TryCast<Agent>() : null;
            var character = view == null ? null : view.Character;
            BindCharacter(replica, character);
            if (changeScene) replica.Placed = false;
            if (character == null) return;
            FreezeRendering(replica);
            if (!replica.Placed || discontinuity)
            {
                replica.Playback.Add(sourceTime, new RenderPose(position, rotation), true);
                character.ThisTransform.SetPositionAndRotation(position, rotation); replica.Placed = true;
            }
            else replica.Playback.Add(sourceTime, new RenderPose(position, rotation));
            if (state.HasAnimation && ApplyAnimation(character, state))
            { replica.HasWorkerAnimation = true; replica.WorkerAnimation = state; replica.FallbackInitialized = false; replica.AnimationCadence.Reset(); }
        }

        private static void ApplyNativePose(AgentGhost ghost, Vector3 position, Quaternion rotation)
        {
            // Exact component equality is deliberate: Unity's Vector3 and
            // Quaternion operators use tolerances and could hide native drift.
            // Read rotation after Position in case the native setter changes it.
            var currentPosition = ghost.Position;
            if (currentPosition.x != position.x || currentPosition.y != position.y || currentPosition.z != position.z)
                ghost.Position = position;
            var currentRotation = ghost.Rotation;
            if (currentRotation.x != rotation.x || currentRotation.y != rotation.y || currentRotation.z != rotation.z || currentRotation.w != rotation.w)
                ghost.Rotation = rotation;
        }

        private static void TickFallbackAnimation(Replica replica, float deltaTime, float now)
        {
            if (replica.HasWorkerAnimation) return;
            var animator = replica.Character == null ? null : replica.Character.Animator;
            if (!PrepareReplicaAnimator(animator)) return;
            if (!replica.FallbackInitialized)
            {
                replica.FallbackStateHash = 0; replica.HasFallbackSpeed = false;
                replica.FallbackInitialized = true;
                // Do not guess state names for unsupported controllers. Native
                // male/female, tripod-bot, police-bot idle and therapy-bot hover
                // paths were verified in game assets.
                foreach (var parameter in animator.parameters)
                {
                    if (parameter.type == AnimatorControllerParameterType.Trigger) animator.ResetTrigger(parameter.nameHash);
                    else if (parameter.type == AnimatorControllerParameterType.Bool) animator.SetBool(parameter.nameHash, false);
                    else if (parameter.type == AnimatorControllerParameterType.Int) animator.SetInteger(parameter.nameHash, parameter.defaultInt);
                    else if (parameter.type == AnimatorControllerParameterType.Float)
                    {
                        bool neutral = parameter.name == "Pose" || parameter.name == "Angle" || parameter.name == "Side" || parameter.name == "Speed";
                        animator.SetFloat(parameter.nameHash, neutral ? 0 : parameter.defaultFloat);
                        if (parameter.name == "Speed") { replica.FallbackSpeedHash = parameter.nameHash; replica.HasFallbackSpeed = true; }
                    }
                }
                for (int layer = 1; layer < animator.layerCount; layer++) animator.SetLayerWeight(layer, 0);
                replica.FallbackStateHash = SelectFallbackState(animator);
                if (replica.FallbackStateHash == 0) { Report("NPC animation fallback unavailable for controller on " + replica.Ghost.Id); return; }
            }
            if (replica.FallbackStateHash == 0) return;
            if (replica.HasFallbackSpeed)
            {
                float target = replica.PoseSeen ? NpcLocomotionPolicy.FreshSpeed(replica.RenderMotionSpeed, now - replica.LastPoseTime, (float)replica.Playback.IntervalSeconds) : 0;
                replica.RenderSpeed = Mathf.MoveTowards(replica.RenderSpeed, target, deltaTime * 8);
                animator.SetFloat(replica.FallbackSpeedHash, replica.RenderSpeed);
            }
            if (animator.GetCurrentAnimatorStateInfo(0).fullPathHash != replica.FallbackStateHash || animator.IsInTransition(0))
                animator.Play(replica.FallbackStateHash, 0, 0);
            // Native Character.DoUpdate advances this disabled/manual animator
            // but also runs AI actions. Advance only rendering, with clip events,
            // state callbacks, root motion and native navigation all suppressed.
            // The scheduler already clamps each source frame's elapsed time;
            // this can contain several short frames while off-camera.
            animator.Update(Mathf.Clamp(deltaTime, 0, NpcAnimationCadence.MaxAccumulatedSeconds));
        }

        private static int SelectFallbackState(Animator animator)
        {
            // Native NPC_Female views have reported HasState(Male...) true while
            // Play silently retained their old seated state. Prefer the authored
            // controller path and verify the evaluated state before caching it.
            bool female = animator.runtimeAnimatorController.name == "NPC_Female";
            for (int index = 0; index < LocomotionStates.Length; index++)
            {
                int candidate = female && index < 2 ? 1 - index : index;
                int hash = Animator.StringToHash(LocomotionStates[candidate]);
                if (!animator.HasState(0, hash)) continue;
                animator.Play(hash, 0, 0);
                animator.Update(0);
                if (animator.GetCurrentAnimatorStateInfo(0).fullPathHash == hash && !animator.IsInTransition(0)) return hash;
            }
            // Failed candidates are attempted only at initialization, never
            // repeatedly resetting a stale action every frame.
            return 0;
        }

        private static bool PrepareReplicaAnimator(Animator animator)
        {
            if (animator == null || animator.runtimeAnimatorController == null) return false;
            // Native characters use disabled animators and evaluate them inside
            // Character.DoUpdate. A freshly pooled view can need its first pose
            // evaluated before isInitialized is true. This invokes no AI update.
            if (!animator.isInitialized) animator.Update(0);
            return animator.isInitialized;
        }

        private static Quaternion Rotation(NpcWorldState state)
        {
            var rotation = new Quaternion(state.Qx, state.Qy, state.Qz, state.Qw);
            float magnitude = rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w;
            return magnitude > 0.000001f && Finite(magnitude) ? Quaternion.Normalize(rotation) : Quaternion.identity;
        }
        private static Person ResolvePerson(string personId)
        {
            if (string.IsNullOrEmpty(personId)) return null;
            var people = PersonDataManager.Instance;
            var supplied = people == null ? null : people.TryProvide(personId);
            var person = supplied == null ? null : supplied.TryCast<Person>();
            if (person == null || person.RuntimeData == null) throw new InvalidOperationException("Authoritative NPC person is not initialized: " + personId);
            return person;
        }
        private static void RequireIdentity(AgentGhost ghost, NpcWorldState state)
        {
            if (!string.Equals(ghost.PrefabId ?? "", state.PrefabId ?? "", StringComparison.Ordinal) || ghost.IsBakedInstance != state.Baked)
                throw new InvalidOperationException("NPC native identity changed; reload the authoritative save: " + state.Id);
            if (ghost.Person != null && !string.Equals(ghost.Person.Guid, state.PersonId, StringComparison.Ordinal))
                throw new InvalidOperationException("NPC belongs to a different person: " + state.Id);
        }
        private static AgentGhost AdoptOffstagePerson(GhostManager manager, Person person, NpcWorldState state)
        {
            var reference = person.RuntimeData.MyGhost;
            string localId = ReferenceEquals(reference, null) ? "" : reference.ReferencedId;
            var localBase = string.IsNullOrEmpty(localId) ? null : manager.GetGhostById(localId);
            var local = localBase == null ? null : localBase.TryCast<AgentGhost>();
            if (local == null)
            {
                if (localBase != null) throw new InvalidOperationException("Person ghost has the wrong native type: " + person.Guid);
                return null;
            }
            if (local.Id == state.Id) throw new InvalidOperationException("NPC registry returned inconsistent identity: " + state.Id);
            if (local.Person == null || local.Person.Guid != person.Guid || !string.IsNullOrEmpty(local.PrefabId) || local.IsBakedInstance || local.SceneIndex != -1 || local.IsActive || local.HasLoadedView)
                throw new InvalidOperationException("Cannot adopt a rendered or mismatched person ghost: " + person.Guid + " local=" + local.Id + " server=" + state.Id);
            if (Replicas.TryGetValue(local.Id, out var replica)) RestoreRendering(replica);
            Replicas.Remove(local.Id); LastStateSequence.Remove(local.Id);
            manager.DeregisterGhostImmediate(local);
            local._id = state.Id;
            local._viewId = string.IsNullOrEmpty(state.ViewId) ? state.Id : state.ViewId;
            return local;
        }
        private static void LinkPerson(AgentGhost ghost, Person person)
        {
            if (person == null) return;
            if (ghost.Person == null) ghost.Person = person;
            var reference = person.RuntimeData.MyGhost;
            if (ReferenceEquals(reference, null) || reference.ReferencedId != ghost.Id || reference._value?.Pointer != ghost.Pointer)
                person.RuntimeData.MyGhost = new GhostReference<AgentGhost>(ghost);
        }
        private static void Report(string message)
        {
            try { Diagnostic?.Invoke(message); } catch { }
        }

        private static bool ApplyAnimation(Character character, NpcWorldState state)
        {
            var animator = character.Animator;
            if (!PrepareReplicaAnimator(animator)) return false;
            int baseState = state.Layers.Length > 0 ? state.Layers[0].StateHash : 0;
            if (!NpcLocomotionPolicy.UsableAnimation(animator.isInitialized, state.Layers.Length, baseState, animator.HasState(0, baseState))) return false;
            character.Pose = state.Pose; character.AnimationOffset = state.AnimationOffset;
            foreach (var parameter in state.Parameters)
            {
                if (parameter.Type == (int)AnimatorControllerParameterType.Float) animator.SetFloat(parameter.Hash, parameter.Float);
                else if (parameter.Type == (int)AnimatorControllerParameterType.Int) animator.SetInteger(parameter.Hash, parameter.Int);
                else if (parameter.Type == (int)AnimatorControllerParameterType.Bool) animator.SetBool(parameter.Hash, parameter.Bool);
            }
            for (int i = 0; i < Math.Min(animator.layerCount, state.Layers.Length); i++)
            {
                var layer = state.Layers[i]; animator.SetLayerWeight(i, Mathf.Clamp01(layer.Weight));
                if (layer.StateHash != 0 && animator.HasState(i, layer.StateHash)) animator.Play(layer.StateHash, i, layer.NormalizedTime);
            }
            // Play selects state/time but does not evaluate a disabled animator's
            // skeleton. Evaluate immediately so newly loaded NPCs cannot retain
            // the bind pose until the next native AI tick (which is blocked).
            animator.Update(0);
            return true;
        }

        private static void RemoveExtraGhosts(GhostManager manager, HashSet<string> keep, long sequence)
        {
            var remove = new List<AgentGhost>();
            foreach (var ghost in manager._agentGhosts)
                if (ghost != null && !keep.Contains(ghost.Id) && (!LastStateSequence.TryGetValue(ghost.Id, out long last) || last <= sequence)) remove.Add(ghost);
            foreach (var ghost in remove)
            {
                if (Replicas.TryGetValue(ghost.Id, out var replica)) RestoreRendering(replica);
                ghost.IsActive = false; manager.UpdateGhostViewPresence(ghost); manager.DeregisterGhostImmediate(ghost);
                Replicas.Remove(ghost.Id); LastStateSequence.Remove(ghost.Id);
            }
        }

        private static bool HasRenderableView(AgentGhost ghost, Character character)
        {
            if (character == null || !character.gameObject.activeInHierarchy || ghost == null || !ghost.IsValid || !ghost.HasLoadedView) return false;
            var view = ghost.LoadedView.TryCast<Agent>();
            // Pooled/inactive characters may retain their old ghost reference.
            // Only the ghost's current canonical view owns its render state.
            return view != null && view.Character == character;
        }

        /// <summary>Call every client Update, including during scene transitions.</summary>
        public static void Tick()
        {
            if (!clientAuthority) return;
            if (Time.realtimeSinceStartup >= nextCleanup)
            {
                nextCleanup = Time.realtimeSinceStartup + 0.5f;
                var characters = CharacterManager.Instance;
                if (characters != null && !CrowdReplication.ClientUsesLocalDecorations) { characters.CancelSetFakeTask(); characters.RemoveFakeCharacters(); }
                // Freeze already loaded save characters before the first network
                // frame, and newly pooled scene views before their next snapshot.
                foreach (var character in Character.instances)
                {
                    if (character == null || character.Agent == null) continue;
                    var ghost = character.Agent.MyGhost;
                    if (!HasRenderableView(ghost, character)) continue;
                    if (!Replicas.TryGetValue(ghost.Id, out var replica))
                    {
                        replica = new Replica { Ghost = ghost, Position = ghost.Position, Rotation = ghost.Rotation };
                        Replicas.Add(ghost.Id, replica);
                    }
                    BindCharacter(replica, character); FreezeRendering(replica);
                }
            }
            TickRendering();
        }

        // Presentation reset only for the session's completed map-load barrier.
        public static void ResetPlaybackAfterMapLoad()
        {
            if (!clientAuthority) return;
            // The session invokes this once at a map-loading barrier release,
            // after its retained logical events have caught up. Loading may
            // have queued minutes of old samples; a newly presented map starts
            // at its latest confirmed pose, without replaying that travel gap.
            // Ordinary live hitches and economy preparation never call this.
            foreach (var replica in Replicas.Values)
            {
                if (!HasRenderableView(replica.Ghost, replica.Character)) continue;
                replica.Playback.Add(replica.LastSourceTime, new RenderPose(replica.Position, replica.Rotation), true);
                replica.RenderMotionSpeed = replica.RenderSpeed = 0;
            }
        }

        // Safe during managed economy preparation: existing visual replicas
        // only. No ghost creation, rebinding, native AI, navigation or cleanup.
        public static void TickRendering()
        {
            if (!clientAuthority) return;
            float now = Time.realtimeSinceStartup, deltaTime = Time.unscaledDeltaTime;
            foreach (var replica in Replicas.Values)
            {
                var character = replica.Character;
                if (!HasRenderableView(replica.Ghost, character))
                { replica.AnimationCadence.Reset(); replica.AnimationIdentitySeen = false; continue; }
                FreezeRendering(replica);
                var transform = character.ThisTransform;
                var before = replica.Playback.Current;
                var rendered = replica.Playback.Advance(deltaTime);
                var distance = rendered.Position - before.Position; distance.y = 0;
                replica.RenderMotionSpeed = distance.sqrMagnitude > .00000001f ? 1 : 0;
                transform.SetPositionAndRotation(rendered.Position, rendered.Rotation); replica.Placed = true;
                var animator = character.Animator;
                if (animator == null) { replica.AnimationCadence.Reset(); replica.AnimationIdentitySeen = false; continue; }
                bool identityChanged = RefreshAnimationIdentity(replica, animator, now);
                if (!replica.AnimationCadence.Advance(deltaTime, character.CameraVisible,
                    identityChanged || !animator.isInitialized || (!replica.HasWorkerAnimation && !replica.FallbackInitialized), out float animationDelta)) continue;
                if (replica.HasWorkerAnimation)
                {
                    if (now - replica.LastPoseTime <= replica.Playback.FreshnessSeconds && PrepareReplicaAnimator(animator))
                        animator.Update(animationDelta);
                }
                else TickFallbackAnimation(replica, animationDelta, now);
            }
        }

        private static bool RefreshAnimationIdentity(Replica replica, Animator animator, float now)
        {
            var controller = animator.runtimeAnimatorController;
            var pooled = replica.Character.PooledElement;
            IntPtr controllerPointer = controller == null ? IntPtr.Zero : controller.Pointer;
            IntPtr poolPointer = pooled == null ? IntPtr.Zero : pooled.Pointer;
            ulong poolVersion = pooled == null ? 0 : pooled.Version;
            if (replica.AnimationIdentitySeen && replica.AnimationAnimator == animator.Pointer &&
                replica.AnimationController == controllerPointer && replica.AnimationPool == poolPointer && replica.AnimationPoolVersion == poolVersion) return false;
            bool previouslyBound = replica.AnimationIdentitySeen;
            replica.AnimationIdentitySeen = true; replica.AnimationAnimator = animator.Pointer;
            replica.AnimationController = controllerPointer; replica.AnimationPool = poolPointer; replica.AnimationPoolVersion = poolVersion;
            replica.AnimationCadence.Reset(); replica.FallbackInitialized = false; replica.RenderSpeed = 0;
            // A new animator or a reused instance must evaluate its own pose.
            // Preserve valid worker action animation by reapplying the fresh
            // snapshot, rather than discarding it merely to reset scheduling.
            if (replica.HasWorkerAnimation && replica.WorkerAnimation != null)
                replica.HasWorkerAnimation = now - replica.LastPoseTime <= replica.Playback.FreshnessSeconds && ApplyAnimation(replica.Character, replica.WorkerAnimation);
            else if (previouslyBound) replica.HasWorkerAnimation = false;
            return true;
        }

        private static void BindCharacter(Replica replica, Character character)
        {
            if (character == replica.Character) return;
            RestoreRendering(replica); replica.Character = character; replica.Placed = false;
            replica.Playback.Clear(); replica.RenderMotionSpeed = 0;
            replica.AnimationCadence.Reset(); replica.AnimationIdentitySeen = false; replica.WorkerAnimation = null;
            // A pooled view has its own animator state. A valid snapshot on the
            // previous view must not bypass initialization of this skeleton.
            replica.HasWorkerAnimation = false; replica.FallbackInitialized = false; replica.RenderSpeed = 0;
            if (character == null) return;
            replica.Playback.Add(replica.LastSourceTime, new RenderPose(replica.Position, replica.Rotation), true);
            var animator = character.Animator;
            replica.OriginalRootMotion = animator != null && animator.applyRootMotion;
            replica.OriginalFireEvents = animator != null && animator.fireEvents;
            replica.OriginalCullingMode = animator == null ? AnimatorCullingMode.AlwaysAnimate : animator.cullingMode;
            var movement = character.Movement;
            if (movement != null)
            {
                var agent = movement.Agent; var obstacle = movement.Obstacle;
                replica.OriginalNavEnabled = agent != null && agent.enabled;
                replica.OriginalObstacleEnabled = obstacle != null && obstacle.enabled;
            }
            // CancelAction cancels/disposes the running action's token. Its
            // SetTask callback is suppressed by the client-authority prefix.
            if (character.cancelSource != null) character.CancelAction();
        }

        private static void FreezeRendering(Replica replica)
        {
            var character = replica.Character;
            if (character == null) return;
            // Keep authority checks every frame, but resolve each native
            // component once and avoid repeated setters on already-guarded
            // Animator/NavMesh components. No persistent cache survives pooling.
            var animator = character.Animator;
            if (animator != null)
            {
                if (animator.applyRootMotion) animator.applyRootMotion = false;
                if (animator.fireEvents) animator.fireEvents = false;
                if (animator.cullingMode != AnimatorCullingMode.AlwaysAnimate) animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                CrowdReplication.SetAuthorityAnimator(animator, true);
            }
            var movement = character.Movement;
            if (movement == null) return;
            var agent = movement.Agent; var obstacle = movement.Obstacle;
            if (agent != null && agent.enabled) agent.enabled = false;
            if (obstacle != null && obstacle.enabled) obstacle.enabled = false;
        }
        private static void RestoreRendering(Replica replica)
        {
            var character = replica.Character;
            if (character == null) return;
            var animator = character.Animator;
            if (animator != null)
            {
                CrowdReplication.SetAuthorityAnimator(animator, false);
                if (animator.applyRootMotion != replica.OriginalRootMotion) animator.applyRootMotion = replica.OriginalRootMotion;
                if (animator.fireEvents != replica.OriginalFireEvents) animator.fireEvents = replica.OriginalFireEvents;
                if (animator.cullingMode != replica.OriginalCullingMode) animator.cullingMode = replica.OriginalCullingMode;
            }
            var movement = character.Movement;
            if (movement == null) return;
            var agent = movement.Agent; var obstacle = movement.Obstacle;
            if (agent != null && agent.enabled != replica.OriginalNavEnabled) agent.enabled = replica.OriginalNavEnabled;
            if (obstacle != null && obstacle.enabled != replica.OriginalObstacleEnabled) obstacle.enabled = replica.OriginalObstacleEnabled;
        }
    }
}
