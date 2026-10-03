using System;
using System.Globalization;
using Nivalis;
using UnityEngine;
using Object = UnityEngine.Object;

namespace NivalisTogether.Dedicated
{
    /// <summary>Opt-in, read-only loaded-world inventory for native acceptance work.</summary>
    internal static class NativeWorldDiagnostics
    {
        private static readonly bool Requested = Environment.GetEnvironmentVariable("NIVALIS_TRACE_WORLD") == "1";
        private static string observedScene = "";

        internal static void Tick(Action<string> log)
        {
            if (!Requested || log == null || !DedicatedWorldRuntime.IsDedicatedServer || !DedicatedWorldRuntime.IsWorldReady) return;
            string scene = DedicatedWorldRuntime.Scene;
            if (scene.Length == 0 || scene == observedScene) return;
            observedScene = scene;
            try
            {
                var terminals = Object.FindObjectsOfType<SceneTransitionInteractable>(true);
                var beds = Object.FindObjectsOfType<Bed>(true);
                var apartments = Object.FindObjectsOfType<Nivalis.Apartment.ApartmentController>(true);
                Nivalis.GhostSystem.CustomerLoop.BaseProperty currentProperty = null;
                bool currentPropertyKnown = false;
                try
                {
                    var player = PlayerManager.InstanceNotNull() ? PlayerManager.Instance.LocalPlayer : null;
                    if (player != null)
                    {
                        currentProperty = player.CurrentProperty;
                        currentPropertyKnown = true;
                        log("WORLD_PLAYER_CONTEXT currentPropertyGuid=" + Cut(currentProperty?.Guid) +
                            " currentPropertyNone=" + (currentProperty == null));
                    }
                    else log("WORLD_PLAYER_CONTEXT unavailable=LocalPlayerMissing");
                }
                catch (Exception ex) { log("WORLD_PLAYER_CONTEXT unavailable=" + ex.GetType().Name); }
                log("WORLD_DIAGNOSTICS scene=" + scene + " terminalObjects=" + terminals.Length + " bedObjects=" + beds.Length +
                    " apartmentObjects=" + apartments.Length);
                int reported = 0;
                for (int i = 0; i < terminals.Length && reported < 96; i++)
                {
                    var terminal = terminals[i];
                    if (terminal == null || !terminal.gameObject.scene.isLoaded || terminal.gameObject.scene.name != scene) continue;
                    reported++;
                    try
                    {
                        var key = terminal.keyTo;
                        log("WORLD_TERMINAL name=" + Cut(terminal.name) + " active=" + terminal.isActiveAndEnabled +
                            " available=" + terminal.IsActive + " canInteract=" + terminal.CanInteract() +
                            " position=" + Position(terminal.transform.position) + " cab=" + terminal.isCabTravel +
                            " portal=" + Cut(key?.Guid) + " destination=" + Cut(key?.SceneName) + " apartment=" + (key?.Apartment != null));
                    }
                    catch (Exception ex) { log("WORLD_TERMINAL unavailable=" + ex.GetType().Name); }
                }
                reported = 0;
                for (int i = 0; i < beds.Length && reported < 32; i++)
                {
                    var bed = beds[i];
                    if (bed == null || !bed.gameObject.scene.isLoaded || bed.gameObject.scene.name != scene) continue;
                    reported++;
                    try
                    {
                        var apartment = bed.Furniture?.CurrentProperty?.TryCast<Nivalis.Apartment.Apartment>();
                        var state = apartment == null ? null : Nivalis.Apartment.ApartmentManager.Instance.GetState(apartment);
                        log("WORLD_BED name=" + Cut(bed.name) + " active=" + bed.isActiveAndEnabled + " available=" + bed.IsActive +
                            " canInteract=" + bed.CanInteract() + " position=" + Position(bed.transform.position) +
                            " targetId=" + SleepReplication.DiagnosticBedId(bed) +
                            " apartment=" + (apartment != null) + " owned=" + (apartment != null && apartment.PlayerOwned) +
                            " shelter=" + (apartment != null && apartment.Shelter) + " apartmentScene=" + Cut(state?.controller?.gameObject.scene.name) +
                            " apartmentGuid=" + Cut(apartment?.Guid) + " matchesCurrentProperty=" +
                            PropertyMatch(apartment, currentProperty, currentPropertyKnown));
                    }
                    catch (Exception ex) { log("WORLD_BED unavailable=" + ex.GetType().Name); }
                }
                reported = 0;
                for (int i = 0; i < apartments.Length && reported < 32; i++)
                {
                    try
                    {
                        var controller = apartments[i];
                        if (controller == null || !controller.gameObject.scene.isLoaded) continue;
                        reported++;
                        var apartment = controller.apartment;
                        string guid = Cut(apartment?.Guid);
                        log("WORLD_APARTMENT apartmentGuid=" + guid + " name=" + Cut(controller.name) +
                            " scene=" + Cut(controller.gameObject.scene.name) + " active=" + controller.isActiveAndEnabled +
                            " position=" + Position(controller.transform.position) + " entered=" + controller.Entered +
                            " enterAndExitWithDistance=" + controller.enterAndExitWithDistance +
                            " owned=" + (apartment != null && apartment.PlayerOwned) +
                            " shelter=" + (apartment != null && apartment.Shelter) +
                            " matchesCurrentProperty=" + PropertyMatch(apartment, currentProperty, currentPropertyKnown));
                        ReportDoor(log, controller.entrance, guid, "entrance");
                        ReportDoor(log, controller.exit, guid, "exit");
                    }
                    catch (Exception ex) { log("WORLD_APARTMENT unavailable=" + ex.GetType().Name); }
                }
            }
            catch (Exception ex) { log("WORLD_DIAGNOSTICS unavailable=" + ex.GetType().Name); }
        }
        private static string PropertyMatch(Nivalis.Apartment.Apartment apartment,
            Nivalis.GhostSystem.CustomerLoop.BaseProperty currentProperty, bool known)
            => !known ? "unknown" : (apartment != null && currentProperty != null && apartment.Pointer == currentProperty.Pointer).ToString();

