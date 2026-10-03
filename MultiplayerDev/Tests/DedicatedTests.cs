using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using NivalisTogether.Dedicated;

// Protocol tests only. Synthetic worker bytes/events are NOT a Unity game simulation,
// and successful results are NOT evidence of gameplay or multiplayer acceptance.
internal static class DedicatedTests
{
    private const string Password = "test-password-only";
    private const string AuthoritySecret = "test-authority-secret-not-production";
    private static string serviceAssembly, dotnet, artifacts;
    private static int assertions;
    private static readonly List<string> scenarios = new List<string>();

    public static int Main(string[] args)
    {
        if (args.Length != 3) { Console.Error.WriteLine("Usage: DedicatedTests.dll SERVICE_DLL DOTNET_EXE ARTIFACT_DIRECTORY"); return 2; }
        serviceAssembly = Path.GetFullPath(args[0]); dotnet = args[1]; artifacts = Path.GetFullPath(args[2]);
        Directory.CreateDirectory(artifacts);
        try
        {
            AuthenticationFramingAndSnapshotValidation();
            ReadyBarrierAuthorityAndPersistence();
            CreditedWorldEventsAndSlowClientIsolation();
            BinarySchedulerCreditsAndAcknowledgementRace();
            SignedRejectionSurvivesTransportClose();
            WorldIsolationAndImportRefusal();
            LocalDiscovery();
            string result = "PASS: " + assertions + " assertions in " + scenarios.Count + " scenarios. Separate standalone service processes; raw independent TCP clients; simulated worker PROTOCOL TESTS ONLY.";
            Console.WriteLine(result);
            File.WriteAllText(Path.Combine(artifacts, "result.txt"), result + Environment.NewLine + string.Join(Environment.NewLine, scenarios));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            File.WriteAllText(Path.Combine(artifacts, "result.txt"), "FAIL after " + assertions + " assertions." + Environment.NewLine + ex);
            return 1;
        }
    }

    private static void Check(bool value, string message)
    { assertions++; if (!value) throw new Exception("FAIL: " + message); }
    private static void Scenario(string name)
    { scenarios.Add(name); Console.WriteLine("PASS: " + name); }
    private static JsonElement Data<T>(T value) => JsonSerializer.SerializeToElement(value, DedicatedProtocol.Json);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static void AuthenticationFramingAndSnapshotValidation()
    {
        using var server = new Service("validation");
        var status = server.Status();
        Check(!status.Ready && status.Players == 0 && status.PasswordRequired, "Service waits for a worker, counts no players, advertises authentication");
        Check(status.State.Contains("Waiting") && status.MaxPlayers == 4, "Waiting state and capacity are accurate");
        using (var waiting = server.Join("player", "Waiting player", Password))
        {
            Check(!waiting.Accepted.Ready && waiting.Accepted.World.Revision == 0, "Player join cannot make an uninitialized world ready");
            waiting.ReadUntil("simulationState");
            waiting.Send("interaction", new InteractionRequest { PlayerId = "forged", RequestId = "early", Kind = "test", TargetId = "object", Data = Data(new { amount = 1 }) });
            Check(waiting.ReadUntil("error").Read<ProtocolError>().Code == "world_not_ready", "Interaction is blocked without a ready worker");
        }
        server.WaitStatus(s => s.Players == 0);
        Scenario("Standalone startup and joining without a worker stay unready; disconnect removes waiting player");

        ExpectJoinError(server, "player", "wrong-password", "authentication_failed");
        ExpectJoinError(server, "worker", "wrong-authority-secret", "authentication_failed");
        using (var peer = new RawPeer(server.Port))
        {
            var join = DedicatedProtocol.CreateJoin(peer.Hello, "player", "wrong-build", Password);
            join.BuildId = "different-build";
            join.Proof = DedicatedProtocol.Proof(peer.Hello, join, Password);
            peer.Send("join", join); peer.ExpectErrorAndClose("incompatible_build");
        }
        using (var peer = new RawPeer(server.Port))
        {
            peer.Send("join", DedicatedProtocol.CreateJoin(peer.Hello, "admin", "invalid-role", Password));
            peer.ExpectErrorAndClose("invalid_message");
        }
        using (var peer = new RawPeer(server.Port))
        { peer.Send("workerReady", new { }); peer.ExpectErrorAndClose("handshake_required"); }
        Scenario("Wrong player/worker secrets, incompatible builds, invalid roles and missing handshake rejected");

        using (var peer = new RawPeer(server.Port)) { peer.WriteBytes(new byte[] { 0, 1, 0, 1 }); Check(peer.WaitClosed(), "Oversized frame header disconnects"); }
        using (var peer = new RawPeer(server.Port)) { peer.WriteBytes(new byte[] { 0, 0, 0, 0 }); Check(peer.WaitClosed(), "Zero-length frame disconnects"); }
        using (var peer = new RawPeer(server.Port)) { peer.WriteBytes(RawPeer.Frame(Encoding.UTF8.GetBytes("{broken json}"))); Check(peer.WaitClosed(), "Malformed JSON disconnects"); }
        using (var peer = new RawPeer(server.Port))
        {
            var message = peer.Envelope("statusQuery", new { });
            byte[] frame = RawPeer.Frame(JsonSerializer.SerializeToUtf8Bytes(message, DedicatedProtocol.Json));
            for (int i = 0; i < frame.Length; i += 3) peer.WriteBytes(frame.Skip(i).Take(Math.Min(3, frame.Length - i)).ToArray());
            Check(!peer.ReadUntil("serverStatus").Read<ServerStatus>().Ready, "Fragmented TCP frame reassembles");
            peer.WriteBytes(frame); Check(peer.WaitClosed(), "Replayed sequence disconnects");
        }
        using (var peer = new RawPeer(server.Port))
        {
            var message = peer.Envelope("statusQuery", new { }); message.SessionId = Guid.NewGuid().ToString("N");
            peer.WriteBytes(RawPeer.Frame(JsonSerializer.SerializeToUtf8Bytes(message, DedicatedProtocol.Json)));
            Check(peer.WaitClosed(), "Mismatched session disconnects");
        }
        using (var peer = server.Join("player", "Bad MAC", Password))
        {
            var message = peer.Envelope("statusQuery", new { }); message.Mac = new string('0', 64);
            peer.WriteBytes(RawPeer.Frame(JsonSerializer.SerializeToUtf8Bytes(message, DedicatedProtocol.Json)));
            Check(peer.WaitClosed(), "Authenticated bad MAC disconnects");
        }
        using (var peer = server.Join("player", "Reflected MAC", Password))
        {
            peer.SendWrongDirection("statusQuery", false);
            Check(peer.WaitClosed(), "Correct-key frame signed for server-to-client direction is rejected by server");
        }
        using (var peer = server.Join("player", "Legacy MAC", Password))
        {
            peer.SendWrongDirection("statusQuery", true);
            Check(peer.WaitClosed(), "Legacy directionless MAC is rejected");
        }
        Check(!server.Status().Ready, "Malformed clients do not crash or activate service");
        Scenario("Frame bounds, malformed JSON, TCP fragmentation, replay, session binding, MAC validation and direction-domain separation");

        using (var worker = server.Join("worker", "Simulated protocol worker", AuthoritySecret))
        {
            Check(worker.ReadUntil("worldLoadComplete").Read<WorldLoadComplete>().Revision == 0, "Fresh worker receives empty server world marker");
            ExpectJoinError(server, "worker", AuthoritySecret, "authority_denied");
            worker.Send("workerReady", new WorkerReady { WorldId = worker.Accepted.World.WorldId, Revision = 0, Scene = "test-scene" });
            worker.ExpectErrorAndClose("invalid_message");
        }
        WaitNoWorker(server);
        Scenario("Only one worker permitted and workerReady without a committed snapshot is rejected");

        InvalidSnapshot(server, "oversized snapshot", (worker, begin) => { begin.Length = DedicatedProtocol.MaxSnapshotBytes + 1; worker.Send("snapshotBegin", begin); });
        InvalidSnapshot(server, "wrong chunk offset", (worker, begin) =>
        {
            worker.Send("snapshotBegin", begin);
            worker.Send("snapshotChunk", new SnapshotChunk { TransferId = begin.TransferId, Offset = 1, Data = new byte[] { 1, 2, 3, 4 } });
        });
        InvalidSnapshot(server, "truncated snapshot", (worker, begin) =>
        {
            worker.Send("snapshotBegin", begin);
            worker.Send("snapshotChunk", new SnapshotChunk { TransferId = begin.TransferId, Offset = 0, Data = new byte[] { 1 } });
            worker.Send("snapshotEnd", new SnapshotEnd { TransferId = begin.TransferId });
        });
        InvalidSnapshot(server, "checksum mismatch", (worker, begin) =>
        {
            begin.Sha256 = new string('0', 64); worker.Send("snapshotBegin", begin);
            worker.Send("snapshotChunk", new SnapshotChunk { TransferId = begin.TransferId, Offset = 0, Data = new byte[] { 1, 2, 3, 4 } });
            worker.Send("snapshotEnd", new SnapshotEnd { TransferId = begin.TransferId });
        });
        var metadata = JsonSerializer.Deserialize<WorldMetadata>(File.ReadAllText(Path.Combine(server.SaveRoot, "world.json")), DedicatedProtocol.Json);
        Check(metadata.Revision == 0 && !Directory.EnumerateFiles(server.SaveRoot, "snapshot-*.bin").Any(), "Rejected snapshots never persist or advance world revision");
        Scenario("Snapshot size, contiguous offsets, exact length and SHA-256 checks reject invalid worker transfers without persistence");
    }

