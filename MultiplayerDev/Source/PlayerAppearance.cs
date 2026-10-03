using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Nivalis;
using Nivalis.GhostSystem.Ai;
using NivalisTogether.Dedicated;
using UnityEngine;
using Object = UnityEngine.Object;

namespace NivalisTogether
{
    /// <summary>Unity-main-thread catalog and visual-only copies of native NPC models.</summary>
    public static class PlayerAppearance
    {
        private const int MaximumPeople = 20000, MaximumTransforms = 512, MaximumRenderers = 96, MaximumBones = 256;
        private static IntPtr catalogOwner;
        private static AppearanceOption[] catalog;

        public static void ResetCatalog() { catalogOwner = IntPtr.Zero; catalog = null; }

        // The title client gets the worker-approved catalog over the account protocol.
        // This native discovery API is called only after the worker's world is loaded.
        public static AppearanceOption[] GetCatalog()
        {
            var manager = PersonDataManager.InstanceNotNull() ? PersonDataManager.Instance : null;
            if (manager == null || manager.database == null || manager.database.People == null)
                throw new InvalidOperationException("Native appearance definitions are not ready.");
            if (catalog == null || catalogOwner != manager.Pointer)
            {
                var people = manager.database.People;
                int count = people.Count;
                if (count < 1 || count > MaximumPeople) throw new InvalidOperationException("Native person catalog size is unsupported.");
                var candidates = new List<(string Id, string Name, GameObject Prefab)>();
                var ids = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < count; i++)
                {
                    var person = people[i];
                    if (person == null || person.CharacterPrefab == null) continue;
                    string id = person.Guid;
                    if (!DedicatedProtocol.Text(id, 128) || !ids.Add(id)) continue;
                    var prefab = person.CharacterPrefab.gameObject;
                    if (prefab == null) continue;
                    string name = CleanName(person.DisplayedName);
                    if (string.IsNullOrWhiteSpace(name)) name = CleanName(person.Name);
                    if (string.IsNullOrWhiteSpace(name)) name = "Character";
                    candidates.Add((id, name, prefab));
                }
                if (people.Count != count) throw new InvalidOperationException("Native person catalog changed during discovery.");
                candidates.Sort((a, b) => StringComparer.Ordinal.Compare(a.Id, b.Id));
                var models = new HashSet<int>();
                var options = new List<AppearanceOption>();
                foreach (var candidate in candidates)
                {
                    int prefabId = candidate.Prefab.GetInstanceID();
                    if (models.Contains(prefabId) || !HasSupportedGeometry(candidate.Prefab)) continue;
                    models.Add(prefabId);
                    options.Add(new AppearanceOption { Id = candidate.Id, Name = candidate.Name });
                }
                if (options.Count == 0) throw new InvalidOperationException("No supported native NPC appearance models are available.");
                catalogOwner = manager.Pointer; catalog = options.ToArray();
            }
            var copy = new AppearanceOption[catalog.Length];
            for (int i = 0; i < copy.Length; i++) copy[i] = new AppearanceOption { Id = catalog[i].Id, Name = catalog[i].Name };
            return copy;
        }

        public static bool CanResolve(string appearanceId)
        {
            try { return ResolvePrefab(appearanceId) != null; } catch { return false; }
        }

        public static Visual BuildPreview(string appearanceId, Transform parent) => CreateVisual(appearanceId, parent);

