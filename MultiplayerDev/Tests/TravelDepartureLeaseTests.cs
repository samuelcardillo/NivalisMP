using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Nivalis;
using UnityEngine;
using Object=UnityEngine.Object;
namespace NivalisTogether.Dedicated {
/*PRODUCTION_TYPES*/
public sealed class BoatPose {}
public sealed class PlayerPose {public string PlayerId,AccountId,Scene,PropertyId="";public bool Active=true;public float X,Y,Z;}
public sealed class InteractionRequest {public string PlayerId,AccountId,RequestId,Kind,TargetId;public JsonElement Data;}
public sealed class PlayerSceneContext {public string PlayerId,Scene,PropertyId="",Epoch="";public long Generation=1;public bool TravelPending;}
public static class PlayerMapRuntime {public static PlayerSceneContext Context;public static PlayerSceneContext GetContext(string player)=>Context?.PlayerId==player?Context:null;}
public static class DedicatedProtocol {public static JsonSerializerOptions Json=new JsonSerializerOptions();public static bool Text(string s,int max,bool empty=false)=>s!=null&&s.Length<=max&&(empty||s.Length>0)&&s.IndexOfAny(new[]{'\r','\n'})<0;public static bool PoseValid(PlayerPose p)=>p!=null&&float.IsFinite(p.X)&&float.IsFinite(p.Y)&&float.IsFinite(p.Z);}
public static class SleepReplication {public static bool IsBusy;}
public static class ApartmentReplication {public static bool IsPlayerBusy(string player)=>false;}
public static class BoatAuthority {public static void CancelTravel(string p){}public static void ValidateTravel(string p,string b,BoatPortal portal,double now){}public static void CommitTravel(string p,BoatPose b,string s){}}
public static class TravelReplication {
 const string RequestKind="group-travel-request",PreparedKind="group-travel-prepared",ArrivedKind="group-travel-arrived";
 static bool worker=true,terminal;static bool HasAuthority=>true;static double now;
 static readonly HashSet<string> present=new HashSet<string>();
 static readonly Dictionary<string,PoseSample> poses=new Dictionary<string,PoseSample>();
 static readonly Dictionary<string,PlayerOperation> operations=new Dictionary<string,PlayerOperation>();
 static readonly Dictionary<string,GroupTravelEvent> completed=new Dictionary<string,GroupTravelEvent>();
 static readonly Queue<string> completedOrder=new Queue<string>();
 public static Action<string,string> BeginPlayerTransition,CancelPlayerTransition,FailPlayerTransition;
 public static Action<string,string,string,string> CommitPlayerContext;
 static readonly List<GroupTravelEvent> emitted=new List<GroupTravelEvent>();
 static void PublishCopy(GroupTravelEvent e)=>emitted.Add(e);
 static void PublishStandalone(string phase,string player,string request,string message)=>PublishCopy(Standalone(phase,player,request,message));
 static void ValidateArrivalBoat(GroupTravelEvent e,InteractionRequest r,GroupTravelAcknowledgement a){}
 static void RequireWorldReady(){}
 static void ValidateTransition(SceneTransitionInteractable p){if(p==null||!p.IsActive||!p.CanInteract())throw new InvalidOperationException("Invalid native departure");ValidateDestination(p.keyTo);}
 /*PRODUCTION_MEMBERS*/
 static string player="11111111111111111111111111111111",account="22222222222222222222222222222222";
 static PortalKey key;static PlayerPose pose;static int assertions;
 static void Check(bool value,string why){assertions++;if(!value)throw new Exception(why);}
 static void Reset(float x=1,double poseTime=100){
  present.Clear();poses.Clear();operations.Clear();completed.Clear();completedOrder.Clear();emitted.Clear();now=100;worker=true;terminal=false;SleepReplication.IsBusy=false;
  var location=new WorldLocation{Guid="location",SceneName="target",SceneIndex=2};key=new PortalKey{Pointer=1,Guid="portal",name="native-road",TargetLocation=location};location.DefaultLocation=key;
  TravelUnlockManager.Instance=new TravelUnlockManager{_allPortalKeys=new[]{key}};MultiplayerSandboxRuntime.Public=true;DedicatedWorldRuntime.Scene="source";
  Object.Loaded=new[]{new SceneTransitionInteractable{keyTo=key,isCabTravel=true,isActiveAndEnabled=true,gameObject=new GameObject{scene=new Scene{name="source",isLoaded=true}},transform=new Transform{position=new Vector3(0,0,0)}}};
  PlayerMapRuntime.Context=new PlayerSceneContext{PlayerId=player,Scene="source"};
  BeginPlayerTransition=(p,e)=>{var c=PlayerMapRuntime.Context;c.TravelPending=true;c.Epoch=e;c.Generation++;};
  CancelPlayerTransition=(p,e)=>{var c=PlayerMapRuntime.Context;if(c!=null&&c.Epoch==e){c.TravelPending=false;c.Epoch="";c.Generation++;}};
  FailPlayerTransition=(p,e)=>{};CommitPlayerContext=(p,s,prop,portal)=>{PlayerMapRuntime.Context.Scene=s;PlayerMapRuntime.Context.TravelPending=false;PlayerMapRuntime.Context.Epoch="";PlayerMapRuntime.Context.Generation++;};
  SetPlayerPresent(player,true);pose=new PlayerPose{PlayerId=player,AccountId=account,Scene="source",X=x};UpdatePlayerPose(pose,poseTime);
 }
 static InteractionRequest Start(bool map=false){var r=new InteractionRequest{PlayerId=player,AccountId=account,RequestId=Guid.NewGuid().ToString("N"),Kind=RequestKind,TargetId=key.Guid,Data=JsonSerializer.SerializeToElement(new GroupTravelIntent{ToScene="target",PortalName=key.name,TargetLocationId="location",IsMapTravel=map})};Handle(r,100);return r;}
 static InteractionRequest Ack(string epoch,double time,bool success=true,string acct=null,string scene="source",string kind=PreparedKind){var r=new InteractionRequest{PlayerId=player,AccountId=acct??account,RequestId=Guid.NewGuid().ToString("N"),Kind=kind,TargetId=epoch,Data=JsonSerializer.SerializeToElement(new GroupTravelAcknowledgement{Success=success,Scene=scene})};Handle(r,time);return r;}
 static string Epoch=>operations[player].Barrier.Epoch;static string Last=>emitted.Count==0?"":emitted[emitted.Count-1].Phase;
 static void InitialDenied(string reason){Start();Check(Last=="denied"&&operations.Count==0,reason);}
 static void ContextReject(Action change,string reason){Reset();Start();string epoch=Epoch;change();Ack(epoch,110);Check(Last=="cancel"&&operations.Count==0,reason);}
 public static int RunDepartureTests(){
  Reset();Start();Check(Last=="prepare"&&operations.Count==1,"valid terminal creates one private preparation");var held=operations[player];string epoch=Epoch;
  Check(held.Departure.PendingGeneration==2&&held.Departure.AccountId==account&&held.Departure.Position.x==1,"lease binds validated account/origin/pending generation");
  Ack(epoch,114);Check(Last=="depart"&&held.Barrier.Phase=="arrive","14second queued preparation survives blocked pose stream");
  int count=emitted.Count;Ack(epoch,114.5);Check(emitted.Count==count,"duplicate prepared ACK cannot depart twice");Ack(epoch,120,true,null,"target",ArrivedKind);Check(Last=="complete"&&operations.Count==0,"arrival ends lease and commits only native assigned destination");
  Reset();Start();epoch=Epoch;pose.X=999;pose.Scene="foreign";pose.AccountId="mutated";pose.Active=false;Ack(epoch,114);Check(Last=="depart","lease is scalar immutable data, not mutable pose wrapper");
  Reset();Start();epoch=Epoch;DedicatedWorldRuntime.Scene="other-player-map";Ack(epoch,110);Check(Last=="depart","worker residency changes do not change assigned source authority");
  Reset();Start(true);epoch=Epoch;DedicatedWorldRuntime.Scene="target";Ack(epoch,110);Check(Last=="depart","public map revalidation uses captured source when worker focuses another player's destination");
  Reset();Start();epoch=Epoch;Ack(epoch,115);Check(Last=="depart","existing15second barrier boundary preserved");
  Reset();Start();epoch=Epoch;Ack(epoch,115.001);Check(Last=="cancel"&&operations.Count==0,"expired preparation lease canceled");
  Reset();Start();epoch=Epoch;Ack(epoch,99);Check(Last=="cancel","backwards authority time rejected");
  Reset();Start();epoch=Epoch;count=emitted.Count;Ack(Guid.NewGuid().ToString("N"),110);Check(emitted.Count==count&&operations.Count==1,"wrong epoch cannot consume lease");
  Ack(epoch,110,true,"33333333333333333333333333333333");Check(emitted.Count==count&&operations.Count==1,"wrong account cannot consume lease");
  Ack(epoch,110,true,null,"foreign");Check(emitted.Count==count&&operations.Count==1,"wrong source ACK cannot consume lease");
  Ack(epoch,110,false);Check(Last=="cancel"&&operations.Count==0,"client preflight rejection invalidates operation");count=emitted.Count;Ack(epoch,111);Check(emitted.Count==count,"late success after cancellation ignored");
  UpdatePlayerPose(new PlayerPose{PlayerId=player,AccountId=account,Scene="source",X=1},100);Start();string replacement=Epoch;Check(replacement!=epoch,"new request receives new epoch");count=emitted.Count;Ack(epoch,110);Check(emitted.Count==count&&Epoch==replacement,"old ACK cannot consume replacement operation");
  Reset();Start();epoch=Epoch;SetPlayerPresent(player,false);count=emitted.Count;Ack(epoch,110);Check(operations.Count==0&&emitted.Count==count,"disconnect removes departure lease and rejects late ACK");
  ContextReject(()=>PlayerMapRuntime.Context=null,"missing current assignment rejected");
  ContextReject(()=>PlayerMapRuntime.Context.TravelPending=false,"canceled assignment rejected");
  ContextReject(()=>PlayerMapRuntime.Context.Generation++,"new assignment generation rejected");
  ContextReject(()=>PlayerMapRuntime.Context.Epoch=Guid.NewGuid().ToString("N"),"replacement pending epoch rejected");
  ContextReject(()=>PlayerMapRuntime.Context.Scene="foreign","different source scene rejected");
  ContextReject(()=>PlayerMapRuntime.Context.PropertyId="apartment","different source property rejected");
  ContextReject(()=>PlayerMapRuntime.Context.PlayerId=Guid.NewGuid().ToString("N"),"different assigned player rejected");
  ContextReject(()=>TravelUnlockManager.Instance.Unlocked=false,"destination unlock revalidated");
  ContextReject(()=>TravelUnlockManager.Instance._allPortalKeys=Array.Empty<PortalKey>(),"removed canonical route rejected");
  ContextReject(()=>operations[player].SourcePosition=new Vector3(9.01f,0,0),"captured origin must remain within unchanged8m terminal bound");
  Reset();Start(true);epoch=Epoch;MultiplayerSandboxRuntime.Public=false;Ack(epoch,110);Check(Last=="cancel","public map rights revalidated");
  Reset(1,94.999);InitialDenied("ordinary stale pose still rejected after5seconds");
  Reset(1,100.01);InitialDenied("future ordinary pose rejected");
  Reset();pose.Active=false;InitialDenied("inactive ordinary pose rejected");
  Reset();pose.Scene="foreign";InitialDenied("ordinary source scene mismatch rejected");
  Reset();pose.PlayerId=Guid.NewGuid().ToString("N");InitialDenied("pose player must match authenticated request");
  Reset();pose.AccountId=Guid.NewGuid().ToString("N");InitialDenied("pose account must match authenticated request");
  Reset();PlayerMapRuntime.Context.PropertyId="apartment";InitialDenied("ordinary pose property must match assignment");
  Reset();PlayerMapRuntime.Context.TravelPending=true;InitialDenied("new request cannot borrow pending assignment");
  Reset(8.001f);InitialDenied("ordinary terminal proximity unchanged beyond8m");
  Reset(8,95);Start();Check(Last=="prepare","existing5second freshness and8m boundaries accepted");
  Reset();Start();held=operations[player];operations[player]=new PlayerOperation{Barrier=held.Barrier,Event=held.Event,SourcePosition=held.SourcePosition,Departure=held.Departure};bool rejected=false;try{RevalidateOperation(held);}catch(InvalidOperationException){rejected=true;}Check(rejected,"detached operation object cannot reuse replacement registry entry");
  return assertions;
 }
}
}
