using System;
using System.IO;
using NivalisTogether.Dedicated;

internal static class ApartmentJoinFenceEntry
{
    public static int Main(string[] args) => SleepRecoveryTests.RunApartmentJoinTests(args);
}

// Reuse the existing independent authenticated TCP fixture, never Unity or a
// production server. Native property transitions are synthetic protocol inputs.
internal static partial class SleepRecoveryTests
{
    public static int RunApartmentJoinTests(string[] args)
    {
        if (args.Length != 3) return 2;
        serviceAssembly = Path.GetFullPath(args[0]); dotnet = args[1]; artifacts = Path.GetFullPath(args[2]); Directory.CreateDirectory(artifacts);
        try
        {
            ApartmentFencePolicy(); OldApartmentImportAndPendingJoin(); ApartmentReadyBeforePrepare(); ApartmentCheckpointCorrelation(); ApartmentInvalidCheckpoint();
            string result = "PASS: " + assertions + " assertions in " + scenarios.Count + " apartment join-fence scenarios. Isolated service/raw authenticated TCP; no native gameplay.";
            Console.WriteLine(result); File.WriteAllText(Path.Combine(artifacts,"result.txt"),result+Environment.NewLine+string.Join(Environment.NewLine,scenarios)); return 0;
        }
        catch(Exception ex) { Console.Error.WriteLine(ex); File.WriteAllText(Path.Combine(artifacts,"result.txt"),"FAIL after "+assertions+" assertions\n"+ex);return 1; }
    }
    private static GroupApartmentEvent ApartmentState(string participant) => new GroupApartmentEvent {
        Sequence=1,Epoch=Guid.NewGuid().ToString("N"),ApartmentId=Guid.NewGuid().ToString("N"),Scene="sleep-recovery-test",Phase="prepare",Enter=true,
        Participants=new[]{participant},RequestId=Guid.NewGuid().ToString("N") };
    private static void Rejected(Action action,string description)
    { try { action(); } catch(InvalidDataException) { Check(true,description);return; } throw new Exception("Expected rejection: "+description); }
    private static void ApartmentFencePolicy()
    {
        var fence=new ApartmentLoadFence();var state=ApartmentState(Guid.NewGuid().ToString("N"));
        Check(fence.CanLoad(1),"ordinary initial checkpoint remains loadable");
        long olderBegin=fence.BeginSnapshot(2);
        Check(fence.Observe(state,2),"prepare opens one new property generation");
        Check(fence.Generation==1&&fence.MinimumRevision==3&&!fence.CanLoad(2)&&!fence.CanLoad(99),"active transition blocks all snapshot revisions");
        Rejected(()=>fence.BeginSnapshot(3),"capture cannot begin inside native transition");
        Rejected(()=>fence.Observe(state,2),"replayed phase sequence rejected");
        state.Sequence++;state.Phase="complete";Rejected(()=>fence.Observe(state,2),"complete requires native execute phase");
        state.Phase="execute";fence.Observe(state,2);
        state.Sequence++;state.Phase="cancel";Rejected(()=>fence.Observe(state,2),"native execution cannot be canceled as unmodified");
        state.Phase="complete";fence.Observe(state,2);
        Check(!fence.TransitionActive&&fence.AwaitingFreshCheckpoint&&!fence.CanLoad(3),"complete alone cannot release old world");
        Rejected(()=>fence.CommitSnapshot(3,olderBegin),"snapshot begun before transition remains invalid after complete");
        long fresh=fence.BeginSnapshot(3);fence.CommitSnapshot(3,fresh);
        Check(fence.CanLoad(3)&&!fence.CanLoad(2),"fresh commit unlocks current load but minimum revision persists");
        state=ApartmentState(Guid.NewGuid().ToString("N"));state.Sequence=4;fence.Observe(state,3);
        state.Sequence++;state.Phase="cancel";fence.Observe(state,3);
        Check(!fence.CanLoad(3)&&!fence.CanLoad(4),"cancel also requires newly captured checkpoint");
        fresh=fence.BeginSnapshot(4);fence.CommitSnapshot(4,fresh);
        Check(fence.CanLoad(4)&&!fence.CanLoad(3),"canceled generation cannot revive older issued imports");
        fence.Reset();Check(fence.CanLoad(1)&&fence.Generation==0,"new authenticated worker resets lost runtime authority");
        Scenario("persistent revision and captured-generation policy");
    }
    private static RawPeer ApartmentLoadedPlayer(Service server,RawPeer worker,ref long revision,string name)
    {
        var player=server.Join("player",name,Password);string token=ApartmentPending(worker,player);
        Upload(worker,worker.Accepted.World.WorldId,++revision,checkpoint,token);
        var loaded=player.ReadUntil("worldLoadComplete").Read<WorldLoadComplete>();
        player.Send("clientReady",loaded);player.ReadUntil("clientReadyAccepted");
        ApartmentPresence(worker,player.Accepted.PlayerId,true);return player;
    }
    private static void ApartmentPresence(RawPeer worker,string id,bool joined)
    {
        for(int i=0;i<100;i++) { var message=worker.Read(); if(message.Type=="playerPresence") { var value=message.Read<PlayerPresence>();if(value.PlayerId==id&&value.Joined==joined)return; } }
        throw new Exception("Expected apartment participant presence was absent.");
    }
    private static string ApartmentPending(RawPeer worker,RawPeer player)
    {
        string token=null;bool present=false;
        for(int i=0;i<100;i++) {
            var message=worker.Read();
            if(message.Type=="checkpointRequested")token=message.Read<CheckpointRequest>().RequestId;
            else if(message.Type=="sleepRecoveryPresence") { var value=message.Read<PlayerPresence>();if(value.PlayerId==player.Accepted.PlayerId&&value.Joined)present=true; }
            if(token!=null&&present){Check(Guid.TryParseExact(token,"N",out _),"pending apartment join retains correlated fresh checkpoint request");return token;}
        }
        throw new Exception("Pending apartment join notification missing.");
    }
    private static void ApartmentPublish(RawPeer worker,GroupApartmentEvent state,params RawPeer[] receivers)
    {
        worker.Send("worldEvent",new WorldEvent{Kind="group-apartment",Data=Data(state)});
        foreach(var peer in receivers) {
            var value=peer.ReadUntil("worldEvent").Read<WorldEvent>();
            Check(value.Kind=="group-apartment"&&value.Data.GetProperty("Sequence").GetInt64()==state.Sequence,"loaded participant receives ordered apartment phase");
        }
    }
    private static void OldApartmentImportAndPendingJoin()
    {
        using var server=new Service("apartment-old-import");using var worker=ReadyWorker(server);long revision=1;
        using var participant=ApartmentLoadedPlayer(server,worker,ref revision,"Existing party");
        using var stale=server.Join("player","Slow native import",Password);string staleToken=ApartmentPending(worker,stale);
        Upload(worker,worker.Accepted.World.WorldId,++revision,checkpoint,staleToken);
        var oldLoad=stale.ReadUntil("worldLoadComplete").Read<WorldLoadComplete>();
        var state=ApartmentState(participant.Accepted.PlayerId);ApartmentPublish(worker,state,participant);
        stale.ExpectErrorAndClose("apartment_transition_load");
        using var pending=server.Join("player","Post-entry world",Password);string token=ApartmentPending(worker,pending);
        Check(!pending.HasMessage("snapshotBegin",120),"new join during prepare receives no previous property snapshot");
        state.Sequence++;state.Phase="execute";ApartmentPublish(worker,state,participant);
        Check(!pending.HasMessage("worldEvent",100),"pending apartment join receives no native execution event or body");
        state.Sequence++;state.Phase="complete";ApartmentPublish(worker,state,participant);
        Check(!pending.HasMessage("snapshotBegin",120),"completion without fresh commit releases no old snapshot");
        Upload(worker,worker.Accepted.World.WorldId,++revision,checkpoint,token);
        var load=pending.ReadUntil("worldLoadComplete").Read<WorldLoadComplete>();Check(load.Revision>oldLoad.Revision,"post-entry native import uses strictly newer checkpoint");
        pending.Send("clientReady",load);pending.ReadUntil("clientReadyAccepted");ApartmentPresence(worker,pending.Accepted.PlayerId,true);
        Check(server.WaitStatus(s=>s.Players==2).Ready,"existing participant and fresh join remain connected after fence");
        Scenario("in-flight imported old snapshot invalidation and fresh post-complete join");
    }
    private static void ApartmentReadyBeforePrepare()
    {
        using var server=new Service("apartment-client-ready-race");using var worker=ReadyWorker(server);long revision=1;
        using var participant=ApartmentLoadedPlayer(server,worker,ref revision,"Frozen party");
        using var racer=ApartmentLoadedPlayer(server,worker,ref revision,"Ready raced worker presence");
        var state=ApartmentState(participant.Accepted.PlayerId);ApartmentPublish(worker,state,participant);
        racer.ExpectErrorAndClose("apartment_transition_load");
        state.Sequence++;state.Phase="cancel";ApartmentPublish(worker,state,participant);
        Upload(worker,worker.Accepted.World.WorldId,++revision,checkpoint);
        using var pending=server.Join("player","After canceled transition",Password);string token=ApartmentPending(worker,pending);
        Check(!pending.HasMessage("snapshotBegin",80),"ordinary fresh-checkpoint-per-join rule survives canceled transition");
        Upload(worker,worker.Accepted.World.WorldId,++revision,checkpoint,token);
        var load=pending.ReadUntil("worldLoadComplete").Read<WorldLoadComplete>();pending.Send("clientReady",load);pending.ReadUntil("clientReadyAccepted");
        Check(load.Revision==revision,"cancel recovery uses current correlated revision");
        Scenario("ready-before-prepare race excludes nonparticipant and cancel remains fresh");
    }
    private static void ApartmentCheckpointCorrelation()
    {
        using var server=new Service("apartment-correlation");using var worker=ReadyWorker(server);long revision=1;
        using var participant=ApartmentLoadedPlayer(server,worker,ref revision,"Party");var state=ApartmentState(participant.Accepted.PlayerId);ApartmentPublish(worker,state,participant);
        using var first=server.Join("player","First pending",Password);string older=ApartmentPending(worker,first);
        using var second=server.Join("player","Second pending",Password);string current=ApartmentPending(worker,second);
        state.Sequence++;state.Phase="execute";ApartmentPublish(worker,state,participant);state.Sequence++;state.Phase="complete";ApartmentPublish(worker,state,participant);
        Upload(worker,worker.Accepted.World.WorldId,++revision,checkpoint,older);
        Check(!first.HasMessage("snapshotBegin",80)&&!second.HasMessage("snapshotBegin",80),"fresh native revision cannot bypass latest correlated join token");
        Upload(worker,worker.Accepted.World.WorldId,++revision,checkpoint,current);
        Check(first.ReadUntil("worldLoadComplete").Read<WorldLoadComplete>().Revision==revision&&second.ReadUntil("worldLoadComplete").Read<WorldLoadComplete>().Revision==revision,
            "latest correlated fresh checkpoint releases every pending join together");
        Scenario("post-transition checkpoint still obeys correlated join barrier");
    }
    private static void ApartmentInvalidCheckpoint()
    {
        using(var server=new Service("apartment-mid-transition-snapshot")) using(var worker=ReadyWorker(server)) {
            long revision=1;using var player=ApartmentLoadedPlayer(server,worker,ref revision,"Party");var state=ApartmentState(player.Accepted.PlayerId);ApartmentPublish(worker,state,player);
            worker.Send("snapshotBegin",new SnapshotBegin{WorldId=worker.Accepted.World.WorldId,Revision=revision+1,TransferId=Guid.NewGuid().ToString("N"),Length=checkpoint.Length,Sha256=Hash(checkpoint),Format="test"});
            worker.ExpectErrorAndClose("invalid_message");Check(server.WaitStatus(s=>!s.Ready).Ready==false,"mid-transition capture fails authority before durable save");
        }
        Scenario("native mid-transition checkpoint publication rejected");
        using(var server=new Service("apartment-earlier-snapshot")) using(var worker=ReadyWorker(server)) {
            long revision=1;using var player=ApartmentLoadedPlayer(server,worker,ref revision,"Party");
            worker.Send("snapshotBegin",new SnapshotBegin{WorldId=worker.Accepted.World.WorldId,Revision=revision+1,TransferId=Guid.NewGuid().ToString("N"),Length=checkpoint.Length,Sha256=Hash(checkpoint),Format="test"});
            worker.Send("worldEvent",new WorldEvent{Kind="group-apartment",Data=Data(ApartmentState(player.Accepted.PlayerId))});
            worker.ExpectErrorAndClose("invalid_message");
        }
        Scenario("prepare cannot bless an earlier in-flight checkpoint");
    }
}
