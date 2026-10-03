using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using Nivalis;
using Nivalis.Boat;
using Nivalis.GhostSystem;
using UnityEngine;
using Object = UnityEngine.Object;

namespace NivalisTogether.Dedicated
{
    // Store identities rather than native pooled wrappers. Lifecycle callbacks
    // record candidates; cleanup re-resolves each identity before touching it.
    internal sealed class BoatGhostCleanupIndex
    {
        private IntPtr manager, registry;
        private string scene;
        private bool initialized;
        private readonly Dictionary<string, IntPtr> pending = new Dictionary<string, IntPtr>(StringComparer.Ordinal);
        internal int Count => pending.Count;
        internal bool Begin(IntPtr managerIdentity, IntPtr registryIdentity, string sceneIdentity)
        {
            if (managerIdentity == IntPtr.Zero || registryIdentity == IntPtr.Zero) { Clear(); return false; }
            if (initialized && manager == managerIdentity && registry == registryIdentity && scene == sceneIdentity) return false;
            Clear(); manager = managerIdentity; registry = registryIdentity; scene = sceneIdentity; initialized = true; return true;
        }
        internal void Observe(IntPtr managerIdentity, string id, IntPtr ghost, bool isBoat)
        {
            if (initialized && managerIdentity == manager && isBoat && !string.IsNullOrEmpty(id) && ghost != IntPtr.Zero) pending[id] = ghost;
        }
        internal void Forget(IntPtr managerIdentity, string id, IntPtr ghost)
        {
            if (managerIdentity == manager && id != null && pending.TryGetValue(id, out var expected) && expected == ghost) pending.Remove(id);
        }
        internal void Invalidate(IntPtr managerIdentity)
        { if (managerIdentity == manager) Clear(); }
        internal void Cleanup(string ownId, Func<string, IntPtr> resolve, Action<string, IntPtr> remove)
        {
            if (pending.Count == 0) return;
            var candidates = new List<KeyValuePair<string, IntPtr>>(pending); pending.Clear();
            foreach (var candidate in candidates)
                if (candidate.Key != ownId && resolve(candidate.Key) == candidate.Value) remove(candidate.Key, candidate.Value);
        }
        internal void Clear()
        { pending.Clear(); manager = registry = IntPtr.Zero; scene = null; initialized = false; }
    }

    internal static class BoatHullColliderPolicy
    {
        internal static bool Eligible(bool enabled, bool activeUnderRoot, bool belongsToRootBody, bool isBox, bool orthogonal) =>
            enabled && activeUnderRoot && belongsToRootBody && isBox && orthogonal;
    }

    internal sealed class BoatFuelApplication
    {
        internal long Revision { get; private set; }
        internal void Apply(float fuel, long revision, Action<float> writeNativeFuel)
        {
            if (revision < Revision) return;
            writeNativeFuel(fuel);
            // A received snapshot is not an acknowledgement: only a completed
            // native fuel write can let movement consume a paid refill.
            Revision = Math.Max(Revision, revision);
        }
    }

    internal static class BoatBoxTransformPolicy
    {
        internal static bool Orthogonal(float xx, float xy, float xz, float yx, float yy, float yz, float zx, float zy, float zz)
        {
            double nx = (double)xx * xx + (double)xy * xy + (double)xz * xz;
            double ny = (double)yx * yx + (double)yy * yy + (double)yz * yz;
            double nz = (double)zx * zx + (double)zy * zy + (double)zz * zz;
            if (!double.IsFinite(nx) || !double.IsFinite(ny) || !double.IsFinite(nz) || nx <= 0 || ny <= 0 || nz <= 0) return false;
            return Math.Abs((double)xx * yx + (double)xy * yy + (double)xz * yz) < 0.0001 * Math.Sqrt(nx * ny) &&
                Math.Abs((double)xx * zx + (double)xy * zy + (double)xz * zz) < 0.0001 * Math.Sqrt(nx * nz) &&
                Math.Abs((double)yx * zx + (double)yy * zy + (double)yz * zz) < 0.0001 * Math.Sqrt(ny * nz);
        }
    }

    internal static class BoatDeparturePoseGate
    {
        internal static bool Accepted(bool connected, string accountId, string boatId, string scene,
            BoatPose expected, BoatPose observed) => connected && expected != null && observed != null &&
            !string.IsNullOrEmpty(expected.PlayerId) && expected.Sequence > 0 && expected.ContextGeneration > 0 &&
            expected.Boarded && expected.Driving && expected.AccountId == accountId && expected.BoatId == boatId &&
            expected.Scene == scene && observed.PlayerId == expected.PlayerId && observed.AccountId == expected.AccountId &&
            observed.BoatId == expected.BoatId && observed.Scene == expected.Scene &&
            observed.ContextGeneration == expected.ContextGeneration && observed.Sequence >= expected.Sequence &&
            observed.Boarded && observed.Driving;
    }

    internal static class BoatWorkerAttachmentPolicy
    {
        internal static bool Prevent(bool configured, bool worker, IntPtr trigger, IntPtr boatNavMesh) =>
            configured && worker && trigger != IntPtr.Zero && trigger == boatNavMesh;
    }

    internal static class BoatBootstrapAttachmentPolicy
    {
        internal static bool Prevent(bool configured, bool worker, bool clientRestoreComplete, IntPtr observed, IntPtr own) =>
            configured && !worker && !clientRestoreComplete && observed != IntPtr.Zero && observed == own;
    }

    internal static class BoatNavMeshAttachmentPolicy
    {
        internal static bool Prevent(bool configured, bool worker, bool clientRestoreComplete,
            IntPtr observed, IntPtr recognizedBoatNavMesh, bool ownBoat) =>
            configured && observed != IntPtr.Zero && observed == recognizedBoatNavMesh &&
            (worker || !ownBoat || !clientRestoreComplete);
    }

    internal static class BoatRestoredAttachmentPolicy
    {
        internal static bool Complete(bool driving, bool boarded, bool ownBoardedBoat, bool ownNavMesh,
            bool occupied, bool ownCockpitPlayer, bool driven, bool ownDrivenBoat) =>
            boarded && ownBoardedBoat && (driving
                ? occupied && ownCockpitPlayer && driven && ownDrivenBoat
                : ownNavMesh && !occupied && !driven);
    }

    internal readonly struct BoatEntryAttempt
    {
        internal readonly bool NormalEntry;
        private readonly long undockSerial;
        internal BoatEntryAttempt(bool configured, bool worker, int nativeDepth, bool own, bool occupied, long sentUndockSerial)
        {
            NormalEntry = configured && !worker && nativeDepth == 0 && own && !occupied;
            undockSerial = sentUndockSerial;
        }
        internal bool NeedsUndock(bool occupied, bool docked, bool ownedDock, long sentUndockSerial) =>
            NormalEntry && occupied && docked && ownedDock && sentUndockSerial == undockSerial;
    }