    private static void InvalidSnapshot(Service server, string name, Action<RawPeer, SnapshotBegin> send)
    {
        using var worker = server.Join("worker", "Validation worker", AuthoritySecret);
        worker.ReadUntil("worldLoadComplete");
        var begin = new SnapshotBegin { TransferId = Guid.NewGuid().ToString("N"), WorldId = worker.Accepted.World.WorldId, Revision = 1, Length = 4, Sha256 = Hash(new byte[] { 1, 2, 3, 4 }), Format = "protocol-test-bytes" };
        send(worker, begin); worker.ExpectErrorAndClose("invalid_message");
        WaitNoWorker(server); Check(!server.Status().Ready, name + " leaves service unready");
    }

    private static void ReadyBarrierAuthorityAndPersistence()
    {
        byte[] checkpoint = Enumerable.Range(0, 110123).Select(i => (byte)((i * 31 + i / 97) % 251)).ToArray();
        byte[] nextCheckpoint = checkpoint.Concat(new byte[] { 7, 8, 9 }).ToArray();
        string worldId; int seed; string root; long revision = 1;
        using (var server = new Service("persistent"))
        {
            root = server.SaveRoot;
            using var player = server.Join("player", "Actual player identity", Password);
            player.ReadUntil("simulationState");
            using var worker = server.Join("worker", "Simulated protocol worker", AuthoritySecret);
            worldId = worker.Accepted.World.WorldId; seed = worker.Accepted.World.Seed;
            worker.ReadUntil("worldLoadComplete");
            Upload(worker, worldId, 1, checkpoint);
            Check(!server.Status().Ready, "Committing bytes alone never marks simulation ready");
            worker.Send("workerReady", new WorkerReady { WorldId = worldId, Revision = 1, Scene = "test-scene" });
            Check(server.WaitStatus(s => s.Ready).Ready, "Authenticated workerReady marks service ready");
            ReceiveSnapshot(player, worldId, 1, checkpoint);
            Check(File.ReadAllBytes(Path.Combine(root, "snapshot-1.bin")).SequenceEqual(checkpoint), "Checkpoint bytes are durable before acknowledgement");
            player.Send("interaction", new InteractionRequest { PlayerId = "spoof", RequestId = "before-load", Kind = "buy", TargetId = "test-shop", Data = Data(new { quantity = 1 }) });
            Check(player.ReadUntil("error").Read<ProtocolError>().Code == "world_not_ready", "Full transfer still requires clientReady load barrier");
            player.Send("clientReady", new WorldLoadComplete { WorldId = worldId, Revision = 1 });
            player.ReadUntil("clientReadyAccepted");
            var arrival = worker.ReadUntil("playerPresence").Read<PlayerPresence>();
            Check(arrival.Joined && arrival.PlayerId == player.Accepted.PlayerId, "Worker receives arrival only after client load barrier");
            Scenario("Large snapshot transfer, durable byte/hash identity and explicit client load barrier");

            using (var firstLate = server.Join("player", "First late join", Password))
            {
                var firstRequest = worker.ReadUntil("checkpointRequested").Read<CheckpointRequest>();
                Check(!firstLate.HasMessage("snapshotBegin", 120), "Late join never receives an old checkpoint immediately");
                Upload(worker, worldId, ++revision, checkpoint);
                Check(!firstLate.HasMessage("snapshotBegin", 120), "Uncorrelated checkpoint does not release a late join");
                var begin = new SnapshotBegin { TransferId = Guid.NewGuid().ToString("N"), WorldId = worldId, Revision = ++revision,
                    Length = checkpoint.Length, Sha256 = Hash(checkpoint), Format = "protocol-test-bytes", CheckpointRequestId = firstRequest.RequestId };
                worker.Send("snapshotBegin", begin);
                worker.Send("snapshotChunk", new SnapshotChunk { TransferId = begin.TransferId, Offset = 0, Data = checkpoint.Take(DedicatedProtocol.SnapshotChunkBytes).ToArray() });
                using var secondLate = server.Join("player", "Join during upload", Password);
                var newestRequest = worker.ReadUntil("checkpointRequested").Read<CheckpointRequest>();
                Check(firstRequest.RequestId != newestRequest.RequestId, "Joining during upload supersedes capture correlation token");
                for (int offset = DedicatedProtocol.SnapshotChunkBytes; offset < checkpoint.Length; offset += DedicatedProtocol.SnapshotChunkBytes)
                    worker.Send("snapshotChunk", new SnapshotChunk { TransferId = begin.TransferId, Offset = offset, Data = checkpoint.Skip(offset).Take(DedicatedProtocol.SnapshotChunkBytes).ToArray() });
                worker.Send("snapshotEnd", new SnapshotEnd { TransferId = begin.TransferId });
                Check(worker.ReadUntil("snapshotCommitted").Read<WorldLoadComplete>().Revision == revision, "Older in-flight capture can persist safely");
                Check(!firstLate.HasMessage("snapshotBegin", 120) && !secondLate.HasMessage("snapshotBegin", 120), "Superseded capture releases neither waiting join");
                Upload(worker, worldId, ++revision, checkpoint, newestRequest.RequestId);
                ReceiveSnapshot(firstLate, worldId, revision, checkpoint);
                ReceiveSnapshot(secondLate, worldId, revision, checkpoint);
            }
            Scenario("Late joins require fresh correlated capture; join during upload supersedes prior token and newest capture releases all waiters");

            using (var stale = server.Join("player", "Stale revision", Password))
            {
                CaptureForJoin(worker, stale, worldId, ref revision, checkpoint);
                stale.Send("clientReady", new WorldLoadComplete { WorldId = worldId, Revision = revision + 1 });
                stale.ExpectErrorAndClose("invalid_message");
            }
            using (var wrongWorld = server.Join("player", "Wrong world", Password))
            {
                CaptureForJoin(worker, wrongWorld, worldId, ref revision, checkpoint);
                wrongWorld.Send("clientReady", new WorldLoadComplete { WorldId = Guid.NewGuid().ToString("N"), Revision = revision });
                wrongWorld.ExpectErrorAndClose("invalid_message");
            }
            Scenario("Client load acknowledgement must match offered world and checkpoint revision");

            player.Send("playerPose", new PlayerPose { PlayerId = "spoof-id", Name = "spoof-name", Scene = "test-scene", X = 1, Y = 2, Z = 3, Yaw = 90, Active = true });
            var pose = worker.ReadUntil("playerPose").Read<PlayerPose>();
            Check(pose.PlayerId == player.Accepted.PlayerId && pose.Name == "Actual player identity" && pose.X == 1, "Service stamps movement identity");
            player.Send("interaction", new InteractionRequest { PlayerId = "spoof-id", RequestId = "purchase-1", Kind = "buy", TargetId = "test-shop", Data = Data(new { quantity = 2 }) });
            var interaction = worker.ReadUntil("interaction").Read<InteractionRequest>();
            Check(interaction.PlayerId == player.Accepted.PlayerId && interaction.RequestId == "purchase-1", "Service stamps interaction identity and routes intent to worker");
            worker.Send("worldEvent", new WorldEvent { Kind = "clock", Revision = 999, Data = Data(new { minute = 123 }) });
            var worldEvent = player.ReadUntil("worldEvent").Read<WorldEvent>();
            Check(worldEvent.WorldId == worldId && worldEvent.Revision == revision && worldEvent.Data.GetProperty("minute").GetInt32() == 123, "Worker event relays with server-owned world/revision");
            worker.Send("playerPose", pose);
            Check(player.ReadUntil("playerPose").Read<PlayerPose>().PlayerId == player.Accepted.PlayerId, "Worker-approved pose reaches loaded player");
            Scenario("Authenticated player identities are stamped; only worker-approved movement and world events reach clients");

            using (var unready = server.Join("player", "Still loading", Password))
            {
                CaptureForJoin(worker, unready, worldId, ref revision, checkpoint);
                worker.Send("worldEvent", new WorldEvent { Kind = "clock", Data = Data(new { minute = 124 }) });
                player.ReadUntil("worldEvent");
                Check(!unready.HasMessage("worldEvent", 250), "Unacknowledged client does not receive world events");
            }
            using (var hostile = server.Join("player", "Client authority attempt", Password))
            {
                CaptureForJoin(worker, hostile, worldId, ref revision, checkpoint);
                hostile.Send("clientReady", new WorldLoadComplete { WorldId = worldId, Revision = revision }); hostile.ReadUntil("clientReadyAccepted");
                hostile.Send("worldEvent", new WorldEvent { WorldId = worldId, Kind = "money", Data = Data(new { money = 999999 }) });
                hostile.ExpectErrorAndClose("authority_violation");
                Check(!player.HasMessage("worldEvent", 250), "Forged client event never reaches other player");
            }
        Scenario("Loading clients receive no ordinary world updates and client-authored world events are rejected");

            var additional = new List<RawPeer>();
            try
            {
                server.WaitStatus(s => s.Players == 1);
                for (int i = 0; i < 3; i++) additional.Add(server.Join("player", "Capacity " + i, Password));
                for (int i = 0; i < 3; i++) worker.ReadUntil("checkpointRequested");
                Check(server.Status().Players == 4, "Capacity counts all connected player roles");
                ExpectJoinError(server, "player", Password, "server_full");
            }
            finally { foreach (var peer in additional) peer.Dispose(); }
            player.Send("leave", new { }); Check(player.WaitClosed(), "Explicit leave closes client connection");
            Check(server.WaitStatus(s => s.Players == 0).Ready, "Service stays ready with zero human players while worker is alive");
            bool departureReceived = false;
            for (int i = 0; i < 32 && !departureReceived; i++)
            {
                var departure = worker.ReadUntil("playerPresence").Read<PlayerPresence>();
                departureReceived = departure.PlayerId == player.Accepted.PlayerId && !departure.Joined;
            }
            Check(departureReceived, "Worker receives removal for the authenticated departing player");
            // The Unity worker can echo a pose already queued before that player's
            // disconnect. Dropping this stale echo must not evict the authority.
            worker.Send("playerPose", pose);
            worker.Send("ping", new { probe = "departed-player-pose" });
            Check(worker.ReadUntil("pong").Type == "pong", "Worker remains connected after echoing a departed player's queued pose");
            Check(server.Status().Ready && server.Status().Players == 0, "Stale worker pose cannot revoke simulation readiness or recreate a departed player");
            Scenario("Worker echo racing a player's disconnect is dropped without disconnecting the simulation");
            Upload(worker, worldId, ++revision, nextCheckpoint);
            worker.Send("workerReady", new WorkerReady { WorldId = worldId, Revision = revision, Scene = "test-scene" });
            Check(server.Status().Ready && server.Status().Players == 0, "Worker can checkpoint continued state with zero players");
            Check(File.ReadAllBytes(Path.Combine(root, "snapshot-" + revision + ".bin")).SequenceEqual(nextCheckpoint), "Zero-player checkpoint persisted");
            Scenario("Capacity enforcement, explicit leave cleanup, and worker-owned persistence with zero human players");

            using (var remaining = server.Join("player", "Worker-loss client", Password))
            {
                CaptureForJoin(worker, remaining, worldId, ref revision, nextCheckpoint);
                remaining.Send("clientReady", new WorldLoadComplete { WorldId = worldId, Revision = revision }); remaining.ReadUntil("clientReadyAccepted");
                worker.Dispose();
                Check(!remaining.ReadUntil("simulationState").Read<SimulationState>().Ready, "Worker loss invalidates ready state and notifies clients");
                remaining.Send("interaction", new InteractionRequest { RequestId = "after-loss", Kind = "test", TargetId = "test", Data = Data(new { }) });
                Check(remaining.ReadUntil("error").Read<ProtocolError>().Code == "world_not_ready", "Interactions blocked again after worker loss");
                Check(!server.Status().Ready, "Service status reports worker loss");
            }
            Scenario("Worker disconnect revokes readiness and closes gameplay load barrier");
            Check(server.StopGracefully(), "Stop marker cleanly exits dedicated process with status zero and is consumed");
        }

        using (var restarted = new Service("persistent-restart", root))
        {
            Check(!restarted.Status().Ready && restarted.Status().WorldId == worldId, "Restart retains same world identity but waits for real simulation");
            using var worker = restarted.Join("worker", "Restore test worker", AuthoritySecret);
            Check(worker.Accepted.World.Seed == seed && worker.Accepted.World.Revision == revision, "Restart preserves seed and checkpoint revision");
            ReceiveSnapshot(worker, worldId, revision, nextCheckpoint);
            worker.Send("workerReady", new WorkerReady { WorldId = worldId, Revision = revision, Scene = "test-scene" });
            Check(restarted.WaitStatus(s => s.Ready).Ready, "Restored worker must acknowledge readiness again");
            Check(Directory.EnumerateFiles(root, "snapshot-*.bin").Count() == 2, "Latest checkpoint and predecessor retained");
        }
        Scenario("Separate-process restart restores exact checkpoint, stable world/seed, and requires worker readiness anew");
    }

