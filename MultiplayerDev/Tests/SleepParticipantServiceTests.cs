using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using NivalisTogether.Dedicated;

public static partial class SleepParticipantServiceTests
{
    private const string ParticipantKind = "sleepParticipantPresence";
    private static void RunParticipants(string root)
    {
        TestLoadFencePolicy();
        TestSleepJoinBarrier(root + "-barrier");
        Directory.CreateDirectory(root);
        using var f = new Fixture(root);
        var worker = f.Worker(out var world);
        var ledger = new AccountWorldState { WorldId = world.WorldId, Revision = 1, LegacyEscrow = new AccountInventoryState() };
        f.Checkpoint(worker, ledger, new byte[] { 1, 2, 3 });
        f.Ready(worker, ledger);
        Check(!worker.Received.Any(m => m.Type == ParticipantKind), "Empty worker roster contains no synthetic sleeper");

        using var a = f.Connect();
        Check(!worker.Received.Any(m => m.Type == ParticipantKind), "Unauthenticated socket is not a sleep participant");
        var auth = f.Authenticate(a, "register", "sleeper_a", "a-long-test-password");
        Check(!worker.Received.Any(m => m.Type == ParticipantKind), "Account selection alone is not a world participant");
        f.Join(a, "native-model-a", out var joined);
        var connected = f.Wait(worker, ParticipantKind).Read<PlayerPresence>();
        Check(connected.Joined && connected.PlayerId == joined.PlayerId && connected.AccountId == auth.AccountId,
            "Accepted authenticated join joins sleep cohort before snapshot or body readiness");
        Check(connected.Name == "sleeper_a" && connected.AppearanceId == "native-model-a", "Service stamps participant identity");
        Check(!worker.Received.Any(m => m.Type == "playerPresence"), "Pending sleeper has no playable body yet");
        Check(!a.Received.Any(m => m.Type == ParticipantKind), "Membership message is worker only");
        var token = f.Wait(worker, "checkpointRequested").Read<CheckpointRequest>().RequestId;
        ledger.Revision = 2;
        ledger.Accounts = new[] { new AccountState { AccountId = auth.AccountId, StateRevision = 1, Initialized = true, Money = 100000, AppearanceId = "native-model-a" } };
        f.Checkpoint(worker, ledger, new byte[] { 4, 5, 6 }, token);
        // The pending-recovery departure precedes clientReady, but must never
        // remove this transport from the all-player sleep denominator.
        Check(worker.Received.Any(m => m.Type == "sleepRecoveryPresence" && !m.Read<PlayerPresence>().Joined), "Recovery-only membership ends at snapshot send");
        Check(!worker.Received.Any(m => m.Type == ParticipantKind), "Snapshot send has no false sleep disconnect or duplicate join");
        f.Load(a, world.WorldId, ledger.Revision);
        var body = f.Wait(worker, "playerPresence").Read<PlayerPresence>();
        Check(body.Joined && body.PlayerId == joined.PlayerId, "Native load emits separate gameplay readiness");
        Check(!worker.Received.Any(m => m.Type == ParticipantKind), "Completing native load preserves continuous connected membership");

        using var b = f.Connect();
        var bAuth = f.Authenticate(b, "register", "sleeper_b", "another-long-password");
        f.Join(b, "native-model-b", out var bJoined);
        var bPresence = f.Wait(worker, ParticipantKind).Read<PlayerPresence>();
        Check(bPresence.Joined && bPresence.PlayerId == bJoined.PlayerId, "A second pending player counts before it can sleep");
        b.Dispose();
        var bRemoved = f.Wait(worker, ParticipantKind).Read<PlayerPresence>();
        Check(!bRemoved.Joined && bRemoved.PlayerId == bJoined.PlayerId && bRemoved.AccountId == bAuth.AccountId,
            "Actual pending disconnect removes only that authenticated participant");

        // Keep A's authenticated client alive while replacing its authority
        // transport. Native loading must not erase the connected cohort.
        worker.Dispose();
        for (int i = 0; i < 10; i++) f.Pump();
        worker = f.Worker(out var resumedWorld);
        var roster = f.Wait(worker, ParticipantKind).Read<PlayerPresence>();
        Check(roster.Joined && roster.PlayerId == joined.PlayerId && roster.AccountId == auth.AccountId,
            "New worker receives complete surviving authenticated roster before native initialization");
        Check(!worker.Received.Any(m => m.Type == ParticipantKind), "Disconnected and unjoined accounts absent from replacement roster");
        f.Ready(worker, ledger);
        var afterNativeReady = f.Wait(worker, ParticipantKind).Read<PlayerPresence>();
        Check(afterNativeReady.Joined && afterNativeReady.PlayerId == joined.PlayerId,
            "First workerReady reasserts membership after native initialization");
        f.Ready(worker, ledger);
        Check(!worker.Received.Any(m => m.Type == ParticipantKind), "Routine readiness heartbeat does not replay roster");
        f.Load(a, resumedWorld.WorldId, ledger.Revision);
        f.Wait(worker, "playerPresence");
        a.Dispose();
        var removed = f.Wait(worker, ParticipantKind).Read<PlayerPresence>();
        Check(!removed.Joined && removed.PlayerId == joined.PlayerId && removed.AccountId == auth.AccountId,
            "Actual loaded disconnect removes sleep participant");

        using var attacker = f.Connect();
        f.Authenticate(attacker, "register", "sleeper_bad", "third-long-password");
        attacker.Connection.Send(ParticipantKind, new PlayerPresence { PlayerId = joined.PlayerId, AccountId = auth.AccountId, Joined = true });
        for (int i = 0; i < 20 && attacker.Connection.Connected; i++) f.Pump();
        Check(!attacker.Connection.Connected, "Authenticated account cannot forge a service sleep presence");
        Check(!worker.Received.Any(m => m.Type == ParticipantKind), "Forged presence never reaches worker");
        worker.Connection.Send("ping", new { });
        f.Wait(worker, "pong");
        Check(worker.Connection.Connected, "Rejected membership spoof leaves authority healthy");
    }