    internal sealed class BoatClientDelivery
    {
        internal bool ApplyRoster, ReplaceRoster;
        internal BoatWorldState Operation;
        internal PlayerBoatState OperationState;
    }
    /// <summary>Durably committed operations may arrive behind newer movement snapshots.</summary>
    internal sealed class BoatClientEventLedger
    {
        private long sequence = -1;
        private readonly HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        private readonly Queue<string> order = new Queue<string>();
        internal BoatClientDelivery Observe(BoatWorldState value, string account, string boatId)
        {
            BoatProtocol.Validate(value);
            var delivery = new BoatClientDelivery { ApplyRoster = value.Sequence >= sequence,
                ReplaceRoster = value.Sequence >= sequence && value.Operation == "snapshot" };
            if (delivery.ApplyRoster) sequence = value.Sequence;
            if (value.Operation == "snapshot" || value.RequestId.Length == 0 || value.BoatId != boatId) return delivery;
            string identity = value.TargetPlayerId + ":" + value.RequestId;
            if (seen.Contains(identity)) return delivery;
            PlayerBoatState operationState = null;
            foreach (var state in value.Boats) if (state.BoatId == boatId)
            {
                if (state.OwnerAccountId != account) throw new InvalidOperationException("An approved boat action changed ownership.");
                operationState = state;
            }
            if (value.Success && operationState == null) throw new InvalidOperationException("An approved boat action omitted its state.");
            seen.Add(identity); order.Enqueue(identity); while (order.Count > 1024) seen.Remove(order.Dequeue());
            delivery.Operation = BoatProtocol.Copy(value); delivery.OperationState = BoatProtocol.Copy(operationState);
            return delivery;
        }
        internal static void UpdateRuntimePoses(Dictionary<string, BoatPose> poses, BoatWorldState value, BoatClientDelivery delivery)
        {
            if (!delivery.ApplyRoster) return;
            // A full roster is also a full transient-pose roster. Recall/dock
            // deliberately remove a pose on the authority; retaining its old
            // same-scene position would hide the new stationary placement.
            if (delivery.ReplaceRoster) poses.Clear();
            else if (value.BoatId.Length != 0 && value.Success) poses.Remove(value.BoatId);
            foreach (var pose in value.RuntimePoses) poses[pose.BoatId] = BoatProtocol.Copy(pose);
        }
    }
    public sealed class BoatDockDescription
    {
        public string DockId = "", Scene = "";
        public float X, Y, Z, Qx, Qy, Qz, Qw = 1, InteractionX, InteractionY, InteractionZ;
        public float FuelCapacity, FuelPricePerLiter;
    }

    /// <summary>Own native boat controls; remote boats contain rendering components only.</summary>
    public static class BoatReplication
    {
        private static readonly Harmony Hooks = new Harmony("local.nivalis.together.personal-boats");
        private static readonly Dictionary<string, PlayerBoatState> states = new Dictionary<string, PlayerBoatState>(StringComparer.Ordinal);
        private static readonly Dictionary<string, RemoteVisual> visuals = new Dictionary<string, RemoteVisual>(StringComparer.Ordinal);
        private static readonly Dictionary<string, BoatPose> poses = new Dictionary<string, BoatPose>(StringComparer.Ordinal);
        private static BoatClientEventLedger deliveries = new BoatClientEventLedger();
        private static BoatFuelApplication appliedFuel = new BoatFuelApplication();
        private static readonly Queue<BoatClientDelivery> operations = new Queue<BoatClientDelivery>();
        private static readonly BoatGhostCleanupIndex legacyBoats = new BoatGhostCleanupIndex();
        private static bool configured, worker, patched, ownInitialized, attachmentRestored;
        private static string accountId = "", recallDock = "", currentScene = "";
        private static PlayerBoatState ownState;
        private static BoatGhost ownGhost;
        private static Action<BoatPose> sendPose;
        private static Action<InteractionRequest> sendIntent;
        private static Action<BoatController, PortalKey> travel;
        private static Action<string> notice;
        private static float nextPose, nextIntent, recallDeadline;
        private static long poseSequence, undockRequestSerial;
        private static int nativeDepth, travelDepth;
        private static IntPtr travelBoat, travelPortal;
        private static bool travelRequestPending, heldBodyWasKinematic;
        private static Rigidbody heldBody;
        public static string OwnBoatId => ownState?.BoatId ?? "";
        public static BoatGhost OwnBoatGhost => ownGhost;
        public static BoatController OwnBoatController => ownGhost?.LoadedView?.TryCast<BoatController>();
        public static bool IsRecalling => recallDock.Length != 0 || NativeRecallInProgress();

        private static bool NativeRecallInProgress()
        {
            if (!configured || worker) return false;
            var dock = ownGhost?.Dock?.Value?.LoadedView?.TryCast<BoatDock>();
            return dock != null && dock.IsRecalling;
        }

        private static void UpdateRecall()
        {
            // StartCoroutine may invoke a Dock callback synchronously before
            // native Recall assigns its recallingSequence handle.
            if (recallDock.Length == 0 || nativeDepth > 0) return;
            var dock = ResolveDock(recallDock);
            // Dock's postfix runs before RecallSequence clears its coroutine.
            // Initialization can also invoke Dock while recall is still moving.
            // Only the finished native sequence at our reserved berth releases
            // pose publication and permits the next interaction.
            if (dock != null && !dock.IsRecalling && dock.MyGhost?.DockedGhost?.ReferencedId == OwnBoatId &&
                ownGhost?.Dock?.ReferencedId == recallDock)
            { recallDock = ""; return; }
            if (Time.realtimeSinceStartup > recallDeadline)
            { recallDock = ""; notice?.Invoke("Your boat recall did not finish. Try recalling it again."); }
        }
        public static string OwnBoatLogicalScene => ownGhost == null ? "" : SceneName(ownGhost.SceneIndex);
        public static bool TravelRequestPending => travelRequestPending;
        public static void SetTravelRequestPending(bool pending)
        {
            if (pending == travelRequestPending) return;
            if (pending)
            {
                var boat = OwnBoatController ?? throw new InvalidOperationException("Your boat is not ready for travel.");
                heldBody = boat.body ?? throw new InvalidOperationException("Your boat's native body is unavailable.");
                heldBodyWasKinematic = heldBody.isKinematic;
                boat.input = Vector2.zero;
                if (!heldBody.isKinematic) { heldBody.velocity = Vector3.zero; heldBody.angularVelocity = Vector3.zero; }
                heldBody.isKinematic = true; travelRequestPending = true;
            }
            else
            {
                travelRequestPending = false;
                if (heldBody != null)
                {
                    heldBody.isKinematic = heldBodyWasKinematic;
                    if (!heldBodyWasKinematic) { heldBody.velocity = Vector3.zero; heldBody.angularVelocity = Vector3.zero; }
                }
                heldBody = null;
                if (OwnBoatController != null) OwnBoatController.input = Vector2.zero;
            }
        }

