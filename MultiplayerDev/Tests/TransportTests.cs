using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using NivalisTogether;

internal static class TransportTests
{
    private const string Code = "test-room-secret-123";
    private static int _checks;
    private static readonly Pose Home = new Pose { Scene = "City", Name = "ignored", X = 1, Y = 2, Z = 3, Yaw = 30, Active = true };
    public static int Main()
    {
        try
        {
            Arguments(); ExchangeAndCapacity(); WrongKeyAndTimeout(); HostValidationAndReplay(); ClientValidationAndReplay(); DuplicateSessions(); LocalSanitization();
            Console.WriteLine("PASS: " + _checks + " transport assertions (loopback UDP, authentication, malformed payloads, replay, capacity, expiry and disconnect).");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void Check(bool condition, string message)
    { _checks++; if (!condition) throw new Exception("FAIL: " + message); }
    private static void Throws(Action action, string message)
    {
        try { action(); } catch (ArgumentException) { Check(true, message); return; }
        Check(false, message);
    }
    private static void Arguments()
    {
        Throws(() => PresenceTransport.Host(0, "short", "A"), "short code rejected");
        Throws(() => PresenceTransport.Join("::1", 7777, Code, "A"), "IPv6 rejected");
        Throws(() => PresenceTransport.Join("255.255.255.255", 7777, Code, "A"), "broadcast rejected");
        Throws(() => PresenceTransport.Join("127.0.0.1", 0, Code, "A"), "invalid port rejected");
    }
    private static void Pump(PresenceTransport host, PresenceTransport[] clients, ref double time, int rounds = 30)
    {
        for (int i = 0; i < rounds; i++)
        {
            time += .05; host.Tick(Home, time);
            for (int c = 0; c < clients.Length; c++)
            { Pose local = Home; local.X = 10 + c; local.Scene = c == 1 ? "Apartment" : "City"; clients[c].Tick(local, time); }
            Thread.Sleep(1);
        }
    }
    private static void ExchangeAndCapacity()
    {
        using var host = PresenceTransport.Host(0, Code, "Host");
        using var a = PresenceTransport.Join("127.0.0.1", host.LocalPort, Code, "Alice");
        using var b = PresenceTransport.Join("127.0.0.1", host.LocalPort, Code, "Bob");
        using var c = PresenceTransport.Join("127.0.0.1", host.LocalPort, Code, "Carol");
        double now = 0; Pump(host, new[] { a, b, c }, ref now);
        Check(host.Connected && a.Connected && b.Connected && c.Connected, "host and three clients connect");
        Check(host.Peers.Count == 3 && a.Peers.Count == 3 && b.Peers.Count == 3, "four-player snapshots");
        Check(host.Peers.Any(p => p.Pose.Name == "Alice" && p.Pose.X == 10), "client position and chosen name reach host");
        Check(a.Peers.Any(p => p.Pose.Name == "Bob" && p.Pose.Scene == "Apartment" && p.Pose.X == 11), "different scene relays intact");
        Check(a.Peers.Any(p => p.Pose.Name == "Host" && p.Pose.X == 1), "host pose reaches client");
        Check(a.Peers.All(p => p.Pose.Name != "Alice"), "snapshot excludes local client");
        using var excess = PresenceTransport.Join("127.0.0.1", host.LocalPort, Code, "Extra");
        Pump(host, new[] { a, b, c, excess }, ref now, 10);
        Check(!excess.Connected && excess.Status.Contains("full"), "fifth player refused");
        a.Dispose(); Pump(host, new[] { b, c }, ref now, 10);
        Check(host.Peers.Count == 2 && b.Peers.Count == 2, "client disconnect propagated");
        // Drain the final legitimate updates before advancing host time without ticking clients.
        now += .05; host.Tick(Home, now); now += 6; host.Tick(Home, now);
        Check(host.Peers.Count == 0, "silent clients expire after five seconds");
        using var shutdownHost = PresenceTransport.Host(0, Code, "Host");
        using var shutdownClient = PresenceTransport.Join("127.0.0.1", shutdownHost.LocalPort, Code, "Guest");
        double otherTime = 0; Pump(shutdownHost, new[] { shutdownClient }, ref otherTime, 10);
        shutdownHost.Dispose(); shutdownClient.Tick(Home, otherTime + .05);
        Check(!shutdownClient.Connected && shutdownClient.Status.Contains("closed"), "host shutdown disconnects client");
    }
    private static void WrongKeyAndTimeout()
    {
        using var host = PresenceTransport.Host(0, Code, "Host");
        using var wrong = PresenceTransport.Join("127.0.0.1", host.LocalPort, "wrong-secret", "Wrong");
        double now = 0; Pump(host, new[] { wrong }, ref now, 220);
        Check(!wrong.Connected && wrong.Status.Contains("timed out") && host.Peers.Count == 0, "wrong room code times out without joining");
        using var raw = RawSocket();
        raw.SendTo(new byte[6000], new IPEndPoint(IPAddress.Loopback, host.LocalPort));
        raw.SendTo(new byte[] { 1, 2, 3 }, new IPEndPoint(IPAddress.Loopback, host.LocalPort));
        host.Tick(Home, now + .1); Check(host.Connected && host.Peers.Count == 0, "oversize and short datagrams ignored");
        using var good = PresenceTransport.Join("127.0.0.1", host.LocalPort, Code, "Good");
        Pump(host, new[] { good }, ref now, 15);
        Check(good.Connected, "host remains usable after malformed traffic");
        good.Tick(Home, now + .1); good.Tick(Home, now + 6.1);
        Check(!good.Connected && good.Status.Contains("timed out") && good.Peers.Count == 0, "silent host timeout clears client peers");
    }

    private static void HostValidationAndReplay()
    {
        using var host = PresenceTransport.Host(0, Code, "Host"); using var raw = RawSocket();
        var endpoint = new IPEndPoint(IPAddress.Loopback, host.LocalPort); Guid client = Guid.NewGuid();
        byte[] hello = Packet(1, Guid.Empty, client, 1, w => Text(w, "Raw"));
        raw.SendTo(hello, endpoint); host.Tick(Home, 0);
        byte[] response = Read(raw); Check(response[4] == 2, "host returns challenge");
        Guid server = new Guid(response.Skip(5).Take(16).ToArray());
        Guid challenge = new Guid(response.Skip(47).Take(16).ToArray());
        byte[] connect = Packet(3, server, client, 2, w => w.Write(challenge.ToByteArray()));
        raw.SendTo(connect, endpoint); host.Tick(Home, .1);
        Check(host.Peers.Count == 1, "authenticated challenge connects raw peer");
        Pose pose = Home; pose.X = 10;
        byte[] valid = Packet(4, server, client, 3, w => PoseBytes(w, pose));
        raw.SendTo(valid, endpoint); host.Tick(Home, .2);
        Check(host.Peers[0].Pose.X == 10, "valid pose accepted");
        pose.X = 90;
        raw.SendTo(Packet(4, server, client, 2, w => PoseBytes(w, pose)), endpoint); host.Tick(Home, .3);
        raw.SendTo(Packet(4, server, client, 3, w => PoseBytes(w, pose)), endpoint); host.Tick(Home, .4);
        Check(host.Peers[0].Pose.X == 10, "stale and duplicate pose sequence rejected");
        byte[] tampered = Packet(4, server, client, 4, w => PoseBytes(w, pose)); tampered[55] ^= 1;
        raw.SendTo(tampered, endpoint); host.Tick(Home, .5);
        Check(host.Peers[0].Pose.X == 10, "bad HMAC rejected");
        pose.X = float.NaN; raw.SendTo(Packet(4, server, client, 4, w => PoseBytes(w, pose)), endpoint); host.Tick(Home, .6);
        Check(host.Peers[0].Pose.X == 10, "NaN wire coordinate rejected");
        pose.X = 1000001; raw.SendTo(Packet(4, server, client, 4, w => PoseBytes(w, pose)), endpoint); host.Tick(Home, .7);
        Check(host.Peers[0].Pose.X == 10, "out-of-range wire coordinate rejected");
        pose.X = 20; pose.Scene = new string('a', 257);
        raw.SendTo(Packet(4, server, client, 4, w => PoseBytes(w, pose)), endpoint); host.Tick(Home, .8);
        Check(host.Peers[0].Pose.X == 10, "overlong wire scene rejected");
        pose.Scene = "City";
        raw.SendTo(Packet(4, Guid.NewGuid(), client, 4, w => PoseBytes(w, pose)), endpoint); host.Tick(Home, .9);
        Check(host.Peers[0].Pose.X == 10, "wrong host session rejected");
        raw.SendTo(Packet(4, server, client, 4, w => { PoseBytes(w, pose); w.Write((byte)1); }), endpoint); host.Tick(Home, 1);
        Check(host.Peers[0].Pose.X == 10, "trailing payload rejected atomically");
        raw.SendTo(Packet(4, server, client, 4, w => PoseBytes(w, pose)), endpoint); host.Tick(Home, 1.1);
        Check(host.Peers[0].Pose.X == 20, "invalid messages do not consume valid sequence");
        raw.SendTo(Packet(6, server, client, 5, null), endpoint); host.Tick(Home, 1.2);
        Check(host.Peers.Count == 0, "authenticated leave accepted");
        raw.SendTo(connect, endpoint); raw.SendTo(valid, endpoint); host.Tick(Home, 1.3);
        Check(host.Peers.Count == 0, "old connect and pose cannot resurrect disconnected peer");
        raw.SendTo(hello, endpoint); host.Tick(Home, 1.4); raw.SendTo(connect, endpoint); host.Tick(Home, 1.5);
        Check(host.Peers.Count == 0, "replayed hello gets new challenge, old connect rejected");
    }

    private static void ClientValidationAndReplay()
    {
        using var server = RawSocket(); var serverEndpoint = (IPEndPoint)server.LocalEndPoint;
        using var client = PresenceTransport.Join("127.0.0.1", serverEndpoint.Port, Code, "Client");
        client.Tick(Home, 0); byte[] hello = Read(server); Guid clientId = new Guid(hello.Skip(21).Take(16).ToArray());
        var destination = new IPEndPoint(IPAddress.Loopback, client.LocalPort); Guid host = Guid.NewGuid(); Guid challenge = Guid.NewGuid();
        server.SendTo(Packet(2, host, clientId, 1, w => w.Write(challenge.ToByteArray())), destination); client.Tick(Home, .1);
        Pose pose = Home;
        Action<BinaryWriter> snapshot = w => { w.Write((byte)1); w.Write(host.ToByteArray()); PoseBytes(w, pose); };
        server.SendTo(Packet(5, host, clientId, 2, snapshot), destination); client.Tick(Home, .2);
        Check(client.Connected && client.Peers.Count == 1 && client.Peers[0].Pose.X == 1, "client validates snapshot handshake");
        pose.X = 80; server.SendTo(Packet(5, host, clientId, 2, snapshot), destination); client.Tick(Home, .3);
        Check(client.Peers[0].Pose.X == 1, "client rejects snapshot replay");
        server.SendTo(Packet(5, Guid.NewGuid(), clientId, 3, snapshot), destination); client.Tick(Home, .4);
        Check(client.Peers[0].Pose.X == 1, "client rejects previous host session");
        server.SendTo(Packet(5, host, clientId, 3, w => { snapshot(w); w.Write((byte)0); }), destination); client.Tick(Home, .5);
        Check(client.Peers[0].Pose.X == 1, "malformed snapshot does not partly commit");
        server.SendTo(Packet(5, host, clientId, 3, snapshot), destination); client.Tick(Home, .6);
        Check(client.Peers[0].Pose.X == 80, "valid newer snapshot commits");
        // Replaying a valid old snapshot must not extend peer liveness.
        server.SendTo(Packet(5, host, clientId, 3, snapshot), destination); client.Tick(Home, 5.7);
        Check(!client.Connected && client.Peers.Count == 0, "snapshot replay cannot prevent timeout");
    }
    private static Socket RawSocket()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0)); socket.ReceiveTimeout = 1000; return socket;
    }
    private static void DuplicateSessions()
    {
        using var host = PresenceTransport.Host(0, Code, "Host"); using var first = RawSocket(); using var second = RawSocket();
        var endpoint = new IPEndPoint(IPAddress.Loopback, host.LocalPort); Guid clientId = Guid.NewGuid();
        byte[] hello = Packet(1, Guid.Empty, clientId, 1, w => Text(w, "Same ID"));
        first.SendTo(hello, endpoint); second.SendTo(hello, endpoint); host.Tick(Home, 0);
        byte[] firstChallenge = Read(first), secondChallenge = Read(second);
        Guid hostId = new Guid(firstChallenge.Skip(5).Take(16).ToArray());
        first.SendTo(Packet(3, hostId, clientId, 2, w => w.Write(firstChallenge.Skip(47).Take(16).ToArray())), endpoint);
        second.SendTo(Packet(3, hostId, clientId, 2, w => w.Write(secondChallenge.Skip(47).Take(16).ToArray())), endpoint);
        host.Tick(Home, .1); Check(host.Peers.Count == 1, "same client session cannot join through two endpoints");
    }
    private static void LocalSanitization()
    {
        using var host = PresenceTransport.Host(0, Code, "Host");
        using var client = PresenceTransport.Join("127.0.0.1", host.LocalPort, Code, "\n" + new string('N', 200));
        double now = 0; Pump(host, new[] { client }, ref now, 15);
        Pose bad = Home; bad.X = float.PositiveInfinity;
        client.Tick(bad, now + .2); host.Tick(Home, now + .25);
        Check(!host.Peers[0].Pose.Active && host.Peers[0].Pose.X == 0, "nonfinite local pose is safely inactive");
        Check(host.Peers[0].Pose.Name.Length == 96 && !host.Peers[0].Pose.Name.Contains('\n'), "local names have bounded sanitized UTF-8");
        bad = Home; bad.Scene = new string('S', 400); bad.Yaw = 730;
        client.Tick(bad, now + .4); host.Tick(Home, now + .45);
        Check(host.Peers[0].Pose.Scene.Length == 256 && host.Peers[0].Pose.Yaw == 10, "local scene and yaw bounded");
    }
    private static byte[] Read(Socket socket)
    {
        byte[] data = new byte[4096]; int length = socket.Receive(data); return data.Take(length).ToArray();
    }
    private static byte[] Packet(byte kind, Guid host, Guid client, ulong sequence, Action<BinaryWriter> payload)
    {
        using var memory = new MemoryStream(); using var writer = new BinaryWriter(memory, Encoding.UTF8, true);
        writer.Write(0x3150544Eu); writer.Write(kind); writer.Write(host.ToByteArray()); writer.Write(client.ToByteArray());
        writer.Write(sequence); writer.Write((ushort)0); payload?.Invoke(writer);
        int signedLength = (int)memory.Length; memory.Position = 45; writer.Write((ushort)(signedLength - 47));
        using var sha = SHA256.Create(); using var hmac = new HMACSHA256(sha.ComputeHash(Encoding.UTF8.GetBytes(Code)));
        byte[] digest = hmac.ComputeHash(memory.ToArray()); memory.Position = signedLength; writer.Write(digest); return memory.ToArray();
    }
    private static void Text(BinaryWriter writer, string value)
    { byte[] bytes = Encoding.UTF8.GetBytes(value); writer.Write((ushort)bytes.Length); writer.Write(bytes); }
    private static void PoseBytes(BinaryWriter writer, Pose pose)
    {
        Text(writer, pose.Scene); Text(writer, pose.Name); writer.Write(pose.X); writer.Write(pose.Y);
        writer.Write(pose.Z); writer.Write(pose.Yaw); writer.Write((byte)(pose.Active ? 1 : 0));
    }
}
