using System;
using Nivalis;
using NivalisTogether.Dedicated;

// ResolvePortal and its predicate are extracted from production by the runner.
// These facades substitute engine assets only; selection and validation are real.
public static class WorkerPortalResolutionTests
{
    private static int assertions;
    private static void Check(bool condition, string message)
    { assertions++; if (!condition) throw new Exception(message); }
    private static void Reject(Action action, string message)
    { bool rejected = false; try { action(); } catch (InvalidOperationException) { rejected = true; } Check(rejected, message); }
    public static int Run()
    {
        var lowtown = new WorldLocation { Pointer = 1, SceneName = "1_Lowtown", SceneIndex = 1 };
        var meridian = new WorldLocation { Pointer = 2, SceneName = "2_Meridian_Market", SceneIndex = 2 };
        var apartment = new PortalKey { Pointer = 3, Guid = "bc22acae-4d06-4ce9-9ea1-59ef0cff49a3", TargetLocation = lowtown, Apartment = new object() };
        var publicLowtown = new PortalKey { Pointer = 4, Guid = "89c5c346-ed08-4697-b181-ea39d54fafe1", TargetLocation = lowtown, name = "Lowtown" };
        var alternate = new PortalKey { Pointer = 5, Guid = "cccccccc-1111-1111-1111-111111111111", TargetLocation = lowtown };
        var foreign = new PortalKey { Pointer = 6, Guid = "00000000-1111-1111-1111-111111111111", TargetLocation = meridian };
        lowtown.DefaultLocation = apartment; // Native sharedassets0.assets Lowtown fixture.
        GameSceneManager.Instance.worldLocations = new[] { meridian, lowtown };
        var location = new PlayerMapLocation { Scene = lowtown.SceneName, PortalId = "" };
        TravelUnlockManager.Instance._allPortalKeys = new[] { alternate, apartment, foreign, publicLowtown };
        Check(WorkerSceneResidency.ResolveForTest(location) == publicLowtown, "Lowtown apartment default selects public arrival");
        Check(location.PortalId == "", "fallback does not rewrite persisted context");
        TravelUnlockManager.Instance._allPortalKeys = new[] { publicLowtown, foreign, apartment, alternate };
        Check(WorkerSceneResidency.ResolveForTest(location) == publicLowtown, "fallback is independent of registry order");
        TravelUnlockManager.Instance._allPortalKeys = new[] { publicLowtown, publicLowtown, alternate };
        Check(WorkerSceneResidency.ResolveForTest(location) == publicLowtown, "repeated native portal asset is one fallback route");
        location.PortalId = publicLowtown.Guid;
        Check(WorkerSceneResidency.ResolveForTest(location) == publicLowtown, "repeated explicit portal asset is accepted");
        var repeatedWrapper = new PortalKey { Pointer = publicLowtown.Pointer, Guid = publicLowtown.Guid, TargetLocation = lowtown, name = publicLowtown.name };
        TravelUnlockManager.Instance._allPortalKeys = new[] { publicLowtown, repeatedWrapper, alternate };
        Check(WorkerSceneResidency.ResolveForTest(location).Pointer == publicLowtown.Pointer, "distinct managed wrappers for one native portal are accepted");
        location.PortalId = "";
        GameSceneManager.Instance.worldLocations = new[] { lowtown, lowtown, new WorldLocation { Pointer = lowtown.Pointer, SceneName = lowtown.SceneName, SceneIndex = lowtown.SceneIndex, DefaultLocation = lowtown.DefaultLocation } };
        Check(WorkerSceneResidency.ResolveForTest(location).Pointer == publicLowtown.Pointer, "repeated native district asset and wrappers are accepted");
        GameSceneManager.Instance.worldLocations = new[] { meridian, lowtown };
        TravelUnlockManager.Instance._allPortalKeys = new[] { publicLowtown, foreign, apartment, alternate };
        lowtown.DefaultLocation = alternate;
        Check(WorkerSceneResidency.ResolveForTest(location) == alternate, "canonical registered public default is preferred");
        lowtown.DefaultLocation = foreign;
        Check(WorkerSceneResidency.ResolveForTest(location) == publicLowtown, "foreign default cannot grant another district");
        lowtown.DefaultLocation = new PortalKey { Pointer = 7, Guid = "00000000-2222-2222-2222-222222222222", TargetLocation = lowtown };
        Check(WorkerSceneResidency.ResolveForTest(location) == publicLowtown, "unregistered default cannot become native route");
        location.PortalId = alternate.Guid;
        Check(WorkerSceneResidency.ResolveForTest(location) == alternate, "explicit valid arrival is preserved");
        location.PortalId = apartment.Guid;
        Reject(() => WorkerSceneResidency.ResolveForTest(location), "explicit apartment cannot silently fall back");
        location.PortalId = foreign.Guid;
        Reject(() => WorkerSceneResidency.ResolveForTest(location), "explicit foreign portal cannot silently fall back");
        location.PortalId = "missing";
        Reject(() => WorkerSceneResidency.ResolveForTest(location), "missing explicit portal cannot silently fall back");
        location.PortalId = "";
        TravelUnlockManager.Instance._allPortalKeys = new[] { apartment, foreign };
        Reject(() => WorkerSceneResidency.ResolveForTest(location), "district with no public arrival rejects");
        var empty = new PortalKey { Pointer = 8, Guid = "", TargetLocation = lowtown };
        TravelUnlockManager.Instance._allPortalKeys = new[] { empty };
        Reject(() => WorkerSceneResidency.ResolveForTest(location), "empty native GUID cannot authorize fallback");
        TravelUnlockManager.Instance._allPortalKeys = new[] { publicLowtown, new PortalKey { Pointer = 9, Guid = publicLowtown.Guid, TargetLocation = lowtown, name = publicLowtown.name } };
        Reject(() => WorkerSceneResidency.ResolveForTest(location), "same GUID and asset name with different pointers remain ambiguous");
        var clone = new WorldLocation { Pointer = 10, SceneName = lowtown.SceneName, SceneIndex = lowtown.SceneIndex };
        TravelUnlockManager.Instance._allPortalKeys = new[] { new PortalKey { Pointer = 11, Guid = publicLowtown.Guid, TargetLocation = clone } };
        Reject(() => WorkerSceneResidency.ResolveForTest(location), "same-named foreign location pointer is not canonical");
        TravelUnlockManager.Instance._allPortalKeys = new[] { publicLowtown };
        TravelUnlockManager.Instance.Unlocked = false;
        Reject(() => WorkerSceneResidency.ResolveForTest(location), "fallback does not bypass district unlock");
        TravelUnlockManager.Instance.Unlocked = true;
        GameSceneManager.Instance.Gameplay = false;
        Reject(() => WorkerSceneResidency.ResolveForTest(location), "fallback does not bypass gameplay eligibility");
        GameSceneManager.Instance.Gameplay = true;
        lowtown.SceneIndex = -1;
        Reject(() => WorkerSceneResidency.ResolveForTest(location), "unbuilt scene remains rejected");
        lowtown.SceneIndex = 1;
        GameSceneManager.Instance.worldLocations = new[] { lowtown, clone };
        Reject(() => WorkerSceneResidency.ResolveForTest(location), "ambiguous district registry remains rejected");

        // Actual native authored GUID collisions; asset identity remains scoped to
        // the committed district, and aliases select consistently in either order.
        CheckAliases(lowtown, publicLowtown, "LowtownTrain");
        CheckAliases(meridian, new PortalKey { Pointer = 20, Guid = "10ca9a93-555f-4b17-bc2a-cea368d60ae0", TargetLocation = meridian, name = "Meridian Market - Train station" }, "Meridian Market - Train station Eastern");
        var mountain = new WorldLocation { Pointer = 30, SceneName = "16_Mountain_Town", SceneIndex = 16 };
        CheckAliases(mountain, new PortalKey { Pointer = 31, Guid = "bd3a32bb-152d-4a63-a39f-facbe7e326c6", TargetLocation = mountain, name = "Mountain Town" }, "Mountain Town - Train");
        var seaside = new WorldLocation { Pointer = 40, SceneName = "9_Seaside_Boardwalk", SceneIndex = 9 };
        var devRoom = new WorldLocation { Pointer = 41, SceneName = "0_Dev_Room", SceneIndex = -1 };
        var seasideKey = new PortalKey { Pointer = 42, Guid = "3705cac6-9d52-4bbe-9b10-b964a1349ad4", TargetLocation = seaside, name = "Seaside Boardwalk" };
        var devKey = new PortalKey { Pointer = 43, Guid = seasideKey.Guid, TargetLocation = devRoom, name = "Dev Room" };
        GameSceneManager.Instance.worldLocations = new[] { seaside, devRoom };
        location = new PlayerMapLocation { Scene = seaside.SceneName, PortalId = seasideKey.Guid };
        TravelUnlockManager.Instance._allPortalKeys = new[] { devKey, seasideKey };
        Check(WorkerSceneResidency.ResolveForTest(location) == seasideKey, "Dev Room GUID collision cannot deny approved Seaside arrival");
        TravelUnlockManager.Instance._allPortalKeys = new[] { seasideKey, devKey };
        Check(WorkerSceneResidency.ResolveForTest(location) == seasideKey, "cross-map collision resolution is independent of order");
        TravelUnlockManager.Instance._allPortalKeys = new[] { devKey };
        Reject(() => WorkerSceneResidency.ResolveForTest(location), "foreign-only GUID never substitutes another district");
        TravelUnlockManager.Instance._allPortalKeys = new[] { new PortalKey { Pointer = 44, Guid = seasideKey.Guid, TargetLocation = seaside, name = "" } };
        Reject(() => WorkerSceneResidency.ResolveForTest(location), "missing native asset name rejects nondeterministic identity");
        return assertions;
    }
    private static void CheckAliases(WorldLocation target, PortalKey primary, string aliasName)
    {
        target.DefaultLocation = primary;
        GameSceneManager.Instance.worldLocations = new[] { target };
        var alias = new PortalKey { Pointer = primary.Pointer + 1000, Guid = primary.Guid, TargetLocation = target, name = aliasName };
        var context = new PlayerMapLocation { Scene = target.SceneName, PortalId = primary.Guid };
        TravelUnlockManager.Instance._allPortalKeys = new[] { alias, primary };
        Check(WorkerSceneResidency.ResolveForTest(context) == primary, "same-map public variants resolve by stable native name: " + primary.name);
        TravelUnlockManager.Instance._allPortalKeys = new[] { primary, alias };
        Check(WorkerSceneResidency.ResolveForTest(context) == primary, "same-map variant selection is independent of registry order: " + primary.name);
        context.PortalId = "";
        Check(WorkerSceneResidency.ResolveForTest(context) == primary, "empty-portal restore accepts authored variants: " + primary.name);
        var privateAlias = new PortalKey { Pointer = primary.Pointer + 2000, Guid = primary.Guid, TargetLocation = target, name = "A private portal", Apartment = new object() };
        TravelUnlockManager.Instance._allPortalKeys = new[] { privateAlias, primary };
        Check(WorkerSceneResidency.ResolveForTest(context) == primary, "apartment alias cannot change property membership: " + primary.name);
    }
}
namespace Nivalis
{
    public sealed class WorldLocation { public long Pointer; public string SceneName; public int SceneIndex; public PortalKey DefaultLocation; }
    public sealed class PortalKey
    {
        public long Pointer; public string Guid; public string name = "Fixture portal"; public object Apartment; public WorldLocation TargetLocation;
        public string SceneName => TargetLocation?.SceneName; public int SceneIndex => TargetLocation?.SceneIndex ?? -1;
    }
    public sealed class GameSceneManager
    {
        public static GameSceneManager Instance = new GameSceneManager(); public WorldLocation[] worldLocations;
        public bool Gameplay = true; public static bool InstanceNotNull() => Instance != null; public bool IsGameplayScene(int index) => Gameplay;
    }
    public sealed class TravelUnlockManager
    {
        public static TravelUnlockManager Instance = new TravelUnlockManager(); public PortalKey[] _allPortalKeys;
        public bool Unlocked = true; public static bool InstanceNotNull() => Instance != null; public bool IsLocationUnlocked(WorldLocation location) => Unlocked;
    }
}
namespace NivalisTogether.Dedicated { public sealed class PlayerMapLocation { public string Scene; public string PortalId; } }
