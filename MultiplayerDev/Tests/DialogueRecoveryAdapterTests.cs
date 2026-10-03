using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using NivalisTogether.Dedicated;

// Actual adapter methods are extracted by Roslyn. These stand-ins exercise
// their authority/lifecycle boundaries without loading or controlling the game.
namespace NivalisTogether.Dedicated
{
    internal static class Time { internal static float realtimeSinceStartup; }
    internal static class DedicatedWorldRuntime { internal static bool IsDedicatedServer, IsWorldReady; }
    // Existing recovery regressions exercise the retained non-sandbox adapter.
    internal static class MultiplayerSandboxRuntime { internal static bool Active; }
    internal static class TravelReplication { internal static bool IsBusy; }
    internal static class SleepReplication { internal static bool IsBusy; }
    internal static class ApartmentReplication { internal static bool IsBusy; }
    internal static class PlayerAccountRuntime
    {
        internal static bool FailClosed, ThrowOnEnter;
        internal static string CurrentAccountId = "";
        internal static int Entered, Exited;
        internal static IDisposable WithAccount(string account)
        {
            if(ThrowOnEnter)throw new InvalidOperationException("scope unavailable");
            Entered++;CurrentAccountId=account;return new Scope();
        }
        internal static void Fault(string reason){FailClosed=true;}
        private sealed class Scope:IDisposable {public void Dispose(){Exited++;CurrentAccountId="";}}
    }
    internal sealed class NativeChoice {internal string articyID, Text; internal bool Enabled=true;}
    internal sealed class NativeStep
    {
        internal List<NativeChoice> Choices = new List<NativeChoice>();
        internal string Text="Line";
        internal Action<int> Callback;
        internal int Reports, LastChoice;
        internal void ReportChoicePicked(int index){Reports++;LastChoice=index;Callback?.Invoke(index);}
    }
    internal sealed class DialogueTreeProgressManager
    {
        internal static DialogueTreeProgressManager Instance=new DialogueTreeProgressManager();
        internal NativeStep _stepDisplayData;
        internal bool IsInDialogue=true;
    }
    internal sealed class DialogueUI
    {
        internal static DialogueUI Instance=new DialogueUI();
        internal NativeStep _currentStepData;
        internal void _HandleNoChoices_b__59_0(){_currentStepData.ReportChoicePicked(-1);}
    }
    internal static partial class ActualRecovery
    {
        private const string SandboxDialogueMessage = "Campaign conversations are disabled in multiplayer sandbox.";
        private static void ApplySandboxPolicy() { }
        private static readonly Dictionary<string,string> RecoveryPlayers=new Dictionary<string,string>();
        private static readonly Dictionary<string,object> Players=new Dictionary<string,object>();
        private static readonly Dictionary<string,WorldEvent> RecoveryResults=new Dictionary<string,WorldEvent>();
        private static readonly Dictionary<string,string> RecoveryRequestBindings=new Dictionary<string,string>();
        private static readonly Queue<string> RecoveryResultOrder=new Queue<string>();
        private static readonly DialogueAccountBinding AccountBinding=new DialogueAccountBinding();
        private static readonly DialogueRecoveryStepGate RecoveryStepGate=new DialogueRecoveryStepGate();
        private static DialogueRecoveryState recoveryIssued,recoveryReplica;
        private static long recoverySequence,lastRecoverySequence,pendingRecoverySequence,stepRevision;
        private static float nextRecovery,lastActivity,nextCapture;
        private static string pendingRecoveryRequest="",owner="",conversation="",targetId="";
        private static bool recoveryAwaitingState,recoveryRetryAfterDenial,clientAuthority,executingChoice;
        internal static string LocalPlayerId="";
        internal static Action<InteractionRequest> OutboundInteraction;
        internal static Action<WorldEvent> OutboundWorldEvent;
        private static bool IsConversationActive=>DialogueTreeProgressManager.Instance.IsInDialogue;
        private static NativeStep observed;
        private static void UpdateNativeStep()
        {
            if(!IsConversationActive){conversation=owner=targetId="";AccountBinding.Clear();return;}
            if(observed!=DialogueTreeProgressManager.Instance._stepDisplayData){observed=DialogueTreeProgressManager.Instance._stepDisplayData;stepRevision++;}
        }
        private static void ReleaseOwner(bool dismiss){owner="";AccountBinding.Release();}
        private static SharedDialogueState CaptureDialogue()
        {
            var native=DialogueTreeProgressManager.Instance._stepDisplayData;
            var state=new SharedDialogueState{Active=true,Text=native.Text,Choices=new DialogueChoiceState[native.Choices.Count]};
            for(int i=0;i<state.Choices.Length;i++)state.Choices[i]=new DialogueChoiceState{Id=native.Choices[i].articyID,Text=native.Choices[i].Text,Enabled=native.Choices[i].Enabled};
            return state;
        }
        internal static void ResetTest(bool worker,string account,string player,NativeStep native)
        {
            MultiplayerSandboxRuntime.Active=false;
            RecoveryPlayers.Clear();Players.Clear();RecoveryResults.Clear();RecoveryRequestBindings.Clear();RecoveryResultOrder.Clear();AccountBinding.Clear();RecoveryStepGate.Reset();
            recoveryIssued=recoveryReplica=null;recoverySequence=lastRecoverySequence=pendingRecoverySequence=0;stepRevision=1;
            nextRecovery=lastActivity=nextCapture=0;pendingRecoveryRequest=owner="";conversation=Guid.NewGuid().ToString("N");targetId="npc";
            recoveryAwaitingState=recoveryRetryAfterDenial=executingChoice=false;clientAuthority=!worker;
            DedicatedWorldRuntime.IsDedicatedServer=worker;DedicatedWorldRuntime.IsWorldReady=true;
            TravelReplication.IsBusy=SleepReplication.IsBusy=ApartmentReplication.IsBusy=false;
            PlayerAccountRuntime.FailClosed=PlayerAccountRuntime.ThrowOnEnter=false;PlayerAccountRuntime.Entered=PlayerAccountRuntime.Exited=0;PlayerAccountRuntime.CurrentAccountId="";
            Time.realtimeSinceStartup=0;LocalPlayerId=player;OutboundInteraction=null;OutboundWorldEvent=null;
            DialogueTreeProgressManager.Instance=new DialogueTreeProgressManager{_stepDisplayData=native};DialogueUI.Instance=new DialogueUI();observed=native;
            if(worker)AccountBinding.Restore(account);
        }
        internal static void Pulse(){Time.realtimeSinceStartup+=3;UpdateNativeStep();TickRecovery();}
        internal static DialogueRecoveryState Current=>recoveryReplica;
        internal static bool ChoiceExecuting=>executingChoice;
        internal static void ForgetAccount()=>AccountBinding.Clear();
    }
}
internal static class DialogueRecoveryAdapterTests
{
    private static int checks;
    private static string account=Guid.NewGuid().ToString("N"), player=Guid.NewGuid().ToString("N");
    private static List<WorldEvent> published;
    private static void Check(bool value,string name){checks++;if(!value)throw new Exception(name);}
    private static void Throws(Action action,string name){checks++;try{action();}catch(InvalidOperationException){return;}catch(InvalidDataException){return;}throw new Exception(name);}
    private static DialogueRecoveryState Read(WorldEvent e)=>e.Data.Deserialize<DialogueRecoveryState>(DedicatedProtocol.Json);
    private static WorldEvent Wrap(DialogueRecoveryState s)=>new WorldEvent{Kind=DialogueRecoveryProtocol.EventKind,TargetPlayerId=s.TargetPlayerId,Data=JsonSerializer.SerializeToElement(s,DedicatedProtocol.Json)};
    private static DialogueRecoveryState Clone(DialogueRecoveryState s,long seq,string phase="state",string req="")
    {
        var copy=JsonSerializer.Deserialize<DialogueRecoveryState>(JsonSerializer.Serialize(s,DedicatedProtocol.Json),DedicatedProtocol.Json);
        copy.Sequence=seq;copy.Phase=phase;copy.RequestId=req;
        if(phase!="state"){copy.Choices=Array.Empty<DialogueRecoveryChoice>();copy.CanContinue=false;}
        return copy;
    }
    private static NativeStep Native()=>new NativeStep{Choices=new List<NativeChoice>{new NativeChoice{articyID="disabled",Text="Unavailable",Enabled=false},new NativeChoice{articyID="yes",Text="Yes"}}};
    private static DialogueRecoveryState StartWorker(NativeStep native)
    {
        ActualRecovery.ResetTest(true,account,player,native);published=new List<WorldEvent>();ActualRecovery.OutboundWorldEvent=published.Add;
        ActualRecovery.SetRecoveryPlayerPresent(player,account,true);ActualRecovery.Pulse();return Read(published[^1]);
    }
    private static InteractionRequest Request(DialogueRecoveryState s,string choice="yes",bool resume=false)=>new InteractionRequest{
        Kind=DialogueRecoveryProtocol.ActionKind,PlayerId=s.TargetPlayerId,AccountId=s.OwnerAccountId,RequestId=Guid.NewGuid().ToString("N"),TargetId=s.TargetId,
        Data=JsonSerializer.SerializeToElement(new DialogueRecoveryIntent{Sequence=s.Sequence,Conversation=s.Conversation,Step=s.Step,ChoiceId=resume?"":choice,Continue=resume},DedicatedProtocol.Json)};
    private static DialogueRecoveryState State(long seq=1)=>new DialogueRecoveryState{Sequence=seq,Conversation=Guid.NewGuid().ToString("N"),Step=1,OwnerAccountId=account,TargetPlayerId=player,TargetId="npc",Text="Line",Choices=new[]{new DialogueRecoveryChoice{Id="yes",Text="Yes",Enabled=true},new DialogueRecoveryChoice{Id="no",Text="Disabled",Enabled=false}}};
    public static int Main()
    {
        var native=Native();var shown=StartWorker(native);
        Check(shown.OwnerAccountId==account&&shown.TargetPlayerId==player&&shown.Choices.Length==2,"pending same account receives exact native choices");
        native.Callback=i=>{Check(i==1,"choice identity resolves actual native index");Check(PlayerAccountRuntime.CurrentAccountId==account&&ActualRecovery.ChoiceExecuting,"native callback runs under bound account and authority scope");};
        var request=Request(shown);int before=published.Count;
        var result=ActualRecovery.HandleRecoveryInteraction(request);
        Check(Read(result).Phase=="accepted"&&Read(result).RequestId==request.RequestId,"native acceptance receipt correlates request");
        Check(published.Count==before,"handler cannot publish fresh state before returned receipt is queued");
        Check(native.Reports==1&&PlayerAccountRuntime.Entered==1&&PlayerAccountRuntime.Exited==1,"callback and account scope each run once");
        var replay=Read(ActualRecovery.HandleRecoveryInteraction(request));
        Check(replay.Phase=="accepted"&&replay.Sequence>Read(result).Sequence&&native.Reports==1,"duplicate request returns fresh sequenced cached outcome without rerunning native action");
        ActualRecovery.Pulse();var heartbeat=Read(published[^1]);
        Check(heartbeat.Step==shown.Step&&heartbeat.Sequence>shown.Sequence,"callback return cannot manufacture native step progression");
        Check(Read(ActualRecovery.HandleRecoveryInteraction(Request(heartbeat))).Phase=="denied"&&native.Reports==1,"same-step heartbeat cannot replay consumed callback");
        string replacement=Guid.NewGuid().ToString("N");ActualRecovery.SetRecoveryPlayerPresent(player,account,false);ActualRecovery.SetRecoveryPlayerPresent(replacement,account,true);ActualRecovery.Pulse();
        Check(Read(ActualRecovery.HandleRecoveryInteraction(Request(Read(published[^1])))).Phase=="denied"&&native.Reports==1,"new player session cannot replay same account native step");
        var next=Native();DialogueTreeProgressManager.Instance._stepDisplayData=next;ActualRecovery.Pulse();
        var reused=Request(Read(published[^1]));reused.PlayerId=player;reused.RequestId=request.RequestId;
        var reuse=Read(ActualRecovery.HandleRecoveryInteraction(reused));
        Check(reuse.Phase=="denied"&&reuse.Sequence>Read(published[^1]).Sequence&&next.Reports==0,"request identity reused on changed intent returns fresh denial without replaying stale frame or native action");
        Check(Read(ActualRecovery.HandleRecoveryInteraction(Request(Read(published[^1])))).Phase=="accepted"&&next.Reports==1,"observed native display transition allows next exact choice");
        ActualRecovery.SetRecoveryPlayerPresent(replacement,account,false);DialogueTreeProgressManager.Instance.IsInDialogue=false;ActualRecovery.Pulse();
        Check(Read(published[^1]).Phase=="complete"&&Read(published[^1]).TargetPlayerId==replacement,"completion is emitted for last target after disconnect to clear global fence");
        before=published.Count;ActualRecovery.Pulse();Check(published.Count==before,"completion fence releases exactly once");

        native=Native();shown=StartWorker(native);PlayerAccountRuntime.ThrowOnEnter=true;
        Check(Read(ActualRecovery.HandleRecoveryInteraction(Request(shown))).Phase=="denied"&&native.Reports==0,"failed account scope denies before native execution");
        PlayerAccountRuntime.ThrowOnEnter=false;ActualRecovery.Pulse();
        Check(Read(ActualRecovery.HandleRecoveryInteraction(Request(Read(published[^1])))).Phase=="accepted"&&native.Reports==1,"pre-execution denial leaves same native step retryable");
        foreach(string fence in new[]{"travel","sleep","apartment","world","account"}){
            native=Native();shown=StartWorker(native);
            TravelReplication.IsBusy=fence=="travel";SleepReplication.IsBusy=fence=="sleep";ApartmentReplication.IsBusy=fence=="apartment";DedicatedWorldRuntime.IsWorldReady=fence!="world";PlayerAccountRuntime.FailClosed=fence=="account";
            Check(Read(ActualRecovery.HandleRecoveryInteraction(Request(shown))).Phase=="denied"&&native.Reports==0,"unsafe native transition denied: "+fence);
        }
        native=Native();shown=StartWorker(native);var stolen=Request(shown);stolen.AccountId=Guid.NewGuid().ToString("N");
        Check(Read(ActualRecovery.HandleRecoveryInteraction(stolen)).Phase=="denied"&&native.Reports==0,"authenticated different account cannot take branch");
        Check(Read(ActualRecovery.HandleRecoveryInteraction(Request(shown,"disabled"))).Phase=="denied"&&native.Reports==0,"disabled native choice cannot execute");
        native.Choices[1].Enabled=false;
        Check(Read(ActualRecovery.HandleRecoveryInteraction(Request(shown))).Phase=="denied"&&native.Reports==0,"native availability rechecked after offered state");
        native=Native();shown=StartWorker(native);ActualRecovery.ForgetAccount();ActualRecovery.Pulse();
        Check(published.Count==1&&Read(ActualRecovery.HandleRecoveryInteraction(Request(shown))).Phase=="denied","unknown legacy branch cannot acquire first arriving account");
        native=new NativeStep();shown=StartWorker(native);native.Callback=i=>Check(i==-1,"continuation invokes actual native sentinel callback");
        Check(shown.CanContinue&&shown.Choices.Length==0,"native continuation exposes only explicit continuation control");
        Check(Read(ActualRecovery.HandleRecoveryInteraction(Request(shown,resume:true))).Phase=="accepted"&&native.Reports==1,"explicit continuation enters native callback once");
        native=Native();shown=StartWorker(native);native.Callback=i=>throw new InvalidOperationException("partial\0failure");
        var failed=Read(ActualRecovery.HandleRecoveryInteraction(Request(shown)));
        Check(failed.Phase=="denied"&&!failed.Message.Contains('\0')&&PlayerAccountRuntime.FailClosed&&PlayerAccountRuntime.Exited==1,"partial native failure faults before restoration and returns bounded valid receipt");
        native=Native();native.Text=new string('x',25000);shown=StartWorker(native);
        Check(!shown.CanContinue&&shown.Choices.Length==0&&shown.Message.Length>0&&native.Reports==0,"unsupported presentation yields explicit paused state without guessed controls");

        ActualRecovery.ResetTest(false,account,player,Native());shown=State();ActualRecovery.ApplyRecovery(Wrap(shown));InteractionRequest sent=null;ActualRecovery.OutboundInteraction=r=>sent=r;
        string id=ActualRecovery.SendRecoveryChoice(shown,shown.Choices[0]);Check(id==sent.RequestId&&ActualRecovery.CanSendRecovery(sent),"pre-load choice produces exact authorized request identity");
        Throws(()=>ActualRecovery.SendRecoveryChoice(shown,shown.Choices[0]),"pending request blocks second client submission");
        var same=Clone(shown,2);ActualRecovery.ApplyRecovery(Wrap(same));Throws(()=>ActualRecovery.SendRecoveryChoice(same,same.Choices[0]),"same-step heartbeat cannot rearm in-flight client");
        ActualRecovery.ApplyRecovery(Wrap(Clone(shown,3,"denied",Guid.NewGuid().ToString("N"))));same=Clone(shown,4);ActualRecovery.ApplyRecovery(Wrap(same));Throws(()=>ActualRecovery.SendRecoveryChoice(same,same.Choices[0]),"unmatched denial cannot rearm client");
        ActualRecovery.ApplyRecovery(Wrap(Clone(shown,5,"denied",id)));Throws(()=>ActualRecovery.SendRecoveryChoice(same,same.Choices[0]),"matched denial still waits fresh full presentation");
        same=Clone(shown,6);ActualRecovery.ApplyRecovery(Wrap(same));id=ActualRecovery.SendRecoveryChoice(same,same.Choices[0]);Check(id==sent.RequestId,"matched denial plus fresh full state permits retry");
        ActualRecovery.ApplyRecovery(Wrap(Clone(shown,7,"accepted",id)));same=Clone(shown,8);ActualRecovery.ApplyRecovery(Wrap(same));Throws(()=>ActualRecovery.SendRecoveryChoice(same,same.Choices[0]),"accepted receipt and same-step heartbeat never rearm client");
        var changed=Clone(shown,9);changed.Step++;ActualRecovery.ApplyRecovery(Wrap(changed));id=ActualRecovery.SendRecoveryChoice(changed,changed.Choices[0]);Check(id==sent.RequestId,"different observed native step rearms client");
        var complete=Clone(changed,10,"complete");ActualRecovery.ApplyRecovery(Wrap(complete));Throws(()=>ActualRecovery.SendRecoveryChoice(changed,changed.Choices[0]),"terminal completion cannot send old choice");
        ActualRecovery.ResetTest(false,account,player,Native());shown=State();ActualRecovery.ApplyRecovery(Wrap(shown));same=Clone(shown,2);ActualRecovery.ApplyRecovery(Wrap(same));ActualRecovery.OutboundInteraction=r=>sent=r;
        Throws(()=>ActualRecovery.SendRecoveryChoice(shown,shown.Choices[0]),"captured stale menu sequence rejected");
        Throws(()=>ActualRecovery.SendRecoveryChoice(same,new DialogueRecoveryChoice{Id="no",Enabled=true}),"caller cannot enable disabled offered choice");
        Throws(()=>ActualRecovery.SendRecoveryChoice(same,new DialogueRecoveryChoice{Continue=true}),"caller cannot invent continuation");
        ActualRecovery.OutboundInteraction=r=>throw new InvalidOperationException("send");Throws(()=>ActualRecovery.SendRecoveryChoice(same,same.Choices[0]),"failed send reported");
        ActualRecovery.OutboundInteraction=r=>sent=r;Check(ActualRecovery.SendRecoveryChoice(same,same.Choices[0])==sent.RequestId,"failed send does not strand local pending state");

        ActualRecovery.ResetTest(false,account,player,Native());shown=State();ActualRecovery.ApplyRecovery(Wrap(shown));ActualRecovery.OutboundInteraction=r=>sent=r;
        id=ActualRecovery.SendRecoveryChoice(shown,shown.Choices[0]);same=Clone(shown,2);ActualRecovery.ApplyRecovery(Wrap(same));
        var rejection=new DialogueRecoveryRejection{RequestId=id,Sequence=shown.Sequence,Conversation=shown.Conversation,Step=shown.Step,OwnerAccountId=account,TargetPlayerId=player,Retryable=false};
        Check(!ActualRecovery.ApplyRecoveryRejection(rejection),"consumed service rejection never reopens step");
        rejection.Retryable=true;rejection.RequestId=Guid.NewGuid().ToString("N");Check(!ActualRecovery.ApplyRecoveryRejection(rejection),"service rejection requires exact pending request id");
        rejection.RequestId=id;rejection.Sequence=2;Check(!ActualRecovery.ApplyRecoveryRejection(rejection),"service rejection requires original submitted sequence rather than latest heartbeat");
        rejection.Sequence=shown.Sequence;rejection.OwnerAccountId=Guid.NewGuid().ToString("N");Check(!ActualRecovery.ApplyRecoveryRejection(rejection),"service rejection requires original account");
        rejection.OwnerAccountId=account;Check(ActualRecovery.ApplyRecoveryRejection(rejection),"matching unconsumed heartbeat race receipt accepted");
        Throws(()=>ActualRecovery.SendRecoveryChoice(same,same.Choices[0]),"retryable service denial waits for later full state");
        ActualRecovery.ApplyRecovery(Wrap(same));Throws(()=>ActualRecovery.SendRecoveryChoice(same,same.Choices[0]),"repeated earlier heartbeat does not count as fresh state");
        same=Clone(shown,3);ActualRecovery.ApplyRecovery(Wrap(same));Check(ActualRecovery.SendRecoveryChoice(same,same.Choices[0])==sent.RequestId,"fresh full state after correlated service rejection allows retry");
        MultiplayerSandboxRuntime.Active=true;
        Check(!ActualRecovery.CanSendRecovery(sent),"sandbox cannot authorize pre-load dialogue lane");
        Throws(()=>ActualRecovery.SendRecoveryChoice(same,same.Choices[0]),"sandbox blocks a previously rendered client callback");
        Check(!ActualRecovery.ApplyRecoveryRejection(rejection),"sandbox cannot reopen recovery through a service retry receipt");
        native=Native();shown=StartWorker(native);request=Request(shown);
        Check(Read(ActualRecovery.HandleRecoveryInteraction(request)).Phase=="accepted","prepare previously accepted recovery cache");
        MultiplayerSandboxRuntime.Active=true;int scopes=PlayerAccountRuntime.Entered;
        Check(Read(ActualRecovery.HandleRecoveryInteraction(request)).Phase=="denied"&&native.Reports==1&&PlayerAccountRuntime.Entered==scopes,
            "sandbox rejects even cached accepted dialogue request without entering account/reward scope");
        Console.WriteLine("Dialogue recovery adapters PASS: "+checks+" assertions using actual production methods and native boundary stand-ins.");return 0;
    }
}
