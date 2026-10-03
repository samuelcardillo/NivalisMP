using System;
using System.IO;
using Nivalis;
using Nivalis.Apartment;
using UnityEngine;
using Object = UnityEngine.Object;

namespace NivalisTogether.Dedicated
{
    /// <summary>
    /// Serializes the native singleton's map/property context. The worker's focus
    /// does not grant a player map membership and never sends a client travel event.
    /// The client entry point is only for restoring an authenticated account after
    /// importing the shared checkpoint. All calls run on Unity's main thread.
    /// </summary>
    public static class WorkerSceneResidency
    {
        private enum Phase { Prepare, ExitApartment, Travel, EnterApartment, Settle }
        private sealed class Operation
        {
            internal PlayerMapLocation Target;
            internal string Epoch, FromScene, NativeError = "";
            internal bool Client, ArrivalObserved, ApartmentObserved, ApartmentEnter, MutationStarted;
            internal double Deadline;
            internal int PlacedFrame;
            internal Vector3 PlacedPosition;
            internal Phase Phase;
            internal PortalKey Portal;
            internal ApartmentController Apartment;
            internal ApartmentDoor Door;
            internal readonly System.Collections.Generic.HashSet<IntPtr> StartupExits = new System.Collections.Generic.HashSet<IntPtr>();
        }
        private static Operation operation;
        private static PlayerMapLocation completedWorkerContext;
        private static Action<string> failure;
        private static bool configured, terminal;
        private static SafeEvent<WorldLocation> arrivalEvent;
        private static Il2CppSystem.Action<WorldLocation> arrivalListener;
        private static ActionNonAlloc<ApartmentController, bool> apartmentEvent;
        private static Il2CppSystem.Action<ApartmentController, bool> apartmentListener;
        public static Action<string> Diagnostic { get; set; }
        public static bool IsBusy => operation != null;
        public static bool IsClientRestoreBusy => operation?.Client ?? false;
        public static bool IsRestoringClient => IsClientRestoreBusy;
        public static bool ClientRestoreComplete { get; private set; }
        public static string FailureReason { get; private set; } = "";
        // Venues and greenhouses use native position triggers, not apartment doors.
        public static string CurrentProperty => PlayerManager.InstanceNotNull()
            ? PlayerManager.Instance.LocalPlayer?.CurrentProperty?.TryCast<Apartment>()?.Guid ?? "" : "";

        public static void Configure(Action<string> failSession)
        {
            Reset(); failure = failSession ?? throw new ArgumentNullException(nameof(failSession)); configured = true;
        }
        public static void Reset()
        {
            RemoveObservers(); operation = null; completedWorkerContext = null;
            failure = null; configured = terminal = false; ClientRestoreComplete = false; FailureReason = "";
        }
        public static void Stop()
        {
            RemoveObservers(); operation = null; completedWorkerContext = null;
            ClientRestoreComplete = false; terminal = true;
        }
        private static bool Authority => configured && !terminal && DedicatedWorldRuntime.IsIsolated;