        public static Visual CreateVisual(string appearanceId, Transform parent)
        {
            var prefab = ResolvePrefab(appearanceId);
            if (prefab == null || !HasSupportedGeometry(prefab)) throw new InvalidOperationException("Selected native character model is unavailable.");
            var visual = new Visual(appearanceId);
            try
            {
                visual.Root = new GameObject("NPC appearance " + appearanceId);
                visual.Root.SetActive(false); visual.Root.layer = 2;
                if (parent != null) visual.Root.transform.SetParent(parent, false);
                var model = new GameObject(prefab.name); model.layer = 2;
                model.transform.SetParent(visual.Root.transform, false);
                model.transform.localScale = prefab.transform.localScale;
                var map = new Dictionary<int, Transform> { [prefab.transform.GetInstanceID()] = model.transform };
                CopyHierarchy(prefab.transform, model.transform, map, 0);
                var firstLod = new HashSet<int>(); var allLods = new HashSet<int>();
                CollectLodRenderers(prefab, firstLod, allLods);
                foreach (var source in prefab.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (!IncludeRenderer(source, firstLod, allLods)) continue;
                    var filter = source.GetComponent<MeshFilter>();
                    if (filter == null || filter.sharedMesh == null) continue;
                    var node = map[source.transform.GetInstanceID()].gameObject;
                    node.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
                    var target = node.AddComponent<MeshRenderer>();
                    CopyRenderer(source, target); visual.RendererCount++;
                }
                foreach (var source in prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (!IncludeRenderer(source, firstLod, allLods) || source.sharedMesh == null) continue;
                    var target = map[source.transform.GetInstanceID()].gameObject.AddComponent<SkinnedMeshRenderer>();
                    target.sharedMesh = source.sharedMesh; target.localBounds = source.localBounds;
                    target.quality = source.quality; target.updateWhenOffscreen = true;
                    CopyRenderer(source, target);
                    var originalBones = source.bones;
                    if (originalBones == null || originalBones.Length > MaximumBones) throw new InvalidOperationException("Character bone array is unsupported.");
                    var bones = new Il2CppReferenceArray<Transform>(originalBones.Length);
                    for (int i = 0; i < originalBones.Length; i++)
                    {
                        var bone = originalBones[i];
                        if (bone == null) continue;
                        if (!map.TryGetValue(bone.GetInstanceID(), out var copy)) throw new InvalidOperationException("Character bone leaves its prefab hierarchy.");
                        bones[i] = copy;
                    }
                    target.bones = bones;
                    var originalRoot = source.rootBone;
                    if (originalRoot != null)
                    {
                        if (!map.TryGetValue(originalRoot.GetInstanceID(), out var copy)) throw new InvalidOperationException("Character root bone leaves its prefab hierarchy.");
                        target.rootBone = copy;
                    }
                    int blendShapes = source.sharedMesh.blendShapeCount;
                    if (blendShapes > 256) throw new InvalidOperationException("Character blend-shape count is unsupported.");
                    for (int i = 0; i < blendShapes; i++) target.SetBlendShapeWeight(i, source.GetBlendShapeWeight(i));
                    visual.RendererCount++;
                }
                if (visual.RendererCount == 0) throw new InvalidOperationException("Character has no supported visible meshes.");
                var sourceAnimator = prefab.GetComponentInChildren<Animator>(true);
                if (sourceAnimator != null)
                {
                    var target = map[sourceAnimator.transform.GetInstanceID()].gameObject.AddComponent<Animator>();
                    target.enabled = false; target.fireEvents = false; target.applyRootMotion = false;
                    target.avatar = sourceAnimator.avatar;
                    // No runtimeAnimatorController: no state-machine behaviours can execute.
                    visual.AnimationRoot = target.gameObject;
                    // The native NPC rigs have an authored Root above Pelvis.
                    // SampleAnimation writes that child's locomotion tracks even
                    // with Animator.applyRootMotion disabled. Never anchor Pelvis:
                    // it contains the height, sway and gait we want to display.
                    visual.MotionRoot = target.transform.Find("Root");
                    var controller = sourceAnimator.runtimeAnimatorController;
                    var clips = controller == null ? null : controller.animationClips;
                    visual.Idle = CopyPoseClip(clips, false);
                    visual.Walk = CopyPoseClip(clips, true);
                }
                visual.CaptureReferencePose(map.Values);
                visual.Root.SetActive(true);
                visual.InspectClipMotion();
                visual.Sample(0, 0);
                return visual;
            }
            catch { visual.Dispose(); throw; }
        }

