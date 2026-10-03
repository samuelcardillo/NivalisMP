using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace NivalisTogether.Dedicated
{
    public sealed class AccountStateChunk
    {
        public string TransferId { get; set; } = "";
        public string AccountId { get; set; } = "";
        public long Revision { get; set; }
        public int Length { get; set; }
        public int Offset { get; set; }
        public string Hash { get; set; } = "";
        public byte[] Bytes { get; set; } = Array.Empty<byte>();
    }

    // Live account updates use the existing credit/ACK stream, and are addressed
    // to one authenticated connection. They never enter a shared economy packet.
    public sealed class AccountStateChunks
    {
        public const string EventKind = "account-state-chunk";
        private const int ChunkSize = 24 * 1024, MaxBytes = AccountProtocol.MaxInventoryBytes + 65536;
        private AccountStateChunk header;
        private MemoryStream input;
        private DateTime started;
        public static IEnumerable<WorldEvent> Capture(AccountState state, string playerId)
        {
            AccountProtocol.Validate(state);
            if (!Guid.TryParseExact(playerId, "N", out _) || !state.Initialized)
                throw new InvalidDataException("A private update requires an initialized authenticated account.");
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(state, DedicatedProtocol.Json);
            if (bytes.Length > MaxBytes) throw new InvalidDataException("Account state exceeds its transfer bound.");
            string id = Guid.NewGuid().ToString("N"), hash = Convert.ToHexString(SHA256.HashData(bytes));
            for (int offset = 0; offset < bytes.Length; offset += ChunkSize)
            {
                byte[] part = new byte[Math.Min(ChunkSize, bytes.Length - offset)];
                Buffer.BlockCopy(bytes, offset, part, 0, part.Length);
                yield return new WorldEvent { Kind = EventKind, TargetPlayerId = playerId,
                    Data = JsonSerializer.SerializeToElement(new AccountStateChunk { TransferId = id,
                        AccountId = state.AccountId, Revision = state.StateRevision, Length = bytes.Length,
                        Offset = offset, Hash = hash, Bytes = part }, DedicatedProtocol.Json) };
            }
        }
        public AccountState Accept(WorldEvent update, string playerId, string accountId)
        {
            if (update == null || update.Kind != EventKind || update.TargetPlayerId != playerId)
                throw new InvalidDataException("Account update was not addressed to this connection.");
            var part = update.Data.Deserialize<AccountStateChunk>(DedicatedProtocol.Json);
            if (part == null || !Guid.TryParseExact(part.TransferId, "N", out _) || part.AccountId != accountId ||
                part.Revision < 0 || part.Length < 1 || part.Length > MaxBytes || part.Offset < 0 ||
                part.Bytes == null || part.Bytes.Length < 1 || part.Bytes.Length > ChunkSize ||
                part.Offset > part.Length - part.Bytes.Length || part.Hash == null || part.Hash.Length != 64)
                throw new InvalidDataException("Invalid private account chunk.");
            if (header == null)
            {
                if (part.Offset != 0) throw new InvalidDataException("Account update has no opening chunk.");
                header = part; input = new MemoryStream(part.Length); started = DateTime.UtcNow;
            }
            if (part.TransferId != header.TransferId || part.Revision != header.Revision || part.Length != header.Length ||
                part.Hash != header.Hash || part.Offset != input.Length || (DateTime.UtcNow - started).TotalSeconds > 40)
                throw new InvalidDataException("Account chunks arrived out of order or expired.");
            input.Write(part.Bytes);
            if (input.Length != header.Length) return null;
            byte[] bytes = input.ToArray();
            if (!DedicatedProtocol.EqualHex(Convert.ToHexString(SHA256.HashData(bytes)), header.Hash))
                throw new InvalidDataException("Private account checksum failed.");
            var state = JsonSerializer.Deserialize<AccountState>(bytes, DedicatedProtocol.Json);
            AccountProtocol.Validate(state);
            if (state.AccountId != accountId || state.StateRevision != header.Revision || !state.Initialized)
                throw new InvalidDataException("Private account payload does not match its envelope.");
            Reset(); return state;
        }
        public void Reset() { input?.Dispose(); input = null; header = null; }
    }
}
