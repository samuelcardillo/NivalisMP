using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Nivalis;
using Nivalis.UI;
using UnityEngine;
using Object=UnityEngine.Object;

namespace NivalisTogether.Dedicated
{
    /// <summary>Explicit party bed intent and the native end-of-day choice flow.
    /// Curfew alone never starts sleep. Native time, business callbacks and saves
    /// run through the game's bed/sleep coroutines. Informational reports also
    /// finish on an empty server without authorizing sleep or financial choices.</summary>
    public static class SleepReplication
    {
        public const string EventKind="group-sleep";
        public const string RequestKind="group-sleep-request";
        private const string ActionKind="group-sleep-action",CancelKind="group-sleep-cancel";
        private static bool configured,enabled,worker,terminal,patched,authorizedSleep,popupOwned,choicePending,waitingForPlayers,awaitingWakeSynchronization;
        private static string localPlayer="",epoch="",owner="",bedId="",scene="",stageSignature="",displayed="",popupTitle="",ownerWaitDiagnostic="",preferredSleeper="";
        private static long sequence,step,lastSequence,popupGeneration;
        private static double now,nextRequest,nextCapture,stageSince,transitionDeadline,nextConsensus;
        private static GroupSleepEvent current,replica;
        private static Action<InteractionRequest> send;
        private static Action<WorldEvent> emit;
        private static Action<string> notice,fail;
        private static readonly HashSet<string> present=new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> recoveryPresent=new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string,PoseSample> poses=new Dictionary<string,PoseSample>(StringComparer.Ordinal);
        private static readonly Dictionary<string,GroupSleepEvent> results=new Dictionary<string,GroupSleepEvent>(StringComparer.Ordinal);
        private static readonly Queue<string> resultOrder=new Queue<string>();
        private static readonly List<Il2CppSystem.Action> callbacks=new List<Il2CppSystem.Action>();
        private static readonly SleepConsensus consensus=new SleepConsensus();
        private static readonly Dictionary<string,ReadyBed> readyBeds=new Dictionary<string,ReadyBed>(StringComparer.Ordinal);
        private static string[] sleepCohort=Array.Empty<string>();
        private sealed class PoseSample {internal PlayerPose Pose;internal double Time;}
        private sealed class ReadyBed {internal string BedId;internal SleepBedReadiness Consent;}
        private static bool Authority=>configured&&DedicatedWorldRuntime.IsIsolated&&worker==DedicatedWorldRuntime.IsDedicatedServer;
        public static bool IsBusy=>enabled&&!terminal&&(worker?(!waitingForPlayers&&epoch.Length>0)||NativeSleepFlowSupport.IsRunning||DayTransitionController.Transitioning:replica!=null&&replica.Phase!="complete"&&replica.Phase!="waiting"&&replica.Phase!="cancelled");
        public static GroupSleepEvent CurrentState=>worker?current:replica;
        public static bool AwaitingWakeSynchronization=>worker&&awaitingWakeSynchronization;
        public static bool IsAwaitingWakeRelease=>AwaitingWakeSynchronization;
        public static string[] WakeParticipants=>(string[])sleepCohort.Clone();
        public static bool IsLocallyReady=>!worker&&replica!=null&&replica.Phase=="waiting"&&Array.IndexOf(replica.ReadyPlayers,localPlayer)>=0;
        // The session owns native focus scheduling. This request never changes
        // player map membership and stays null while any native route is active.
        public static PlayerMapLocation GetPendingSleepFocus(double timestamp)
        {
            if(!double.IsFinite(timestamp))throw new ArgumentException("Invalid sleep focus clock.");
            // Receive can refresh poses before Tick. Use the same session clock
            // supplied by the caller, not the previous sleep tick's timestamp.
            now=timestamp;
            if(!Authority||!worker||!enabled||terminal||!DedicatedWorldRuntime.IsWorldReady||!waitingForPlayers||!consensus.CanStart||MapOperationsPending||DialogueReplication.IsConversationActive)return null;
            string[] candidates=OrderedReadyPlayers();
            foreach(string id in candidates){
                if(!TryValidateReadyBed(id,out var ready))return null;
                if(ready.Consent.Scene==DedicatedWorldRuntime.Scene&&ready.Consent.PropertyId==WorkerSceneResidency.CurrentProperty)return null;
            }
            foreach(string id in candidates){
                var location=PlayerMapRuntime.GetLocation(id);var consent=readyBeds[id].Consent;
                if(location!=null&&location.HasPosition&&location.Scene==consent.Scene&&location.PropertyId==consent.PropertyId)return location;
            }
            return null;
        }