        private static GameObject ResolvePrefab(string id)
        {
            if (!DedicatedProtocol.Text(id, 128)) return null;
            var manager = PersonDataManager.InstanceNotNull() ? PersonDataManager.Instance : null;
            if (manager == null) return null;
            var person = manager.TryProvide(id)?.TryCast<Person>();
            return person == null || !string.Equals(person.Guid, id, StringComparison.Ordinal) || person.CharacterPrefab == null
                ? null : person.CharacterPrefab.gameObject;
        }
        private static bool HasSupportedGeometry(GameObject prefab)
        {
            var transforms = prefab.GetComponentsInChildren<Transform>(true);
            if (transforms.Length < 1 || transforms.Length > MaximumTransforms) return false;
            var renderers = prefab.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length > MaximumRenderers) return false;
            foreach (var renderer in renderers)
            {
                var skin = renderer.TryCast<SkinnedMeshRenderer>();
                if (skin != null && skin.sharedMesh != null) return true;
                var mesh = renderer.TryCast<MeshRenderer>();
                if (mesh != null && mesh.GetComponent<MeshFilter>()?.sharedMesh != null) return true;
            }
            return false;
        }
        private static void CopyHierarchy(Transform original, Transform target, Dictionary<int, Transform> map, int depth)
        {
            if (depth > 48) throw new InvalidOperationException("Character hierarchy is too deep.");
            for (int i = 0; i < original.childCount; i++)
            {
                if (map.Count >= MaximumTransforms) throw new InvalidOperationException("Character hierarchy is too large.");
                var source = original.GetChild(i);
                var child = new GameObject(source.name); child.layer = 2;
                child.transform.SetParent(target, false);
                child.transform.localPosition = source.localPosition; child.transform.localRotation = source.localRotation;
                child.transform.localScale = source.localScale;
                child.SetActive(source.gameObject.activeSelf);
                map.Add(source.GetInstanceID(), child.transform);
                CopyHierarchy(source, child.transform, map, depth + 1);
            }
        }
        private static void CollectLodRenderers(GameObject prefab, HashSet<int> first, HashSet<int> all)
        {
            foreach (var group in prefab.GetComponentsInChildren<LODGroup>(true))
            {
                var levels = group.GetLODs();
                if (levels == null || levels.Length > 16) throw new InvalidOperationException("Character LOD array is unsupported.");
                for (int i = 0; i < levels.Length; i++)
                {
                    var level = levels[i];
                    if (ReferenceEquals(level, null)) throw new InvalidOperationException("Character LOD is missing.");
                    var renderers = level.renderers;
                    if (renderers == null || renderers.Length > MaximumRenderers) throw new InvalidOperationException("Character LOD renderer list is unsupported.");
                    foreach (var renderer in renderers) if (renderer != null)
                    { all.Add(renderer.GetInstanceID()); if (i == 0) first.Add(renderer.GetInstanceID()); }
                }
            }
        }
        private static bool IncludeRenderer(Renderer source, HashSet<int> first, HashSet<int> all) =>
            source != null && (!all.Contains(source.GetInstanceID()) || first.Contains(source.GetInstanceID()));
        private static void CopyRenderer(Renderer source, Renderer target)
        {
            target.sharedMaterials = source.sharedMaterials; target.enabled = source.enabled;
            target.shadowCastingMode = source.shadowCastingMode; target.receiveShadows = source.receiveShadows;
        }
        private static AnimationClip CopyPoseClip(Il2CppReferenceArray<AnimationClip> clips, bool walking)
        {
            if (clips == null || clips.Length > 4096) return null;
            AnimationClip best = null; int score = 0; string name = null;
            foreach (var candidate in clips)
            {
                if (candidate == null || !float.IsFinite(candidate.length) || candidate.length <= 0 || candidate.length > 120) continue;
                int candidateScore = PlayerAppearancePolicy.ClipScore(candidate.name, walking);
                if (candidateScore > score || (candidateScore > 0 && candidateScore == score && StringComparer.Ordinal.Compare(candidate.name, name) < 0))
                { best = candidate; score = candidateScore; name = candidate.name; }
            }
            if (best == null) return null;
            // Clone only the clip asset, never an NPC GameObject; strip every animation
            // event on this owned copy before any sampling, leaving the game asset intact.
            var copy = Object.Instantiate(best);
            try
            {
                copy.events = new Il2CppReferenceArray<AnimationEvent>(0);
                copy.wrapMode = WrapMode.Loop;
                return copy;
            }
            catch { if (copy != null) Object.Destroy(copy); throw; }
        }
        private static string CleanName(string value)
        {
            value = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
            return value.Length <= 80 ? value : value.Substring(0, 80);
        }

        public sealed class Visual : IDisposable
        {
            private struct TransformPose
            {
                internal Transform Transform;
                internal Vector3 Position, Scale;
                internal Quaternion Rotation;
                internal TransformPose(Transform transform)
                { Transform = transform; Position = transform.localPosition; Rotation = transform.localRotation; Scale = transform.localScale; }
                internal void Restore()
                { Transform.localPosition = Position; Transform.localRotation = Rotation; Transform.localScale = Scale; }
            }
            internal Visual(string id) { AppearanceId = id; }
            public string AppearanceId { get; }
            public GameObject Root { get; internal set; }
            public int RendererCount { get; internal set; }
            internal GameObject AnimationRoot;
            internal Transform MotionRoot;
            internal AnimationClip Idle, Walk;
            private TransformPose[] referencePose, transitionPose;
            private TransformPose animationAnchor, motionAnchor;
            private AnimationClip previousClip;
            private float idleTime, walkTime, transitionTime;
            private string motionReport = "";
            private Transform planarMotionBone;
            private Vector3 planarOrigin;
            internal void CaptureReferencePose(IEnumerable<Transform> transforms)
            {
                var pose = new List<TransformPose>();
                foreach (var transform in transforms) pose.Add(new TransformPose(transform));
                referencePose = pose.ToArray(); transitionPose = new TransformPose[referencePose.Length];
                if (AnimationRoot != null) animationAnchor = new TransformPose(AnimationRoot.transform);
                if (MotionRoot != null) motionAnchor = new TransformPose(MotionRoot);
            }
            public string AnimationDescription => "idle=" + (Idle == null ? "prefabPose" : Idle.name) + ",walk=" + (Walk == null ? "unavailable" : Walk.name) +
                ",motionRoot=" + (MotionRoot == null ? "none" : MotionRoot.name) + motionReport;
            internal void InspectClipMotion()
            {
                // Bounded, once per visual, on our event-free geometry clone. This
                // distinguishes humanoid sampling drift from network movement in
                // the diagnostic log without touching any native NPC or player.
                if (AnimationRoot == null || MotionRoot == null || Walk == null) return;
                var pelvis = MotionRoot.Find("Pelvis");
                var referencePelvis = pelvis == null ? default : pelvis.localPosition;
                Vector3 firstRoot = default, firstPelvis = default;
                float rootTravel = 0, pelvisTravel = 0;
                try
                {
                    for (int frame = 0; frame < 5; frame++)
                    {
                        foreach (var pose in referencePose) pose.Restore();
                        Walk.SampleAnimation(AnimationRoot, Walk.length * (frame == 4 ? .999f : frame * .25f));
                        var r = MotionRoot.localPosition;
                        var p = pelvis == null ? default : pelvis.localPosition;
                        if (frame == 0) { firstRoot = r; firstPelvis = p; }
                        rootTravel = Math.Max(rootTravel, PlanarDistance(r, firstRoot));
                        pelvisTravel = Math.Max(pelvisTravel, PlanarDistance(p, firstPelvis));
                    }
                    motionReport = ",sampledRootTravel=" + rootTravel.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                        ",sampledPelvisTravel=" + pelvisTravel.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
                    // These verified humanoid rigs apply baked forward motion to
                    // Pelvis rather than Root. Convert this normal-walk clip to an
                    // in-place pose: anchor horizontal translation only, keeping
                    // vertical gait, body rotation and every limb channel intact.
                    if (pelvis != null && rootTravel < .01f && pelvisTravel > .5f &&
                        Quaternion.Angle(motionAnchor.Rotation, Quaternion.identity) < .01f &&
                        Walk.name.StartsWith("IN1_X0749. Normal Walk Forward long_m_fix_2", StringComparison.Ordinal))
                    { planarMotionBone = pelvis; planarOrigin = referencePelvis; }
                    motionReport += ",walkPelvisPlanarAnchor=" + (planarMotionBone != null);
                }
                catch (Exception ex) { motionReport = ",motionProbe=" + ex.GetType().Name; }
                finally { foreach (var pose in referencePose) pose.Restore(); }
            }
            private static float PlanarDistance(Vector3 a, Vector3 b)
            { float x = a.x - b.x, z = a.z - b.z; return (float)Math.Sqrt(x * x + z * z); }
            public void Sample(float deltaSeconds, float speed)
            {
                if (Root == null || AnimationRoot == null || !Root.activeInHierarchy) return;
                var clip = speed > .08f && Walk != null ? Walk : Idle;
                if (clip == null) return;
                float delta = Mathf.Clamp(deltaSeconds, 0, .25f);
                if (previousClip != clip)
                {
                    for (int i = 0; i < referencePose.Length; i++)
                        transitionPose[i] = new TransformPose(referencePose[i].Transform);
                    transitionTime = previousClip == null ? .16f : 0;
                    previousClip = clip;
                }
                bool walking = clip == Walk;
                if (walking) walkTime = (walkTime + delta * Mathf.Clamp(speed / 1.4f, .35f, 2.5f)) % clip.length;
                else idleTime = (idleTime + delta) % clip.length;
                try
                {
                    // Unkeyed channels must start from the prefab pose on every
                    // frame, not inherit a partially blended previous activity.
                    foreach (var pose in referencePose) pose.Restore();
                    clip.SampleAnimation(AnimationRoot, walking ? walkTime : idleTime);
                    transitionTime += delta;
                    if (transitionTime < .16f)
                    {
                        float blend = Mathf.SmoothStep(0, 1, transitionTime / .16f);
                        foreach (var from in transitionPose)
                        {
                            var transform = from.Transform;
                            transform.localPosition = Vector3.Lerp(from.Position, transform.localPosition, blend);
                            transform.localRotation = Quaternion.Slerp(from.Rotation, transform.localRotation, blend);
                            transform.localScale = Vector3.Lerp(from.Scale, transform.localScale, blend);
                        }
                    }
                }
                finally
                {
                    animationAnchor.Restore();
                    if (MotionRoot != null) motionAnchor.Restore();
                    if (walking && planarMotionBone != null)
                    {
                        var position = planarMotionBone.localPosition;
                        position.x = planarOrigin.x; position.z = planarOrigin.z;
                        planarMotionBone.localPosition = position;
                    }
                }
            }
            public void Dispose()
            {
                if (Root != null) Object.Destroy(Root);
                if (Idle != null) Object.Destroy(Idle);
                if (Walk != null) Object.Destroy(Walk);
                Root = null; AnimationRoot = null; MotionRoot = null; Idle = Walk = previousClip = null;
                planarMotionBone = null;
                referencePose = transitionPose = null;
            }
        }
    }

    internal static class PlayerAppearancePolicy
    {
        internal static int ClipScore(string name, bool walking)
        {
            if (string.IsNullOrWhiteSpace(name)) return 0;
            string n = name.ToLowerInvariant();
            foreach (string excluded in new[] { "sit", "sleep", "phone", "smok", "chair", "bench", "freezing", "crouch", "eat", "serv", "dance", "talk", "laptop", "door", "lean", "work", "hold", "fish", "drunk", "hurt", "pickup", "tpose", "cough", "drink", "backward", "strafe", "stair" })
                if (n.Contains(excluded, StringComparison.Ordinal)) return 0;
            // Ordinary full-body locomotion in the native NPC controller. Activity
            // variants must not win an alphabetical tie against standing/walking.
            if (walking && n == "in1_x0749. normal walk forward long_m_fix_2") return 10;
            if (!walking && n == "in1_x0186.idle 1_m_fix_2") return 10;
            if (walking) return n == "walk" || n == "walking" ? 4 : n.Contains("walk", StringComparison.Ordinal) ? 2 : 0;
            return n == "idle" ? 4 : n.Contains("idle", StringComparison.Ordinal) ? 2 : 0;
        }
    }
}
