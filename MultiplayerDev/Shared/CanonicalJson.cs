using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NivalisTogether.Dedicated
{
    /// <summary>Measure or authenticate the canonical wire serialization without
    /// retaining another full copy of its bytes. Values are serialized each time:
    /// callers may change delivery IDs and revisions between uses.</summary>
    public static class CanonicalJson
    {
        private static readonly byte[] ServerPrefix = Encoding.ASCII.GetBytes("server-to-client\n");
        private static readonly byte[] ClientPrefix = Encoding.ASCII.GetBytes("client-to-server\n");

        public static int ByteCount<T>(T value)
        {
            using var sink = new SerializationSink(null);
            JsonSerializer.Serialize(sink, value, DedicatedProtocol.Json);
            return sink.BytesWritten;
        }

        public static string HmacHex<T>(T value, byte[] key, bool serverToClient)
        {
            // A fresh hash per envelope keeps failures, directions and sessions
            // independent. Stream serialization uses the same options, encoder,
            // converters and property order as SerializeToUtf8Bytes.
            using var hash = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
            hash.AppendData(serverToClient ? ServerPrefix : ClientPrefix);
            using var sink = new SerializationSink(hash);
            JsonSerializer.Serialize(sink, value, DedicatedProtocol.Json);
            Span<byte> digest = stackalloc byte[32];
            if (!hash.TryGetHashAndReset(digest, out int written) || written != digest.Length)
                throw new CryptographicException("Invalid session authentication digest.");
            return Convert.ToHexString(digest);
        }

        private sealed class SerializationSink : Stream
        {
            private readonly IncrementalHash hash;
            public int BytesWritten { get; private set; }
            public SerializationSink(IncrementalHash hash) { this.hash = hash; }
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => BytesWritten;
            public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
            public override void Write(ReadOnlySpan<byte> buffer)
            {
                BytesWritten = checked(BytesWritten + buffer.Length);
                hash?.AppendData(buffer);
            }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
