using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using Nivalis;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace NivalisTogether.Dedicated
{
    internal static class CrowdDecorationPolicy
    {
        public static bool AllowsLocalSimulation(bool clientAuthority, int protocol, bool realCharacter, bool hasGhost) =>
            clientAuthority && protocol >= 8 && !realCharacter && !hasGhost;
    }
    public sealed class CrowdFrame
    {
        public long Sequence { get; set; }
        public int ChunkIndex { get; set; }
        public int ChunkCount { get; set; } = 1;
        public CrowdState[] Actors { get; set; } = Array.Empty<CrowdState>();
    }
    public sealed class CrowdState
    {
        public string Id { get; set; } = "";
        public string PrefabId { get; set; } = "";
        public int Scene { get; set; }
        public float X { get; set; } public float Y { get; set; } public float Z { get; set; }
        public float Qx { get; set; } public float Qy { get; set; } public float Qz { get; set; } public float Qw { get; set; } = 1;
        public float Sx { get; set; } = 1; public float Sy { get; set; } = 1; public float Sz { get; set; } = 1;
        public AnimatorLayerSnapshot[] Layers { get; set; } = Array.Empty<AnimatorLayerSnapshot>();
        public AnimatorParameterSnapshot[] Parameters { get; set; } = Array.Empty<AnimatorParameterSnapshot>();
        public CrowdRendererState[] Renderers { get; set; } = Array.Empty<CrowdRendererState>();
    }
    public sealed class CrowdRendererState
    {
        public string Path { get; set; } = "";
        public bool Active { get; set; }
        public float[] BlendShapes { get; set; } = Array.Empty<float>();
    }

    /// <summary>Decorative actors only. Protocol 8 lets each client run native ghostless fillers for its own map; real NPC ghosts remain worker-owned.</summary>
    public static class CrowdReplication
    {
        private const int MaximumActors = 2048, MaximumChunks = 512, MaximumTransforms = 512, MaximumVisible = 256;
        private const float DrawDistance = 160;
        private static readonly Harmony Patches = new Harmony("local.nivalis.together.crowds");
        private static readonly Dictionary<int, Identity> Identities = new Dictionary<int, Identity>();
        private static readonly Dictionary<int, string> PrefabIds = new Dictionary<int, string>();
        private static readonly Dictionary<string, Replica> Replicas = new Dictionary<string, Replica>();
        private static readonly Dictionary<long, Roster> Rosters = new Dictionary<long, Roster>();
        private static readonly Dictionary<int, Animator> VisualAnimators = new Dictionary<int, Animator>();
        private static bool clientAuthority, patched;
        private static int initializedWorkerScene = int.MinValue;
        private static long sequence, completedSequence = -1;
        private static float nextPrefabScan, nextDiagnostic;
        private static int localCrowdScene = int.MinValue, localCrowdMinute = int.MinValue;
        private static bool UsesLocalDecorations => DedicatedProtocol.Version >= 8;
        public static bool ClientUsesLocalDecorations => clientAuthority && UsesLocalDecorations;
        public static bool IsLocalDecoration(Character character)
        {
            // This guard runs for each native NPC update/animation callback.
            // Real characters never qualify; avoid creating Agent/Ghost wrappers
            // for them, and resolve a decorative character's Agent only once.
            // All observations remain fresh for this invocation/pool lifetime.
            if (!clientAuthority || !UsesLocalDecorations || character == null || character.Type == Character.CharacterType.Real) return false;
            var agent = character.Agent;
            return agent == null || agent.MyGhost == null;
        }
        public static Action<string> Diagnostic { get; set; }
        public static int LastCapturedCount { get; private set; }
        public static int UnresolvedPrefabCount { get; private set; }
        public static int RenderedCount { get; private set; }
        private sealed class Identity { public Character Character; public ulong PoolVersion; public string Id; }
        private sealed class Replica
        {
            public CrowdState State; public long Sequence, AppliedSequence = -1;
            public GameObject Root; public Animator Animator;
            public readonly Dictionary<string, Renderer> Renderers = new Dictionary<string, Renderer>();
            public bool Placed; public float RetryAfter;
        }
        private sealed class Roster
        {
            public int Expected;
            public readonly HashSet<int> Chunks = new HashSet<int>();
            public readonly HashSet<string> Ids = new HashSet<string>();
        }

        public static void SetClientAuthority(bool enabled)
        {
            clientAuthority = enabled;
            localCrowdScene = localCrowdMinute = int.MinValue;
            if (enabled) { InstallPatches(); if (!UsesLocalDecorations) return; }
            foreach (Replica replica in Replicas.Values) DestroyVisual(replica);
            Replicas.Clear(); Rosters.Clear(); completedSequence = -1; RenderedCount = 0;
        }
        private static void PrepareWorker(CharacterManager manager)
        {
            InstallPatches();
            manager.FullAnimation = true;
            if (initializedWorkerScene != manager.scene)
            {
                initializedWorkerScene = manager.scene;
                // Preserve the native population/task lifecycle and its spawn
                // visibility/distance rules. FullAnimation keeps worker animation active.
                manager.OnMinuteUpdate(TimeOfDayManager.CurrentTime);
            }
        }
        private static void InstallPatches()
        {
            if (!patched)
            {
                // SetFakeTask takes a native CancellationToken value wrapper.
                // Harmony's input conversion corrupted its native cancellation
                // source. Its MoveNext/closure also have native value-type receivers
                // which Harmony copies without writeback. Leave this entire native
                // asynchronous path untouched; only replica animation callbacks need guards.
                var guard = new HarmonyMethod(typeof(CrowdReplication).GetMethod(nameof(AllowAnimationCallback), BindingFlags.NonPublic | BindingFlags.Static));
                // These are all StateMachineBehaviour subclasses in the inspected Assembly-CSharp build.
                foreach (string name in new[] { "AnimStateBroadcaster", "AnimatorEmoter", "WoofTools.StateMachineBehaviours.SMB_ForceAnimationEvent", "Assets._Work_Ben._CookingPrototype.Scripts.Utility.SMB_EventOnLoopEnd" })
                {
                    Type type = AccessTools.TypeByName(name); if (type == null) throw new TypeLoadException(name);
                    foreach (string callback in new[] { "OnStateEnter", "OnStateExit", "OnStateUpdate" })
                    {
                        var target = type.GetMethod(callback, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                        if (target != null) Patches.Patch(target, prefix: guard);
                    }
                }
                patched = true;
            }
        }
        private static bool AllowAnimationCallback(Animator __0) => __0 == null || !VisualAnimators.ContainsKey(__0.GetInstanceID());
        internal static void SetAuthorityAnimator(Animator animator, bool registered)
        {
            if (animator == null) return;
            if (registered) { InstallPatches(); VisualAnimators[animator.GetInstanceID()] = animator; }
            else VisualAnimators.Remove(animator.GetInstanceID());
        }

        public static CrowdFrame Capture()
        {
            if (clientAuthority) return null;
            var manager = CharacterManager.Instance;
            if (manager == null || !manager.IsLoaded || GameSceneManager.Instance == null || GameSceneManager.Instance.IsLoading) return null;
            PrepareWorker(manager);
            var serialization = SerializationManager.Instance;
            if (serialization == null || serialization.Prefabs == null) return null;
            if (Time.realtimeSinceStartup >= nextPrefabScan)
            {
                nextPrefabScan = Time.realtimeSinceStartup + 5;
                PrefabIds.Clear();
                foreach (var pair in serialization.Prefabs.prefabDictionary)
                    if (pair.Value != null && DedicatedProtocol.Text(pair.Key, 256)) PrefabIds[pair.Value.GetInstanceID()] = pair.Key;
            }
            var states = new List<CrowdState>(); var seen = new HashSet<int>(); int unresolved = 0;
            foreach (Character character in Character.instances)
            {
                if (character == null || !character.gameObject.activeInHierarchy || character.Type == Character.CharacterType.Real ||
                    (character.Agent != null && character.Agent.MyGhost != null)) continue;
                var pooled = character.PooledElement;
                var original = pooled == null ? null : pooled.Original;
                if (original == null) { unresolved++; continue; }
                if (!PrefabIds.TryGetValue(original.GetInstanceID(), out string prefabId))
                {
                    var serializable = original.GetComponent<SerializableObject>();
                    prefabId = serializable == null ? "" : serializable.GetPrefabGuid();
                    if (!DedicatedProtocol.Text(prefabId, 256) || !serialization.Prefabs.prefabDictionary.ContainsKey(prefabId)) { unresolved++; continue; }
                }
                int key = character.GetInstanceID(); ulong version = pooled.Version;
                if (!Identities.TryGetValue(key, out Identity identity) || identity.Character != character || identity.PoolVersion != version)
                { identity = new Identity { Character = character, PoolVersion = version, Id = Guid.NewGuid().ToString("N") }; Identities[key] = identity; }
                seen.Add(key);
                // Original identifies the complete pooled prefab, which can have Character below its root.
                var transform = pooled.ThisTransform; var p = transform.position; var q = transform.rotation; var s = transform.lossyScale;
                var state = new CrowdState { Id = identity.Id, PrefabId = prefabId, Scene = character.gameObject.scene.buildIndex,
                    X = p.x, Y = p.y, Z = p.z, Qx = q.x, Qy = q.y, Qz = q.z, Qw = q.w, Sx = s.x, Sy = s.y, Sz = s.z };
                if (state.Scene < 0) continue;
                CaptureAnimation(character.Animator, state);
                state.Renderers = CaptureRenderers(transform);
                Validate(state); states.Add(state);
                if (states.Count > MaximumActors) throw new InvalidOperationException("Native decorative crowd exceeds the supported 2048-actor roster.");
            }
            foreach (int id in Identities.Keys.Where(id => !seen.Contains(id)).ToArray()) Identities.Remove(id);
            LastCapturedCount = states.Count; UnresolvedPrefabCount = unresolved;
            if (unresolved > 0 && Time.realtimeSinceStartup >= nextDiagnostic)
            { nextDiagnostic = Time.realtimeSinceStartup + 10; Diagnostic?.Invoke("Crowd capture: " + states.Count + " native actors; " + unresolved + " skipped because no verified prefab identity was available."); }
            return new CrowdFrame { Sequence = ++sequence, Actors = states.ToArray() };
        }

        public static IReadOnlyList<WorldEvent> CaptureEvents()
        {
            // A decorative roster is partial to the worker's resident scene. It
            // cannot authoritatively replace another client's map population.
            if (UsesLocalDecorations) return Array.Empty<WorldEvent>();
            CrowdFrame frame = Capture(); if (frame == null) return Array.Empty<WorldEvent>();
            var chunks = new List<CrowdFrame>(); var group = new List<CrowdState>(); int size = 256;
            foreach (CrowdState state in frame.Actors)
            {
                int actorBytes = JsonSerializer.SerializeToUtf8Bytes(state, DedicatedProtocol.Json).Length + 1;
                if (actorBytes > 44000) throw new InvalidOperationException("Decorative actor state exceeds its packet budget.");
                if (size + actorBytes > 44000) { chunks.Add(new CrowdFrame { Sequence = frame.Sequence, Actors = group.ToArray() }); group.Clear(); size = 256; }
                group.Add(state); size += actorBytes;
            }
            if (group.Count > 0 || chunks.Count == 0) chunks.Add(new CrowdFrame { Sequence = frame.Sequence, Actors = group.ToArray() });
            if (chunks.Count > MaximumChunks) throw new InvalidOperationException("Crowd roster exceeds the chunk budget.");
            var events = new List<WorldEvent>(chunks.Count);
            for (int i = 0; i < chunks.Count; i++)
            {
                chunks[i].ChunkIndex = i; chunks[i].ChunkCount = chunks.Count;
                var data = JsonSerializer.SerializeToElement(chunks[i], DedicatedProtocol.Json);
                events.Add(new WorldEvent { Kind = "crowds", Data = data });
            }
            return events;
        }
        private static void CaptureAnimation(Animator animator, CrowdState state)
        {
            if (animator == null) return;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            if (!animator.isInitialized) return;
            var layers = new List<AnimatorLayerSnapshot>();
            for (int i = 0; i < Math.Min(animator.layerCount, 8); i++)
            {
                var current = animator.GetCurrentAnimatorStateInfo(i);
                layers.Add(new AnimatorLayerSnapshot { StateHash = current.fullPathHash, NormalizedTime = current.normalizedTime, Weight = animator.GetLayerWeight(i) });
            }
            var parameters = new List<AnimatorParameterSnapshot>();
            foreach (var parameter in animator.parameters)
            {
                var value = new AnimatorParameterSnapshot { Hash = parameter.nameHash, Type = (int)parameter.type };
                if (parameter.type == AnimatorControllerParameterType.Float) value.Float = animator.GetFloat(parameter.nameHash);
                else if (parameter.type == AnimatorControllerParameterType.Int) value.Int = animator.GetInteger(parameter.nameHash);
                else if (parameter.type == AnimatorControllerParameterType.Bool) value.Bool = animator.GetBool(parameter.nameHash);
                else continue;
                parameters.Add(value); if (parameters.Count == 96) break;
            }
            state.Layers = layers.ToArray(); state.Parameters = parameters.ToArray();
        }
        private static CrowdRendererState[] CaptureRenderers(Transform root)
        {
            var result = new List<CrowdRendererState>();
            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                var skinned = renderer.TryCast<SkinnedMeshRenderer>();
                if (skinned == null && renderer.TryCast<MeshRenderer>() == null) continue;
                string path = RelativePath(root, renderer.transform); if (!DedicatedProtocol.Text(path, 256, true)) continue;
                var state = new CrowdRendererState { Path = path, Active = renderer.gameObject.activeInHierarchy };
                if (skinned != null && skinned.sharedMesh != null)
                {
                    state.BlendShapes = new float[Math.Min(64, skinned.sharedMesh.blendShapeCount)];
                    for (int i = 0; i < state.BlendShapes.Length; i++) state.BlendShapes[i] = skinned.GetBlendShapeWeight(i);
                }
                result.Add(state); if (result.Count >= 64) break;
            }
            return result.ToArray();
        }

        public static void Apply(WorldEvent worldEvent)
        {
            if (!clientAuthority || worldEvent == null || worldEvent.Kind != "crowds") return;
            if (UsesLocalDecorations) return;
            CrowdFrame frame = worldEvent.Data.Deserialize<CrowdFrame>(DedicatedProtocol.Json);
            if (frame == null || frame.Sequence <= completedSequence) return;
            if (frame.Sequence <= 0 || frame.ChunkCount < 1 || frame.ChunkCount > MaximumChunks || frame.ChunkIndex < 0 || frame.ChunkIndex >= frame.ChunkCount || frame.Actors == null || frame.Actors.Length > MaximumActors) throw new ArgumentException("Invalid crowd frame.");
            foreach (CrowdState state in frame.Actors) Validate(state);
            if (!Rosters.TryGetValue(frame.Sequence, out Roster roster))
            {
                if (Rosters.Count >= 4) Rosters.Remove(Rosters.Keys.Min());
                roster = new Roster { Expected = frame.ChunkCount }; Rosters.Add(frame.Sequence, roster);
            }
            if (roster.Expected != frame.ChunkCount) throw new ArgumentException("Crowd chunk count changed.");
            if (!roster.Chunks.Add(frame.ChunkIndex)) return;
            foreach (CrowdState state in frame.Actors)
            {
                roster.Ids.Add(state.Id); if (roster.Ids.Count > MaximumActors) throw new ArgumentException("Crowd roster limit exceeded.");
                if (!Replicas.TryGetValue(state.Id, out Replica replica))
                {
                    if (Replicas.Count >= MaximumActors * 4) throw new ArgumentException("Incomplete crowd state limit exceeded.");
                    replica = new Replica(); Replicas.Add(state.Id, replica);
                }
                if (frame.Sequence < replica.Sequence) continue;
                if (replica.State != null && replica.State.PrefabId != state.PrefabId) DestroyVisual(replica);
                replica.State = state; replica.Sequence = frame.Sequence;
            }
            if (roster.Chunks.Count == roster.Expected)
            {
                foreach (string id in Replicas.Keys.Where(id => !roster.Ids.Contains(id) && Replicas[id].Sequence <= frame.Sequence).ToArray())
                { DestroyVisual(Replicas[id]); Replicas.Remove(id); }
                completedSequence = frame.Sequence;
                foreach (long value in Rosters.Keys.Where(value => value <= frame.Sequence).ToArray()) Rosters.Remove(value);
            }
        }
        private static void Validate(CrowdState state)
        {
            if (state == null || !Guid.TryParseExact(state.Id, "N", out _) || !DedicatedProtocol.Text(state.PrefabId, 256) || state.Scene < 0 || state.Scene > 100000 ||
                !Coordinate(state.X) || !Coordinate(state.Y) || !Coordinate(state.Z) || !float.IsFinite(state.Qx) || !float.IsFinite(state.Qy) || !float.IsFinite(state.Qz) || !float.IsFinite(state.Qw) ||
                !Scale(state.Sx) || !Scale(state.Sy) || !Scale(state.Sz) || state.Layers == null || state.Layers.Length > 8 || state.Parameters == null || state.Parameters.Length > 96 || state.Renderers == null || state.Renderers.Length > 64)
                throw new ArgumentException("Invalid decorative crowd actor.");
            foreach (var layer in state.Layers) if (layer == null || !float.IsFinite(layer.NormalizedTime) || !float.IsFinite(layer.Weight)) throw new ArgumentException("Invalid crowd animation layer.");
            foreach (var parameter in state.Parameters) if (parameter == null || !float.IsFinite(parameter.Float)) throw new ArgumentException("Invalid crowd animation parameter.");
            foreach (var renderer in state.Renderers)
                if (renderer == null || !DedicatedProtocol.Text(renderer.Path, 256, true) || renderer.BlendShapes == null || renderer.BlendShapes.Length > 64 || renderer.BlendShapes.Any(v => !float.IsFinite(v) || Math.Abs(v) > 1000)) throw new ArgumentException("Invalid crowd appearance.");
        }
        private static bool Coordinate(float value) => float.IsFinite(value) && Math.Abs(value) <= 1000000;
        private static bool Scale(float value) => float.IsFinite(value) && Math.Abs(value) >= .001f && Math.Abs(value) <= 20;

        public static void Tick()
        {
            if (!clientAuthority) return;
            foreach (int id in VisualAnimators.Keys.Where(id => VisualAnimators[id] == null).ToArray()) VisualAnimators.Remove(id);
            if (UsesLocalDecorations)
            {
                var manager = CharacterManager.Instance;
                if (manager == null || !manager.IsLoaded || !DedicatedWorldRuntime.IsWorldReady) return;
                int minute = TimeOfDayManager.TotalGameSeconds / 60;
                if (manager.scene != localCrowdScene || minute != localCrowdMinute)
                {
                    // The replicated clock deliberately emits no native global
                    // time events. Refresh only cosmetic filler population here.
                    manager.OnMinuteUpdate(TimeOfDayManager.CurrentTime);
                    localCrowdScene = manager.scene; localCrowdMinute = minute;
                }
                return;
            }
            var camera = Camera.main; int createBudget = 2, rendered = 0;
            float blend = 1 - Mathf.Exp(-12 * Time.unscaledDeltaTime);
            foreach (Replica replica in Replicas.Values)
            {
                CrowdState state = replica.State; if (state == null) continue;
                var scene = SceneManager.GetSceneByBuildIndex(state.Scene); var position = new Vector3(state.X, state.Y, state.Z);
                bool visible = camera != null && scene.IsValid() && scene.isLoaded && (camera.transform.position - position).sqrMagnitude <= DrawDistance * DrawDistance && rendered < MaximumVisible;
                if (!visible) { if (replica.Root != null) DestroyVisual(replica); continue; }
                rendered++;
                if (replica.Root == null && createBudget > 0 && Time.realtimeSinceStartup >= replica.RetryAfter)
                {
                    createBudget--;
                    try { CreateVisual(replica); }
                    catch (Exception ex) { DestroyVisual(replica); replica.RetryAfter = Time.realtimeSinceStartup + 10; Diagnostic?.Invoke("Crowd visual unavailable: " + ex.Message); }
                }
                if (replica.Root == null) continue;
                var q = new Quaternion(state.Qx, state.Qy, state.Qz, state.Qw); float magnitude = q.x*q.x+q.y*q.y+q.z*q.z+q.w*q.w;
                var rotation = magnitude > .000001f && float.IsFinite(magnitude) ? Quaternion.Normalize(q) : Quaternion.identity;
                bool snap = !replica.Placed || (replica.Root.transform.position - position).sqrMagnitude > 144;
                replica.Root.transform.SetPositionAndRotation(snap ? position : Vector3.Lerp(replica.Root.transform.position, position, blend), snap ? rotation : Quaternion.Slerp(replica.Root.transform.rotation, rotation, blend));
                replica.Root.transform.localScale = new Vector3(state.Sx, state.Sy, state.Sz); replica.Placed = true;
                if (replica.AppliedSequence != replica.Sequence && ApplyVisualState(replica)) replica.AppliedSequence = replica.Sequence;
            }
            RenderedCount = Replicas.Values.Count(r => r.Root != null);
        }

        private static void CreateVisual(Replica replica)
        {
            var serialization = SerializationManager.Instance;
            if (serialization == null || serialization.Prefabs == null || !serialization.Prefabs.prefabDictionary.TryGetValue(replica.State.PrefabId, out GameObject prefab) || prefab == null)
                throw new InvalidOperationException("Native prefab is unavailable: " + replica.State.PrefabId);
            // Build only transforms/renderer/Animator components. Cloning an entire NPC prefab would execute native Awake/save/ghost registration hooks.
            var map = new Dictionary<int, Transform>(); var rendererMap = new Dictionary<int, Renderer>();
            replica.Root = new GameObject(prefab.name); replica.Root.layer = prefab.layer; replica.Root.SetActive(false); Object.DontDestroyOnLoad(replica.Root);
            map.Add(prefab.transform.GetInstanceID(), replica.Root.transform);
            CopyHierarchy(prefab.transform, replica.Root.transform, map);
            foreach (var source in prefab.GetComponentsInChildren<MeshRenderer>(true))
            {
                var filter = source.GetComponent<MeshFilter>(); if (filter == null || filter.sharedMesh == null) continue;
                var target = map[source.transform.GetInstanceID()].gameObject;
                target.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
                var renderer = target.AddComponent<MeshRenderer>(); renderer.sharedMaterials = source.sharedMaterials;
                renderer.shadowCastingMode = source.shadowCastingMode; renderer.receiveShadows = source.receiveShadows;
                replica.Renderers[RelativePath(prefab.transform, source.transform)] = renderer;
                rendererMap[source.GetInstanceID()] = renderer;
            }
            foreach (var source in prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (source.sharedMesh == null) continue;
                var target = map[source.transform.GetInstanceID()].gameObject.AddComponent<SkinnedMeshRenderer>();
                target.sharedMesh = source.sharedMesh; target.sharedMaterials = source.sharedMaterials; target.localBounds = source.localBounds;
                target.quality = source.quality; target.updateWhenOffscreen = true; target.shadowCastingMode = source.shadowCastingMode; target.receiveShadows = source.receiveShadows;
                var bones = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Transform>(source.bones.Length);
                for (int i = 0; i < bones.Length; i++) if (source.bones[i] != null && map.TryGetValue(source.bones[i].GetInstanceID(), out Transform bone)) bones[i] = bone;
                target.bones = bones;
                if (source.rootBone != null && map.TryGetValue(source.rootBone.GetInstanceID(), out Transform rootBone)) target.rootBone = rootBone;
                replica.Renderers[RelativePath(prefab.transform, source.transform)] = target;
                rendererMap[source.GetInstanceID()] = target;
            }
            foreach (var source in prefab.GetComponentsInChildren<LODGroup>(true))
            {
                var target = map[source.transform.GetInstanceID()].gameObject.AddComponent<LODGroup>();
                target.fadeMode = source.fadeMode; target.animateCrossFading = source.animateCrossFading;
                target.localReferencePoint = source.localReferencePoint; target.size = source.size;
                var originalLods = source.GetLODs();
                var copies = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<LOD>(originalLods.Length);
                for (int i = 0; i < originalLods.Length; i++)
                {
                    var renderers = new List<Renderer>();
                    foreach (Renderer originalRenderer in originalLods[i].renderers)
                        if (originalRenderer != null && rendererMap.TryGetValue(originalRenderer.GetInstanceID(), out Renderer renderer)) renderers.Add(renderer);
                    var lodRenderers = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Renderer>(renderers.Count);
                    for (int j = 0; j < renderers.Count; j++) lodRenderers[j] = renderers[j];
                    copies[i] = new LOD(originalLods[i].screenRelativeTransitionHeight, lodRenderers) { fadeTransitionWidth = originalLods[i].fadeTransitionWidth };
                }
                target.SetLODs(copies); target.enabled = source.enabled;
            }
            var sourceAnimator = prefab.GetComponentInChildren<Animator>(true);
            if (sourceAnimator != null)
            {
                replica.Animator = map[sourceAnimator.transform.GetInstanceID()].gameObject.AddComponent<Animator>();
                VisualAnimators[replica.Animator.GetInstanceID()] = replica.Animator;
                replica.Animator.fireEvents = false;
                replica.Animator.avatar = sourceAnimator.avatar; replica.Animator.runtimeAnimatorController = sourceAnimator.runtimeAnimatorController;
                replica.Animator.applyRootMotion = false; replica.Animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }
            if (replica.Renderers.Count == 0) throw new InvalidOperationException("Native crowd prefab has no supported mesh renderers.");
            replica.Root.name = "NivalisTogether.Crowd." + replica.State.Id;
            replica.Root.SetActive(true); replica.Placed = false; replica.AppliedSequence = -1;
        }
        private static void CopyHierarchy(Transform source, Transform target, Dictionary<int, Transform> map)
        {
            for (int i = 0; i < source.childCount; i++)
            {
                if (map.Count >= MaximumTransforms) throw new InvalidOperationException("Crowd skeleton hierarchy exceeds 512 transforms.");
                Transform original = source.GetChild(i); var copy = new GameObject(original.name); copy.layer = original.gameObject.layer; copy.transform.SetParent(target, false);
                copy.transform.localPosition = original.localPosition; copy.transform.localRotation = original.localRotation; copy.transform.localScale = original.localScale;
                // Keep hierarchy active; authoritative renderer visibility is applied separately, including props removed by native SetFake.
                map.Add(original.GetInstanceID(), copy.transform); CopyHierarchy(original, copy.transform, map);
            }
        }
        private static string RelativePath(Transform root, Transform value)
        {
            var parts = new List<string>(); int depth = 0;
            while (value != root && value != null && depth++ < 32)
            {
                int ordinal = 0; var parent = value.parent;
                if (parent != null) for (int i = 0; i < value.GetSiblingIndex(); i++) if (parent.GetChild(i).name == value.name) ordinal++;
                parts.Add(value.name + "#" + ordinal); value = parent;
            }
            parts.Reverse(); return string.Join("/", parts);
        }
        private static bool ApplyVisualState(Replica replica)
        {
            CrowdState state = replica.State;
            foreach (Renderer renderer in replica.Renderers.Values) renderer.enabled = false;
            foreach (CrowdRendererState appearance in state.Renderers)
            {
                if (!replica.Renderers.TryGetValue(appearance.Path, out Renderer renderer)) continue;
                renderer.enabled = appearance.Active;
                var skinned = renderer.TryCast<SkinnedMeshRenderer>();
                if (skinned != null && skinned.sharedMesh != null)
                    for (int i = 0; i < Math.Min(appearance.BlendShapes.Length, skinned.sharedMesh.blendShapeCount); i++) skinned.SetBlendShapeWeight(i, appearance.BlendShapes[i]);
            }
            var animator = replica.Animator;
            if (animator == null) return true;
            if (!animator.isInitialized) return false;
            animator.applyRootMotion = false;
            foreach (AnimatorParameterSnapshot parameter in state.Parameters)
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
            return true;
        }
        private static void DestroyVisual(Replica replica)
        { if (replica.Root != null) Object.Destroy(replica.Root); replica.Root = null; replica.Animator = null; replica.Renderers.Clear(); replica.Placed = false; replica.AppliedSequence = -1; }
    }
}