    private static GroupSleepEvent Sleep(long sequence, string epoch, string phase, params string[] participants)
        => new GroupSleepEvent { Sequence = sequence, Epoch = epoch, Phase = phase, Participants = participants,
            ReadyPlayers = participants, GameSeconds = phase == "sleeping" ? 70000 : 100000 };

    private static void TestLoadFencePolicy()
    {
        var fence = new SleepLoadFence();
        string epoch = Guid.NewGuid().ToString("N"), player = Guid.NewGuid().ToString("N");
        Check(fence.CanLoad(1), "Initial committed world may be loaded");
        fence.Observe(Sleep(1, epoch, "waiting", player), 10);
        Check(!fence.TransitionActive && fence.CanLoad(10), "Readiness waiting permits remaining players to import");
        fence.Observe(Sleep(2, epoch, "cancelled", player), 10);
        Check(fence.CanLoad(10), "Cancelled readiness imposes no fictitious night barrier");
        long beforeNight = fence.BeginSnapshot();
        Check(fence.Observe(Sleep(3, epoch, "sleeping", player), 10), "Native night begins a new fence");
        Check(fence.TransitionActive && !fence.CanLoad(10), "Active night blocks old world imports");
        fence.CommitSnapshot(11, beforeNight);
        Check(!fence.CanLoad(11), "A capture from before night cannot open the fence");
        fence.Observe(Sleep(4, epoch, "synchronizing", player), 11);
        long duringNight = fence.BeginSnapshot();
        fence.CommitSnapshot(12, duringNight);
        Check(!fence.CanLoad(12), "Wake checkpoint can commit without releasing pending joins");
        bool wrongEpoch = false;
        try { fence.Observe(Sleep(5, Guid.NewGuid().ToString("N"), "complete", player), 12); }
        catch (InvalidDataException) { wrongEpoch = true; }
        Check(wrongEpoch && fence.TransitionActive, "Unrelated completion cannot release active night");
        fence.Observe(Sleep(6, epoch, "complete", player), 12);
        Check(!fence.TransitionActive && !fence.CanLoad(12), "Complete still waits for a new durable capture");
        fence.CommitSnapshot(13, duringNight);
        Check(!fence.CanLoad(13), "Capture begun before complete cannot reopen imports after complete");
        long afterNight = fence.BeginSnapshot();
        fence.CommitSnapshot(14, afterNight);
        Check(fence.CanLoad(14) && !fence.CanLoad(12), "Only a fresh post-completion world passes");
        fence.Observe(Sleep(7, epoch, "complete", player), 14);
        Check(fence.CanLoad(14), "Repeated final state does not create an endless fresh-checkpoint requirement");
        fence.Reset();
        Check(fence.CanLoad(1) && fence.Generation == 0, "Worker replacement resets old native transition fence");
    }

