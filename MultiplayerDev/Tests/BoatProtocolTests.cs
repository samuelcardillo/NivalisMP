using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace NivalisTogether.Dedicated
{
    // Protocol and account persistence are production sources. The reused account
    // test fixture replaces native inventory/Harmony boundaries only.
    public static class BoatProtocolProbe
    {
        private static int checks;
        private static readonly string A = new string('a', 32), B = new string('b', 32),
            BoatA = new string('c', 32), BoatB = new string('d', 32), PlayerA = new string('e', 32), PlayerB = new string('f', 32);
        private static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
        private static void Throws(Action action, string message)
        { checks++; try { action(); } catch (Exception ex) when (ex is System.IO.InvalidDataException || ex is InvalidOperationException || ex is OverflowException) { return; } throw new Exception(message); }
        private static PlayerBoatState Boat(string owner = null, string id = null) => new PlayerBoatState
        { BoatId = id ?? BoatA, OwnerAccountId = owner ?? A, PrefabId = "basic-boat", Fuel = 100 };
        private static AccountState Account(string id) => new AccountState
        { AccountId = id, Initialized = true, Money = 4700, AppearanceId = "approved", StateRevision = 7,
            Inventory = JsonSerializer.SerializeToElement(new[] { new SharedItemStack { Type = "saved", LastUpdate = 8, Items = new[] { new[] { 0, 8, 5, 8 } } } }) };
        private static AccountWorldState Ledger() => new AccountWorldState
        { WorldId = new string('1', 32), Revision = 5, Accounts = new[] { Account(A), Account(B) }, LegacyEscrow = new AccountInventoryState { Money = 97000 } };
        private static BoatPose Pose() => new BoatPose
        { PlayerId = PlayerA, AccountId = A, BoatId = BoatA, Scene = "1_Lowtown", ContextGeneration = 2, Sequence = 1, Fuel = 99, Boarded = true, Driving = true };
        private static T RoundTrip<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, DedicatedProtocol.Json), DedicatedProtocol.Json);
        private static void RejectState(Action<PlayerBoatState> mutation, string message)
        { var boat = Boat(); mutation(boat); Throws(() => BoatProtocol.Validate(boat), message); }
        private static void RejectPose(Action<BoatPose> mutation, string message)
        { var pose = Pose(); mutation(pose); Throws(() => BoatProtocol.Validate(pose), message); }

        public static int Run()
        {
            checks = 0;
            // A pre-feature checkpoint has no Boat property; its account schema and
            // personal assets are preserved until authority explicitly grants a boat.
            var old = JsonSerializer.Deserialize<AccountState>("{\"AccountId\":\"" + A + "\",\"StateRevision\":7,\"Money\":4700,\"Inventory\":[],\"AppearanceId\":\"approved\",\"Initialized\":true}");
            AccountProtocol.Validate(old); Check(old.Boat == null && old.Money == 4700 && old.StateRevision == 7, "old account JSON remains valid without a boat");
            var boat = Boat(); BoatProtocol.Validate(boat); Check(boat.Scene == "" && boat.Qw == 1 && boat.Unlocked, "new basic boat remains unplaced with identity rotation");
            RejectState(b => b.BoatId = "other", "malformed boat identity rejected");
            RejectState(b => b.OwnerAccountId = "", "missing owner rejected");
            RejectState(b => b.PrefabId = "", "missing prefab rejected");
            RejectState(b => b.Scene = "bad\nscene", "control characters rejected");
            RejectState(b => b.DockId = "dock", "unplaced boat cannot claim a dock");
            RejectState(b => b.X = 1, "unplaced boat cannot carry a position");
            RejectState(b => b.PlayerAboard = true, "unplaced boat cannot have a passenger");
            RejectState(b => b.PlayerDriving = true, "unplaced boat cannot have a driver");
            RejectState(b => b.Revision = -1, "negative persisted revision rejected");
            RejectState(b => b.Fuel = float.NaN, "NaN fuel rejected");
            RejectState(b => b.Fuel = float.PositiveInfinity, "infinite fuel rejected");
            RejectState(b => b.Fuel = -1, "negative fuel rejected");
            RejectState(b => b.Fuel = BoatProtocol.MaxFuel + 1, "unbounded fuel rejected");
            RejectState(b => { b.Scene = "1_Lowtown"; b.X = float.NaN; }, "NaN position rejected");
            RejectState(b => { b.Scene = "1_Lowtown"; b.Y = float.NegativeInfinity; }, "infinite position rejected");
            RejectState(b => { b.Scene = "1_Lowtown"; b.Z = BoatProtocol.MaxCoordinate + 1; }, "out-of-world position rejected");
            RejectState(b => { b.Scene = "1_Lowtown"; b.Qw = 0; }, "zero rotation rejected");
            RejectState(b => { b.Scene = "1_Lowtown"; b.Qw = 2; }, "scaled rotation rejected");
            RejectState(b => { b.Scene = "1_Lowtown"; b.Qw = float.NaN; }, "NaN rotation rejected");
            RejectState(b => { b.Scene = "1_Lowtown"; b.PlayerDriving = true; }, "driving requires boarded state");
            boat.Scene = "1_Lowtown"; boat.X = -123; boat.Z = 42; boat.Qy = 0.70710677f; boat.Qw = 0.70710677f;
            boat.PlayerAboard = boat.PlayerDriving = true; boat.Revision = 9;
            BoatProtocol.Validate(boat); var serialized = RoundTrip(boat); BoatProtocol.Validate(serialized);
            Check(serialized.PlayerDriving && serialized.PlayerAboard && serialized.X == -123 && serialized.Fuel == 100 && serialized.Qy == boat.Qy, "placed boat and reconnect state round-trip");
            var detached = BoatProtocol.Copy(boat); detached.Fuel = 0; Check(boat.Fuel == 100, "state copy detached");
            var ledger = Ledger(); ledger.Accounts[0].Boat = boat;
            AccountProtocol.Validate(ledger, ledger.WorldId, ledger.Revision);
            ledger.Accounts[1].Boat = Boat(B, BoatA);
            Throws(() => AccountProtocol.Validate(ledger, ledger.WorldId, ledger.Revision), "duplicate boat IDs across accounts rejected");
            ledger.Accounts[1].Boat.BoatId = BoatA.ToUpperInvariant();
            Throws(() => AccountProtocol.Validate(ledger, ledger.WorldId, ledger.Revision), "case alias of duplicate boat GUID rejected");
            ledger.Accounts[1].Boat = Boat(A, BoatB);
            Throws(() => AccountProtocol.Validate(ledger, ledger.WorldId, ledger.Revision), "boat owner must match containing account");
            ledger.Accounts[1].Boat = Boat(B, BoatB); AccountProtocol.Validate(ledger, ledger.WorldId, ledger.Revision);
            Check(RoundTrip(ledger).Accounts[1].Boat.BoatId == BoatB, "two distinct boats survive ledger serialization");
            RejectPose(p => p.PlayerId = "", "pose needs player identity");
            RejectPose(p => p.AccountId = "", "pose needs account identity");
            RejectPose(p => p.BoatId = "", "pose needs boat identity");
            RejectPose(p => p.Scene = "", "pose must name a scene");
            RejectPose(p => p.ContextGeneration = 0, "pose requires committed context generation");
            RejectPose(p => p.Sequence = 0, "pose requires positive sequence");
            RejectPose(p => p.AppliedFuelRevision = -1, "pose rejects negative applied fuel revision");
            RejectPose(p => p.Boarded = false, "driving pose must be boarded");
            RejectPose(p => p.Fuel = -1, "pose cannot carry invalid fuel");
            var poseCopy = BoatProtocol.Copy(Pose()); Check(BoatProtocol.PoseValid(poseCopy) && !BoatProtocol.PoseValid(null), "pose validity handles null and copies");
            poseCopy.AppliedFuelRevision = 42;
            Check(BoatProtocol.Copy(poseCopy).AppliedFuelRevision == 42 && RoundTrip(poseCopy).AppliedFuelRevision == 42,
                "applied native fuel revision survives wire roundtrip and defensive pose copies");
            Check(typeof(BoatPose).GetProperty("DockId") == null, "movement cannot claim authoritative dock");
            BoatProtocol.Validate(new BoatInteractionIntent { BoatId = BoatA });
            Throws(() => BoatProtocol.Validate(new BoatInteractionIntent { BoatId = BoatA, DockId = "x\0y" }), "intent dock identifier rejects control characters");
            var world = new BoatWorldState { Sequence = 1, Boats = new[] { boat }, RuntimePoses = new[] { Pose() } };
            BoatProtocol.Validate(world); var worldCopy = BoatProtocol.Copy(world); worldCopy.Boats[0].Fuel = 8; worldCopy.RuntimePoses[0].Fuel = 7;
            Check(world.Boats[0].Fuel == 100 && world.RuntimePoses[0].Fuel == 99, "world copy deeply detaches state and poses");
            world.RuntimePoses[0].AccountId = B; Throws(() => BoatProtocol.Validate(world), "world pose cannot borrow another account boat");
            world.RuntimePoses[0] = Pose(); world.RuntimePoses[0].Scene = "2_Meridian"; Throws(() => BoatProtocol.Validate(world), "world pose must match boat scene");
            world.RuntimePoses = new[] { Pose(), Pose() }; Throws(() => BoatProtocol.Validate(world), "duplicate runtime boat poses rejected");
            world.RuntimePoses = Array.Empty<BoatPose>(); world.Boats = new PlayerBoatState[BoatProtocol.MaxBoats + 1];
            Throws(() => BoatProtocol.Validate(world), "world replica roster bounded to 32");

            PlayerAccountRuntime.Reset(); DedicatedWorldRuntime.IsDedicatedServer = true;
            SharedEconomyReplication.ApplyPersonal(new PersonalInventoryState()); ledger = Ledger();
            PlayerAccountRuntime.InitializeWorker(ledger);
            Check(PlayerAccountRuntime.GetBoat(A) == null && PlayerAccountRuntime.ListBoats().Length == 0, "old accounts have no boat until explicit authority grant");
            var before = PlayerAccountRuntime.GetAccount(A); string inventory = before.Inventory.GetRawText();
            var starting = Boat(); starting.Revision = 123;
            var granted = PlayerAccountRuntime.EnsureBoat(A, starting);
            Check(granted.Revision == 1 && granted.OwnerAccountId == A && PlayerAccountRuntime.IsDirty, "grant receives authoritative initial revision and dirties checkpoint");
            var after = PlayerAccountRuntime.GetAccount(A);
            Check(after.Money == before.Money && after.Inventory.GetRawText() == inventory && after.AppearanceId == before.AppearanceId &&
                after.StateRevision == before.StateRevision + 1, "grant changes only boat and account revision");
            starting.Fuel = 0; granted.Fuel = 1; Check(PlayerAccountRuntime.GetBoat(A).Fuel == 100, "grant inputs and outputs cannot mutate persisted state");
            long firstRevision = after.StateRevision;
            var repeat = PlayerAccountRuntime.EnsureBoat(A, Boat(B, BoatB));
            Check(repeat.BoatId == BoatA && PlayerAccountRuntime.GetAccount(A).StateRevision == firstRevision, "repeat grant is idempotent even if replacement state differs");
            Throws(() => PlayerAccountRuntime.EnsureBoat(B, Boat(B, BoatA)), "runtime rejects duplicate boat ownership before mutation");
            Check(PlayerAccountRuntime.GetBoat(B) == null, "failed duplicate grant leaves other account untouched");
            PlayerAccountRuntime.EnsureBoat(B, Boat(B, BoatB));
            var own = PlayerAccountRuntime.GetBoat(A); own.Scene = "1_Lowtown"; own.X = 10; own.Fuel = 80; own.Revision = 10000; own.PlayerAboard = own.PlayerDriving = true;
            var moved = PlayerAccountRuntime.SetBoat(A, own);
            Check(moved.Revision == 2 && moved.PlayerAboard && moved.PlayerDriving && PlayerAccountRuntime.GetAccount(A).StateRevision == firstRevision + 1, "update increments persisted revision independently of caller and preserves aboard/driving flags");
            var wrong = BoatProtocol.Copy(moved); wrong.OwnerAccountId = B;
            Throws(() => PlayerAccountRuntime.SetBoat(A, wrong), "cannot transfer account boat");
            wrong = BoatProtocol.Copy(moved); wrong.BoatId = new string('1', 32);
            Throws(() => PlayerAccountRuntime.SetBoat(A, wrong), "cannot replace persistent boat identity");
            wrong = BoatProtocol.Copy(moved); wrong.PrefabId = "upgraded-boat";
            Throws(() => PlayerAccountRuntime.SetBoat(A, wrong), "cannot replace persistent prefab");
            var all = PlayerAccountRuntime.ListBoats(); all[0].Fuel = 0; all[1] = null;
            Check(PlayerAccountRuntime.GetBoat(A).Fuel == 80 && PlayerAccountRuntime.ListBoats().Length == 2, "list returns detached states");
            using (PlayerAccountRuntime.WithAccount(B))
            {
                Throws(() => PlayerAccountRuntime.SetBoat(A, moved), "another account transaction cannot alter boat");
                Throws(() => PlayerAccountRuntime.EnsureBoat(A, Boat()), "idempotent ensure cannot bypass scope restriction");
            }
            using (PlayerAccountRuntime.WithAccount(A))
            {
                // Normal native debit and boat fuel update share the owner scope.
                Nivalis.PlayerManager._instance.LocalPlayer.Inventory._money -= 100;
                moved.Fuel = 90; PlayerAccountRuntime.SetBoat(A, moved);
            }
            Check(PlayerAccountRuntime.GetAccount(A).Money == before.Money - 100 && PlayerAccountRuntime.GetBoat(A).Fuel == 90,
                "own synchronous transaction persists fuel and matching native debit");
            Check(PlayerAccountRuntime.GetAccount(B).Money == before.Money && PlayerAccountRuntime.GetBoat(B).Fuel == 100 &&
                PlayerAccountRuntime.LegacyEscrowMoney == 97000, "refueling leaves other account and legacy assets unchanged");
            var checkpoint = PlayerAccountRuntime.CaptureLedger(6); PlayerAccountRuntime.MarkCommitted(6);
            Check(!PlayerAccountRuntime.IsDirty && checkpoint.Accounts[0].Boat.Fuel == 90 && checkpoint.Accounts[1].Boat.BoatId == BoatB,
                "paired ledger checkpoint contains distinct boats");
            moved.Fuel = 89; PlayerAccountRuntime.SetBoat(A, moved);
            Check(PlayerAccountRuntime.IsDirty && checkpoint.Accounts[0].Boat.Fuel == 90, "post-capture mutation cannot alter captured checkpoint");
            PlayerAccountRuntime.Reset(); SharedEconomyReplication.ApplyPersonal(new PersonalInventoryState());
            checkpoint = RoundTrip(checkpoint); PlayerAccountRuntime.InitializeWorker(checkpoint);
            Check(PlayerAccountRuntime.GetBoat(A).Fuel == 90 && PlayerAccountRuntime.GetBoat(A).PlayerAboard && PlayerAccountRuntime.GetBoat(A).PlayerDriving &&
                PlayerAccountRuntime.GetAccount(A).Money == before.Money - 100 && !PlayerAccountRuntime.IsDirty, "restart restores exact boat/boarded/wallet state without a new grant");
            Check(Task.Run(() => { try { PlayerAccountRuntime.SetBoat(A, moved); return false; } catch (InvalidOperationException) { return true; } }).GetAwaiter().GetResult(),
                "boat persistence remains confined to worker Unity thread");
            checkpoint.Accounts[0].Boat.Revision = long.MaxValue;
            var overflowBefore = PlayerAccountRuntime.GetAccount(A);
            Throws(() => PlayerAccountRuntime.SetBoat(A, moved), "boat revision overflow rejected before persistence");
            Check(PlayerAccountRuntime.GetAccount(A).StateRevision == overflowBefore.StateRevision && PlayerAccountRuntime.GetBoat(A).Fuel == 90,
                "overflow leaves boat and account unchanged");
            PlayerAccountRuntime.Reset(); SharedEconomyReplication.ApplyPersonal(new PersonalInventoryState());
            return checks;
        }
    }
}
