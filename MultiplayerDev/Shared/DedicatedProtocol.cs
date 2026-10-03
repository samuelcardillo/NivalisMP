using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NivalisTogether.Dedicated
{
    public static class DedicatedProtocol
    {
        public const int Version = 14;
        // Native save compatibility stays stable; Version gates multiplayer wire changes.
        public const string BuildId = "D7D7FEF8B76699AE6A9F70B111C239B013BA00261A02A85BDB394BDE38C46504";
        public const int MaxFrameBytes = 65536, MaxQueueBytes = 2 * 1024 * 1024, MaxQueueFrames = 256;
        public const int MaxSnapshotBytes = 64 * 1024 * 1024, SnapshotChunkBytes = 32768;
        public static readonly JsonSerializerOptions Json = new JsonSerializerOptions {
            PropertyNameCaseInsensitive = false, MaxDepth = 24,
            Converters = { new Int32ArrayJsonConverter() }
        };
        public static JoinRequest CreateJoin(ServerHello hello, string role, string name, string secret)
        {
            var request = new JoinRequest { Role = role, Name = name, Nonce = Guid.NewGuid().ToString("N") };
            request.Proof = Proof(hello, request, secret); return request;
        }
        public static string Proof(ServerHello hello, JoinRequest join, string secret)
            => Mac(secret ?? "", "join|" + hello.SessionId + "|" + hello.Nonce + "|" + join.Nonce + "|" + join.Role + "|" + join.Name + "|" + join.Version + "|" + join.BuildId);
        public static byte[] DeriveSessionKey(ServerHello hello, JoinRequest join, string secret)
            => Convert.FromHexString(Mac(secret ?? "", "session|" + hello.SessionId + "|" + hello.Nonce + "|" + join.Nonce + "|" + join.Role));
        public static string Mac(string secret, string value)
        { using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret)); return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(value))); }
        public static bool EqualHex(string a, string b)
        {
            try { return a != null && b != null && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(a), Convert.FromHexString(b)); }
            catch (FormatException) { return false; }
        }
        public static bool Text(string value, int max, bool empty = false)
        {
            if (value == null || value.Length > max || (!empty && value.Length == 0)) return false;
            foreach (char c in value) if (char.IsControl(c)) return false;
            return true;
        }
        public static bool PoseValid(PlayerPose pose) => pose != null && Text(pose.Scene, 128, true) && Text(pose.PropertyId, 128, true) &&
            float.IsFinite(pose.X) && float.IsFinite(pose.Y) && float.IsFinite(pose.Z) && float.IsFinite(pose.Yaw) &&
            Math.Abs(pose.X) <= 1000000 && Math.Abs(pose.Y) <= 1000000 && Math.Abs(pose.Z) <= 1000000 && Math.Abs(pose.Yaw) <= 360;
    }

    public sealed class WireMessage
    {
        public string Type { get; set; } = "";
        public string RequestId { get; set; } = "";
        public string SessionId { get; set; } = "";
        public ulong Sequence { get; set; }
        public JsonElement Payload { get; set; }
        public string Mac { get; set; } = "";
        // Local transport telemetry, never supplied by or serialized to a peer.
        [JsonIgnore] public int ReceivedFrameBytes { get; internal set; }
        public T Read<T>() => Payload.Deserialize<T>(DedicatedProtocol.Json);
    }
    public sealed class ServerHello
    {
        public AccountServerIdentity AccountIdentity { get; set; }
        public int Version { get; set; } = DedicatedProtocol.Version;
        public string BuildId { get; set; } = DedicatedProtocol.BuildId;
        public string SessionId { get; set; } = "";
        public string Nonce { get; set; } = "";
    }
    public sealed class JoinRequest
    {
        public int Version { get; set; } = DedicatedProtocol.Version;
        public string BuildId { get; set; } = DedicatedProtocol.BuildId;
        public string Role { get; set; } = "player";
        public string Name { get; set; } = "Player";
        public string Nonce { get; set; } = "";
        public string Proof { get; set; } = "";
    }
    public sealed class WorldMetadata
    {
        public int SchemaVersion { get; set; } = 1;
        public string WorldId { get; set; } = "";
        public string CreatedUtc { get; set; } = "";
        public string BuildId { get; set; } = DedicatedProtocol.BuildId;
        public int Seed { get; set; }
        public long Revision { get; set; }
        public string SnapshotFile { get; set; } = "";
        public string SnapshotHash { get; set; } = "";
        public string SnapshotFormat { get; set; } = "";
        public int SnapshotLength { get; set; }
        public string AccountLedgerFile { get; set; } = "";
        public string AccountLedgerHash { get; set; } = "";
        public int AccountLedgerLength { get; set; }
    }
    public sealed class JoinAccepted
    {
        public string AccountId { get; set; } = "";
        public string PlayerId { get; set; } = "";
        public string Role { get; set; } = "";
        public WorldMetadata World { get; set; } = new WorldMetadata();
        public bool Ready { get; set; }
    }
    public sealed class ServerStatus
    {
        public string ServerInstanceId { get; set; } = "";
        public string ServerId { get; set; } = "";
        public int Version { get; set; } = DedicatedProtocol.Version;
        public string BuildId { get; set; } = DedicatedProtocol.BuildId;
        public string Name { get; set; } = "";
        public string WorldId { get; set; } = "";
        public int Players { get; set; }
        public int MaxPlayers { get; set; }
        public int Port { get; set; }
        public bool PasswordRequired { get; set; }
        public bool Ready { get; set; }
        public string State { get; set; } = "Waiting for Unity simulation worker";
    }
    public sealed class ProtocolError { public string Code { get; set; } = ""; public string Message { get; set; } = ""; }
    public sealed class SnapshotBegin
    {
        public string TransferId { get; set; } = "";
        public string WorldId { get; set; } = "";
        public long Revision { get; set; }
        public int Length { get; set; }
        public string Sha256 { get; set; } = "";
        public string Format { get; set; } = "";
        public string CheckpointRequestId { get; set; } = "";
        public string AccountLedgerHash { get; set; } = "";
    }
    public sealed class CheckpointRequest { public string RequestId { get; set; } = ""; }
    public sealed class SnapshotChunk { public string TransferId { get; set; } = ""; public int Offset { get; set; } public byte[] Data { get; set; } = Array.Empty<byte>(); }
    public sealed class SnapshotEnd { public string TransferId { get; set; } = ""; }
    public sealed class WorldLoadComplete { public string WorldId { get; set; } = ""; public long Revision { get; set; } }
    public sealed class WorkerReady
    {
        public string WorldId { get; set; } = ""; public long Revision { get; set; }
        public string Scene { get; set; } = "";
    }
    public sealed class SimulationState { public bool Ready { get; set; } public string Message { get; set; } = ""; }
    public sealed class PlayerPose
    {
        public long ContextGeneration { get; set; }
        public string AccountId { get; set; } = ""; public string AppearanceId { get; set; } = "";
        public string PlayerId { get; set; } = ""; public string Name { get; set; } = "";
        public string PropertyId { get; set; } = "";
        public string Scene { get; set; } = ""; public float X { get; set; } public float Y { get; set; }
        public float Z { get; set; } public float Yaw { get; set; } public bool Active { get; set; }
    }
    public sealed class InteractionRequest
    {
        public long ContextGeneration { get; set; }
        public string AccountId { get; set; } = "";
        public string PlayerId { get; set; } = ""; public string RequestId { get; set; } = "";
        public string Kind { get; set; } = ""; public string TargetId { get; set; } = "";
        public JsonElement Data { get; set; }
    }
    /// <summary>Worker-authored simulation event; opaque bounded payload never interpreted as client authority.</summary>
    public sealed class WorldEvent
    {
        public string TargetPlayerId { get; set; } = "";
        public string WorldId { get; set; } = ""; public long Revision { get; set; }
        public long DeliveryId { get; set; }
        public string Kind { get; set; } = ""; public JsonElement Data { get; set; }
    }
    public sealed class WorldFlowCredit { public int BulkBytes { get; set; } public int ControlBytes { get; set; } }
    public sealed class WorldEventAcknowledgement { public long[] DeliveryIds { get; set; } = Array.Empty<long>(); }
    public sealed class PlayerPresence { public string PlayerId { get; set; } = ""; public string AccountId { get; set; } = ""; public string AppearanceId { get; set; } = ""; public string Name { get; set; } = ""; public bool Joined { get; set; } }
}