        public static void Configure(bool isWorker,string playerId,Action<InteractionRequest> sendIntent,Action<WorldEvent> emitEvent,Action<string> showNotice,Action<string> failSession)
        {
            if(!Guid.TryParseExact(playerId,"N",out _))throw new ArgumentException("Sleep authority requires an authenticated player.");
            Stop();configured=true;terminal=false;worker=isWorker;localPlayer=playerId;send=sendIntent;emit=emitEvent;notice=showNotice;fail=failSession;
            enabled=false;epoch=owner=bedId=scene=stageSignature=displayed=preferredSleeper="";sequence=step=lastSequence=0;now=nextRequest=nextCapture=stageSince=transitionDeadline=0;
            present.Clear();recoveryPresent.Clear();poses.Clear();results.Clear();resultOrder.Clear();current=replica=null;authorizedSleep=choicePending=false;ownerWaitDiagnostic="";
            consensus.Reset();readyBeds.Clear();sleepCohort=Array.Empty<string>();waitingForPlayers=awaitingWakeSynchronization=false;nextConsensus=0;
            InstallPatches();
        }
        public static void SetEnabled(bool value)
        {
            if(value&&(!Authority||!DedicatedWorldRuntime.IsWorldReady))throw new InvalidOperationException("Sleep authority requires a loaded isolated world.");
            enabled=value;
        }
        public static void Stop()
        {
            enabled=false;terminal=true;authorizedSleep=choicePending=false;epoch=owner=preferredSleeper="";current=replica=null;
            present.Clear();recoveryPresent.Clear();poses.Clear();NativeSleepFlowSupport.EndTracking();HidePopup();
            consensus.Reset();readyBeds.Clear();sleepCohort=Array.Empty<string>();waitingForPlayers=awaitingWakeSynchronization=false;SleepLoadingOverlay.Hide();
            send=null;emit=null;notice=null;fail=null;
        }
        public static void SetPlayerPresent(string id,bool joined)
        {
            if(!worker||!Guid.TryParseExact(id,"N",out _))return;
            consensus.SetLoaded(id,joined);
            if(joined){if(present.Count>=32&&!present.Contains(id))throw new InvalidOperationException("Too many sleep participants.");present.Add(id);nextCapture=0;}
            else{present.Remove(id);poses.Remove(id);readyBeds.Remove(id);if(owner==id)owner="";nextCapture=0;}
        }
        public static void SetParticipantConnected(string id,bool joined)
        {
            if(!worker||!Guid.TryParseExact(id,"N",out _))return;
            consensus.SetConnected(id,joined);
            if(!joined){readyBeds.Remove(id);present.Remove(id);poses.Remove(id);recoveryPresent.Remove(id);if(owner==id)owner="";
                var remaining=new List<string>(sleepCohort);remaining.Remove(id);sleepCohort=remaining.ToArray();}
            nextCapture=nextConsensus=0;
        }
        public static void CancelLocalReadiness()
        {
            if(!IsLocallyReady)throw new InvalidOperationException("You are not waiting to sleep.");
            Send(CancelKind,replica.Epoch,new GroupSleepIntent{Epoch=replica.Epoch,Step=replica.Step,Action="cancel"});
        }
        public static void UpdatePlayerPose(PlayerPose pose,double timestamp)
        {
            if(worker&&pose!=null&&present.Contains(pose.PlayerId)&&DedicatedProtocol.PoseValid(pose)&&double.IsFinite(timestamp))
                poses[pose.PlayerId]=new PoseSample{Pose=pose,Time=timestamp};
        }
        public static void SetRecoveryPlayerPresent(string id,bool joined)
        {
            if(!worker||!Guid.TryParseExact(id,"N",out _))return;
            if(joined){if(recoveryPresent.Count>=32&&!recoveryPresent.Contains(id))throw new InvalidOperationException("Too many pending sleep participants.");recoveryPresent.Add(id);}
            else{recoveryPresent.Remove(id);if(owner==id&&!present.Contains(id))owner="";}
            nextCapture=0;
        }
        public static bool? RouteClientInteraction(IInteractable focus)
        {
            if(!Authority||worker)return null;
            var bed=focus?.TryCast<Bed>();if(bed==null)return null;
            if(!enabled||terminal)return false;
            try{
                if(IsBusy||TravelReplication.IsBusy)throw new InvalidOperationException("Finish the current party transition first.");
                if(!bed.IsActive||!bed.CanInteract())throw new InvalidOperationException("Put down the held object before sleeping.");
                string target=GetBedId(bed);
                ShowPopup("Sleep until morning?","Every connected player must choose sleep at a bed. You can cancel while waiting for the others. Morning appears after everyone's world has finished updating.",
                    new[]{"Sleep together","Cancel"},new Action[]{()=>Send(RequestKind,target,new GroupSleepIntent()),()=>{}});
            }
            catch(Exception ex){notice?.Invoke(ex.Message);}
            return false;
        }
        private static void Send(string kind,string target,GroupSleepIntent intent,bool recovery=false)
        {
            if(!configured||(!enabled&&!recovery)||terminal||send==null)throw new InvalidOperationException("The shared world connection is not ready.");
            if(now<nextRequest)throw new InvalidOperationException("Wait a moment before sending another shared sleep choice.");nextRequest=now+.25;
            send(new InteractionRequest {Kind=kind,PlayerId=localPlayer,TargetId=target,RequestId=Guid.NewGuid().ToString("N"),Data=JsonSerializer.SerializeToElement(intent,DedicatedProtocol.Json)});
        }
        public static bool Handles(InteractionRequest request)=>request!=null&&(request.Kind==RequestKind||request.Kind==ActionKind||request.Kind==CancelKind);
        public static bool CanContinueDuringShutdown(InteractionRequest request)=>SleepRecoveryProtocol.AllowsShutdownContinuation(current,request);
        public static void Handle(InteractionRequest request,double timestamp)
        {
            if(!Authority||!worker||!enabled||terminal||!Handles(request))throw new InvalidOperationException("Shared sleep must execute on the loaded worker.");
            now=timestamp;
            if(!double.IsFinite(now)||!Guid.TryParseExact(request.PlayerId,"N",out _)||!DedicatedProtocol.Text(request.RequestId,64)||!DedicatedProtocol.Text(request.TargetId,128))throw new InvalidDataException("Invalid sleep request.");
            string key=request.PlayerId+":"+request.RequestId;
            if(results.TryGetValue(key,out var previous)){Publish(previous);return;}
            bool nativeActionStarted=false;
            try{
                var intent=request.Data.Deserialize<GroupSleepIntent>(DedicatedProtocol.Json)??throw new InvalidDataException("Invalid sleep intent.");
                ValidateIntent(intent);
                if(request.Kind==RequestKind){
                    RequireReady();
                    var actor=RequireActor(request.PlayerId);
                    if(actor.Pose.AccountId!=request.AccountId)throw new InvalidOperationException("The sleep request account binding changed.");
                    if((epoch.Length>0&&!waitingForPlayers)||NativeSleepFlowSupport.IsRunning||DayTransitionController.Transitioning)throw new InvalidOperationException("Finish the current shared day transition first.");
                    if(DialogueReplication.IsConversationActive)throw new InvalidOperationException("Finish the shared conversation before sleeping.");
                    var bed=FindBed(request.TargetId);RequireBed(bed,actor.Pose);
                    int hour=TimeOfDayManager.ClockHour;if(hour>=8&&hour<18)throw new InvalidOperationException("The game allows sleep from 18:00 until 08:00.");
                    if(!waitingForPlayers){epoch=Guid.NewGuid().ToString("N");owner=request.PlayerId;scene=DedicatedWorldRuntime.Scene;step=0;stageSignature="";waitingForPlayers=true;}
                    var context=PlayerMapRuntime.GetContext(request.PlayerId);var point=bed.transform.position;
                    consensus.SetReady(request.PlayerId,true);readyBeds[request.PlayerId]=new ReadyBed{BedId=request.TargetId,
                        Consent=new SleepBedReadiness{AccountId=request.AccountId,Scene=context.Scene,PropertyId=context.PropertyId,Generation=context.Generation,X=point.x,Y=point.y,Z=point.z}};
                    preferredSleeper=request.PlayerId;
                    Remember(key,Response("accepted",request,"Ready to sleep. Waiting for every connected player."));
                }
                else if(request.Kind==CancelKind){
                    if(!waitingForPlayers||intent.Action!="cancel"||intent.Epoch!=epoch||request.TargetId!=epoch||!consensus.IsReady(request.PlayerId))
                        throw new InvalidOperationException("That sleep request is no longer waiting for players.");
                    consensus.SetReady(request.PlayerId,false);readyBeds.Remove(request.PlayerId);
                    Remember(key,Response("accepted",request,"You cancelled sleep readiness."));
                }
                else{
                    if(!DedicatedWorldRuntime.IsWorldReady||WorkerSceneResidency.IsBusy)throw new InvalidOperationException("Wait for the native shared day context to finish loading.");
                    ChooseOwner();if(owner!=request.PlayerId)throw new InvalidOperationException("Another player is handling the shared day transition.");
                    ValidateChoice(current,intent,request.PlayerId,request.TargetId);
                    if(current.RecoveryOwner){
                        if(!recoveryPresent.Contains(request.PlayerId)||!SleepRecoveryProtocol.IsChoicePhase(current.Phase))throw new InvalidOperationException("This player no longer has a pending recovery choice.");
                    }else RequireActor(request.PlayerId);
                    if(choicePending)throw new InvalidOperationException("The native day transition is still completing the previous choice.");
                    var stage=NativeSleepFlowSupport.Capture();
                    if(current==null||StageSignature(stage)!=stageSignature)throw new InvalidOperationException("The day summary changed. Wait for its current choices.");
                    nativeActionStarted=choicePending=true;
                    if(intent.Action=="next"&&stage.Phase=="summary")NativeSleepFlowSupport.InvokeNext();
                    else if(intent.Action=="ranking"&&stage.Phase=="ranking")NativeSleepFlowSupport.InvokeRankingContinue();
                    else if(intent.Action=="debt"&&stage.Phase=="debt")NativeSleepFlowSupport.InvokeDebtChoice(intent.Percentage);
                    else throw new InvalidOperationException("That day transition choice is no longer available.");
                    transitionDeadline=now+120;
                    Remember(key,Response("accepted",request,"Shared day choice accepted."));
                }
                nextCapture=nextConsensus=0;Tick(now);
            }
            catch(Exception ex){
                Remember(key,Response("denied",request,ex.Message));
                if(nativeActionStarted)Fail("Native day transition failed after an accepted choice: "+Cut(ex.Message,240));
            }
        }
        private static PoseSample RequireActor(string id)
        {
            var context=PlayerMapRuntime.GetContext(id);
            if(!present.Contains(id)||!poses.TryGetValue(id,out var pose)||!pose.Pose.Active||now-pose.Time<0||now-pose.Time>3||
                context==null||context.TravelPending||PlayerMapRuntime.IsPlayerPending(id)||TravelReplication.IsPlayerBusy(id)||ApartmentReplication.IsPlayerBusy(id)||
                pose.Pose.ContextGeneration!=context.Generation||pose.Pose.Scene!=context.Scene||pose.Pose.PropertyId!=context.PropertyId)
                throw new InvalidOperationException("Your current map position is not available. Finish loading or traveling before choosing sleep.");
            return pose;
        }
        private static bool MapOperationsPending=>TravelReplication.HasPendingOperations||ApartmentReplication.HasPendingOperations||PlayerMapRuntime.HasPendingRestores||WorkerSceneResidency.IsBusy;
        private static void RequireReady()
        {
            if(!DedicatedWorldRuntime.IsWorldReady||MapOperationsPending)throw new InvalidOperationException("Wait for every player's map to finish loading or traveling.");
        }
        private static void RequireBed(Bed bed,PlayerPose actor)
        {
            if(bed==null||!bed.isActiveAndEnabled||!bed.gameObject.scene.isLoaded||bed.gameObject.scene.name!=actor.Scene||
                (bed.transform.position-new Vector3(actor.X,actor.Y,actor.Z)).sqrMagnitude>16)
                throw new InvalidOperationException("Stand within four metres of a bed in the shared loaded district.");
            PropertyOwnershipReplication.RequireTargetInteraction(actor.AccountId,bed);
            if(!bed.IsActive||!bed.CanInteract()||bed.IsHeld||bed.IsBeingPlaced)throw new InvalidOperationException("That bed is not available.");
            var apartment=bed.Furniture?.CurrentProperty?.TryCast<Nivalis.Apartment.Apartment>();
            if(apartment==null||(!apartment.PlayerOwned&&!apartment.Shelter))throw new InvalidOperationException("Use a bed in a shared owned or rented apartment, or a shelter.");
            if(apartment.Guid!=actor.PropertyId)throw new InvalidOperationException("The bed is outside this player's assigned apartment.");
            var state=Nivalis.Apartment.ApartmentManager.Instance.GetState(apartment);
            if(state?.controller==null||state.controller.gameObject.scene.name!=actor.Scene||!state.controller.gameObject.scene.isLoaded)
                throw new InvalidOperationException("This sleeping apartment is not loaded with the party. Travel there together first.");
            // Native SleepSequence wakes through LocalPlayer.CurrentProperty;
            // TryToSleep only changes lastSleepApartment. A remote bed must not
            // begin irreversible daily work with a different worker property.
            var workerProperty=PlayerManager.InstanceNotNull()?PlayerManager.Instance.LocalPlayer?.CurrentProperty:null;
            if(workerProperty==null||workerProperty.Pointer!=apartment.Pointer)
                throw new InvalidOperationException("The shared worker has not entered this sleeping apartment. Shared apartment entry is required before sleeping here.");
            if(NativeSleepFlowSupport.IsRunning)throw new InvalidOperationException("The worker is already completing a native sleep action.");
        }
        private static Bed FindBed(string id)
        {
            Bed found=null;var beds=Object.FindObjectsOfType<Bed>(true);if(beds.Length>2048)throw new InvalidOperationException("Too many loaded beds.");
            foreach(var bed in beds)if(bed!=null&&bed.gameObject.scene.isLoaded&&bed.gameObject.scene.name==DedicatedWorldRuntime.Scene&&GetBedId(bed)==id){
                if(found!=null)throw new InvalidOperationException("The bed identity is ambiguous.");found=bed;
            }
            return found;
        }
        internal static string DiagnosticBedId(Bed bed) => GetBedId(bed);
        private static string GetBedId(Bed bed)
        {
            var saved=bed.GetComponentInParent<SerializableObject>();string id=saved?.GetGuid()??"";var anchor=id.Length==0?null:saved.transform;
            var path=new List<string>();for(var at=bed.transform;at!=null&&at!=anchor;at=at.parent)path.Add(at.name+"#"+at.GetSiblingIndex());path.Reverse();
            int ordinal=0;foreach(var other in bed.GetComponents<Bed>()){if(other.Pointer==bed.Pointer)break;ordinal++;}
            return "bed:"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bed.gameObject.scene.path+"|"+id+"|"+string.Join("/",path)+"|"+ordinal)));
        }
        public static void Tick(double timestamp)
        {
            if(!enabled||terminal||!Authority||!DedicatedWorldRuntime.IsWorldReady)return;
            now=timestamp;if(!double.IsFinite(now))throw new ArgumentException("Invalid sleep clock.");
            if(!worker){ShowReplica();return;}
            if(awaitingWakeSynchronization)return;
            if(waitingForPlayers){TickWaiting();return;}
            if(epoch.Length==0&&MapOperationsPending)return;
            var stage=NativeSleepFlowSupport.Capture();
            if(epoch.Length==0){
                // A normal curfew summary may precede any bed request. Connected
                // players acknowledge it; an empty server can finish informational
                // reports without authorizing sleep or making a financial choice.
                if(stage.Phase!="summary"&&stage.Phase!="debt"&&stage.Phase!="ranking")return;
                epoch=Guid.NewGuid().ToString("N");scene=DedicatedWorldRuntime.Scene;step=0;stageSignature="";authorizedSleep=false;
            }
            if(scene!=DedicatedWorldRuntime.Scene){Fail("Native sleep changed district outside the party travel barrier.");return;}
            ChooseOwner();
            bool automaticReport=SleepReportContinuation.CanContinue(stage.Phase,authorizedSleep,consensus.ParticipantCount,present.Count,recoveryPresent.Count);
            if(owner.Length==0&&!automaticReport&&(stage.Phase=="summary"||stage.Phase=="debt"||stage.Phase=="ranking")){
                string missing=epoch+":"+stage.Phase;
                if(ownerWaitDiagnostic!=missing){ownerWaitDiagnostic=missing;notice?.Invoke("Shared day requires an explicit "+stage.Phase+" choice. A loaded player or authenticated reconnecting player can answer; the native checkpoint waits until the day transition completes.");}
            }else ownerWaitDiagnostic="";
            bool done=authorizedSleep?NativeSleepFlowSupport.CompletionObserved:!stage.Busy&&stage.Phase!="summary"&&stage.Phase!="debt"&&stage.Phase!="ranking";
            if(done){
                if(authorizedSleep){
                    awaitingWakeSynchronization=true;
                    current=new GroupSleepEvent {Epoch=epoch,Step=++step,Phase="synchronizing",Owner=owner,Scene=scene,Title="Loading morning",Text="The night has passed. Updating every player's world..."};
                    Publish(current);return;
                }
                CompleteSleep("Day summary complete. Curfew remains active until everyone chooses to sleep.");return;
            }
            string signature=StageSignature(stage);
            if(signature!=stageSignature){stageSignature=signature;stageSince=now;step++;choicePending=false;nextCapture=0;transitionDeadline=now+120;}
            if(now>=nextCapture){
                nextCapture=now+2;
                var choices=new List<GroupSleepChoice>();
                if(stage.Phase=="summary")choices.Add(new GroupSleepChoice{Action="next",Label="Continue"});
                else if(stage.Phase=="ranking")choices.Add(new GroupSleepChoice{Action="ranking",Label="Continue"});
                else if(stage.Phase=="debt")foreach(float percentage in stage.Percentages)choices.Add(new GroupSleepChoice{Action="debt",Percentage=percentage,Label=percentage==0?"Pay nothing now":"Pay "+percentage.ToString("0.##")+"%"});
                current=new GroupSleepEvent {Epoch=epoch,Step=step,Phase=stage.Phase,Owner=owner,RecoveryOwner=recoveryPresent.Contains(owner),Scene=scene,Title=stage.Phase=="debt"?"Shared debt payment":"Shared day transition",
                    Text=StageText(stage),Choices=choices.ToArray()};Publish(current);
            }
            if(authorizedSleep&&(stage.Phase=="newspaper"||stage.Phase=="supply")&&now-stageSince>=1)
                NativeSleepFlowSupport.TickAuthorizedSleep(true);
            // Unanimous bed consent also accepts informational daily reports.
            // With no connected/pending players, a natural report must not hold
            // native saving and shutdown forever waiting for an absent owner.
            // A financial choice still requires an explicit owner action.
            if(automaticReport&&!choicePending&&now-stageSince>=1){
                choicePending=true;transitionDeadline=now+120;
                if(!authorizedSleep)notice?.Invoke("Completing unattended native "+stage.Phase+" report; no sleep requested.");
                try{if(stage.Phase=="summary")NativeSleepFlowSupport.InvokeNext();else NativeSleepFlowSupport.InvokeRankingContinue();}
                catch(Exception ex){Fail("The native day report could not continue: "+Cut(ex.Message,240));return;}
            }
            if(authorizedSleep&&stage.Phase!="summary"&&stage.Phase!="debt"&&stage.Phase!="ranking"&&now>transitionDeadline)
                Fail("The native sleep sequence did not finish. The world has been stopped before another checkpoint.");
            if(choicePending&&now>transitionDeadline)Fail("The native day choice did not advance. The world has been stopped before another checkpoint.");
        }
        private static void TickWaiting()
        {
            if(now<nextConsensus&&now<nextCapture)return;nextConsensus=now+.25;
            if(NativeSleepFlowSupport.IsRunning||DayTransitionController.Transitioning){CancelWaiting("Sleep cancelled because the shared world started another day transition.");return;}
            int hour=TimeOfDayManager.ClockHour;
            if(hour>=8&&hour<18){CancelWaiting("Sleep cancelled because it is already daytime.");return;}
            foreach(string id in consensus.ReadyPlayers){
                if(!TryValidateReadyBed(id,out _)){consensus.SetReady(id,false);readyBeds.Remove(id);nextCapture=0;}
            }
            if(consensus.ReadyCount==0||consensus.ParticipantCount==0){CancelWaiting("No players are waiting to sleep.");return;}
            if(consensus.CanStart&&TryBeginUnanimousSleep())return;
            if(consensus.ReadyCount==0){CancelWaiting("The selected sleeping bed is no longer available.");return;}
            if(now>=nextCapture){
                nextCapture=now+2;
                current=new GroupSleepEvent {Epoch=epoch,Step=++step,Phase="waiting",Owner=owner,Scene=scene,Title="Waiting for everyone",Text=consensus.CanStart?
                    "Everyone is ready. Preparing the shared sleep location...":consensus.ReadyCount+" of "+consensus.ParticipantCount+" players ready to sleep. Every connected player must choose sleep at a bed."};
                Publish(current);
            }
        }
        private static bool TryValidateReadyBed(string id,out ReadyBed ready)
        {
            ready=null;
            if(!present.Contains(id)||!readyBeds.TryGetValue(id,out ready)||!poses.TryGetValue(id,out var pose))return false;
            var context=PlayerMapRuntime.GetContext(id);
            return ready.Consent.Matches(pose.Pose,context,now-pose.Time,
                PlayerMapRuntime.IsPlayerPending(id)||TravelReplication.IsPlayerBusy(id)||ApartmentReplication.IsPlayerBusy(id));
        }
        private static string[] OrderedReadyPlayers()
        {
            var result=new List<string>(consensus.ReadyPlayers);
            if(result.Remove(preferredSleeper))result.Insert(0,preferredSleeper);
            return result.ToArray();
        }
        private static bool TryBeginUnanimousSleep()
        {
            // No mutation can intervene on Unity's main thread between this full
            // membership check and the native bed call. Late joins wait for dawn.
            if(!consensus.CanStart)throw new InvalidOperationException("Every connected player must be ready to sleep.");
            if(MapOperationsPending||DialogueReplication.IsConversationActive)return false;
            Bed bed=null;string selected="";
            foreach(string id in OrderedReadyPlayers()){
                var ready=readyBeds[id];
                if(ready.Consent.Scene!=DedicatedWorldRuntime.Scene||ready.Consent.PropertyId!=WorkerSceneResidency.CurrentProperty)continue;
                try{var actor=RequireActor(id);bed=FindBed(ready.BedId);RequireBed(bed,actor.Pose);selected=id;break;}
                catch(InvalidOperationException){consensus.SetReady(id,false);readyBeds.Remove(id);nextCapture=0;return false;}
            }
            if(selected.Length==0)return false; // Root will service GetPendingSleepFocus.
            sleepCohort=consensus.Participants;owner=selected;bedId=readyBeds[owner].BedId;scene=DedicatedWorldRuntime.Scene;
            waitingForPlayers=false;authorizedSleep=true;transitionDeadline=now+120;stageSignature="";
            current=new GroupSleepEvent {Epoch=epoch,Step=++step,Phase="sleeping",Owner=owner,Scene=scene,Title="Sleeping until morning",Text="Everyone is ready. The shared world is sleeping..."};Publish(current);
            try{NativeSleepFlowSupport.BeginTracking();bed.DoInteraction();nextCapture=0;}
            catch(Exception ex){
                if(NativeSleepFlowSupport.IsRunning||NativeSleepFlowSupport.HasObservedSleep)Fail("Native sleep stopped unexpectedly: "+Cut(ex.Message,240));
                else{CancelWaiting("Sleep could not begin: "+Cut(ex.Message,240));NativeSleepFlowSupport.EndTracking();}
            }
            return true;
        }
        private static void CancelWaiting(string text)
        {
            consensus.ClearReadiness();readyBeds.Clear();sleepCohort=Array.Empty<string>();authorizedSleep=false;
            if(epoch.Length>0){current=new GroupSleepEvent {Epoch=epoch,Step=++step,Phase="cancelled",Scene=scene,Title="Shared sleep",Text=text};Publish(current);}
            epoch=owner=bedId=stageSignature=preferredSleeper="";waitingForPlayers=authorizedSleep=choicePending=awaitingWakeSynchronization=false;
        }
        public static void CancelPendingSleepFocus(string reason)
        {
            // Only a managed waiting round may be cancelled after a rejected
            // focus preflight. A native night already in progress stays guarded.
            if(!Authority||!worker||!enabled||terminal||!waitingForPlayers||authorizedSleep||awaitingWakeSynchronization)return;
            CancelWaiting("Sleep cancelled: "+(string.IsNullOrWhiteSpace(reason)?"the sleeping location could not be prepared.":Cut(reason,240)));
        }
        public static void ReleaseCompletedSleep()
        {
            if(!Authority||!worker||!enabled||terminal||!awaitingWakeSynchronization||!NativeSleepFlowSupport.CompletionObserved)
                throw new InvalidOperationException("The native morning and every client must finish synchronization before release.");
            CompleteSleep("Everyone's world is ready. Good morning!");
        }
        private static void CompleteSleep(string text)
        {
            current=new GroupSleepEvent {Epoch=epoch,Step=++step,Phase="complete",Owner=owner,RecoveryOwner=recoveryPresent.Contains(owner),Scene=scene,Title="Shared day",Text=text};Publish(current);
            epoch=owner=bedId=stageSignature=preferredSleeper="";authorizedSleep=choicePending=waitingForPlayers=awaitingWakeSynchronization=false;
            consensus.ClearReadiness();readyBeds.Clear();sleepCohort=Array.Empty<string>();NativeSleepFlowSupport.EndTracking();
        }
        private static string StageSignature(NativeSleepStage stage)=>stage.Phase+"|"+stage.Money+"|"+stage.Debt+"|"+string.Join(",",stage.Percentages)+"|"+stage.Text;
        private static string StageText(NativeSleepStage stage)
        {
            if(!string.IsNullOrWhiteSpace(stage.Text))return Cut(stage.Text,3000);
            switch(stage.Phase){
                case "newspaper":return "Morning newspaper. The shared sleep sequence will continue shortly.";
                case "supply":return "Reviewing today's supply trends. The shared sleep sequence will continue shortly.";
                case "sleeping":return "The shared world is sleeping until morning.";
                case "transition":return "The game is completing the shared day transition.";
                case "debt":return "Choose a debt payment from the shared wallet.";
                case "summary":return "Continue after reviewing the shared daily summary.";
                case "ranking":return "Continue after reviewing the shared daily rankings.";
                default:return "Waiting for the shared day transition.";
            }
        }
        private static void ChooseOwner()
        {
            string previous=owner;var available=new List<string>();
            foreach(string id in present)if(poses.TryGetValue(id,out var pose)&&pose.Pose.Active&&now-pose.Time<=(id==previous?5:3))available.Add(id);
            // Pending authenticated peers may answer an existing native choice;
            // they never gain a player body or permission to start a bed request.
            owner=SleepRecoveryProtocol.SelectOwner(previous,available,recoveryPresent);
            if(owner!=previous)nextCapture=0;
        }
        private static void Fail(string message){terminal=true;fail?.Invoke(message);}
        private static GroupSleepEvent Response(string phase,InteractionRequest request,string text)=>new GroupSleepEvent {Epoch=epoch,Step=step,Phase=phase,Owner=owner,RecoveryOwner=recoveryPresent.Contains(owner),TargetPlayer=request.PlayerId,RequestId=request.RequestId,Scene=DedicatedWorldRuntime.Scene,Title="Shared sleep",Text=Cut(text,2000)};
        private static void Remember(string key,GroupSleepEvent value)
        {results[key]=value;resultOrder.Enqueue(key);while(resultOrder.Count>1024)results.Remove(resultOrder.Dequeue());Publish(value);}
        private static void Publish(GroupSleepEvent value)
        {
            value.Participants=authorizedSleep?sleepCohort:consensus.Participants;
            value.ReadyPlayers=authorizedSleep?sleepCohort:consensus.ReadyPlayers;
            value.NightTransition=authorizedSleep;
            value.Sequence=++sequence;value.GameSeconds=TimeOfDayManager.TotalGameSeconds;value.Curfew=CurfewManager.IsCurfewInEffect;ValidateWire(value);
            var evt=new WorldEvent {Kind=EventKind,Data=JsonSerializer.SerializeToElement(value,DedicatedProtocol.Json)};
            if(JsonSerializer.SerializeToUtf8Bytes(evt,DedicatedProtocol.Json).Length>24000)throw new InvalidDataException("Sleep event exceeds its delivery bound.");
            emit?.Invoke(evt);
        }
        public static bool Apply(WorldEvent evt,double timestamp)
        {
            if(evt?.Kind!=EventKind)return false;
            if(worker||!enabled||terminal||!Authority)return true;
            now=timestamp;if(!double.IsFinite(now))throw new ArgumentException("Invalid sleep clock.");var value=evt.Data.Deserialize<GroupSleepEvent>(DedicatedProtocol.Json);ValidateWire(value);
            if(value.Sequence<=lastSequence)return true;lastSequence=value.Sequence;
            if(value.TargetPlayer.Length>0&&value.TargetPlayer!=localPlayer)return true;
            if(value.Phase=="accepted"||value.Phase=="denied"){notice?.Invoke(value.Text);if(value.Phase=="denied"){displayed="";SleepLoadingOverlay.RetryActions();}return true;}
            replica=value;
            CurfewManager.IsCurfewInEffect=value.Curfew;
            var curfew=CurfewManager._instance;
            if(curfew?.isNightMode!=null){curfew.isNightMode._runtimeValue=value.Curfew;curfew.isNightMode._isRuntimeValueInitialized=true;}
            TimeWeatherReplication.RefreshClientClockDisplay();
            if(value.Phase=="complete"||value.Phase=="cancelled"){HidePopup();SleepLoadingOverlay.Hide();displayed="";notice?.Invoke(value.Text);return true;}
            ShowReplica();return true;
        }
        // This path intentionally performs managed validation/render callbacks only.
        // Curfew fields, gameplay PopupDialog and native state await the load barrier.
        public static bool ApplyRecovery(WorldEvent evt,double timestamp,Action<GroupSleepEvent,Action<GroupSleepChoice>> render)
        {
            if(evt?.Kind!=EventKind)return false;
            if(!Authority||worker||enabled||terminal)throw new InvalidOperationException("Shared day recovery is outside its pending-load scope.");
            now=timestamp;if(!double.IsFinite(now))throw new ArgumentException("Invalid recovery clock.");
            var value=evt.Data.Deserialize<GroupSleepEvent>(DedicatedProtocol.Json);ValidateWire(value);
            if(value.Sequence<=lastSequence)return true;lastSequence=value.Sequence;
            if(value.TargetPlayer.Length>0&&value.TargetPlayer!=localPlayer)return true;
            if(SleepRecoveryProtocol.IsResponse(value)){notice?.Invoke(value.Text);return true;}
            replica=value;
            if(value.Phase=="complete"){notice?.Invoke("The shared day is complete. Waiting for the fresh server world...");render?.Invoke(value,null);return true;}
            var offered=value;
            render?.Invoke(value,choice=>{
                if(!Authority||worker||enabled||terminal||replica==null||!replica.RecoveryOwner||replica.Epoch!=offered.Epoch||replica.Step!=offered.Step)
                    throw new InvalidOperationException("This pending shared day choice has changed.");
                var intent=new GroupSleepIntent{Epoch=offered.Epoch,Step=offered.Step,Action=choice.Action,Percentage=choice.Percentage};
                ValidateChoice(replica,intent,localPlayer,offered.Epoch);
                Send(ActionKind,offered.Epoch,intent,true);
            });
            return true;
        }
        public static bool CanSendRecovery(InteractionRequest request)=>Authority&&!worker&&!enabled&&!terminal&&
            SleepRecoveryProtocol.AllowsPendingAction(replica,request,localPlayer);
        public static void ClearRecovery(){if(!worker&&!enabled)replica=null;}
        private static void ShowReplica()
        {
            SleepLoadingOverlay.Tick();
            if(replica==null||replica.Phase=="complete"||replica.Phase=="cancelled")return;
            string key=replica.Epoch+":"+replica.Step+":"+replica.Owner;
            if(replica.Phase=="waiting"){
                key=replica.Epoch+":"+string.Join(",",replica.Participants)+":"+string.Join(",",replica.ReadyPlayers);
                // Other players' readiness must not close this player's bed
                // confirmation before they can contribute their own vote.
                if(IsLocallyReady){HidePopup();SleepLoadingOverlay.Set("Waiting for everyone",replica.Text,new[]{"Cancel sleep"},new Action[]{CancelLocalReadiness},replica.Epoch+":waiting");}
                else{SleepLoadingOverlay.Hide();if(displayed!=key)notice?.Invoke(Cut(replica.Text,300));}
                displayed=key;return;
            }
            HidePopup();
            bool automatic=replica.NightTransition&&(replica.Phase=="summary"||replica.Phase=="ranking");
            if(replica.Choices.Length==0||replica.Owner!=localPlayer||automatic){
                string detail=replica.Phase=="synchronizing"?"The night has passed. Waiting for every player's world to finish updating...":
                    replica.Phase=="debt"?"Waiting for the shared debt choice before the world can continue.":
                    replica.NightTransition?"Passing the night and preparing the morning...":replica.Text;
                SleepLoadingOverlay.Set(replica.Phase=="synchronizing"?"Loading morning":replica.NightTransition?"Sleeping until morning":replica.Title,detail);
                displayed=key;return;
            }
            string requestEpoch=replica.Epoch;long requestStep=replica.Step;
            var labels=new List<string>();var actions=new List<Action>();
            foreach(var option in replica.Choices){var choice=option;labels.Add(choice.Label);actions.Add(()=>{
                try{Send(ActionKind,requestEpoch,new GroupSleepIntent{Epoch=requestEpoch,Step=requestStep,Action=choice.Action,Percentage=choice.Percentage});}
                catch(Exception ex){notice?.Invoke(ex.Message);throw;}
            });}
            SleepLoadingOverlay.Set(replica.Title,replica.Text,labels.ToArray(),actions.ToArray(),key+":"+replica.Phase);displayed=key;
        }
        private static void ShowPopup(string title,string text,string[] labels,Action[] actions)
        {
            if(PopupDialog.IsPopupVisible&&!OwnsPopup())throw new InvalidOperationException("Close the current dialog before choosing shared sleep.");
            HidePopup();callbacks.Clear();
            long generation=++popupGeneration;
            var native=new Il2CppReferenceArray<Il2CppSystem.Action>(actions.Length);
            for(int i=0;i<actions.Length;i++){
                // Native AddButton invokes our action without hiding the panel.
                // Close while ownership is intact, before sending or replacing UI.
                Action action=actions[i];Il2CppSystem.Action callback=(Il2CppSystem.Action)(Action)(()=>{
                    if(generation!=popupGeneration||!OwnsPopup())return;
                    try{HidePopup();action();}catch(Exception ex){notice?.Invoke(ex.Message);}
                });
                callbacks.Add(callback);native[i]=callback;
            }
            PopupDialog.Display(title,text,new Il2CppStringArray(labels),native,true);popupOwned=true;popupTitle=title;
        }
        private static bool OwnsPopup()
        {
            var popup=PopupDialog._instance;
            return popupOwned&&popup!=null&&popup.titleText!=null&&popup.titleText.text==popupTitle;
        }
        private static void HidePopup()
        {
            bool hide=OwnsPopup();popupGeneration++;popupOwned=false;popupTitle="";
            var popup=PopupDialog._instance;if(hide)popup.Hide();callbacks.Clear();
        }
        public static void ValidateIntent(GroupSleepIntent value)=>SleepRecoveryProtocol.ValidateIntent(value);
        public static void ValidateChoice(GroupSleepEvent current,GroupSleepIntent intent,string playerId,string targetId)=>SleepRecoveryProtocol.ValidateChoice(current,intent,playerId,targetId);
        public static void ValidateWire(GroupSleepEvent value)=>SleepRecoveryProtocol.ValidateWire(value);
        private static string Cut(string value,int maximum){value=value??"";return value.Length<=maximum?value:value.Substring(0,maximum);}
        private static void InstallPatches()
        {
            if(patched)return;var harmony=new Harmony("local.nivalis.together.sleep-authority");
            const BindingFlags flags=BindingFlags.Public|BindingFlags.Instance|BindingFlags.DeclaredOnly;
            var normal=typeof(SleepManager).GetMethod("TryToSleep",flags,null,new[]{typeof(IBed)},null);
            var forced=typeof(SleepManager).GetMethod("ForceSleep",flags,null,Type.EmptyTypes,null);
            if(normal==null||forced==null)throw new MissingMethodException("Native sleep entrypoints differ from the inspected build.");
            harmony.Patch(normal,prefix:new HarmonyMethod(typeof(SleepReplication),nameof(ClientSleepPrefix)));
            harmony.Patch(forced,prefix:new HarmonyMethod(typeof(SleepReplication),nameof(ClientForceSleepPrefix)));
            patched=true;
        }
        private static bool ClientSleepPrefix(IBed __0)
        {
            if(!Authority)return true;
            if(worker)return authorizedSleep&&!waitingForPlayers&&!awaitingWakeSynchronization;
            if(!terminal&&enabled){var bed=__0?.TryCast<Bed>();if(bed!=null)RouteClientInteraction(bed.Cast<IInteractable>());}
            return false;
        }
        private static bool ClientForceSleepPrefix()=>!Authority||worker&&authorizedSleep&&!waitingForPlayers&&!awaitingWakeSynchronization;
    }
}
