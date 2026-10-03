using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using NivalisTogether.Dedicated;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace NivalisTogether
{
    /// <summary>
    /// Visual replicas only: no Character, NPC, interaction, navigation, or collision components.
    /// All methods run on Unity's main thread. Supply only server-authorized remote player poses;
    /// the caller excludes its own PlayerId and calls Remove on player disconnect.
    /// </summary>
    public sealed class PlayerReplicaRenderer : IDisposable
    {
        private sealed class Replica
        {
            public PlayerPose Pose;
            public GameObject Root;
            public TextMeshPro Label;
            public PlayerAppearance.Visual Appearance;
            public GameObject Fallback;
            public string AttemptedAppearance = "";
            public float RetryAppearanceAfter;
            public bool AnimationFailed;
            public bool Placed;
            public string LastScene = "";
            public string LastProperty = "";
            public float UpdatedAt;
            public float CameraHeadDistance = -1;
            public bool OverlapHidden;
            public readonly RemoteLocomotion Locomotion = new RemoteLocomotion();
        }
        private readonly Dictionary<string, Replica> replicas = new Dictionary<string, Replica>();
        private readonly Action<string> log;
        private Material material;
        private bool disposed, reportedLabelFailure;
        private float nextDiagnostic;

        public PlayerReplicaRenderer(Action<string> log = null) { this.log = log; }

        public void Apply(PlayerPose pose)
        {
            if (disposed || pose == null || !DedicatedProtocol.PoseValid(pose) || !DedicatedProtocol.Text(pose.PlayerId, 64)) return;
            if (!replicas.TryGetValue(pose.PlayerId, out var replica))
            {
                if (replicas.Count >= 32) return;
                replica = new Replica(); replicas.Add(pose.PlayerId, replica);
            }
            bool resetMotion = replica.Pose == null || !pose.Active || !replica.Pose.Active ||
                !string.Equals(replica.Pose.Scene, pose.Scene, StringComparison.Ordinal) ||
                !string.Equals(replica.Pose.PropertyId, pose.PropertyId, StringComparison.Ordinal) ||
                replica.Pose.ContextGeneration != pose.ContextGeneration;
            replica.Locomotion.Observe(pose.X, pose.Z, Time.unscaledTime, resetMotion);
            // Copy network data so the caller can safely reuse its message instance.
            replica.Pose = new PlayerPose { PlayerId = pose.PlayerId, AccountId = pose.AccountId, AppearanceId = pose.AppearanceId ?? "",
                Name = pose.Name ?? "Player", Scene = pose.Scene, PropertyId = pose.PropertyId ?? "", ContextGeneration = pose.ContextGeneration,
                X = pose.X, Y = pose.Y, Z = pose.Z, Yaw = pose.Yaw, Active = pose.Active };
            replica.UpdatedAt = Time.unscaledTime;
        }

        public void Remove(string id)
        {
            if (id == null || !replicas.TryGetValue(id, out var replica)) return;
            replica.Appearance?.Dispose();
            if (replica.Root != null) Object.Destroy(replica.Root);
            replicas.Remove(id);
        }

        public void Tick(string localScene, bool active, string localProperty = "")
        {
            if (disposed) return;
            Camera camera = active ? Camera.main : null;
            Vector3 cameraPosition = camera != null ? camera.transform.position : default;
            float now = Time.unscaledTime;
            bool diagnostic = log != null && now >= nextDiagnostic;
            int diagnosticRows = 0;
            if (diagnostic)
            {
                nextDiagnostic = now + 10;
                log("REMOTE_PLAYERS count=" + replicas.Count + " localActive=" + active + " localScene=" + JsonSerializer.Serialize(localScene ?? "") +
                    " cameraReady=" + (camera != null && camera.isActiveAndEnabled));
            }
            float blend = 1f - Mathf.Exp(-12f * Math.Min(Time.unscaledDeltaTime, .25f));
            foreach (var pair in replicas)
            {
                var replica = pair.Value;
                var pose = replica.Pose;
                bool eligible = active && pose.Active && string.Equals(localScene, pose.Scene, StringComparison.Ordinal)
                    && string.Equals(localProperty ?? "", pose.PropertyId ?? "", StringComparison.Ordinal)
                    && now - replica.UpdatedAt < 15f;
                replica.CameraHeadDistance = -1; replica.OverlapHidden = false;
                if (replica.Root == null)
                {
                    if (!eligible)
                    {
                        if (diagnostic && diagnosticRows++ < 32) ReportReplica(pair.Key, replica, eligible, camera, now);
                        continue;
                    }
                    Create(replica, pair.Key);
                }
                bool wasVisible = replica.Root.activeSelf;
                if (!eligible)
                {
                    replica.Root.SetActive(false);
                    replica.Placed = false;
                    if (diagnostic && diagnosticRows++ < 32) ReportReplica(pair.Key, replica, eligible, camera, now);
                    continue;
                }
                EnsureAppearance(replica, pair.Key, now);
                var position = new Vector3(pose.X, pose.Y, pose.Z);
                bool snap = !replica.Placed || !wasVisible || replica.LastScene != pose.Scene || replica.LastProperty != pose.PropertyId || Vector3.Distance(replica.Root.transform.position, position) > 12f;
                replica.Root.transform.position = snap ? position : Vector3.Lerp(replica.Root.transform.position, position, blend);
                var rotation = Quaternion.Euler(0, pose.Yaw, 0);
                replica.Root.transform.rotation = snap ? rotation : Quaternion.Slerp(replica.Root.transform.rotation, rotation, blend);
                replica.LastScene = pose.Scene; replica.LastProperty = pose.PropertyId; replica.Placed = true;
                // Separate clients can spawn at the exact same saved position.
                // Keep tracking this pose, but hide this visual while its rendered
                // head overlaps the local first-person camera. Measuring feet would
                // miss the obstruction because the camera is about1.6m above them.
                if (camera != null)
                {
                    Vector3 head = replica.Root.transform.position + new Vector3(0, 1.68f, 0);
                    replica.CameraHeadDistance = Vector3.Distance(cameraPosition, head);
                    replica.OverlapHidden = replica.CameraHeadDistance < .7f;
                }
                replica.Root.SetActive(!replica.OverlapHidden);
                if (!replica.OverlapHidden && replica.Appearance != null && !replica.AnimationFailed)
                {
                    float delta = Mathf.Clamp(Time.unscaledDeltaTime, 0, .25f);
                    float speed = replica.Locomotion.Tick(now, delta);
                    try { replica.Appearance.Sample(delta, speed); }
                    catch (Exception ex)
                    {
                        replica.AnimationFailed = true;
                        log?.Invoke("Remote character animation stopped; model remains visible: " + ex.Message);
                    }
                }
                if (replica.Label != null)
                {
                    string name = DedicatedProtocol.Text(pose.Name, 48) ? pose.Name : "Player";
                    if (replica.Label.text != name) replica.Label.text = name;
                    replica.Label.gameObject.SetActive(!replica.OverlapHidden && camera != null && Vector3.Distance(cameraPosition, position) < 60f);
                    if (camera != null) replica.Label.transform.rotation = camera.transform.rotation;
                }
                if (diagnostic && diagnosticRows++ < 32) ReportReplica(pair.Key, replica, eligible, camera, now);
            }
        }

        private void ReportReplica(string id, Replica replica, bool eligible, Camera camera, float now)
        {
            var pose = replica.Pose;
            bool exists = replica.Root != null;
            string name = DedicatedProtocol.Text(pose.Name, 48) ? pose.Name : "Player";
            bool cameraReady = camera != null && camera.isActiveAndEnabled;
            bool cameraIncludesLayer = exists && cameraReady && (camera.cullingMask & (1 << replica.Root.layer)) != 0;
            string rootPosition = exists ? Position(replica.Root.transform.position) : "none";
            // These are rendering prerequisites, not a claim of visible pixels:
            // camera framing, occlusion, materials and display output are untested.
            log("REMOTE_PLAYER id=" + id + " name=" + JsonSerializer.Serialize(name) + " poseActive=" + pose.Active +
                " scene=" + JsonSerializer.Serialize(pose.Scene ?? "") + " ageSeconds=" + Math.Max(0, now - replica.UpdatedAt).ToString("F3", CultureInfo.InvariantCulture) +
                " eligible=" + eligible + " rootExists=" + exists + " rootActive=" + (exists && replica.Root.activeInHierarchy) +
                " overlapHidden=" + replica.OverlapHidden + " cameraHeadDistance=" + replica.CameraHeadDistance.ToString("F3", CultureInfo.InvariantCulture) +
                " appearance=" + JsonSerializer.Serialize(pose.AppearanceId ?? "") + " nativeModel=" + (replica.Appearance != null) +
                " placed=" + replica.Placed + " cameraReady=" + cameraReady + " cameraIncludesLayer=" + cameraIncludesLayer +
                " target=" + Position(new Vector3(pose.X, pose.Y, pose.Z)) + " yaw=" + pose.Yaw.ToString("F1", CultureInfo.InvariantCulture) + " root=" + rootPosition);
        }
        private static string Position(Vector3 value) => value.x.ToString("F3", CultureInfo.InvariantCulture) + "," +
            value.y.ToString("F3", CultureInfo.InvariantCulture) + "," + value.z.ToString("F3", CultureInfo.InvariantCulture);

        private void Create(Replica replica, string id)
        {
            replica.Root = new GameObject("NivalisTogether.RemotePlayer." + id);
            replica.Root.layer = 2;
            Object.DontDestroyOnLoad(replica.Root);
            try
            {
                var labelObject = new GameObject("Player name");
                labelObject.layer = 2;
                labelObject.transform.SetParent(replica.Root.transform, false);
                labelObject.transform.localPosition = new Vector3(0, 2.1f, 0);
                labelObject.transform.localScale = new Vector3(.12f, .12f, .12f);
                replica.Label = labelObject.AddComponent<TextMeshPro>();
                replica.Label.font = TMP_Settings.defaultFontAsset;
                replica.Label.fontSize = 3.5f;
                replica.Label.alignment = TextAlignmentOptions.Center;
                replica.Label.rectTransform.sizeDelta = new Vector2(34, 6);
                replica.Label.enableWordWrapping = false;
                replica.Label.overflowMode = TextOverflowModes.Ellipsis;
                replica.Label.color = new Color(.76f, 1f, 1f, 1);
                replica.Label.richText = false;
            }
            catch (Exception ex)
            {
                if (!reportedLabelFailure) { log?.Invoke("Remote player name label unavailable: " + ex.Message); reportedLabelFailure = true; }
            }
            log?.Invoke("Created remote player replica " + id);
        }

        private void EnsureAppearance(Replica replica, string id, float now)
        {
            string desired = replica.Pose.AppearanceId ?? "";
            if (replica.Appearance != null && string.Equals(replica.Appearance.AppearanceId, desired, StringComparison.Ordinal)) return;
            if (string.Equals(replica.AttemptedAppearance, desired, StringComparison.Ordinal) && now < replica.RetryAppearanceAfter) return;
            replica.AttemptedAppearance = desired; replica.RetryAppearanceAfter = now + 10;
            replica.Appearance?.Dispose(); replica.Appearance = null; replica.AnimationFailed = false;
            try
            {
                replica.Appearance = PlayerAppearance.CreateVisual(desired, replica.Root.transform);
                if (replica.Fallback != null) { replica.Fallback.SetActive(false); Object.Destroy(replica.Fallback); replica.Fallback = null; }
                log?.Invoke("REMOTE_APPEARANCE player=" + id + " appearance=" + JsonSerializer.Serialize(desired) +
                    " nativeRenderers=" + replica.Appearance.RendererCount + " " + replica.Appearance.AnimationDescription);
            }
            catch (Exception ex)
            {
                EnsureFallback(replica);
                log?.Invoke("Remote native appearance unavailable; temporary marker retained: " + ex.Message);
            }
        }

        private void EnsureFallback(Replica replica)
        {
            if (replica.Fallback != null) return;
            if (material == null)
            {
                var shader = Shader.Find("Standard") ?? Shader.Find("Unlit/Color");
                if (shader == null) throw new InvalidOperationException("No compatible remote-player shader is available.");
                material = new Material(shader); material.color = new Color(.10f, .80f, .95f, 1);
                if (material.HasProperty("_EmissionColor"))
                { material.EnableKeyword("_EMISSION"); material.SetColor("_EmissionColor", new Color(.04f, .5f, .65f)); }
            }
            replica.Fallback = new GameObject("Temporary player marker"); replica.Fallback.layer = 2;
            replica.Fallback.transform.SetParent(replica.Root.transform, false);
            Shape(replica.Fallback, PrimitiveType.Capsule, new Vector3(0, .88f, 0), new Vector3(.5f, .7f, .5f));
            Shape(replica.Fallback, PrimitiveType.Sphere, new Vector3(0, 1.68f, 0), new Vector3(.34f, .34f, .34f));
            Shape(replica.Fallback, PrimitiveType.Cube, new Vector3(0, 1.6f, .25f), new Vector3(.13f, .10f, .24f));
        }

        private void Shape(GameObject root, PrimitiveType kind, Vector3 position, Vector3 scale)
        {
            var part = GameObject.CreatePrimitive(kind);
            part.layer = 2;
            part.transform.SetParent(root.transform, false);
            part.transform.localPosition = position; part.transform.localScale = scale;
            var collider = part.GetComponent<Collider>();
            if (collider != null) { collider.enabled = false; Object.Destroy(collider); }
            part.GetComponent<Renderer>().sharedMaterial = material;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            foreach (var replica in replicas.Values)
            { replica.Appearance?.Dispose(); if (replica.Root != null) Object.Destroy(replica.Root); }
            replicas.Clear();
            if (material != null) Object.Destroy(material);
            material = null;
        }
    }

    // Estimate planar locomotion from received poses, not the renderer's catch-up
    // distance. A short measurement window absorbs duplicate/batched messages;
    // hysteresis keeps packet timing from alternating standing and walking.
    internal sealed class RemoteLocomotion
    {
        private bool initialized, walking;
        private float x, z, sampleStart, distance, lastMovement, targetSpeed, speed;
        internal void Observe(float nextX, float nextZ, float now, bool reset)
        {
            float dx = nextX - x, dz = nextZ - z;
            float step = (float)Math.Sqrt(dx * dx + dz * dz);
            if (!initialized || reset || step > 12 || now - sampleStart > 1 || now < sampleStart)
            {
                initialized = true; x = nextX; z = nextZ; sampleStart = now;
                lastMovement = now - 1; distance = targetSpeed = speed = 0; walking = false;
                return;
            }
            x = nextX; z = nextZ;
            if (step > .003f) { distance += step; lastMovement = now; }
            float elapsed = now - sampleStart;
            if (elapsed >= .12f)
            {
                targetSpeed = Math.Min(distance / elapsed, 8);
                distance = 0; sampleStart = now;
            }
        }
        internal float Tick(float now, float delta)
        {
            if (!initialized) return 0;
            if (now - lastMovement > .22f) targetSpeed = 0;
            float blend = 1 - (float)Math.Exp(-12 * Math.Max(0, Math.Min(delta, .25f)));
            speed += (targetSpeed - speed) * blend;
            if (walking ? speed < .08f : speed > .18f) walking = !walking;
            return walking ? Math.Max(speed, .09f) : 0;
        }
    }
}
