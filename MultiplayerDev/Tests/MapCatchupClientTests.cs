using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using NivalisTogether.Dedicated;
internal sealed class MapCatchupClientTests
{
 private static int checks;
 private static void Check(bool condition,string reason){checks++;if(!condition)throw new Exception(reason);}
 private static void Reject(Action action,string reason){try{action();}catch(InvalidDataException){Check(true,reason);return;}throw new Exception(reason);}
 private ClientMutationBudget clientMutationBudget=new ClientMutationBudget(Stopwatch.Frequency,maximumActions:256);
 private ClientMutationBudget normalClientMutationBudget;
 private readonly ClientMutationBudget mapCatchupMutationBudget=new ClientMutationBudget(Stopwatch.Frequency,12,1024);
 private bool mutationFrameSelected;private int mutationFrame;
 private readonly HashSet<long> retainedMapDeliveries=new();
 private string mapCatchupEpoch="";
 private long mapCatchupGeneration,mapCatchupAppliedDelivery,mapCatchupReportedDelivery;
 private readonly string playerId=new string('a',32);
 private bool mapCatchupBarrierRequested,mapCatchupBarrierReported;private int mapCatchupBarrierRound;private long mapCatchupBarrierDelivery;
 private double mapCatchupSettledAt=-1,nextMapCatchupProgress;
 private double Now;
 private bool worker,terminalSession,npcMapPresentationPending;
 private readonly List<long> appliedDeliveries=new(),native=new(),acknowledged=new();
 private readonly Queue<(WorldEvent Update,int Bytes)> deferredTravelEvents=new();private int deferredTravelBytes;
 private readonly FakeFence economyPreparation=new();
 private readonly FakeConnection connection=new();
 private PlayerMapRestore clientMapRestore;
 private static class TravelReplication {public static bool IsBusy;}
 private static class ApartmentReplication {public static bool IsBusy;}
 private static class WorkerSceneResidency {public static bool IsRestoringClient;}
 private static class MapCatchupOverlay {public static bool Visible;public static void SetVisible(bool visible)=>Visible=visible;public static void Tick(){}public static void Reset()=>Visible=false;}
 private static class SessionTransitionPolicy {public const int MaxRetainedMapEvents=16384,MaxRetainedMapBytes=256*1024*1024;public static bool CanRetainMapEvent(int count,int bytes,int next)=>count<MaxRetainedMapEvents&&next>0&&next<=MaxRetainedMapBytes-bytes;}
 private sealed class FakeFence {public bool IsPending,IsFenced;}
 private sealed class FakeConnection {public bool Connected=true,AllowSend=true;public readonly List<MapCatchupProgress> Reports=new();public Func<MapCatchupProgress,bool> OnSend;public bool Send(string kind,MapCatchupProgress value){if(!AllowSend)return false;Check(kind==MapCatchupProgress.MessageKind,"Only scoped catch-up wire kind used");Reports.Add(value);return OnSend?.Invoke(value)??true;}}
 private void Log(string message){}
 private void ApplyWorldEvent(WorldEvent update){if(update.Kind==MapCatchupBarrier.EventKind)ApplyMapCatchupBarrier(update);native.Add(update.DeliveryId);AppliedWorldEvent(update,0);}
 private void AppliedWorldEvent(WorldEvent update,long started,bool retained=false){TrackAppliedRetainedMapDelivery(update,retained);appliedDeliveries.Add(update.DeliveryId);}
 private void FlushWorldAcknowledgements(bool full=false){if(!connection.AllowSend)return;acknowledged.AddRange(appliedDeliveries);appliedDeliveries.Clear();}
 /*PRODUCTION*/
 private static WorldEvent Event(long id)=>new(){Kind="npcs",DeliveryId=id,Data=JsonSerializer.SerializeToElement(new{Sequence=id})};
 private static PlayerSceneContext Context(long generation,bool pending,string epoch)=>new(){PlayerId=new string('a',32),Scene="2_Meridian_Market",Generation=generation,TravelPending=pending,Epoch=pending?epoch:""};
 private static MapCatchupClientTests Armed(out PlayerSceneContext pending,out PlayerSceneContext settled){
  TravelReplication.IsBusy=ApartmentReplication.IsBusy=WorkerSceneResidency.IsRestoringClient=false;MapCatchupOverlay.Reset();
  var probe=new MapCatchupClientTests();pending=Context(10,true,new string('b',32));settled=Context(11,false,"");probe.ObserveMapCatchupContext(pending);return probe;
 }
 private static void BudgetAndAuthorization(){
  var probe=Armed(out var pending,out var settled);probe.BeginClientMutationFrame(1);var normal=probe.clientMutationBudget;
  long eightMs=(long)(Stopwatch.Frequency*.008),twelveMs=(long)Math.Ceiling(Stopwatch.Frequency*.012);
  Check(normal.TryStart(0)&&!normal.CanStart(eightMs),"Ordinary play retains4ms budget");
  probe.RetainMapEvent(Event(1),0);probe.Now=200;probe.ObserveMapCatchupContext(settled);
  probe.BeginClientMutationFrame(1);Check(ReferenceEquals(normal,probe.clientMutationBudget)&&!probe.clientMutationBudget.CanStart(eightMs),"Settling mid-frame cannot replace the already spent budget");
  probe.BeginClientMutationFrame(2);Check(ReferenceEquals(probe.clientMutationBudget,probe.mapCatchupMutationBudget),"Next frame shares accelerated catch-up budget");
  Check(probe.clientMutationBudget.TryStart(0)&&probe.clientMutationBudget.CanStart(eightMs)&&!probe.clientMutationBudget.CanStart(twelveMs),"Catch-up admits8ms work but stops strictly at12ms");
  probe.UpdateMapCatchupPresentation();Check(MapCatchupOverlay.Visible&&probe.mapCatchupSettledAt==200,"Slow225s-cap native route starts fresh bounded catch-up and shows owned overlay");
  Check(probe.mapCatchupAppliedDelivery==0,"Retaining and ACKing a frame grants no native progress watermark");
  probe.TrackAppliedRetainedMapDelivery(Event(999),false);Check(probe.mapCatchupAppliedDelivery==0,"Later control IDs cannot masquerade as retained history progress");
  Reject(()=>probe.ObserveMapCatchupContext(Context(12,true,new string('c',32))),"New map cannot cross unapplied retained history");
  probe.ResetMapCatchup();Check(probe.retainedMapDeliveries.Count==0&&probe.mapCatchupEpoch==""&&!MapCatchupOverlay.Visible&&!probe.clientMutationBudget.CanStart(0),"Disconnect clears retained IDs, progress, locks and every active budget");
  probe.ObserveMapCatchupContext(Context(20,true,new string('d',32)));probe.Now=500;probe.ObserveMapCatchupContext(Context(21,false,""));probe.BeginClientMutationFrame(2);
  Check(probe.clientMutationBudget.TryStart(0)&&probe.mapCatchupGeneration==20,"Same-process reconnect can reuse frame number with a fresh authorized epoch");
 }
 private static void OrderedDrainBeyondOldDeadline(){
  var probe=Armed(out var pending,out var settled);var grace=new MapCatchupDeadline();grace.ObserveContext(pending,0,0);
  for(int i=1;i<=3218;i++)probe.RetainMapEvent(Event(i),0);
  probe.FlushWorldAcknowledgements();Check(probe.acknowledged.Count==3218&&probe.native.Count==0&&probe.mapCatchupAppliedDelivery==0,"Actual retention acknowledges coherent storage only, never native progress");
  probe.Now=200;probe.ObserveMapCatchupContext(settled);grace.ObserveContext(settled,200,3220);
  probe.connection.OnSend=message=>{Check(probe.appliedDeliveries.Count==0,"Real delivery ACKs precede grace reports on TCP");grace.ObserveProgress(message,probe.Now,3222);return true;};
  // Limit to140 inexpensive fake native entries per frame to model the observed
  // ~153 events/sec drain: the complete historical replay takes23s, beyond20s.
  probe.clientMutationBudget=new ClientMutationBudget(Stopwatch.Frequency,10000,140);
  probe.economyPreparation.IsFenced=true;int frame=0;
  while(probe.deferredTravelEvents.Count>0){
   probe.Now=201+frame;probe.clientMutationBudget.BeginFrame(++frame);probe.FlushDeferredTravelEvents();probe.FlushWorldAcknowledgements();probe.ReportMapCatchupProgress();
   if(frame>=20)Check(grace.CanDeferDeliveryTimeout(probe.Now),"Verified advancing retained history keeps only authorized catch-up alive past original20s deadline");
  }
  Check(frame==23&&probe.native.Count==3218&&probe.retainedMapDeliveries.Count==0,"Entire3218-event history drains within120s with no coalescing or dropped chunks");
  for(int i=0;i<probe.native.Count;i++)Check(probe.native[i]==i+1,"Native history remains strictFIFO exactly once");
  Check(probe.connection.Reports.TrueForAll(r=>!r.Complete)&&probe.connection.Reports[^1].RequestBarrier,"Local FIFO drain requests an ordered service frontier but cannot announce completion");
  probe.ApplyWorldEvent(Event(3219));probe.economyPreparation.IsFenced=false;probe.ReportMapCatchupProgress();
  Check(MapCatchupOverlay.Visible&&probe.IsNativeMapCatchup&&grace.CanDeferDeliveryTimeout(probe.Now),"Empty local queues keep accelerated budget and overlay while upstream history remains");
  var marker=grace.BeginBarrierRound(probe.Now);grace.BarrierIssued(marker,3221);
  var markerEvent=new WorldEvent{Kind=MapCatchupBarrier.EventKind,TargetPlayerId=probe.playerId,DeliveryId=3221,Data=JsonSerializer.SerializeToElement(marker)};
  probe.ApplyWorldEvent(markerEvent);probe.ApplyWorldEvent(Event(3222));probe.ReportMapCatchupProgress();
  Check(probe.connection.Reports[^1].Complete&&probe.connection.Reports[^1].AppliedDeliveryId==3221&&probe.acknowledged.Contains(3222)&&grace.CanDeferDeliveryTimeout(probe.Now),"Exact marker completion follows all applied ACKs but still awaits service confirmation");
  Check(grace.TryConfirm(probe.Now,0,out var confirmation),"Service confirms only actual prefix acknowledgement");probe.AcceptMapCatchupConfirmation(confirmation);
  Check(!MapCatchupOverlay.Visible&&probe.mapCatchupEpoch=="","Only service-confirmed fresh prefix removes overlay");
 }
 private static void PendingAndFailure(){
  var probe=Armed(out _,out var settled);probe.RetainMapEvent(Event(1),0);probe.Now=10;probe.ObserveMapCatchupContext(settled);
  probe.economyPreparation.IsPending=probe.economyPreparation.IsFenced=true;probe.BeginClientMutationFrame(1);probe.FlushDeferredTravelEvents();probe.ReportMapCatchupProgress();
  Check(probe.native.Count==0&&probe.connection.Reports.Count==0,"Pending background/native preparation cannot produce applied progress or bypass FIFO");
  probe.economyPreparation.IsPending=probe.economyPreparation.IsFenced=false;probe.BeginClientMutationFrame(2);probe.FlushDeferredTravelEvents();
  probe.connection.AllowSend=false;probe.ReportMapCatchupProgress();Check(probe.connection.Reports.Count==0&&probe.mapCatchupEpoch.Length>0,"Blocked final ACK transport cannot close grace first");
  probe.connection.AllowSend=true;probe.ReportMapCatchupProgress();Check(probe.connection.Reports.Count==1&&probe.connection.Reports[0].RequestBarrier&&!probe.connection.Reports[0].Complete,"Final ACK and service barrier request resume together when transport accepts them");
  probe=Armed(out _,out settled);probe.RetainMapEvent(Event(1),0);probe.Now=1;probe.ObserveMapCatchupContext(settled);probe.Now=122;
  bool expired=false;try{probe.UpdateMapCatchupPresentation();}catch(IOException){expired=true;}Check(expired,"Client independently bounds stalled catch-up duration");
  probe.ResetMapCatchup();Check(!MapCatchupOverlay.Visible,"Failure cleanup removes owned catch-up UI");
 }
 private static void EmptyAndRepeatedFrontiers(){
  var p=Armed(out _,out var settled);p.Now=10;p.ObserveMapCatchupContext(settled);p.ReportMapCatchupProgress();
  Check(p.connection.Reports.Count==1&&p.connection.Reports[0].RequestBarrier&&p.connection.Reports[0].AppliedDeliveryId==0&&MapCatchupOverlay.Visible,"An empty local backlog still needs a service frontier, with no invented applied ID");
  WorldEvent Marker(int round,long id)=>new(){Kind=MapCatchupBarrier.EventKind,TargetPlayerId=p.playerId,DeliveryId=id,Data=JsonSerializer.SerializeToElement(new MapCatchupBarrier{Epoch=p.mapCatchupEpoch,Generation=10,Round=round})};
  Reject(()=>p.ApplyMapCatchupBarrier(Marker(2,10)),"Client rejects skipped marker rounds");
  p.ApplyWorldEvent(Marker(1,10));p.ReportMapCatchupProgress();
  Check(p.connection.Reports[^1].Complete&&MapCatchupOverlay.Visible&&p.mapCatchupEpoch.Length>0,"Applying marker cannot close synchronization without service confirmation");
  p.Now=14;p.ApplyWorldEvent(Event(11));p.FlushWorldAcknowledgements();p.ReportMapCatchupProgress();
  Check(!p.connection.Reports[^1].Complete&&p.connection.Reports[^1].AppliedDeliveryId==11,"Actual later FIFO application keeps liveness between bounded freshness rounds");
  Reject(()=>p.ObserveMapCatchupContext(Context(12,true,new string('c',32))),"A new transition cannot replace an unconfirmed frontier even with no retained map entries");
  p.ApplyWorldEvent(Marker(2,20));p.ApplyWorldEvent(Event(21));p.ReportMapCatchupProgress();
  Check(p.connection.Reports[^1].Complete&&p.connection.Reports[^1].AppliedDeliveryId==20&&p.acknowledged.Contains(21),"Later work cannot change the exact marker confirmation identity");
  Reject(()=>p.AcceptMapCatchupConfirmation(new MapCatchupConfirmation{Epoch=p.mapCatchupEpoch,Generation=10,Round=1,BarrierDeliveryId=10}),"Stale service confirmation cannot close a newer round");
  var confirmation=new MapCatchupConfirmation{Epoch=p.mapCatchupEpoch,Generation=10,Round=2,BarrierDeliveryId=20};p.AcceptMapCatchupConfirmation(confirmation);
  Check(!MapCatchupOverlay.Visible&&!p.IsNativeMapCatchup,"Fresh matching service confirmation releases catch-up mode");
  Reject(()=>p.AcceptMapCatchupConfirmation(confirmation),"Confirmation replay cannot reopen or reclose synchronization");
  p.ResetMapCatchup();Reject(()=>p.ApplyMapCatchupBarrier(Marker(1,30)),"Old marker cannot cross disconnect/reset");
 }
 public static int Main(){try{BudgetAndAuthorization();OrderedDrainBeyondOldDeadline();PendingAndFailure();EmptyAndRepeatedFrontiers();Console.WriteLine("PASS MapCatchupClientTests: "+checks+" production-extracted budget, FIFO drain, progress, ACK ordering and reconnect assertions.");return 0;}catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
}
