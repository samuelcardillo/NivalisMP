using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NivalisTogether.Dedicated;

// The launcher inserts the actual production completion branch, preparation
// pump, dispatch, deferred flush and retention methods. Only native integrations
// outside the scheduling scenario are stubs; no installed/game code is run.
internal sealed class MapRestoreFenceProbe
{
    private static int checks;
    private static void Check(bool value,string reason){checks++;if(!value)throw new Exception(reason);}
    private sealed class Prepared : IIncrementalPreparedWorldUpdate {
        public int Required=1,Progress;public bool Aborted;
        public void Commit(){Commits++;Thread.Sleep(8);}
        public bool Advance(ClientMutationBudget budget){if(Aborted)throw new InvalidOperationException("Aborted fixture");if(!budget.TryStart(Stopwatch.GetTimestamp()))return false;if(++Progress<Required)return false;Commit();return true;}
        public void Abort(){Aborted=true;}
    }
    private static int Commits;
    private readonly BoundedPreparationFence<Prepared,WorldEvent> economyPreparation=new BoundedPreparationFence<Prepared,WorldEvent>();
    private readonly ClientMutationBudget clientMutationBudget=new ClientMutationBudget(Stopwatch.Frequency);
    private IPreparedWorldUpdate progressingWorldUpdate;
    private string progressingWorldScene,progressingWorldProperty;
    private readonly Queue<(WorldEvent Update,int Bytes)> deferredTravelEvents=new Queue<(WorldEvent,int)>();
    private readonly HashSet<long> retainedMapDeliveries=new HashSet<long>();
    private readonly List<long> retained=new List<long>(),native=new List<long>();
    private readonly HashSet<long> ack=new HashSet<long>();
    private readonly FakeConnection connection=new FakeConnection();
    private readonly FakeMembership clientScene=new FakeMembership();
    private bool worker,terminalSession,disposed,clientMapRestoreAcknowledged,npcMapPresentationPending,apartmentTravelContextPending;
    private bool loaded=true;
    private string playerId=new string('a',32),acknowledgedWakeToken="";
    private double Now=>0;
    private double nextPose;
    private int deferredTravelBytes,frame,sends;
    private PlayerMapRestore clientMapRestore=new PlayerMapRestore{Epoch=new string('b',32)};
    private SleepWakeMarker pendingWakeMarker;
    private WorldEvent orderedTransitionEvent;
    private sealed class FakeConnection {public bool Connected=true;}
    private sealed class FakeMembership {public PlayerSceneContext Current;public bool Observe(PlayerSceneContext value){Current=value;return true;}}
    private static class WorkerSceneResidency {public static bool ClientRestoreComplete=true,IsRestoringClient;public static string CurrentProperty="";public static void BeginClientRestore(PlayerMapLocation location,string epoch,double now){}}
    private static class BoatReplication {public static int Attachments;public static bool TryRestorePlayerAttachment(){Attachments++;return true;}}
    private static class DedicatedWorldRuntime {public static string Scene="3_Docks";}
    private static class TravelReplication {public const string EventKind="group-travel";public static bool IsBusy;public static int Accepts;public static void AcceptTravelContext(){Accepts++;}public static bool Apply(WorldEvent value,double now)=>false;}
    private static class ApartmentReplication {public const string EventKind="group-apartment";public static bool IsBusy;public static int Accepts;public static void AcceptTravelContext(){Accepts++;}public static bool Apply(WorldEvent value,double now)=>false;}
    private static class SleepReplication {public const string EventKind="group-sleep";public static bool IsBusy;public static bool Apply(WorldEvent value,double now)=>false;}
    private static class SessionTransitionPolicy {
        public const int MaxRetainedMapEvents=16384,MaxRetainedMapBytes=256*1024*1024;
        public static bool HasApartmentOverlap(WorldEvent update,bool apartment,bool travel,bool sleep)=>false;
        public static bool IsCompletedTravel(WorldEvent update)=>false;
        public static bool CanRetainMapEvent(int count,int bytes,int incoming)=>count<MaxRetainedMapEvents&&incoming>0&&incoming<=MaxRetainedMapBytes-bytes;
    }
    private void SendInteraction(InteractionRequest request){if(economyPreparation.IsFenced)throw new InvalidOperationException("Asynchronous restore ACK crossed the preparation fence.");Check(request.Kind==PlayerMapRestore.AcknowledgementKind,"Production branch sends restoration ACK only");sends++;}
    private void Status(string value){}
    private void ObserveMapCatchupContext(PlayerSceneContext context){}
    private void Log(string value){}
    private void AcceptApartmentTravelContext(){}
    private bool ApplyTravelNotice(WorldEvent update)=>false;
    private void ApplyWorldEvent(WorldEvent update){Check(clientMapRestore==null,"Retained native state must wait for accepted map context");native.Add(update.DeliveryId);}
    private void AppliedWorldEvent(WorldEvent update,long started,bool isRetained=false){ack.Add(update.DeliveryId);if(isRetained)retained.Add(update.DeliveryId);}
    private static WorldEvent Update(long id)=>new WorldEvent{Kind="npcs",DeliveryId=id,Data=JsonSerializer.SerializeToElement(new{Sequence=id})};
    private void RunCompletion(){/*PRODUCTION_COMPLETION*/}
    /*PRODUCTION_METHODS*/
    private void BeginFrame(){clientMutationBudget.BeginFrame(++frame);}
    private void DrainQueue()
    {
        for(int i=0;i<20&&economyPreparation.IsFenced;i++){
            BeginFrame();PumpEconomyPreparation();
            Check(clientMapRestore!=null,"Map restore remains held while ordered FIFO drains");
            Check(native.Count==0,"No map-native apply occurs merely to clear the FIFO");
            if(economyPreparation.IsFenced){RunCompletion();Check(sends==0&&BoatReplication.Attachments==0,"Partly drained FIFO still blocks entire attachment/ACK branch");}
        }
        Check(!economyPreparation.IsFenced,"Production pump drains queue despite held map restore");
    }
    private static void QueueOnly()
    {
        BoatReplication.Attachments=0;TravelReplication.Accepts=ApartmentReplication.Accepts=0;
        var probe=new MapRestoreFenceProbe();probe.economyPreparation.Enqueue(Update(1),2048);probe.economyPreparation.Enqueue(Update(2),2048);
        probe.RunCompletion();Check(probe.sends==0&&BoatReplication.Attachments==0&&!probe.clientMapRestoreAcknowledged,"Queue-only fence blocks attachment and async ACK");
        probe.DrainQueue();Check(string.Join(",",probe.retained)=="1,2"&&probe.deferredTravelEvents.Count==2,"Actual dispatch moves queued snapshots into ordered map retention");
        probe.RunCompletion();Check(probe.sends==1&&BoatReplication.Attachments==1&&probe.clientMapRestoreAcknowledged,"Completion resumes once after FIFO clears");
        Check(TravelReplication.Accepts==1&&ApartmentReplication.Accepts==1,"Context acceptance also waits for full completion gate");
        probe.RunCompletion();Check(probe.sends==1&&BoatReplication.Attachments==1,"Acknowledged restore cannot reattach or resend");
        probe.BeginFrame();probe.DispatchWorldEvent(new WorldEvent{Kind=PlayerSceneContext.EventKind,Data=JsonSerializer.SerializeToElement(new PlayerSceneContext{PlayerId=probe.playerId,TravelPending=false})},false);
        Check(probe.clientMapRestore==null&&!probe.clientMapRestoreAcknowledged,"Authoritative nonpending context clears held restoration");
        for(int i=0;i<20&&probe.deferredTravelEvents.Count>0;i++){probe.BeginFrame();probe.FlushDeferredTravelEvents();}
        Check(string.Join(",",probe.native)=="1,2"&&probe.deferredTravelBytes==0,"Retained snapshots apply in order after accepted context");
    }
    private static void PendingCommit()
    {
        BoatReplication.Attachments=0;var probe=new MapRestoreFenceProbe();using var release=new ManualResetEventSlim(false);
        probe.economyPreparation.Start(_=>{release.Wait();return new Prepared();},Update(10),0);probe.economyPreparation.Enqueue(Update(11),2048);
        probe.RunCompletion();Check(probe.sends==0&&BoatReplication.Attachments==0,"Running preparation blocks the same entire branch");
        release.Set();var task=(Task)typeof(BoundedPreparationFence<Prepared,WorldEvent>).GetField("pending",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(probe.economyPreparation);Check(task.Wait(TimeSpan.FromSeconds(2)),"Preparation fixture completes");
        probe.BeginFrame();probe.PumpEconomyPreparation();probe.RunCompletion();
        Check(Commits==1&&!probe.economyPreparation.IsPending&&probe.economyPreparation.Count==1,"Heavy production commit leaves later delivery queued for another frame");
        Check(probe.sends==0&&BoatReplication.Attachments==0,"Completed preparation with queued successor still blocks attachment/ACK");
        probe.DrainQueue();probe.RunCompletion();Check(probe.sends==1&&BoatReplication.Attachments==1,"Completion proceeds after commit and FIFO both finish");
    }
    private static void IncrementalProgress(){
        var probe=new MapRestoreFenceProbe();var prepared=new Prepared{Required=3};
        probe.economyPreparation.Start(_=>prepared,Update(20),0);
        var task=(Task)typeof(BoundedPreparationFence<Prepared,WorldEvent>).GetField("pending",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(probe.economyPreparation);Check(task.Wait(TimeSpan.FromSeconds(2)),"Incremental preparation finishes");
        probe.economyPreparation.Enqueue(Update(21),2048);
        for(int i=0;i<2;i++){probe.BeginFrame();probe.PumpEconomyPreparation();Check(probe.economyPreparation.IsPending&&!probe.ack.Contains(20)&&probe.economyPreparation.Count==1,"False progress keeps final ACK and later FIFO pending");}
        probe.BeginFrame();probe.PumpEconomyPreparation();Check(probe.ack.Contains(20)&&!probe.economyPreparation.IsPending&&probe.progressingWorldUpdate==null,"True progress publishes final ACK and clears tracked native cursor");
        probe=new MapRestoreFenceProbe();prepared=new Prepared{Required=3};probe.economyPreparation.Start(_=>prepared,Update(30),0);
        task=(Task)typeof(BoundedPreparationFence<Prepared,WorldEvent>).GetField("pending",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(probe.economyPreparation);task.Wait();probe.BeginFrame();probe.PumpEconomyPreparation();
        probe.CancelPreparedWorldUpdate();Check(prepared.Aborted&&!probe.economyPreparation.IsFenced&&probe.progressingWorldUpdate==null&&!probe.ack.Contains(30),"Production cancellation aborts partial native cursor and clears fence without ACK");
        probe=new MapRestoreFenceProbe();prepared=new Prepared{Required=3};probe.economyPreparation.Start(_=>prepared,Update(40),0);
        task=(Task)typeof(BoundedPreparationFence<Prepared,WorldEvent>).GetField("pending",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(probe.economyPreparation);task.Wait();probe.BeginFrame();probe.PumpEconomyPreparation();
        DedicatedWorldRuntime.Scene="other";bool rejected=false;try{probe.BeginFrame();probe.PumpEconomyPreparation();}catch(InvalidOperationException){rejected=true;}finally{DedicatedWorldRuntime.Scene="3_Docks";}
        Check(rejected&&prepared.Progress==1&&!probe.ack.Contains(40),"Scene replacement between slices is rejected before more native work or ACK");probe.CancelPreparedWorldUpdate();
    }
    public static int Main(){try{QueueOnly();PendingCommit();IncrementalProgress();Console.WriteLine("PASS MapRestoreFenceProbe: "+checks+" assertions; production-extracted completion/pump/dispatch/map-retention integration.");return 0;}catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
}
/*PRODUCTION_FENCE*/
