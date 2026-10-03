using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using NivalisTogether.Dedicated;
using NivalisTogether.Server;

public static class AccountServiceTests
{
    private static int checks;
    private static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
    private sealed class Client:IDisposable
    {
        public DedicatedConnection Connection;
        public ServerHello Hello;
        public readonly List<WireMessage> Received=new List<WireMessage>();
        public void Pump(){Connection.Tick();while(Connection.TryReceive(out var m))Received.Add(m);}
        public WireMessage Take(string type){var m=Received.FirstOrDefault(x=>x.Type==type);if(m!=null)Received.Remove(m);return m;}
        public void Dispose()=>Connection.Dispose();
    }
    private sealed class Fixture:IDisposable
    {
        public DedicatedService Service;
        public readonly Options Options;
        public readonly List<Client> Clients=new List<Client>();
        public Fixture(string root){var l=new TcpListener(IPAddress.Loopback,0);l.Start();int port=((IPEndPoint)l.LocalEndpoint).Port;l.Stop();Options=new Options{Bind=IPAddress.Loopback,Port=port,SaveRoot=root,AuthoritySecret="test-worker-secret-32-characters",Discovery=false};Service=new DedicatedService(Options);}
        public void Pump(){Service.Tick();foreach(var c in Clients)c.Pump();Thread.Sleep(1);}
        public WireMessage Wait(Client c,string kind,int ms=10000){var until=Stopwatch.StartNew();while(until.ElapsedMilliseconds<ms){Pump();var m=c.Take(kind);if(m!=null)return m;if(!c.Connection.Connected)throw new Exception("Disconnected waiting for "+kind+": "+c.Connection.Status);}throw new Exception("Timeout waiting for "+kind);}
        public Client Connect(){var c=new Client{Connection=DedicatedConnection.ConnectAsync("127.0.0.1",Options.Port).GetAwaiter().GetResult()};Clients.Add(c);c.Hello=Wait(c,"serverHello").Read<ServerHello>();return c;}
        public Client Worker(out WorldMetadata world){var c=Connect();var join=DedicatedProtocol.CreateJoin(c.Hello,"worker","test-worker",Options.AuthoritySecret);c.Connection.Send("join",join);c.Connection.SetAuthenticationKey(DedicatedProtocol.DeriveSessionKey(c.Hello,join,Options.AuthoritySecret));world=Wait(c,"joinAccepted").Read<JoinAccepted>().World;Wait(c,"worldLoadComplete");return c;}
        public AccountAuthenticated Authenticate(Client c,string mode,string username,string password){
            var begin=AccountAuthentication.CreateBegin(mode,username);begin.RoomProof=AccountProtocol.RoomProof(c.Hello,begin,"");c.Connection.Send("accountBegin",begin);
            var challenge=Wait(c,"accountChallenge").Read<AccountAuthChallenge>();
            using var auth=AccountAuthentication.CreateClient(c.Hello.AccountIdentity,c.Hello.AccountIdentity.ServerId,c.Hello,begin,challenge,password);
            c.Connection.AuthenticateAccountFrame=m=>{if(!auth.VerifyServerProof(m.Read<AccountAuthenticated>().ServerProof))throw new CryptographicException();return auth.SessionKey;};
            if(mode=="register")c.Connection.Send("accountRegister",new AccountRegistrationProof{Registration=auth.Registration,Proof=auth.Proof});else c.Connection.Send("accountProof",auth.Proof);
            return Wait(c,"accountAuthenticated").Read<AccountAuthenticated>();
        }
        public void Checkpoint(Client worker,AccountWorldState ledger,byte[] native,string request=""){
            byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(ledger,DedicatedProtocol.Json);var t=new AccountDataTransfer();t.StartSend(bytes,ledger.WorldId,ledger.Revision,"ledger");
            while(t.IsSending){t.PumpSend(worker.Connection);Pump();}
            string id=Guid.NewGuid().ToString("N");worker.Connection.Send("snapshotBegin",new SnapshotBegin{TransferId=id,WorldId=ledger.WorldId,Revision=ledger.Revision,Format="nivalis-es2-v1",Length=native.Length,Sha256=Convert.ToHexString(SHA256.HashData(native)),AccountLedgerHash=Convert.ToHexString(SHA256.HashData(bytes)),CheckpointRequestId=request});
            worker.Connection.Send("snapshotChunk",new SnapshotChunk{TransferId=id,Data=native});worker.Connection.Send("snapshotEnd",new SnapshotEnd{TransferId=id});Wait(worker,"snapshotCommitted");
        }
        public void Ready(Client worker,AccountWorldState ledger){worker.Connection.Send("appearanceCatalog",new AccountAppearanceCatalog{Options=new[]{new AppearanceOption{Id="native-model-a",Name="A"},new AppearanceOption{Id="native-model-b",Name="B"}}});worker.Connection.Send("workerReady",new WorkerReady{WorldId=ledger.WorldId,Revision=ledger.Revision,Scene="test-scene"});for(int i=0;i<5;i++)Pump();}
        public AccountPreparation Join(Client client,string appearance,out JoinAccepted joined){client.Connection.Send("accountProfile",new AccountProfileRequest{AppearanceId=appearance});Wait(client,"accountProfileAccepted");client.Connection.Send("accountJoin",new{});joined=Wait(client,"joinAccepted").Read<JoinAccepted>();var w=Clients.First(c=>c.Received.Any(x=>x.Type=="accountPrepare"));return w.Take("accountPrepare").Read<AccountPreparation>();}
        public AccountState Load(Client client,string world,long revision){var transfer=new AccountDataTransfer();transfer.BeginReceive(Wait(client,"accountDataBegin").Read<AccountDataBegin>(),world,"account");while(transfer.IsReceiving){Pump();WireMessage chunk;while((chunk=client.Take("accountDataChunk"))!=null)transfer.ReceiveChunk(chunk.Read<SnapshotChunk>());var end=client.Take("accountDataEnd");if(end!=null)transfer.EndReceive(end.Read<SnapshotEnd>());}Wait(client,"worldLoadComplete");var account=JsonSerializer.Deserialize<AccountState>(transfer.CompletedBytes,DedicatedProtocol.Json);client.Connection.Send("clientReady",new WorldLoadComplete{WorldId=world,Revision=revision});Wait(client,"clientReadyAccepted");return account;}
        public void Dispose(){foreach(var c in Clients)c.Dispose();Service.Dispose();}
    }
    public static int Main(string[] args){try{Run(args[0]);Console.WriteLine("PASS account service "+checks+" assertions");return 0;}catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
    private static void Run(string root)
    {
        TestAuthenticationBoundaries();
        Directory.CreateDirectory(root);string accountId,identity;long finalRevision;string worldId;
        using(var f=new Fixture(root)){
            var worker=f.Worker(out var world);worldId=world.WorldId;identity=worker.Hello.AccountIdentity.ServerId;
            Check(worker.Received.Any(x=>x.Type=="accountDataBegin"),"Worker receives ledger before starting native world");
            var ledger=new AccountWorldState{WorldId=worldId,Revision=1,LegacyEscrow=new AccountInventoryState()};f.Checkpoint(worker,ledger,new byte[]{1,2,3});f.Ready(worker,ledger);
            using var a=f.Connect();var auth=f.Authenticate(a,"register","Test_A","a-long-test-password");accountId=auth.AccountId;
            Check(Guid.TryParseExact(accountId,"N",out _),"Stable account ID issued");Check(auth.AppearanceOptions.Length==2,"Validated catalog returned after auth");
            var prep=f.Join(a,"native-model-a",out var joined);Check(prep.AccountId==accountId&&prep.PlayerId==joined.PlayerId,"Preparation stamps account identity");
            Check(!a.Received.Any(x=>x.Type=="snapshotBegin"),"Uninitialized account cannot receive shared world");
            var token=f.Wait(worker,"checkpointRequested").Read<CheckpointRequest>().RequestId;
            ledger.Revision=2;ledger.Accounts=new[]{new AccountState{AccountId=accountId,StateRevision=1,Initialized=true,Money=123,AppearanceId="native-model-a",Inventory=JsonSerializer.SerializeToElement(new[]{new{Type="starter",LastUpdate=0,Items=new[]{new[]{1,2,3,4}}}})}};ledger.DialogueOwnerAccountId=accountId;
            f.Checkpoint(worker,ledger,new byte[]{4,5,6},token);var own=f.Load(a,worldId,2);Check(own.AccountId==accountId&&own.Money==123,"Own account ledger paired with fresh native world");
            using var b=f.Connect();var bAuth=f.Authenticate(b,"register","test_b","another-long-password");f.Join(b,"native-model-b",out var bJoined);token=f.Wait(worker,"checkpointRequested").Read<CheckpointRequest>().RequestId;
            ledger.Revision=3;ledger.Accounts=ledger.Accounts.Append(new AccountState{AccountId=bAuth.AccountId,StateRevision=1,Initialized=true,Money=456,AppearanceId="native-model-b"}).ToArray();
            f.Checkpoint(worker,ledger,new byte[]{7,8,9},token);var bOwn=f.Load(b,worldId,3);Check(bOwn.AccountId==bAuth.AccountId&&bOwn.Money==456,"Second account receives its own wallet");
            Check(!a.Received.Any(x=>x.Type=="accountDataBegin"),"Existing player never receives another account's bootstrap");
            var credit=f.Wait(worker,"worldFlowCredit").Read<WorldFlowCredit>();Check(credit.BulkBytes>0,"Worker has bounded delivery credit");
            worker.Connection.Send("worldEvent",new WorldEvent{Kind="account-test-private",TargetPlayerId=joined.PlayerId,Data=JsonSerializer.SerializeToElement(new{Money=321})});
            var evt=f.Wait(a,"worldEvent").Read<WorldEvent>();Check(evt.TargetPlayerId==joined.PlayerId,"Private event routes to actor");for(int i=0;i<10;i++)f.Pump();Check(!b.Received.Any(x=>x.Type=="worldEvent"),"Private event excluded from other account");a.Connection.Send("worldEventAck",new WorldEventAcknowledgement{DeliveryIds=new[]{evt.DeliveryId}});
            AssignScene(f,worker,a,new PlayerSceneContext{PlayerId=joined.PlayerId,Generation=1,Scene="test-scene",PropertyId="authoritative-property"});
            AssignScene(f,worker,b,new PlayerSceneContext{PlayerId=bJoined.PlayerId,Generation=1,Scene="other-scene"});
            a.Connection.Send("playerPose",new PlayerPose{ContextGeneration=1,PlayerId=bJoined.PlayerId,AccountId=bAuth.AccountId,AppearanceId="forged",Scene="test-scene",PropertyId="forged-property",Active=true});
            var pose=f.Wait(worker,"playerPose").Read<PlayerPose>();Check(pose.PlayerId==joined.PlayerId&&pose.AccountId==accountId&&pose.AppearanceId=="native-model-a","Client cannot spoof account or appearance");
            Check(pose.PropertyId=="authoritative-property","Service stamps committed property rather than client claim");
            a.Connection.Send("interaction",new InteractionRequest{ContextGeneration=1,PlayerId="forged",AccountId=bAuth.AccountId,RequestId="request",Kind="test",TargetId="target",Data=JsonSerializer.SerializeToElement(new{})});var action=f.Wait(worker,"interaction").Read<InteractionRequest>();Check(action.AccountId==accountId&&action.PlayerId==joined.PlayerId,"Interaction account identity stamped");
            TestSceneRouting(f,worker,a,b,joined.PlayerId,bJoined.PlayerId,pose);
            TestBoatRouting(f,worker,a,b,joined.PlayerId,bJoined.PlayerId,accountId,bAuth.AccountId);
            a.Dispose();for(int i=0;i<5;i++)f.Pump();using var reconnect=f.Connect();var again=f.Authenticate(reconnect,"login","TEST_A","a-long-test-password");Check(again.AccountId==accountId&&again.AppearanceId=="native-model-a","Reconnect restores stable account/profile");
            using var duplicate=f.Connect();bool duplicateRejected=false;try{f.Authenticate(duplicate,"login","test_a","a-long-test-password");}catch{duplicateRejected=!duplicate.Connection.Connected;}Check(duplicateRejected,"Duplicate account session rejected");
            using var wrong=f.Connect();bool wrongRejected=false;try{f.Authenticate(wrong,"login","test_b","incorrect-long-password");}catch{wrongRejected=!wrong.Connection.Connected;}Check(wrongRejected,"Wrong password fails without creating an authenticated session");
            using var unknown=f.Connect();bool unknownRejected=false;try{f.Authenticate(unknown,"login","nonexistent","incorrect-long-password");}catch{unknownRejected=!unknown.Connection.Connected;}Check(unknownRejected,"Synthetic unknown-user challenge cannot authenticate");
            b.Connection.Send("accountDataBegin",new AccountDataBegin {WorldId=worldId,Revision=99,Length=1,TransferId=Guid.NewGuid().ToString("N"),Sha256=new string('0',64)});
            for(int i=0;i<20&&b.Connection.Connected;i++)f.Pump();Check(!b.Connection.Connected,"Player cannot publish inventory/account ledger authority");
            worker.Connection.Send("ping",new{});f.Wait(worker,"pong");Check(worker.Connection.Connected,"Malicious client cannot remove world authority");
            finalRevision=ledger.Revision;
        }
        string credentials=File.ReadAllText(Path.Combine(root,"accounts.json"));Check(!credentials.Contains("a-long-test-password")&&!credentials.Contains("another-long-password"),"No plaintext password persisted");
        var metadata=JsonSerializer.Deserialize<WorldMetadata>(File.ReadAllBytes(Path.Combine(root,"world.json")),DedicatedProtocol.Json);Check(metadata.Revision==finalRevision&&metadata.AccountLedgerFile=="accounts-state-3.json","World manifest atomically references matching ledger revision");
        Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root,metadata.AccountLedgerFile))))==metadata.AccountLedgerHash,"Persisted account ledger hash matches");
        Check(JsonSerializer.Deserialize<AccountWorldState>(File.ReadAllBytes(Path.Combine(root,metadata.AccountLedgerFile)),DedicatedProtocol.Json).DialogueOwnerAccountId==accountId,"Conversation account owner persists with native checkpoint");
        // An unreferenced crash-orphan sidecar must never supersede the committed manifest.
        File.WriteAllText(Path.Combine(root,"accounts-state-999.json"),"partial orphan");
        using(var f=new Fixture(root)){var worker=f.Worker(out var world);Check(worker.Hello.AccountIdentity.ServerId==identity,"Server pin survives restart");Check(world.Revision==finalRevision,"Native world revision survives restart");using var client=f.Connect();var login=f.Authenticate(client,"login","test_a","a-long-test-password");Check(login.AccountId==accountId,"Account ID survives service restart");
            using var first=f.Connect();first.Connection.Send("statusQuery",new{});var one=f.Wait(first,"serverStatus").Read<ServerStatus>();
            using var second=f.Connect();second.Connection.Send("statusQuery",new{});var two=f.Wait(second,"serverStatus").Read<ServerStatus>();
            Check(Guid.TryParseExact(one.ServerInstanceId,"N",out _)&&one.ServerInstanceId==two.ServerInstanceId,"Status connections share per-process instance identity");
            string id=Guid.NewGuid().ToString("N");worker.Connection.Send("snapshotBegin",new SnapshotBegin{TransferId=id,WorldId=worldId,Revision=4,Format="nivalis-es2-v1",Length=3,Sha256=Convert.ToHexString(SHA256.HashData(new byte[]{9,9,9})),AccountLedgerHash=new string('0',64)});
            for(int i=0;i<20&&worker.Connection.Connected;i++)f.Pump();Check(!worker.Connection.Connected,"Native checkpoint without matching account sidecar rejected");
            var unchanged=JsonSerializer.Deserialize<WorldMetadata>(File.ReadAllBytes(Path.Combine(root,"world.json")),DedicatedProtocol.Json);Check(unchanged.Revision==3,"Rejected checkpoint preserves durable paired revision");
        }
        TestLegacyMigration(root+"-legacy");
        TestMapControlGuards(root+"-map-guards");
        TestReceiptDiagnostics(root+"-receipt-guards");
        var duplicateProperty=new AccountWorldState{WorldId=worldId,Revision=1,Accounts=new[]{new AccountState{AccountId=accountId}},Owners=new[]{new PropertyOwner{Kind="apartment",PropertyId="same-native-guid",OwnerAccountId=accountId},new PropertyOwner{Kind="business",PropertyId="same-native-guid",OwnerAccountId=accountId}}};
        bool ownerRejected=false;try{AccountProtocol.Validate(duplicateProperty,worldId,1);}catch(InvalidDataException){ownerRejected=true;}Check(ownerRejected,"One native property cannot have two kind/owner records");
        duplicateProperty.Owners=Array.Empty<PropertyOwner>();duplicateProperty.DialogueOwnerAccountId=Guid.NewGuid().ToString("N");bool dialogueRejected=false;try{AccountProtocol.Validate(duplicateProperty,worldId,1);}catch(InvalidDataException){dialogueRejected=true;}Check(dialogueRejected,"Unknown persistent conversation owner rejected");
    }
    private static void PumpFor(Fixture fixture,int milliseconds=60)
    {var timer=Stopwatch.StartNew();while(timer.ElapsedMilliseconds<milliseconds)fixture.Pump();}
    private static void AssignScene(Fixture fixture,Client worker,Client target,PlayerSceneContext context)
    {
        worker.Connection.Send("worldEvent",new WorldEvent{Kind=PlayerSceneContext.EventKind,TargetPlayerId=context.PlayerId,Data=JsonSerializer.SerializeToElement(context,DedicatedProtocol.Json)});
        var update=fixture.Wait(target,"worldEvent").Read<WorldEvent>();
        Check(update.Kind==PlayerSceneContext.EventKind&&update.TargetPlayerId==context.PlayerId,"Context assignment delivered privately");
        target.Connection.Send("worldEventAck",new WorldEventAcknowledgement{DeliveryIds=new[]{update.DeliveryId}});PumpFor(fixture);
    }
    private static void TestSceneRouting(Fixture f,Client worker,Client a,Client b,string aid,string bid,PlayerPose original)
    {
        Check(!a.Received.Any(m=>m.Type=="worldEvent")&&!b.Received.Any(m=>m.Type=="worldEvent"),"Scene assignments do not leak to another account");
        a.Connection.Send("playerPose",new PlayerPose{ContextGeneration=1,Scene="other-scene",Active=true});PumpFor(f);
        Check(worker.Take("playerPose")==null,"Forged destination scene never reaches worker");
        string epoch=Guid.NewGuid().ToString("N");
        AssignScene(f,worker,a,new PlayerSceneContext{PlayerId=aid,Generation=2,Scene="test-scene",PropertyId="authoritative-property",TravelPending=true,Epoch=epoch});
        a.Connection.Send("playerPose",new PlayerPose{ContextGeneration=2,Scene="test-scene",Active=true});PumpFor(f);
        Check(worker.Take("playerPose")==null,"Traveling player movement is fenced");
        a.Connection.Send("interaction",new InteractionRequest{ContextGeneration=2,Kind="shop-buy",TargetId="target",RequestId="during-travel",Data=JsonSerializer.SerializeToElement(new{})});f.Wait(a,"playerSceneRejected");
        Check(worker.Take("interaction")==null&&a.Connection.Connected,"Travel-time transaction denied without disconnect");
        a.Connection.Send("interaction",new InteractionRequest{ContextGeneration=2,Kind="group-travel-prepared",TargetId=epoch,RequestId="prepared",Data=JsonSerializer.SerializeToElement(new{})});
        Check(f.Wait(worker,"interaction").Read<InteractionRequest>().PlayerId==aid,"Issued private travel acknowledgement routed");
        a.Connection.Send("interaction",new InteractionRequest{ContextGeneration=2,Kind="group-travel-arrived",TargetId=Guid.NewGuid().ToString("N"),RequestId="forged-epoch",Data=JsonSerializer.SerializeToElement(new{})});f.Wait(a,"playerSceneRejected");
        Check(worker.Take("interaction")==null,"Different route epoch cannot acknowledge travel");
        b.Connection.Send("playerPose",new PlayerPose{ContextGeneration=1,Scene="other-scene",Active=true,X=2});
        var other=f.Wait(worker,"playerPose").Read<PlayerPose>();Check(other.PlayerId==bid&&other.Scene=="other-scene","Other player stays active in own map during private travel");
        worker.Connection.Send("worldEvent",new WorldEvent{Kind="group-travel",TargetPlayerId=aid,Data=JsonSerializer.SerializeToElement(new{PlayerId=aid,Phase="prepare",Epoch=epoch,Participants=new[]{aid}})});
        var route=f.Wait(a,"worldEvent").Read<WorldEvent>();a.Connection.Send("worldEventAck",new WorldEventAcknowledgement{DeliveryIds=new[]{route.DeliveryId}});PumpFor(f);
        Check(route.TargetPlayerId==aid&&!b.Received.Any(m=>m.Type=="worldEvent"),"Private travel control does not teleport another client");
        string apartmentEpoch=Guid.NewGuid().ToString("N"),apartmentId=Guid.NewGuid().ToString("N");
        AssignScene(f,worker,b,new PlayerSceneContext{PlayerId=bid,Generation=2,Scene="other-scene",TravelPending=true,Epoch=apartmentEpoch});
        worker.Connection.Send("worldEvent",new WorldEvent{Kind="group-apartment",TargetPlayerId=bid,Data=JsonSerializer.SerializeToElement(new GroupApartmentEvent{Sequence=1,Epoch=apartmentEpoch,PlayerId=bid,Phase="prepare",ApartmentId=apartmentId,Scene="other-scene",Enter=true,Participants=new[]{bid}},DedicatedProtocol.Json)});
        var apartment=f.Wait(b,"worldEvent").Read<WorldEvent>();b.Connection.Send("worldEventAck",new WorldEventAcknowledgement{DeliveryIds=new[]{apartment.DeliveryId}});PumpFor(f);
        Check(apartment.Kind=="group-apartment"&&!a.Received.Any(m=>m.Type=="worldEvent")&&a.Connection.Connected,"Another map's apartment transition stays private during district travel");
        AssignScene(f,worker,b,new PlayerSceneContext{PlayerId=bid,Generation=3,Scene="other-scene",PropertyId=apartmentId});
        AssignScene(f,worker,a,new PlayerSceneContext{PlayerId=aid,Generation=3,Scene="other-scene"});
        a.Received.RemoveAll(m=>m.Type=="playerPose");b.Received.RemoveAll(m=>m.Type=="playerPose");
        worker.Connection.Send("playerPose",original);PumpFor(f);
        Check(!a.Received.Any(m=>m.Type=="playerPose")&&!b.Received.Any(m=>m.Type=="playerPose"),"Stale source-map worker echo cannot revive departed avatar");
        a.Connection.Send("playerPose",new PlayerPose{ContextGeneration=3,Scene="other-scene",Active=true,X=3});
        var arrived=f.Wait(worker,"playerPose").Read<PlayerPose>();worker.Connection.Send("playerPose",arrived);
        Check(f.Wait(b,"playerPose").Read<PlayerPose>().ContextGeneration==3,"Committed destination pose visible to peers");
        worker.Connection.Send("worldEvent",new WorldEvent{Kind="npcs",Data=JsonSerializer.SerializeToElement(new{GlobalRoster=true})});
        var globalA=f.Wait(a,"worldEvent").Read<WorldEvent>();var globalB=f.Wait(b,"worldEvent").Read<WorldEvent>();
        Check(globalA.Kind=="npcs"&&globalB.Kind=="npcs","Global ghost roster continues reaching every loaded map");
        a.Connection.Send("worldEventAck",new WorldEventAcknowledgement{DeliveryIds=new[]{globalA.DeliveryId}});b.Connection.Send("worldEventAck",new WorldEventAcknowledgement{DeliveryIds=new[]{globalB.DeliveryId}});
        worker.Connection.Send("worldEvent",new WorldEvent{Kind="player-map-error",TargetPlayerId=aid,Data=JsonSerializer.SerializeToElement(new ProtocolError{Code="player_map_failed",Message="Isolated test map failure."},DedicatedProtocol.Json)});
        var error=f.Wait(a,"worldEvent").Read<WorldEvent>();a.Connection.Send("worldEventAck",new WorldEventAcknowledgement{DeliveryIds=new[]{error.DeliveryId}});PumpFor(f);
        Check(error.Kind=="player-map-error"&&!b.Received.Any(m=>m.Type=="worldEvent")&&b.Connection.Connected,"Map failure reaches only the affected character");
        Check(worker.Connection.Connected,"Scene routing preserves world authority");
    }
    private static void TestMapControlGuards(string root)
    {
        var invalid=new[]{
            new WorldEvent{Kind="group-travel",Data=JsonSerializer.SerializeToElement(new{Phase="prepare",PlayerId="",Epoch=Guid.NewGuid().ToString("N")})},
            new WorldEvent{Kind="player-map-error",Data=JsonSerializer.SerializeToElement(new ProtocolError{Code="player_map_failed",Message="Cannot broadcast a private failure."})},
            new WorldEvent{Kind=PlayerSceneContext.EventKind,TargetPlayerId=Guid.NewGuid().ToString("N"),Data=JsonSerializer.SerializeToElement(new PlayerSceneContext{PlayerId=Guid.NewGuid().ToString("N"),Generation=1,Scene="test-scene"})}
        };
        for(int i=0;i<invalid.Length;i++)
        {
            using var f=new Fixture(root+"-"+i);var worker=f.Worker(out var world);
            var ledger=new AccountWorldState{WorldId=world.WorldId,Revision=1,LegacyEscrow=new AccountInventoryState()};
            f.Checkpoint(worker,ledger,new byte[]{1,2,3});f.Ready(worker,ledger);
            worker.Connection.Send("worldEvent",invalid[i]);PumpFor(f);
            Check(!worker.Connection.Connected,"Invalid map authority rejected at service boundary: "+invalid[i].Kind);
            var persisted=JsonSerializer.Deserialize<WorldMetadata>(File.ReadAllBytes(Path.Combine(f.Options.SaveRoot,"world.json")),DedicatedProtocol.Json);
            Check(persisted.Revision==1,"Rejected map control cannot change committed native/account world");
        }
    }
    private static void TestBoatRouting(Fixture f,Client worker,Client a,Client b,string aid,string bid,string accountA,string accountB)
    {
        // Use the real authenticated TCP service. Ownership of BoatId is checked
        // independently by worker authority; this boundary binds sender and map.
        string boatA=Guid.NewGuid().ToString("N"),boatB=Guid.NewGuid().ToString("N");
        BoatPose Boat(string boat,string scene,long generation,long sequence)=>new BoatPose{
            PlayerId=bid,AccountId=accountB,BoatId=boat,Scene=scene,ContextGeneration=generation,Sequence=sequence,
            X=12,Y=3,Z=-7,Qw=1,Fuel=55,AppliedFuelRevision=17,Boarded=true,Driving=true};
        Check(worker.Take(BoatProtocol.PoseKind)==null,"Boat routing begins with no queued movement");
        a.Connection.Send(BoatProtocol.PoseKind,Boat(boatA,"other-scene",3,1));
        var forwarded=f.Wait(worker,BoatProtocol.PoseKind).Read<BoatPose>();
        Check(forwarded.PlayerId==aid&&forwarded.AccountId==accountA,"Boat pose spoofed player/account identities overwritten with authenticated sender");
        Check(forwarded.AppliedFuelRevision==17,"Authenticated boat forwarding preserves the native-applied fuel revision for worker validation");
        Check(forwarded.BoatId==boatA&&forwarded.Scene=="other-scene"&&forwarded.ContextGeneration==3&&forwarded.Sequence==1&&
            forwarded.X==12&&forwarded.Fuel==55&&forwarded.Boarded&&forwarded.Driving,"Valid public-map boat pose reaches worker without losing movement payload");
        PumpFor(f,110);
        a.Connection.Send(BoatProtocol.PoseKind,Boat(boatA,"forged-scene",3,2));PumpFor(f);
        Check(worker.Take(BoatProtocol.PoseKind)==null&&a.Connection.Connected,"Wrong-scene boat movement is dropped without disconnect");
        a.Connection.Send(BoatProtocol.PoseKind,Boat(boatA,"other-scene",2,3));PumpFor(f);
        Check(worker.Take(BoatProtocol.PoseKind)==null,"Stale generation boat movement never reaches worker");
        a.Connection.Send(BoatProtocol.PoseKind,Boat(boatA,"other-scene",4,4));PumpFor(f);
        Check(worker.Take(BoatProtocol.PoseKind)==null,"Client cannot choose its next map generation for boat movement");
        // b is in an apartment on other-scene after TestSceneRouting; a boat
        // claiming the public scene does not confer public-map membership.
        b.Connection.Send(BoatProtocol.PoseKind,Boat(boatB,"other-scene",3,1));PumpFor(f);
        Check(worker.Take(BoatProtocol.PoseKind)==null&&b.Connection.Connected,"Apartment membership fences boat movement even with matching scene and generation");
        AssignScene(f,worker,a,new PlayerSceneContext{PlayerId=aid,Generation=4,Scene="other-scene",TravelPending=true,Epoch=Guid.NewGuid().ToString("N")});
        a.Connection.Send(BoatProtocol.PoseKind,Boat(boatA,"other-scene",4,5));PumpFor(f);
        Check(worker.Take(BoatProtocol.PoseKind)==null,"Pending private travel cannot publish boat movement");
        AssignScene(f,worker,a,new PlayerSceneContext{PlayerId=aid,Generation=5,Scene="boat-destination"});
        a.Connection.Send(BoatProtocol.PoseKind,Boat(boatA,"other-scene",4,6));PumpFor(f);
        Check(worker.Take(BoatProtocol.PoseKind)==null,"Old-map boat pose remains fenced after arrival");
        // Both frames enter one service tick, whose shared timestamp makes the
        // throttle deterministic without relying on host scheduling latency.
        a.Connection.Send(BoatProtocol.PoseKind,Boat(boatA,"boat-destination",5,7));
        a.Connection.Send(BoatProtocol.PoseKind,Boat(boatA,"boat-destination",5,8));PumpFor(f,110);
        forwarded=worker.Take(BoatProtocol.PoseKind)?.Read<BoatPose>();
        Check(forwarded?.Sequence==7&&forwarded.PlayerId==aid&&forwarded.Scene=="boat-destination","Committed destination permits valid own boat movement again");
        Check(worker.Take(BoatProtocol.PoseKind)==null,"Boat movement burst forwards at most one pose within the service throttle window");
        var later=Boat(boatA,"boat-destination",5,9);later.PlayerId="malformed-spoof";later.AccountId="malformed-spoof";
        a.Connection.Send(BoatProtocol.PoseKind,later);
        forwarded=f.Wait(worker,BoatProtocol.PoseKind).Read<BoatPose>();
        Check(forwarded.Sequence==9&&forwarded.PlayerId==aid&&forwarded.AccountId==accountA,"Authentication stamping precedes validation of untrusted identity fields");
        var malformed=Boat(boatA,"boat-destination",5,10);malformed.Qw=0;
        a.Connection.Send(BoatProtocol.PoseKind,malformed);PumpFor(f,110);
        Check(!a.Connection.Connected&&worker.Take(BoatProtocol.PoseKind)==null,"Malformed boat quaternion is rejected before worker forwarding, even during throttle");
        Check(worker.Connection.Connected&&b.Connection.Connected,"Malformed boat sender cannot remove worker or another authenticated player");
        worker.Connection.Send("ping",new{});f.Wait(worker,"pong");
    }
    private static void TestReceiptDiagnostics(string root)
    {
        using var f=new Fixture(root);var worker=f.Worker(out var world);
        var ledger=new AccountWorldState{WorldId=world.WorldId,Revision=1,LegacyEscrow=new AccountInventoryState()};
        f.Checkpoint(worker,ledger,new byte[]{1,2,3});f.Ready(worker,ledger);
        using var client=f.Connect();var account=f.Authenticate(client,"register","receipt_probe","receipt-probe-password");
        f.Join(client,"native-model-a",out var joined);string token=f.Wait(worker,"checkpointRequested").Read<CheckpointRequest>().RequestId;
        ledger.Revision=2;ledger.Accounts=new[]{new AccountState{AccountId=account.AccountId,Initialized=true,AppearanceId="native-model-a"}};
        f.Checkpoint(worker,ledger,new byte[]{4,5,6},token);f.Load(client,world.WorldId,2);
        const string secretMarker="NEVER_LOG_RECEIPT_PAYLOAD";
        var delivered=new List<WorldEvent>();
        for(int index=0;index<11;index++)
        {
            worker.Connection.Send("worldEvent",new WorldEvent{Kind=index%2==0?"npcs":"business-state-chunk",
                Data=JsonSerializer.SerializeToElement(new{Sequence=100L+index,PrivateTestPayload=secretMarker})});
            delivered.Add(f.Wait(client,"worldEvent").Read<WorldEvent>());
        }
        client.Connection.Send("worldEventAck",new WorldEventAcknowledgement{DeliveryIds=new[]{delivered[0].DeliveryId,delivered[2].DeliveryId}});PumpFor(f);
        // Advance only receipt timestamps in this synthetic service fixture;
        // native clocks, production servers and actual wait limits are unchanged.
        var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Public;
        var peers=(System.Collections.IEnumerable)typeof(DedicatedService).GetField("_peers",flags).GetValue(f.Service);
        object target=null;foreach(var peer in peers)if((string)peer.GetType().GetField("Id",flags).GetValue(peer)==joined.PlayerId)target=peer;
        var pending=(System.Collections.IDictionary)target.GetType().GetField("Deliveries",flags).GetValue(target);
        Check(pending.Count==9,"Receipt diagnostics fixture retains only unacknowledged deliveries");
        foreach(System.Collections.DictionaryEntry entry in pending)
            entry.Value.GetType().GetField("Sent",flags).SetValue(entry.Value,DateTime.UtcNow.AddSeconds(-WorldEventFlow.StallSeconds-2));
        var output=new StringWriter();var original=Console.Out;
        try{Console.SetOut(output);PumpFor(f);}finally{Console.SetOut(original);}
        string diagnostic=output.ToString();
        Check(!client.Connection.Connected&&worker.Connection.Connected,"Expired receipt disconnects only its player");
        Check(diagnostic.Contains("Slow client receipts pending=9")&&diagnostic.Contains("omitted=1"),"Receipt diagnostic lists at most eight outstanding records");
        Check(diagnostic.Contains("id="+delivered[1].DeliveryId+",kind=business-state-chunk,sequence=101,ageSeconds="),"Oldest receipt logs delivery ID, kind, producer sequence and age");
        Check(diagnostic.Contains("id="+delivered[3].DeliveryId+",kind=business-state-chunk,sequence=103,ageSeconds="),"Subsequent pending receipt metadata remains correlated");
        Check(!diagnostic.Contains("id="+delivered[0].DeliveryId+",kind=")&&!diagnostic.Contains("id="+delivered[2].DeliveryId+",kind="),"Acknowledged receipts are excluded from timeout diagnostics");
        Check(!diagnostic.Contains(secretMarker)&&!diagnostic.Contains("PrivateTestPayload"),"Receipt diagnostics never contain payload contents");
    }
    private static void TestAuthenticationBoundaries()
    {
        foreach(string type in new[]{"join","accountProof","accountRegister"})foreach(bool signed in new[]{false,true}){
            var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
            try{
                using var client=DedicatedConnection.ConnectAsync("127.0.0.1",((IPEndPoint)listener.LocalEndpoint).Port).GetAwaiter().GetResult();
                using var server=DedicatedConnection.FromAccepted(listener.AcceptTcpClient());
                server.Send("serverHello",new ServerHello {SessionId=server.SessionId,Nonce=Guid.NewGuid().ToString("N")});
                WireMessage hello=null;for(int i=0;i<100&&hello==null;i++){server.Tick();client.Tick();client.TryReceive(out hello);Thread.Sleep(1);}
                byte[] key=RandomNumberGenerator.GetBytes(32);client.Send(type,new{});if(signed)client.SetAuthenticationKey(key);client.Send("afterAuthentication",new{});client.Tick();
                WireMessage terminal=null;for(int i=0;i<100&&terminal==null;i++){server.Tick();server.TryReceive(out terminal);Thread.Sleep(1);}
                Check(terminal?.Type==type,"Terminal auth frame received");Check(!server.TryReceive(out _),"Coalesced successor remains undecoded before auth dispatch");
                server.SetAuthenticationKey(key);WireMessage next=null;for(int i=0;i<100&&server.Connected&&next==null;i++){server.Tick();server.TryReceive(out next);Thread.Sleep(1);}
                Check(signed?server.Connected&&next?.Type=="afterAuthentication":!server.Connected&&next==null,"Post-auth successor requires correct MAC");
                CryptographicOperations.ZeroMemory(key);
            }finally{listener.Stop();}
        }
    }
    private static void TestLegacyMigration(string root)
    {
        Directory.CreateDirectory(root);byte[] oldWorld={12,34,56,78};
        var old=new WorldMetadata {WorldId=Guid.NewGuid().ToString("N"),Revision=17,CreatedUtc=DateTime.UtcNow.ToString("O"),SnapshotFile="snapshot-17.bin",SnapshotFormat="nivalis-es2-v1",SnapshotLength=oldWorld.Length,SnapshotHash=Convert.ToHexString(SHA256.HashData(oldWorld))};
        File.WriteAllBytes(Path.Combine(root,old.SnapshotFile),oldWorld);File.WriteAllBytes(Path.Combine(root,"world.json"),JsonSerializer.SerializeToUtf8Bytes(old,DedicatedProtocol.Json));
        using(var f=new Fixture(root)){
            var worker=f.Worker(out var imported);Check(imported.Revision==17,"Legacy world revision is preserved");
            var transfer=new AccountDataTransfer();transfer.BeginReceive(worker.Take("accountDataBegin").Read<AccountDataBegin>(),old.WorldId,"ledger");WireMessage chunk;while((chunk=worker.Take("accountDataChunk"))!=null)transfer.ReceiveChunk(chunk.Read<SnapshotChunk>());transfer.EndReceive(worker.Take("accountDataEnd").Read<SnapshotEnd>());
            var ledger=JsonSerializer.Deserialize<AccountWorldState>(transfer.CompletedBytes,DedicatedProtocol.Json);Check(ledger.Accounts.Length==0&&ledger.Migration==AccountProtocol.Migration&&ledger.Revision==17,"Legacy bootstrap awards no shared assets to an account");
            Check(worker.Received.First(x=>x.Type=="snapshotBegin").Read<SnapshotBegin>().AccountLedgerHash==transfer.Completed.Sha256,"Legacy native snapshot binds synthesized ledger hash");
            Check(File.ReadAllBytes(Path.Combine(root,"legacy-escrow",old.SnapshotFile)).SequenceEqual(oldWorld),"Legacy escrow preserves exact native bytes");
            ledger.Revision=18;ledger.LegacyEscrow=new AccountInventoryState{Money=789,Inventory=JsonSerializer.SerializeToElement(new[]{new{Type="legacy",LastUpdate=0,Items=new[]{new[]{5,6,7,8}}}})};f.Checkpoint(worker,ledger,new byte[]{90,91,92});
        }
        using(var f=new Fixture(root)){f.Worker(out var restored);Check(restored.Revision==18,"Migrated ledger and native world restart at same revision");Check(File.ReadAllBytes(Path.Combine(root,"legacy-escrow",old.SnapshotFile)).SequenceEqual(oldWorld),"Later commits leave escrow unchanged");var state=JsonSerializer.Deserialize<AccountWorldState>(File.ReadAllBytes(Path.Combine(root,restored.AccountLedgerFile)),DedicatedProtocol.Json);Check(state.LegacyEscrow.Money==789&&state.Accounts.Length==0,"Legacy income ledger survives without creating a fake account");}
    }
}
