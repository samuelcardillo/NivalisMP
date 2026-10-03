using System;
using System.IO;
using System.Text.Json;
using NivalisTogether.Dedicated;

public static class PlayerSceneMembershipTests
{
    private static int checks;
    private static readonly string A = Guid.NewGuid().ToString("N"), B = Guid.NewGuid().ToString("N"), Epoch = Guid.NewGuid().ToString("N");
    private static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    private static void Reject(Action action, string message)
    { checks++; try { action(); } catch (InvalidDataException) { return; } throw new Exception(message); }
    private static PlayerSceneContext Context(string id = null, long generation = 1, string scene = "1_Lowtown", bool pending = false) =>
        new PlayerSceneContext { PlayerId = id ?? A, Generation = generation, Scene = scene, TravelPending = pending, Epoch = pending ? Epoch : "" };
    private static PlayerPose Pose(long generation = 1, string scene = "1_Lowtown", string id = null) =>
        new PlayerPose { PlayerId = id ?? A, ContextGeneration = generation, Scene = scene, Active = true };
    private static InteractionRequest Intent(long generation, string kind, string target = null) =>
        new InteractionRequest { PlayerId = A, ContextGeneration = generation, Kind = kind, TargetId = target ?? Epoch };
    public static int Main()
    {
        try { Run(); Console.WriteLine("PASS player scene membership " + checks + " assertions"); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void Run()
    {
        Check(DedicatedProtocol.Version == 14, "Server-confirmed ordered map catch-up requires protocol fourteen");
        var first = new PlayerSceneMembership(); var other = new PlayerSceneMembership();
        Check(!first.TryAcceptPose(Pose()), "Movement cannot initialize membership");
        Check(!first.AllowsInteraction(Intent(1, "shop-buy")), "Unassigned account cannot transact");
        var initial = Context(); Check(first.Observe(initial), "Worker initializes membership");
        initial.Scene = "tampered"; Check(first.Current.Scene == "1_Lowtown", "Assignment defensively copied");
        var returned = first.Current; returned.Generation = 99; Check(first.Current.Generation == 1, "Current returns defensive copy");
        Check(!first.Observe(Context()), "Identical assignment idempotent");
        Reject(() => first.Observe(Context(scene: "2_Midtown")), "Same generation cannot change scene");
        Reject(() => first.Observe(Context(id: B)), "Assignment cannot change player identity");
        Check(first.TryAcceptPose(Pose()), "Assigned scene movement accepted");
        Check(!first.TryAcceptPose(Pose(scene: "2_Midtown")), "Spoofed scene movement rejected");
        Check(!first.TryAcceptPose(Pose(generation: 0)), "Missing generation rejected");
        Check(!first.TryAcceptPose(Pose(generation: 2)), "Future generation cannot grant scene");
        Check(!first.TryAcceptPose(Pose(id: B)), "Other player's assignment unusable");
        var spoofedProperty = Pose(); spoofedProperty.PropertyId = "another-apartment";
        Check(!first.TryAcceptPose(spoofedProperty), "Property must match committed context after service stamping");
        var inactive = Pose(); inactive.Active = false; inactive.Scene = "";
        Check(first.TryAcceptPose(inactive), "Settled inactive loading pose permitted");
        inactive.Scene = "elsewhere"; Check(!first.TryAcceptPose(inactive), "Inactive scene spoof rejected");
        Check(first.AllowsInteraction(Intent(1, "shop-buy")), "Settled current-generation intent accepted for worker validation");
        Check(!first.AllowsInteraction(Intent(0, "shop-buy")), "Old unversioned intent rejected");
        other.Observe(Context(id: B, scene: "2_Midtown"));
        first.Observe(Context(generation: 2, pending: true));
        Check(!first.TryAcceptPose(Pose(generation: 2)), "Movement fenced during authorized travel");
        Check(!first.AllowsInteraction(Intent(2, "shop-buy")), "Transactions fenced during travel");
        Check(!first.AllowsInteraction(Intent(1, "group-travel-prepared")), "Pre-transition generation ack rejected");
        foreach (string kind in new[] { "group-travel-prepared", "group-travel-arrived", "group-apartment-prepared", "group-apartment-arrived", PlayerMapRestore.AcknowledgementKind })
        {
            Check(first.AllowsInteraction(Intent(2, kind)), "Current issued epoch ACK allowed: " + kind);
            Check(!first.AllowsInteraction(Intent(2, kind, Guid.NewGuid().ToString("N"))), "Foreign epoch ACK rejected: " + kind);
        }
        Check(other.TryAcceptPose(Pose(scene: "2_Midtown", id: B)), "Another player remains active during travel");
        first.Observe(Context(generation: 3, scene: "2_Midtown"));
        Check(!first.TryAcceptPose(Pose(generation: 1)), "Old source pose cannot revive old avatar");
        Check(!first.TryAcceptPose(Pose(generation: 3)), "New generation does not authorize old scene");
        Check(first.TryAcceptPose(Pose(generation: 3, scene: "2_Midtown")), "Arrival commits destination movement");
        Reject(() => first.Observe(Context(generation: 2, pending: true)), "Late pending assignment cannot revert committed scene");
        first.Observe(Context(generation: 4, scene: "2_Midtown", pending: true));
        first.Observe(Context(generation: 5, scene: "2_Midtown"));
        Check(first.TryAcceptPose(Pose(generation: 5, scene: "2_Midtown")), "Canceled route restores authorized source with new generation");
        first.Reset(); Check(!first.IsAssigned && !first.TryAcceptPose(Pose(generation: 5, scene: "2_Midtown")), "World reload revokes old authority");
        first.Observe(Context()); Check(first.TryAcceptPose(Pose()), "New session may begin generation one");
        foreach (var invalid in new[] { new PlayerSceneContext(), Context(generation: 0), Context(scene: "bad\nscene") })
            Reject(() => PlayerSceneContext.Validate(invalid), "Malformed context rejected");
        var badEpoch = Context(); badEpoch.Epoch = Epoch; Reject(() => PlayerSceneContext.Validate(badEpoch), "Settled context cannot keep a route epoch");
        badEpoch = Context(pending: true); badEpoch.Epoch = ""; Reject(() => PlayerSceneContext.Validate(badEpoch), "Pending context needs an epoch");
        TestPersistence(); TestRoutingControls();
    }
    private static void TestPersistence()
    {
        var old = JsonSerializer.Deserialize<AccountState>("{\"AccountId\":\"" + A + "\",\"Money\":100000,\"StateRevision\":7,\"AppearanceId\":\"original\",\"Inventory\":[],\"Initialized\":true}", DedicatedProtocol.Json);
        AccountProtocol.Validate(old); Check(old.Location == null && old.Money == 100000 && old.StateRevision == 7, "Old ledger location migrates without resetting assets");
        old.Location = new PlayerMapLocation { Scene = "2_Midtown", PropertyId = "owned-apartment", PortalId = "native-door", HasPosition = true, X = 12, Y = 1.5f, Z = -8, Yaw = 270 };
        var ledger = new AccountWorldState { WorldId = Guid.NewGuid().ToString("N"), Revision = 4, Accounts = new[] { old }, SpawnLocation = new PlayerMapLocation { Scene = "1_Lowtown", HasPosition = true, X = 2 } };
        var clone = JsonSerializer.Deserialize<AccountWorldState>(JsonSerializer.SerializeToUtf8Bytes(ledger, DedicatedProtocol.Json), DedicatedProtocol.Json);
        AccountProtocol.Validate(clone, ledger.WorldId, 4);
        Check(clone.Accounts[0].Location.Scene == "2_Midtown" && clone.Accounts[0].Location.PropertyId == "owned-apartment" && clone.Accounts[0].Location.X == 12, "Player location survives paired ledger serialization");
        Check(clone.SpawnLocation.Scene == "1_Lowtown" && clone.SpawnLocation.X == 2, "Spawn remains independent from account scene");
        Check(clone.Accounts[0].Money == 100000 && clone.Accounts[0].StateRevision == 7 && clone.Accounts[0].AppearanceId == "original", "Location serialization preserves assets and appearance");
        var copied = PlayerMapLocation.Copy(old.Location); copied.X = 90; Check(old.Location.X == 12, "Location copy isolates asynchronous work");
        foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, 1000001f })
        { var bad = PlayerMapLocation.Copy(old.Location); bad.X = invalid; Reject(() => PlayerMapLocation.Validate(bad), "Invalid stored coordinate refused"); }
        var noPosition = new PlayerMapLocation { Scene = "1_Lowtown" }; PlayerMapLocation.Validate(noPosition); Check(true, "Position may be absent pending canonical spawn");
        noPosition.Yaw = 361; Reject(() => PlayerMapLocation.Validate(noPosition), "Out-of-range yaw rejected even without position");
        var pose = Pose(); pose.PropertyId = new string('x', 129); Check(!DedicatedProtocol.PoseValid(pose), "Property identity bound enforced");
        clone.SpawnLocation.Scene = ""; Reject(() => AccountProtocol.Validate(clone, clone.WorldId, clone.Revision), "Invalid stable spawn fails ledger validation");
    }
    private static void TestRoutingControls()
    {
        var restore = new PlayerMapRestore { PlayerId = A, Epoch = Epoch, Location = new PlayerMapLocation { Scene = "2_Midtown" } };
        PlayerMapRestore.Validate(restore); Check(true, "Bounded worker-authored restore accepted");
        restore.Location.Scene = ""; Reject(() => PlayerMapRestore.Validate(restore), "Restore needs canonical nonempty scene");
        var scheduler = new WorldEventScheduler();
        var bulk = new WorldEvent { Kind = "npcs", Data = JsonSerializer.SerializeToElement(new { Count = 1 }) };
        var contextA = new WorldEvent { Kind = PlayerSceneContext.EventKind, TargetPlayerId = A, Data = JsonSerializer.SerializeToElement(Context()) };
        var contextB = new WorldEvent { Kind = PlayerSceneContext.EventKind, TargetPlayerId = B, Data = JsonSerializer.SerializeToElement(Context(id: B)) };
        Check(WorldEventFlow.IsControlKind(contextA.Kind) && WorldEventFlow.IsOrderingBarrier(contextA), "Scene context uses ordered control lane");
        Check(WorldEventFlow.IsControlKind(PlayerMapRestore.EventKind) && WorldEventFlow.IsOrderingBarrier(new WorldEvent { Kind = PlayerMapRestore.EventKind }), "Restore uses ordered control lane");
        Check(WorldEventFlow.IsControlKind("player-map-error") && WorldEventFlow.IsOrderingBarrier(new WorldEvent { Kind = "player-map-error" }), "Private map failure uses ordered control lane");
        scheduler.Enqueue(bulk); scheduler.Enqueue(contextA); scheduler.Enqueue(contextB);
        Check(scheduler.TryPeek(99999, 99999, out var next) && ReferenceEquals(next.Event, bulk), "Context cannot overtake earlier world batch"); scheduler.Remove(next);
        Check(scheduler.TryPeek(99999, 99999, out next) && next.Event.TargetPlayerId == A, "First private assignment retained"); scheduler.Remove(next);
        Check(scheduler.TryPeek(99999, 99999, out next) && next.Event.TargetPlayerId == B, "Second private assignment does not replace first"); scheduler.Remove(next);
        Check(scheduler.Count == 0, "Scene controls release ordered queue");
        var apartment = new GroupApartmentEvent { Sequence = 1, Epoch = Epoch, Phase = "prepare", PlayerId = A, ApartmentId = Guid.NewGuid().ToString("N"), Scene = "1_Lowtown", Participants = new[] { A } };
        ApartmentProtocol.Validate(apartment); Check(true, "Private apartment route accepted");
        apartment.Participants = new[] { A, B }; Reject(() => ApartmentProtocol.Validate(apartment), "Apartment transition cannot drag another player");
        apartment.Participants = new[] { A }; apartment.PlayerId = ""; Reject(() => ApartmentProtocol.Validate(apartment), "Broadcast apartment route rejected");
    }
}