    private static void CreditedWorldEventsAndSlowClientIsolation()
    {
        using var server = new Service("world-flow");
        using var player = server.Join("player", "Applying client", Password);
        player.ReadUntil("simulationState");
        using var worker = server.Join("worker", "Credit-aware protocol worker", AuthoritySecret);
        worker.ReadUntil("worldLoadComplete");
        string worldId = worker.Accepted.World.WorldId; long revision = 1;
        byte[] checkpoint = new byte[] { 2, 7, 1, 8, 2, 8 };
        Upload(worker, worldId, revision, checkpoint);
        worker.Send("workerReady", new WorkerReady { WorldId = worldId, Revision = revision, Scene = "flow-test" });
        ReceiveSnapshot(player, worldId, revision, checkpoint);
        player.Send("clientReady", new WorldLoadComplete { WorldId = worldId, Revision = revision });
        player.ReadUntil("clientReadyAccepted"); worker.ReadUntil("playerPresence");

        const int chunks = 80;
        string content = new string('x', 30000);
        var burst = Enumerable.Range(0, chunks).Select(i => new WorldEvent {
            Kind = "inventory-chunk", Data = Data(new { batch = "over-two-megabytes", index = i, count = chunks, content }) }).ToArray();
        Check(burst.Sum(e => Encoding.UTF8.GetByteCount(e.Data.GetRawText())) > 2 * 1024 * 1024, "Burst exceeds the former two-megabyte transport queue");
        int sent = 0, received = 0; var timer = Stopwatch.StartNew();
        while (received < chunks && timer.ElapsedMilliseconds < 12000)
        {
            while (sent < chunks && worker.TrySendWorldEvent(burst[sent])) sent++;
            while (player.TryReadAvailable(out var message))
            {
                if (message.Type == "error") throw new Exception("Flow burst rejected: " + message.Read<ProtocolError>().Code);
                if (message.Type != "worldEvent") continue;
                var value = message.Read<WorldEvent>();
                Check(value.Kind == "inventory-chunk" && value.WorldId == worldId && value.Revision == revision,
                    "Burst deliveries preserve authoritative kind, world and revision");
                Check(value.Data.GetProperty("index").GetInt32() == received && value.Data.GetProperty("count").GetInt32() == chunks &&
                    value.Data.GetProperty("content").GetString() == content, "Complete chunk batch arrives in order without dropped or truncated data");
                received++;
            }
            Thread.Sleep(1);
        }
        Check(sent == chunks && received == chunks && server.Status().Ready, "Credited burst drains fully without resetting either endpoint");
        // Repeating a previously applied receipt is idempotent; it cannot mint credit.
        long acknowledged = player.LastDeliveryId;
        player.Send("worldEventAck", new WorldEventAcknowledgement { DeliveryIds = new[] { acknowledged } });
        player.Send("worldEventAck", new WorldEventAcknowledgement { DeliveryIds = new[] { acknowledged } });
        player.Send("ping", new { }); Check(player.ReadUntil("pong").Type == "pong", "Repeated past acknowledgement is harmless");
        Scenario("Protocol credits stream an ordered burst above two MiB without resets; applied delivery receipts are idempotent");

        using var stalled = server.Join("player", "Not applying updates", Password);
        CaptureForJoin(worker, stalled, worldId, ref revision, checkpoint);
        stalled.Send("clientReady", new WorldLoadComplete { WorldId = worldId, Revision = revision });
        stalled.ReadUntil("clientReadyAccepted"); worker.ReadUntil("playerPresence");
        stalled.AutoAcknowledgeWorldEvents = false;
        // Drain the last burst's acknowledged credit grants before measuring a full window.
        timer.Restart(); int fillSent = 0, healthyReceived = 0, stalledReceived = 0, charged = 0;
        WorldEvent next = null;
        while (timer.ElapsedMilliseconds < 1200)
        {
            next = new WorldEvent { Kind = "npc-chunk", Data = Data(new { index = fillSent, content }) };
            if (worker.TrySendWorldEvent(next)) { fillSent++; charged += WorldEventFlow.Cost(next); }
            while (player.TryReadAvailable(out var message))
            {
                if (message.Type == "error") throw new Exception("Applying peer failed: " + message.Read<ProtocolError>().Code);
                if (message.Type == "worldEvent") { Check(message.Read<WorldEvent>().Data.GetProperty("index").GetInt32() == healthyReceived++, "Healthy recipient consumes the saturated batch in order"); }
            }
            while (stalled.TryReadAvailable(out var message))
            {
                if (message.Type == "worldEvent") { Check(message.Read<WorldEvent>().Data.GetProperty("index").GetInt32() == stalledReceived++, "Unapplied recipient sees no gaps before its credit window fills"); }
                else if (message.Type == "error") throw new Exception("Stalled peer rejected before timeout: " + message.Read<ProtocolError>().Code);
            }
            Thread.Sleep(2);
        }
        Check(fillSent > 0 && healthyReceived == fillSent && stalledReceived == fillSent, "Both clients receive the same bounded pre-stall batch");
        Check(charged <= WorldEventFlow.BulkWindowBytes && charged + WorldEventFlow.Cost(next) > WorldEventFlow.BulkWindowBytes && !worker.TrySendWorldEvent(next),
            "No client can accumulate more than one bulk window; worker pauses without dropping the batch");
        var control = new WorldEvent { Kind = "timeWeather", Data = Data(new { minute = 321 }) };
        Check(worker.TrySendWorldEvent(control), "Reserved control credit remains usable while bulk is saturated");
        Check(player.ReadUntil("worldEvent").Read<WorldEvent>().Kind == "timeWeather" &&
            stalled.ReadUntil("worldEvent").Read<WorldEvent>().Kind == "timeWeather", "Control event reaches both peers during bulk backpressure");

        string rejection = null; bool checkedPausedReadiness = false;
        while (timer.Elapsed.TotalSeconds < WorldEventFlow.StallSeconds + 6 && rejection == null)
        {
            worker.PumpMaintenance();
            while (player.TryReadAvailable(out var message))
                Check(message.Type != "error" && message.Type != "simulationState", "Applying peer stays connected and ready while the other application is stalled");
            while (stalled.TryReadAvailable(out var message))
            {
                if (message.Type == "error") { rejection = message.Read<ProtocolError>().Code; break; }
            }
            if (!checkedPausedReadiness && timer.Elapsed.TotalSeconds >= 16)
            {
                var status = server.Status();
                Check(status.Ready && status.Players == 2, "Worker pong maintains readiness beyond the simulation-event timeout while delivery is paused");
                checkedPausedReadiness = true;
            }
            Thread.Sleep(10);
        }
        Check(checkedPausedReadiness && rejection == "slow_client" && stalled.WaitClosed(), "Only the unapplied client is evicted with a bounded slow-client diagnostic");
        Check(server.WaitStatus(s => s.Players == 1).Ready, "Slow application eviction preserves authority and the healthy player");
        var resumed = new WorldEvent { Kind = "npc-chunk", Data = Data(new { resumed = true }) };
        worker.Send("worldEvent", resumed);
        Check(player.ReadUntil("worldEvent").Read<WorldEvent>().Data.GetProperty("resumed").GetBoolean(), "Bulk flow resumes to the healthy client after stalled recipient removal");
        Scenario("Separate control credit works under bulk saturation; stale application receipts evict only the stalled client while worker pong preserves readiness");

        player.Send("worldEventAck", new WorldEventAcknowledgement { DeliveryIds = new[] { player.LastDeliveryId + 1000 } });
        player.ExpectErrorAndClose("invalid_message");
        Check(server.WaitStatus(s => s.Players == 0).Ready, "Future receipt cannot manufacture credit or disconnect the worker");
        Scenario("Future delivery acknowledgements are rejected without disrupting the authoritative service");
    }

