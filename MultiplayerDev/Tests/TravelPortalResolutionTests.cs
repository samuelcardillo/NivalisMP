using System;
using Nivalis;
using NivalisTogether.Dedicated;
using UnityEngine;

// The runner extracts the production resolution methods and text validator.
// These facades supply native assets and loaded scene objects only.
public static class TravelPortalResolutionTests
{
    private static int assertions;
    private static void Check(bool condition, string message)
    { assertions++; if (!condition) throw new Exception(message); }
    private static void Reject(Action action, string message)
    { bool rejected = false; try { action(); } catch (InvalidOperationException) { rejected = true; } Check(rejected, message); }
    private static PortalKey Resolve(PortalKey key) => TravelReplication.ResolveForTest(key.Guid, key.SceneName, key.name, key.TargetLocation.Guid);
    private static SceneTransitionInteractable Transition(PortalKey key, float x, bool cab = false) => new SceneTransitionInteractable {
        keyTo = key, isCabTravel = cab, isActiveAndEnabled = true,
        gameObject = new GameObject { scene = new Scene { name = DedicatedWorldRuntime.Scene, isLoaded = true } },
        transform = new Transform { position = new Vector3(x, 0, 0) }
    };
    private static SceneTransitionInteractable Find(PortalKey key) =>
        TravelReplication.TransitionForTest(key.Guid, key.SceneName, key.name, key.TargetLocation.Guid, new Vector3(0, 0, 0));
    private static PortalKey Clone(PortalKey key, long pointer) => new PortalKey {
        Pointer = pointer, Guid = key.Guid, name = key.name, TargetLocation = key.TargetLocation, Apartment = key.Apartment
    };

