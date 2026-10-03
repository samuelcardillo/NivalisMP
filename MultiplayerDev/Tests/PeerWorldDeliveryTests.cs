using System;using System.Collections;using System.Collections.Generic;using System.IO;using System.Linq;using System.Net.Sockets;using System.Reflection;using System.Runtime.Serialization;using System.Text.Json;
using NivalisTogether.Dedicated;using NivalisTogether.Server;
public static class PeerWorldDeliveryTests {
 private static int checks;private static readonly BindingFlags F=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
 private static void Check(bool value,string why){checks++;if(!value)throw new Exception(why);}
 private static void Set(object o,string field,object value)=>o.GetType().GetField(field,F).SetValue(o,value);
 private static T Get<T>(object o,string field)=>(T)o.GetType().GetField(field,F).GetValue(o);
 private static object Call(string method,object service,params object[] args){try{return typeof(DedicatedService).GetMethod(method,BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance).Invoke(service,args);}catch(TargetInvocationException ex)when(ex.InnerException!=null){throw ex.InnerException;}}
 private sealed class Fixture:IDisposable {
  internal DedicatedService Service;internal object Worker,Slow,Fast;internal readonly List<DedicatedConnection> Connections=new();
  internal Fixture(){
   Service=(DedicatedService)FormatterServices.GetUninitializedObject(typeof(DedicatedService));Worker=Peer("worker");Slow=Peer("player");Fast=Peer("player");
   var list=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(Slow.GetType()));list.Add(Worker);list.Add(Slow);list.Add(Fast);
   Set(Service,"_peers",list);Set(Service,"_worker",Worker);Set(Service,"_ready",true);Set(Service,"_world",new WorldMetadata{WorldId=new string('a',32),Revision=1});Credits();
  }
  internal object Peer(string role){var p=Activator.CreateInstance(typeof(DedicatedService).GetNestedType("Peer",BindingFlags.NonPublic),true);var c=DedicatedConnection.FromAccepted(new TcpClient());Connections.Add(c);Set(p,"Connection",c);Set(p,"Role",role);Set(p,"Loaded",role=="player");Set(p,"Account",new AccountProfile{AccountId=Guid.NewGuid().ToString("N")});return p;}
  internal void Credits(){Call("GrantWorldCredits",Service);Drain(Worker);}
  internal void Publish(WorldEvent update,double seconds){Call("WorkerMessage",Service,Worker,new WireMessage{Type="worldEvent",Payload=JsonSerializer.SerializeToElement(update,DedicatedProtocol.Json)},DateTime.UnixEpoch.AddSeconds(seconds));}
  internal void Pump(object peer,double seconds)=>Call("PumpWorldEvents",null,peer,DateTime.UnixEpoch.AddSeconds(seconds));
  internal void Ack(object peer,params long[] ids)=>Call("AcknowledgeWorldEvents",null,peer,new WorldEventAcknowledgement{DeliveryIds=ids});
  internal void Progress(object peer,MapCatchupProgress progress,double seconds)=>Call("Handle",Service,peer,new WireMessage{Type=MapCatchupProgress.MessageKind,Payload=JsonSerializer.SerializeToElement(progress,DedicatedProtocol.Json)},DateTime.UnixEpoch.AddSeconds(seconds));
  internal void Saturate(object peer){Set(peer,"BulkInFlight",DedicatedService.PlayerBulkWindowBytes);Set(peer,"LastDeliveryId",1L);var receipt=Activator.CreateInstance(typeof(DedicatedService).GetNestedType("Delivery",BindingFlags.NonPublic),true);Set(receipt,"Cost",DedicatedService.PlayerBulkWindowBytes);Set(receipt,"Sent",DateTime.UnixEpoch);Get<IDictionary>(peer,"Deliveries").Add(1L,receipt);}
  public void Dispose(){foreach(var c in Connections)c.Dispose();}
 }
 private static List<WireMessage> Drain(object peer){var connection=Get<DedicatedConnection>(peer,"Connection");var queue=Get<Queue<byte[]>>(connection,"_outgoing");var result=new List<WireMessage>();while(queue.Count>0){var frame=queue.Dequeue();result.Add(JsonSerializer.Deserialize<WireMessage>(new ReadOnlySpan<byte>(frame,4,frame.Length-4),DedicatedProtocol.Json));}Set(connection,"<PendingSendBytes>k__BackingField",0);return result;}
 private static WorldEvent State(long sequence,string kind="npcs",string target="")=>new(){Kind=kind,TargetPlayerId=target,Data=JsonSerializer.SerializeToElement(new{Sequence=sequence})};
 private static void SlowPeerCannotPauseOtherPeer(){
  using var f=new Fixture();f.Saturate(f.Slow);var seen=new List<long>();
  for(int i=0;i<40;i++){
   f.Publish(State(300+i),i);f.Pump(f.Slow,i);f.Pump(f.Fast,i);
   foreach(var message in Drain(f.Fast)){Check(message.Type=="worldEvent","Only actual world deliveries emitted");var e=message.Read<WorldEvent>();seen.Add(e.Data.GetProperty("Sequence").GetInt64());f.Ack(f.Fast,e.DeliveryId);}
   f.Credits();Check(Get<int>(f.Service,"_bulkGranted")==WorldEventFlow.BulkWindowBytes,"Stalled recipient cannot suppress worker capture credit");
  }
  Check(seen.SequenceEqual(Enumerable.Range(300,40).Select(i=>(long)i)),"Healthy peer receives every fresh source sequence while slow peer is stalled40seconds");
  Check(Get<PeerWorldEventQueue>(f.Slow,"WorldQueue").Count==40&&Get<long>(f.Slow,"LastDeliveryId")==1&&Get<IDictionary>(f.Slow,"Deliveries").Count==1,"Unsent slow history has no issued IDs or per-delivery timeout clocks");
  f.Ack(f.Slow,1);var drained=new List<long>();
  for(int i=0;i<4&&Get<PeerWorldEventQueue>(f.Slow,"WorldQueue").Count>0;i++){f.Pump(f.Slow,50+i);foreach(var wire in Drain(f.Slow)){var e=wire.Read<WorldEvent>();drained.Add(e.Data.GetProperty("Sequence").GetInt64());f.Ack(f.Slow,e.DeliveryId);}}
  Check(drained.SequenceEqual(seen)&&Get<PeerWorldEventQueue>(f.Slow,"WorldQueue").Bytes==0,"Slow peer resumes every complete event in exact original order");
 }
 private static void StrictControlOrderAndSendFailure(){
  using var f=new Fixture();f.Saturate(f.Slow);f.Publish(State(1),0);f.Publish(State(2,"timeWeather"),1);f.Pump(f.Slow,2);
  Check(Drain(f.Slow).Count==0&&Get<PeerWorldEventQueue>(f.Slow,"WorldQueue").Count==2,"Control cannot jump an older blocked bulk head");
  f.Ack(f.Slow,1);f.Pump(f.Slow,3);var sent=Drain(f.Slow).Select(m=>m.Read<WorldEvent>()).ToArray();
  Check(sent.Length==2&&sent[0].Kind=="npcs"&&sent[1].Kind=="timeWeather"&&sent[0].DeliveryId==2&&sent[1].DeliveryId==3,"Per-peer send preserves global wire order and consecutive issued IDs");
  var receipts=Get<IDictionary>(f.Slow,"Deliveries");Check((DateTime)receipts[2L].GetType().GetField("Sent",F).GetValue(receipts[2L])==DateTime.UnixEpoch.AddSeconds(3),"Delivery deadline begins at actual send, not enqueue");
  f.Publish(State(3),4);var c=Get<DedicatedConnection>(f.Slow,"Connection");var pending=Get<Queue<byte[]>>(c,"_outgoing");for(int i=0;i<DedicatedProtocol.MaxQueueFrames;i++)pending.Enqueue(new byte[1]);long before=Get<long>(f.Slow,"LastDeliveryId");f.Pump(f.Slow,5);
  Check(Get<long>(f.Slow,"LastDeliveryId")==before&&Get<PeerWorldEventQueue>(f.Slow,"WorldQueue").Count==1,"Rejected transport Send consumes neither queued state nor delivery ID");pending.Clear();
 }
 private static void AuthorityAndContextTiming(){
  using var f=new Fixture();f.Saturate(f.Slow);f.Publish(State(1),0);string player=Get<string>(f.Slow,"Id"),epoch=new string('b',32);
  var pending=new PlayerSceneContext{PlayerId=player,Scene="1_Lowtown",Generation=10,TravelPending=true,Epoch=epoch};
  var settled=new PlayerSceneContext{PlayerId=player,Scene="2_Meridian_Market",Generation=11};
  foreach(var context in new[]{pending,settled})f.Publish(new WorldEvent{Kind=PlayerSceneContext.EventKind,TargetPlayerId=player,Data=JsonSerializer.SerializeToElement(context,DedicatedProtocol.Json)},1);
  Check(Get<PlayerSceneMembership>(f.Slow,"SceneMembership").Current.Generation==11,"Worker scene authorization changes at ingress even when client delivery waits");
  Check(Get<PeerWorldEventQueue>(f.Fast,"WorldQueue").Count==1,"Private scene contexts never enter another peer's FIFO");
  var policy=Get<MapCatchupDeadline>(f.Slow,"MapCatchup");Check(!policy.CanDeferDeliveryTimeout(99),"Unsent contexts cannot grant client catch-up grace");
  f.Ack(f.Slow,1);f.Pump(f.Slow,100);var wire=Drain(f.Slow).Select(m=>m.Read<WorldEvent>()).ToArray();
  Check(wire.Length==3&&wire[1].Kind==PlayerSceneContext.EventKind&&wire[2].Kind==PlayerSceneContext.EventKind,"Actual private contexts remain behind older world data");
  Check(policy.ObserveProgress(new MapCatchupProgress{Epoch=epoch,Generation=10,AppliedDeliveryId=4},101,4)&&policy.CanDeferDeliveryTimeout(101),"Grace anchors to actual settled delivery after long queue delay");
  Call("ClearDeliveries",null,f.Slow);Check(Get<PeerWorldEventQueue>(f.Slow,"WorldQueue").Count==0&&Get<IDictionary>(f.Slow,"Deliveries").Count==0&&!policy.CanDeferDeliveryTimeout(101),"World import/disconnect clears FIFO, issued receipts and grace together");
 }
 private static void OnlyLaggingPeerIsRejected(){
  using var f=new Fixture();f.Saturate(f.Slow);var queue=Get<PeerWorldEventQueue>(f.Slow,"WorldQueue");var value=State(1);value.WorldId=new string('a',32);value.Revision=1;int cost=WorldEventFlow.Cost(value);
  for(int i=0;i<PeerWorldEventQueue.MaxEvents;i++)queue.Enqueue(value,cost,false,0);
  f.Publish(State(999),1);
  Check(!Get<DedicatedConnection>(f.Slow,"Connection").Connected&&Get<DedicatedConnection>(f.Fast,"Connection").Connected&&Get<DedicatedConnection>(f.Worker,"Connection").Connected,"Queue overflow disconnects only the lagging recipient");
  f.Pump(f.Fast,1);Check(Drain(f.Fast).Any(m=>m.Type=="worldEvent"&&m.Read<WorldEvent>().Data.GetProperty("Sequence").GetInt64()==999),"Same authoritative event still reaches healthy peer after overflow");
  using var expiry=new Fixture();expiry.Saturate(expiry.Slow);expiry.Publish(State(1),0);expiry.Pump(expiry.Slow,120);
  Check(!Get<DedicatedConnection>(expiry.Slow,"Connection").Connected&&Get<DedicatedConnection>(expiry.Fast,"Connection").Connected,"Unsent queue age bound rejects only its owner");
 }
 private static void PlayerWindowAndDiagnostics(){
  using var f=new Fixture();var queue=Get<PeerWorldEventQueue>(f.Slow,"WorldQueue");var value=State(1);value.WorldId=new string('a',32);value.Revision=1;value.Data=JsonSerializer.SerializeToElement(new{Sequence=1,Padding=new string('x',7141)});int cost=WorldEventFlow.Cost(value);
  for(int i=0;i<600;i++)queue.Enqueue(value,cost,false,0);
  var issued=new List<WorldEvent>();for(int i=0;i<30;i++){f.Pump(f.Slow,1);issued.AddRange(Drain(f.Slow).Select(m=>m.Read<WorldEvent>()));}
  Check(DedicatedService.PlayerBulkWindowBytes==1024*1024&&WorldEventFlow.BulkWindowBytes==256*1024,"Player pipeline experiment does not change authenticated worker ingress allowance");
  Check(cost==8192&&issued.Count==128&&Get<int>(f.Slow,"BulkInFlight")==DedicatedService.PlayerBulkWindowBytes&&queue.Count==472,"Sent-but-unapplied player state is bounded at exactly one MiB");
  Check(issued.Select(e=>e.DeliveryId).SequenceEqual(Enumerable.Range(1,128).Select(i=>(long)i)),"Expanded window preserves every issued delivery in FIFO order");
  Check(Get<long>(f.Slow,"WorldSentCostBytes")==128L*cost&&Get<long>(f.Slow,"WorldAcknowledgedCostBytes")==0,"Send accounting never implies native application or acknowledges queued history");
  f.Ack(f.Slow,1,2,3);f.Ack(f.Slow,1,2,3);
  Check(Get<long>(f.Slow,"WorldAcknowledgedCostBytes")==3L*cost,"Repeated receipt batches cannot inflate applied-byte diagnostics");
  var original=Console.Out;using var capture=new StringWriter();
  try{Console.SetOut(capture);Call("LogWorldDelivery",null,f.Slow,DateTime.UnixEpoch.AddSeconds(10));Call("LogWorldDelivery",null,f.Slow,DateTime.UnixEpoch.AddSeconds(19));}
  finally{Console.SetOut(original);}
  string diagnostic=capture.ToString();
  Check(diagnostic.Split("WORLD_DELIVERY").Length==2&&diagnostic.Contains("queueEvents=472")&&diagnostic.Contains("queueOldestSeconds=10.000")&&diagnostic.Contains("issuedOldestSeconds=9.000"),"Periodic diagnostics distinguish unsent history age from issued receipt age and are rate bounded");
  Check(diagnostic.Contains("sentCostBytesTotal=1048576")&&diagnostic.Contains("acknowledgedCostBytesTotal=24576")&&!diagnostic.Contains("Sequence"),"Delivery counters reveal throughput without serializing private world payloads");
  f.Pump(f.Slow,20);Check(Drain(f.Slow).Count==3&&queue.Count==469,"Only actually acknowledged credit permits more queued state to be sent");
  using var small=new Fixture();var smallQueue=Get<PeerWorldEventQueue>(small.Slow,"WorldQueue");value=State(1);value.WorldId=new string('a',32);value.Revision=1;cost=WorldEventFlow.Cost(value);
  for(int i=0;i<300;i++)smallQueue.Enqueue(value,cost,false,0);
  for(int i=0;i<15;i++){small.Pump(small.Slow,1);Drain(small.Slow);}
  Check(Get<IDictionary>(small.Slow,"Deliveries").Count==192&&smallQueue.Count==108&&Get<int>(small.Slow,"BulkInFlight")==192*2048,"Minimum-size events cannot overflow the client's256-message preparation FIFO");
 }
 private static string AuthorizeCatchup(Fixture f){
  string player=Get<string>(f.Slow,"Id"),epoch=new string('b',32);
  foreach(var context in new[]{new PlayerSceneContext{PlayerId=player,Scene="1_Lowtown",Generation=10,TravelPending=true,Epoch=epoch},new PlayerSceneContext{PlayerId=player,Scene="2_Meridian_Market",Generation=11}})
   f.Publish(new WorldEvent{Kind=PlayerSceneContext.EventKind,TargetPlayerId=player,Data=JsonSerializer.SerializeToElement(context)},0);
  f.Pump(f.Slow,1);Drain(f.Slow);f.Ack(f.Slow,1,2);return epoch;
 }
 private static void OrderedServiceConfirmation(){
  using var f=new Fixture();string epoch=AuthorizeCatchup(f);f.Publish(State(100),2);
  f.Progress(f.Slow,new MapCatchupProgress{Epoch=epoch,Generation=10,RequestBarrier=true},2.1);
  Check(Get<PeerWorldEventQueue>(f.Slow,"WorldQueue").Count==2&&Get<PeerWorldEventQueue>(f.Fast,"WorldQueue").Count==1,"Service marker follows current queued prefix and remains private");
  f.Pump(f.Slow,2.2);var sent=Drain(f.Slow).Select(m=>m.Read<WorldEvent>()).ToArray();
  Check(sent.Length==2&&sent[0].Kind=="npcs"&&sent[1].Kind==MapCatchupBarrier.EventKind&&sent[1].DeliveryId==4,"Barrier never bypasses older authoritative state");
  f.Publish(State(101),2.3);f.Pump(f.Slow,2.3);Drain(f.Slow);
  f.Progress(f.Slow,new MapCatchupProgress{Epoch=epoch,Generation=10,AppliedDeliveryId=4,Complete=true},2.4);
  Check(Drain(f.Slow).Count==0,"Client marker report cannot substitute for ordinary application ACKs");
  f.Ack(f.Slow,4);Call("PumpMapCatchup",f.Service,f.Slow,DateTime.UnixEpoch.AddSeconds(2.5));
  Check(Drain(f.Slow).Count==0,"Marker ACK alone cannot skip an earlier unacknowledged world mutation");
  f.Ack(f.Slow,3);Call("PumpMapCatchup",f.Service,f.Slow,DateTime.UnixEpoch.AddSeconds(2.6));
  var confirmation=Drain(f.Slow);Check(confirmation.Count==1&&confirmation[0].Type==MapCatchupConfirmation.MessageKind&&confirmation[0].Read<MapCatchupConfirmation>().BarrierDeliveryId==4,"Fresh fully applied prefix confirms while later live delivery remains outstanding");
  Check(Get<IDictionary>(f.Slow,"Deliveries").Contains(5L),"Confirmation neither acknowledges nor removes later live state");
  f.Progress(f.Slow,new MapCatchupProgress{Epoch=epoch,Generation=10,AppliedDeliveryId=5},3.7);
  f.Progress(f.Slow,new MapCatchupProgress{Epoch=epoch,Generation=10,AppliedDeliveryId=4,Complete=true},3.8);
  Check(Get<DedicatedConnection>(f.Slow,"Connection").Connected&&Drain(f.Slow).Count==0,"In-flight honest progress after a delayed confirmation is inert and never disconnects or reconfirms");
  Check(Get<IDictionary>(f.Slow,"Deliveries").Contains(5L)&&!Get<MapCatchupDeadline>(f.Slow,"MapCatchup").CanDeferDeliveryTimeout(3.8),"Trailing reports neither grant receipt credit nor reopen confirmed grace");
  using var retry=new Fixture();epoch=AuthorizeCatchup(retry);retry.Progress(retry.Slow,new MapCatchupProgress{Epoch=epoch,Generation=10,RequestBarrier=true},2);
  retry.Pump(retry.Slow,5);Drain(retry.Slow);retry.Ack(retry.Slow,3);
  retry.Progress(retry.Slow,new MapCatchupProgress{Epoch=epoch,Generation=10,AppliedDeliveryId=3,Complete=true},5.1);
  Check(Drain(retry.Slow).Count==0&&Get<PeerWorldEventQueue>(retry.Slow,"WorldQueue").Count==1,"Stale queued prefix cannot close overlay; a new ordered frontier is appended");
  retry.Pump(retry.Slow,5.2);var renewed=Drain(retry.Slow).Single().Read<WorldEvent>();
  Check(renewed.Data.Deserialize<MapCatchupBarrier>().Round==2,"Service advances a bounded freshness round");
  retry.Ack(retry.Slow,4);retry.Progress(retry.Slow,new MapCatchupProgress{Epoch=epoch,Generation=10,AppliedDeliveryId=4,Complete=true},5.3);
  Check(Drain(retry.Slow).Single().Read<MapCatchupConfirmation>().Round==2,"Fresh next prefix completes within original catch-up phase");
 }
 private static void CatchupPolicyFailuresStayPeerLocal(){
  using var expiry=new Fixture();string epoch=AuthorizeCatchup(expiry);
  expiry.Progress(expiry.Slow,new MapCatchupProgress{Epoch=epoch,Generation=10,RequestBarrier=true},2);
  expiry.Pump(expiry.Slow,2.1);Drain(expiry.Slow);
  expiry.Progress(expiry.Slow,new MapCatchupProgress{Epoch=epoch,Generation=10,AppliedDeliveryId=3,Complete=true},2.2);
  Check(Get<DedicatedConnection>(expiry.Slow,"Connection").Connected,"Unacknowledged marker prevents premature confirmation before expiry");
  Call("PumpMapCatchup",expiry.Service,expiry.Slow,DateTime.UnixEpoch.AddSeconds(22.3));
  Check(!Get<DedicatedConnection>(expiry.Slow,"Connection").Connected&&Get<DedicatedConnection>(expiry.Fast,"Connection").Connected&&Get<DedicatedConnection>(expiry.Worker,"Connection").Connected,"Periodic catchup progress expiry rejects only its peer without escaping service Tick");
  using var rounds=new Fixture();epoch=AuthorizeCatchup(rounds);
  rounds.Progress(rounds.Slow,new MapCatchupProgress{Epoch=epoch,Generation=10,RequestBarrier=true},2);
  for(int round=1;round<=MapCatchupDeadline.MaximumBarrierRounds;round++){
   double now=2+round*3;rounds.Pump(rounds.Slow,now);var marker=Drain(rounds.Slow).Single().Read<WorldEvent>();
   Check(marker.Data.Deserialize<MapCatchupBarrier>().Round==round,"Only consecutive bounded freshness rounds are issued");
   rounds.Ack(rounds.Slow,marker.DeliveryId);rounds.Progress(rounds.Slow,new MapCatchupProgress{Epoch=epoch,Generation=10,AppliedDeliveryId=marker.DeliveryId,Complete=true},now+.1);
  }
  Check(!Get<DedicatedConnection>(rounds.Slow,"Connection").Connected&&Get<DedicatedConnection>(rounds.Fast,"Connection").Connected&&Get<DedicatedConnection>(rounds.Worker,"Connection").Connected,"Exhausted freshness rounds reject only the lagging actor instead of terminating authority");
  using var reused=new Fixture();epoch=AuthorizeCatchup(reused);string player=Get<string>(reused.Slow,"Id");
  reused.Publish(new WorldEvent{Kind=PlayerSceneContext.EventKind,TargetPlayerId=player,Data=JsonSerializer.SerializeToElement(new PlayerSceneContext{PlayerId=player,Scene="2_Meridian_Market",Generation=12,TravelPending=true,Epoch=epoch})},2);
  reused.Pump(reused.Slow,3);
  Check(!Get<DedicatedConnection>(reused.Slow,"Connection").Connected&&Get<DedicatedConnection>(reused.Fast,"Connection").Connected&&Get<DedicatedConnection>(reused.Worker,"Connection").Connected,"Delivered reused catchup epoch is contained at recipient pump instead of escaping service Tick");
 }
 public static int Run(){SlowPeerCannotPauseOtherPeer();StrictControlOrderAndSendFailure();AuthorityAndContextTiming();OnlyLaggingPeerIsRejected();PlayerWindowAndDiagnostics();OrderedServiceConfirmation();CatchupPolicyFailuresStayPeerLocal();Console.WriteLine("PASS PeerWorldDeliveryTests: "+checks+" actual worker ingress, per-peer delivery, credit, privacy, deadline and isolated-failure assertions.");return checks;}
}
