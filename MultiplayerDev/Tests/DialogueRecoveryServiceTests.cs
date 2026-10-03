using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using NivalisTogether.Dedicated;

public static partial class DialogueRecoveryServiceTests
{
    private static long recoverySequence;
    private static SnapshotBegin deferredNativeBegin;
    private static DialogueRecoveryState State(string account,string player,string conversation,long step=1)=>new DialogueRecoveryState {
        Sequence=++recoverySequence,Conversation=conversation,Step=step,OwnerAccountId=account,TargetPlayerId=player,TargetId="dialogue",
        SpeakerName="Test speaker",Text="Existing native branch\nOnly its owner may answer.",Choices=new[]{
            new DialogueRecoveryChoice {Id="yes",Text="Continue the branch",Enabled=true},new DialogueRecoveryChoice{Id="disabled",Text="Unavailable",Enabled=false}}};
    private static InteractionRequest Choice(DialogueRecoveryState state,string choice="yes",bool continuation=false)=>new InteractionRequest {
        PlayerId="forged",AccountId="forged",RequestId=Guid.NewGuid().ToString("N"),Kind=DialogueRecoveryProtocol.ActionKind,TargetId=state.TargetId,
        Data=JsonSerializer.SerializeToElement(new DialogueRecoveryIntent{Sequence=state.Sequence,Conversation=state.Conversation,Step=state.Step,ChoiceId=continuation?"":choice,Continue=continuation},DedicatedProtocol.Json)};
    private static WorldEvent Publish(Fixture f,Client worker,Client target,DialogueRecoveryState state)
    {
        worker.Connection.Send("worldEvent",new WorldEvent {Kind=DialogueRecoveryProtocol.EventKind,TargetPlayerId=state.TargetPlayerId,Data=JsonSerializer.SerializeToElement(state,DedicatedProtocol.Json)});
        var evt=f.Wait(target,"worldEvent").Read<WorldEvent>();
        Check(evt.Kind==DialogueRecoveryProtocol.EventKind&&evt.TargetPlayerId==state.TargetPlayerId,"Recovery is privately addressed");
        target.Connection.Send("worldEventAck",new WorldEventAcknowledgement{DeliveryIds=new[]{evt.DeliveryId}});f.Pump();return evt;
    }
    private static string LastToken(Fixture f,Client worker)
    {
        for(int i=0;i<8;i++)f.Pump();
        var messages=worker.Received.Where(x=>x.Type=="checkpointRequested").ToArray();
        Check(messages.Length>0,"Fresh checkpoint request exists");foreach(var m in messages)worker.Received.Remove(m);
        return messages.Last().Read<CheckpointRequest>().RequestId;
    }
    private static string BeginUpload(Fixture f,Client worker,AccountWorldState ledger,string token)
    {
        byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(ledger,DedicatedProtocol.Json);var transfer=new AccountDataTransfer();transfer.StartSend(bytes,ledger.WorldId,ledger.Revision,"ledger");
        while(transfer.IsSending){transfer.PumpSend(worker.Connection);f.Pump();}
        string id=Guid.NewGuid().ToString("N");byte[] native={31,41,59};
        deferredNativeBegin=new SnapshotBegin{WorldId=ledger.WorldId,Revision=ledger.Revision,TransferId=id,Length=native.Length,Format="fixture-native",Sha256=Convert.ToHexString(SHA256.HashData(native)),AccountLedgerHash=Convert.ToHexString(SHA256.HashData(bytes)),CheckpointRequestId=token};
        for(int i=0;i<4;i++)f.Pump();return id;
    }
    private static void RunRecovery(string root)
    {
        TestPureRecovery();
        Directory.CreateDirectory(root);
        using var f=new Fixture(root);var worker=f.Worker(out var world);
        var ledger=new AccountWorldState{WorldId=world.WorldId,Revision=1,LegacyEscrow=new AccountInventoryState{Money=99000}};
        f.Checkpoint(worker,ledger,new byte[]{1,2,3});f.Ready(worker,ledger);
        using var a=f.Connect();var authA=f.Authenticate(a,"register","recovery_owner","long-owner-test-password");
        using var b=f.Connect();var authB=f.Authenticate(b,"register","other_account","long-other-test-password");
        ledger.Revision=2;ledger.Accounts=new[]{new AccountState{AccountId=authA.AccountId,Money=123,Initialized=true,StateRevision=1,AppearanceId="native-model-a"},
            new AccountState{AccountId=authB.AccountId,Money=456,Initialized=true,StateRevision=1,AppearanceId="native-model-b"}};
        f.Checkpoint(worker,ledger,new byte[]{4,5,6});
        f.Join(a,"native-model-a",out var joinedA);f.Join(b,"native-model-b",out var joinedB);
        var presence=worker.Received.Where(m=>m.Type=="dialogueRecoveryPresence").Select(m=>m.Read<PlayerPresence>()).ToArray();
        Check(presence.Any(p=>p.Joined&&p.PlayerId==joinedA.PlayerId&&p.AccountId==authA.AccountId),"Pending owner presence binds real account");
        Check(presence.Any(p=>p.Joined&&p.PlayerId==joinedB.PlayerId&&p.AccountId==authB.AccountId),"Pending nonowner presence remains distinct");
        string preToken=LastToken(f,worker);ledger.Revision=3;string upload=BeginUpload(f,worker,ledger,preToken);
        string conversation=Guid.NewGuid().ToString("N");var state=State(authA.AccountId,joinedA.PlayerId,conversation);
        var peers=(System.Collections.IEnumerable)typeof(NivalisTogether.Server.DedicatedService).GetField("_peers",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).GetValue(f.Service);
        object observer=null;
        foreach(var value in peers)if((string)value.GetType().GetField("Id").GetValue(value)==joinedB.PlayerId)observer=value;
        Check(observer!=null,"Find authenticated pending observer for deterministic liveness check");
        observer.GetType().GetField("LoadStarted").SetValue(observer,DateTime.UtcNow.AddSeconds(-90));
        Publish(f,worker,a,state);
        Check((DateTime.UtcNow-(DateTime)observer.GetType().GetField("LoadStarted").GetValue(observer)).TotalSeconds<2,"Private recovery heartbeat renews all pending import waiters");
        Check(b.Received.Any(m=>m.Type=="simulationState"&&m.Read<SimulationState>().Ready&&m.Read<SimulationState>().Message.Contains("interrupted conversation")),"Other account receives only a generic waiting notice");
        Check(!b.Received.Any(m=>m.Type=="worldEvent"),"Different account receives no dialogue recovery text");
        worker.Connection.Send("snapshotBegin",deferredNativeBegin);
        worker.Connection.Send("snapshotChunk",new SnapshotChunk{TransferId=upload,Data=new byte[]{31,41,59}});
        worker.Connection.Send("snapshotEnd",new SnapshotEnd{TransferId=upload});f.Wait(worker,"snapshotCommitted");
        for(int i=0;i<10;i++)f.Pump();
        Check(!a.Received.Any(m=>m.Type=="accountDataBegin"||m.Type=="snapshotBegin")&&!b.Received.Any(m=>m.Type=="snapshotBegin"),"Pre-recovery upload cannot release any stale import");
        string activeToken=LastToken(f,worker);
        b.Connection.Send("interaction",Choice(state));f.Wait(b,"dialogueRecoveryRejected");
        Check(!worker.Received.Any(m=>m.Type=="interaction"),"Authenticated other account cannot act for conversation owner");
        a.Connection.Send("interaction",Choice(state,"disabled"));f.Wait(a,"dialogueRecoveryRejected");
        a.Connection.Send("interaction",Choice(state,"",true));f.Wait(a,"dialogueRecoveryRejected");
        Check(!worker.Received.Any(m=>m.Type=="interaction"),"Disabled choice and invented continuation rejected");
        var racedRequest=Choice(state);
        var beforeClickHeartbeat=State(authA.AccountId,joinedA.PlayerId,conversation);Publish(f,worker,a,beforeClickHeartbeat);
        a.Connection.Send("interaction",racedRequest);var raced=f.Wait(a,"dialogueRecoveryRejected").Read<DialogueRecoveryRejection>();
        Check(raced.Retryable&&raced.RequestId==racedRequest.RequestId&&raced.Sequence==state.Sequence&&raced.OwnerAccountId==authA.AccountId&&raced.TargetPlayerId==joinedA.PlayerId,"In-flight heartbeat race gets exact retryable rejection receipt before any native action");
        var request=Choice(beforeClickHeartbeat);a.Connection.Send("interaction",request);var forwarded=f.Wait(worker,"interaction").Read<InteractionRequest>();
        Check(forwarded.PlayerId==joinedA.PlayerId&&forwarded.AccountId==authA.AccountId&&forwarded.RequestId==request.RequestId,"Service stamps both authenticated identities");
        a.Connection.Send("interaction",request);var duplicate=f.Wait(a,"dialogueRecoveryRejected").Read<DialogueRecoveryRejection>();Check(!duplicate.Retryable,"Duplicate accepted action cannot rearm client controls");
        var heartbeat=State(authA.AccountId,joinedA.PlayerId,conversation);Publish(f,worker,a,heartbeat);
        a.Connection.Send("interaction",Choice(heartbeat));f.Wait(a,"dialogueRecoveryRejected");
        Check(!worker.Received.Any(m=>m.Type=="interaction"),"Same-step heartbeat never reopens consumed choice");
        var next=State(authA.AccountId,joinedA.PlayerId,conversation,2);next.CanContinue=true;next.Choices=Array.Empty<DialogueRecoveryChoice>();Publish(f,worker,a,next);
        var reusedId=Choice(next,"",true);reusedId.RequestId=request.RequestId;a.Connection.Send("interaction",reusedId);
        Check(!f.Wait(a,"dialogueRecoveryRejected").Read<DialogueRecoveryRejection>().Retryable&&!worker.Received.Any(m=>m.Type=="interaction"),"A previously consumed request ID cannot be reused at a new native step");
        a.Connection.Send("interaction",Choice(state));f.Wait(a,"dialogueRecoveryRejected");
        var continuation=Choice(next,"",true);a.Connection.Send("interaction",continuation);f.Wait(worker,"interaction");
        a.Dispose();for(int i=0;i<8;i++)f.Pump();
        Check(worker.Received.Any(m=>m.Type=="dialogueRecoveryPresence"&&!m.Read<PlayerPresence>().Joined&&m.Read<PlayerPresence>().PlayerId==joinedA.PlayerId),"Pending cancellation releases worker recovery ownership");
        using var reconnect=f.Connect();var again=f.Authenticate(reconnect,"login","recovery_owner","long-owner-test-password");
        Check(again.AccountId==authA.AccountId,"Pending recovery reconnect retains account");f.Join(reconnect,"native-model-a",out var joinedAgain);
        Check(joinedAgain.PlayerId!=joinedA.PlayerId,"Recovery reconnect gets fresh player session");
        var resumed=State(authA.AccountId,joinedAgain.PlayerId,conversation,3);Publish(f,worker,reconnect,resumed);
        var resumedRequest=Choice(resumed);reconnect.Connection.Send("interaction",resumedRequest);f.Wait(worker,"interaction");
        var denied=State(authA.AccountId,joinedAgain.PlayerId,conversation,3);denied.Phase="denied";denied.RequestId=resumedRequest.RequestId;denied.Choices=Array.Empty<DialogueRecoveryChoice>();
        Publish(f,worker,reconnect,denied);
        var reissued=State(authA.AccountId,joinedAgain.PlayerId,conversation,3);Publish(f,worker,reconnect,reissued);
        reconnect.Connection.Send("interaction",Choice(reissued));f.Wait(worker,"interaction");
        var complete=State(authA.AccountId,joinedAgain.PlayerId,conversation,4);complete.Phase="complete";complete.Choices=Array.Empty<DialogueRecoveryChoice>();Publish(f,worker,reconnect,complete);
        string freshToken=LastToken(f,worker);Check(freshToken!=activeToken,"Dialogue completion rotates checkpoint correlation");
        reconnect.Connection.Send("interaction",Choice(reissued));f.Wait(reconnect,"dialogueRecoveryRejected");
        ledger.Revision=4;f.Checkpoint(worker,ledger,new byte[]{7,8,9},activeToken);
        for(int i=0;i<8;i++)f.Pump();Check(!reconnect.Received.Any(m=>m.Type=="snapshotBegin"),"Old correlation cannot release completed recovery into stale world");
        ledger.Revision=5;f.Checkpoint(worker,ledger,new byte[]{10,11,12},freshToken);
        var own=f.Load(reconnect,world.WorldId,5);var other=f.Load(b,world.WorldId,5);
        Check(own.AccountId==authA.AccountId&&own.Money==123&&other.AccountId==authB.AccountId&&other.Money==456,"Fresh paired checkpoint preserves separate private accounts");
        Check(worker.Received.Any(m=>m.Type=="dialogueRecoveryPresence"&&!m.Read<PlayerPresence>().Joined&&m.Read<PlayerPresence>().PlayerId==joinedAgain.PlayerId),"Actual bootstrap withdraws recovery presence before normal loading");
        Check(worker.Connection.Connected,"Rejected pending choices preserve worker authority");
        var durable=JsonSerializer.Deserialize<WorldMetadata>(File.ReadAllBytes(Path.Combine(root,"world.json")),DedicatedProtocol.Json);
        Check(durable.Revision==5&&durable.AccountLedgerHash==Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root,durable.AccountLedgerFile)))) ,"Recovery releases only durable paired state");
    }
    private static void TestPureRecovery()
    {
        string account=Guid.NewGuid().ToString("N"),player=Guid.NewGuid().ToString("N"),conversation=Guid.NewGuid().ToString("N");
        var state=State(account,player,conversation);DialogueRecoveryProtocol.ValidateWire(state);
        var request=Choice(state);request.PlayerId=player;request.AccountId=account;
        var permit=new DialogueRecoveryPermit();permit.Observe(state);Check(permit.TryConsume(request,player,account),"Exact initial permit accepted");
        Check(!permit.TryConsume(request,player,account),"Duplicate permit denied");
        var heartbeat=State(account,player,conversation);permit.Observe(heartbeat);var repeat=Choice(heartbeat);repeat.PlayerId=player;repeat.AccountId=account;
        Check(!permit.TryConsume(repeat,player,account),"Heartbeat cannot reopen pending native action");
        var wrongDenial=State(account,player,conversation);wrongDenial.Phase="denied";wrongDenial.RequestId=Guid.NewGuid().ToString("N");wrongDenial.Choices=Array.Empty<DialogueRecoveryChoice>();permit.Observe(wrongDenial);
        Check(!permit.TryConsume(repeat,player,account),"Unrelated denial cannot reopen permit");
        wrongDenial.RequestId=request.RequestId;permit.Observe(wrongDenial);Check(permit.TryConsume(repeat,player,account),"Exact denied request may retry current offered state");
        var complete=State(account,player,conversation,9);complete.Phase="complete";complete.Choices=Array.Empty<DialogueRecoveryChoice>();permit.Observe(complete);Check(permit.State==null,"Complete clears permit even after native step advances");
        var fence=new DialogueRecoveryLoadFence();long prior=fence.BeginSnapshot();fence.Begin(10);Check(!fence.CanLoad(10),"Active recovery fences load");
        bool blocked=false;try{fence.BeginSnapshot();}catch(InvalidDataException){blocked=true;}Check(blocked,"Active recovery rejects capture");
        fence.Complete();fence.CommitSnapshot(11,prior);Check(!fence.CanLoad(11),"Pre-recovery capture cannot clear completed fence");
        long fresh=fence.BeginSnapshot();fence.CommitSnapshot(11,fresh);Check(fence.CanLoad(11),"Post-completion capture clears fence");
        Check(WorldEventFlow.IsControlKind(DialogueRecoveryProtocol.EventKind)&&WorldEventFlow.IsOrderingBarrier(new WorldEvent{Kind=DialogueRecoveryProtocol.EventKind}),"Recovery follows bounded control and global ordering lanes");
    }
}