        public static void SetOwnState(PlayerBoatState state)
        {
            if (state == null) return;
            BoatProtocol.Validate(state);
            if (state.OwnerAccountId != accountId) throw new InvalidOperationException("A private boat belongs to another account.");
            if (ownState != null && (ownState.BoatId != state.BoatId || ownState.PrefabId != state.PrefabId))
                throw new InvalidOperationException("The private boat identity changed.");
            if (ownState != null && state.Revision < ownState.Revision) return;
            ownState = BoatProtocol.Copy(state); states[state.BoatId] = ownState;
        }
        public static bool IsNativeTravelComplete(string boatId, PortalKey key)
        {
            var boat = OwnBoatController;
            if (!configured || worker || ownState == null || ownState.OwnerAccountId != accountId ||
                boatId != OwnBoatId || key == null || !WorkerSceneResidency.ClientRestoreComplete ||
                BoatPortal.sequence?.IsInProgress == true || ownGhost == null || !ownGhost.IsValid ||
                DedicatedWorldRuntime.Scene != key.SceneName || ownGhost.SceneIndex != key.SceneIndex ||
                boat == null || boat.MyGhost?.Pointer != ownGhost.Pointer) return false;
            var player = PlayerManager.Instance.LocalPlayer?.Character;
            var controller = player?.Controller;
            if (controller == null || !boat.Occupied || boat.Cockpit?.Occupied != true ||
                boat.Cockpit.Player?.Pointer != player.Pointer || !ownGhost.Driven ||
                player.DrivenBoat?.Pointer != boat.Pointer) return false;
            if (ownGhost.Boarded && controller.boardedBoat?.Pointer == boat.Pointer) return true;

            var previous = controller.boardedBoat;
            if (previous != null && previous.MyGhost?.Id != boatId) return false;
            // Native portal spawning enters the destination cockpit on a new
            // view, but retains the ghost's Boarded flag. OnDeckEnter then exits
            // early without replacing the controller's old boardedBoat pointer.
            // Finish the native deck callback lifecycle on this exact owned view;
            // do not move the seated player, re-enter the cockpit, or replay travel.
            var previousPointer = previous?.Pointer ?? IntPtr.Zero;
            nativeDepth++;
            try
            {
                if (ownGhost.Boarded) boat.OnDeckExit(controller);
                boat.OnDeckEnter(controller);
                if (OwnBoatController?.Pointer != boat.Pointer ||
                    !BoatRestoredAttachmentPolicy.Complete(true, ownGhost.Boarded,
                        controller.boardedBoat?.Pointer == boat.Pointer, false,
                        boat.Occupied && boat.Cockpit?.Occupied == true, boat.Cockpit?.Player?.Pointer == player.Pointer,
                        ownGhost.Driven, player.DrivenBoat?.Pointer == boat.Pointer))
                    throw new InvalidOperationException("Your arriving boat's native player attachment did not finish restoring.");
                Debug.Log("BOAT_ARRIVAL_ATTACHMENT boat=" + boatId + " previousView=" + previousPointer +
                    " currentView=" + boat.Pointer + " boardedView=" + controller.boardedBoat.Pointer);
                return true;
            }
            finally { nativeDepth--; }
        }

        public static void Configure(bool isWorker, string localAccountId, Action<BoatPose> outboundPose,
            Action<InteractionRequest> outboundIntent, Action<BoatController, PortalKey> requestTravel, Action<string> showNotice)
        {
            Reset(); Install(); worker = isWorker; accountId = localAccountId ?? "";
            sendPose = outboundPose; sendIntent = outboundIntent; travel = requestTravel; notice = showNotice;
            configured = true;
        }

        public static PlayerBoatState CreateStartingState(string ownerAccountId)
        {
            if (!DedicatedWorldRuntime.IsDedicatedServer || !Guid.TryParseExact(ownerAccountId, "N", out _))
                throw new InvalidOperationException("A basic boat requires an authenticated account on the worker.");
            var manager = GhostManager.Instance;
            BoatGhost template = null;
            foreach (var ghost in manager.Ghosts)
            {
                var boat = ghost.TryCast<BoatGhost>();
                if (boat == null || string.IsNullOrEmpty(boat.PrefabId)) continue;
                if (template == null || StringComparer.Ordinal.Compare(boat.Id, template.Id) < 0) template = boat;
            }
            if (template == null) throw new InvalidOperationException("The native basic boat template is missing.");
            var prefab = ResolvePrefab(template.PrefabId);
            var controller = prefab.GetComponent<BoatController>();
            float capacity = controller?.FuelCapacity ?? 0;
            if (!float.IsFinite(capacity) || capacity <= 0 || capacity > 100000)
                throw new InvalidOperationException("The native basic boat fuel capacity is unavailable.");
            return new PlayerBoatState { BoatId = Guid.NewGuid().ToString("N"), OwnerAccountId = ownerAccountId,
                PrefabId = template.PrefabId, Scene = "", DockId = "", Qw = 1, Fuel = capacity, Unlocked = true, Revision = 1 };
        }

        public static BoatDock ResolveDock(string id)
        {
            if (string.IsNullOrEmpty(id) || !GhostManager.HasInstance) return null;
            return GhostManager.Instance.GetGhostById(id)?.TryCast<BoatDockGhost>()?.LoadedView?.TryCast<BoatDock>();
        }
        public static BoatDockDescription DescribeDock(string id)
        {
            var dock = ResolveDock(id);
            if (dock == null || !dock.isActiveAndEnabled || !dock.gameObject.scene.isLoaded)
                throw new InvalidOperationException("The native boat dock is not available in the loaded district.");
            var p = dock.transform.position; var q = dock.transform.rotation;
            var description = new BoatDockDescription { DockId = id, Scene = dock.gameObject.scene.name,
                X = p.x, Y = p.y, Z = p.z, Qx = q.x, Qy = q.y, Qz = q.z, Qw = q.w,
                InteractionX = p.x, InteractionY = p.y, InteractionZ = p.z,
                FuelCapacity = dock.boatPrefab?.FuelCapacity ?? 0 };
            foreach (var recall in Object.FindObjectsOfType<BoatRecallInteraction>())
                if (recall.dock?.Pointer == dock.Pointer)
                { var point = recall.transform.position; description.InteractionX = point.x; description.InteractionY = point.y; description.InteractionZ = point.z; break; }
            foreach (var fuel in Object.FindObjectsOfType<BoatFuelDistributor>())
                if (fuel.dock?.Pointer == dock.Pointer)
                { fuel.UpdatePrice(false); description.FuelPricePerLiter = fuel.fuelPrice; break; }
            return description;
        }

