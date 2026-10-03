using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Loader;
using System.Text.Json;
using NivalisTogether;
using NivalisTogether.Dedicated;

internal static class SessionTransitionTests
{
    private static int checks;
    private static readonly string Epoch=new string('a',32),Other=new string('b',32);
    private static WorldEvent Event(string kind,string phase,string epoch=null)=>new WorldEvent{
        Kind=kind,Data=JsonSerializer.SerializeToElement(new{Phase=phase,Epoch=epoch??Epoch},DedicatedProtocol.Json)};
    private static InteractionRequest Request(string kind,string epoch=null)=>new InteractionRequest{Kind=kind,TargetId=epoch??Epoch};
    private static void Check(bool value,string label){checks++;if(!value)throw new Exception(label);}
    public static int Main(string[] args)
    {
        string root=Path.GetFullPath(args[0]);
        AssemblyLoadContext.Default.Resolving+=(context,name)=>{
            foreach(string dir in new[]{"BepInEx/interop","BepInEx/core"}){string path=Path.Combine(root,dir,name.Name+".dll");if(File.Exists(path))return context.LoadFromAssemblyPath(path);}return null;};
        Run();Console.WriteLine("Session transition policy PASS: "+checks+" assertions; pure policy, no Unity/native calls.");return 0;
    }
    private static void Run()
    {
        CheckMapRetentionCapacity();
        Check(DedicatedProtocol.Version==14,"service-confirmed map catch-up frontiers require matching protocol14 peers");
        CheckCatchupAcknowledgements();
        foreach(string action in new[]{"dialogue-choice","dialogue-resume","dialogue-close","dialogue-start"})
        {
            Check(!SessionTransitionPolicy.WaitForAccountCheckpoint(true,true,action),"active dialogue can finish native save lock: "+action);
            Check(SessionTransitionPolicy.WaitForAccountCheckpoint(true,false,action),"new dialogue waits for prior checkpoint: "+action);
        }
        foreach(string action in new[]{"shop-buy","property-rent","business-restock",null})
            Check(SessionTransitionPolicy.WaitForAccountCheckpoint(true,true,action),"economic action cannot bypass account checkpoint: "+action);
        var stepAck=new WorldEvent{Kind="dialogue-result",Data=JsonSerializer.SerializeToElement(new DialogueResult{Success=true})};
        Check(!SessionTransitionPolicy.RequiresDurableResult(stepAck),"dialogue step acknowledgment cannot deadlock native save lock");
        Check(!SessionTransitionPolicy.RequiresDurableResult(new WorldEvent{Kind="business-result",Data=JsonSerializer.SerializeToElement(new BusinessResult{Success=true,NeedsCheckpoint=false})}),"craft start acknowledgment does not force a canceling checkpoint");
        foreach(string kind in new[]{"business-result","interaction-result"})
            Check(SessionTransitionPolicy.RequiresDurableResult(new WorldEvent{Kind=kind,Data=JsonSerializer.SerializeToElement(new{Success=true})}),"mutation success waits for paired commit: "+kind);
        foreach(string operation in new[]{"recall","dock","refuel","board","disembark"})
        {
            Check(SessionTransitionPolicy.RequiresDurableResult(new WorldEvent{Kind=BoatProtocol.EventKind,
                Data=JsonSerializer.SerializeToElement(new BoatWorldState{Operation=operation,Success=true})}),"boat mutation success waits for paired account commit: "+operation);
            Check(!SessionTransitionPolicy.RequiresDurableResult(new WorldEvent{Kind=BoatProtocol.EventKind,
                Data=JsonSerializer.SerializeToElement(new BoatWorldState{Operation=operation,Success=false})}),"boat rejection does not wait for a nonexistent commit: "+operation);
        }
        foreach(bool success in new[]{true,false})
            Check(!SessionTransitionPolicy.RequiresDurableResult(new WorldEvent{Kind=BoatProtocol.EventKind,
                Data=JsonSerializer.SerializeToElement(new BoatWorldState{Operation="snapshot",Success=success})}),"periodic boat snapshots never force a checkpoint");
        foreach(var payload in new[]{JsonSerializer.SerializeToElement(new{Success=true}),JsonSerializer.SerializeToElement(new{Operation="refuel"}),
            JsonSerializer.SerializeToElement(new{Operation="refuel",Success="true"}),JsonSerializer.SerializeToElement(0)})
            Check(!SessionTransitionPolicy.RequiresDurableResult(new WorldEvent{Kind=BoatProtocol.EventKind,Data=payload}),"incomplete or mistyped boat result cannot create a durable success fence");
        foreach(var route in new[]{
            ("group-apartment","prepare","group-apartment-prepared"),
            ("group-apartment","execute","group-apartment-arrived"),
            ("group-travel","prepare","group-travel-prepared"),
            ("group-travel","depart","group-travel-arrived")})
        {
            var control=Event(route.Item1,route.Item2);var ack=Request(route.Item3);
            Check(SessionTransitionPolicy.IsOrderedAcknowledgement(control,ack,false),"exact synchronous acknowledgement allowed after older native commit");
            Check(!SessionTransitionPolicy.IsOrderedAcknowledgement(control,ack,true),"running native preparation never bypassed");
            Check(!SessionTransitionPolicy.IsOrderedAcknowledgement(null,ack,false),"outside dispatch has no exception");
            Check(!SessionTransitionPolicy.IsOrderedAcknowledgement(control,null,false),"null request cannot pass");
            Check(!SessionTransitionPolicy.IsOrderedAcknowledgement(control,Request(route.Item3,Other),false),"other epoch cannot pass");
            Check(!SessionTransitionPolicy.IsOrderedAcknowledgement(Event(route.Item1,route.Item2,"bad"),Request(route.Item3,"bad"),false),"malformed same epoch cannot pass");
            foreach(string action in new[]{"group-apartment-request","group-travel-request","group-sleep-action","shop-buy","dialogue-choice","business-hire"})
                Check(!SessionTransitionPolicy.IsOrderedAcknowledgement(control,Request(action),false),"ordinary action cannot use ACK exception: "+action);
            foreach(string phase in new[]{"complete","cancel","accepted","denied","heartbeat"})
                Check(!SessionTransitionPolicy.IsOrderedAcknowledgement(Event(route.Item1,phase),ack,false),"wrong control phase cannot authorize ACK: "+phase);
        }
        Check(!SessionTransitionPolicy.IsOrderedAcknowledgement(Event("group-apartment","prepare"),Request("group-travel-prepared"),false),"cross-module ACK rejected");
        Check(!SessionTransitionPolicy.IsOrderedAcknowledgement(Event("group-travel","prepare"),Request("group-apartment-prepared"),false),"reverse cross-module ACK rejected");
        Check(SessionTransitionPolicy.IsCompletedTravel(Event("group-travel","complete")),"verified travel-complete branch may latch property context");
        foreach(string phase in new[]{"prepare","depart","cancel","accepted","denied","fatal","heartbeat"})
            Check(!SessionTransitionPolicy.IsCompletedTravel(Event("group-travel",phase)),"non-completion cannot adopt property context: "+phase);
        Check(!SessionTransitionPolicy.IsCompletedTravel(Event("group-apartment","complete")),"apartment completion cannot masquerade as district arrival");
        foreach(string phase in new[]{"prepare","execute","complete","cancel","heartbeat"})
        {
            Check(SessionTransitionPolicy.HasApartmentOverlap(Event("group-apartment",phase),false,true,false),"apartment mutation cannot overlap travel");
            Check(SessionTransitionPolicy.HasApartmentOverlap(Event("group-apartment",phase),false,false,true),"apartment mutation cannot overlap sleep");
            Check(!SessionTransitionPolicy.HasApartmentOverlap(Event("group-apartment",phase),true,false,false),"own apartment continuation stays allowed");
            Check(SessionTransitionPolicy.HasApartmentOverlap(Event("group-travel",phase),true,false,false),"travel mutation cannot overlap apartment");
            Check(SessionTransitionPolicy.HasApartmentOverlap(Event("group-sleep",phase),true,false,false),"sleep mutation cannot overlap apartment");
        }
        foreach(string kind in new[]{"group-apartment","group-travel","group-sleep"})
            foreach(string phase in new[]{"accepted","denied","fatal"})
                Check(!SessionTransitionPolicy.HasApartmentOverlap(Event(kind,phase),true,true,true),"cosmetic/fatal routes keep their own target handling");
        foreach(string phase in new[]{"waiting","cancelled"})
            Check(!SessionTransitionPolicy.HasApartmentOverlap(Event("group-sleep",phase),true,true,true),"sleep readiness does not conflict with independent apartment travel: "+phase);
        Check(!SessionTransitionPolicy.HasApartmentOverlap(Event("npcs","update"),true,true,true),"ordinary mutations go through bounded deferral instead of overlap exception");
        Check(!SessionTransitionPolicy.IsOrderedAcknowledgement(new WorldEvent{Kind="group-apartment",Data=JsonSerializer.SerializeToElement(1)},Request("group-apartment-prepared"),false),"non-object payload cannot authorize an ACK");
    }
    private static void CheckCatchupAcknowledgements()
    {
        var wake=new SleepWakeMarker{Epoch=Epoch,Token=Other,Revision=4,GameSeconds=600};
        var ready=Request(SleepWakeProtocol.ReadyKind);ready.Data=JsonSerializer.SerializeToElement(wake,DedicatedProtocol.Json);
        Check(SessionTransitionPolicy.IsCatchupAcknowledgement(ready,wake),"actual native morning acknowledgement can complete while service frontier is pending");
        Check(!SessionTransitionPolicy.IsCatchupAcknowledgement(ready,null),"unsolicited wake acknowledgement cannot bypass catchup");
        ready.TargetId=Other;Check(!SessionTransitionPolicy.IsCatchupAcknowledgement(ready,wake),"foreign wake epoch cannot bypass catchup");ready.TargetId=Epoch;
        ready.Data=JsonSerializer.SerializeToElement(new SleepWakeMarker{Epoch=Epoch,Token=Other,Revision=5,GameSeconds=600});
        Check(!SessionTransitionPolicy.IsCatchupAcknowledgement(ready,wake),"different wake revision cannot bypass catchup");
        foreach(var data in new[]{default(JsonElement),JsonSerializer.SerializeToElement(0),JsonSerializer.SerializeToElement(new{Epoch=Epoch}),JsonSerializer.SerializeToElement<SleepWakeMarker>(null)})
        {ready.Data=data;Check(!SessionTransitionPolicy.IsCatchupAcknowledgement(ready,wake),"malformed wake cannot bypass catchup");}
        foreach(string kind in new[]{"group-travel-prepared","group-travel-arrived","group-apartment-prepared","group-apartment-arrived",PlayerMapRestore.AcknowledgementKind})
            Check(SessionTransitionPolicy.IsCatchupAcknowledgement(Request(kind),wake),"existing native route acknowledgement remains available: "+kind);
        foreach(string kind in new[]{"shop-buy","group-travel-request","group-apartment-request","group-sleep-request","group-sleep-action","business-hire","dialogue-choice",null})
            Check(!SessionTransitionPolicy.IsCatchupAcknowledgement(Request(kind),wake),"ordinary action cannot bypass waiting service frontier: "+kind);
        Check(!SessionTransitionPolicy.IsCatchupAcknowledgement(null,wake),"null interaction cannot bypass catchup");
    }
    private static void CheckMapRetentionCapacity()
    {
        int maxBytes=SessionTransitionPolicy.MaxRetainedMapBytes,maxEvents=SessionTransitionPolicy.MaxRetainedMapEvents;
        Check(maxBytes==256*1024*1024&&maxEvents==16384,"native cab retention has explicit byte and event caps");
        Check(SessionTransitionPolicy.CanRetainMapEvent(0,0,1),"empty map backlog admits an event");
        Check(SessionTransitionPolicy.CanRetainMapEvent(maxEvents-1,maxBytes-1,1),"exact final event and byte fit is accepted");
        Check(!SessionTransitionPolicy.CanRetainMapEvent(maxEvents,0,1),"event cap still rejects small-message floods");
        Check(!SessionTransitionPolicy.CanRetainMapEvent(1,maxBytes,1),"full byte budget rejects one further byte");
        Check(!SessionTransitionPolicy.CanRetainMapEvent(1,maxBytes-1,2),"one-byte overflow is rejected");
        Check(!SessionTransitionPolicy.CanRetainMapEvent(1,1,int.MaxValue),"oversized next event cannot overflow arithmetic");
        Check(!SessionTransitionPolicy.CanRetainMapEvent(1,int.MaxValue,1),"corrupt byte total cannot wrap capacity check");
        Check(!SessionTransitionPolicy.CanRetainMapEvent(-1,0,1)&&!SessionTransitionPolicy.CanRetainMapEvent(0,-1,1),"negative accounting fails closed");
        Check(!SessionTransitionPolicy.CanRetainMapEvent(0,0,0)&&!SessionTransitionPolicy.CanRetainMapEvent(0,0,-1),"nonpositive event size fails closed");

        // The native run retained ~1.4 MiB/s. Model 120 seconds at 2 MiB/s,
        // using normal 32 KiB packet sizes without allocating their payloads.
        const int packetBytes=32*1024,packetsPerSecond=64;
        var queue=new Queue<(int Id,int Bytes)>();int retained=0,nextId=0,nextApplied=0;
        bool admitted=true,ordered=true,accounted=true;
        for(int second=0;second<120;second++)
            for(int packet=0;packet<packetsPerSecond;packet++)
            {
                admitted&=SessionTransitionPolicy.CanRetainMapEvent(queue.Count,retained,packetBytes);
                queue.Enqueue((nextId++,packetBytes));retained+=packetBytes;
            }
        Check(admitted&&retained==240*1024*1024&&queue.Count==7680,"120-second native load retains all 240 MiB within both caps");
        Check(retained>64*1024*1024,"long-load fixture reproduces the old byte-limit failure");
        Check(maxBytes-retained==16*1024*1024,"long-load fixture leaves bounded catch-up headroom");

        // Continue receiving the same live rate while catch-up applies twice
        // that rate, retaining every event in FIFO order rather than coalescing.
        for(int second=0;second<120;second++)
        {
            for(int packet=0;packet<packetsPerSecond;packet++)
            {
                admitted&=SessionTransitionPolicy.CanRetainMapEvent(queue.Count,retained,packetBytes);
                queue.Enqueue((nextId++,packetBytes));retained+=packetBytes;
            }
            for(int applied=0;applied<packetsPerSecond*2;applied++)
            {
                var entry=queue.Dequeue();ordered&=entry.Id==nextApplied++;retained-=entry.Bytes;
                accounted&=retained==queue.Count*packetBytes;
            }
        }
        Check(admitted,"ongoing live traffic remains admitted during catch-up");
        Check(ordered&&accounted,"catch-up preserves every arrival and exact byte accounting");
        Check(queue.Count==0&&retained==0&&nextApplied==nextId,"catch-up drains completely before an application fence can release");
    }
}
