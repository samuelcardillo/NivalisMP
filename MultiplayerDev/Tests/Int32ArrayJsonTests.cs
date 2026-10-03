using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NivalisTogether.Dedicated;

internal static class ExperimentOptions
{
    internal static readonly JsonSerializerOptions Baseline = new JsonSerializerOptions { MaxDepth = 24, PropertyNameCaseInsensitive = false };
    internal static readonly JsonSerializerOptions Candidate = DedicatedProtocol.Json;
    internal static JsonSerializerOptions Current = Baseline;

}

internal static class Int32ArrayJsonTests
{
    private static int checks;
    private static long consumed;
    private static void Check(bool value, string why) { checks++; if (!value) throw new Exception(why); }
    private static void Cases()
    {
        foreach (int length in new[] { 0, 1, 2, 4, 8, 9, 100, 65536 })
        {
            int[] values = Enumerable.Range(0, length).Select(i => i % 2 == 0 ? int.MinValue + i : int.MaxValue - i).ToArray();
            byte[] old = JsonSerializer.SerializeToUtf8Bytes(values, ExperimentOptions.Baseline);
            Check(old.SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(values, ExperimentOptions.Candidate)), "exact wire " + length);
            Check(JsonSerializer.Deserialize<int[]>(old, ExperimentOptions.Candidate).SequenceEqual(values), "direct parity " + length);
            using var stream = new OneByteStream(old);
            Check(JsonSerializer.Deserialize<int[]>(stream, ExperimentOptions.Candidate).SequenceEqual(values), "stream fragmented parity " + length);
        }
        Check(JsonSerializer.Deserialize<int[]>("null", ExperimentOptions.Candidate) == null, "null parity");
        Check(JsonSerializer.Serialize<int[]>(null, ExperimentOptions.Candidate) == "null", "null write parity");
        foreach (string text in new[] { "{}", "1", "[null]", "[true]", "[\"1\"]", "[1.0]", "[1e0]", "[2147483648]", "[-2147483649]", "[1,]", "[1] 2", "[[1]]", "[1", "[NaN]" })
        {
            bool old = Reject(text, ExperimentOptions.Baseline), next = Reject(text, ExperimentOptions.Candidate);
            Check(old && next, "reject parity " + text);
        }
        string rows = "{\"Sequence\":1,\"Inventory\":[],\"Vendors\":[{\"VendorId\":\"vendor\",\"Stacks\":[{\"Type\":\"type\",\"Items\":[null,[],[1,2,3,4]]}]}]}";
        var baseline = JsonSerializer.Deserialize<SharedEconomyState>(rows, ExperimentOptions.Baseline);
        var candidate = JsonSerializer.Deserialize<SharedEconomyState>(rows, ExperimentOptions.Candidate);
        Check(JsonSerializer.Serialize(candidate, ExperimentOptions.Baseline) == JsonSerializer.Serialize(baseline, ExperimentOptions.Baseline), "actual DTO null and rows parity");
    }
    private static bool Reject(string text, JsonSerializerOptions options) { try { JsonSerializer.Deserialize<int[]>(text, options); return false; } catch (JsonException) { return true; } }
    private sealed class OneByteStream : MemoryStream
    {
        internal OneByteStream(byte[] value) : base(value, false) { }
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(1, count));
        public override int Read(Span<byte> buffer) => base.Read(buffer.Slice(0, Math.Min(1, buffer.Length)));
    }
    private sealed class Measurement { public string Label { get; set; } public long BytesPerSnapshot { get; set; } public double MsPerSnapshot { get; set; } }
    private static Measurement Measure(string label, Func<SharedEconomyState> action)
    {
        for (int i = 0; i < 2; i++) consumed += action().Vendors.Length;
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
        for (int i = 0; i < 5; i++) consumed += action().Vendors.Length;
        return new Measurement { Label = label, BytesPerSnapshot = (GC.GetAllocatedBytesForCurrentThread() - before) / 5, MsPerSnapshot = (Stopwatch.GetTimestamp() - start) * 1000d / Stopwatch.Frequency / 5 };
    }
    private static object Benchmark()
    {
        // Representative synthetic values, not captured player/world data. Actual DTO classes extracted at build.
        const int vendors = 319, perVendor = 1000, max = 16 * 1024 * 1024;
        var state = new SharedEconomyState { Sequence = 1, Vendors = Enumerable.Range(0, vendors).Select(v => new SharedVendorStock { VendorId = "vendor-" + v, Stacks = new[] { new SharedItemStack { Type = "type-" + v, Items = Enumerable.Range(0, perVendor).Select(i => new[] { i % 23, i % 127, i % 5000, unchecked(i * 123457) }).ToArray() } } }).ToArray() };
        byte[] raw = JsonSerializer.SerializeToUtf8Bytes(state, ExperimentOptions.Baseline);
        var encoded = BoundedSnapshotCodec.Encode(raw, max);
        Check(raw.SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(state, ExperimentOptions.Candidate)), "whole DTO exact serialization parity");
        var measured = new List<Measurement>();
        foreach (var option in new[] { ExperimentOptions.Baseline, ExperimentOptions.Candidate })
        {
            ExperimentOptions.Current = option;
            string label = ReferenceEquals(option, ExperimentOptions.Baseline) ? "default" : "small-int-array";
            measured.Add(Measure(label + "-raw", () => JsonSerializer.Deserialize<SharedEconomyState>(raw, option)));
            measured.Add(Measure(label + "-actual-streaming-codec", () => ReferenceEquals(option, ExperimentOptions.Baseline) ? BaselineSnapshotCodec.DecodeJson<SharedEconomyState>(encoded.Encoding, encoded.RawLength, encoded.Hash, encoded.Bytes, max) : BoundedSnapshotCodec.DecodeJson<SharedEconomyState>(encoded.Encoding, encoded.RawLength, encoded.Hash, encoded.Bytes, max)));
        }
        return new { rowCount = vendors * perVendor, rawBytes = raw.Length, encodedBytes = encoded.Bytes.Length, measured };
    }
    internal static int Main(string[] args)
    {
        Cases(); object benchmark = Benchmark();
        var report = new { framework = RuntimeInformation.FrameworkDescription, checks, fixture = "synthetic 319k four-int rows; actual production DTO extracted, actual registered converter and codec compiled; baseline codec differs only in options", benchmark, consumed };
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(args[0], json); Console.WriteLine(json); return 0;
    }
}