        private static void ReportDoor(Action<string> log, Nivalis.Apartment.ApartmentDoor door, string apartmentGuid, string side)
        {
            try
            {
                string prefix = "WORLD_APARTMENT_DOOR apartmentGuid=" + apartmentGuid + " side=" + side;
                if (door == null) { log(prefix + " present=False"); return; }
                var target = door.teleportPoint;
                string targetState = target == null ? " teleportPresent=False" :
                    " teleportPresent=True teleportScene=" + Cut(target.gameObject.scene.name) +
                    " teleportLoaded=" + target.gameObject.scene.isLoaded +
                    " teleportActive=" + target.gameObject.activeInHierarchy + " teleportPosition=" + Position(target.position);
                log(prefix + " present=True name=" + Cut(door.name) + " scene=" + Cut(door.gameObject.scene.name) +
                    " loaded=" + door.gameObject.scene.isLoaded + " active=" + door.isActiveAndEnabled +
                    " available=" + door.IsActive + " position=" + Position(door.transform.position) + targetState);
            }
            catch (Exception ex) { log("WORLD_APARTMENT_DOOR apartmentGuid=" + apartmentGuid + " side=" + side + " unavailable=" + ex.GetType().Name); }
        }
        private static string Cut(string value)
        {
            value = (value ?? "").Replace('\r', ' ').Replace('\n', ' ');
            return value.Length <= 128 ? value : value.Substring(0, 128);
        }
        private static string Position(Vector3 value) => value.x.ToString("F3", CultureInfo.InvariantCulture) + "," +
            value.y.ToString("F3", CultureInfo.InvariantCulture) + "," + value.z.ToString("F3", CultureInfo.InvariantCulture);
    }
}