        public static BoatTriggerContact DescribePortalContact(PlayerBoatState boat, BoatPortal portal)
        {
            if (!DedicatedWorldRuntime.IsDedicatedServer || boat == null || portal == null)
                throw new InvalidOperationException("Boat passage geometry requires the authoritative worker.");
            BoatProtocol.Validate(boat);
            var portalColliders = portal.GetComponents<Collider>();
            var trigger = portal.GetComponent<BoxCollider>();
            if (portalColliders.Length != 1 || trigger == null || !trigger.enabled || !trigger.isTrigger ||
                !portal.isActiveAndEnabled || !portal.gameObject.activeInHierarchy || !OrthogonalBoxTransform(trigger.transform))
                throw new InvalidOperationException("This native passage has no supported box trigger.");
            var prefab = ResolvePrefab(boat.PrefabId);
            var controller = prefab.GetComponent<BoatController>();
            var body = controller.body;
            if (body == null || body.gameObject.Pointer != prefab.Pointer)
                throw new InvalidOperationException("The native boat hull has no valid root body.");
            var root = prefab.transform;
            var poseRotation = Rotation(boat).normalized;
            var inverseRootRotation = Quaternion.Inverse(root.rotation);
            var boxes = new List<BoatTriggerBox>();
            var colliders = prefab.GetComponentsInChildren<Collider>(true);
            if (colliders.Length > 512) throw new InvalidOperationException("The native boat has too many hull colliders.");
            foreach (var collider in colliders)
            {
                if (collider == null) continue;
                // Native GetMainGameObject resolves the attached rigidbody. Loose
                // objects with their own bodies cannot authorize the owner's hull.
                var box = collider.TryCast<BoxCollider>();
                // The worker disables its physics layer collisions. Its global
                // mask cannot describe the client's native trigger eligibility;
                // use only exact enabled geometry belonging to this boat body.
                if (!BoatHullColliderPolicy.Eligible(collider.enabled, ActiveUnder(collider.transform, root),
                    NearestBody(collider.transform)?.Pointer == body.Pointer, box != null,
                    box != null && OrthogonalBoxTransform(box.transform))) continue;
                // Unsupported or sheared shapes never add an approximate acceptance radius.
                var center = inverseRootRotation * (box.transform.TransformPoint(box.center) - root.position);
                var rotation = poseRotation * inverseRootRotation * box.transform.rotation;
                boxes.Add(TriggerBox(Position(boat) + poseRotation * center, rotation,
                    Vector3.Scale(box.size, Abs(box.transform.lossyScale)) * 0.5f));
            }
            if (boxes.Count == 0) throw new InvalidOperationException("The native boat has no supported hull box colliders.");
            return new BoatTriggerContact
            {
                PortalBox = TriggerBox(trigger.transform.TransformPoint(trigger.center), trigger.transform.rotation,
                    Vector3.Scale(trigger.size, Abs(trigger.transform.lossyScale)) * 0.5f),
                HullBoxes = boxes.ToArray()
            };
        }
        private static bool ActiveUnder(Transform child, Transform root)
        {
            for (int depth = 0; child != null && depth < 64; depth++, child = child.parent)
            {
                if (!child.gameObject.activeSelf) return false;
                if (child.Pointer == root.Pointer) return true;
            }
            return false;
        }
        private static Rigidbody NearestBody(Transform child)
        {
            // Prefab assets are not active scene objects. Walk the serialized
            // parents explicitly so an inactive hierarchy query cannot hide a
            // separate body or incorrectly omit the native root body.
            for (int depth = 0; child != null && depth < 64; depth++, child = child.parent)
            {
                var body = child.GetComponent<Rigidbody>();
                if (body != null) return body;
            }
            return null;
        }
        private static bool OrthogonalBoxTransform(Transform value)
        {
            var x = value.TransformVector(Vector3.right); var y = value.TransformVector(Vector3.up);
            var z = value.TransformVector(Vector3.forward);
            return BoatBoxTransformPolicy.Orthogonal(x.x, x.y, x.z, y.x, y.y, y.z, z.x, z.y, z.z);
        }
        private static Vector3 Abs(Vector3 value) => new Vector3(Mathf.Abs(value.x), Mathf.Abs(value.y), Mathf.Abs(value.z));
        private static BoatTriggerBox TriggerBox(Vector3 center, Quaternion rotation, Vector3 half)
        {
            rotation = rotation.normalized;
            return new BoatTriggerBox { X = center.x, Y = center.y, Z = center.z,
                Qx = rotation.x, Qy = rotation.y, Qz = rotation.z, Qw = rotation.w,
                HalfX = half.x, HalfY = half.y, HalfZ = half.z };
        }

        public static BoatPose PublishOwnPose()
        {
            var pose = CaptureOwnPose() ?? throw new InvalidOperationException("Your boat is not ready to synchronize its departure.");
            if (sendPose == null) throw new InvalidOperationException("The shared boat connection is unavailable.");
            sendPose(pose);
            // The session stamps the authenticated player and map generation only
            // when it can publish. A synchronization fence cannot arm this gate.
            BoatProtocol.Validate(pose);
            return BoatProtocol.Copy(pose);
        }
        public static bool IsOwnPoseAcknowledged(BoatPose expected)
        {
            poses.TryGetValue(OwnBoatId, out var observed);
            return BoatDeparturePoseGate.Accepted(configured && !worker, accountId, OwnBoatId,
                DedicatedWorldRuntime.Scene, expected, observed);
        }

        public static void Apply(BoatWorldState value)
        {
            if (!configured || worker || value == null) return;
            var delivery = deliveries.Observe(value, accountId, OwnBoatId);
            // A private reply can be delayed by its checkpoint while the shared
            // world publishes newer snapshots. Deliver its exact-once operation
            // even then; its old roster must not rewind current visual state.
            if (delivery.ApplyRoster)
            {
                var observed = new HashSet<string>(StringComparer.Ordinal);
                foreach (var state in value.Boats)
                {
                    observed.Add(state.BoatId);
                    if (!states.TryGetValue(state.BoatId, out var previous) || previous.Revision <= state.Revision)
                        states[state.BoatId] = BoatProtocol.Copy(state);
                    if (state.OwnerAccountId == accountId) SetOwnState(state);
                }
                if (delivery.ReplaceRoster)
                    foreach (var id in new List<string>(states.Keys)) if (!observed.Contains(id)) states.Remove(id);
                BoatClientEventLedger.UpdateRuntimePoses(poses, value, delivery);
            }
            if (delivery.Operation != null)
            {
                if (!delivery.Operation.Success) notice?.Invoke(delivery.Operation.Message);
                else operations.Enqueue(delivery);
            }
        }