    private static void BinarySchedulerCreditsAndAcknowledgementRace()
    {
        using var server = new Service("binary-world-flow");
        using var player = server.Join("player", "Binary applying client", Password);
        player.ReadUntil("simulationState");
        using var worker = server.Join("worker", "Binary scheduler worker", AuthoritySecret);
        worker.ReadUntil("worldLoadComplete");
        string worldId = worker.Accepted.World.WorldId;
        byte[] checkpoint = new byte[] { 1, 6, 1, 8 };
        Upload(worker, worldId, 1, checkpoint);
        worker.Send("workerReady", new WorkerReady { WorldId = worldId, Revision = 1, Scene = "binary-test" });
        ReceiveSnapshot(player, worldId, 1, checkpoint);
        player.Send("clientReady", new WorldLoadComplete { WorldId = worldId, Revision = 1 });
        player.ReadUntil("clientReadyAccepted"); worker.ReadUntil("playerPresence");

        // FB EF BE encodes to ++++ in base64. SerializeToElement(byte[]) retains
        // those plus signs, while nesting its JsonElement in a wire envelope
        // escapes them. This is the production economy-chunk undercharge shape.
        byte[] binary = Enumerable.Range(0, 5400).Select(i => new byte[] { 0xfb, 0xef, 0xbe }[i % 3]).ToArray();
        const int chunks = 64; var scheduler = new WorldEventScheduler();
        for (int i = 0; i < chunks; i++) scheduler.Enqueue(new WorldEvent {
            Kind = "economy-chunk", Data = Data(new { index = i, count = chunks, bytes = binary }) });
        Check(scheduler.TryPeek(WorldEventFlow.BulkWindowBytes, WorldEventFlow.ControlWindowBytes, out var first), "Binary batch is schedulable within a single lane window");
        var nested = JsonSerializer.Deserialize<WorldEvent>(JsonSerializer.Serialize(first.Event, DedicatedProtocol.Json), DedicatedProtocol.Json);
        int originalBytes = Encoding.UTF8.GetByteCount(first.Event.Data.GetRawText());
        int wireBytes = Encoding.UTF8.GetByteCount(nested.Data.GetRawText());
        Check(wireBytes > originalBytes * 4 && wireBytes <= 48000, "Fixture reproduces substantial base64 escape expansion inside valid wire payload size");
        Check(first.Cost == WorldEventFlow.Cost(nested) && first.Cost >= wireBytes + 1024, "Actual scheduler charges the same bytes the service sees after nested serialization");
        Check((long)wireBytes * chunks > 2 * 1024 * 1024, "Binary regression streams more than two MiB of escaped application payload");
        int received = 0; var timer = Stopwatch.StartNew();
        while ((scheduler.Count > 0 || received < chunks) && timer.ElapsedMilliseconds < 12000)
        {
            while (worker.TrySendScheduled(scheduler)) { }
            while (player.TryReadAvailable(out var message))
            {
                if (message.Type == "error") throw new Exception("Binary credit relay rejected: " + message.Read<ProtocolError>().Code);
                if (message.Type != "worldEvent") continue;
                var value = message.Read<WorldEvent>();
                Check(value.Kind == "economy-chunk" && value.Data.GetProperty("index").GetInt32() == received++, "Escaped binary chunks retain scheduler FIFO ordering");
                Check(value.Data.GetProperty("bytes").GetBytesFromBase64().SequenceEqual(binary), "Escaped binary chunk decodes to the original bytes");
            }
            Thread.Sleep(1);
        }
        Check(scheduler.Count == 0 && received == chunks && server.Status().Ready, "Scheduler and service agree on binary credits through repeated window replenishment");
        Scenario("Production-shaped byte-array chunks preserve canonical credit cost across nested JSON escaping and drain a multi-MiB scheduler batch");

        player.AutoAcknowledgeWorldEvents = false;
        worker.Send("worldEvent", new WorldEvent { Kind = "dialogue-state", Data = Data(new { request = "ack-after-worker-loss" }) });
        var delivered = player.ReadUntil("worldEvent").Read<WorldEvent>();
        worker.Dispose();
        Check(!player.ReadUntil("simulationState").Read<SimulationState>().Ready, "Worker disconnect revokes world readiness before the delayed application receipt");
        player.Send("worldEventAck", new WorldEventAcknowledgement { DeliveryIds = new[] { delivered.DeliveryId } });
        player.Send("worldEventAck", new WorldEventAcknowledgement { DeliveryIds = new[] { delivered.DeliveryId } });
        player.Send("ping", new { });
        Check(player.ReadUntil("pong").Type == "pong", "Valid previously-issued ACK racing worker disconnect remains harmless even when repeated");
        var status = server.Status();
        Check(!status.Ready && status.Players == 1, "Late receipt does not create an invalid_message cascade or restore authority");
        player.Send("worldEventAck", new WorldEventAcknowledgement { DeliveryIds = new[] { delivered.DeliveryId + 1 } });
        player.ExpectErrorAndClose("invalid_message");
        Scenario("Previously-issued application acknowledgements tolerate worker-loss races; future receipts still fail closed while unready");
    }