        /// <summary>
        /// Call only with the worker's authoritative player context. Keep the request
        /// queued until true; account scopes must not span this asynchronous operation.
        /// </summary>
        public static bool Ensure(PlayerMapLocation context, double timestamp)
        {
            RequireAuthority(true); Validate(context, timestamp);
            if (operation != null) return false;
            DetachWorkerBoat();
            if (completedWorkerContext != null)
            {
                bool matches = Same(completedWorkerContext, context);
                completedWorkerContext = null;
                if (matches && ContextMatches(context) && PositionMatches(context)) return true;
            }
            if (context.HasPosition && ContextMatches(context) && PositionMatches(context)) return true;
            RequireIdleNative();
            Begin(context, Guid.NewGuid().ToString("N"), timestamp, false);
            return false;
        }
        public static void BeginClientRestore(PlayerMapLocation context, string epoch, double timestamp)
        {
            RequireAuthority(false); Validate(context, timestamp);
            if (!Guid.TryParseExact(epoch, "N", out _) || operation != null)
                throw new InvalidOperationException("The authenticated map restoration is already active or has no valid epoch.");
            RequireIdleNative(); ClientRestoreComplete = false;
            Begin(context, epoch, timestamp, true);
        }
        private static void Begin(PlayerMapLocation context, string epoch, double timestamp, bool client)
        {
            // Resolve all cross-map references before any native mutation. A portal
            // from another district is never accepted merely because its ID exists.
            var portal = DedicatedWorldRuntime.Scene != context.Scene || (!context.HasPosition && context.PropertyId.Length == 0)
                ? ResolvePortal(context) : null;
            operation = new Operation { Target = PlayerMapLocation.Copy(context), Epoch = epoch,
                Client = client, Deadline = timestamp + 90, Phase = Phase.Prepare,
                Portal = portal, FromScene = DedicatedWorldRuntime.Scene };
            Diagnostic?.Invoke("MAP_CONTEXT begin epoch=" + epoch + " client=" + client +
                " from=" + operation.FromScene + " to=" + context.Scene + " property=" + context.PropertyId);
            Tick(timestamp);
        }
        public static void Tick(double timestamp)
        {
            var value = operation;
            if (value == null || !Authority) return;
            try
            {
                if (!double.IsFinite(timestamp) || timestamp > value.Deadline)
                    throw new InvalidOperationException("Native map context loading exceeded its deadline.");
                if (value.NativeError.Length > 0) throw new InvalidOperationException(value.NativeError);
                if (value.Phase == Phase.Travel)
                {
                    if (!value.ArrivalObserved || !DedicatedWorldRuntime.IsWorldReady ||
                        DedicatedWorldRuntime.Scene != value.Target.Scene || !TravelLocksReleased()) return;
                    RemoveArrivalObserver(); value.Phase = Phase.Prepare;
                }
                if (value.Phase == Phase.ExitApartment || value.Phase == Phase.EnterApartment)
                {
                    if (!value.ApartmentObserved || !DedicatedWorldRuntime.IsWorldReady) return;
                    ValidateApartment(value.Apartment, value.Door, value.ApartmentEnter, false);
                    string expected = value.ApartmentEnter ? value.Apartment.apartment.Guid : "";
                    if (CurrentProperty != expected || value.Apartment.Entered != value.ApartmentEnter)
                        throw new InvalidOperationException("The native apartment context did not match its authorized door.");
                    RemoveApartmentObserver(); value.Apartment = null; value.Door = null; value.Phase = Phase.Prepare;
                }
                if (value.Phase == Phase.Settle)
                {
                    // Native position/venue trackers need a frame after teleport.
                    if (Time.frameCount <= value.PlacedFrame || !DedicatedWorldRuntime.IsWorldReady || !TravelLocksReleased()) return;
                    var character = PlayerManager.Instance.LocalPlayer?.Character;
                    if (!ContextMatches(value.Target) || character == null || !Finite(character.transform.position) ||
                        (character.transform.position - value.PlacedPosition).sqrMagnitude > 1)
                        throw new InvalidOperationException("The native player did not settle in the requested map context. " + SettlementDiagnostic(value));
                    operation = null;
                    if (value.Client) ClientRestoreComplete = true;
                    else completedWorkerContext = PlayerMapLocation.Copy(value.Target);
                    Diagnostic?.Invoke("MAP_CONTEXT complete epoch=" + value.Epoch + " client=" + value.Client +
                        " scene=" + value.Target.Scene + " property=" + value.Target.PropertyId);
                    return;
                }
                if (value.Phase != Phase.Prepare) return;
                if (!value.Client) DetachWorkerBoat();
                RequireIdleNative();
                string property = CurrentProperty;
                if (property.Length > 0 && (DedicatedWorldRuntime.Scene != value.Target.Scene || property != value.Target.PropertyId))
                { InvokeApartment(value, property, false); return; }
                if (DedicatedWorldRuntime.Scene != value.Target.Scene)
                { InvokeTravel(value); return; }
                if (property != value.Target.PropertyId)
                { InvokeApartment(value, value.Target.PropertyId, true); return; }
                var controller = PlayerManager.Instance.LocalPlayer.Character.Controller;
                if (value.Target.HasPosition)
                {
                    value.MutationStarted = true;
                    value.PlacedPosition = new Vector3(value.Target.X, value.Target.Y, value.Target.Z);
                    controller.TeleportPlayer(value.PlacedPosition,
                        Quaternion.Euler(0, value.Target.Yaw, 0), false);
                }
                else
                {
                    // A disconnect can follow an approved arrival before its first
                    // pose. Restore the canonical arrival, never the worker's focus.
                    var target = ResolveFallbackArrival(value);
                    value.PlacedPosition = target.position; value.MutationStarted = true;
                    controller.TeleportPlayer(target, true);
                }
                value.PlacedFrame = Time.frameCount; value.Phase = Phase.Settle;
            }
            catch (Exception ex)
            {
                if (!value.Client && !value.MutationStarted)
                {
                    // Rejected worker preflight has not changed the native world.
                    // The session can reject only its queued actor and keep running.
                    RemoveObservers(); operation = null; throw;
                }
                Abort("Native map context failed: " + ex.Message);
            }
        }