    public static int Run()
    {
        // Captured from the native 101-entry registry: equal GUIDs identify
        // different named assets even when they share a destination scene.
        var lowtown = new WorldLocation { Guid = "ea81e56d-65bf-42e6-bef9-90f6d1466ea6", SceneName = "1_Lowtown", SceneIndex = 1 };
        var meridian = new WorldLocation { Guid = "d2034f98-532b-4b62-b9d9-11fe43b1fe51", SceneName = "2_Meridian_Market", SceneIndex = 2 };
        var boardwalk = new WorldLocation { Guid = "75695152-7f17-4acc-9a18-f46fc090e026", SceneName = "9_Seaside_Boardwalk", SceneIndex = 9 };
        var devRoom = new WorldLocation { Guid = "123d48f4-f503-403d-b839-ddb1086692eb", SceneName = "0_Dev_Room", SceneIndex = -1 };
        var lowtownRoad = new PortalKey { Pointer = 1, Guid = "89c5c346-ed08-4697-b181-ea39d54fafe1", name = "Lowtown", TargetLocation = lowtown };
        var lowtownTrain = new PortalKey { Pointer = 2, Guid = lowtownRoad.Guid, name = "LowtownTrain", TargetLocation = lowtown };
        var meridianWest = new PortalKey { Pointer = 3, Guid = "10ca9a93-555f-4b17-bc2a-cea368d60ae0", name = "Meridian Market - Train station", TargetLocation = meridian };
        var meridianEast = new PortalKey { Pointer = 4, Guid = meridianWest.Guid, name = "Meridian Market - Train station Eastern", TargetLocation = meridian };
        var boardwalkKey = new PortalKey { Pointer = 5, Guid = "3705cac6-9d52-4bbe-9b10-b964a1349ad4", name = "Seaside Boardwalk", TargetLocation = boardwalk };
        var devKey = new PortalKey { Pointer = 6, Guid = boardwalkKey.Guid, name = "Dev Room", TargetLocation = devRoom };
        var registry = new[] { devKey, lowtownTrain, meridianEast, boardwalkKey, lowtownRoad, meridianWest };
        TravelUnlockManager.Instance._allPortalKeys = registry;
        Check(Resolve(lowtownRoad) == lowtownRoad, "duplicate Lowtown GUID resolves the road asset by full identity");
        Check(Resolve(lowtownTrain) == lowtownTrain, "duplicate Lowtown GUID resolves the train asset by full identity");
        Check(Resolve(meridianWest) == meridianWest, "duplicate Meridian GUID resolves the western station");
        Check(Resolve(meridianEast) == meridianEast, "duplicate Meridian GUID resolves the eastern station");
        Check(Resolve(boardwalkKey) == boardwalkKey, "Boardwalk resolves despite an earlier Dev Room asset with the same GUID");
        Check(Resolve(devKey) == null, "unbuilt Dev Room scene cannot become a travel destination");
        Array.Reverse(registry);
        Check(Resolve(lowtownRoad) == lowtownRoad && Resolve(lowtownTrain) == lowtownTrain && Resolve(boardwalkKey) == boardwalkKey,
            "qualified selection is independent of native registry order");
        Check(TravelReplication.ResolveForTest(lowtownRoad.Guid, lowtown.SceneName, "", lowtown.Guid) == null,
            "missing portal asset name cannot select the first duplicate GUID");
        Check(TravelReplication.ResolveForTest(lowtownRoad.Guid, lowtown.SceneName, lowtownRoad.name, "") == null,
            "missing destination location GUID cannot select a portal");
        Check(TravelReplication.ResolveForTest(lowtownRoad.Guid, "", lowtownRoad.name, lowtown.Guid) == null,
            "missing destination scene cannot select a portal");
        Check(TravelReplication.ResolveForTest("", lowtown.SceneName, lowtownRoad.name, lowtown.Guid) == null,
            "missing portal GUID cannot select a portal");
        Check(TravelReplication.ResolveForTest(lowtownRoad.Guid, lowtown.SceneName, null, lowtown.Guid) == null,
            "null asset name is rejected");
        Check(TravelReplication.ResolveForTest(lowtownRoad.Guid, lowtown.SceneName, "lowtown", lowtown.Guid) == null,
            "asset name matching is exact and case sensitive");
        Check(TravelReplication.ResolveForTest(lowtownRoad.Guid, lowtown.SceneName, lowtownRoad.name, meridian.Guid) == null,
            "foreign location GUID cannot authorize a same-scene asset");
        Check(TravelReplication.ResolveForTest(lowtownRoad.Guid, meridian.SceneName, lowtownRoad.name, lowtown.Guid) == null,
            "foreign scene cannot authorize a matching portal GUID and name");
        Check(TravelReplication.ResolveForTest(lowtownRoad.Guid, lowtown.SceneName, new string('x', 129), lowtown.Guid) == null,
            "oversized asset name is rejected");
        Check(TravelReplication.ResolveForTest(lowtownRoad.Guid, lowtown.SceneName, "Lowtown\n", lowtown.Guid) == null,
            "control characters in asset qualifiers are rejected");

        var wrapper = Clone(lowtownRoad, lowtownRoad.Pointer);
        TravelUnlockManager.Instance._allPortalKeys = new[] { lowtownRoad, lowtownRoad, wrapper, lowtownTrain };
        Check(Resolve(lowtownRoad).Pointer == lowtownRoad.Pointer, "repeated entries and wrappers for one native asset remain valid");
        var ambiguous = Clone(lowtownRoad, 7);
        TravelUnlockManager.Instance._allPortalKeys = new[] { lowtownTrain, lowtownRoad, ambiguous };
        Reject(() => Resolve(lowtownRoad), "equal full identity on different native pointers is ambiguous");
        TravelUnlockManager.Instance._allPortalKeys = new[] { ambiguous, lowtownRoad, lowtownTrain };
        Reject(() => Resolve(lowtownRoad), "ambiguous full identity remains rejected after reordering");
        var apartment = Clone(lowtownRoad, 8); apartment.Apartment = new object();
        var noLocation = Clone(lowtownRoad, 9); noLocation.TargetLocation = null;
        TravelUnlockManager.Instance._allPortalKeys = new[] { apartment, noLocation, lowtownRoad, null };
        Check(Resolve(lowtownRoad) == lowtownRoad, "apartment, missing-location and null assets do not contaminate a public route");
        TravelUnlockManager.Instance._allPortalKeys = new[] { apartment, noLocation, null };
        Check(Resolve(lowtownRoad) == null, "no qualifying public asset is rejected");
        TravelUnlockManager.Instance._allPortalKeys = new PortalKey[8192];
        TravelUnlockManager.Instance._allPortalKeys[8191] = lowtownRoad;
        Check(Resolve(lowtownRoad) == lowtownRoad, "maximum bounded registry includes its final entry");
        TravelUnlockManager.Instance._allPortalKeys = new PortalKey[8193];
        TravelUnlockManager.Instance._allPortalKeys[0] = lowtownRoad;
        Check(Resolve(lowtownRoad) == null, "oversized registry is rejected before taking its first match");
        TravelUnlockManager.Instance._allPortalKeys = null;
        Check(Resolve(lowtownRoad) == null, "missing native registry is rejected");
        TravelUnlockManager.Instance = null;
        Check(Resolve(lowtownRoad) == null, "unavailable unlock manager is rejected");
        TravelUnlockManager.Instance = new TravelUnlockManager { _allPortalKeys = registry };

        DedicatedWorldRuntime.Scene = meridian.SceneName;
        var near = Transition(lowtownRoad, 2);
        var far = Transition(lowtownRoad, 7);
        var duplicateGuidTerminal = Transition(lowtownTrain, 1);
        UnityEngine.Object.Loaded = new[] { duplicateGuidTerminal, far, near };
        Check(Find(lowtownRoad) == near, "loaded terminals match full identity and choose the closest route");
        Check(Find(lowtownTrain) == duplicateGuidTerminal, "same-GUID train terminal retains its own identity");
        UnityEngine.Object.Loaded = new[] { near, far, duplicateGuidTerminal };
        Check(Find(lowtownRoad) == near, "nearest terminal choice is independent of object order");
        var inactive = Transition(lowtownRoad, 0); inactive.isActiveAndEnabled = false;
        var otherScene = Transition(lowtownRoad, 0); otherScene.gameObject.scene.name = lowtown.SceneName;
        var outside = Transition(lowtownRoad, 8.001f);
        var boundary = Transition(lowtownRoad, 8);
        var unloaded = Transition(lowtownRoad, 0); unloaded.gameObject.scene.isLoaded = false;
        var locked = Transition(lowtownRoad, 0); locked.IsActive = false;
        var cannotInteract = Transition(lowtownRoad, 0); cannotInteract.Interactable = false;
        var invalidPosition = Transition(lowtownRoad, float.NaN);
        UnityEngine.Object.Loaded = new[] { inactive, otherScene, outside, null, unloaded, locked, cannotInteract, invalidPosition, boundary };
        Check(Find(lowtownRoad) == boundary, "only active current-scene terminals within eight meters qualify");
        UnityEngine.Object.Loaded = new[] { inactive, otherScene, outside, null, unloaded, locked, cannotInteract, invalidPosition };
        Check(Find(lowtownRoad) == null, "no nearby loaded terminal is rejected");
        var repeatedWrapperTerminal = Transition(wrapper, 1);
        UnityEngine.Object.Loaded = new[] { far, repeatedWrapperTerminal };
        Check(Find(lowtownRoad) == repeatedWrapperTerminal, "distinct wrappers for one native portal can share an unambiguous route");
        var ambiguousTerminal = Transition(ambiguous, 6);
        UnityEngine.Object.Loaded = new[] { near, ambiguousTerminal };
        Reject(() => Find(lowtownRoad), "nearby terminals with identical identity but different native keys are ambiguous");
        var cab = Transition(lowtownRoad, 6, true);
        UnityEngine.Object.Loaded = new[] { near, cab };
        Reject(() => Find(lowtownRoad), "nearby cab and non-cab routes with one identity are ambiguous");
        UnityEngine.Object.Loaded = new[] { cab, near };
        Reject(() => Find(lowtownRoad), "cab ambiguity is independent of enumeration order");
        cab.transform.position = new Vector3(9, 0, 0);
        UnityEngine.Object.Loaded = new[] { near, cab };
        Check(Find(lowtownRoad) == near, "out-of-range terminals do not make the nearby route ambiguous");
        UnityEngine.Object.Loaded = new[] { near };
        Check(TravelReplication.TransitionForTest(lowtownRoad.Guid, lowtown.SceneName, "", lowtown.Guid, new Vector3()) == null,
            "loaded terminal lookup rejects missing asset qualifiers");
        UnityEngine.Object.Loaded = new SceneTransitionInteractable[2048];
        UnityEngine.Object.Loaded[2047] = near;
        Check(Find(lowtownRoad) == near, "maximum bounded terminal list includes its final entry");
        UnityEngine.Object.Loaded = new SceneTransitionInteractable[2049];
        UnityEngine.Object.Loaded[0] = near;
        Reject(() => Find(lowtownRoad), "oversized terminal list is rejected before selecting a match");
        TravelUnlockManager.Instance._allPortalKeys = registry;
        lowtown.DefaultLocation = lowtownRoad;
        TravelReplication.MapForTest(lowtownRoad); Check(true, "canonical public map default accepted without terminal proximity");
        Reject(() => TravelReplication.MapForTest(lowtownTrain), "map shortcut cannot target same-GUID train arrival instead of canonical default");
        Reject(() => TravelReplication.MapForTest(devKey), "map mode cannot target developer room");
        lowtownRoad.Apartment = new object(); Reject(() => TravelReplication.MapForTest(lowtownRoad), "map mode cannot bypass private apartment access"); lowtownRoad.Apartment = null;
        MultiplayerSandboxRuntime.Public = false; Reject(() => TravelReplication.MapForTest(lowtownRoad), "map mode requires isolated public destination policy"); MultiplayerSandboxRuntime.Public = true;
        TravelUnlockManager.Instance.Unlocked = false; Reject(() => TravelReplication.MapForTest(lowtownRoad), "map default still requires native registered location access"); TravelUnlockManager.Instance.Unlocked = true;
        lowtown.DefaultLocation = null; Reject(() => TravelReplication.MapForTest(lowtownRoad), "map cannot fabricate missing public arrival"); lowtown.DefaultLocation = lowtownRoad;
        var water = new BoatPortal { keyTo = lowtownRoad, isActiveAndEnabled = true,
            gameObject = new GameObject { scene = new Scene { name = DedicatedWorldRuntime.Scene, isLoaded = true } },
            transform = new Transform { position = new Vector3(20,0,0) } };
        UnityEngine.Object.LoadedBoats = new[] { water };
        BoatPortal Water() => TravelReplication.BoatForTest(lowtownRoad.Guid, lowtown.SceneName, lowtownRoad.name, lowtown.Guid, new Vector3());
        Check(Water() == water, "wide waterway candidate is not incorrectly limited by pedestrian terminal radius; authority validates trigger");
        water.isActiveAndEnabled = false; Check(Water() == null, "disabled waterway unavailable"); water.isActiveAndEnabled = true;
        water.gameObject.scene.isLoaded = false; Check(Water() == null, "unloaded waterway unavailable"); water.gameObject.scene.isLoaded = true;
        water.gameObject.scene.name = lowtown.SceneName; Check(Water() == null, "waterway must belong to worker's focused source map"); water.gameObject.scene.name = DedicatedWorldRuntime.Scene;
        water.keyTo = lowtownTrain; Check(Water() == null, "waterway retains full native route identity"); water.keyTo = lowtownRoad;
        water.transform.position = new Vector3(float.NaN,0,0); Check(Water() == null, "invalid waterway position rejected");
        return assertions;
    }
}