    private static void WorldIsolationAndImportRefusal()
    {
        string first, second;
        using (var a = new Service("world-a")) first = a.Status().WorldId;
        using (var b = new Service("world-b")) second = b.Status().WorldId;
        Check(first != second, "Different server save roots receive different world identities");
        string refused = Path.Combine(artifacts, "player-save-refusal"); Directory.CreateDirectory(refused);
        File.WriteAllBytes(Path.Combine(refused, "unrelated-player.sav"), new byte[] { 2, 4, 6 });
        using (var service = new Service("refusal", refused, false))
        {
            Check(service.Process.WaitForExit(3000) && service.Process.ExitCode != 0, "Server refuses a root containing player saves");
            Check(!File.Exists(Path.Combine(refused, "world.json")), "Refusal never creates server metadata over player save root");
            Check(File.ReadAllBytes(Path.Combine(refused, "unrelated-player.sav")).SequenceEqual(new byte[] { 2, 4, 6 }), "Refusal preserves unrelated save bytes");
        }
        Scenario("New worlds are unique and importing a local player save root is refused without changing its saves");
    }

    private static void LocalDiscovery()
    {
        using var server = new Service("discovery", discovery: true);
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.ReceiveTimeout = 250;
        byte[] invalid = Encoding.ASCII.GetBytes("NIVALIS_DISCOVER_V0");
        udp.Send(invalid, invalid.Length, new IPEndPoint(IPAddress.Loopback, server.Port + 1));
        IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
        bool timedOut = false;
        try { udp.Receive(ref remote); } catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut) { timedOut = true; }
        Check(timedOut, "UDP discovery ignores malformed/version-mismatched request");
        udp.Client.ReceiveTimeout = 2000;
        byte[] request = Encoding.ASCII.GetBytes("NIVALIS_DISCOVER_V1");
        udp.Send(request, request.Length, new IPEndPoint(IPAddress.Loopback, server.Port + 1));
        var response = JsonSerializer.Deserialize<ServerStatus>(udp.Receive(ref remote), DedicatedProtocol.Json);
        Check(response.WorldId == server.Status().WorldId && response.Port == server.Port && !response.Ready && response.PasswordRequired,
            "LAN discovery reports same service identity, TCP port, authentication and worker readiness as TCP status");
        Check(IPAddress.IsLoopback(remote.Address) && remote.Port == server.Port + 1, "LAN discovery reply comes from configured local discovery endpoint");
        Scenario("UDP LAN discovery responds only to exact versioned query and accurately reports unready authenticated service");
    }

    private static void SignedRejectionSurvivesTransportClose()
    {
        // Exercise the actual Tick-driven client against an independent raw TCP
        // peer. The diagnostic must survive an error frame immediately followed
        // by FIN, while ordinary queued messages still disappear on disconnect.
        foreach (bool validMac in new[] { true, false })
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var connecting = DedicatedConnection.ConnectAsync("127.0.0.1", port, 3);
                using var serverSocket = listener.AcceptTcpClient();
                serverSocket.NoDelay = true;
                using var client = connecting.GetAwaiter().GetResult();
                using var stream = serverSocket.GetStream();
                string session = Guid.NewGuid().ToString("N");
                var hello = new WireMessage { Type = "serverHello", SessionId = session, Sequence = 1,
                    Payload = Data(new ServerHello { SessionId = session, Nonce = "transport-test-only" }) };
                byte[] helloFrame = RawPeer.Frame(JsonSerializer.SerializeToUtf8Bytes(hello, DedicatedProtocol.Json));
                stream.Write(helloFrame, 0, helloFrame.Length);
                WireMessage receivedHello = null;
                var deadline = Stopwatch.StartNew();
                while (deadline.ElapsedMilliseconds < 3000 && receivedHello == null)
                {
                    client.Tick();
                    if (client.TryReceive(out var message)) receivedHello = message;
                    else Thread.Sleep(2);
                }
                Check(receivedHello?.Type == "serverHello" && client.SessionId == session, "Actual transport adopts raw peer handshake before authentication");
                byte[] key = RandomNumberGenerator.GetBytes(32);
                client.SetAuthenticationKey(key);
                var ordinary = new WireMessage { Type = "pong", SessionId = session, Sequence = 2, Payload = Data(new { }) };
                var rejected = new WireMessage { Type = "error", SessionId = session, Sequence = 3,
                    Payload = Data(new ProtocolError { Code = "test_rejection", Message = "Signed rejection before FIN." }) };
                byte[] ordinaryFrame = SignedServerFrame(ordinary, key);
                byte[] rejectedFrame = SignedServerFrame(rejected, validMac ? key : RandomNumberGenerator.GetBytes(32));
                byte[] combined = ordinaryFrame.Concat(rejectedFrame).ToArray();
                stream.Write(combined, 0, combined.Length);
                serverSocket.Client.Shutdown(SocketShutdown.Send);
                deadline.Restart();
                while (client.Connected && deadline.ElapsedMilliseconds < 3000) { client.Tick(); Thread.Sleep(2); }
                Check(!client.Connected, "Actual transport observes FIN/authentication failure after queued frames");
                if (validMac)
                {
                    Check(client.Status.Contains("Remote protocol error [test_rejection]") && client.Status.Contains("Signed rejection before FIN."),
                        "Authenticated rejection code and message survive transport EOF cleanup");
                }
                else
                {
                    Check(client.Status.Contains("authentication failed", StringComparison.OrdinalIgnoreCase) && !client.Status.Contains("test_rejection"),
                        "Unauthenticated rejection cannot replace the transport authentication failure diagnostic");
                }
                Check(!client.TryReceive(out _), "Transport close clears ordinary queued messages instead of replaying stale application work");
            }
            finally { listener.Stop(); }
        }
        Scenario("Actual client preserves verified rejection diagnostics across coalesced frame/FIN, rejects forged diagnostics, and clears stale work");
    }

    private static byte[] SignedServerFrame(WireMessage message, byte[] key)
    {
        message.Mac = "";
        byte[] canonical = JsonSerializer.SerializeToUtf8Bytes(message, DedicatedProtocol.Json);
        using var hmac = new HMACSHA256(key);
        message.Mac = Convert.ToHexString(hmac.ComputeHash(Encoding.ASCII.GetBytes("server-to-client\n").Concat(canonical).ToArray()));
        return RawPeer.Frame(JsonSerializer.SerializeToUtf8Bytes(message, DedicatedProtocol.Json));
    }

    private static void ExpectJoinError(Service server, string role, string secret, string code)
    {
        using var peer = new RawPeer(server.Port);
        peer.Send("join", DedicatedProtocol.CreateJoin(peer.Hello, role, "Rejected peer", secret));
        peer.ExpectErrorAndClose(code);
    }
    private static void WaitNoWorker(Service server) => server.WaitStatus(s => !s.Ready && s.State.Contains("Waiting"));

    private static void CaptureForJoin(RawPeer worker, RawPeer joining, string worldId, ref long revision, byte[] bytes)
    {
        var request = worker.ReadUntil("checkpointRequested").Read<CheckpointRequest>();
        Upload(worker, worldId, ++revision, bytes, request.RequestId);
        ReceiveSnapshot(joining, worldId, revision, bytes);
    }

    private static void Upload(RawPeer worker, string worldId, long revision, byte[] bytes, string checkpointRequestId = "")
    {
        var begin = new SnapshotBegin { TransferId = Guid.NewGuid().ToString("N"), WorldId = worldId, Revision = revision, Length = bytes.Length, Sha256 = Hash(bytes), Format = "protocol-test-bytes", CheckpointRequestId = checkpointRequestId };
        worker.Send("snapshotBegin", begin);
        for (int offset = 0; offset < bytes.Length; offset += DedicatedProtocol.SnapshotChunkBytes)
            worker.Send("snapshotChunk", new SnapshotChunk { TransferId = begin.TransferId, Offset = offset, Data = bytes.Skip(offset).Take(DedicatedProtocol.SnapshotChunkBytes).ToArray() });
        worker.Send("snapshotEnd", new SnapshotEnd { TransferId = begin.TransferId });
        var ack = worker.ReadUntil("snapshotCommitted").Read<WorldLoadComplete>();
        Check(ack.WorldId == worldId && ack.Revision == revision, "Snapshot acknowledgement follows correct committed world/revision");
    }
    private static void ReceiveSnapshot(RawPeer peer, string worldId, long revision, byte[] expected)
    {
        var begin = peer.ReadUntil("snapshotBegin").Read<SnapshotBegin>();
        Check(begin.WorldId == worldId && begin.Revision == revision && begin.Length == expected.Length, "Snapshot offer matches world, revision and exact length");
        using var output = new MemoryStream();
        for (;;)
        {
            var message = peer.Read();
            if (message.Type == "snapshotEnd") { Check(message.Read<SnapshotEnd>().TransferId == begin.TransferId, "Snapshot end identifies offered transfer"); break; }
            Check(message.Type == "snapshotChunk", "Snapshot stream contains only chunks before end");
            var chunk = message.Read<SnapshotChunk>();
            Check(chunk.TransferId == begin.TransferId && chunk.Offset == output.Length && chunk.Data.Length <= DedicatedProtocol.SnapshotChunkBytes, "Snapshot chunks are ordered, contiguous and bounded");
            output.Write(chunk.Data);
        }
        var loaded = peer.ReadUntil("worldLoadComplete").Read<WorldLoadComplete>();
        Check(loaded.WorldId == worldId && loaded.Revision == revision, "World load marker follows stream end");
        byte[] actual = output.ToArray();
        Check(actual.Length == begin.Length && Hash(actual) == begin.Sha256 && actual.SequenceEqual(expected), "Transferred bytes match offered length, hash and original fixture");
    }

    private sealed class Service : IDisposable
    {
        public readonly int Port;
        public readonly string SaveRoot;
        public readonly Process Process;
        private readonly string logPath;
        private readonly StringBuilder output = new StringBuilder();
        public Service(string name, string saveRoot = null, bool wait = true, bool discovery = false)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); Port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            if (Port >= 65535) Port = 49001;
            SaveRoot = saveRoot ?? Path.Combine(artifacts, name + "-world");
            logPath = Path.Combine(artifacts, name + "-service.log");
            var start = new ProcessStartInfo(dotnet) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { serviceAssembly, "--bind", "127.0.0.1", "--port", Port.ToString(), "--name", "Protocol test service", "--max-players", "4", "--save-root", SaveRoot, "--password", Password, "--authority-secret", AuthoritySecret,
                "--stop-file", Path.Combine(SaveRoot, "stop-service.request") }) start.ArgumentList.Add(arg);
            if (!discovery) start.ArgumentList.Add("--no-discovery");
            Process = new Process { StartInfo = start };
            Process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
            Process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
            Process.Start(); Process.BeginOutputReadLine(); Process.BeginErrorReadLine();
            if (wait)
            {
                var timer = Stopwatch.StartNew();
                while (timer.ElapsedMilliseconds < 5000)
                {
                    if (Process.HasExited) throw new Exception("Service exited early: " + Logs());
                    try { Status(); return; } catch (SocketException) { Thread.Sleep(25); }
                }
                Dispose(); throw new Exception("Service did not start: " + Logs());
            }
        }
        public ServerStatus Status()
        { using var query = new RawPeer(Port); query.Send("statusQuery", new { }); return query.ReadUntil("serverStatus").Read<ServerStatus>(); }
        public ServerStatus WaitStatus(Func<ServerStatus, bool> predicate)
        {
            var timer = Stopwatch.StartNew();
            do { var status = Status(); if (predicate(status)) return status; Thread.Sleep(20); } while (timer.ElapsedMilliseconds < 3000);
            throw new Exception("Expected server status was not reached.");
        }
        public RawPeer Join(string role, string name, string secret)
        {
            var peer = new RawPeer(Port);
            var request = DedicatedProtocol.CreateJoin(peer.Hello, role, name, secret);
            peer.Send("join", request); peer.Key = DedicatedProtocol.DeriveSessionKey(peer.Hello, request, secret);
            peer.Accepted = peer.ReadUntil("joinAccepted").Read<JoinAccepted>();
            Check(peer.Accepted.Role == role, "Accepted role is exact requested authorized role");
            return peer;
        }
        private string Logs() { lock (output) return output.ToString(); }
        public bool StopGracefully()
        {
            if (Process.HasExited) return Process.ExitCode == 0;
            string marker = Path.Combine(SaveRoot, "stop-service.request");
            File.WriteAllText(marker, "protocol-test-owned service stop");
            return Process.WaitForExit(3000) && Process.ExitCode == 0 && !File.Exists(marker);
        }
        public void Dispose()
        {
            if (!Process.HasExited && !StopGracefully()) Process.Kill(true);
            Process.WaitForExit(3000);
            File.WriteAllText(logPath, Logs());
            Process.Dispose();
        }
    }

    // Independent, blocking TCP implementation. It deliberately does not use DedicatedConnection,
    // so tests cover its peer service wire behavior rather than replaying the same channel code.
    private sealed class RawPeer : IDisposable
    {
        private readonly TcpClient socket;
        private readonly NetworkStream stream;
        private ulong outbound, inbound;
        public readonly ServerHello Hello;
        public byte[] Key;
        public JoinAccepted Accepted;
        public bool AutoAcknowledgeWorldEvents = true;
        public long LastDeliveryId;
        private int bulkCredit, controlCredit;
        private readonly Queue<WireMessage> pending = new Queue<WireMessage>();
        private bool disposed;
        public RawPeer(int port)
        {
            socket = new TcpClient(); socket.Connect(IPAddress.Loopback, port); socket.NoDelay = true;
            stream = socket.GetStream(); stream.ReadTimeout = 3000; stream.WriteTimeout = 3000;
            var message = ReadEnvelope();
            Check(message.Type == "serverHello", "First server frame is handshake");
            Hello = message.Read<ServerHello>();
            Check(Hello.SessionId == message.SessionId && Hello.Version == DedicatedProtocol.Version, "Handshake identifies matching envelope session and protocol");
        }
        public WireMessage Envelope<T>(string type, T payload)
        {
            var message = new WireMessage { Type = type, Payload = Data(payload), SessionId = Hello.SessionId, Sequence = ++outbound };
            if (Key != null) message.Mac = Sign(message, false);
            return message;
        }
        public void Send<T>(string type, T payload)
        {
            if (type == "worldEvent" && Accepted?.Role == "worker" && payload is WorldEvent value)
            {
                var timeout = Stopwatch.StartNew();
                while (!TrySendWorldEvent(value))
                {
                    if (timeout.ElapsedMilliseconds > 5000) throw new TimeoutException("Worker did not receive world-event credit.");
                    Thread.Sleep(2);
                }
                return;
            }
            SendUnchecked(type, payload);
        }
        public void SendUnchecked<T>(string type, T payload) => WriteBytes(Frame(JsonSerializer.SerializeToUtf8Bytes(Envelope(type, payload), DedicatedProtocol.Json)));
        public bool TrySendWorldEvent(WorldEvent value)
        {
            Check(Accepted?.Role == "worker", "Only the synthetic authority consumes worker credit");
            PumpMaintenance();
            int cost = WorldEventFlow.Cost(value); bool control = WorldEventFlow.IsControlKind(value.Kind);
            if (cost > (control ? controlCredit : bulkCredit)) return false;
            if (control) controlCredit -= cost; else bulkCredit -= cost;
            SendUnchecked("worldEvent", value); return true;
        }
        public bool TrySendScheduled(WorldEventScheduler scheduler)
        {
            PumpMaintenance();
            if (!scheduler.TryPeek(bulkCredit, controlCredit, out var entry)) return false;
            if (WorldEventFlow.IsControlKind(entry.Event.Kind)) controlCredit -= entry.Cost; else bulkCredit -= entry.Cost;
            SendUnchecked("worldEvent", entry.Event); scheduler.Remove(entry); return true;
        }
        public void PumpMaintenance()
        {
            while (socket.Available > 0)
            {
                var message = ReadEnvelope();
                if (!Maintenance(message)) pending.Enqueue(message);
            }
        }
        private bool Maintenance(WireMessage message)
        {
            if (message.Type == "ping") { SendUnchecked("pong", new { }); return true; }
            if (message.Type != "worldFlowCredit") return false;
            Check(Accepted?.Role == "worker", "Only the worker receives delivery credit");
            var credit = message.Read<WorldFlowCredit>();
            Check(credit.BulkBytes >= 0 && credit.ControlBytes >= 0 && (credit.BulkBytes != 0 || credit.ControlBytes != 0), "Credit grants are positive additive lane allowances");
            bulkCredit = checked(bulkCredit + credit.BulkBytes); controlCredit = checked(controlCredit + credit.ControlBytes);
            Check(bulkCredit <= WorldEventFlow.BulkWindowBytes && controlCredit <= WorldEventFlow.ControlWindowBytes, "Unspent worker credit stays within bounded lane windows");
            return true;
        }
        private WireMessage Consumed(WireMessage message)
        {
            if (message.Type == "worldEvent" && Accepted?.Role == "player")
            {
                var value = message.Read<WorldEvent>();
                Check(value.DeliveryId > LastDeliveryId, "Recipient delivery IDs are positive and strictly increasing");
                LastDeliveryId = value.DeliveryId;
                if (AutoAcknowledgeWorldEvents) SendUnchecked("worldEventAck", new WorldEventAcknowledgement { DeliveryIds = new[] { value.DeliveryId } });
            }
            return message;
        }
        public bool TryReadAvailable(out WireMessage result)
        {
            for (;;)
            {
                if (pending.Count > 0) { result = Consumed(pending.Dequeue()); return true; }
                if (socket.Available == 0) { result = null; return false; }
                var message = ReadEnvelope();
                if (!Maintenance(message)) { result = Consumed(message); return true; }
            }
        }
        public void SendWrongDirection(string type, bool directionless)
        {
            var message = Envelope(type, new { }); message.Mac = "";
            if (directionless)
            {
                using var hmac = new HMACSHA256(Key);
                message.Mac = Convert.ToHexString(hmac.ComputeHash(JsonSerializer.SerializeToUtf8Bytes(message, DedicatedProtocol.Json)));
            }
            else message.Mac = Sign(message, true);
            WriteBytes(Frame(JsonSerializer.SerializeToUtf8Bytes(message, DedicatedProtocol.Json)));
        }
        public void WriteBytes(byte[] bytes) => stream.Write(bytes, 0, bytes.Length);
        public static byte[] Frame(byte[] body)
        {
            byte[] framed = new byte[4 + body.Length]; int size = body.Length;
            framed[0] = (byte)(size >> 24); framed[1] = (byte)(size >> 16); framed[2] = (byte)(size >> 8); framed[3] = (byte)size;
            Buffer.BlockCopy(body, 0, framed, 4, size); return framed;
        }
        public WireMessage Read()
        {
            for (;;)
            {
                if (pending.Count > 0) return Consumed(pending.Dequeue());
                var message = ReadEnvelope();
                if (Maintenance(message)) continue;
                return Consumed(message);
            }
        }
        private WireMessage ReadEnvelope()
        {
            byte[] header = ReadBytes(4); int length = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
            Check(length >= 2 && length <= DedicatedProtocol.MaxFrameBytes, "Server response frame obeys length bound");
            var message = JsonSerializer.Deserialize<WireMessage>(ReadBytes(length), DedicatedProtocol.Json);
            Check(message.Sequence == ++inbound, "Server response sequence strictly advances");
            if (Hello != null) Check(message.SessionId == Hello.SessionId, "Server response stays bound to handshake session");
            if (Key != null)
            {
                string mac = message.Mac; message.Mac = "";
                Check(DedicatedProtocol.EqualHex(mac, Sign(message, true)), "Authenticated server response has valid directional MAC");
                message.Mac = mac;
            }
            return message;
        }
        private string Sign(WireMessage message, bool serverToClient)
        {
            using var hmac = new HMACSHA256(Key);
            byte[] prefix = Encoding.ASCII.GetBytes(serverToClient ? "server-to-client\n" : "client-to-server\n");
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(message, DedicatedProtocol.Json);
            return Convert.ToHexString(hmac.ComputeHash(prefix.Concat(json).ToArray()));
        }
        private byte[] ReadBytes(int count)
        {
            byte[] bytes = new byte[count]; int read = 0;
            while (read < count) { int n = stream.Read(bytes, read, count - read); if (n == 0) throw new EndOfStreamException(); read += n; }
            return bytes;
        }
        public WireMessage ReadUntil(string type)
        {
            for (int i = 0; i < 100; i++)
            {
                var message = Read(); if (message.Type == type) return message;
                if (message.Type == "error") throw new Exception("Unexpected service error waiting for " + type + ": " + message.Read<ProtocolError>().Code);
            }
            throw new Exception("Did not receive " + type);
        }
        public bool HasMessage(string type, int milliseconds)
        {
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < milliseconds)
            {
                if (TryReadAvailable(out var message)) { if (message.Type == type) return true; }
                else Thread.Sleep(10);
            }
            return false;
        }
        public void ExpectErrorAndClose(string code)
        {
            Check(ReadUntil("error").Read<ProtocolError>().Code == code, "Reject code is " + code);
            Check(WaitClosed(), "Rejected peer socket is closed");
        }
        public bool WaitClosed()
        {
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < 3000)
            {
                if (socket.Client.Poll(10000, SelectMode.SelectRead))
                {
                    if (socket.Available == 0) return true;
                    try { Read(); } catch (EndOfStreamException) { return true; } catch (IOException) { return true; }
                }
            }
            return false;
        }
        public void Dispose() { if (disposed) return; disposed = true; stream.Dispose(); socket.Dispose(); }
    }
}
