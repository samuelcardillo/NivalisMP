using System;
using System.IO;
using System.Security.Cryptography;
using NivalisTogether.Dedicated;

namespace NivalisTogether
{
    // Unity-independent helper: transfer and verify bytes before the game can load them.
    internal sealed class WorldSnapshotTransfer
    {
        private SnapshotBegin receiving;
        private MemoryStream receiveBytes;
        private byte[] sendBytes;
        private SnapshotBegin sending;
        private int sent;
        private bool began;
        public bool IsSending => sendBytes != null;
        public long LastSentRevision { get; private set; }
        public SnapshotBegin Completed { get; private set; }
        public byte[] CompletedBytes { get; private set; }
        public void BeginReceive(SnapshotBegin begin, string expectedWorld)
        {
            if (begin == null || begin.WorldId != expectedWorld || begin.Format != "nivalis-es2-v1" || begin.Length < 1 ||
                begin.Length > DedicatedProtocol.MaxSnapshotBytes || !Guid.TryParseExact(begin.TransferId, "N", out _) || begin.Revision < 0)
                throw new InvalidDataException("Invalid server world snapshot header.");
            receiveBytes?.Dispose(); receiving = begin; receiveBytes = new MemoryStream(begin.Length);
            Completed = null; CompletedBytes = null;
        }
        public void ReceiveChunk(SnapshotChunk chunk)
        {
            if (receiving == null || chunk == null || chunk.TransferId != receiving.TransferId || chunk.Offset != receiveBytes.Length ||
                chunk.Data == null || chunk.Data.Length == 0 || chunk.Data.Length > DedicatedProtocol.SnapshotChunkBytes ||
                receiveBytes.Length + chunk.Data.Length > receiving.Length) throw new InvalidDataException("Invalid world snapshot chunk.");
            receiveBytes.Write(chunk.Data, 0, chunk.Data.Length);
        }
        public void EndReceive(SnapshotEnd end)
        {
            if (receiving == null || end.TransferId != receiving.TransferId || receiveBytes.Length != receiving.Length)
                throw new InvalidDataException("Incomplete world snapshot.");
            byte[] data = receiveBytes.ToArray();
            if (!DedicatedProtocol.EqualHex(Convert.ToHexString(SHA256.HashData(data)), receiving.Sha256))
                throw new InvalidDataException("World snapshot checksum failed.");
            Completed = receiving; CompletedBytes = data; receiving = null; receiveBytes.Dispose(); receiveBytes = null;
        }
        public void StartSend(byte[] bytes, string worldId, long revision, string checkpointRequestId = "", string accountLedgerHash = "")
        {
            if (IsSending) throw new InvalidOperationException("A snapshot is already being sent.");
            if (bytes.Length < 1 || bytes.Length > DedicatedProtocol.MaxSnapshotBytes) throw new InvalidDataException("World save exceeds snapshot size limit.");
            sendBytes = bytes; sent = 0; began = false;
            sending = new SnapshotBegin { TransferId = Guid.NewGuid().ToString("N"), WorldId = worldId, Revision = revision,
                Length = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), Format = "nivalis-es2-v1", CheckpointRequestId = checkpointRequestId,
                AccountLedgerHash = accountLedgerHash };
        }
        public void PumpSend(DedicatedConnection connection)
        {
            if (sendBytes == null) return;
            if (!began) { if (!connection.Send("snapshotBegin", sending)) return; began = true; }
            for (int i = 0; i < 8 && sent < sendBytes.Length; i++)
            {
                int length = Math.Min(DedicatedProtocol.SnapshotChunkBytes, sendBytes.Length - sent);
                var data = new byte[length]; Buffer.BlockCopy(sendBytes, sent, data, 0, length);
                if (!connection.Send("snapshotChunk", new SnapshotChunk { TransferId = sending.TransferId, Offset = sent, Data = data })) return;
                sent += length;
            }
            if (sent == sendBytes.Length && connection.Send("snapshotEnd", new SnapshotEnd { TransferId = sending.TransferId }))
            { LastSentRevision = sending.Revision; sendBytes = null; sending = null; }
        }
        public void Reset()
        { receiveBytes?.Dispose(); receiveBytes = null; receiving = null; sendBytes = null; sending = null; Completed = null; CompletedBytes = null; LastSentRevision = 0; }
    }
}
