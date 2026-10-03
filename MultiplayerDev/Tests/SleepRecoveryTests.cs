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

// Isolated authenticated raw TCP protocol tests. Synthetic worker events do not
// simulate Unity, prove bed reachability, or prove native sleep completion.
internal static partial class SleepRecoveryTests
{
    private const string Password = "test-password-only";
    private const string AuthoritySecret = "test-authority-secret-not-production";
    private static string serviceAssembly, dotnet, artifacts;
    private static int assertions;
    private static readonly List<string> scenarios = new List<string>();
    private static readonly byte[] checkpoint = { 1, 2, 3, 5, 8, 13 };

    public static int Main(string[] args)
    {
        if (args.Length != 3) { Console.Error.WriteLine("Usage: SleepRecoveryTests.dll SERVICE_DLL DOTNET_EXE ARTIFACT_DIRECTORY"); return 2; }
        serviceAssembly = Path.GetFullPath(args[0]); dotnet = args[1]; artifacts = Path.GetFullPath(args[2]); Directory.CreateDirectory(artifacts);
        try
        {
            RecoveryChoicesAndSnapshotBarrier();
            AllParticipantsLeaveAndRejoin();
            RecoveryCreditAndAcknowledgements();
            InvalidAcknowledgementBatch();
            WorkerPayloadValidation();
            string result = "PASS: " + assertions + " assertions in " + scenarios.Count + " scenarios. Isolated service, raw authenticated TCP clients, synthetic worker PROTOCOL ONLY; no game process controlled.";
            Console.WriteLine(result); File.WriteAllText(Path.Combine(artifacts, "result.txt"), result + Environment.NewLine + string.Join(Environment.NewLine, scenarios)); return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex); File.WriteAllText(Path.Combine(artifacts, "result.txt"), "FAIL after " + assertions + " assertions." + Environment.NewLine + ex); return 1;
        }
    }
    private static void Check(bool value, string message) { assertions++; if (!value) throw new Exception("FAIL: " + message); }
    private static void Scenario(string name) { scenarios.Add(name); Console.WriteLine("PASS: " + name); }
    private static JsonElement Data<T>(T value) => JsonSerializer.SerializeToElement(value, DedicatedProtocol.Json);
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private sealed class Choice
    {
        public string Action { get; set; } = "next";
        public string Label { get; set; } = "Continue";
        public float Percentage { get; set; }
    }
    private sealed class SleepState
    {
        public long Sequence { get; set; } = 1;
        public string Epoch { get; set; } = Guid.NewGuid().ToString("N");
        public long Step { get; set; } = 1;
        public string Phase { get; set; } = "summary";
        public string Owner { get; set; } = "";
        public bool RecoveryOwner { get; set; } = true;
        public string TargetPlayer { get; set; } = "";
        public string RequestId { get; set; } = "";
        public string Scene { get; set; } = "sleep-recovery-test";
        public string Title { get; set; } = "Shared day transition";
        public string Text { get; set; } = "Review the summary and explicitly continue.";
        public int GameSeconds { get; set; } = 93721;
        public bool Curfew { get; set; } = true;
        public Choice[] Choices { get; set; } = { new Choice() };
    }
    private static RawPeer ReadyWorker(Service server)
    {
        var worker = server.Join("worker", "Synthetic sleep authority", AuthoritySecret);
        worker.ReadUntil("worldLoadComplete"); Upload(worker, worker.Accepted.World.WorldId, 1, checkpoint);
        worker.Send("workerReady", new WorkerReady { WorldId = worker.Accepted.World.WorldId, Revision = 1, Scene = "sleep-recovery-test" });
        Check(server.WaitStatus(s => s.Ready).Ready, "Only committed worker readiness starts the fixture"); return worker;
    }
    private static string PendingArrival(RawPeer worker, RawPeer pending)
    {
        string requestId = null; PlayerPresence presence = null;
        while (requestId == null || presence == null)
        {
            var message = worker.Read();
            if (message.Type == "checkpointRequested") requestId = message.Read<CheckpointRequest>().RequestId;
            else if (message.Type == "sleepRecoveryPresence") presence = message.Read<PlayerPresence>();
            else if (message.Type == "error") throw new Exception("Worker rejected awaiting recovery presence: " + message.Read<ProtocolError>().Code);
        }
        Check(presence.Joined && presence.PlayerId == pending.Accepted.PlayerId, "Authenticated waiting join is announced only as sleep recovery presence");
        Check(Guid.TryParseExact(requestId, "N", out _), "Waiting join still requests a fresh correlated checkpoint"); return requestId;
    }
    private static WorldEvent Event(SleepState state) => new WorldEvent { Kind = "group-sleep", Data = Data(state) };
    private static void Publish(RawPeer worker, SleepState state, params RawPeer[] recipients)
    {
        worker.Send("worldEvent", Event(state));
        foreach (var recipient in recipients)
        {
            var delivered = recipient.ReadUntil("worldEvent").Read<WorldEvent>();
            Check(delivered.Kind == "group-sleep" && delivered.WorldId == worker.Accepted.World.WorldId && delivered.Revision >= 1,
                "Pending recovery receives authoritative world identity through ordinary worldEvent");
            Check(delivered.Data.GetProperty("Sequence").GetInt64() == state.Sequence && delivered.Data.GetProperty("Epoch").GetString() == state.Epoch,
                "Recovery delivery preserves exact issued state and sequence");
        }
    }
    private static InteractionRequest Intent(SleepState state, string action = "next", float percentage = 0, string requestId = null)
        => new InteractionRequest { PlayerId = "forged-player-id", RequestId = requestId ?? Guid.NewGuid().ToString("N"), Kind = "group-sleep-action", TargetId = state.Epoch,
            Data = Data(new { Epoch = state.Epoch, Step = state.Step, Action = action, Percentage = percentage }) };
    private static void RejectedChoice(RawPeer peer, RawPeer worker, InteractionRequest request, string reason)
    {
        peer.Send("interaction", request);
        Check(peer.ReadUntil("sleepRecoveryRejected").Read<ProtocolError>().Code == "sleep_choice_rejected", reason + " receives a nonterminal recovery rejection");
        Check(!worker.HasMessage("interaction", 40), reason + " never reaches the authoritative worker"); Thread.Sleep(110);
    }
    private static void BarrierRejected(RawPeer peer, RawPeer worker, string type, object value, string reason)
    {
        peer.Send(type, value); Check(peer.ReadUntil("error").Read<ProtocolError>().Code == "world_not_ready", reason + " retains the load barrier");
        Check(!worker.HasMessage(type, 40), reason + " is never forwarded"); Thread.Sleep(110);
    }
    private static void RecoveryChoicesAndSnapshotBarrier()
    {
        using var server = new Service("choices-and-barrier"); using var worker = ReadyWorker(server);
        using var first = server.Join("player", "Pending first", Password); PendingArrival(worker, first);
        var state = new SleepState { Owner = first.Accepted.PlayerId };
        Check(!first.HasMessage("snapshotBegin", 100), "Pending join never receives the old saved checkpoint");
        RejectedChoice(first, worker, Intent(state), "Unissued choice");
        BarrierRejected(first, worker, "playerPose", new PlayerPose { Scene = state.Scene, Active = true }, "Pending pose");
        BarrierRejected(first, worker, "interaction", new InteractionRequest { Kind = "group-sleep-request", TargetId = "bed:invented", RequestId = "bed-before-load", Data = Data(new { }) }, "Pending new bed intent");
        BarrierRejected(first, worker, "interaction", new InteractionRequest { Kind = "buy", TargetId = "shop:invented", RequestId = "buy-before-load", Data = Data(new { Quantity = 1 }) }, "Pending ordinary gameplay");
        foreach (string kind in new[] { "timeWeather", "inventory-chunk", "npc-chunk", "group-travel", "dialogue-state" })
            worker.Send("worldEvent", new WorldEvent { Kind = kind, Data = Data(new { Phase = "complete", test = kind }) });
        Check(!first.HasMessage("worldEvent", 150), "Unloaded peer sees no weather, inventory, NPC, travel or dialogue world events");
        Publish(worker, state, first);
        state.Sequence++; state.RecoveryOwner = false; Publish(worker, state, first);
        RejectedChoice(first, worker, Intent(state), "Pending identity without explicit recovery ownership");
        state.Sequence++; state.RecoveryOwner = true; Publish(worker, state, first);
        var good = Intent(state, requestId: "repeatable-request"); first.Send("interaction", good);
        var forwarded = worker.ReadUntil("interaction").Read<InteractionRequest>();
        Check(forwarded.PlayerId == first.Accepted.PlayerId && forwarded.RequestId == good.RequestId && forwarded.TargetId == state.Epoch,
            "Issued choice is forwarded with authenticated identity, original request ID and epoch");
        first.Send("interaction", good); var repeated = worker.ReadUntil("interaction").Read<InteractionRequest>();
        Check(repeated.RequestId == forwarded.RequestId && repeated.PlayerId == forwarded.PlayerId, "Transport preserves repeat request identity for the worker's native-call dedupe"); Thread.Sleep(120);
        RejectedChoice(first, worker, Intent(state, "debt", 50), "Unoffered action");
        RejectedChoice(first, worker, Intent(state, percentage: 1), "Unoffered percentage");
        var wrongTarget = Intent(state); wrongTarget.TargetId = Guid.NewGuid().ToString("N"); RejectedChoice(first, worker, wrongTarget, "Forged target epoch");
        var stale = Intent(state); state.Sequence++; state.Step++; state.Phase = "debt";
        state.Choices = new[] { new Choice { Action = "debt", Label = "Pay nothing now", Percentage = 0 }, new Choice { Action = "debt", Label = "Pay 25%", Percentage = 25 } };
        Publish(worker, state, first); RejectedChoice(first, worker, stale, "Old issued step replay");
        RejectedChoice(first, worker, Intent(state, "debt", 25.0001f), "Nearly matching unoffered native percentage");
        first.Send("interaction", Intent(state, "debt", 25)); forwarded = worker.ReadUntil("interaction").Read<InteractionRequest>();
        Check(forwarded.PlayerId == first.Accepted.PlayerId && forwarded.Data.GetProperty("Percentage").GetSingle() == 25, "Only the exact offered debt percentage reaches worker");
        Scenario("Pending recovery whitelist, authenticated exact choices, worker dedupe identity and stale/forged/unoffered action rejection");

        using var second = server.Join("player", "Pending second", Password); string latestCheckpoint = PendingArrival(worker, second);
        // A fresh current publication gives late arrivals state without granting ownership.
        state.Sequence++; Publish(worker, state, first, second);
        RejectedChoice(second, worker, Intent(state, "debt", 25), "Non-owner late join");
        state.Sequence++; state.TargetPlayer = first.Accepted.PlayerId; state.RequestId = "targeted-notice"; state.Phase = "accepted"; state.Choices = Array.Empty<Choice>();
        Publish(worker, state, first); Check(!second.HasMessage("worldEvent", 100), "Targeted accepted response is not leaked to another pending peer");
        first.Send("interaction", Intent(state, "debt", 25));
        Check(worker.ReadUntil("interaction").Read<InteractionRequest>().PlayerId == first.Accepted.PlayerId, "A targeted result does not replace the still-issued native choice state");
        state.Sequence++; state.TargetPlayer = ""; state.RequestId = ""; state.Phase = "ranking"; state.Choices = new[] { new Choice { Action = "ranking" } }; state.Step++;
        Publish(worker, state, first, second);
        first.Dispose(); Check(server.WaitStatus(s => s.Players == 1).Ready, "Pending owner departure keeps the world ready");
        var gone = worker.ReadUntil("sleepRecoveryPresence").Read<PlayerPresence>();
        Check(!gone.Joined && gone.PlayerId == state.Owner, "Departing pending owner is removed from recovery candidates");
        state.Owner = second.Accepted.PlayerId; state.Sequence++; state.Step++;
        Publish(worker, state, second); second.Send("interaction", Intent(state, "ranking"));
        Check(worker.ReadUntil("interaction").Read<InteractionRequest>().PlayerId == second.Accepted.PlayerId, "A newly worker-issued owner can explicitly resolve the pending choice");
        Scenario("Late join read-only state, private responses, owner departure presence and worker-issued reassignment");

        state.Phase = "complete"; state.Sequence++; state.Step++; state.Choices = Array.Empty<Choice>(); Publish(worker, state, second);
        RejectedChoice(second, worker, Intent(state, "ranking"), "Choice after completion");
        Check(!second.HasMessage("snapshotBegin", 100), "Completion alone cannot release the checkpoint barrier");
        Upload(worker, worker.Accepted.World.WorldId, 2, checkpoint, Guid.NewGuid().ToString("N"));
        Check(!second.HasMessage("snapshotBegin", 100), "Wrong correlation after recovery cannot release waiting join");
        Upload(worker, worker.Accepted.World.WorldId, 3, checkpoint, latestCheckpoint);
        ReceiveSnapshot(second, worker.Accepted.World.WorldId, 3, checkpoint);
        var released = worker.ReadUntil("sleepRecoveryPresence").Read<PlayerPresence>();
        Check(!released.Joined && released.PlayerId == second.Accepted.PlayerId, "Snapshot start removes the recovery candidate before gameplay load");
        BarrierRejected(second, worker, "interaction", new InteractionRequest { Kind = "buy", TargetId = "shop", RequestId = "transferred-not-loaded", Data = Data(new { }) }, "Transferred snapshot without load ACK");
        second.Send("clientReady", new WorldLoadComplete { WorldId = worker.Accepted.World.WorldId, Revision = 3 }); second.ReadUntil("clientReadyAccepted");
        var joined = worker.ReadUntil("playerPresence").Read<PlayerPresence>();
        Check(joined.Joined && joined.PlayerId == second.Accepted.PlayerId, "Ordinary player arrival occurs only after exact clientReady barrier");
        worker.Send("worldEvent", new WorldEvent { Kind = "timeWeather", Data = Data(new { test = "loaded" }) });
        Check(second.ReadUntil("worldEvent").Read<WorldEvent>().Kind == "timeWeather", "Ordinary world events resume after actual load acceptance");
        Scenario("Recovery completion still requires fresh correlated snapshot, recovery removal and explicit clientReady barrier");
    }
    private static void AllParticipantsLeaveAndRejoin()
    {
        using var server = new Service("zero-player-rejoin"); using var worker = ReadyWorker(server);
        using var departed = server.Join("player", "Leaving recovery owner", Password); PendingArrival(worker, departed);
        var state = new SleepState { Owner = departed.Accepted.PlayerId }; Publish(worker, state, departed);
        departed.Dispose(); Check(server.WaitStatus(s => s.Players == 0).Ready, "The worker stays ready when every participant leaves during a pending choice");
        var gone = worker.ReadUntil("sleepRecoveryPresence").Read<PlayerPresence>();
        Check(!gone.Joined && gone.PlayerId == state.Owner, "Last participant departure revokes recovery membership");
        using var rejoined = server.Join("player", "New recovery participant", Password); PendingArrival(worker, rejoined);
        var priorOwnerIntent = Intent(state); state.Sequence++; state.Step++; state.Owner = rejoined.Accepted.PlayerId;
        Publish(worker, state, rejoined);
        RejectedChoice(rejoined, worker, priorOwnerIntent, "Rejoined identity replaying a departed owner's step");
        rejoined.Send("interaction", Intent(state)); var forwarded = worker.ReadUntil("interaction").Read<InteractionRequest>();
        Check(forwarded.PlayerId == rejoined.Accepted.PlayerId && forwarded.Data.GetProperty("Step").GetInt64() == state.Step,
            "A new authenticated pending participant can answer newly issued ownership without loading an old snapshot");
        Check(!rejoined.HasMessage("snapshotBegin", 100), "Recovery choice does not itself load stale world bytes");
        Scenario("All-player departure and a new pending participant's explicitly reassigned recovery choice");
    }
    private static void RecoveryCreditAndAcknowledgements()
    {
        using var server = new Service("recovery-credit"); using var worker = ReadyWorker(server);
        using var pending = server.Join("player", "Credit test pending", Password); PendingArrival(worker, pending); pending.AutoAcknowledgeWorldEvents = false;
        var state = new SleepState { Owner = pending.Accepted.PlayerId }; int cost = WorldEventFlow.Cost(Event(state));
        Check(cost == 2048 && WorldEventFlow.IsControlKind("group-sleep"), "Small recovery states consume the reserved control lane minimum");
        int count = WorldEventFlow.ControlWindowBytes / cost;
        for (int i = 0; i < count; i++) { state.Sequence = i + 1; state.Step = i + 1; Publish(worker, state, pending); }
        state.Sequence++; state.Step++; Check(!worker.TrySendWorldEvent(Event(state)), "Unacknowledged pending recovery fills exactly one bounded control window");
        var bulk = new WorldEvent { Kind = "inventory-chunk", Data = Data(new { content = new string('x', 30000) }) };
        Check(worker.TrySendWorldEvent(bulk), "Pending recovery does not consume or block the separate bulk credit lane");
        Check(!pending.HasMessage("worldEvent", 80), "Available bulk credit never leaks an inventory event through the pending barrier");
        long receipt = pending.LastDeliveryId;
        pending.Send("worldEventAck", new WorldEventAcknowledgement { DeliveryIds = new[] { receipt } });
        Publish(worker, state, pending);
        pending.Send("worldEventAck", new WorldEventAcknowledgement { DeliveryIds = new[] { receipt } });
        pending.Send("worldEventAck", new WorldEventAcknowledgement { DeliveryIds = new[] { receipt } });
        pending.Send("ping", new { }); pending.ReadUntil("pong"); Thread.Sleep(50);
        state.Sequence++; state.Step++; Check(!worker.TrySendWorldEvent(Event(state)), "Duplicate past ACK cannot manufacture replacement recovery credit");
        pending.Send("worldEventAck", new WorldEventAcknowledgement { DeliveryIds = new[] { pending.LastDeliveryId + 1 } }); pending.ExpectErrorAndClose("invalid_message");
        Check(server.WaitStatus(s => s.Players == 0).Ready, "Future pending ACK rejects only its player and preserves authoritative readiness");
        Scenario("Pending delivery IDs and ACKs conserve control credit, isolate bulk, and reject credit forgery");
    }
    private static void InvalidAcknowledgementBatch()
    {
        using var server = new Service("invalid-ack-batch"); using var worker = ReadyWorker(server);
        using var pending = server.Join("player", "Duplicate ACK batch", Password); PendingArrival(worker, pending); pending.AutoAcknowledgeWorldEvents = false;
        Publish(worker, new SleepState { Owner = pending.Accepted.PlayerId }, pending);
        pending.Send("worldEventAck", new WorldEventAcknowledgement { DeliveryIds = new[] { pending.LastDeliveryId, pending.LastDeliveryId } }); pending.ExpectErrorAndClose("invalid_message");
        Check(server.WaitStatus(s => s.Players == 0).Ready, "Duplicate IDs in one recovery ACK batch reject only their sender");
        Scenario("Pending recovery uses the same whole-batch ACK validation as loaded recipients");
    }
    private static JsonElement Padded(SleepState state, int exactBytes)
    {
        string json = JsonSerializer.Serialize(state, DedicatedProtocol.Json); string prefix = json.Substring(0, json.Length - 1) + ",\"Padding\":\"";
        int pad = exactBytes - Encoding.UTF8.GetByteCount(prefix + "\"}"); if (pad < 0) throw new ArgumentException("Bound fixture too small.");
        using var document = JsonDocument.Parse(prefix + new string('x', pad) + "\"}"); return document.RootElement.Clone();
    }
    private static void WorkerPayloadValidation()
    {
        var state = new SleepState { Owner = Guid.NewGuid().ToString("N") };
        Check(WorldEventFlow.Cost(new WorldEvent { Kind = "group-sleep", Data = Padded(state, 24000) }) == 25024, "Exactly 24,000 payload bytes obey the sleep control bound");
        bool rejected = false; try { WorldEventFlow.Cost(new WorldEvent { Kind = "group-sleep", Data = Padded(state, 24001) }); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "24,001-byte sleep control is locally rejected before queueing");
        using (var server = new Service("oversized-recovery"))
        using (var worker = ReadyWorker(server))
        using (var pending = server.Join("player", "Oversize pending", Password))
        {
            PendingArrival(worker, pending); state.Owner = pending.Accepted.PlayerId;
            worker.SendUnchecked("worldEvent", new WorldEvent { Kind = "group-sleep", Data = Padded(state, 24001) }); worker.ExpectErrorAndClose("invalid_message");
            Check(!pending.HasMessage("worldEvent", 100), "Oversized worker recovery data is never delivered to pending clients");
            Check(!server.WaitStatus(s => !s.Ready).Ready, "Invalid worker recovery fails authority closed");
        }
        using (var server = new Service("malformed-recovery"))
        using (var worker = ReadyWorker(server))
        using (var pending = server.Join("player", "Malformed pending", Password))
        {
            PendingArrival(worker, pending); state.Owner = pending.Accepted.PlayerId; state.Epoch = "forged-invalid-epoch";
            worker.Send("worldEvent", Event(state)); worker.ExpectErrorAndClose("invalid_message");
            Check(!pending.HasMessage("worldEvent", 100), "Structurally invalid recovery cannot create an issued pending choice");
        }
        Scenario("24 KiB sleep payload bound and malformed worker state reject before any pending delivery");
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