        public static void Tick()
        {
            if (!configured || worker || !DedicatedWorldRuntime.IsWorldReady || !GhostManager.HasInstance || ownState == null) return;
            EnsureOwnGhost();
            while (operations.Count != 0) ExecuteOperation(operations.Dequeue());
            UpdateRecall();
            RemoveLegacyClientBoats();
            var controller = OwnBoatController;
            if (controller != null) BoatController.Instance = controller;
            UpdateRemoteVisuals();
            if (Time.realtimeSinceStartup < nextPose || IsRecalling) return;
            nextPose = Time.realtimeSinceStartup + 0.2f;
            var pose = CaptureOwnPose(); if (pose != null) sendPose?.Invoke(pose);
        }

        public static BoatPose CaptureOwnPose()
        {
            var boat = OwnBoatController;
            if (!configured || worker || boat == null || ownGhost == null || !ownGhost.IsValid ||
                ownState == null || IsRecalling || BoatPortal.sequence?.IsInProgress == true) return null;
            string scene = OwnBoatLogicalScene;
            if (scene.Length == 0 || DedicatedWorldRuntime.Scene != scene) return null;
            var p = boat.transform.position; var q = boat.transform.rotation;
            return new BoatPose { BoatId = ownState.BoatId, AccountId = accountId, Scene = scene,
                Sequence = ++poseSequence, X = p.x, Y = p.y, Z = p.z, Qx = q.x, Qy = q.y, Qz = q.z, Qw = q.w,
                Fuel = ownGhost.Fuel, AppliedFuelRevision = appliedFuel.Revision,
                Driving = boat.Occupied, Boarded = boat.Occupied || ownGhost.Boarded };
        }

        public static bool TryRestorePlayerAttachment()
        {
            if (!configured || worker || ownState == null || !WorkerSceneResidency.ClientRestoreComplete) return false;
            EnsureOwnGhost();
            if (attachmentRestored) return true;
            if (!ownState.PlayerAboard && !ownState.PlayerDriving) { attachmentRestored = true; return true; }
            if (DedicatedWorldRuntime.Scene != ownState.Scene) return false;
            var boat = OwnBoatController; var player = PlayerManager.Instance.LocalPlayer?.Character;
            if (boat == null || player?.Controller?.CharacterController == null || boat.PlayerSpawnPoint == null || boat.navMesh == null) return false;
            // The account's authoritative saved boat decides this attachment;
            // client coordinates never select another player's deck or cockpit.
            nativeDepth++;
            try
            {
                // Native cockpit entry sets Driven but assumes the player has
                // already boarded the deck. Bootstrap deliberately prevented that
                // automatic trigger, so restore the normal native deck path first.
                var controller = player.Controller;
                controller.TeleportPlayer(boat.PlayerSpawnPoint, false);
                boat.navMesh.OnTriggerEnter(controller.CharacterController);
                boat.OnDeckEnter(controller);
                if (ownState.PlayerDriving) boat.Cockpit.Enter(player);
                if (!BoatRestoredAttachmentPolicy.Complete(ownState.PlayerDriving, ownGhost.Boarded,
                    controller.boardedBoat?.Pointer == boat.Pointer, controller.NavMesh?.Pointer == boat.navMesh.Pointer,
                    boat.Cockpit?.Occupied == true, boat.Cockpit?.Player?.Pointer == player.Pointer,
                    ownGhost.Driven, player.DrivenBoat?.Pointer == boat.Pointer))
                    throw new InvalidOperationException("Your saved boat's native player attachment did not finish restoring.");
                attachmentRestored = true; return true;
            }
            finally { nativeDepth--; }
        }

        private static void EnsureOwnGhost()
        {
            if (ownState == null || !GhostManager.HasInstance) return;
            if (BoatPortal.sequence?.IsInProgress == true) return;
            var manager = GhostManager.Instance;
            if (ownGhost == null || !ownGhost.IsValid)
            {
                ownGhost = manager.GetGhostById(ownState.BoatId)?.TryCast<BoatGhost>();
                if (ownGhost == null)
                {
                    ResolvePrefab(ownState.PrefabId);
                    ownGhost = new BoatGhost(ownState.BoatId, ownState.PrefabId);
                    ownGhost._sceneIndex = SceneIndex(ownState.Scene);
                    ownGhost._IsActive_k__BackingField = true; ownGhost._isViewLess = false;
                    ownGhost._IsBakedInstance_k__BackingField = false; ownGhost._viewId = ownState.BoatId;
                    ownGhost._transform = new PositionRotation(Position(ownState), Rotation(ownState));
                    ownGhost.unlocked = ownState.Unlocked;
                    appliedFuel.Apply(ownState.Fuel, ownState.Revision, fuel => ownGhost.fuel = fuel);
                    ownGhost.boarded = ownGhost.driven = false;
                    ownGhost.dock = new GhostReference<BoatDockGhost>(ownState.DockId);
                    manager.RegisterGhostImmediate(ownGhost);
                }
                else appliedFuel.Apply(ownState.Fuel, ownState.Revision, fuel => ownGhost.Fuel = fuel);
                ownInitialized = false;
            }
            if (ownGhost.SceneIndex >= 0) manager.InstantiateGhostViewIfNeeded(ownGhost);
            var controller = OwnBoatController;
            if (controller != null && !ownInitialized)
            {
                BoatController.Instance = controller;
                if (ownState.DockId.Length != 0)
                {
                    var dock = ResolveDock(ownState.DockId);
                    if (dock == null) return;
                    nativeDepth++; try { dock.Dock(controller); } finally { nativeDepth--; }
                }
                ownInitialized = true;
            }
        }

        private static void ExecuteOperation(BoatClientDelivery delivery)
        {
            if (ownState == null || ownGhost == null) throw new InvalidOperationException("Your personal boat is not ready.");
            var operation = delivery.Operation;
            var approved = delivery.OperationState;
            nativeDepth++;
            try
            {
                if (operation.Operation == "recall")
                {
                    var dock = ResolveDock(approved.DockId) ?? throw new InvalidOperationException("Your assigned dock is not loaded.");
                    recallDock = approved.DockId; recallDeadline = Time.realtimeSinceStartup + 45;
                    ownGhost._IsActive_k__BackingField = true;
                    dock.Recall(ownGhost);
                }
                else if (operation.Operation == "dock")
                {
                    var dock = ResolveDock(approved.DockId) ?? throw new InvalidOperationException("Your assigned dock is not loaded.");
                    var boat = OwnBoatController ?? throw new InvalidOperationException("Your boat is not loaded.");
                    dock.Dock(boat);
                }
                else if (operation.Operation == "undock")
                {
                    var dock = ownGhost.Dock?.Value?.LoadedView?.TryCast<BoatDock>();
                    if (dock != null) dock.Undock();
                    else ownGhost.Dock = new GhostReference<BoatDockGhost>("");
                }
                else if (operation.Operation == "refuel")
                    appliedFuel.Apply(approved.Fuel, approved.Revision, fuel => ownGhost.Fuel = fuel);
                if (!string.IsNullOrEmpty(operation.Message)) notice?.Invoke(operation.Message);
            }
            finally { nativeDepth--; }
        }