        private static void InvokeTravel(Operation value)
        {
            if (value.Portal == null || value.Portal.SceneName != value.Target.Scene ||
                DedicatedWorldRuntime.Scene != value.FromScene)
                throw new InvalidOperationException("The authoritative destination portal changed before loading.");
            value.Phase = Phase.Travel; value.ArrivalObserved = false;
            arrivalEvent = TravelManager.OnAreaArrival;
            if (ReferenceEquals(arrivalEvent, null)) throw new InvalidOperationException("Native arrival observation is unavailable.");
            arrivalListener = (Il2CppSystem.Action<WorldLocation>)(Action<WorldLocation>)(location =>
            {
                if (!ReferenceEquals(operation, value) || !Authority || value.Phase != Phase.Travel || location == null) return;
                if (location.Guid == value.Portal.TargetLocation.Guid && location.SceneName == value.Target.Scene)
                    value.ArrivalObserved = true;
            });
            arrivalEvent.AddListener(arrivalListener);
            // Focus changes have no cab cinematic and no global client event. Native
            // scene cleanup, player placement and lock release still run normally.
            value.MutationStarted = true;
            TravelManager.Instance.RequestTravel(value.Portal, false, false, null);
        }
        private static void InvokeApartment(Operation value, string id, bool enter)
        {
            var apartment = ResolveApartment(id);
            var door = enter ? apartment.entrance : apartment.exit;
            ValidateApartment(apartment, door, enter, true);
            value.Apartment = apartment; value.Door = door; value.ApartmentEnter = enter;
            value.ApartmentObserved = false; value.Phase = enter ? Phase.EnterApartment : Phase.ExitApartment;
            apartmentEvent = ApartmentController.OnPlayerEnterExit;
            if (ReferenceEquals(apartmentEvent, null)) throw new InvalidOperationException("Native apartment observation is unavailable.");
            apartmentListener = (Il2CppSystem.Action<ApartmentController, bool>)(Action<ApartmentController, bool>)((observed, entered) =>
            {
                if (!ReferenceEquals(operation, value) || !Authority) return;
                if (!AllowNativeApartmentObservation(observed, entered) || value.ApartmentObserved)
                    value.NativeError = "A native apartment callback changed or repeated the authorized context.";
                else value.ApartmentObserved = true;
            });
            apartmentEvent.Add(apartmentListener);
            value.MutationStarted = true;
            door.Enter();
            // Enter() teleports and invokes the native apartment callback synchronously.
            // The independent observer also works before client authority is enabled.
            if (!value.ApartmentObserved)
                throw new InvalidOperationException("The native apartment event did not acknowledge the authorized door.");
        }
        public static bool AllowNativeTravel(PortalKey key, bool cab, bool force, Nivalis.Boat.BoatGhost boat)
        {
            var value = operation;
            return Authority && value != null && value.Phase == Phase.Travel && !cab && !force && boat == null &&
                key != null && value.Portal != null && key.Pointer == value.Portal.Pointer &&
                key.Guid == value.Portal.Guid && key.SceneName == value.Target.Scene;
        }
        public static bool AllowNativeLoad(string to, string from, bool title, bool credits)
        {
            var value = operation;
            return Authority && value != null && value.Phase == Phase.Travel && !title && !credits &&
                to == value.Target.Scene && from == value.FromScene;
        }
        public static bool AllowNativeApartment(ApartmentDoor door)
        {
            var value = operation;
            return Authority && value != null && (value.Phase == Phase.ExitApartment || value.Phase == Phase.EnterApartment) &&
                door != null && value.Door != null && door.Pointer == value.Door.Pointer;
        }
        public static bool AllowNativeApartmentObservation(ApartmentController controller, bool enter)
        {
            var value = operation;
            if (!Authority) return false;
            try
            {
                if (value != null && (value.Phase == Phase.ExitApartment || value.Phase == Phase.EnterApartment) &&
                    controller != null && value.Apartment != null && controller.Pointer == value.Apartment.Pointer &&
                    enter == value.ApartmentEnter) return true;
                if (IsNativeStartupExit(value, controller, enter) && value.StartupExits.Add(controller.Pointer))
                {
                    TraceApartmentObservation(value, controller, enter, "startup-reset-accepted");
                    return true;
                }
                TraceApartmentObservation(value, controller, enter, "rejected");
            }
            catch (Exception ex)
            {
                // This is called inside the native event bus; rejection must not
                // unwind a Unity callback or alter the operation's authorization.
                Diagnostic?.Invoke("MAP_CONTEXT apartment-observation rejected phase=" + (value?.Phase.ToString() ?? "idle") +
                    " reason=" + ex.GetType().Name);
            }
            return false;
        }
        private static bool IsNativeStartupExit(Operation value, ApartmentController controller, bool enter)
        {
            // Start schedules this exact one-frame ForceExit reset for every
            // non-entered apartment. ForceExit emits false before clearing its
            // coroutine handle, even though no player property change occurred.
            if (value == null || value.Phase != Phase.Travel || enter || controller == null || controller.apartment == null ||
                value.FromScene == value.Target.Scene || !controller.gameObject.scene.isLoaded ||
                controller.gameObject.scene.name != value.Target.Scene || ReferenceEquals(controller._forceExitRoutine, null) ||
                !ApartmentManager.InstanceNotNull() || controller.Entered || CurrentProperty.Length != 0) return false;
            var canonical = ApartmentManager.Instance.GetState(controller.apartment)?.controller;
            return canonical != null && canonical.Pointer == controller.Pointer;
        }
        private static void TraceApartmentObservation(Operation value, ApartmentController controller, bool enter, string result)
        {
            Diagnostic?.Invoke("MAP_CONTEXT apartment-observation " + result +
                " phase=" + (value?.Phase.ToString() ?? "idle") + " enter=" + enter +
                " controller=" + (controller?.name ?? "null") + " apartment=" + (controller?.apartment?.Guid ?? "") +
                " scene=" + (controller == null ? "" : controller.gameObject.scene.name) +
                " property=" + CurrentProperty + " startupRoutine=" + (controller != null && !ReferenceEquals(controller._forceExitRoutine, null)));
        }
        private static PortalKey ResolvePortal(PlayerMapLocation context)
        {
            if (!TravelUnlockManager.InstanceNotNull() || !GameSceneManager.InstanceNotNull())
                throw new InvalidOperationException("Native map references are not ready.");
            var locations = GameSceneManager.Instance.worldLocations;
            if (locations == null || locations.Length > 512) throw new InvalidOperationException("Native district registry is unavailable.");
            WorldLocation location = null;
            foreach (var candidate in locations)
                if (candidate != null && candidate.SceneName == context.Scene)
                {
                    if (location != null && location.Pointer != candidate.Pointer)
                        throw new InvalidOperationException("Native district identity is ambiguous.");
                    location = candidate;
                }
            if (location == null || location.SceneName != context.Scene || location.SceneIndex < 0 || !GameSceneManager.Instance.IsGameplayScene(location.SceneIndex) ||
                !TravelUnlockManager.Instance.IsLocationUnlocked(location))
                throw new InvalidOperationException("The account's district is not an unlocked gameplay map.");
            var keys = TravelUnlockManager.Instance._allPortalKeys;
            if (keys == null || keys.Length > 8192) throw new InvalidOperationException("Native portal registry is unavailable.");
            string id = context.PortalId;
            if (id.Length == 0)
            {
                // Legacy accounts have no arrival portal. Some districts (Lowtown)
                // default to an apartment portal, which must never implicitly change
                // account property membership. Select only a registered public route.
                var defaultKey = location.DefaultLocation;
                PortalKey preferred = null, fallback = null;
                foreach (var key in keys)
                {
                    if (!IsCanonicalPublicPortal(key, location) || string.IsNullOrEmpty(key.Guid)) continue;
                    if (defaultKey != null && key.Pointer == defaultKey.Pointer) preferred = key;
                    if (fallback == null || string.CompareOrdinal(key.Guid, fallback.Guid) < 0) fallback = key;
                }
                id = (preferred ?? fallback)?.Guid ?? "";
            }
            if (id.Length == 0) throw new InvalidOperationException("The account's district has no canonical public arrival portal.");
            PortalKey selected = null;
            var names = new System.Collections.Generic.Dictionary<string, PortalKey>(StringComparer.Ordinal);
            foreach (var key in keys)
            {
                if (key == null || key.Guid != id || !IsCanonicalPublicPortal(key, location)) continue;
                // Authored public arrival variants can share a GUID (Lowtown and
                // LowtownTrain); even different districts can collide (Dev Room
                // and Seaside Boardwalk). First constrain the authoritative map,
                // then choose a stable native asset for its public arrival.
                string name = key.name;
                if (string.IsNullOrEmpty(name)) throw new InvalidOperationException("Native portal identity has no stable asset name.");
                if (names.TryGetValue(name, out var prior) && prior.Pointer != key.Pointer)
                    throw new InvalidOperationException("Native portal asset name is ambiguous within its district.");
                names[name] = key;
                if (selected == null || string.CompareOrdinal(name, selected.name) < 0) selected = key;
            }
            if (!IsCanonicalPublicPortal(selected, location))
                throw new InvalidOperationException("The account's arrival portal is not canonical for its district.");
            return selected;
        }
        private static bool IsCanonicalPublicPortal(PortalKey key, WorldLocation location) =>
            key != null && key.SceneName == location.SceneName && key.SceneIndex >= 0 && key.Apartment == null &&
            key.TargetLocation != null && key.TargetLocation.Pointer == location.Pointer;
        private static ApartmentController ResolveApartment(string id)
        {
            var controllers = Object.FindObjectsOfType<ApartmentController>(true);
            if (controllers.Length > 512 || string.IsNullOrEmpty(id)) throw new InvalidOperationException("Invalid native apartment registry.");
            ApartmentController selected = null;
            foreach (var candidate in controllers)
            {
                if (candidate == null || candidate.apartment == null || candidate.apartment.Guid != id ||
                    !candidate.gameObject.scene.isLoaded || candidate.gameObject.scene.name != DedicatedWorldRuntime.Scene) continue;
                if (selected != null && selected.Pointer != candidate.Pointer)
                    throw new InvalidOperationException("Native apartment identity is ambiguous.");
                selected = candidate;
            }
            return selected ?? throw new InvalidOperationException("The account's apartment is not present in its district.");
        }
        private static Transform ResolveFallbackArrival(Operation value)
        {
            Transform target;
            if (value.Target.PropertyId.Length > 0)
            {
                var apartment = ResolveApartment(value.Target.PropertyId);
                ValidateApartment(apartment, apartment.entrance, true, false);
                if (!apartment.Entered) throw new InvalidOperationException("The apartment fallback is not entered.");
                target = apartment.entrance.teleportPoint;
            }
            else
            {
                var key = value.Portal ?? ResolvePortal(value.Target);
                var portal = TravelManager.Instance.GetTravelPortal(key);
                if (portal == null || portal.keyTo == null || portal.keyTo.Pointer != key.Pointer ||
                    !portal.gameObject.scene.isLoaded || portal.gameObject.scene.name != value.Target.Scene)
                    throw new InvalidOperationException("The account's arrival portal is not loaded in its district.");
                target = portal.SpawnPosition;
            }
            if (target == null || !target.gameObject.scene.isLoaded || target.gameObject.scene.name != value.Target.Scene || !Finite(target.position))
                throw new InvalidOperationException("The account's canonical fallback arrival is unavailable.");
            return target;
        }
        private static void ValidateApartment(ApartmentController controller, ApartmentDoor door, bool enter, bool before)
        {
            if (controller == null || controller.apartment == null || !ApartmentManager.InstanceNotNull() || door == null ||
                door.teleportPoint == null || !controller.gameObject.scene.isLoaded || controller.gameObject.scene.name != DedicatedWorldRuntime.Scene ||
                door.gameObject.scene.name != DedicatedWorldRuntime.Scene || !door.gameObject.scene.isLoaded ||
                door.teleportPoint.gameObject.scene.name != DedicatedWorldRuntime.Scene || !door.teleportPoint.gameObject.scene.isLoaded ||
                (enter ? controller.entrance : controller.exit)?.Pointer != door.Pointer || controller.enterAndExitWithDistance)
                throw new InvalidOperationException("The native apartment route is unavailable or crosses maps.");
            var canonical = ApartmentManager.Instance.GetState(controller.apartment)?.controller;
            if (canonical == null || canonical.Pointer != controller.Pointer || controller.apartment.StageAccess ||
                (!controller.apartment.PlayerOwned && !controller.apartment.Shelter))
                throw new InvalidOperationException("The account's apartment has no canonical usable native state.");
            if (before && (!door.isActiveAndEnabled || !door.IsActive || !door.CanInteract() ||
                controller.Entered == enter || CurrentProperty != (enter ? "" : controller.apartment.Guid)))
                throw new InvalidOperationException("The native apartment door is not ready for the context change.");
        }
        private static bool ContextMatches(PlayerMapLocation value) => DedicatedWorldRuntime.IsWorldReady &&
            DedicatedWorldRuntime.Scene == value.Scene && CurrentProperty == value.PropertyId;
        private static bool PositionMatches(PlayerMapLocation value)
        {
            if (!value.HasPosition) return true;
            var character = PlayerManager.Instance.LocalPlayer?.Character;
            return character != null && (character.transform.position - new Vector3(value.X, value.Y, value.Z)).sqrMagnitude <= 1;
        }
        private static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z) &&
            Math.Abs(value.x) <= 100000 && Math.Abs(value.y) <= 100000 && Math.Abs(value.z) <= 100000;
        private static bool TravelLocksReleased() => TravelManager.InstanceNotNull() &&
            ReferenceEquals(TravelManager.Instance._timePauseLock, null) && ReferenceEquals(TravelManager.Instance._preventShortcutBool, null);
        private static void DetachWorkerBoat()
        {
            // A worker may overlap the saved native boat while focusing another
            // player. PlayerNavMesh sets its attachment before OnDeckEnter, so
            // boardedBoat alone cannot detect the resulting position constraint.
            if (BoatReplication.DetachWorkerBoatAttachment())
                Diagnostic?.Invoke("MAP_CONTEXT detached stale worker boat navigation attachment");
        }
        private static string SettlementDiagnostic(Operation value)
        {
            try
            {
                var character = PlayerManager.Instance.LocalPlayer?.Character;
                var controller = character?.Controller;
                return "scene=" + DedicatedWorldRuntime.Scene + " property=" + CurrentProperty +
                    " expected=" + PositionDiagnostic(value.PlacedPosition) +
                    " actual=" + (character == null ? "missing" : PositionDiagnostic(character.transform.position)) +
                    " navMesh=" + (controller?.NavMesh?.name ?? "none") +
                    " boardedBoat=" + (controller?.boardedBoat?.name ?? "none") +
                    " client=" + value.Client;
            }
            catch (Exception ex) { return "Native position diagnostic unavailable: " + ex.GetType().Name; }
        }
        private static string PositionDiagnostic(Vector3 value) =>
            value.x.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "," +
            value.y.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "," +
            value.z.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        private static void RequireIdleNative()
        {
            // Native CanTravel is only !Curfew.IsCurfew (02:00-08:00), not a
            // loading/transition lock. Internal worker focus and authenticated
            // account restoration must also work at night. RequestTravel and its
            // teleport coroutine have no inner curfew gate; exact route permits
            // above authorize only this bounded operation. Ordinary player travel
            // still checks CanTravel in TravelReplication.RequireWorldReady.
            if (!DedicatedWorldRuntime.IsWorldReady || !TravelManager.InstanceNotNull() || !TravelLocksReleased() ||
                SleepReplication.IsBusy || DialogueReplication.IsConversationActive)
                throw new InvalidOperationException("Wait for the current native map or day transition to finish.");
            var player = PlayerManager.Instance.LocalPlayer;
            if (player == null || player.IsHoldingObject || player.Character?.Controller == null || player.Character.Controller.boardedBoat != null)
                throw new InvalidOperationException("Native map context cannot change while holding an object or aboard a boat.");
        }
        private static void RequireAuthority(bool worker)
        {
            if (!Authority || DedicatedWorldRuntime.IsDedicatedServer != worker)
                throw new InvalidOperationException("Native map context authority is unavailable.");
        }
        private static void Validate(PlayerMapLocation context, double timestamp)
        { PlayerMapLocation.Validate(context); if (!double.IsFinite(timestamp)) throw new ArgumentOutOfRangeException(nameof(timestamp)); }
        private static bool Same(PlayerMapLocation a, PlayerMapLocation b) => a.Scene == b.Scene && a.PropertyId == b.PropertyId &&
            a.PortalId == b.PortalId && a.HasPosition == b.HasPosition && a.X == b.X && a.Y == b.Y && a.Z == b.Z && a.Yaw == b.Yaw;
        private static void RemoveArrivalObserver()
        {
            if (!ReferenceEquals(arrivalEvent, null) && !ReferenceEquals(arrivalListener, null))
                try { arrivalEvent.RemoveListener(arrivalListener); } catch { /* expired operation cannot authorize another route */ }
            arrivalEvent = null; arrivalListener = null;
        }
        private static void RemoveApartmentObserver()
        {
            if (!ReferenceEquals(apartmentEvent, null) && !ReferenceEquals(apartmentListener, null))
                try { apartmentEvent.Remove(apartmentListener); } catch { /* callback from an expired operation is inert */ }
            apartmentEvent = null; apartmentListener = null;
        }
        private static void RemoveObservers() { RemoveArrivalObserver(); RemoveApartmentObserver(); }
        private static void Abort(string reason)
        {
            FailureReason = reason; terminal = true; RemoveObservers(); operation = null;
            completedWorkerContext = null; ClientRestoreComplete = false; Diagnostic?.Invoke("MAP_CONTEXT fatal " + reason);
            failure?.Invoke(reason);
        }
    }
}
