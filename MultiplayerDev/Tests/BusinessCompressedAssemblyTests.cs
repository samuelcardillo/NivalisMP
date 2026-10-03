using System;
using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace NivalisTogether.Dedicated
{
    public static class DedicatedProtocol
    {public static readonly JsonSerializerOptions Json=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase};}
    public sealed class WorldEvent {public string Kind{get;set;}public JsonElement Data{get;set;}}
    public static class Time {public static float realtimeSinceStartup;}
    public sealed class BusinessState {public long Sequence{get;set;}public string Payload{get;set;}="";}
    public sealed class BusinessProcessorState {public string Id{get;set;}="";}
    public sealed class BusinessMealState {public string Id{get;set;}="";}
    public sealed class BusinessCraftState {public long Sequence{get;set;}public string Payload{get;set;}="";public BusinessProcessorState[] Processors{get;set;}=Array.Empty<BusinessProcessorState>();public BusinessMealState[] Meals{get;set;}=Array.Empty<BusinessMealState>();}

    public static partial class BusinessStateReplication
    {
        const int MaxBytes=16*1024*1024,ChunkBytes=24*1024;
        static long applied,incomingSequence;static byte[] incoming;
        static int incomingOffset,incomingRawLength,preparationGeneration;static string incomingHash,incomingEncoding;
        static float incomingStarted;public static string LastCaptureHash="";
        public static int NativeApplies;public static bool FailNative;public static string Payload;
        sealed class ApplyProfile {public long Started,DecodeJson,Validation,ManagedPreparation;public bool BackgroundPrepared,WireValidated;}
        static void ValidateWire(BusinessState state){}
        static void ApplyProfiled(BusinessState state,ApplyProfile profile)
        {if(FailNative)throw new InvalidOperationException("native transaction failed");NativeApplies++;applied=state.Sequence;Payload=state.Payload;}
        public static void Reset(){preparationGeneration++;applied=incomingSequence=0;incoming=null;incomingOffset=incomingRawLength=0;incomingHash=incomingEncoding=null;NativeApplies=0;LastCaptureHash="";FailNative=false;Payload=null;}
    }
    public static partial class BusinessCraftStateReplication
    {
        const int MaxBytes=16*1024*1024,ChunkBytes=24*1024;
        static bool clientAuthority=true;static long applied,incomingSequence;static byte[] incoming;
        static int incomingOffset,incomingRawLength,preparationGeneration;static string incomingHash,incomingEncoding;
        static float incomingStarted;public static string LastCaptureHash="";
        public static int NativeApplies;public static bool FailNative;public static string Payload;
        internal sealed class CraftProfile {public long Started,Decode,Validation,Preparation;public bool BackgroundPrepared;}
        static void Validate(BusinessCraftState state){}
        static void ApplyProfiled(BusinessCraftState state,CraftProfile profile,bool validated)
        {if(FailNative)throw new InvalidOperationException("native transaction failed");NativeApplies++;applied=state.Sequence;Payload=state.Payload;}
        public static void Reset(){preparationGeneration++;applied=incomingSequence=0;incoming=null;incomingOffset=incomingRawLength=0;incomingHash=incomingEncoding=null;NativeApplies=0;LastCaptureHash="";FailNative=false;Payload=null;}
    }
    // Production assembly, decode/preparation and commit-wrapper bodies are
    // appended by the runner. Toy payload DTO validation/native transactions
    // are substituted; focused preparation suites exercise the real validators.
    public static class BusinessCompressedAssemblyTests
    {
        sealed class Part
        {
            public long Sequence{get;set;}public string Hash{get;set;}
            public string Encoding{get;set;}public int RawLength{get;set;}
            public int Length{get;set;}public int Offset{get;set;}public byte[] Bytes{get;set;}
        }
        static int checks;
        static void Check(bool value,string why){checks++;if(!value)throw new Exception(why);}
        static void Reject(Action action,string why)
        {bool failed=false;try{action();}catch(ArgumentException){failed=true;}catch(InvalidDataException){failed=true;}catch(JsonException){failed=true;}Check(failed,why);}
        static Part Clone(Part part)=>JsonSerializer.Deserialize<Part>(JsonSerializer.Serialize(part,DedicatedProtocol.Json),DedicatedProtocol.Json);
        static Part[] Parts(EncodedSnapshot encoded,long sequence)
        {
            var result=new List<Part>();
            for(int offset=0;offset<encoded.Bytes.Length;offset+=24*1024){
                int size=Math.Min(24*1024,encoded.Bytes.Length-offset);var bytes=new byte[size];Buffer.BlockCopy(encoded.Bytes,offset,bytes,0,size);
                result.Add(new Part{Sequence=sequence,Hash=encoded.Hash,Encoding=encoded.Encoding,RawLength=encoded.RawLength,Length=encoded.Bytes.Length,Offset=offset,Bytes=bytes});
            }
            return result.ToArray();
        }
        public static int Run()
        {
            foreach(bool craft in new[]{false,true})RunFamily(craft);
            return checks;
        }
        static void RunFamily(bool craft)
        {
            string kind=craft?"business-craft-chunk":"business-state-chunk";
            void Reset(){Time.realtimeSinceStartup=0;if(craft)BusinessCraftStateReplication.Reset();else BusinessStateReplication.Reset();}
            int Applied()=>craft?BusinessCraftStateReplication.NativeApplies:BusinessStateReplication.NativeApplies;
            string Hash()=>craft?BusinessCraftStateReplication.LastCaptureHash:BusinessStateReplication.LastCaptureHash;
            void Feed(Part part){var evt=new WorldEvent{Kind=kind,Data=JsonSerializer.SerializeToElement(part,DedicatedProtocol.Json)};if(craft)BusinessCraftStateReplication.Apply(evt);else BusinessStateReplication.Apply(evt);}
            void FeedAll(Part[] parts){foreach(var part in parts)Feed(part);}
            var random=new byte[70000];new Random(142857).NextBytes(random);string payload=Convert.ToBase64String(random);
            var encoded=BoundedSnapshotCodec.EncodeJson(new BusinessState{Sequence=1,Payload=payload},16*1024*1024);
            var parts=Parts(encoded,1);Check(parts.Length>=3&&encoded.Encoding==BoundedSnapshotCodec.GzipEncoding,"fixture requires several compressed chunks");
            Reset();Feed(parts[0]);Check(Applied()==0,"no native mutation before complete assembly");
            for(int i=1;i<parts.Length;i++)Feed(parts[i]);
            Check(Applied()==1&&Hash()==encoded.Hash,"complete compressed batch applies once and publishes encoded checksum");
            Check((craft?BusinessCraftStateReplication.Payload:BusinessStateReplication.Payload)==payload,"entire decoded payload reaches transaction");
            FeedAll(parts);Check(Applied()==1,"already committed sequence does not reapply");

            foreach(string field in new[]{"encoding","rawLength","length","hash","sequence","offset"}){
                Reset();Feed(parts[0]);var changed=Clone(parts[1]);
                if(field=="encoding"){changed.Encoding=BoundedSnapshotCodec.JsonEncoding;changed.RawLength=changed.Length;}
                if(field=="rawLength")changed.RawLength++;
                if(field=="length")changed.Length++;
                if(field=="hash")changed.Hash=new string('0',64);
                if(field=="sequence")changed.Sequence++;
                if(field=="offset")changed.Offset++;
                Reject(()=>Feed(changed),"mixed batch "+field+" is rejected");Check(Applied()==0,"mixed metadata never reaches transaction");
                FeedAll(parts);Check(Applied()==1,"fresh complete batch recovers after rejected "+field);
            }
            Reset();Feed(parts[1]);Check(Applied()==0,"late join tail is ignored");FeedAll(parts);Check(Applied()==1,"late join applies next complete header batch");
            Reset();Feed(parts[0]);Feed(parts[1]);Reject(()=>Feed(parts[1]),"duplicate interior chunk cannot be appended twice");Check(Applied()==0,"duplicate chunk never partially applies");
            Reset();Feed(parts[0]);Reject(()=>Feed(parts[2]),"missing interior chunk rejected");
            Reset();Feed(parts[0]);Time.realtimeSinceStartup=31;Reject(()=>Feed(parts[1]),"assembly timeout rejected");Check(Applied()==0,"timeout never partially applies");
            Reset();Feed(parts[0]);FeedAll(parts);Check(Applied()==1,"new offset-zero header safely replaces incomplete assembly");

            foreach(string field in new[]{"encoding","rawLengthZero","rawLengthOversize","rawLengthNegative","offset","empty","hugechunk","hash"}){
                Reset();var invalid=Clone(parts[0]);
                if(field=="encoding")invalid.Encoding="zip-unknown";
                if(field=="rawLengthZero")invalid.RawLength=0;
                if(field=="rawLengthOversize")invalid.RawLength=16*1024*1024+1;
                if(field=="rawLengthNegative")invalid.RawLength=-1;
                if(field=="offset")invalid.Offset=-1;
                if(field=="empty")invalid.Bytes=Array.Empty<byte>();
                if(field=="hugechunk")invalid.Bytes=new byte[24*1024+1];
                if(field=="hash")invalid.Hash="bad";
                Reject(()=>Feed(invalid),"invalid header "+field+" rejected");Check(Applied()==0,"invalid header never mutates native state");
            }
            Reset();FeedAll(parts);var stale=Clone(parts[0]);stale.RawLength=16*1024*1024+1;
            Reject(()=>Feed(stale),"malformed stale header still bounded before sequence skip");
            foreach(int delta in new[]{-1,1}){
                Reset();var wrong=Parts(encoded,1);foreach(var part in wrong)part.RawLength+=delta;
                Reject(()=>FeedAll(wrong),"exact decoded raw length enforced "+delta);Check(Applied()==0,"wrong raw length never reaches native apply");
            }
            Reset();var damaged=Parts(encoded,1);damaged[1].Bytes[0]^=1;
            Reject(()=>FeedAll(damaged),"encoded checksum verified before native mutation");Check(Applied()==0,"corrupt compressed batch never mutates native state");
            Reset();Reject(()=>FeedAll(Parts(encoded,2)),"envelope and decoded sequence must match");Check(Applied()==0,"sequence mismatch never mutates native state");

            Reset();var raw=JsonSerializer.SerializeToUtf8Bytes(new BusinessState{Sequence=3,Payload="legacy"},DedicatedProtocol.Json);
            var legacy=new Part{Sequence=3,Hash=Convert.ToHexString(SHA256.HashData(raw)),Encoding=BoundedSnapshotCodec.JsonEncoding,RawLength=0,Length=raw.Length,Bytes=raw};
            Feed(legacy);Check(Applied()==1,"legacy json-v1 with absent raw length remains readable");
            var defaultDto=JsonSerializer.SerializeToElement(new{sequence=4,hash=legacy.Hash,length=raw.Length,offset=0,bytes=raw},DedicatedProtocol.Json);
            Check((craft?defaultDto.Deserialize<BusinessCraftChunk>(DedicatedProtocol.Json).Encoding:defaultDto.Deserialize<BusinessStateChunk>(DedicatedProtocol.Json).Encoding)==BoundedSnapshotCodec.JsonEncoding,"absent encoding defaults to legacy JSON");
            Reset();if(craft)BusinessCraftStateReplication.FailNative=true;else BusinessStateReplication.FailNative=true;
            bool failed=false;try{FeedAll(parts);}catch(InvalidOperationException){failed=true;}
            Check(failed&&Applied()==0&&Hash()=="","failed native transaction does not publish transport checksum or revision");
            if(craft)BusinessCraftStateReplication.FailNative=false;else BusinessStateReplication.FailNative=false;
            FeedAll(parts);Check(Applied()==1,"same authoritative revision can retry after native transaction failure");
        }
    }
}