        public static bool? TryClientInteraction(IInteractable target)
        {
            if (!configured || worker || target == null) return null;
            var cockpit = target.TryCast<BoatCockpit>();
            if (cockpit != null) return IsOwn(cockpit.boat);
            var recall = target.TryCast<BoatRecallInteraction>();
            if (recall != null) { Request("boat-recall", recall.dock); return false; }
            var fuel = target.TryCast<BoatFuelDistributor>();
            if (fuel != null) { Request("boat-refuel", fuel.dock); return false; }
            return null;
        }
        private static void Request(string kind, BoatDock dock, bool entryFallback = false)
        {
            if (!configured || worker || ownState == null || sendIntent == null || dock?.MyGhost == null) return;
            if (!entryFallback && Time.realtimeSinceStartup < nextIntent) return;
            nextIntent = Time.realtimeSinceStartup + 0.75f;
            sendIntent(new InteractionRequest { Kind = kind, RequestId = Guid.NewGuid().ToString("N"), TargetId = dock.MyGhost.Id,
                Data = JsonSerializer.SerializeToElement(new BoatInteractionIntent { BoatId = ownState.BoatId, DockId = dock.MyGhost.Id }, DedicatedProtocol.Json) });
            if (kind == "boat-undock") undockRequestSerial++;
        }

        public static void ExecuteAuthorizedTravel(BoatController boat, PortalKey key)
        {
            if (!configured || worker || !IsOwn(boat) || key == null || travelDepth != 0)
                throw new InvalidOperationException("The native boat travel authorization is invalid.");
            travelBoat = boat.Pointer; travelPortal = key.Pointer; travelDepth++;
            try
            {
                if (BoatPortal.sequence == null) BoatPortal.sequence = new BoatTeleportSequence();
                BoatPortal.sequence.Start(boat, key);
            }
            finally { travelDepth--; travelBoat = travelPortal = IntPtr.Zero; }
        }
        public static bool IsOwn(BoatController boat) => ownState != null && boat?.MyGhost?.Id == ownState.BoatId;
        private static bool BoatTravelPrefix(BoatController __0, PortalKey __1)
        {
            if (!configured || worker) return true;
            if (travelDepth > 0 && __0?.Pointer == travelBoat && __1?.Pointer == travelPortal) return true;
            if (!travelRequestPending && IsOwn(__0) && __1 != null) travel?.Invoke(__0, __1);
            return false;
        }
        private static bool RecallPrefix(BoatDock __instance, BoatGhost __0) =>
            !configured || worker || (nativeDepth > 0 && __0?.Id == OwnBoatId && __instance?.MyGhost?.Id == recallDock);
        private static bool DockPrefix(BoatDock __instance, BoatController __0)
        {
            if (!configured || worker) return true;
            if (!IsOwn(__0)) return false;
            if (nativeDepth > 0 || (recallDock.Length != 0 && Time.realtimeSinceStartup <= recallDeadline && __instance?.MyGhost?.Id == recallDock)) return true;
            Request("boat-dock", __instance); return false;
        }
        private static void DockPostfix(BoatDock __instance, BoatController __0)
        {
            if (!configured || worker || !IsOwn(__0)) return;
            UpdateRecall();
        }
        private static bool UndockPrefix(BoatDock __instance)
        {
            if (!configured || worker) return true;
            if (__instance?.MyGhost?.DockedGhost?.ReferencedId != OwnBoatId) return false;
            if (nativeDepth > 0 || recallDock.Length != 0) return true;
            Request("boat-undock", __instance); return false;
        }
        private static bool EnterPrefix(BoatCockpit __instance, out BoatEntryAttempt __state)
        {
            bool own = configured && !worker && IsOwn(__instance?.boat);
            __state = new BoatEntryAttempt(configured, worker, nativeDepth, own, own && __instance.Occupied, undockRequestSerial);
            return !configured || own;
        }
        private static void EnterPostfix(BoatCockpit __instance, BoatEntryAttempt __state)
        {
            if (!__state.NormalEntry || !configured || worker || nativeDepth != 0 || !IsOwn(__instance?.boat)) return;
            var boat = __instance.boat;
            var dock = boat.MyGhost?.Dock?.Value?.LoadedView?.TryCast<BoatDock>();
            if (!__state.NeedsUndock(__instance.Occupied, boat.Docked,
                dock?.MyGhost?.DockedGhost?.ReferencedId == OwnBoatId, undockRequestSerial)) return;
            // Native Enter normally raises boatEnterEvent -> dock.OnBoatEnter.
            // A missed event must not strand an occupied boat at its berth.
            // Deduplicate by a successfully sent request during this exact
            // entry, including when the native event already requested it.
            Request("boat-undock", dock, entryFallback: true);
        }
        private static bool DeckEnterPrefix(BoatController __instance) => !configured || (!worker && IsOwn(__instance) &&
            !BoatBootstrapAttachmentPolicy.Prevent(configured, worker, WorkerSceneResidency.ClientRestoreComplete,
                __instance?.Pointer ?? IntPtr.Zero, OwnBoatController?.Pointer ?? IntPtr.Zero));
        private static BoatController FindNavMeshBoat(PlayerNavMesh navMesh)
        {
            if (navMesh == null) return null;
            var parent = navMesh.GetComponentInParent<BoatController>();
            if (parent?.navMesh?.Pointer == navMesh.Pointer) return parent;
            // The serialized nav mesh may be outside the boat's transform tree.
            // Match that exact reference; ordinary terrain nav meshes stay native.
            foreach (var boat in Object.FindObjectsOfType<BoatController>())
                if (boat?.navMesh?.Pointer == navMesh.Pointer) return boat;
            return null;
        }
        private static bool BoatNavMeshEnterPrefix(PlayerNavMesh __instance)
        {
            if (!configured) return true;
            var boat = FindNavMeshBoat(__instance);
            // Match the boat's serialized navmesh exactly, leaving terrain native.
            // Reject foreign/legacy decks before the native NavMesh setter, not
            // only at OnDeckEnter. Own saved decks wait until bootstrap placement
            // settles, then the authenticated cockpit/deck restoration can run.
            return !BoatNavMeshAttachmentPolicy.Prevent(configured, worker, WorkerSceneResidency.ClientRestoreComplete,
                __instance?.Pointer ?? IntPtr.Zero, boat?.navMesh?.Pointer ?? IntPtr.Zero, IsOwn(boat));
        }
        public static bool DetachWorkerBoatAttachment()
        {
            if (!configured || !worker || !PlayerManager.InstanceNotNull()) return false;
            var controller = PlayerManager.Instance.LocalPlayer?.Character?.Controller;
            var navMesh = controller?.NavMesh;
            var boat = FindNavMeshBoat(navMesh);
            if (!BoatWorkerAttachmentPolicy.Prevent(configured, worker,
                navMesh?.Pointer ?? IntPtr.Zero, boat?.navMesh?.Pointer ?? IntPtr.Zero)) return false;
            if (controller.CharacterController == null ||
                (controller.boardedBoat != null && controller.boardedBoat.Pointer != boat.Pointer))
                throw new InvalidOperationException("The worker's native boat attachment is inconsistent.");
            // OnTriggerExit performs the public NavMesh setter before onExit.
            // OnDeckExit alone only clears boarding and leaves position constrained
            // to the old deck, even when boardedBoat was never assigned.
            navMesh.OnTriggerExit(controller.CharacterController);
            if (controller.NavMesh?.Pointer == navMesh.Pointer) controller.NavMesh = null;
            if (controller.boardedBoat?.Pointer == boat.Pointer) boat.OnDeckExit(controller);
            if (controller.NavMesh?.Pointer == navMesh.Pointer || controller.boardedBoat?.Pointer == boat.Pointer)
                throw new InvalidOperationException("The worker could not leave its native boat attachment.");
            return true;
        }
        private static bool FindBoatPrefix(ref BoatGhost __result)
        { if (!configured || worker) return true; __result = ownGhost; return false; }
        private static bool UnlockPrefix(BoatController __instance) => !configured || worker;
        private static bool FuelPrefix() => !configured || worker;
        private static bool PhysicsPrefix(BoatController __instance) => !configured || worker || !travelRequestPending || !IsOwn(__instance);
        private static void InputPostfix(BoatController __instance)
        { if (configured && !worker && travelRequestPending && IsOwn(__instance)) __instance.input = Vector2.zero; }
        private static void FixSingleton()
        { if (configured && !worker && OwnBoatController != null) BoatController.Instance = OwnBoatController; }
        private static void Install()
        {
            if (patched) return;
            Patch(typeof(BoatTeleportSequence), "Start", nameof(BoatTravelPrefix));
            Patch(typeof(BoatDock), "Recall", nameof(RecallPrefix));
            Patch(typeof(BoatDock), "Dock", nameof(DockPrefix), nameof(DockPostfix));
            Patch(typeof(BoatDock), "Undock", nameof(UndockPrefix));
            Patch(typeof(BoatCockpit), "Enter", nameof(EnterPrefix), nameof(EnterPostfix));
            // Worker scene focus can place its helper near the legacy boat.
            // PlayerNavMesh assigns NavMesh before raising OnDeckEnter; blocking
            // only the latter leaves the worker silently constrained to the deck.
            Patch(typeof(PlayerNavMesh), "OnTriggerEnter", nameof(BoatNavMeshEnterPrefix));
            // It must never acquire a native boarding context from a trigger;
            // personal boarding is represented by the authenticated boat pose.
            Patch(typeof(BoatController), "OnDeckEnter", nameof(DeckEnterPrefix));
            Patch(typeof(BoatGhost), "FindBoat", nameof(FindBoatPrefix));
            Patch(typeof(BoatController), "UpdateUnlocking", nameof(UnlockPrefix));
            Patch(typeof(BoatController), "FixedUpdate", nameof(PhysicsPrefix));
            Patch(typeof(BoatController), "Update", null, nameof(InputPostfix));
            Patch(typeof(BoatController), "Awake", null, nameof(FixSingleton));
            Patch(typeof(BoatController), "OnDestroy", null, nameof(FixSingleton));
            Patch(typeof(BoatFuelDistributor), "TryStartFueling", nameof(FuelPrefix));
            Patch(typeof(BoatFuelDistributor), "UpdateFueling", nameof(FuelPrefix));
            Patch(typeof(GhostManager), "RegisterGhostImmediate", null, nameof(GhostRegisteredPostfix));
            Patch(typeof(GhostManager), "DeregisterGhostImmediate", nameof(GhostDeregisteringPrefix));
            Patch(typeof(GhostManager), "InitializeInternal", nameof(GhostManagerInvalidatingPrefix));
            Patch(typeof(GhostManager), "OnDestroy", nameof(GhostManagerInvalidatingPrefix));
            patched = true;
        }
        private static void Patch(Type type, string name, string prefix, string postfix = null)
        {
            var method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null) throw new MissingMethodException(type.FullName, name);
            HarmonyMethod Hook(string hook) => hook == null ? null : new HarmonyMethod(typeof(BoatReplication).GetMethod(hook, BindingFlags.Static | BindingFlags.NonPublic));
            Hooks.Patch(method, prefix: Hook(prefix), postfix: Hook(postfix));
        }

