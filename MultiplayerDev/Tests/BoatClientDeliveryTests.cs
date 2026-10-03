using System;
using System.Collections.Generic;
using NivalisTogether.Dedicated;

public static class BoatClientDeliveryTests
{
    private static int checks;
    private static string owner = new string('a', 32), boat = new string('b', 32), player = new string('c', 32);
    private static void Check(bool result, string message) { checks++; if (!result) throw new Exception(message); }
    private static PlayerBoatState State(long revision = 1) => new PlayerBoatState
    { OwnerAccountId = owner, BoatId = boat, PrefabId = "native-basic", Scene = "1_Lowtown", DockId = "dock-a", Fuel = 60, Revision = revision };
    private static BoatWorldState Event(long sequence, string operation = "snapshot", string request = "") => new BoatWorldState
    { Sequence = sequence, Operation = operation, RequestId = request, TargetPlayerId = player, BoatId = operation == "snapshot" ? "" : boat, Boats = new[] { State(sequence) } };
    public static void Main()
    {
        GhostCleanupLifecycle();
        // The live worker catalog reported every layer pair ignored, but its
        // PlayerNavMesh and cabin boxes were enabled, active, orthogonal and
        // attached to the correct root body. Those exact boxes remain usable.
        Check(BoatHullColliderPolicy.Eligible(true, true, true, true, true), "worker-disabled collision matrix cannot erase valid canonical native hull geometry");
        Check(!BoatHullColliderPolicy.Eligible(false, true, true, true, true), "disabled native hull collider remains excluded");
        Check(!BoatHullColliderPolicy.Eligible(true, false, true, true, true), "inactive authored hull branch remains excluded");
        Check(!BoatHullColliderPolicy.Eligible(true, true, false, true, true), "loose boat object with separate rigidbody cannot authorize passage");
        Check(!BoatHullColliderPolicy.Eligible(true, true, true, false, true), "unsupported native collider does not add approximate hull geometry");
        Check(!BoatHullColliderPolicy.Eligible(true, true, true, true, false), "sheared native hull remains excluded");
        var fuelApplication = new BoatFuelApplication(); float nativeFuel = 96;
        Check(fuelApplication.Revision == 0, "a fresh connection has no native fuel acknowledgement");
        fuelApplication.Apply(96, 10, value => nativeFuel = value);
        Check(nativeFuel == 96 && fuelApplication.Revision == 10, "restored native fuel stamps its authenticated saved revision");
        var paidRefill = State(11); paidRefill.Fuel = 100;
        Check(fuelApplication.Revision == 10 && nativeFuel == 96, "receiving a newer paid state cannot acknowledge fuel before native application");
        bool nativeWriteFailed = false;
        try { fuelApplication.Apply(paidRefill.Fuel, paidRefill.Revision, _ => throw new InvalidOperationException("native view unavailable")); }
        catch (InvalidOperationException) { nativeWriteFailed = true; }
        Check(nativeWriteFailed && fuelApplication.Revision == 10, "failed native refuel cannot release the server's stale-fuel fence");
        fuelApplication.Apply(paidRefill.Fuel, paidRefill.Revision, value => nativeFuel = value);
        Check(nativeFuel == 100 && fuelApplication.Revision == 11, "successful native refill advances its movement acknowledgement");
        nativeFuel = 99;
        fuelApplication.Apply(96, 10, value => nativeFuel = value);
        Check(nativeFuel == 99 && fuelApplication.Revision == 11, "late old fuel operation cannot roll back a later native refill");
        Check(BoatBoxTransformPolicy.Orthogonal(0, 0, -.3f, 0, .3f, 0, .3f, 0, 0), "primary native PlayerNavMesh box's rotated uniform basis remains eligible");
        Check(BoatBoxTransformPolicy.Orthogonal(-3, 0, 0, 0, 2, 0, 0, 0, 1), "orthogonal nonuniform or mirrored native scale remains eligible");
        Check(!BoatBoxTransformPolicy.Orthogonal(1, 0, 0, .3f, 1, 0, 0, 0, 1), "sheared native box cannot become a false-positive OBB");
        Check(!BoatBoxTransformPolicy.Orthogonal(0, 0, 0, 0, 1, 0, 0, 0, 1), "collapsed box transform is unsupported");
        Check(!BoatBoxTransformPolicy.Orthogonal(float.NaN, 0, 0, 0, 1, 0, 0, 0, 1), "invalid box transform cannot authorize travel");
        var departure = new BoatPose { PlayerId = player, AccountId = owner, BoatId = boat, Scene = "3_Docks",
            ContextGeneration = 7, Sequence = 100, Boarded = true, Driving = true };
        bool Confirmed(BoatPose observed, BoatPose expected = null, bool connected = true, string scene = "3_Docks") =>
            BoatDeparturePoseGate.Accepted(connected, owner, boat, scene, expected ?? departure, observed);
        var accepted = BoatProtocol.Copy(departure);
        Check(Confirmed(accepted), "departure waits for exact authenticated held-pose echo");
        accepted.Sequence = 99; Check(!Confirmed(accepted), "older movement snapshot cannot release departure hold");
        accepted.Sequence = 101; Check(Confirmed(accepted), "later regular held pose recovers an immediate flush dropped by native rate gate");
        accepted.PlayerId = new string('d', 32); Check(!Confirmed(accepted), "another player cannot acknowledge departure"); accepted.PlayerId = player;
        accepted.AccountId = new string('e', 32); Check(!Confirmed(accepted), "another account cannot acknowledge departure"); accepted.AccountId = owner;
        accepted.BoatId = new string('f', 32); Check(!Confirmed(accepted), "another boat cannot acknowledge departure"); accepted.BoatId = boat;
        accepted.ContextGeneration = 8; Check(!Confirmed(accepted), "changed map membership cannot acknowledge old departure"); accepted.ContextGeneration = 7;
        accepted.Scene = "16_Mountain_Town"; Check(!Confirmed(accepted), "another scene snapshot cannot acknowledge departure"); accepted.Scene = "3_Docks";
        Check(!Confirmed(accepted, scene: "16_Mountain_Town"), "changed local map cannot release a stale departure");
        accepted.Driving = false; Check(!Confirmed(accepted), "disembark or cockpit exit cannot authorize boat departure"); accepted.Driving = true;
        accepted.Boarded = false; Check(!Confirmed(accepted), "unboarded echo cannot authorize departure"); accepted.Boarded = true;
        Check(!Confirmed(accepted, connected: false), "disconnect makes queued departure acknowledgement unusable");
        Check(!BoatDeparturePoseGate.Accepted(true, owner, boat, "3_Docks", null, accepted), "cleared timeout or cancellation cannot be revived by a late echo");
        Check(!Confirmed(null), "missing authority pose keeps boat held and request unsent");
        var boatDeck = new IntPtr(1234); var terrain = new IntPtr(5678);
        Check(!BoatRestoredAttachmentPolicy.Complete(true, false, false, false, true, true, true, true), "occupied restored cockpit alone cannot acknowledge missing native deck attachment");
        Check(BoatRestoredAttachmentPolicy.Complete(true, true, true, false, true, true, true, true), "native driving attachment is complete without requiring cockpit player to remain on walking navmesh");
        Check(!BoatRestoredAttachmentPolicy.Complete(true, true, false, true, true, true, true, true), "driver cannot acknowledge another or missing boarded controller");
        Check(!BoatRestoredAttachmentPolicy.Complete(true, true, true, true, false, true, true, true), "saved driver waits for occupied native cockpit");
        Check(!BoatRestoredAttachmentPolicy.Complete(true, true, true, true, true, false, true, true), "another cockpit occupant cannot satisfy private restoration");
        Check(!BoatRestoredAttachmentPolicy.Complete(true, true, true, true, true, true, false, true), "missing native driven flag cannot acknowledge driving restore");
        Check(!BoatRestoredAttachmentPolicy.Complete(true, true, true, true, true, true, true, false), "foreign driven boat cannot acknowledge driving restore");
        Check(BoatRestoredAttachmentPolicy.Complete(false, true, true, true, false, false, false, false), "standing saved player restores exact native walking deck attachment");
        Check(!BoatRestoredAttachmentPolicy.Complete(false, true, true, false, false, false, false, false), "standing boarded flags alone cannot replace native deck navmesh attachment");
        Check(!BoatRestoredAttachmentPolicy.Complete(false, true, true, true, true, true, true, true), "standing restore cannot silently promote player to driver");
        Check(BoatNavMeshAttachmentPolicy.Prevent(true, false, false, boatDeck, boatDeck, false), "legacy boat cannot constrain client navmesh during bootstrap");
        Check(BoatNavMeshAttachmentPolicy.Prevent(true, false, true, boatDeck, boatDeck, false), "foreign boat cannot set native attachment before later deck ownership rejection");
        Check(BoatNavMeshAttachmentPolicy.Prevent(true, false, false, boatDeck, boatDeck, true), "recognized own deck waits for authenticated map placement");
        Check(!BoatNavMeshAttachmentPolicy.Prevent(true, false, true, boatDeck, boatDeck, true), "recognized own deck permits normal boarding after placement");
        Check(BoatNavMeshAttachmentPolicy.Prevent(true, true, true, boatDeck, boatDeck, true), "worker always rejects recognized boat navmesh entry");
        Check(!BoatNavMeshAttachmentPolicy.Prevent(true, false, false, terrain, boatDeck, false), "unrelated terrain navmesh is not rejected as a foreign boat");
        Check(!BoatNavMeshAttachmentPolicy.Prevent(false, false, false, boatDeck, boatDeck, false), "single-player foreign and legacy deck behavior stays native");
        Check(BoatBootstrapAttachmentPolicy.Prevent(true, false, false, boatDeck, boatDeck), "reconnect destination overlap cannot attach own deck before map placement");
        Check(!BoatBootstrapAttachmentPolicy.Prevent(true, false, true, boatDeck, boatDeck), "completed map placement permits authenticated boat attachment and ordinary later boarding");
        Check(BoatBootstrapAttachmentPolicy.Prevent(true, false, false, boatDeck, boatDeck), "next reconnect re-arms own deck bootstrap fence");
        Check(!BoatBootstrapAttachmentPolicy.Prevent(false, false, false, boatDeck, boatDeck), "single-player boat attachment remains native");
        Check(!BoatBootstrapAttachmentPolicy.Prevent(true, true, false, boatDeck, boatDeck), "client bootstrap policy does not replace worker attachment fence");
        Check(!BoatBootstrapAttachmentPolicy.Prevent(true, false, false, terrain, boatDeck), "terrain or unrelated navmesh remains native during bootstrap");
        Check(!BoatBootstrapAttachmentPolicy.Prevent(true, false, false, boatDeck, IntPtr.Zero), "unavailable own boat cannot classify a native attachment by proximity");
        Check(!BoatBootstrapAttachmentPolicy.Prevent(true, false, false, IntPtr.Zero, IntPtr.Zero), "missing attachment callback cannot match an absent boat");
        Check(BoatWorkerAttachmentPolicy.Prevent(true, true, boatDeck, boatDeck), "worker boat navmesh entry blocked before hidden deck attachment");
        Check(!BoatWorkerAttachmentPolicy.Prevent(true, false, boatDeck, boatDeck), "client boat entry and reconnect attachment remain native");
        Check(!BoatWorkerAttachmentPolicy.Prevent(false, true, boatDeck, boatDeck), "unconfigured single-player navmesh remains native");
        Check(!BoatWorkerAttachmentPolicy.Prevent(true, true, terrain, boatDeck), "worker terrain navmesh remains native");
        Check(!BoatWorkerAttachmentPolicy.Prevent(true, true, boatDeck, IntPtr.Zero), "unmatched navmesh cannot be classified as a boat by proximity");
        Check(!BoatWorkerAttachmentPolicy.Prevent(true, true, IntPtr.Zero, IntPtr.Zero), "missing navmesh cannot trigger worker detachment");
        var missingNativeEvent = new BoatEntryAttempt(true, false, 0, true, false, 10);
        Check(missingNativeEvent.NeedsUndock(true, true, true, 10), "normal own cockpit entry recovers a missing native undock callback");
        Check(!missingNativeEvent.NeedsUndock(true, true, true, 11), "native callback already sent one undock so postfix must not duplicate it");
        Check(!missingNativeEvent.NeedsUndock(false, true, true, 10), "failed cockpit entry cannot undock the boat");
        Check(!missingNativeEvent.NeedsUndock(true, false, true, 10), "entry into an already free boat cannot emit undock");
        Check(!missingNativeEvent.NeedsUndock(true, true, false, 10), "entry cannot release another owner's berth");
        Check(!new BoatEntryAttempt(false, false, 0, true, false, 10).NeedsUndock(true, true, true, 10), "single-player entry is untouched");
        Check(!new BoatEntryAttempt(true, true, 0, true, false, 10).NeedsUndock(true, true, true, 10), "worker helper cannot emit client undock");
        Check(!new BoatEntryAttempt(true, false, 1, true, false, 10).NeedsUndock(true, true, true, 10), "reconnect attachment restoration cannot emit a new undock operation");
        Check(!new BoatEntryAttempt(true, false, 0, false, false, 10).NeedsUndock(true, true, true, 10), "foreign boat entry cannot emit an operation");
        Check(!new BoatEntryAttempt(true, false, 0, true, true, 10).NeedsUndock(true, true, true, 10), "repeated Enter while occupied cannot retry or duplicate an operation");
        var laterEntry = new BoatEntryAttempt(true, false, 0, true, false, 11);
        Check(laterEntry.NeedsUndock(true, true, true, 11), "a later fresh entry after exit may retry a denied or missing undock");
        var ledger = new BoatClientEventLedger();
        Check(ledger.Observe(Event(10), owner, boat).ReplaceRoster, "initial roster accepted");
        var delayed = Event(5, "recall", new string('d', 32));
        var delivery = ledger.Observe(delayed, owner, boat);
        Check(!delivery.ApplyRoster && !delivery.ReplaceRoster, "delayed operation cannot rewind current roster");
        Check(delivery.Operation?.Operation == "recall" && delivery.OperationState.DockId == "dock-a", "delayed committed recall still delivered");
        Check(ledger.Observe(delayed, owner, boat).Operation == null, "delayed duplicate does not recall twice");
        delayed.Boats[0].DockId = "changed-after-delivery";
        Check(delivery.OperationState.DockId == "dock-a", "queued native action retains exact approved dock");
        Check(delivery.Operation.Boats[0].DockId == "dock-a", "queued action owns its wire payload");
        var newerPrivate = ledger.Observe(Event(11, "dock", new string('e', 32)), owner, boat);
        Check(newerPrivate.ApplyRoster && !newerPrivate.ReplaceRoster, "one private operation preserves all other boats");
        Check(newerPrivate.OperationState.Revision == 11, "native dock uses operation-specific state");
        Check(!ledger.Observe(Event(9), owner, boat).ApplyRoster, "older shared snapshot ignored");
        Check(ledger.Observe(Event(12), owner, boat).ReplaceRoster, "new shared snapshot can remove absent boats");
        var denied = Event(3, "refuel", new string('f', 32)); denied.Success = false; denied.Boats = Array.Empty<PlayerBoatState>();
        Check(ledger.Observe(denied, owner, boat).Operation?.Success == false, "delayed denial delivered without owning state");
        Check(ledger.Observe(denied, owner, boat).Operation == null, "denial repeats do not spam");
        var wrongOwner = Event(20, "dock", new string('1', 32)); wrongOwner.Boats[0].OwnerAccountId = new string('2', 32);
        bool rejected = false; try { ledger.Observe(wrongOwner, owner, boat); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "approved native action cannot change boat owner");
        var missing = Event(21, "recall", new string('3', 32)); missing.Boats = Array.Empty<PlayerBoatState>();
        rejected = false; try { ledger.Observe(missing, owner, boat); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "successful action must include its exact boat state");
        var other = Event(22, "recall", new string('4', 32)); other.BoatId = new string('5', 32);
        Check(ledger.Observe(other, owner, boat).Operation == null, "another boat's action cannot execute locally");
        var movement = new Dictionary<string, BoatPose>(StringComparer.Ordinal);
        var moving = Event(30);
        moving.RuntimePoses = new[] { new BoatPose { PlayerId = player, AccountId = owner, BoatId = boat, Scene = "1_Lowtown", ContextGeneration = 1, Sequence = 1, X = 25, Fuel = 60 } };
        BoatClientEventLedger.UpdateRuntimePoses(movement, moving, ledger.Observe(moving, owner, boat));
        Check(movement[boat].X == 25, "live remote motion retained");
        var recalled = Event(31); recalled.Boats[0].X = 100; recalled.Boats[0].DockId = "another-dock";
        BoatClientEventLedger.UpdateRuntimePoses(movement, recalled, ledger.Observe(recalled, owner, boat));
        Check(!movement.ContainsKey(boat), "same-map recall clears old transient pose so stationary placement is visible");
        moving.Sequence = 32; moving.RuntimePoses[0].X = 104;
        BoatClientEventLedger.UpdateRuntimePoses(movement, moving, ledger.Observe(moving, owner, boat));
        Check(movement[boat].X == 104, "motion resumes after new docking placement");
        var absent = Event(33); absent.Boats = Array.Empty<PlayerBoatState>();
        BoatClientEventLedger.UpdateRuntimePoses(movement, absent, ledger.Observe(absent, owner, boat));
        Check(movement.Count == 0, "departed boats cannot retain transient positions");
        moving.Sequence = 34;
        BoatClientEventLedger.UpdateRuntimePoses(movement, moving, ledger.Observe(moving, owner, boat));
        var dockReply = Event(35, "dock", new string('6', 32));
        BoatClientEventLedger.UpdateRuntimePoses(movement, dockReply, ledger.Observe(dockReply, owner, boat));
        Check(!movement.ContainsKey(boat), "private approved docking invalidates old movement too");
        moving.Sequence = 36;
        BoatClientEventLedger.UpdateRuntimePoses(movement, moving, ledger.Observe(moving, owner, boat));
        BoatClientEventLedger.UpdateRuntimePoses(movement, recalled, ledger.Observe(recalled, owner, boat));
        Check(movement.ContainsKey(boat), "late old roster cannot erase newer live movement");
        for (int i = 0; i < 100; i++)
        {
            var action = Event(i + 100, "undock", Guid.NewGuid().ToString("N"));
            ledger.Observe(Event(i + 1000), owner, boat);
            Check(ledger.Observe(action, owner, boat).Operation != null, "checkpoint-delayed action remains live " + i);
            Check(ledger.Observe(action, owner, boat).Operation == null, "checkpoint-delayed action exactly once " + i);
        }
        Console.WriteLine("PASS: " + checks + " actual boat client delivery checks.");
    }
    private static void GhostCleanupLifecycle()
    {
        var index = new BoatGhostCleanupIndex();
        var manager = new IntPtr(101); var registryIdentity = new IntPtr(201); string scene = "3_Docks";
        var native = new Dictionary<string, (IntPtr Pointer, bool Boat)>(StringComparer.Ordinal);
        var removed = new List<string>(); int enumerations = 0;
        void Add(string id, int pointer, bool isBoat, bool registered = false)
        { native[id] = (new IntPtr(pointer), isBoat); if (registered) index.Observe(manager, id, new IntPtr(pointer), isBoat); }
        void Tick()
        {
            if (index.Begin(manager, registryIdentity, scene))
            { enumerations++; foreach (var pair in native) index.Observe(manager, pair.Key, pair.Value.Pointer, pair.Value.Boat); }
            index.Cleanup("own", id => native.TryGetValue(id, out var value) ? value.Pointer : IntPtr.Zero,
                (id, pointer) => { Check(native[id].Pointer == pointer && native[id].Boat, "cleanup only removes the exact registered native boat"); native.Remove(id); removed.Add(id); });
        }
        Add("own", 1, true); Add("legacy", 2, true); Add("npc", 3, false); Tick();
        Check(removed.Count == 1 && removed[0] == "legacy" && native.ContainsKey("own") && native.ContainsKey("npc"), "bootstrap removes legacy boat and preserves owned boat and non-boats");
        for (int i = 0; i < 100; i++) Tick();
        Check(enumerations == 1 && index.Count == 0, "stable world ticks never repeat the global ghost scan");
        Add("late-foreign", 4, true, true); Tick();
        Check(!native.ContainsKey("late-foreign") && enumerations == 1, "registration after bootstrap removes a foreign boat without global enumeration");
        Add("late-npc", 5, false, true); Tick();
        Check(native.ContainsKey("late-npc") && enumerations == 1, "non-boat registration does not enter the cleanup queue");
        Add("own", 1, true, true); Tick();
        Check(native.ContainsKey("own"), "owned boat native re-registration during travel remains preserved");

        int beforeCount = native.Count; native.Remove("late-npc"); Add("same-count-foreign", 6, true, true); Tick();
        Check(native.Count == beforeCount - 1 && !native.ContainsKey("same-count-foreign") && enumerations == 1, "same-count native replacement is detected from registration, not count polling");
        Add("pooled", 7, true, true); index.Forget(manager, "pooled", new IntPtr(7)); native.Remove("pooled");
        Check(index.Count == 0, "deregistration forgets a candidate before its native wrapper is pooled");
        Add("pooled-new-id", 7, true, true); Tick();
        Check(!native.ContainsKey("pooled-new-id"), "reused native pointer under a new identity is independently observed");
        Add("replaced", 8, true, true); Add("replaced", 9, false); Tick();
        Check(native.ContainsKey("replaced") && !native["replaced"].Boat, "stale candidate cannot remove a different native object reusing its ID");
        Add("replaced", 10, true, true); index.Forget(manager, "replaced", new IntPtr(8)); Tick();
        Check(!native.ContainsKey("replaced"), "late old-pointer deregistration does not erase the new registered candidate");

        Add("scene-native", 11, true); scene = "16_Mountain_Town"; Tick();
        Check(!native.ContainsKey("scene-native") && enumerations == 2 && native.ContainsKey("own"), "scene switch performs one bootstrap and preserves the account boat");
        Add("registry-native", 12, true); registryIdentity = new IntPtr(202); Tick();
        Check(!native.ContainsKey("registry-native") && enumerations == 3, "same manager with a rebuilt registry triggers a fresh bootstrap");
        Add("restore-native", 13, true); index.Invalidate(manager); Tick();
        Check(!native.ContainsKey("restore-native") && enumerations == 4, "native world reinitialization invalidates same-pointer registry state");
        index.Invalidate(new IntPtr(999)); Tick();
        Check(enumerations == 4, "unrelated manager teardown cannot invalidate active-world tracking");
        Add("old-pending", 14, true, true); native.Clear(); Add("own", 15, true); Add("new-manager-native", 16, true);
        var oldManager = manager; manager = new IntPtr(102); Tick();
        Check(!native.ContainsKey("new-manager-native") && native.ContainsKey("own") && enumerations == 5, "new manager clears stale pending identities and bootstraps its own registry");
        index.Observe(oldManager, "foreign-from-old-manager", new IntPtr(17), true);
        Check(index.Count == 0, "callbacks from the previous manager cannot contaminate the new world");
        Add("reconnect-native", 18, true); index.Clear(); Tick();
        Check(!native.ContainsKey("reconnect-native") && enumerations == 6, "disconnect/reset forces bootstrap even when native pointers and map are reused");

        Add("nested-first", 19, true, true);
        index.Cleanup("own", id => native[id].Pointer, (id, pointer) => { native.Remove(id); Add("nested-second", 20, true, true); });
        Check(index.Count == 1 && native.ContainsKey("nested-second"), "native removal callbacks retain newly registered candidates for the next tick");
        Tick(); Check(!native.ContainsKey("nested-second"), "registration during cleanup is processed without invalidating enumeration");
        Check(!index.Begin(IntPtr.Zero, registryIdentity, scene) && index.Count == 0, "missing native manager clears candidate state without requesting an invalid scan");
    }
}
