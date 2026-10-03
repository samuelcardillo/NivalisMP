using System;
using Nivalis.Apartment;
using NivalisTogether.Dedicated;

public static class WorkerApartmentObservationTests
{
    private static int assertions;
    private static void Check(bool condition, string message)
    { assertions++; if (!condition) throw new Exception(message); }
    private static ApartmentController Controller(int pointer, string scene = "1_Lowtown")
    {
        var controller = new ApartmentController { Pointer = new IntPtr(pointer), name = "Apartment " + pointer,
            apartment = new Apartment { Guid = "apartment-" + pointer }, _forceExitRoutine = new object(),
            gameObject = new GameObject { scene = new Scene { name = scene, isLoaded = true } } };
        ApartmentManager.Instance.States[controller.apartment] = new ApartmentState { controller = controller };
        return controller;
    }
    private static void Travel() => WorkerSceneResidency.BeginTest("Travel", "2_Meridian_Market", "1_Lowtown");
    public static int Run()
    {
        var controller = Controller(1); Travel();
        Check(WorkerSceneResidency.AllowNativeApartmentObservation(controller, false), "canonical destination startup reset is accepted");
        Check(WorkerSceneResidency.Logs.Exists(s => s.Contains("startup-reset-accepted") && s.Contains("scene=1_Lowtown") && s.Contains("startupRoutine=True")), "accepted reset logs its exact native context");
        Check(!WorkerSceneResidency.AllowNativeApartmentObservation(controller, false), "same controller reset cannot repeat in one operation");
        Check(WorkerSceneResidency.Logs.Exists(s => s.Contains("rejected") && s.Contains("phase=Travel")), "rejection logs the operation phase");
        var another = Controller(2);
        Check(WorkerSceneResidency.AllowNativeApartmentObservation(another, false), "another canonical apartment gets its own startup reset");
        Travel();
        Check(WorkerSceneResidency.AllowNativeApartmentObservation(controller, false), "new bounded travel operation gets fresh reset permits");
        Travel();
        Check(!WorkerSceneResidency.AllowNativeApartmentObservation(controller, true), "startup reset never permits entry");
        controller.Entered = true;
        Check(!WorkerSceneResidency.AllowNativeApartmentObservation(controller, false), "entered apartment cannot use startup exemption");
        controller.Entered = false; controller._forceExitRoutine = null;
        Check(!WorkerSceneResidency.AllowNativeApartmentObservation(controller, false), "ordinary exit without startup coroutine is rejected");
        controller._forceExitRoutine = new object(); controller.gameObject.scene.isLoaded = false;
        Check(!WorkerSceneResidency.AllowNativeApartmentObservation(controller, false), "unloaded controller is rejected");
        controller.gameObject.scene.isLoaded = true; controller.gameObject.scene.name = "2_Meridian_Market";
        Check(!WorkerSceneResidency.AllowNativeApartmentObservation(controller, false), "source-scene controller is rejected");
        controller.gameObject.scene.name = "3_Midtown";
        Check(!WorkerSceneResidency.AllowNativeApartmentObservation(controller, false), "unrelated-scene controller is rejected");
        controller.gameObject.scene.name = "1_Lowtown";
        ApartmentManager.Instance.States[controller.apartment].controller = another;
        Check(!WorkerSceneResidency.AllowNativeApartmentObservation(controller, false), "noncanonical controller is rejected");
        ApartmentManager.Instance.States[controller.apartment].controller = controller;
        WorkerSceneResidency.CurrentProperty = controller.apartment.Guid;
        Check(!WorkerSceneResidency.AllowNativeApartmentObservation(controller, false), "occupied native property cannot be cleared by startup exemption");
        WorkerSceneResidency.CurrentProperty = "";
        foreach (string phase in new[] { "Prepare", "Settle", "EnterApartment", "ExitApartment" })
        {
            WorkerSceneResidency.BeginTest(phase, "2_Meridian_Market", "1_Lowtown");
            Check(!WorkerSceneResidency.AllowNativeApartmentObservation(controller, false), "startup permit is unavailable in " + phase);
        }
        WorkerSceneResidency.BeginTest("Travel", "1_Lowtown", "1_Lowtown");
        Check(!WorkerSceneResidency.AllowNativeApartmentObservation(controller, false), "same-scene operation cannot claim scene startup");
        Travel(); WorkerSceneResidency.Authority = false;
        Check(!WorkerSceneResidency.AllowNativeApartmentObservation(controller, false), "unconfigured or terminal authority never grants permit");
        Travel(); WorkerSceneResidency.ClearTest();
        Check(!WorkerSceneResidency.AllowNativeApartmentObservation(controller, false), "completed operation never grants startup permit");
        Travel();
        Check(!WorkerSceneResidency.AllowNativeApartmentObservation(null, false), "null callback remains rejected");
        var missing = ApartmentManager.Instance; ApartmentManager.Instance = null;
        Check(!WorkerSceneResidency.AllowNativeApartmentObservation(controller, false), "missing native manager remains rejected");
        ApartmentManager.Instance = missing;
        WorkerSceneResidency.BeginTest("EnterApartment", "1_Lowtown", "1_Lowtown", controller, true);
        Check(WorkerSceneResidency.AllowNativeApartmentObservation(controller, true), "exact authorized apartment entry still works");
        Check(!WorkerSceneResidency.AllowNativeApartmentObservation(another, true), "wrong controller cannot use authorized apartment entry");
        Check(!WorkerSceneResidency.AllowNativeApartmentObservation(controller, false), "wrong direction cannot use authorized apartment entry");
        WorkerSceneResidency.BeginTest("ExitApartment", "1_Lowtown", "1_Lowtown", controller, false);
        Check(WorkerSceneResidency.AllowNativeApartmentObservation(controller, false), "exact authorized apartment exit still works");
        return assertions;
    }
}
namespace Nivalis.Apartment
{
    public sealed class Apartment { public string Guid; }
    public sealed class Scene { public string name; public bool isLoaded; }
    public sealed class GameObject { public Scene scene; }
    public sealed class ApartmentController
    {
        public IntPtr Pointer; public string name; public Apartment apartment; public GameObject gameObject;
        public bool Entered; public object _forceExitRoutine;
    }
    public sealed class ApartmentState { public ApartmentController controller; }
    public sealed class ApartmentManager
    {
        public static ApartmentManager Instance = new ApartmentManager();
        public readonly System.Collections.Generic.Dictionary<Apartment, ApartmentState> States = new System.Collections.Generic.Dictionary<Apartment, ApartmentState>();
        public static bool InstanceNotNull() => Instance != null;
        public ApartmentState GetState(Apartment apartment) => States.TryGetValue(apartment, out var value) ? value : null;
    }
}
