using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using NivalisTogether.Dedicated;

// Real loopback sockets and production transport; no game or service is launched.
public static class DedicatedBackpressureTests
{
    private static int checks;
    private static void Check(bool value,string reason){checks++;if(!value)throw new Exception(reason);}
    private static int QueueCount(DedicatedConnection connection)=>(int)((ICollection)typeof(DedicatedConnection).GetField("_incoming",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(connection)).Count;
    private static int QueueBytes(DedicatedConnection connection)=>(int)typeof(DedicatedConnection).GetField("_incomingBytes",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(connection);
    public static int Run()
    {
        SlowConsumer(600,128,false);
        SlowConsumer(100,60000,true);
        PartialFrameAndAuthenticationBoundary();
        Console.WriteLine("PASS DedicatedBackpressureTests: "+checks+" assertions; two signed slow-consumer floods, outgoing traffic under receive pressure, partial-frame and authentication boundary.");
        return checks;
    }
    private static void SlowConsumer(int total,int padding,bool byteBound)
    {
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        var connecting=DedicatedConnection.ConnectAsync("127.0.0.1",((IPEndPoint)listener.LocalEndpoint).Port);
        using var receiver=DedicatedConnection.FromAccepted(listener.AcceptTcpClient());
        using var sender=connecting.GetAwaiter().GetResult();listener.Stop();
        Check(receiver.Send("serverHello",new{}),"Handshake could not queue.");
        var deadline=Stopwatch.StartNew();bool hello=false;
        while(!hello&&deadline.ElapsedMilliseconds<3000){receiver.Tick();sender.Tick();hello=sender.TryReceive(out _);Thread.Sleep(1);}
        Check(hello,"Loopback handshake did not complete.");
        var key=Enumerable.Range(0,32).Select(i=>(byte)(i+1)).ToArray();receiver.SetAuthenticationKey(key);sender.SetAuthenticationKey(key);
        string body=new string('x',padding);int sent=0,received=0,maxCount=0,maxBytes=0;
        void Fill(){for(int i=0;i<16&&sent<total;i++){if(!sender.Send("testFrame",new{Number=sent,Body=body}))break;sent++;}}
        void ObserveBounds(){
            int count=QueueCount(receiver),bytes=QueueBytes(receiver);maxCount=Math.Max(maxCount,count);maxBytes=Math.Max(maxBytes,bytes);
            Check(count<=32,"Slow consumer exceeded frame high-water.");
            Check(bytes<=256*1024+DedicatedProtocol.MaxFrameBytes,"Slow consumer exceeded byte high-water plus one atomic frame.");
            Check(sender.Connected&&receiver.Connected,"Backpressure disconnected a healthy ordered stream.");
        }
        // Produce well beyond the old hard queue cap while deliberately not consuming.
        for(int tick=0;tick<100;tick++){Fill();sender.Tick();receiver.Tick();ObserveBounds();Thread.Sleep(1);}
        Check(byteBound?maxBytes>=256*1024:maxCount==32,"The relevant receive high-water was not exercised.");
        if(byteBound)Check(maxCount<32,"Large frames should be stopped by byte pressure first.");
        Check(receiver.Send("testAck",new{Number=314}),"Outgoing ACK could not queue while receive was paused.");
        bool acknowledged=false;deadline.Restart();
        while(!acknowledged&&deadline.ElapsedMilliseconds<3000){receiver.Tick();sender.Tick();if(sender.TryReceive(out var ack)){Check(ack.Type=="testAck"&&ack.Payload.GetProperty("Number").GetInt32()==314,"Outgoing ACK was corrupted.");acknowledged=true;}Thread.Sleep(1);}
        Check(acknowledged,"Receive backpressure prevented outgoing progress.");
        deadline.Restart();
        while(received<total&&deadline.ElapsedMilliseconds<10000){
            Fill();sender.Tick();receiver.Tick();ObserveBounds();
            // One atomic message per tick models a deliberately bounded UI consumer.
            if(receiver.TryReceive(out var value)){
                Check(value.Type=="testFrame"&&value.Payload.GetProperty("Number").GetInt32()==received,"Slow-consumer stream reordered or lost a message.");
                Check(value.Payload.GetProperty("Body").GetString()==body,"Payload changed under receive backpressure.");received++;
            }
            Thread.Sleep(1);
        }
        Check(sent==total&&received==total&&receiver.Connected&&sender.Connected,"Flood failed to drain completely and remain connected.");
        Check(!receiver.TryReceive(out _),"Flood delivered a duplicate message.");
    }
    private static byte[] Frame(DedicatedConnection receiver,ulong sequence,string type)
    {
        var json=JsonSerializer.SerializeToUtf8Bytes(new WireMessage{SessionId=receiver.SessionId,Sequence=sequence,Type=type,Payload=JsonSerializer.SerializeToElement(new{Number=(int)sequence})},DedicatedProtocol.Json);
        var result=new byte[json.Length+4];result[0]=(byte)(json.Length>>24);result[1]=(byte)(json.Length>>16);result[2]=(byte)(json.Length>>8);result[3]=(byte)json.Length;Buffer.BlockCopy(json,0,result,4,json.Length);return result;
    }
    private static void PartialFrameAndAuthenticationBoundary()
    {
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        using var raw=new TcpClient();raw.Connect(IPAddress.Loopback,((IPEndPoint)listener.LocalEndpoint).Port);
        using var receiver=DedicatedConnection.FromAccepted(listener.AcceptTcpClient());listener.Stop();
        var stream=raw.GetStream();byte[] first=Frame(receiver,1,"join"),second=Frame(receiver,2,"ordinary");
        stream.Write(first,0,2);receiver.Tick();
        Check(receiver.Connected&&!receiver.TryReceive(out _),"Partial header was discarded or dispatched.");
        stream.Write(first,2,first.Length-2);stream.Write(second,0,second.Length);Thread.Sleep(10);receiver.Tick();
        Check(receiver.TryReceive(out var join)&&join.Type=="join"&&join.Sequence==1,"Partial-frame handshake was not reconstructed exactly.");
        Check(!receiver.TryReceive(out _),"Coalesced successor crossed the authentication dispatch boundary.");
        receiver.SetAuthenticationKey(new byte[32]);receiver.Tick();
        Check(!receiver.Connected&&!receiver.TryReceive(out _),"Unsigned successor inherited authentication from the prior join.");
    }
}
