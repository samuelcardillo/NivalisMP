using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using NivalisTogether;
using NivalisTogether.Dedicated;

// Pure transfer-helper verification. No Unity/game state is loaded or simulated.
internal static class WorldSnapshotTransferTests
{
    private static int checks;
    private static readonly string World = Guid.NewGuid().ToString("N");
    public static int Main()
    {
        try
        {
            ReceiveValidation(); BackpressureRoundTrip();
            Console.WriteLine("PASS: " + checks + " pure snapshot transfer assertions (validation, exact bytes/hash, reset, bounded queue and backpressure recovery). No game simulation.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    private static void Invalid(Action action, string message)
    { try { action(); } catch (InvalidDataException) { Check(true, message); return; } throw new Exception("Expected InvalidDataException: " + message); }
    private static SnapshotBegin Begin(byte[] bytes)
        => new SnapshotBegin { TransferId = Guid.NewGuid().ToString("N"), WorldId = World, Revision = 7,
            Length = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), Format = "nivalis-es2-v1" };
    private static void ReceiveValidation()
    {
        var transfer = new WorldSnapshotTransfer(); byte[] bytes = { 11, 22, 33, 44 }; var begin = Begin(bytes);
        Invalid(() => transfer.BeginReceive(null, World), "Null header rejected");
        Invalid(() => transfer.BeginReceive(begin, Guid.NewGuid().ToString("N")), "Wrong world rejected");
        var bad = Begin(bytes); bad.Format = "unknown"; Invalid(() => transfer.BeginReceive(bad, World), "Wrong native-save format rejected");
        bad = Begin(bytes); bad.Length = DedicatedProtocol.MaxSnapshotBytes + 1; Invalid(() => transfer.BeginReceive(bad, World), "Snapshot bound enforced before allocation");
        bad = Begin(bytes); bad.TransferId = "../file"; Invalid(() => transfer.BeginReceive(bad, World), "Non-GUID transfer rejected");
        transfer.BeginReceive(begin, World);
        Invalid(() => transfer.ReceiveChunk(new SnapshotChunk { TransferId = begin.TransferId, Offset = 1, Data = bytes }), "Noncontiguous chunk rejected");
        Invalid(() => transfer.ReceiveChunk(new SnapshotChunk { TransferId = begin.TransferId, Data = new byte[5] }), "Overlong transfer rejected");
        Invalid(() => transfer.EndReceive(new SnapshotEnd { TransferId = begin.TransferId }), "Truncated end rejected");
        transfer.ReceiveChunk(new SnapshotChunk { TransferId = begin.TransferId, Offset = 0, Data = bytes });
        transfer.EndReceive(new SnapshotEnd { TransferId = begin.TransferId });
        Check(transfer.Completed.Revision == 7 && transfer.CompletedBytes.SequenceEqual(bytes), "Valid receive commits exact native bytes and revision");
        transfer.Reset(); Check(transfer.Completed == null && transfer.CompletedBytes == null && !transfer.IsSending && transfer.LastSentRevision == 0, "Reset releases transfer state");
        bad = Begin(bytes); bad.Sha256 = new string('0', 64); transfer.BeginReceive(bad, World);
        transfer.ReceiveChunk(new SnapshotChunk { TransferId = bad.TransferId, Data = bytes });
        Invalid(() => transfer.EndReceive(new SnapshotEnd { TransferId = bad.TransferId }), "Checksum mismatch rejected");
        Check(transfer.Completed == null && transfer.CompletedBytes == null, "Invalid bytes never become loadable Completed state");
        transfer.Reset();
    }
    private static void BackpressureRoundTrip()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var connecting = DedicatedConnection.ConnectAsync("127.0.0.1", port);
        using var receiver = DedicatedConnection.FromAccepted(listener.AcceptTcpClient());
        using var sender = connecting.GetAwaiter().GetResult(); listener.Stop();
        receiver.Send("serverHello", new ServerHello { SessionId = receiver.SessionId, Nonce = Guid.NewGuid().ToString("N") });
        var deadline = Stopwatch.StartNew(); bool handshaken = false;
        while (deadline.ElapsedMilliseconds < 3000 && !handshaken)
        { receiver.Tick(); sender.Tick(); handshaken = sender.TryReceive(out _); Thread.Sleep(1); }
        Check(handshaken, "Loopback helper channel handshakes");
        byte[] bytes = Enumerable.Range(0, 3 * 1024 * 1024 + 513).Select(i => (byte)((i * 17) % 251)).ToArray();
        string token = Guid.NewGuid().ToString("N");
        var outgoing = new WorldSnapshotTransfer(); var incoming = new WorldSnapshotTransfer();
        outgoing.StartSend(bytes, World, 8, token);
        for (int i = 0; i < 100; i++) outgoing.PumpSend(sender);
        Check(outgoing.IsSending && sender.PendingSendBytes > 1024 * 1024 && sender.PendingSendBytes <= DedicatedProtocol.MaxQueueBytes,
            "Large transfer stalls safely at bounded send queue without dropping sender state");
        deadline.Restart();
        while (incoming.Completed == null && deadline.ElapsedMilliseconds < 8000)
        {
            outgoing.PumpSend(sender); sender.Tick(); receiver.Tick();
            Check(sender.Connected && receiver.Connected, "Backpressure keeps channel connected");
            while (receiver.TryReceive(out var message))
            {
                if (message.Type == "snapshotBegin")
                {
                    var begin = message.Read<SnapshotBegin>(); Check(begin.CheckpointRequestId == token, "Fresh-join correlation token survives helper serialization");
                    incoming.BeginReceive(begin, World);
                }
                else if (message.Type == "snapshotChunk") incoming.ReceiveChunk(message.Read<SnapshotChunk>());
                else if (message.Type == "snapshotEnd") incoming.EndReceive(message.Read<SnapshotEnd>());
                else throw new Exception("Unexpected transfer message: " + message.Type);
            }
            Thread.Sleep(1);
        }
        Check(incoming.Completed != null && incoming.CompletedBytes.SequenceEqual(bytes), "Backpressured stream resumes with every byte intact");
        Check(!outgoing.IsSending && outgoing.LastSentRevision == 8 && incoming.Completed.Revision == 8, "Sender completion and receiver checkpoint agree");
        outgoing.Reset(); incoming.Reset();
    }
}