namespace Nivalis
{
    public sealed class WorldLocation { public string Guid; public string SceneName; public int SceneIndex; public PortalKey DefaultLocation; }
    public sealed class PortalKey
    {
        public long Pointer; public string Guid; public string name; public object Apartment; public WorldLocation TargetLocation;
        public string SceneName => TargetLocation?.SceneName; public int SceneIndex => TargetLocation?.SceneIndex ?? -1;
    }
    public sealed class TravelUnlockManager
    {
        public static TravelUnlockManager Instance = new TravelUnlockManager(); public PortalKey[] _allPortalKeys;
        public static bool InstanceNotNull() => Instance != null;
        public bool Unlocked = true; public bool IsLocationUnlocked(WorldLocation location) => Unlocked;
    }
    public sealed class SceneTransitionInteractable
    {
        public PortalKey keyTo; public bool isCabTravel; public bool isActiveAndEnabled;
        public bool IsActive = true; public bool Interactable = true; public bool CanInteract() => Interactable;
        public GameObject gameObject; public Transform transform;
    }
    public sealed class BoatPortal { public PortalKey keyTo; public bool isActiveAndEnabled; public GameObject gameObject; public Transform transform; }
}
namespace UnityEngine
{
    public static class Object
    {
        public static Nivalis.SceneTransitionInteractable[] Loaded = Array.Empty<Nivalis.SceneTransitionInteractable>();
        public static Nivalis.BoatPortal[] LoadedBoats = Array.Empty<Nivalis.BoatPortal>();
        public static T[] FindObjectsOfType<T>() => typeof(T) == typeof(Nivalis.BoatPortal) ? (T[])(object)LoadedBoats : (T[])(object)Loaded;
    }
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public float sqrMagnitude => x * x + y * y + z * z;
    }
    public struct Scene { public string name; public bool isLoaded; }
    public sealed class GameObject { public Scene scene; }
    public sealed class Transform { public Vector3 position; }
}
namespace NivalisTogether.Dedicated
{
    public static class DedicatedWorldRuntime { public static string Scene; }
    public static class MultiplayerSandboxRuntime { public static bool Public = true; public static bool IsPublicTravelDestination(Nivalis.PortalKey key) => Public; }
}
