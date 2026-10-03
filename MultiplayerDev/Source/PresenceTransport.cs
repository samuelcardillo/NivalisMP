using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace NivalisTogether
{
    public struct Pose
    {
        public string Scene, Name;
        public float X, Y, Z, Yaw;
        public bool Active;
    }

    public readonly struct RemotePose
    {
        public readonly string Id;
        public readonly Pose Pose;
        public RemotePose(string id, Pose pose) { Id = id; Pose = pose; }
    }

    /// <summary>Authenticated LAN/manual IPv4 presence only. Call Tick and Dispose on one thread.</summary>
    public sealed class PresenceTransport : IDisposable
    {
        private const uint Magic = 0x3150544E;
        private const int HeaderBytes = 47, MacBytes = 32, MaxPacketBytes = 4096;
        private const double SendInterval = 0.1, RetryInterval = 0.5, PeerTimeout = 5, JoinTimeout = 10;
        private const int MaxNameBytes = 96, MaxSceneBytes = 256;
        private const float CoordinateLimit = 1000000;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private enum Kind : byte { Hello = 1, Challenge, Connect, Pose, Snapshot, Leave, Shutdown, Full }
        private sealed class Pending
        {
            public Guid Client, Challenge;
            public string Name = "Player";
            public double Created;
        }
        private sealed class Member
        {
            public IPEndPoint Endpoint;
            public Guid Client, Challenge;
            public ulong Sequence;
            public double LastSeen;
            public Pose Pose;
        }
        private readonly Socket _socket;
        private readonly HMACSHA256 _hmac;
        private readonly Guid _clientId = Guid.NewGuid();
        private readonly string _name;
        private readonly IPEndPoint _server;
        private readonly byte[] _receive = new byte[MaxPacketBytes + 1];
        private readonly Dictionary<string, Pending> _pending = new Dictionary<string, Pending>();
        private readonly Dictionary<string, Member> _members = new Dictionary<string, Member>();
        private readonly List<RemotePose> _peers = new List<RemotePose>();
        private readonly ReadOnlyCollection<RemotePose> _peerView;
        private Guid _hostId, _challenge;
        private ulong _sendSequence, _hostSequence;
        private double _started = double.NaN, _lastSeen, _nextSend, _now;
        private bool _disposed, _stopped;
        private Pose _local;
        public bool IsHost { get; }
        public bool Connected { get; private set; }
        public int LocalPort { get; }
        public string Status { get; private set; }
        public IReadOnlyList<RemotePose> Peers => _peerView;

        public static PresenceTransport Host(int port, string code, string name)
            => new PresenceTransport(true, null, port, code, name);
        public static PresenceTransport Join(string ipv4, int port, string code, string name)
        {
            if (!IPAddress.TryParse(ipv4, out IPAddress address) || address.AddressFamily != AddressFamily.InterNetwork ||
                address.Equals(IPAddress.Any) || address.Equals(IPAddress.Broadcast) || address.GetAddressBytes()[0] >= 224)
                throw new ArgumentException("Enter the host's IPv4 address.", nameof(ipv4));
            if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
            return new PresenceTransport(false, new IPEndPoint(address, port), 0, code, name);
        }
        private PresenceTransport(bool host, IPEndPoint server, int port, string code, string name)
        {
            if (port < 0 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
            if (string.IsNullOrWhiteSpace(code) || code.Length < 8 || code.Length > 128)
                throw new ArgumentException("Room code must contain 8 to 128 characters.", nameof(code));
            IsHost = host; _server = server; _name = CleanText(name, MaxNameBytes, "Player");
            _local = new Pose { Scene = "", Name = _name }; _peerView = _peers.AsReadOnly();
            using (SHA256 sha = SHA256.Create()) _hmac = new HMACSHA256(sha.ComputeHash(Utf8.GetBytes(code)));
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                _socket.ExclusiveAddressUse = true;
                _socket.Bind(new IPEndPoint(IPAddress.Any, port)); _socket.Blocking = false;
                LocalPort = ((IPEndPoint)_socket.LocalEndPoint).Port;
            }
            catch { _socket.Dispose(); _hmac.Dispose(); throw; }
            _hostId = host ? Guid.NewGuid() : Guid.Empty; Connected = host;
            Status = host ? "Hosting on UDP " + LocalPort + " (1/4 players)" : "Connecting...";
        }

        public void Tick(Pose local, double now)
        {
            if (_disposed || _stopped) return;
            if (double.IsNaN(now) || double.IsInfinity(now)) throw new ArgumentOutOfRangeException(nameof(now));
            if (double.IsNaN(_started)) { _started = _lastSeen = _now = now; _nextSend = now; }
            _now = Math.Max(_now, now); _local = CleanPose(local); Receive();
            if (_stopped) return;
            if (IsHost)
            {
                var expired = new List<string>();
                foreach (var pair in _members) if (_now - pair.Value.LastSeen >= PeerTimeout) expired.Add(pair.Key);
                foreach (string key in expired) _members.Remove(key);
                expired.Clear();
                foreach (var pair in _pending) if (_now - pair.Value.Created >= PeerTimeout) expired.Add(pair.Key);
                foreach (string key in expired) _pending.Remove(key);
                _peers.Clear();
                foreach (Member member in _members.Values) _peers.Add(new RemotePose(member.Client.ToString("N"), member.Pose));
                Status = "Hosting on UDP " + LocalPort + " (" + (_members.Count + 1) + "/4 players)";
                if (_now >= _nextSend)
                {
                    foreach (Member member in _members.Values) SendSnapshot(member);
                    _nextSend = _now + SendInterval;
                }
            }
            else
            {
                if (Connected && _now - _lastSeen >= PeerTimeout) { Stop("Disconnected: host timed out."); return; }
                if (!Connected && _now - _started >= JoinTimeout) { Stop("Connection timed out: check IP, port and room code."); return; }
                if (_now >= _nextSend)
                {
                    if (Connected) Send(Kind.Pose, _server, _hostId, _clientId, writer => WritePose(writer, _local));
                    else if (_challenge != Guid.Empty) Send(Kind.Connect, _server, _hostId, _clientId, writer => writer.Write(_challenge.ToByteArray()));
                    else Send(Kind.Hello, _server, Guid.Empty, _clientId, writer => WriteText(writer, _name));
                    _nextSend = _now + (Connected ? SendInterval : RetryInterval);
                }
            }
        }

        private void Receive()
        {
            for (int i = 0; i < 64; i++)
            {
                EndPoint endpoint = new IPEndPoint(IPAddress.Any, 0); int length;
                try { length = _socket.ReceiveFrom(_receive, 0, _receive.Length, SocketFlags.None, ref endpoint); }
                catch (SocketException ex)
                {
                    if (ex.SocketErrorCode == SocketError.WouldBlock) break;
                    if (ex.SocketErrorCode == SocketError.MessageSize || ex.SocketErrorCode == SocketError.ConnectionReset) continue;
                    Stop("Network receive error: " + ex.SocketErrorCode); break;
                }
                if (length < HeaderBytes + MacBytes || length > MaxPacketBytes) continue;
                IPEndPoint sender = (IPEndPoint)endpoint;
                if (!IsHost && !sender.Equals(_server)) continue;
                byte[] mac = _hmac.ComputeHash(_receive, 0, length - MacBytes);
                if (!CryptographicOperations.FixedTimeEquals(mac, new ReadOnlySpan<byte>(_receive, length - MacBytes, MacBytes))) continue;
                try
                {
                    using var stream = new MemoryStream(_receive, 0, length - MacBytes, false);
                    using var reader = new BinaryReader(stream, Utf8);
                    if (reader.ReadUInt32() != Magic) continue;
                    Kind kind = (Kind)reader.ReadByte(); Guid host = ReadGuid(reader), client = ReadGuid(reader);
                    ulong sequence = reader.ReadUInt64(); int payloadLength = reader.ReadUInt16();
                    if (sequence == 0 || client == Guid.Empty || payloadLength != length - HeaderBytes - MacBytes) continue;
                    if (IsHost) ReceiveHost(sender, kind, host, client, sequence, reader);
                    else ReceiveClient(kind, host, client, sequence, reader);
                }
                catch (Exception ex) when (ex is EndOfStreamException || ex is InvalidDataException || ex is DecoderFallbackException || ex is ArgumentException)
                { /* Validate a complete packet before committing its contents. */ }
                if (_stopped) break;
            }
        }

        private void ReceiveHost(IPEndPoint sender, Kind kind, Guid host, Guid client, ulong sequence, BinaryReader reader)
        {
            string key = sender.ToString();
            if (kind == Kind.Hello && host == Guid.Empty)
            {
                string name = ReadText(reader, MaxNameBytes); End(reader);
                if (name.Length == 0) return;
                if (_members.TryGetValue(key, out Member existing))
                { if (existing.Client == client) SendSnapshot(existing); return; }
                foreach (Member member in _members.Values) if (member.Client == client) return;
                if (_members.Count >= 3) { Send(Kind.Full, sender, _hostId, client, null); return; }
                if (!_pending.TryGetValue(key, out Pending pending) || pending.Client != client)
                {
                    if (_pending.Count >= 16 && !_pending.ContainsKey(key)) return;
                    pending = new Pending { Client = client, Challenge = Guid.NewGuid(), Name = name, Created = _now };
                    _pending[key] = pending;
                }
                Send(Kind.Challenge, sender, _hostId, client, writer => writer.Write(pending.Challenge.ToByteArray()));
                return;
            }
            if (host != _hostId) return;
            if (kind == Kind.Connect)
            {
                Guid challenge = ReadGuid(reader); End(reader);
                if (_members.TryGetValue(key, out Member established))
                {
                    if (established.Client != client || established.Challenge != challenge || sequence <= established.Sequence) return;
                    established.Sequence = sequence; established.LastSeen = _now; SendSnapshot(established); return;
                }
                if (!_pending.TryGetValue(key, out Pending pending) || pending.Client != client || pending.Challenge != challenge || _now - pending.Created >= PeerTimeout) return;
                if (_members.Count >= 3) { Send(Kind.Full, sender, _hostId, client, null); return; }
                foreach (Member member in _members.Values) if (member.Client == client) return;
                var joined = new Member { Endpoint = sender, Client = client, Challenge = challenge, Sequence = sequence, LastSeen = _now,
                    Pose = new Pose { Scene = "", Name = pending.Name } };
                _members.Add(key, joined); _pending.Remove(key); SendSnapshot(joined); return;
            }
            if (!_members.TryGetValue(key, out Member peer) || peer.Client != client || sequence <= peer.Sequence) return;
            if (kind == Kind.Pose)
            {
                Pose pose = ReadPose(reader); End(reader); pose.Name = peer.Pose.Name;
                peer.Pose = pose; peer.Sequence = sequence; peer.LastSeen = _now;
            }
            else if (kind == Kind.Leave) { End(reader); _members.Remove(key); }
        }

        private void ReceiveClient(Kind kind, Guid host, Guid client, ulong sequence, BinaryReader reader)
        {
            if (client != _clientId || host == Guid.Empty || (_hostId != Guid.Empty && host != _hostId) || sequence <= _hostSequence) return;
            if (kind == Kind.Challenge && !Connected)
            {
                Guid challenge = ReadGuid(reader); End(reader); if (challenge == Guid.Empty) return;
                _hostId = host; _challenge = challenge; _hostSequence = sequence; _nextSend = _now;
            }
            else if (kind == Kind.Snapshot && _challenge != Guid.Empty)
            {
                int count = reader.ReadByte(); if (count < 1 || count > 3) return;
                var updated = new List<RemotePose>(count); var seen = new HashSet<Guid>();
                for (int i = 0; i < count; i++)
                {
                    Guid id = ReadGuid(reader); if (id == Guid.Empty || id == _clientId || !seen.Add(id)) return;
                    updated.Add(new RemotePose(id.ToString("N"), ReadPose(reader)));
                }
                End(reader); if (!seen.Contains(host)) return;
                _peers.Clear(); _peers.AddRange(updated); Connected = true; _hostSequence = sequence; _lastSeen = _now;
                _nextSend = Math.Min(_nextSend, _now + SendInterval); Status = "Connected (" + (count + 1) + "/4 players)";
            }
            else if (kind == Kind.Full && !Connected) { End(reader); Stop("Room is full (4 players maximum)."); }
            else if (kind == Kind.Shutdown && Connected) { End(reader); Stop("Disconnected: host closed the room."); }
        }
        private void SendSnapshot(Member recipient)
        {
            Send(Kind.Snapshot, recipient.Endpoint, _hostId, recipient.Client, writer =>
            {
                writer.Write((byte)_members.Count); writer.Write(_hostId.ToByteArray()); WritePose(writer, _local);
                foreach (Member member in _members.Values)
                    if (member.Client != recipient.Client) { writer.Write(member.Client.ToByteArray()); WritePose(writer, member.Pose); }
            });
        }
        private void Send(Kind kind, IPEndPoint destination, Guid host, Guid client, Action<BinaryWriter> payload)
        {
            if (_disposed) return;
            using var stream = new MemoryStream(1024); using var writer = new BinaryWriter(stream, Utf8, true);
            writer.Write(Magic); writer.Write((byte)kind); writer.Write(host.ToByteArray()); writer.Write(client.ToByteArray());
            writer.Write(++_sendSequence); writer.Write((ushort)0); payload?.Invoke(writer);
            int signedLength = checked((int)stream.Length);
            if (signedLength + MacBytes > MaxPacketBytes) throw new InvalidOperationException("Presence packet exceeded its limit.");
            stream.Position = HeaderBytes - 2; writer.Write((ushort)(signedLength - HeaderBytes));
            byte[] mac = _hmac.ComputeHash(stream.ToArray()); stream.Position = signedLength; writer.Write(mac);
            try { _socket.SendTo(stream.GetBuffer(), 0, signedLength + MacBytes, SocketFlags.None, destination); }
            catch (SocketException ex)
            {
                if (ex.SocketErrorCode != SocketError.WouldBlock && ex.SocketErrorCode != SocketError.ConnectionReset &&
                    ex.SocketErrorCode != SocketError.NetworkUnreachable && ex.SocketErrorCode != SocketError.HostUnreachable)
                    Status = "Network send error: " + ex.SocketErrorCode;
            }
        }
        private Pose CleanPose(Pose pose)
        {
            pose.Name = _name; pose.Scene = CleanText(pose.Scene, MaxSceneBytes, "");
            if (!Coordinate(pose.X) || !Coordinate(pose.Y) || !Coordinate(pose.Z) || !float.IsFinite(pose.Yaw))
            { pose.Active = false; pose.X = pose.Y = pose.Z = pose.Yaw = 0; }
            else pose.Yaw = ((pose.Yaw % 360) + 540) % 360 - 180;
            if (pose.Scene.Length == 0) pose.Active = false; return pose;
        }
        private static bool Coordinate(float value) => float.IsFinite(value) && Math.Abs(value) <= CoordinateLimit;
        private static string CleanText(string text, int maxBytes, string fallback)
        {
            if (string.IsNullOrWhiteSpace(text)) return fallback;
            var builder = new StringBuilder();
            int bytes = 0;
            foreach (char c in text)
            {
                if (char.IsControl(c) || char.IsSurrogate(c)) continue;
                int length = c <= 0x7f ? 1 : c <= 0x7ff ? 2 : 3;
                if (bytes + length > maxBytes) break;
                builder.Append(c); bytes += length;
            }
            string clean = builder.ToString().Trim();
            return clean.Length == 0 ? fallback : clean;
        }
        private static void WriteText(BinaryWriter writer, string value)
        { byte[] bytes = Utf8.GetBytes(value); writer.Write((ushort)bytes.Length); writer.Write(bytes); }
        private static string ReadText(BinaryReader reader, int maxBytes)
        {
            int length = reader.ReadUInt16(); if (length > maxBytes) throw new InvalidDataException();
            byte[] bytes = reader.ReadBytes(length); if (bytes.Length != length) throw new EndOfStreamException();
            string text = Utf8.GetString(bytes);
            foreach (char c in text) if (char.IsControl(c) || char.IsSurrogate(c)) throw new InvalidDataException();
            return text;
        }
        private static void WritePose(BinaryWriter writer, Pose pose)
        {
            WriteText(writer, pose.Scene); WriteText(writer, pose.Name);
            writer.Write(pose.X); writer.Write(pose.Y); writer.Write(pose.Z); writer.Write(pose.Yaw); writer.Write((byte)(pose.Active ? 1 : 0));
        }
        private static Pose ReadPose(BinaryReader reader)
        {
            var pose = new Pose { Scene = ReadText(reader, MaxSceneBytes), Name = ReadText(reader, MaxNameBytes),
                X = reader.ReadSingle(), Y = reader.ReadSingle(), Z = reader.ReadSingle(), Yaw = reader.ReadSingle() };
            byte active = reader.ReadByte();
            if (active > 1 || !Coordinate(pose.X) || !Coordinate(pose.Y) || !Coordinate(pose.Z) ||
                !float.IsFinite(pose.Yaw) || Math.Abs(pose.Yaw) > 360 || pose.Name.Length == 0 || (active == 1 && pose.Scene.Length == 0)) throw new InvalidDataException();
            pose.Active = active == 1; return pose;
        }
        private static Guid ReadGuid(BinaryReader reader)
        { byte[] bytes = reader.ReadBytes(16); if (bytes.Length != 16) throw new EndOfStreamException(); return new Guid(bytes); }
        private static void End(BinaryReader reader)
        { if (reader.BaseStream.Position != reader.BaseStream.Length) throw new InvalidDataException(); }
        private void Stop(string status) { Connected = false; _stopped = true; _peers.Clear(); Status = status; }
        public void Dispose()
        {
            if (_disposed) return;
            if (IsHost) foreach (Member member in _members.Values) Send(Kind.Shutdown, member.Endpoint, _hostId, member.Client, null);
            else if (Connected) Send(Kind.Leave, _server, _hostId, _clientId, null);
            _disposed = true; _socket.Dispose(); _hmac.Dispose(); _members.Clear(); _pending.Clear(); _peers.Clear();
            Connected = false; Status = "Disconnected.";
        }
    }
}
