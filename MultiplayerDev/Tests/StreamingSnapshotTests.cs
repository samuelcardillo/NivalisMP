using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NivalisTogether.Dedicated;

internal static class StreamingSnapshotTests
{
    private static int checks;
    private static long consumed;
    private static void Check(bool condition,string reason){checks++;if(!condition)throw new Exception(reason);}
    private static void Reject(Action action,string reason){bool failed=false;try{action();}catch(Exception ex)when(ex is ArgumentException||ex is InvalidDataException||ex is JsonException||ex is InvalidOperationException){failed=true;}Check(failed,reason);}
    private static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes));
    private static byte[] Zip(byte[] bytes){using var output=new MemoryStream();using(var zip=new GZipStream(output,CompressionLevel.Fastest,true))zip.Write(bytes);return output.ToArray();}
    private static T Decode<T>(EncodedSnapshot value,int max)=>BoundedSnapshotCodec.DecodeJson<T>(value.Encoding,value.RawLength,value.Hash,value.Bytes,max);
    private static void Parity<T>(T value,string label)
    {
        byte[] raw=JsonSerializer.SerializeToUtf8Bytes(value,DedicatedProtocol.Json);int max=Math.Max(raw.Length,128);
        var old=BoundedSnapshotCodec.Encode(raw,max);var next=BoundedSnapshotCodec.EncodeJson(value,max);
        Check(next.RawLength==raw.Length,label+" exact canonical length");
        Check(next.Encoding==old.Encoding,label+" encoding selection parity");
        Check(next.Bytes.SequenceEqual(old.Bytes),label+" exact compressed/raw bytes");
        Check(next.Hash==old.Hash,label+" exact encoded hash");
        Check(BoundedSnapshotCodec.Decode(next.Encoding,next.RawLength,next.Hash,next.Bytes,max).SequenceEqual(raw),label+" legacy decoder compatibility");
        var restored=Decode<T>(next,max);var prior=JsonSerializer.Deserialize<T>(BoundedSnapshotCodec.Decode(old.Encoding,old.RawLength,old.Hash,old.Bytes,max),DedicatedProtocol.Json);
        Check(JsonSerializer.SerializeToUtf8Bytes(restored,DedicatedProtocol.Json).SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(prior,DedicatedProtocol.Json)),label+" streaming JSON result parity");
    }
    private sealed class Snapshot { public long Sequence{get;set;} public string Name{get;set;}=""; public int[][] Rows{get;set;}=Array.Empty<int[]>(); }
    private static Snapshot State(int count)
    {
        var rows=new int[count][];
        for(int i=0;i<count;i++)rows[i]=new[]{i%23,i%127,i%5000,unchecked(i*123457)};
        return new Snapshot{Sequence=1641,Name="native snapshot + <>& café 漢字",Rows=rows};
    }
    private static void ParityCases()
    {
        Parity<object>(null,"null");Parity(true,"boolean");Parity(-123.4500m,"decimal");
        Parity(new{Text="\"<>&+'\\\n\r\t café 漢字 😀 \ud800",Low=long.MinValue,High=ulong.MaxValue},"escaping");
        Parity(State(4000),"multi-buffer row DTO");Parity(Enumerable.Range(0,60000).ToArray(),"large numeric array");
        var random=new Random(591);var randomBytes=new byte[65536];random.NextBytes(randomBytes);
        Parity(new SnapshotChunk{TransferId="transfer",Offset=0,Data=randomBytes},"large base64 token");
        using var raw=JsonDocument.Parse(" { \"text\":\"+é<>\",\"n\":1e2,\"dup\":1,\"dup\":2,\"array\":[ null, true ] } ");
        Parity(raw.RootElement,"noncanonical element");
        for(int length=0;length<=24;length++){
            string value=new string(Enumerable.Range(0,length).Select(_=>(char)random.Next(33,90)).ToArray());
            byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(value,DedicatedProtocol.Json);
            var encoded=BoundedSnapshotCodec.EncodeJson(value,bytes.Length);
            Check(encoded.Bytes.Length<=bytes.Length,"Encoded output bounded even when gzip header would exceed maximum");
            Check(Decode<string>(encoded,bytes.Length)==value,"Tiny/incompressible maximum-size fallback roundtrip");
            if(Zip(bytes).Length>=bytes.Length)Check(encoded.Encoding==BoundedSnapshotCodec.JsonEncoding&&encoded.Bytes.SequenceEqual(bytes),"Tiny/incompressible fallback retains exact raw JSON");
        }
    }
    private static void LimitsAndFailures()
    {
        var state=State(1000);byte[] raw=JsonSerializer.SerializeToUtf8Bytes(state,DedicatedProtocol.Json);
        var encoded=BoundedSnapshotCodec.EncodeJson(state,raw.Length);
        Check(encoded.Encoding==BoundedSnapshotCodec.GzipEncoding,"Limit cases use actual gzip path");
        Check(Decode<Snapshot>(encoded,raw.Length).Rows.Length==1000,"Exact maximum raw length accepted");
        Reject(()=>BoundedSnapshotCodec.EncodeJson(state,raw.Length-1),"Serialize one byte over raw limit rejected during stream");
        Reject(()=>BoundedSnapshotCodec.EncodeJson(state,0),"Invalid encoding bound rejected");
        Reject(()=>BoundedSnapshotCodec.EncodeJson(default(JsonElement),100),"Undefined JSON rejected");
        Reject(()=>Decode<Snapshot>(encoded,raw.Length-1),"Declared raw length over bound rejected before decompression");
        Reject(()=>BoundedSnapshotCodec.DecodeJson<Snapshot>(encoded.Encoding,raw.Length-1,encoded.Hash,encoded.Bytes,raw.Length),"Expanded bytes beyond declaration rejected");
        Reject(()=>BoundedSnapshotCodec.DecodeJson<Snapshot>(encoded.Encoding,raw.Length+1,encoded.Hash,encoded.Bytes,raw.Length+1),"Premature decompression EOF rejected");
        Reject(()=>BoundedSnapshotCodec.DecodeJson<Snapshot>("unknown",raw.Length,encoded.Hash,encoded.Bytes,raw.Length),"Unknown encoding rejected");
        Reject(()=>BoundedSnapshotCodec.DecodeJson<Snapshot>(encoded.Encoding,raw.Length,null,encoded.Bytes,raw.Length),"Missing hash rejected");
        Reject(()=>BoundedSnapshotCodec.DecodeJson<Snapshot>(encoded.Encoding,raw.Length,encoded.Hash.ToLowerInvariant(),encoded.Bytes,raw.Length),"Hash comparison semantics preserved");
        var corrupt=(byte[])encoded.Bytes.Clone();corrupt[corrupt.Length/2]^=0x80;
        Reject(()=>BoundedSnapshotCodec.DecodeJson<Snapshot>(encoded.Encoding,raw.Length,encoded.Hash,corrupt,raw.Length),"Tampered encoded bytes rejected by hash");
        var badCrc=(byte[])encoded.Bytes.Clone();badCrc[badCrc.Length-8]^=0x80;
        Reject(()=>BoundedSnapshotCodec.DecodeJson<Snapshot>(encoded.Encoding,raw.Length,Hash(badCrc),badCrc,raw.Length),"Corrupted gzip trailer rejected even with recomputed encoded hash");
        var truncated=encoded.Bytes.Take(encoded.Bytes.Length/2).ToArray();
        Reject(()=>BoundedSnapshotCodec.DecodeJson<Snapshot>(encoded.Encoding,raw.Length,Hash(truncated),truncated,raw.Length),"Truncated compressed body rejected");
        Reject(()=>BoundedSnapshotCodec.DecodeJson<Snapshot>(encoded.Encoding,raw.Length,encoded.Hash,null,raw.Length),"Null body rejected");
        Reject(()=>BoundedSnapshotCodec.DecodeJson<Snapshot>(encoded.Encoding,raw.Length,encoded.Hash,Array.Empty<byte>(),raw.Length),"Empty body rejected");
        Reject(()=>BoundedSnapshotCodec.DecodeJson<int>(BoundedSnapshotCodec.JsonEncoding,3,Hash(new byte[]{49,50}),new byte[]{49,50},128),"Raw JSON length declaration mismatch rejected");
        foreach(string text in new[]{"{}{}","{} 7","{} /* trailing */","{ broken",new string('[',25)+"0"+new string(']',25)}){
            byte[] bytes=Encoding.UTF8.GetBytes(text);byte[] zip=Zip(bytes);int max=Math.Max(bytes.Length,zip.Length);
            Reject(()=>BoundedSnapshotCodec.DecodeJson<JsonElement>(BoundedSnapshotCodec.JsonEncoding,bytes.Length,Hash(bytes),bytes,max),"Raw invalid/trailing/deep JSON rejected");
            Reject(()=>BoundedSnapshotCodec.DecodeJson<JsonElement>(BoundedSnapshotCodec.GzipEncoding,bytes.Length,Hash(zip),zip,max),"Compressed invalid/trailing/deep JSON rejected");
        }
        var whitespace=Encoding.UTF8.GetBytes("{\"ok\":true}"+new string(' ',50000)+"\r\n\t");var whitespaceZip=Zip(whitespace);
        var read=BoundedSnapshotCodec.DecodeJson<JsonElement>(BoundedSnapshotCodec.GzipEncoding,whitespace.Length,Hash(whitespaceZip),whitespaceZip,whitespace.Length);
        Check(read.GetProperty("ok").GetBoolean(),"Valid trailing whitespace consumed through exact EOF");
        Reject(()=>BoundedSnapshotCodec.DecodeJson<JsonElement>(BoundedSnapshotCodec.GzipEncoding,whitespace.Length-1,Hash(whitespaceZip),whitespaceZip,whitespace.Length),"Trailing whitespace also counts toward declared limit");
        var one=Zip(Encoding.UTF8.GetBytes("{}"));var two=Zip(Encoding.UTF8.GetBytes(" 9"));var concatenated=one.Concat(two).ToArray();
        Reject(()=>BoundedSnapshotCodec.DecodeJson<JsonElement>(BoundedSnapshotCodec.GzipEncoding,5,Hash(concatenated),concatenated,128),"Concatenated gzip second JSON value rejected");
        Check(Decode<Snapshot>(encoded,raw.Length).Rows.Length==1000,"Failed reads do not leak state into later snapshots");
    }
    private sealed class Measurement {public string Name{get;set;}public long AllocatedBytesPerSnapshot{get;set;}public double MillisecondsPerSnapshot{get;set;}public int Gen0{get;set;}public int Gen1{get;set;}public int Gen2{get;set;}}
    private static Measurement Measure(string name,Action action,int iterations)
    {
        for(int i=0;i<3;i++)action();GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
        int g0=GC.CollectionCount(0),g1=GC.CollectionCount(1),g2=GC.CollectionCount(2);long start=GC.GetAllocatedBytesForCurrentThread();var time=Stopwatch.StartNew();
        for(int i=0;i<iterations;i++)action();time.Stop();
        return new Measurement{Name=name,AllocatedBytesPerSnapshot=(GC.GetAllocatedBytesForCurrentThread()-start)/iterations,MillisecondsPerSnapshot=time.Elapsed.TotalMilliseconds/iterations,Gen0=GC.CollectionCount(0)-g0,Gen1=GC.CollectionCount(1)-g1,Gen2=GC.CollectionCount(2)-g2};
    }
    private static object Benchmark()
    {
        const int iterations=8,max=32*1024*1024;var state=State(319000);byte[] raw=JsonSerializer.SerializeToUtf8Bytes(state,DedicatedProtocol.Json);var encoded=BoundedSnapshotCodec.Encode(raw,max);
        var oldEncode=Measure("legacy-json-array-then-gzip",()=>consumed+=BoundedSnapshotCodec.Encode(JsonSerializer.SerializeToUtf8Bytes(state,DedicatedProtocol.Json),max).Bytes.Length,iterations);
        var nextEncode=Measure("stream-json-to-gzip",()=>consumed+=BoundedSnapshotCodec.EncodeJson(state,max).Bytes.Length,iterations);
        var oldDecode=Measure("legacy-gzip-array-then-json",()=>consumed+=JsonSerializer.Deserialize<Snapshot>(BoundedSnapshotCodec.Decode(encoded.Encoding,encoded.RawLength,encoded.Hash,encoded.Bytes,max),DedicatedProtocol.Json).Rows.Length,iterations);
        var nextDecode=Measure("stream-gzip-to-json",()=>consumed+=Decode<Snapshot>(encoded,max).Rows.Length,iterations);
        Check(oldEncode.AllocatedBytesPerSnapshot-nextEncode.AllocatedBytesPerSnapshot>=raw.Length-65536,"Streaming encode removes full uncompressed snapshot allocation");
        Check(oldDecode.AllocatedBytesPerSnapshot-nextDecode.AllocatedBytesPerSnapshot>=raw.Length-65536,"Streaming decode removes full uncompressed snapshot allocation");
        return new{Runtime=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,Rows=state.Rows.Length,RawBytes=raw.Length,EncodedBytes=encoded.Bytes.Length,Iterations=iterations,Measurements=new[]{oldEncode,nextEncode,oldDecode,nextDecode},Consumed=consumed};
    }
    public static int Main(string[] args)
    {
        try{ParityCases();LimitsAndFailures();var benchmark=Benchmark();if(args.Length>0)File.WriteAllText(args[0],JsonSerializer.Serialize(new{PassedAssertions=checks,Benchmark=benchmark},new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine("PASS StreamingSnapshotTests: "+checks+" assertions; exact codec parity, bounded complete JSON, corruption/trailing rejection and 319k-row allocation benchmark.");return 0;}
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
