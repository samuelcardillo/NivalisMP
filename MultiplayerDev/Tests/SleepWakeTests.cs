using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using NivalisTogether.Dedicated;

internal static class SleepWakeTests
{
    private static int assertions;
    private static readonly string A=Id(), B=Id(), C=Id(), Epoch=Id();
    private static string Id() => Guid.NewGuid().ToString("N");
    private static void Check(bool value,string message)
    { assertions++; if(!value) throw new Exception(message); }
    private static void Reject(Action action,string message)
    {
        bool rejected=false;
        try { action(); }
        catch(InvalidDataException) { rejected=true; }
        catch(InvalidOperationException) { rejected=true; }
        Check(rejected,message);
    }
    private static SleepWakeMarker Marker(string epoch=null) => new SleepWakeMarker {
        Epoch=epoch??Epoch,Token=Id(),Revision=1611,GameSeconds=115200
    };
    private static SleepWakeMarker Copy(SleepWakeMarker value) => new SleepWakeMarker {
        Epoch=value.Epoch,Token=value.Token,Revision=value.Revision,GameSeconds=value.GameSeconds
    };
    private static WorldEvent Event(string kind,string tag) => new WorldEvent {
        Kind=kind,WorldId=Epoch,Revision=1611,Data=JsonSerializer.SerializeToElement(new { Tag=tag })
    };
    private static WorldEventScheduler.Entry Take(WorldEventScheduler queue,int bulk,int control,string tag)
    {
        Check(queue.TryPeek(bulk,control,out var item),"Expected schedulable event "+tag);
        Check(item.Event.Data.GetProperty("Tag").GetString()==tag,"Unexpected event before "+tag);
        queue.Remove(item); return item;
    }
    private static void Protocol()
    {
        var valid=Marker();
        SleepWakeProtocol.Validate(valid);
        Check(SleepWakeProtocol.Matches(valid,Copy(valid)),"Marker round trip must retain identity.");
        Reject(()=>SleepWakeProtocol.Validate(null),"Null marker is rejected.");
        foreach(var field in new[]{"epoch","token","revision","clock"}) {
            var bad=Copy(valid);
            if(field=="epoch") bad.Epoch="invalid";
            if(field=="token") bad.Token=Guid.NewGuid().ToString("D");
            if(field=="revision") bad.Revision=0;
            if(field=="clock") bad.GameSeconds=-1;
            Reject(()=>SleepWakeProtocol.Validate(bad),"Invalid marker "+field+" is rejected.");
        }
        foreach(var field in new[]{"epoch","token","revision","clock"}) {
            var other=Copy(valid);
            if(field=="epoch") other.Epoch=Id();
            if(field=="token") other.Token=Id();
            if(field=="revision") other.Revision++;
            if(field=="clock") other.GameSeconds++;
            Check(!SleepWakeProtocol.Matches(valid,other),"Acknowledgement must match "+field+" exactly.");
        }
    }
    private static void Barrier()
    {
        var barrier=new SleepWakeBarrier(); var marker=Marker();
        Check(!barrier.Complete&&barrier.PendingCount==0,"Idle barrier cannot signal completion.");
        Reject(()=>barrier.Arm(marker),"No epoch may arm before a sleep cohort is captured.");
        var selected=new List<string>{A,B}; barrier.Begin(Epoch,selected);
        selected.Add(C);
        Check(barrier.PendingCount==2&&!barrier.Complete,"Begin snapshots the two-player cohort.");
        Check(!barrier.Acknowledge(A,marker)&&barrier.PendingCount==2,"Early acknowledgement cannot release a player before the post-night marker.");
        Reject(()=>barrier.Arm(Marker(Id())),"A marker from another sleep is rejected.");
        Check(barrier.Marker==null&&barrier.PendingCount==2,"Rejected arm preserves the pending cohort.");
        barrier.Arm(marker);
        Check(barrier.Marker!=marker&&SleepWakeProtocol.Matches(barrier.Marker,marker),"Arm copies the authoritative marker.");
        var expected=Copy(marker); marker.Token=Id(); marker.Revision++;
        Check(SleepWakeProtocol.Matches(barrier.Marker,expected),"Mutating the caller marker cannot change the armed challenge.");
        Reject(()=>barrier.Arm(expected),"Rearming cannot overwrite an issued challenge.");
        Check(!barrier.Acknowledge(C,expected)&&barrier.PendingCount==2,"Late join is excluded from the captured wake cohort.");
        foreach(var field in new[]{"epoch","token","revision","clock"}) {
            var forged=Copy(expected);
            if(field=="epoch") forged.Epoch=Id();
            if(field=="token") forged.Token=Id();
            if(field=="revision") forged.Revision--;
            if(field=="clock") forged.GameSeconds--;
            Check(!barrier.Acknowledge(A,forged)&&barrier.PendingCount==2&&!barrier.Complete,
                "Forged or stale "+field+" acknowledgement must not release anyone.");
        }
        Reject(()=>barrier.Acknowledge(A,new SleepWakeMarker()),"Malformed participant acknowledgement is rejected.");
        Check(barrier.PendingCount==2,"Malformed acknowledgement leaves the barrier intact.");
        Check(barrier.Acknowledge(A,expected)&&barrier.PendingCount==1&&!barrier.Complete,"One applied client cannot reveal the world for the second client.");
        Check(barrier.Acknowledge(A,Copy(expected))&&barrier.PendingCount==1&&!barrier.Complete,"Duplicate matching ACK is idempotent.");
        Check(barrier.Acknowledge(B,expected)&&barrier.PendingCount==0&&barrier.Complete,"Every cohort client applying the morning releases the barrier.");
        barrier.Reset();
        Check(barrier.Epoch==""&&barrier.Marker==null&&barrier.PendingCount==0&&!barrier.Complete,"Reset removes epoch, challenge and readiness.");
        Check(!barrier.Acknowledge(A,expected),"An ACK after disconnect/reset cannot resurrect completion.");

        var next=Marker(Id()); barrier.Begin(next.Epoch,new[]{A,B}); barrier.Arm(next);
        Check(!barrier.Acknowledge(A,expected)&&barrier.PendingCount==2,"A previous night's valid ACK cannot release a new night.");
        barrier.Remove(C);
        Check(barrier.PendingCount==2,"Departure outside the cohort does not change pending players.");
        barrier.Remove(B); barrier.Remove(B);
        Check(barrier.PendingCount==1&&!barrier.Complete,"Repeated departure removes only the departing player.");
        Check(!barrier.Acknowledge(B,next),"A departed player cannot acknowledge the current cohort.");
        Check(barrier.Acknowledge(A,next)&&barrier.Complete,"Remaining applied client may release after another disconnects.");

        barrier.Begin(Epoch,new[]{A,B}); barrier.Remove(A); barrier.Remove(B);
        Check(!barrier.Complete,"Even complete departure cannot finish before a post-night marker is armed.");
        barrier.Arm(expected);
        Check(barrier.Complete,"A finished native night with no remaining clients does not deadlock.");
        barrier.Begin(Epoch,new string[0]);
        Check(!barrier.Complete&&barrier.PendingCount==0,"Empty cohort remains unready before authoritative completion.");
        barrier.Arm(expected); Check(barrier.Complete,"Empty wake cohort needs no client ACK after authoritative completion.");

        barrier.Begin(Epoch,new[]{A}); barrier.Arm(expected);
        Reject(()=>barrier.Begin("bad",new[]{B}),"Malformed new epoch is rejected.");
        Reject(()=>barrier.Begin(Id(),new[]{B,B}),"Duplicate cohort cannot manufacture extra participants.");
        Reject(()=>barrier.Begin(Id(),new[]{"bad"}),"Malformed participant ID is rejected.");
        var tooMany=new List<string>(); for(int i=0;i<33;i++) tooMany.Add(Id());
        Reject(()=>barrier.Begin(Id(),tooMany),"Wake cohort remains bounded to 32 players.");
        Check(barrier.Epoch==Epoch&&barrier.PendingCount==1&&SleepWakeProtocol.Matches(barrier.Marker,expected),
            "Rejected replacement does not damage the active barrier.");
        tooMany.RemoveAt(32); barrier.Begin(Id(),tooMany);
        Check(barrier.PendingCount==32&&barrier.Marker==null&&!barrier.Complete,"Maximum valid cohort starts a fresh unarmed barrier.");
    }
    private static void Ordering()
    {
        var wake=Event(SleepWakeProtocol.EventKind,"wake");
        Check(WorldEventFlow.IsControlKind(wake.Kind),"Wake marker uses reserved control credits.");
        Check(WorldEventFlow.IsOrderingBarrier(wake),"Wake marker is a global ordering barrier without relying on a Phase field.");
        int cost=WorldEventFlow.Cost(wake); var queue=new WorldEventScheduler();
        queue.Enqueue(Event("npc-state-chunk","npc-1"));
        queue.Enqueue(Event("crowd-state-chunk","crowd-1"));
        queue.Enqueue(Event("npc-state-chunk","npc-2"));
        queue.Enqueue(wake);
        queue.Enqueue(Event("timeWeather","after-clock"));
        queue.Enqueue(Event("quest-state-chunk","after-quests"));
        Check(!queue.TryPeek(0,WorldEventFlow.ControlWindowBytes,out _),"Reserved control credit cannot let wake or subsequent time pass older bulk when bulk credit is exhausted.");
        Check(!queue.TryPeek(cost-1,WorldEventFlow.ControlWindowBytes,out _),"Insufficient bulk credit must not consume or skip earlier snapshots.");
        Check(queue.Count==6,"Blocked peek leaves the full queue intact.");
        Take(queue,cost,WorldEventFlow.ControlWindowBytes,"npc-1");
        Check(!queue.TryPeek(0,WorldEventFlow.ControlWindowBytes,out _),"Wake still waits when one of several older snapshots is delivered.");
        Take(queue,cost,WorldEventFlow.ControlWindowBytes,"crowd-1");
        Check(!queue.TryPeek(0,WorldEventFlow.ControlWindowBytes,out _),"Wake cannot pass the second event of an older bulk kind.");
        Take(queue,cost,WorldEventFlow.ControlWindowBytes,"npc-2");
        Check(!queue.TryPeek(WorldEventFlow.BulkWindowBytes,0,out _),"Post-wake bulk cannot pass the marker while control credits are exhausted.");
        Check(!queue.TryPeek(WorldEventFlow.BulkWindowBytes,cost-1,out _),"Insufficient marker credit retains the global fence.");
        Take(queue,0,cost,"wake");
        Take(queue,0,cost,"after-clock");
        Take(queue,cost,0,"after-quests");
        Check(queue.Count==0&&queue.PayloadBytes==0,"Ordered stream drains every event and releases accounting.");

        queue.Enqueue(Event("npc-state-chunk","older"));
        queue.Enqueue(Event("timeWeather","before-clock"));
        queue.Enqueue(Event(SleepWakeProtocol.EventKind,"wake-a"));
        queue.Enqueue(Event("npc-state-chunk","between"));
        queue.Enqueue(Event(SleepWakeProtocol.EventKind,"wake-b"));
        queue.Enqueue(Event("timeWeather","after-b"));
        Take(queue,0,cost,"before-clock");
        Check(!queue.TryPeek(0,cost,out _),"An earlier ordinary control may progress, but never opens the wake fence.");
        Take(queue,cost,cost,"older");
        Take(queue,0,cost,"wake-a");
        Check(!queue.TryPeek(0,cost,out _),"A second wake marker retains its own intervening bulk fence.");
        Take(queue,cost,cost,"between");
        Take(queue,0,cost,"wake-b");
        Take(queue,0,cost,"after-b");
        Check(queue.Count==0,"Multiple wake epochs remain FIFO with the intervening world state.");
        queue.Enqueue(Event("npc-state-chunk","abandoned")); queue.Enqueue(wake); queue.Clear();
        queue.Enqueue(Event("timeWeather","new-session"));
        Take(queue,0,cost,"new-session");
        Check(queue.Count==0&&queue.PayloadBytes==0,"Scheduler reset leaves no stale wake fence in a reconnect.");
    }
    public static int Main()
    {
        try {
            Protocol(); Barrier(); Ordering();
            Console.WriteLine("PASS: "+assertions+" sleep wake marker, cohort, ACK and credit-ordering assertions; pure protocol only, no native process.");
            return 0;
        } catch(Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
