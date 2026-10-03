using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using NivalisTogether.Dedicated;

internal static class CanonicalJsonTests
{
    private static int checks;
    private static long consumed;
    private static readonly byte[] Key = Enumerable.Range(0,32).Select(x => (byte)x).ToArray();
    private static void Check(bool value,string reason) { checks++; if(!value) throw new Exception(reason); }
    private static void Reject(Action action,string reason) { bool failed=false; try { action(); } catch(Exception ex) when(ex is JsonException || ex is InvalidOperationException || ex is InvalidDataException || ex is IOException || ex is ArgumentException) { failed=true; } Check(failed,reason); }
    // Intentionally keep the pre-change implementation as the independent oracle.
    private static string OldMac<T>(T value,byte[] key,bool server)
    {
        var prefix=Encoding.ASCII.GetBytes(server?"server-to-client\n":"client-to-server\n");
        var json=JsonSerializer.SerializeToUtf8Bytes(value,DedicatedProtocol.Json);
        var signed=new byte[prefix.Length+json.Length];
        Buffer.BlockCopy(prefix,0,signed,0,prefix.Length);Buffer.BlockCopy(json,0,signed,prefix.Length,json.Length);
        using var hash=new HMACSHA256(key);return Convert.ToHexString(hash.ComputeHash(signed));
    }
    private static void Parity<T>(T value,string label)
    {
        Check(CanonicalJson.ByteCount(value)==JsonSerializer.SerializeToUtf8Bytes(value,DedicatedProtocol.Json).Length,label+" canonical byte length");
        foreach(bool server in new[]{false,true}) Check(CanonicalJson.HmacHex(value,Key,server)==OldMac(value,Key,server),label+" canonical byte stream/MAC parity");
    }
    private static WireMessage Envelope(JsonElement payload,ulong sequence=1) => new WireMessage {Type="worldEvent",RequestId="abc",SessionId="00112233445566778899aabbccddeeff",Sequence=sequence,Payload=payload};
    private static void CanonicalAndMutableCases()
    {
        Parity<object>(null,"null");
        Parity(new { Text="quotes\" <>&+'\\\n\r\t café 漢字 😀 \ud800", Minimum=long.MinValue, Maximum=ulong.MaxValue, Number=-0.0 },"escaping and numbers");
        var random=new Random(371);var bytes=new byte[32768];random.NextBytes(bytes);
        Parity(new SnapshotChunk {TransferId="transfer",Offset=0,Data=bytes},"native byte-array converter");
        var payload=JsonSerializer.SerializeToElement(new SnapshotChunk {TransferId="transfer",Offset=0,Data=bytes},DedicatedProtocol.Json);
        Parity(payload,"nested escaped base64");Parity(Envelope(payload),"wire envelope");
        using var raw=JsonDocument.Parse(" { \"x\": \"+é<>\", \"num\": 1e2, \"same\": 1, \"same\": 2, \"nested\": [ true ,null ] } ");
        Parity(raw.RootElement,"noncanonical JSON and duplicate properties");
        var message=Envelope(raw.RootElement.Clone());var first=CanonicalJson.HmacHex(message,Key,true);
        message.Sequence++;Check(CanonicalJson.HmacHex(message,Key,true)!=first,"No stale sequence hash cache");Parity(message,"changed sequence");
        message.SessionId=Guid.NewGuid().ToString("N");Parity(message,"changed session");
        message.Payload=payload;Parity(message,"changed payload");
        var expected=CanonicalJson.HmacHex(message,Key,true);
        Check(CanonicalJson.HmacHex(message,Key,false)!=expected,"Direction domain separation retained");
        Check(CanonicalJson.HmacHex(message,Enumerable.Repeat((byte)27,32).ToArray(),true)!=expected,"Key separation retained");
        for(int i=0;i<12;i++){Parity(i%2==0?Envelope(payload):Envelope(raw.RootElement.Clone()),"repeated independent hash "+i);}
        Reject(()=>CanonicalJson.ByteCount(default(JsonElement)),"Undefined JSON rejected");
        Reject(()=>CanonicalJson.HmacHex(default(JsonElement),Key,true),"Failed serialization rejected during hash");
        Check(CanonicalJson.HmacHex(message,Key,true)==expected,"Failed hash cannot contaminate next envelope");
        using var deep=JsonDocument.Parse(new string('[',25)+"0"+new string(']',25));
        Reject(()=>CanonicalJson.ByteCount(deep.RootElement),"Canonical maximum depth remains enforced");
        Reject(()=>CanonicalJson.HmacHex(deep.RootElement,Key,true),"MAC maximum depth remains enforced");
        Parity(message,"hash reset after depth failure");
        message.ReceivedFrameBytes=12345;
        Check(!JsonSerializer.Serialize(message,DedicatedProtocol.Json).Contains("ReceivedFrameBytes"),"Local byte counter absent from wire schema");
        Check(CanonicalJson.HmacHex(message,Key,true)==expected,"Telemetry never enters authentication");
        var untrusted=JsonSerializer.Deserialize<WireMessage>(JsonSerializer.Serialize(message,DedicatedProtocol.Json).TrimEnd('}')+",\"ReceivedFrameBytes\":2147483647}",DedicatedProtocol.Json);
        Check(untrusted.ReceivedFrameBytes==0,"Peer cannot assign local byte counter");
    }
    private static void FlowBounds()
    {
        WorldEvent Event(int count,string kind="npc-state-chunk")=>new WorldEvent {Kind=kind,Data=JsonSerializer.SerializeToElement(new string('a',count),DedicatedProtocol.Json)};
        var limit=Event(47998);Check(CanonicalJson.ByteCount(limit.Data)==48000&&WorldEventFlow.Cost(limit)==49024,"Exact payload limit retained");
        Reject(()=>WorldEventFlow.Cost(Event(47999)),"Payload one byte over limit rejected");
        Check(WorldEventFlow.Cost(Event(23998,"group-sleep"))==25024,"Exact control limit retained");
        Reject(()=>WorldEventFlow.Cost(Event(23999,"group-sleep")),"Oversize sleep control rejected");
        Reject(()=>WorldEventFlow.Cost(Event(23999,"group-apartment")),"Oversize apartment control rejected");
        using var escaped=JsonDocument.Parse("{\"s\":\""+new string('+',8100)+"\"}");
        var tooEscaped=new WorldEvent {Kind="npc-state-chunk",Data=escaped.RootElement};
        Check(Encoding.UTF8.GetByteCount(escaped.RootElement.GetRawText())<48000,"Escaped rejection exercises short raw input");
        Reject(()=>WorldEventFlow.Cost(tooEscaped),"Canonical escaping cannot bypass payload bound");
        var item=new WorldEvent{Kind="npc-state-chunk",Data=JsonSerializer.SerializeToElement(new{Body=new string('a',42000)})};var queue=new WorldEventScheduler();var entry=queue.Enqueue(item);
        Check(ReferenceEquals(entry.Event,item)&&entry.PayloadBytes==JsonSerializer.SerializeToUtf8Bytes(item,DedicatedProtocol.Json).Length,"Enqueue returns exact charged entry");
        Check(queue.PayloadBytes==entry.PayloadBytes&&queue.Count==1,"Queue credit accounting unchanged");queue.Clear();
        item.DeliveryId=long.MaxValue;item.Revision=long.MaxValue;item.WorldId=Guid.NewGuid().ToString("N");
        var next=queue.Enqueue(item);Check(next.PayloadBytes>entry.PayloadBytes,"Changed mutable delivery fields remeasured");
        Check(next.PayloadBytes==CanonicalJson.ByteCount(item),"Mutable enqueue size exact");queue.Clear();
        item.Data=JsonSerializer.SerializeToElement(new{Body=new string('a',100)});var small=queue.Enqueue(item);Check(small.Cost==2048&&small.PayloadBytes<next.PayloadBytes,"Changed payload remeasured without stale cache");
    }
    private sealed class RawPeer : IDisposable
    {
        public readonly TcpClient Raw;public readonly DedicatedConnection Receiver;
        public RawPeer(bool authenticated=true)
        {
            var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();Raw=new TcpClient();Raw.Connect(IPAddress.Loopback,((IPEndPoint)listener.LocalEndpoint).Port);
            Receiver=DedicatedConnection.FromAccepted(listener.AcceptTcpClient());listener.Stop();if(authenticated)Receiver.SetAuthenticationKey(Key);
        }
        public WireMessage Message()=>new WireMessage{Type="worldEvent",SessionId=Receiver.SessionId,Sequence=1,Payload=JsonSerializer.SerializeToElement(new {Text="+<>&é",Number=1})};
        public byte[] Send(WireMessage message,bool direction=false,string suffix="")
        {
            message.Mac=OldMac(message,Key,direction);var json=JsonSerializer.Serialize(message,DedicatedProtocol.Json);
            if(suffix.Length>0)json=json.TrimEnd('}')+suffix+"}";
            return SendText(json);
        }
        public byte[] SendText(string json)
        {
            var bytes=Encoding.UTF8.GetBytes(json);var frame=new byte[bytes.Length+4];frame[0]=(byte)(bytes.Length>>24);frame[1]=(byte)(bytes.Length>>16);frame[2]=(byte)(bytes.Length>>8);frame[3]=(byte)bytes.Length;
            Buffer.BlockCopy(bytes,0,frame,4,bytes.Length);Raw.GetStream().Write(frame);return frame;
        }
        public void Pump(){for(int i=0;i<25&&Receiver.Connected;i++){Receiver.Tick();Thread.Sleep(1);}}
        public void Dispose(){Receiver.Dispose();Raw.Dispose();}
    }
    private static void TransportSecurity()
    {
        using(var peer=new RawPeer()){
            var message=peer.Message();var frame=peer.Send(message,false,",\"ReceivedFrameBytes\":999999");peer.Pump();
            WireMessage received=null;Check(peer.Receiver.Connected&&peer.Receiver.TryReceive(out received),"Legacy canonical MAC accepted over socket");
            Check(received.ReceivedFrameBytes==frame.Length,"Actual parsed frame bytes including header override untrusted telemetry");
            Check(received.Payload.GetProperty("Text").GetString()=="+<>&é","Signed escaped payload preserved");
            message.Sequence=2;message.Mac="";frame=peer.Send(message);peer.Pump();
            Check(peer.Receiver.TryReceive(out received)&&received.Sequence==2&&received.ReceivedFrameBytes==frame.Length,"Successive authenticated envelopes and local sizes remain independent");
            message.Mac="";peer.Send(message);peer.Pump();Check(!peer.Receiver.Connected&&!peer.Receiver.TryReceive(out _),"Replay sequence rejected before dispatch");
        }
        using(var peer=new RawPeer()){peer.Send(peer.Message(),true);peer.Pump();Check(!peer.Receiver.Connected&&!peer.Receiver.TryReceive(out _),"Reflected server direction rejected");}
        using(var peer=new RawPeer()){
            var message=peer.Message();message.Mac=OldMac(message,Key,false);message.Payload=JsonSerializer.SerializeToElement(new{Text="altered",Number=1});
            peer.SendText(JsonSerializer.Serialize(message,DedicatedProtocol.Json));peer.Pump();Check(!peer.Receiver.Connected&&!peer.Receiver.TryReceive(out _),"Payload tampering rejected");
        }
        using(var peer=new RawPeer()){peer.SendText("{ broken JSON");peer.Pump();Check(!peer.Receiver.Connected&&!peer.Receiver.TryReceive(out _),"Malformed frame rejected");}
        using(var peer=new RawPeer()){peer.SendText(new string(' ',65537));peer.Pump();Check(!peer.Receiver.Connected&&!peer.Receiver.TryReceive(out _),"Oversize framing rejected");}
        using(var peer=new RawPeer()){Check(!peer.Receiver.Send("worldEvent",new{Text=new string('x',65536)}),"Outgoing frame cap retained");Check(peer.Receiver.Connected,"Rejected oversize send does not corrupt transport");}
    }
    private sealed class Measurement { public string Name {get;set;} public long BytesPerMessage {get;set;} public double MicrosecondsPerMessage {get;set;} public int Gen0Collections {get;set;} public int Gen1Collections {get;set;} public int Gen2Collections {get;set;} }
    private static Measurement Measure(string name,Action action,int count=200)
    {
        for(int i=0;i<30;i++)action();GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
        int gen0=GC.CollectionCount(0),gen1=GC.CollectionCount(1),gen2=GC.CollectionCount(2);
        long allocated=GC.GetAllocatedBytesForCurrentThread();var watch=Stopwatch.StartNew();for(int i=0;i<count;i++)action();watch.Stop();
        return new Measurement{Name=name,BytesPerMessage=(GC.GetAllocatedBytesForCurrentThread()-allocated)/count,MicrosecondsPerMessage=watch.Elapsed.TotalMilliseconds*1000/count,Gen0Collections=GC.CollectionCount(0)-gen0,Gen1Collections=GC.CollectionCount(1)-gen1,Gen2Collections=GC.CollectionCount(2)-gen2};
    }
    private static object Benchmark()
    {
        var results=new List<object>();var random=new Random(194);
        foreach(int bytesLength in new[]{28000,31000,33000}){
            var bytes=new byte[bytesLength];random.NextBytes(bytes);
            var data=JsonSerializer.SerializeToElement(new SnapshotChunk {TransferId="0123456789abcdef0123456789abcdef",Offset=32768,Data=bytes},DedicatedProtocol.Json);
            var evt=new WorldEvent {Kind="npc-state-chunk",WorldId="0123456789abcdef0123456789abcdef",Revision=1641,DeliveryId=973,Data=data};
            var message=Envelope(JsonSerializer.SerializeToElement(evt,DedicatedProtocol.Json),17);
            int payloadBytes=CanonicalJson.ByteCount(data),wireBytes=CanonicalJson.ByteCount(message)+68;
            Check(payloadBytes>=40000&&payloadBytes<=48000,"Benchmark uses realistic canonical 40–48KiB chunk");
            var oldSign=Measure("old-sign",()=>consumed+=OldMac(message,Key,true).Length);
            var newSign=Measure("stream-sign",()=>consumed+=CanonicalJson.HmacHex(message,Key,true).Length);
            var oldCost=Measure("old-cost-and-queue-size",()=>{consumed+=JsonSerializer.SerializeToUtf8Bytes(data,DedicatedProtocol.Json).Length;consumed+=JsonSerializer.SerializeToUtf8Bytes(evt,DedicatedProtocol.Json).Length;});
            var newCost=Measure("stream-cost-and-queue-size",()=>{consumed+=CanonicalJson.ByteCount(data);consumed+=CanonicalJson.ByteCount(evt);});
            var oldTelemetry=Measure("old-receive-and-enqueue-telemetry",()=>{consumed+=Encoding.UTF8.GetByteCount(message.Payload.GetRawText());consumed+=JsonSerializer.SerializeToUtf8Bytes(evt,DedicatedProtocol.Json).Length;});
            int charged=CanonicalJson.ByteCount(evt);message.ReceivedFrameBytes=wireBytes;
            var newTelemetry=Measure("local-frame-and-existing-entry-telemetry",()=>{consumed+=message.ReceivedFrameBytes;consumed+=charged;});
            Check(newSign.BytesPerMessage<oldSign.BytesPerMessage/4,"Streaming signing materially reduces payload allocations");
            Check(newCost.BytesPerMessage<oldCost.BytesPerMessage/4,"Streaming credit computation materially reduces payload allocations");
            results.Add(new{PayloadBytes=payloadBytes,EnvelopeBytes=CanonicalJson.ByteCount(message),Measurements=new[]{oldSign,newSign,oldCost,newCost,oldTelemetry,newTelemetry}});
        }
        return new{Runtime=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,ProcessArchitecture=System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),Iterations=200,Results=results,Consumed=consumed};
    }
    public static int Main(string[] args)
    {
        try { CanonicalAndMutableCases();FlowBounds();TransportSecurity();var benchmark=Benchmark();if(args.Length>0)File.WriteAllText(args[0],JsonSerializer.Serialize(new{PassedAssertions=checks,Benchmark=benchmark},new JsonSerializerOptions{WriteIndented=true}));Console.WriteLine("PASS CanonicalJsonTests: "+checks+" assertions; canonical MAC parity, signed sockets, escaping/limits, mutable-event accounting and allocation benchmark.");return 0; }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
