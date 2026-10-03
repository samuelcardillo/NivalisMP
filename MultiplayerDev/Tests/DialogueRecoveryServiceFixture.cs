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

public static partial class DialogueRecoveryServiceTests
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
    public static int Main(string[] args){try{RunRecovery(args[0]);Console.WriteLine("PASS dialogue recovery service "+checks+" assertions");return 0;}catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
}
