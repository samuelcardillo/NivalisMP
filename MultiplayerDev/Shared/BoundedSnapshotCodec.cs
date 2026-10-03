using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace NivalisTogether.Dedicated
{
    public sealed class EncodedSnapshot
    {
        public string Encoding { get; set; } = "json-v1";
        public int RawLength { get; set; }
        public string Hash { get; set; } = "";
        public byte[] Bytes { get; set; } = Array.Empty<byte>();
    }

    /// <summary>Managed-only whole-snapshot compression. Callers retain their own
    /// framing, sequences and complete-assembly checks before calling Decode.</summary>
    public static class BoundedSnapshotCodec
    {
        public const string JsonEncoding = "json-v1", GzipEncoding = "gzip-v1";
        /// <summary>Serialize a stable DTO directly into compression. The common
        /// gzip path never materializes the uncompressed JSON byte array.</summary>
        public static EncodedSnapshot EncodeJson<T>(T state, int maximumRawBytes)
        {
            if (maximumRawBytes < 1) throw new ArgumentException("Invalid snapshot size bound.");
            byte[] compressed; int rawLength;
            using (var output = new MemoryStream())
            {
                using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true))
                {
                    using var counted = new BoundedJsonWriteStream(gzip, maximumRawBytes);
                    JsonSerializer.Serialize(counted, state, DedicatedProtocol.Json);
                    rawLength = counted.BytesWritten;
                }
                compressed = output.ToArray();
            }
            byte[] bytes = compressed; string encoding = GzipEncoding;
            if (compressed.Length >= rawLength)
            {
                // Tiny/incompressible snapshots retain the existing json-v1
                // fallback. Only this uncommon path needs the raw bytes.
                bytes = new byte[rawLength];
                using var output = new MemoryStream(bytes, true);
                using var counted = new BoundedJsonWriteStream(output, rawLength);
                JsonSerializer.Serialize(counted, state, DedicatedProtocol.Json);
                if (counted.BytesWritten != rawLength)
                    throw new InvalidDataException("Snapshot changed during serialization.");
                encoding = JsonEncoding;
            }
            return new EncodedSnapshot { Encoding = encoding, RawLength = rawLength,
                Bytes = bytes, Hash = Convert.ToHexString(SHA256.HashData(bytes)) };
        }

        /// <summary>Verify the encoded bytes and consume one complete canonical
        /// JSON document through a bounded decompression stream. No raw JSON copy
        /// is retained; normal DTO allocations still belong to the caller.</summary>
        public static T DecodeJson<T>(string encoding, int rawLength, string hash, byte[] encoded, int maximumRawBytes)
        {
            ValidateHeader(encoding, rawLength, encoded?.Length ?? 0, maximumRawBytes);
            if (hash == null || hash.Length != 64 ||
                !string.Equals(Convert.ToHexString(SHA256.HashData(encoded)), hash, StringComparison.Ordinal))
                throw new InvalidDataException("Snapshot encoded checksum mismatch.");
            if (encoding == JsonEncoding) return JsonSerializer.Deserialize<T>(encoded, DedicatedProtocol.Json);
            using var input = new MemoryStream(encoded, false);
            using var gzip = new GZipStream(input, CompressionMode.Decompress, false);
            using var counted = new ExactJsonReadStream(gzip, rawLength);
            T result = JsonSerializer.Deserialize<T>(counted, DedicatedProtocol.Json);
            counted.Complete();
            return result;
        }

        // These wrappers do not own their inner streams. Keeping the gzip close
        // outside the write wrapper finalizes its trailer before hashing output.
        private sealed class BoundedJsonWriteStream : Stream
        {
            private readonly Stream inner;
            private readonly int maximum;
            public int BytesWritten { get; private set; }
            public BoundedJsonWriteStream(Stream inner, int maximum) { this.inner = inner; this.maximum = maximum; }
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => BytesWritten;
            public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }
            // Do not introduce intermediate gzip flush markers: compression is
            // finalized once, just as Encode(byte[]) finalizes its single write.
            public override void Flush() { }
            public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
            public override void Write(ReadOnlySpan<byte> buffer)
            {
                if (buffer.Length > maximum - BytesWritten)
                    throw new InvalidDataException("Snapshot raw length is outside its supported bounds.");
                inner.Write(buffer); BytesWritten += buffer.Length;
            }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }

        private sealed class ExactJsonReadStream : Stream
        {
            private readonly Stream inner;
            private readonly int expected;
            private int bytesRead;
            private bool ended;
            public ExactJsonReadStream(Stream inner, int expected) { this.inner = inner; this.expected = expected; }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => expected;
            public override long Position { get => bytesRead; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
            public override int Read(Span<byte> buffer)
            {
                if (buffer.Length == 0 || ended) return 0;
                if (bytesRead == expected)
                {
                    if (inner.ReadByte() != -1)
                        throw new InvalidDataException("Snapshot decoded length exceeds its declared bound.");
                    ended = true; return 0;
                }
                int read = inner.Read(buffer.Slice(0, Math.Min(buffer.Length, expected - bytesRead)));
                if (read == 0)
                    throw new InvalidDataException("Snapshot decoded length is shorter than declared.");
                bytesRead += read; return read;
            }
            public void Complete()
            {
                // Deserialize(Stream) normally consumes through EOF. Retain an
                // explicit check even if a future serializer stops at the root:
                // remaining JSON whitespace is legal; a second value is not.
                Span<byte> tail = stackalloc byte[256]; int count;
                while ((count = Read(tail)) != 0)
                    for (int i = 0; i < count; i++)
                        if (tail[i] != 32 && tail[i] != 9 && tail[i] != 10 && tail[i] != 13)
                            throw new JsonException("Unexpected data after the snapshot JSON document.");
            }
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }

        public static EncodedSnapshot Encode(byte[] raw, int maximumRawBytes)
        {
            if (maximumRawBytes < 1 || raw == null || raw.Length < 1 || raw.Length > maximumRawBytes)
                throw new ArgumentException("Snapshot raw length is outside its supported bounds.");
            byte[] compressed;
            using (var output = new MemoryStream())
            {
                using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true)) gzip.Write(raw, 0, raw.Length);
                compressed = output.ToArray();
            }
            bool useGzip = compressed.Length < raw.Length;
            byte[] bytes = useGzip ? compressed : raw;
            return new EncodedSnapshot { Encoding = useGzip ? GzipEncoding : JsonEncoding,
                RawLength = raw.Length, Bytes = bytes, Hash = Convert.ToHexString(SHA256.HashData(bytes)) };
        }
        public static void ValidateHeader(string encoding, int rawLength, int encodedLength, int maximumRawBytes)
        {
            if (maximumRawBytes < 1 || rawLength < 1 || rawLength > maximumRawBytes ||
                encodedLength < 1 || encodedLength > maximumRawBytes ||
                (encoding != JsonEncoding && encoding != GzipEncoding) ||
                (encoding == JsonEncoding && encodedLength != rawLength))
                throw new ArgumentException("Snapshot encoding or length is outside its supported bounds.");
        }
        public static byte[] Decode(string encoding, int rawLength, string hash, byte[] encoded, int maximumRawBytes)
        {
            ValidateHeader(encoding, rawLength, encoded?.Length ?? 0, maximumRawBytes);
            if (hash == null || hash.Length != 64 ||
                !string.Equals(Convert.ToHexString(SHA256.HashData(encoded)), hash, StringComparison.Ordinal))
                throw new InvalidDataException("Snapshot encoded checksum mismatch.");
            if (encoding == JsonEncoding) return encoded;
            // Allocate only the declared, independently bounded output. Reading
            // beyond that exact size is an error, not an unbounded CopyTo growth.
            var raw = new byte[rawLength];
            using var input = new MemoryStream(encoded, false);
            using var gzip = new GZipStream(input, CompressionMode.Decompress, false);
            int offset = 0;
            while (offset < raw.Length)
            {
                int read = gzip.Read(raw, offset, raw.Length - offset);
                if (read == 0) throw new InvalidDataException("Snapshot decoded length is shorter than declared.");
                offset += read;
            }
            if (gzip.ReadByte() != -1) throw new InvalidDataException("Snapshot decoded length exceeds its declared bound.");
            return raw;
        }
    }
}