    private static void TestSleepJoinBarrier(string root)
    {
        using var f = new Fixture(root);
        var worker = f.Worker(out var world);
        var ledger = new AccountWorldState { WorldId = world.WorldId, Revision = 1, LegacyEscrow = new AccountInventoryState() };
        f.Checkpoint(worker, ledger, new byte[] { 1, 2, 3 }); f.Ready(worker, ledger);
        using var a = f.Connect();
        var auth = f.Authenticate(a, "register", "barrier_a", "a-long-test-password");
        f.Join(a, "native-model-a", out var joined);
        var token = f.Wait(worker, "checkpointRequested").Read<CheckpointRequest>().RequestId;
        ledger.Revision++;
        ledger.Accounts = new[] { new AccountState { AccountId = auth.AccountId, StateRevision = 1, Initialized = true, Money = 100000, AppearanceId = "native-model-a" } };
        f.Checkpoint(worker, ledger, new byte[] { 4, 5, 6 }, token); f.Load(a, world.WorldId, ledger.Revision);
        string epoch = Guid.NewGuid().ToString("N");
        PublishSleep(f, worker, Sleep(1, epoch, "waiting", joined.PlayerId), a);
        using var b = f.Connect();
        var bAuth = f.Authenticate(b, "register", "barrier_b", "another-long-password");
        f.Join(b, "native-model-b", out var bJoined);
        token = f.Wait(worker, "checkpointRequested").Read<CheckpointRequest>().RequestId;
        ledger.Revision++;
        ledger.Accounts = ledger.Accounts.Append(new AccountState { AccountId = bAuth.AccountId, StateRevision = 1, Initialized = true, Money = 100000, AppearanceId = "native-model-b" }).ToArray();
        f.Checkpoint(worker, ledger, new byte[] { 7, 8, 9 }, token);
        f.Load(b, world.WorldId, ledger.Revision);
        Check(b.Connection.Connected, "Real pending join can finish native import while another player waits to sleep");
        PublishSleep(f, worker, Sleep(2, epoch, "cancelled", joined.PlayerId, bJoined.PlayerId), a, b);
        epoch = Guid.NewGuid().ToString("N");
        PublishSleep(f, worker, Sleep(3, epoch, "sleeping", joined.PlayerId, bJoined.PlayerId), a, b);

        using var c = f.Connect();
        var cAuth = f.Authenticate(c, "register", "barrier_c", "third-long-password");
        f.Join(c, "native-model-a", out var cJoined);
        token = f.Wait(worker, "checkpointRequested").Read<CheckpointRequest>().RequestId;
        ledger.Revision++;
        ledger.Accounts = ledger.Accounts.Append(new AccountState { AccountId = cAuth.AccountId, StateRevision = 1, Initialized = true, Money = 100000, AppearanceId = "native-model-a" }).ToArray();
        f.Checkpoint(worker, ledger, new byte[] { 10, 11, 12 }, token);
        Check(!c.Received.Any(m => m.Type == "accountDataBegin" || m.Type == "snapshotBegin"), "Join after sleep start cannot import an active-night checkpoint");
        Check(worker.Connection.Connected, "Wake checkpoint remains valid authority traffic during night fence");
        PublishSleep(f, worker, Sleep(4, epoch, "synchronizing", joined.PlayerId, bJoined.PlayerId), a, b, c);
        worker.Received.RemoveAll(m => m.Type == "checkpointRequested");
        ledger.Revision++;
        byte[] accountBytes = JsonSerializer.SerializeToUtf8Bytes(ledger, DedicatedProtocol.Json);
        var transfer = new AccountDataTransfer(); transfer.StartSend(accountBytes, world.WorldId, ledger.Revision, "ledger");
        while (transfer.IsSending) { transfer.PumpSend(worker.Connection); f.Pump(); }
        PublishSleep(f, worker, Sleep(5, epoch, "complete", joined.PlayerId, bJoined.PlayerId), a, b, c);
        token = f.Wait(worker, "checkpointRequested").Read<CheckpointRequest>().RequestId;
        Check(!c.Received.Any(m => m.Type == "accountDataBegin" || m.Type == "snapshotBegin"), "Complete requests fresh capture before releasing pending join");
        string id = Guid.NewGuid().ToString("N"); byte[] native = { 13, 14, 15 };
        worker.Connection.Send("snapshotBegin", new SnapshotBegin { TransferId = id, WorldId = world.WorldId, Revision = ledger.Revision,
            Format = "nivalis-es2-v1", Length = native.Length, Sha256 = Convert.ToHexString(SHA256.HashData(native)),
            AccountLedgerHash = Convert.ToHexString(SHA256.HashData(accountBytes)), CheckpointRequestId = token });
        worker.Connection.Send("snapshotChunk", new SnapshotChunk { TransferId = id, Data = native });
        worker.Connection.Send("snapshotEnd", new SnapshotEnd { TransferId = id }); f.Wait(worker, "snapshotCommitted");
        Check(!c.Received.Any(m => m.Type == "accountDataBegin" || m.Type == "snapshotBegin"), "Cross-completion ledger capture cannot bypass service sleep fence");
        token = f.Wait(worker, "checkpointRequested").Read<CheckpointRequest>().RequestId;
        ledger.Revision++;
        f.Checkpoint(worker, ledger, new byte[] { 16, 17, 18 }, token);
        var imported = f.Load(c, world.WorldId, ledger.Revision);
        Check(imported.AccountId == cAuth.AccountId && imported.Money == 100000, "Post-completion paired checkpoint releases pending account with its own wallet");
        Check(a.Connection.Connected && b.Connection.Connected && worker.Connection.Connected, "Existing players and authority remain connected through fenced night");
    }

    private static void PublishSleep(Fixture f, Client worker, GroupSleepEvent state, params Client[] recipients)
    {
        worker.Connection.Send("worldEvent", new WorldEvent { Kind = SleepRecoveryProtocol.EventKind, Data = JsonSerializer.SerializeToElement(state, DedicatedProtocol.Json) });
        foreach (var recipient in recipients)
        {
            var update = f.Wait(recipient, "worldEvent").Read<WorldEvent>();
            Check(update.Kind == SleepRecoveryProtocol.EventKind && update.Data.Deserialize<GroupSleepEvent>(DedicatedProtocol.Json).Sequence == state.Sequence,
                "Sleep authority state reaches expected connected recipient");
            recipient.Connection.Send("worldEventAck", new WorldEventAcknowledgement { DeliveryIds = new[] { update.DeliveryId } });
        }
        for (int i = 0; i < 3; i++) f.Pump();
    }
}