        private static void RemoveLegacyClientBoats()
        {
            if (!configured || worker || !GhostManager.HasInstance) return;
            var manager = GhostManager.Instance;
            if (!manager.IsSystemInitialized) { legacyBoats.Clear(); return; }
            if (legacyBoats.Begin(manager.Pointer, manager._ghostsDictionary.Pointer, DedicatedWorldRuntime.Scene))
                foreach (var ghost in manager.Ghosts)
                    legacyBoats.Observe(manager.Pointer, ghost.Id, ghost.Pointer, ghost.TryCast<BoatGhost>() != null);
            legacyBoats.Cleanup(OwnBoatId, id => manager.GetGhostById(id)?.Pointer ?? IntPtr.Zero, (id, pointer) =>
            {
                var boat = manager.GetGhostById(id)?.TryCast<BoatGhost>();
                if (boat == null || boat.Pointer != pointer || boat.Id == OwnBoatId) return;
                var dock = boat.Dock?.Value;
                if (dock?.DockedGhost?.ReferencedId == boat.Id) dock.DockedGhost = new GhostReference<BoatGhost>("");
                manager.DeregisterGhostImmediate(boat);
            });
        }
        private static void GhostRegisteredPostfix(GhostManager __instance, Ghost __0)
        {
            if (!configured || worker || __instance == null || __0 == null) return;
            var boat = __0.TryCast<BoatGhost>();
            if (boat != null) legacyBoats.Observe(__instance.Pointer, boat.Id, boat.Pointer, true);
        }
        private static void GhostDeregisteringPrefix(GhostManager __instance, Ghost __0)
        {
            if (configured && !worker && __instance != null && __0 != null)
                legacyBoats.Forget(__instance.Pointer, __0.Id, __0.Pointer);
        }
        private static void GhostManagerInvalidatingPrefix(GhostManager __instance)
        { if (configured && !worker && __instance != null) legacyBoats.Invalidate(__instance.Pointer); }
        private static GameObject ResolvePrefab(string id)
        {
            var dictionary = SerializationManager.Instance?.Prefabs?.prefabDictionary;
            if (dictionary == null || string.IsNullOrEmpty(id) || !dictionary.TryGetValue(id, out var prefab) || prefab == null || prefab.GetComponent<BoatController>() == null)
                throw new InvalidOperationException("The native basic boat prefab is unavailable.");
            return prefab;
        }
        private static int SceneIndex(string scene)
        {
            if (string.IsNullOrEmpty(scene)) return -1;
            var keys = TravelUnlockManager.Instance?._allPortalKeys;
            if (keys != null) foreach (var key in keys) if (key != null && key.SceneName == scene) return key.SceneIndex;
            throw new InvalidOperationException("The boat's saved district is unknown.");
        }
        private static string SceneName(int scene)
        {
            if (scene < 0 || !TravelUnlockManager.InstanceNotNull()) return "";
            var keys = TravelUnlockManager.Instance._allPortalKeys;
            if (keys != null) foreach (var key in keys) if (key != null && key.SceneIndex == scene && !string.IsNullOrEmpty(key.SceneName)) return key.SceneName;
            return "";
        }
        private static Vector3 Position(PlayerBoatState state) => new Vector3(state.X, state.Y, state.Z);
        private static Quaternion Rotation(PlayerBoatState state) => Quaternion.Normalize(new Quaternion(state.Qx, state.Qy, state.Qz, state.Qw));

