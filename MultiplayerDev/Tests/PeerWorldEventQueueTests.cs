using System;
using System.IO;
using System.Text.Json;
using NivalisTogether.Dedicated;
using NivalisTogether.Server;

public static class PeerWorldEventQueueTests
{
    private static int checks;
    private static void Check(bool value,string why){checks++;if(!value)throw new Exception(why);}
    private static void Reject(Action action,string why){checks++;try{action();}catch(Exception e)when(e is IOException||e is InvalidDataException||e is InvalidOperationException||e is ArgumentOutOfRangeException){return;}throw new Exception(why);}
    private static WorldEvent Event(int value,string kind="npcs",int padding=0)=>new(){WorldId=new string('a',32),Revision=5,Kind=kind,Data=JsonSerializer.SerializeToElement(new{Number=value,Padding=new string('x',padding)},DedicatedProtocol.Json)};
    private static void Put(PeerWorldEventQueue q,WorldEvent e,double now=1)=>q.Enqueue(e,WorldEventFlow.Cost(e),WorldEventFlow.IsControlKind(e.Kind),now);
    private static PeerWorldEventQueue.Entry Head(PeerWorldEventQueue q){Check(q.TryPeek(int.MaxValue,int.MaxValue,out var e),"Expected head");return e;}
    private static void IsolationAndOrder()
    {
        var a=new PeerWorldEventQueue();var b=new PeerWorldEventQueue();var original=Event(1);original.DeliveryId=77;
        Put(a,original);Put(b,original);var ah=Head(a);var bh=Head(b);
        Check(!ReferenceEquals(original,ah.Event)&&!ReferenceEquals(ah.Event,bh.Event),"Each recipient owns a distinct mutable envelope");
        Check(ah.Event.DeliveryId==0&&bh.Event.DeliveryId==0,"Receipt identity is not inherited or assigned on enqueue");
        ah.Event.DeliveryId=100;Check(bh.Event.DeliveryId==0&&original.DeliveryId==77,"Peer delivery identity cannot corrupt another peer or worker envelope");
        original.Kind="timeWeather";original.Data=JsonSerializer.SerializeToElement(new{Number=999});original.TargetPlayerId=new string('b',32);
        Check(ah.Event.Kind=="npcs"&&bh.Event.Data.GetProperty("Number").GetInt32()==1&&ah.Event.TargetPlayerId=="","Later source envelope mutation cannot change queued history/privacy");
        Put(a,Event(2,"player-scene-context"));
        Check(!a.TryPeek(ah.Cost-1,int.MaxValue,out _),"Later control cannot overtake underfunded bulk head");
        Check(a.TryPeek(ah.Cost,0,out var same)&&ReferenceEquals(same,ah),"Only correct head lane credit matters");
        Reject(()=>a.Remove(bh),"Equal-looking entry from another peer cannot remove head");
        a.Remove(ah);Check(a.TryPeek(0,int.MaxValue,out var control)&&control.Control,"Control follows older bulk with own lane charge");a.Remove(control);
        Check(a.Count==0&&a.Bytes==0&&b.Count==1,"Peer drain cannot alter another peer queue/accounting");
        for(int i=0;i<300;i++)Put(a,Event(i,i%7==0?"interaction-result":"shared-economy-chunk"));
        for(int i=0;i<300;i++){var h=Head(a);Check(h.Event.Data.GetProperty("Number").GetInt32()==i,"Mixed mutation/control history is strict FIFO with no coalescing");a.Remove(h);}
        Check(a.Count==0&&a.Bytes==0,"Exact accounting after complete replay");
    }
    private static void BoundsAndAge()
    {
        var q=new PeerWorldEventQueue();var small=Event(1);
        for(int i=0;i<PeerWorldEventQueue.MaxEvents;i++)Put(q,small,10);
        Check(q.Count==PeerWorldEventQueue.MaxEvents&&q.Bytes==PeerWorldEventQueue.MaxEvents*WorldEventFlow.Cost(small),"Small payloads reach exact bounded count");
        Reject(()=>Put(q,small,11),"Count overflow fails without dropping history");Check(q.Count==PeerWorldEventQueue.MaxEvents,"Overflow preserves existing queue");q.Clear();
        Check(q.Count==0&&q.Bytes==0&&!q.IsExpired(10000),"Clear discards only this peer's queue and age");
        var large=Event(3,padding:47000);int charge=WorldEventFlow.Cost(large),fits=PeerWorldEventQueue.MaxBytes/charge;
        for(int i=0;i<fits;i++)Put(q,large,10);
        Check(q.Bytes==fits*charge&&q.Bytes<=PeerWorldEventQueue.MaxBytes,"Large records obey conservative byte cap");
        Reject(()=>Put(q,large,11),"Byte overflow fails before enqueue");Check(q.Count==fits&&q.Bytes==fits*charge,"Byte overflow changes no counters");
        q.Clear();Put(q,small,10);Put(q,small,100);
        Check(!q.IsExpired(129.999)&&q.IsExpired(130),"Age expires exactly120s from oldest queued event");
        q.Remove(Head(q));Check(!q.IsExpired(130)&&q.IsExpired(220),"Removing oldest exposes successor's own queue age");
        q.Clear();Reject(()=>Put(q,small,double.NaN),"Nonfinite enqueue clock rejected");Reject(()=>q.IsExpired(-1),"Invalid expiry clock rejected");
        Reject(()=>q.Enqueue(small,1,false,1),"Undercharged payload rejected");Reject(()=>q.Enqueue(small,2048,true,1),"Forged control lane rejected");
        Reject(()=>Put(q,new WorldEvent()),"Malformed envelope rejected");Reject(()=>q.TryPeek(-1,0,out _),"Negative credit rejected");
        var privateEvent=Event(1);privateEvent.TargetPlayerId=new string('c',32);Put(q,privateEvent);
        Check(Head(q).Event.TargetPlayerId==privateEvent.TargetPlayerId,"Private target preserved exactly for service routing");
        var bytes=new byte[3000];for(int i=0;i<bytes.Length;i++)bytes[i]=251;
        var escaped=Event(7);escaped.Data=JsonSerializer.SerializeToElement(new{Bytes=bytes});Put(q,escaped);
        Check(q.Bytes==WorldEventFlow.Cost(privateEvent)+WorldEventFlow.Cost(escaped),"Canonical base64 escaping shares exact service credit charge");
    }
    public static int Run(){IsolationAndOrder();BoundsAndAge();Console.WriteLine("PASS PeerWorldEventQueueTests: "+checks+" assertions; immutable per-peer envelopes, strictFIFO, isolated credits, count/bytes/age bounds.");return checks;}
}
