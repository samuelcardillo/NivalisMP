using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using NivalisTogether.Dedicated;

internal static class ApartmentTests
{
    private static int checks;
    private static readonly string A = new string('a',32), B = new string('b',32), C = new string('c',32), Epoch = new string('d',32);
    private const string Property = "682367a8-c73e-4682-812b-9fdb6e8ab604";
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    private static void Throws(Action action, string message) { checks++; try { action(); } catch { return; } throw new Exception(message); }
    private static GroupApartmentEvent Event(string phase="prepare") => new GroupApartmentEvent {
        Sequence=1,Epoch=Epoch,Phase=phase,PlayerId=A,ApartmentId=Property,Scene="1_Lowtown",Enter=false,Participants=new[]{A} };
    private static WorldEvent Wire(string kind, object data) => new WorldEvent {Kind=kind,Data=JsonSerializer.SerializeToElement(data,DedicatedProtocol.Json)};
    public static int Main()
    {
        WireValidation(); MembershipAndPhases(); BoundedDeadlines(); NativeIdentity(); SchedulerFences();
        Console.WriteLine("Apartment protocol PASS: "+checks+" assertions. Pure logic only; native entry/exit requires live acceptance."); return 0;
    }
    private static void WireValidation()
    {
        foreach (var phase in new[]{"prepare","execute","complete","cancel","heartbeat","accepted","denied","fatal"})
        { var value=Event(phase);ApartmentProtocol.Validate(value);Check(true,"valid phase "+phase); }
        var entry=Event();entry.Enter=true;Check(entry.SourceProperty==""&&entry.DestinationProperty==Property,"native entry context");
        var exit=Event();Check(exit.SourceProperty==Property&&exit.DestinationProperty=="","native exit context");
        var clone=JsonSerializer.Deserialize<GroupApartmentEvent>(JsonSerializer.Serialize(exit,DedicatedProtocol.Json),DedicatedProtocol.Json);
        Check(ApartmentProtocol.SameRoute(exit,clone),"wire route round trip");
        clone.Enter=true;Check(!ApartmentProtocol.SameRoute(exit,clone),"opposite door cannot reuse epoch");
        clone=Event();clone.Scene="2_Meridian_Market";Check(!ApartmentProtocol.SameRoute(exit,clone),"scene cannot change under epoch");
        clone=Event();clone.ApartmentId="11111111-1111-1111-1111-111111111111";Check(!ApartmentProtocol.SameRoute(exit,clone),"apartment cannot change under epoch");
        foreach (Action<GroupApartmentEvent> invalidate in new Action<GroupApartmentEvent>[] {
            v=>v.Sequence=0,v=>v.Epoch="wrong",v=>v.Phase="depart",v=>v.ApartmentId="door-name",v=>v.Scene="",
            v=>v.PlayerId="bad",v=>v.PlayerId="",v=>v.RequestId="bad",v=>v.Message=new string('x',241),v=>v.Participants=new[]{A,A},v=>v.Participants=new[]{A,B},
            v=>v.FailedPlayers=new[]{C},v=>v.FailedPlayers=new[]{A,A},v=>v.Participants=new string[33] })
        { var value=Event();invalidate(value);Throws(()=>ApartmentProtocol.Validate(value),"invalid wire accepted"); }
    }
    private static void MembershipAndPhases()
    {
        var barrier=new ApartmentBarrier(Epoch,new[]{A,B},10);
        Check(!barrier.AllPrepared,"prepare waits for everyone");
        Check(!barrier.Acknowledge(A,Epoch,true),"arrival cannot precede prepare");
        Check(!barrier.Acknowledge(C,Epoch,false),"late loaded peer cannot join frozen membership");
        Check(!barrier.CanAcknowledge(A,new string('e',32),false),"stale epoch ignored including negative ack");
        Throws(()=>barrier.BeginWorker(11),"cannot execute before both prepared");
        Check(barrier.Acknowledge(A,Epoch,false),"first participant prepared");
        Check(!barrier.Acknowledge(A,Epoch,false),"duplicate prepare is idempotent");
        Check(!barrier.AllPrepared,"duplicate cannot satisfy second member");
        Check(barrier.Acknowledge(B,Epoch,false)&&barrier.AllPrepared,"second participant prepared");
        barrier.BeginWorker(12);
        Check(!barrier.CanAcknowledge(A,Epoch,false),"late negative prepared cannot cancel native worker execution");
        Check(!barrier.CanAcknowledge(A,Epoch,true),"early negative arrival cannot remove participant during worker execution");
        Check(!barrier.AllArrived,"worker phase is not arrival");
        barrier.BeginArrivals(13);
        Check(barrier.Acknowledge(A,Epoch,true),"first arrival");
        Check(!barrier.Acknowledge(A,Epoch,true),"arrival replay idempotent");
        Check(barrier.MissingArrivals.SequenceEqual(new[]{B}),"missing arrivals exact");
        Check(barrier.Acknowledge(B,Epoch,true)&&barrier.AllArrived,"party complete");
        var leaving=new ApartmentBarrier(Epoch,new[]{A,B},0);leaving.Acknowledge(A,Epoch,false);leaving.Remove(B);
        Check(leaving.AllPrepared&&!leaving.Contains(B),"disconnected member removed from both sets");
        leaving.BeginWorker(1);leaving.BeginArrivals(2);leaving.Remove(A);Check(leaving.AllArrived,"last disconnect cannot deadlock barrier");
        Throws(()=>new ApartmentBarrier(Epoch,Array.Empty<string>(),0),"zero-player native request refused");
        Throws(()=>new ApartmentBarrier(Epoch,new[]{A,A},0),"duplicate membership refused");
    }
    private static void BoundedDeadlines()
    {
        var barrier=new ApartmentBarrier(Epoch,new[]{A},100);Check(barrier.Deadline==115&&barrier.FinalDeadline==160,"fixed prep/total bound");
        barrier.Acknowledge(A,Epoch,false);barrier.BeginWorker(114);Check(barrier.Deadline==129,"worker bound");
        barrier.BeginArrivals(128);Check(barrier.Deadline==158,"client arrival bound");
        Throws(()=>barrier.BeginArrivals(129),"cannot reset arrival deadline");
        var expired=new ApartmentBarrier(Epoch,new[]{A},0);expired.Acknowledge(A,Epoch,false);Throws(()=>expired.BeginWorker(15.001),"prepare expiry enforced");
        var worker=new ApartmentBarrier(Epoch,new[]{A},0);worker.Acknowledge(A,Epoch,false);worker.BeginWorker(1);Throws(()=>worker.BeginArrivals(16.001),"worker expiry enforced");
        Throws(()=>new ApartmentBarrier(Epoch,new[]{A},double.NaN),"NaN clock refused");
        Throws(()=>new ApartmentBarrier(Epoch,new[]{A},double.PositiveInfinity),"infinite clock refused");
    }
    private static void NativeIdentity()
    {
        var permit=new ApartmentNativePermit(Epoch,Property,false);
        Check(!permit.ObserveContext(Epoch,Property,false),"context event cannot precede native entry permit");
        Check(!permit.Consume(Epoch,Property,true),"wrong side rejected");
        Check(!permit.Consume(A,Property,false),"old/new epoch rejected");
        Check(!permit.Consume(Epoch,"11111111-1111-1111-1111-111111111111",false),"wrong apartment rejected");
        Check(!permit.Consumed,"invalid invocation cannot consume permit");
        Check(permit.Consume(Epoch,Property,false)&&permit.Consumed,"exact native exit accepted once");
        Check(!permit.Consume(Epoch,Property,false),"same-door reentrant/native replay rejected");
        Check(!permit.ObserveArrival(true,true),"nearby position without native callback is insufficient");
        Check(!permit.ObserveContext(Epoch,Property,true),"opposite native context callback rejected");
        Check(permit.ObserveContext(Epoch,Property,false),"exact callback recorded without inspecting transient Entered value");
        Check(!permit.ObserveContext(Epoch,Property,false),"duplicate callback rejected");
        Check(!permit.ObserveArrival(false,true),"exit event before Entered=false is not completion");
        Check(!permit.ObserveArrival(true,false),"correct context but no native target arrival is insufficient");
        Check(permit.ObserveArrival(true,true),"post-native context and position establish arrival");
        Check(permit.IsComplete(true),"recorded arrival remains valid after moving away from target");
        Check(!permit.IsComplete(false),"later unsupported property change invalidates completion");
        Throws(()=>new ApartmentNativePermit("bad",Property,true),"invalid native epoch refused");
    }
    private static void SchedulerFences()
    {
        Check(WorldEventFlow.IsControlKind("group-apartment"),"apartment uses control reservation");
        foreach(var phase in new[]{"prepare","execute","complete","cancel"})Check(WorldEventFlow.IsOrderingBarrier(Wire("group-apartment",Event(phase))),"apartment phase fenced "+phase);
        foreach(var phase in new[]{"accepted","denied","heartbeat","fatal"})Check(!WorldEventFlow.IsOrderingBarrier(Wire("group-apartment",Event(phase))),"notice not a fence "+phase);
        var queue=new WorldEventScheduler();var earlier=Wire("shared-economy-chunk",new{Offset=100});var prepare=Wire("group-apartment",Event());var later=Wire("dialogue-state",new{Current="x"});
        queue.Enqueue(earlier);queue.Enqueue(prepare);queue.Enqueue(later);
        Check(!queue.TryPeek(0,WorldEventFlow.ControlWindowBytes,out _),"control transition cannot overtake exhausted earlier bulk");
        Check(queue.TryPeek(WorldEventFlow.BulkWindowBytes,WorldEventFlow.ControlWindowBytes,out var first)&&ReferenceEquals(first.Event,earlier),"economy before property prepare");queue.Remove(first);
        Check(queue.TryPeek(0,WorldEventFlow.ControlWindowBytes,out var second)&&ReferenceEquals(second.Event,prepare),"property prepare follows earlier bulk");queue.Remove(second);
        Check(queue.TryPeek(0,WorldEventFlow.ControlWindowBytes,out var third)&&ReferenceEquals(third.Event,later),"later dialogue after property fence");queue.Remove(third);
        Check(queue.Count==0&&queue.PayloadBytes==0,"queue completely drained");
        Throws(()=>WorldEventFlow.Cost(Wire("group-apartment",new{Blob=new string('x',24000)})),"oversized apartment control rejected");
    }
}