        private sealed class RemoteVisual
        { internal GameObject Root; internal Vector3 Position; internal Quaternion Rotation; }
        private static void UpdateRemoteVisuals()
        {
            string scene = DedicatedWorldRuntime.Scene;
            if (scene != currentScene) { ClearVisuals(); currentScene = scene; }
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (var state in states.Values)
            {
                if (state.OwnerAccountId == accountId || state.Scene != scene) continue;
                present.Add(state.BoatId);
                if (!visuals.TryGetValue(state.BoatId, out var visual))
                {
                    visual = BuildVisual(ResolvePrefab(state.PrefabId), state.BoatId);
                    visual.Position = Position(state); visual.Rotation = Rotation(state);
                    visual.Root.transform.SetPositionAndRotation(visual.Position, visual.Rotation); visuals.Add(state.BoatId, visual);
                }
                var position = Position(state); var rotation = Rotation(state);
                if (poses.TryGetValue(state.BoatId, out var pose) && pose.Scene == scene)
                { position = new Vector3(pose.X, pose.Y, pose.Z); rotation = Quaternion.Normalize(new Quaternion(pose.Qx, pose.Qy, pose.Qz, pose.Qw)); }
                float blend = Mathf.Clamp01(Time.unscaledDeltaTime * 10);
                visual.Position = (visual.Position - position).sqrMagnitude > 2500 ? position : Vector3.Lerp(visual.Position, position, blend);
                visual.Rotation = Quaternion.Slerp(visual.Rotation, rotation, blend);
                visual.Root.transform.SetPositionAndRotation(visual.Position, visual.Rotation);
            }
            foreach (var id in new List<string>(visuals.Keys)) if (!present.Contains(id))
            { Object.Destroy(visuals[id].Root); visuals.Remove(id); poses.Remove(id); }
        }
        private static RemoteVisual BuildVisual(GameObject prefab, string id)
        {
            var root = new GameObject("Other player's boat " + id); root.layer = 2; root.SetActive(false);
            try
            {
                var map = new Dictionary<int, Transform> { [prefab.transform.GetInstanceID()] = root.transform };
                CopyHierarchy(prefab.transform, root.transform, map, 0);
                var included = new HashSet<int>(); var all = new HashSet<int>();
                foreach (var group in prefab.GetComponentsInChildren<LODGroup>(true))
                {
                    var lods = group.GetLODs();
                    for (int i = 0; i < lods.Length; i++) foreach (var renderer in lods[i].renderers)
                        if (renderer != null) { all.Add(renderer.GetInstanceID()); if (i == 0) included.Add(renderer.GetInstanceID()); }
                }
                int count = 0;
                foreach (var renderer in prefab.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (all.Contains(renderer.GetInstanceID()) && !included.Contains(renderer.GetInstanceID())) continue;
                    var source = renderer.GetComponent<MeshFilter>(); if (source?.sharedMesh == null) continue;
                    var node = map[renderer.transform.GetInstanceID()].gameObject;
                    node.AddComponent<MeshFilter>().sharedMesh = source.sharedMesh;
                    var target = node.AddComponent<MeshRenderer>(); target.sharedMaterials = renderer.sharedMaterials;
                    target.enabled = renderer.enabled; target.shadowCastingMode = renderer.shadowCastingMode; target.receiveShadows = renderer.receiveShadows;
                    if (++count > 2048) throw new InvalidOperationException("The boat visual has too many meshes.");
                }
                if (count == 0) throw new InvalidOperationException("The native boat has no supported render geometry.");
                root.SetActive(true); return new RemoteVisual { Root = root };
            }
            catch { Object.Destroy(root); throw; }
        }
        private static void CopyHierarchy(Transform source, Transform target, Dictionary<int, Transform> map, int depth)
        {
            if (depth > 48 || map.Count > 4096) throw new InvalidOperationException("The boat visual hierarchy exceeds its limit.");
            for (int i = 0; i < source.childCount; i++)
            {
                var child = source.GetChild(i); var node = new GameObject(child.name); node.layer = 2;
                node.transform.SetParent(target, false); node.transform.localPosition = child.localPosition;
                node.transform.localRotation = child.localRotation; node.transform.localScale = child.localScale;
                node.SetActive(child.gameObject.activeSelf); map.Add(child.GetInstanceID(), node.transform);
                CopyHierarchy(child, node.transform, map, depth + 1);
            }
        }
        private static void ClearVisuals()
        { foreach (var visual in visuals.Values) if (visual.Root != null) Object.Destroy(visual.Root); visuals.Clear(); }
        public static void Reset()
        {
            bool wasClient = configured && !worker;
            if (travelRequestPending) SetTravelRequestPending(false);
            configured = false;
            if (wasClient && ownGhost != null && GhostManager.HasInstance && ownGhost.IsValid)
            {
                try { OwnBoatController?.Cockpit?.Exit(); GhostManager.Instance.DeregisterGhostImmediate(ownGhost); }
                catch { /* The native world may already have unloaded. */ }
            }
            ClearVisuals(); states.Clear(); poses.Clear(); deliveries = new BoatClientEventLedger(); operations.Clear();
            legacyBoats.Clear();
            appliedFuel = new BoatFuelApplication();
            ownState = null; ownGhost = null; ownInitialized = attachmentRestored = false;
            accountId = recallDock = currentScene = ""; nextPose = nextIntent = recallDeadline = 0;
            poseSequence = undockRequestSerial = 0; nativeDepth = travelDepth = 0; travelBoat = travelPortal = IntPtr.Zero;
            sendPose = null; sendIntent = null; travel = null; notice = null;
        }
    }
}
