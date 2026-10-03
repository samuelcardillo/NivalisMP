using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using NivalisTogether.Dedicated;

namespace NivalisTogether.Server
{
    public static class ServerProgram
    {
        public static int Main(string[] args)
        {
            try
            {
                var options = Options.Parse(args);
                if (options.Help) { Console.WriteLine("Nivalis dedicated service: --bind 0.0.0.0 --port 47778 --name NAME --max-players 4 --save-root PATH [--password VALUE] [--authority-secret VALUE] [--stop-file PATH] [--no-discovery]. Credentials can instead use NIVALIS_SERVER_PASSWORD / NIVALIS_AUTHORITY_SECRET. A real local Unity worker is required for readiness. This service does not simulate gameplay by itself."); return 0; }
                using var service = new DedicatedService(options);
                bool stopping = false; Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping = true; };
                while (!stopping && !service.ConsumeStopRequest()) { service.Tick(); Thread.Sleep(10); }
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine("Server stopped: " + ex.Message); return 1; }
        }
    }

    internal sealed class Options
    {
        public IPAddress Bind = IPAddress.Any;
        public int Port = 47778, MaxPlayers = 4;
        public string Name = "Nivalis Server", SaveRoot = Path.Combine(AppContext.BaseDirectory, "world");
        public string Password = Environment.GetEnvironmentVariable("NIVALIS_SERVER_PASSWORD") ?? "";
        public string AuthoritySecret = Environment.GetEnvironmentVariable("NIVALIS_AUTHORITY_SECRET") ?? "";
        public string StopFile = "";
        public bool Discovery = true, Help;
        public static Options Parse(string[] args)
        {
            var result = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string key = args[i];
                if (key == "--help") { result.Help = true; continue; }
                if (key == "--no-discovery") { result.Discovery = false; continue; }
                if (++i >= args.Length) throw new ArgumentException("Missing value for " + key);
                string value = args[i];
                switch (key)
                {
                    case "--bind": result.Bind = IPAddress.Parse(value); break;
                    case "--port": result.Port = int.Parse(value); break;
                    case "--max-players": result.MaxPlayers = int.Parse(value); break;
                    case "--name": result.Name = value; break;
                    case "--save-root": result.SaveRoot = Path.GetFullPath(value); break;
                    case "--password": result.Password = value; break;
                    case "--authority-secret": result.AuthoritySecret = value; break;
                    case "--stop-file": result.StopFile = value; break;
                    default: throw new ArgumentException("Unknown option " + key);
                }
            }
            if (result.Bind.AddressFamily != AddressFamily.InterNetwork || result.Port < 1 || result.Port > 65534 ||
                result.MaxPlayers < 1 || result.MaxPlayers > 32 || !DedicatedProtocol.Text(result.Name, 80) ||
                result.Password.Length > 128 || (result.Password.Length > 0 && result.Password.Length < 8) ||
                result.AuthoritySecret.Length > 256 || (result.AuthoritySecret.Length > 0 && result.AuthoritySecret.Length < 16))
                throw new ArgumentException("Invalid bind, port, player limit, name or secret length.");
            result.SaveRoot = Path.GetFullPath(result.SaveRoot);
            if (result.StopFile.Length > 0)
            {
                result.StopFile = Path.GetFullPath(result.StopFile);
                if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(result.StopFile)), Path.TrimEndingDirectorySeparator(result.SaveRoot), StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(Path.GetFileName(result.StopFile), "stop-service.request", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Stop file must be save-root/stop-service.request.");
            }
            return result;
        }
    }

    internal sealed partial class DedicatedService : IDisposable
    {
        // This is only the per-player sent-but-unapplied allowance. The worker's
        // ingress window and each player's queued history retain their own bounds.
        internal const int PlayerBulkWindowBytes = 1024 * 1024;
        // Client managed preparation retains at most256 ordered world messages.
        // A byte window alone would admit512 minimum-cost events into that FIFO.
        internal const int PlayerMaxInFlightEvents = 192;
        private sealed class Peer
        {
            public DedicatedConnection Connection;
            public ServerHello Hello;
            public string Role = "", Id = Guid.NewGuid().ToString("N"), Name = "";
            public AccountProfile Account;
            public AccountAuthChallenge Challenge;
            public AccountVerifier Verifier;
            public bool ExistingAccount;
            public DateTime AuthStarted;
            public readonly AccountDataTransfer AccountOutgoing = new AccountDataTransfer();
            public bool EmptyWorldPending;
            public string LastMessageType = "", LastWorldKind = "";
            public int LastFrameBytes;
            public DateTime LastStalePoseLog;
            public DateTime Created = DateTime.UtcNow, LastPose, LastBoatPose, RateWindow = DateTime.UtcNow, LastPing = DateTime.UtcNow, LoadStarted;
            public int Requests, Interactions;
            public bool Loaded, AwaitingCheckpoint;
            public GroupSleepEvent RecoveryState;
            public readonly DialogueRecoveryPermit DialogueRecovery = new DialogueRecoveryPermit();
            public long ExpectedRevision;
            public OutgoingSnapshot Outgoing;
            public long LastDeliveryId;
            public int BulkInFlight, ControlInFlight;
            public long WorldSentCostBytes, WorldAcknowledgedCostBytes;
            public DateTime LastWorldDeliveryDiagnostic;
            public readonly Dictionary<long, Delivery> Deliveries = new Dictionary<long, Delivery>();
            public readonly Dictionary<string, PlayerPose> PendingPoses = new Dictionary<string, PlayerPose>();
            public readonly PlayerSceneMembership SceneMembership = new PlayerSceneMembership();
            public readonly MapCatchupDeadline MapCatchup = new MapCatchupDeadline();
            public readonly PeerWorldEventQueue WorldQueue = new PeerWorldEventQueue();
        }
        private sealed class Delivery
        {
            public int Cost;
            public bool Control;
            public DateTime Sent;
            // Receipt diagnostics contain transport metadata only, never payloads or account data.
            public string Kind = "";
            public long Sequence = -1;
        }
        private sealed class OutgoingSnapshot
        {
            public SnapshotBegin Begin;
            public byte[] Bytes;
            public int Offset;
            public bool Begun;
        }
        private sealed class IncomingSnapshot : IDisposable
        {
            public SnapshotBegin Begin;
            public long ApartmentGeneration;
            public long SleepGeneration;
            public MemoryStream Stream;
            public DateTime Started = DateTime.UtcNow;
            public AccountWorldState Accounts;
            public byte[] AccountBytes;
            public void Dispose() => Stream?.Dispose();
        }
        private readonly Options _options;
        private readonly string _instanceId = Guid.NewGuid().ToString("N");
        private readonly FileStream _worldLock;
        private readonly TcpListener _listener;
        private readonly List<Peer> _peers = new List<Peer>();
        private readonly Socket _discovery;
        private readonly byte[] _discoveryBuffer = new byte[256];
        private readonly string _metadataPath;
        private readonly AccountStore _accounts;
        private AccountWorldState _ledger;
        private byte[] _ledgerBytes;
        private AppearanceOption[] _appearances = Array.Empty<AppearanceOption>();
        private readonly AccountDataTransfer _accountIncoming = new AccountDataTransfer();
        private DateTime _accountIncomingStarted;
        private readonly Dictionary<string,(DateTime Window,int Count)> _authRates = new Dictionary<string,(DateTime,int)>();
        private WorldMetadata _world;
        private byte[] _snapshot = Array.Empty<byte>();
        private Peer _worker;
        private IncomingSnapshot _incoming;
        private bool _ready;
        private DateTime _lastSimulation, _discoveryWindow;
        private int _discoveryReplies;
        private int _bulkGranted, _controlGranted;
        private string _checkpointRequest = "";
        private long _lastSleepSequence;
        private long _lastDialogueSequence, _accountDialogueGeneration;
        private long _accountSleepGeneration;
        private string _recoveryConversation = "", _recoveryAccount = "";
        private bool _sleepTransitionActive, _travelTransitionActive;
        private DateTime _nextDialogueWaitNotice;
        private readonly DialogueRecoveryLoadFence _dialogueLoads = new DialogueRecoveryLoadFence();
        private readonly ApartmentLoadFence _apartmentLoads = new ApartmentLoadFence();
        private readonly SleepLoadFence _sleepLoads = new SleepLoadFence();
        public DedicatedService(Options options)
        {
            _options = options;
            Directory.CreateDirectory(options.SaveRoot);
            _worldLock = new FileStream(Path.Combine(options.SaveRoot, "server.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            _metadataPath = Path.Combine(options.SaveRoot, "world.json");
            _accounts = new AccountStore(options.SaveRoot);
            LoadWorld();
            if (options.AuthoritySecret.Length == 0)
            {
                string secretPath = Path.Combine(options.SaveRoot, "authority-secret.txt");
                if (File.Exists(secretPath)) options.AuthoritySecret = File.ReadAllText(secretPath).Trim();
                else { options.AuthoritySecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)); File.WriteAllText(secretPath, options.AuthoritySecret, new UTF8Encoding(false)); }
                if (options.AuthoritySecret.Length < 16 || options.AuthoritySecret.Length > 256) throw new InvalidDataException("Invalid stored authority secret.");
            }
            _listener = new TcpListener(options.Bind, options.Port); _listener.Start(64);
            if (options.Discovery)
            {
                try { _discovery = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp); _discovery.Bind(new IPEndPoint(options.Bind, options.Port + 1)); _discovery.Blocking = false; }
                catch (SocketException ex) { _discovery?.Dispose(); _discovery = null; Console.WriteLine("LAN discovery unavailable: " + ex.SocketErrorCode); }
            }
            Console.WriteLine("Dedicated service listening on " + options.Bind + ":" + options.Port + "; world " + _world.WorldId + "; ready=false");
            Console.WriteLine("Persistence: " + options.SaveRoot + "; waiting for authenticated loopback Unity worker.");
        }

        private void LoadWorld()
        {
            if (!File.Exists(_metadataPath))
            {
                // Never import a local player's save. Only create server-owned metadata here.
                if (Directory.EnumerateFiles(_options.SaveRoot, "*.sav").Any()) throw new InvalidDataException("Save root contains player .sav files. Choose a new empty server world folder.");
                _world = new WorldMetadata { WorldId = Guid.NewGuid().ToString("N"), CreatedUtc = DateTime.UtcNow.ToString("O"), Seed = RandomNumberGenerator.GetInt32(int.MaxValue) };
                WriteAtomic(_metadataPath, JsonSerializer.SerializeToUtf8Bytes(_world, DedicatedProtocol.Json)); LoadAccounts(); return;
            }
            if (new FileInfo(_metadataPath).Length > 16384) throw new InvalidDataException("World metadata is oversized.");
            _world = JsonSerializer.Deserialize<WorldMetadata>(File.ReadAllBytes(_metadataPath), DedicatedProtocol.Json);
            if (_world == null || _world.SchemaVersion != 1 || _world.BuildId != DedicatedProtocol.BuildId || !Guid.TryParseExact(_world.WorldId, "N", out _) || _world.Revision < 0)
                throw new InvalidDataException("Incompatible server world metadata.");
            if (_world.Revision == 0) { LoadAccounts(); return; }
            string expectedFile = "snapshot-" + _world.Revision + ".bin";
            if (_world.SnapshotFile != expectedFile || _world.SnapshotLength < 1 || _world.SnapshotLength > DedicatedProtocol.MaxSnapshotBytes)
                throw new InvalidDataException("Invalid persisted snapshot descriptor.");
            string path = Path.Combine(_options.SaveRoot, expectedFile);
            if (new FileInfo(path).Length != _world.SnapshotLength) throw new InvalidDataException("Persisted snapshot length mismatch.");
            _snapshot = File.ReadAllBytes(path);
            if (!DedicatedProtocol.EqualHex(Hash(_snapshot), _world.SnapshotHash)) throw new InvalidDataException("Persisted snapshot checksum mismatch.");
            LoadAccounts();
        }

        private void LoadAccounts()
        {
            if(_world.AccountLedgerFile.Length==0){
                // Preserve the entire anonymous world verbatim. No account is
                // silently awarded its shared backpack, wallet or owned assets.
                if(_world.Revision>0){
                    string root=Path.Combine(_options.SaveRoot,"legacy-escrow");Directory.CreateDirectory(root);
                    string manifest=Path.Combine(root,"world.json");
                    if(!File.Exists(manifest)){
                        WriteAtomic(Path.Combine(root,_world.SnapshotFile),_snapshot);
                        WriteAtomic(manifest,JsonSerializer.SerializeToUtf8Bytes(_world,DedicatedProtocol.Json));
                    }
                }
                _ledger=new AccountWorldState {WorldId=_world.WorldId,Revision=_world.Revision};
                _ledgerBytes=JsonSerializer.SerializeToUtf8Bytes(_ledger,DedicatedProtocol.Json);return;
            }
            if(_world.AccountLedgerFile!="accounts-state-"+_world.Revision+".json"||_world.AccountLedgerLength<1||_world.AccountLedgerLength>AccountProtocol.MaxLedgerBytes)
                throw new InvalidDataException("Invalid account ledger descriptor.");
            _ledgerBytes=AccountStore.ReadBounded(Path.Combine(_options.SaveRoot,_world.AccountLedgerFile),AccountProtocol.MaxLedgerBytes);
            if(_ledgerBytes.Length!=_world.AccountLedgerLength||!DedicatedProtocol.EqualHex(Hash(_ledgerBytes),_world.AccountLedgerHash))throw new InvalidDataException("Account ledger checksum mismatch.");
            _ledger=JsonSerializer.Deserialize<AccountWorldState>(_ledgerBytes,DedicatedProtocol.Json);
            AccountProtocol.Validate(_ledger,_world.WorldId,_world.Revision);_accounts.ValidateLedgerAccounts(_ledger);
        }

        public void Tick()
        {
            DateTime now = DateTime.UtcNow;
            for (int i = 0; i < 16 && _listener.Pending(); i++)
            {
                TcpClient socket = _listener.AcceptTcpClient();
                if (_peers.Count >= _options.MaxPlayers + 16) { socket.Dispose(); continue; }
                var connection = DedicatedConnection.FromAccepted(socket);
                var peer = new Peer { Connection = connection, Hello = new ServerHello { SessionId = connection.SessionId, Nonce = Guid.NewGuid().ToString("N"), AccountIdentity = _accounts.Identity } };
                _peers.Add(peer); connection.Send("serverHello", peer.Hello);
            }
            Discovery(now);
            foreach (Peer peer in _peers.ToArray())
            {
                peer.Connection.Tick();
                int count = 0;
                while (peer.Connection.Connected && count++ < 64 && peer.Connection.TryReceive(out WireMessage message))
                {
                    try { Handle(peer, message, now); }
                    catch (Exception ex) when (ex is JsonException || ex is InvalidDataException || ex is ArgumentException || ex is NullReferenceException || ex is CryptographicException)
                    { Console.WriteLine(DateTime.UtcNow.ToString("O") + " Invalid payload exception=" + ex.GetType().Name + " " + PeerDiagnostic(peer)); Reject(peer, "invalid_message", "Invalid protocol payload."); }
                }
                if (peer.Connection.Connected)
                {
                    PumpWorldEvents(peer, now);
                    if (!peer.Connection.Connected) { Remove(peer); continue; }
                    PumpMapCatchup(peer, now);
                    if (!peer.Connection.Connected) { Remove(peer); continue; }
                    LogWorldDelivery(peer, now);
                    PumpSnapshot(peer);
                    PumpPoses(peer);
                    if (peer.Deliveries.Values.Any(delivery => (now - delivery.Sent).TotalSeconds > WorldEventFlow.StallSeconds) &&
                        !peer.MapCatchup.CanDeferDeliveryTimeout((now - DateTime.UnixEpoch).TotalSeconds))
                    {
                        Console.WriteLine(now.ToString("O") + " Slow client receipts " + ReceiptDiagnostic(peer, now));
                        Reject(peer, "slow_client", "World updates were not applied within the delivery timeout.");
                    }
                    // Human trust/account forms have a bounded allowance;
                    // an issued cryptographic challenge still expires after20s.
                    if (peer.Role.Length == 0 && ((now - peer.Created).TotalSeconds > 180 || (peer.Challenge != null && (now-peer.AuthStarted).TotalSeconds>20))) peer.Connection.Dispose();
                    else if ((now - peer.Connection.LastReceiveUtc).TotalSeconds >
                        (peer.Role.Length==0?180:((peer.Role == "worker" && (!_ready || _incoming != null)) || (peer.Role == "player" && !peer.Loaded) ? 120 : 30))) peer.Connection.Dispose();
                    else if (peer.Role == "player" && !peer.Loaded && peer.ExpectedRevision > 0 && (now - peer.LoadStarted).TotalSeconds > 120) peer.Connection.Dispose();
                    if (peer.Role.Length > 0 && (now - peer.LastPing).TotalSeconds >= 5) { Send(peer, "ping", new { }); peer.LastPing = now; }
                }
                if (!peer.Connection.Connected) Remove(peer);
            }
            if (_incoming != null && (now - _incoming.Started).TotalSeconds > 120) { _incoming.Dispose(); _incoming = null; if (_worker != null) Reject(_worker, "snapshot_timeout", "Snapshot transfer timed out."); }
            if((_accountIncoming.IsReceiving||_accountIncoming.Completed!=null)&&(now-_accountIncomingStarted).TotalSeconds>120)
            {_accountIncoming.Reset();if(_worker!=null)Reject(_worker,"account_transfer_timeout","Account transfer timed out.");}
            if (_ready && (now - _lastSimulation).TotalSeconds > 15) SetReady(false, "Unity simulation heartbeat expired.");
            GrantWorldCredits();
        }

        private void Handle(Peer peer, WireMessage message, DateTime now)
        {
            peer.LastMessageType = message.Type; peer.LastWorldKind = "";
            peer.LastFrameBytes = message.ReceivedFrameBytes;
            if ((now - peer.RateWindow).TotalSeconds >= 1) { peer.RateWindow = now; peer.Requests = 0; peer.Interactions = 0; }
            if (++peer.Requests > (peer.Role == "worker" ? 1000 : 100)) { Reject(peer, "rate_limit", "Too many messages."); return; }
            if (peer.Role.Length == 0)
            {
                if (message.Type == "statusQuery") { Send(peer, "serverStatus", Status()); return; }
                if (message.Type == "accountBegin") { BeginAccountAuthentication(peer,message.Read<AuthBegin>(),now); return; }
                if (message.Type == "accountProof" || message.Type == "accountRegister") { FinishAccountAuthentication(peer,message,now); return; }
                if (message.Type != "join") { Reject(peer, "handshake_required", "Join handshake required."); return; }
                Join(peer, message.Read<JoinRequest>()); return;
            }
            if (message.Type == "ping") { Send(peer, "pong", new { }); return; }
            // Authenticated worker pong is produced by Unity's main-thread pump.
            // Backpressure can intentionally pause events, without pausing simulation.
            if (message.Type == "pong") { if (peer == _worker && _ready) _lastSimulation = now; return; }
            if (message.Type == "statusQuery") { Send(peer, "serverStatus", Status()); return; }
            if (message.Type == "leave") { peer.Connection.Dispose(); return; }
            if (TryHandleChat(peer, message, now)) return;
            if(peer.Role=="account"){
                if(message.Type=="accountProfile"){
                    var profile=message.Read<AccountProfileRequest>();
                    if(profile==null||!_appearances.Any(a=>a.Id==profile.AppearanceId)){Reject(peer,"appearance_unavailable","Choose an available character appearance.");return;}
                    if(peer.Account.AppearanceId!=profile.AppearanceId)peer.Account=_accounts.SetAppearance(peer.Account.AccountId,profile.AppearanceId);
                    Send(peer,"accountProfileAccepted",peer.Account);return;
                }
                if(message.Type=="accountJoin"){CompletePlayerJoin(peer);return;}
                Reject(peer,"account_not_joined","Select a character and join the server world first.");return;
            }
            if (peer.Role == "worker") { WorkerMessage(peer, message, now); return; }
            if (message.Type == "worldEventAck") { AcknowledgeWorldEvents(peer, message.Read<WorldEventAcknowledgement>()); PumpMapCatchup(peer, now); return; }
            if (message.Type == "clientReady")
            {
                var ack = message.Read<WorldLoadComplete>();
                if (!_ready || peer.AwaitingCheckpoint || peer.Outgoing != null || peer.AccountOutgoing.IsSending || ack.WorldId != _world.WorldId || ack.Revision != peer.ExpectedRevision || ack.Revision <= 0) throw new InvalidDataException();
                if (peer.Loaded) return;
                if (!_dialogueLoads.CanLoad(ack.Revision))
                { Reject(peer, "dialogue_load_changed", "A conversation changed while your world was loading. Reconnect after it finishes."); return; }
                if (!_apartmentLoads.CanLoad(ack.Revision))
                { RejectStaleApartmentLoad(peer); return; }
                if (!_sleepLoads.CanLoad(ack.Revision))
                { Reject(peer, "sleep_load_changed", "The shared night changed while your world was loading. Reconnect after morning is ready."); return; }
                peer.Loaded = true; Send(peer, "clientReadyAccepted", ack);
                if (_worker != null) Send(_worker, "playerPresence", Presence(peer,true));
                return;
            }
            // A pending authenticated join may finish an already-issued shared
            // day choice. It receives no player body or ordinary interaction access.
            if (message.Type == "interaction" && !peer.Loaded && peer.AwaitingCheckpoint && _ready && _worker != null)
            {
                InteractionRequest request = message.Read<InteractionRequest>();
                if (request != null && request.Kind == DialogueRecoveryProtocol.ActionKind)
                {
                    if (++peer.Interactions > 10) { Reject(peer, "interaction_rate", "Too many interaction requests."); return; }
                    if (!DedicatedProtocol.Text(request.TargetId, 128) || !Guid.TryParseExact(request.RequestId, "N", out _) ||
                        request.Data.ValueKind == JsonValueKind.Undefined || Encoding.UTF8.GetByteCount(request.Data.GetRawText()) > 8192) throw new InvalidDataException();
                    request.PlayerId = peer.Id; request.AccountId = peer.Account.AccountId;
                    bool laneAvailable = _dialogueLoads.Active && !_sleepTransitionActive && !_travelTransitionActive && !_apartmentLoads.TransitionActive;
                    if (!laneAvailable || !peer.DialogueRecovery.TryConsume(request, peer.Id, peer.Account.AccountId))
                    { Send(peer, "dialogueRecoveryRejected", peer.DialogueRecovery.Reject(request, peer.Id, peer.Account.AccountId, laneAvailable)); return; }
                    Send(_worker, "interaction", request); return;
                }
                if (request != null && request.Kind == SleepRecoveryProtocol.ActionKind)
                {
                    if (++peer.Interactions > 10) { Reject(peer, "interaction_rate", "Too many interaction requests."); return; }
                    if (!DedicatedProtocol.Text(request.TargetId, 128) || !DedicatedProtocol.Text(request.RequestId, 64) ||
                        request.Data.ValueKind == JsonValueKind.Undefined || Encoding.UTF8.GetByteCount(request.Data.GetRawText()) > 8192) throw new InvalidDataException();
                    request.PlayerId = peer.Id;request.AccountId=peer.Account.AccountId;
                    if (_dialogueLoads.Active || !SleepRecoveryProtocol.AllowsPendingAction(peer.RecoveryState, request, peer.Id))
                    { Send(peer, "sleepRecoveryRejected", new ProtocolError { Code = "sleep_choice_rejected", Message = "That shared day choice has changed or belongs to another player. Wait for the current choices." }); return; }
                    Send(_worker, "interaction", request); return;
                }
            }
            if (!peer.Loaded || !_ready || _worker == null) { Send(peer, "error", new ProtocolError { Code = "world_not_ready", Message = "Wait for the server world load barrier." }); return; }
            if (message.Type == MapCatchupProgress.MessageKind)
            {
                bool requested = peer.MapCatchup.BarrierRequested;
                peer.MapCatchup.ObserveProgress(message.Read<MapCatchupProgress>(),
                    (now - DateTime.UnixEpoch).TotalSeconds, peer.LastDeliveryId);
                if (!requested && peer.MapCatchup.BarrierRequested)
                {
                    try { QueueMapCatchupBarrier(peer, now); }
                    catch (Exception ex) when (ex is IOException || ex is InvalidDataException)
                    { Reject(peer, "slow_client", "Your map synchronization queue exceeded its bounded capacity."); return; }
                }
                PumpMapCatchup(peer, now);
                return;
            }
            if (message.Type == "playerPose")
            {
                PlayerPose pose = message.Read<PlayerPose>(); if (!DedicatedProtocol.PoseValid(pose)) throw new InvalidDataException();
                if ((now - peer.LastPose).TotalSeconds < .035) return;
                peer.LastPose = now; pose.PlayerId = peer.Id; pose.Name = peer.Name;pose.AccountId=peer.Account.AccountId;pose.AppearanceId=peer.Account.AppearanceId;
                pose.PropertyId = peer.SceneMembership.Current?.PropertyId ?? "";
                if (!peer.SceneMembership.TryAcceptPose(pose)) return;
                Send(_worker, "playerPose", pose); return;
            }
            if (message.Type == BoatProtocol.PoseKind)
            {
                var boatPose = message.Read<BoatPose>();
                if (boatPose == null) throw new InvalidDataException("Missing boat position.");
                boatPose.PlayerId = peer.Id; boatPose.AccountId = peer.Account.AccountId;
                BoatProtocol.Validate(boatPose);
                var context = peer.SceneMembership.Current;
                if (context == null || context.TravelPending || context.PropertyId.Length != 0 ||
                    boatPose.Scene != context.Scene || boatPose.ContextGeneration != context.Generation) return;
                if ((now - peer.LastBoatPose).TotalSeconds < .09) return;
                peer.LastBoatPose = now;
                Send(_worker, BoatProtocol.PoseKind, boatPose); return;
            }
            if (message.Type == "interaction")
            {
                if (++peer.Interactions > 10) { Reject(peer, "interaction_rate", "Too many interaction requests."); return; }
                InteractionRequest request = message.Read<InteractionRequest>();
                if (!DedicatedProtocol.Text(request.Kind, 48) || !DedicatedProtocol.Text(request.TargetId, 128) || !DedicatedProtocol.Text(request.RequestId, 64) ||
                    request.Data.ValueKind == JsonValueKind.Undefined || Encoding.UTF8.GetByteCount(request.Data.GetRawText()) > 8192) throw new InvalidDataException();
                request.PlayerId = peer.Id;request.AccountId=peer.Account.AccountId;
                if (!peer.SceneMembership.AllowsInteraction(request))
                { Send(peer, "playerSceneRejected", new ProtocolError { Code = "player_scene_changed", Message = "Wait for your current map transition to finish, then try again." }); return; }
                Send(_worker, "interaction", request); return;
            }
            Reject(peer, "authority_violation", "Clients may send movement and interaction requests only.");
        }

        private void BeginAccountAuthentication(Peer peer,AuthBegin begin,DateTime now)
        {
            AccountAuthentication.ValidateBegin(begin);
            if(peer.Challenge!=null)throw new InvalidDataException("Account challenge already issued.");
            string ip=peer.Connection.RemoteEndpoint.Address.ToString();
            foreach(string key in _authRates.Where(x=>(now-x.Value.Window).TotalMinutes>=1).Select(x=>x.Key).ToArray())_authRates.Remove(key);
            _authRates.TryGetValue(ip,out var rate);
            if(rate.Count>=8||(_authRates.Count>=1024&&rate.Count==0)){Reject(peer,"login_rate","Too many login attempts. Try again shortly.");return;}
            _authRates[ip]=(rate.Count==0?now:rate.Window,rate.Count+1);
            if(!DedicatedProtocol.EqualHex(begin.RoomProof,AccountProtocol.RoomProof(peer.Hello,begin,_options.Password)))
            {Reject(peer,"authentication_failed","Server password or account authentication failed.");return;}
            peer.ExistingAccount=_accounts.TryGet(begin.Username,out peer.Account,out peer.Verifier);
            if(begin.Mode=="login"&&!peer.ExistingAccount)peer.Verifier=_accounts.FakeVerifier(begin.Username);
            peer.Challenge=AccountAuthentication.CreateChallenge(_accounts.IdentityKey,_accounts.Identity,peer.Hello,begin,begin.Mode=="login"?peer.Verifier:null);
            peer.AuthStarted=now;Send(peer,"accountChallenge",peer.Challenge);
        }
        private void FinishAccountAuthentication(Peer peer,WireMessage message,DateTime now)
        {
            var challenge=peer.Challenge;
            if(challenge==null||(now-peer.AuthStarted).TotalSeconds>20)throw new InvalidDataException("Account challenge expired or already used.");
            peer.Challenge=null; // Consumed even if decryption/proof validation fails.
            AccountVerifier verifier=peer.Verifier;AccountAuthProof proof;
            if(message.Type=="accountRegister"){
                if(challenge.Mode!="register")throw new InvalidDataException();
                var request=message.Read<AccountRegistrationProof>();
                if(request==null)throw new InvalidDataException();
                verifier=AccountAuthentication.OpenRegistration(_accounts.IdentityKey,challenge,request.Registration);proof=request.Proof;
            }else{
                if(challenge.Mode!="login")throw new InvalidDataException();proof=message.Read<AccountAuthProof>();
            }
            peer.Verifier=null;
            bool valid=AccountAuthentication.VerifyProof(challenge,verifier,proof,out byte[] serverProof,out byte[] key);
            try{
                if(!valid||(challenge.Mode=="login"&&!peer.ExistingAccount)) {Reject(peer,"authentication_failed","Account authentication failed.");return;}
                if(challenge.Mode=="register"){
                    if(peer.ExistingAccount){Reject(peer,"account_unavailable","That account cannot be created. Choose another username or log in.");return;}
                    try{peer.Account=_accounts.Register(challenge.Username,verifier);}
                    catch(InvalidOperationException){Reject(peer,"account_unavailable","That account cannot be created.");return;}
                }
                if(_peers.Any(p=>p!=peer&&p.Connection.Connected&&p.Account?.AccountId==peer.Account.AccountId&&p.Role.Length>0))
                {Reject(peer,"account_in_use","This account is already connected.");return;}
                peer.Connection.SetAuthenticationKey(key);
                peer.Role="account";peer.Name=peer.Account.Username;
                Send(peer,"accountAuthenticated",new AccountAuthenticated {AccountId=peer.Account.AccountId,Username=peer.Account.Username,
                    AppearanceId=peer.Account.AppearanceId,AppearanceOptions=_appearances,ServerProof=serverProof});
            }finally{if(key!=null)CryptographicOperations.ZeroMemory(key);}
        }
        private void CompletePlayerJoin(Peer peer)
        {
            if(!_ready||_worker==null){Send(peer,"accountJoinRejected",new ProtocolError {Code="world_not_ready",Message="Wait for the server world to finish loading."});return;}
            if(!_appearances.Any(x=>x.Id==peer.Account.AppearanceId)){Send(peer,"accountJoinRejected",new ProtocolError {Code="appearance_required",Message="Choose an available character appearance."});return;}
            if(_peers.Count(p=>p.Role=="player")>=_options.MaxPlayers){Reject(peer,"server_full","Player limit reached.");return;}
            peer.Role="player";peer.AwaitingCheckpoint=true;peer.LoadStarted=DateTime.UtcNow;peer.ExpectedRevision=_world.Revision;
            Send(peer,"joinAccepted",new JoinAccepted {PlayerId=peer.Id,AccountId=peer.Account.AccountId,Role="player",World=_world,Ready=_ready});
            // Sleep consensus counts the whole authenticated connected cohort,
            // including clients still importing their first world snapshot.
            Send(_worker,"sleepParticipantPresence",Presence(peer,true));
            Send(_worker,"accountPrepare",new AccountPreparation {PlayerId=peer.Id,AccountId=peer.Account.AccountId,Username=peer.Name,AppearanceId=peer.Account.AppearanceId});
            Send(_worker,"dialogueRecoveryPresence",Presence(peer,true));
            _checkpointRequest=Guid.NewGuid().ToString("N");Send(_worker,"checkpointRequested",new CheckpointRequest {RequestId=_checkpointRequest});
            Send(_worker,"sleepRecoveryPresence",Presence(peer,true));
            Console.WriteLine("Joined player "+peer.Id+" account="+peer.Account.AccountId+"; players="+_peers.Count(p=>p.Role=="player"));
        }
        private static PlayerPresence Presence(Peer peer,bool joined)=>new PlayerPresence {PlayerId=peer.Id,Name=peer.Name,AccountId=peer.Account?.AccountId??"",AppearanceId=peer.Account?.AppearanceId??"",Joined=joined};
        private void SendSleepParticipantRoster()
        {
            if (_worker == null || !_worker.Connection.Connected) return;
            foreach (var player in _peers.Where(p => p.Role == "player" && p.Connection.Connected))
                Send(_worker, "sleepParticipantPresence", Presence(player, true));
        }

        private void Join(Peer peer, JoinRequest join)
        {
            if (join == null || join.Version != DedicatedProtocol.Version || join.BuildId != DedicatedProtocol.BuildId)
            { Reject(peer, "incompatible_build", "Protocol or game build does not match."); return; }
            if (!DedicatedProtocol.Text(join.Name, 48) || !Guid.TryParseExact(join.Nonce, "N", out _) || (join.Role != "player" && join.Role != "worker")) throw new InvalidDataException();
            bool worker = join.Role == "worker";
            if(!worker){Reject(peer,"account_required","This server requires an account login.");return;}
            if (worker && (!IPAddress.IsLoopback(peer.Connection.RemoteEndpoint.Address) || _worker != null))
            { Reject(peer, "authority_denied", "Only one authenticated local Unity worker is permitted."); return; }
            string secret = worker ? _options.AuthoritySecret : _options.Password;
            if (!DedicatedProtocol.EqualHex(join.Proof, DedicatedProtocol.Proof(peer.Hello, join, secret)))
            { Reject(peer, "authentication_failed", "Join authentication failed."); return; }
            if (!worker && _peers.Count(p => p.Role == "player") >= _options.MaxPlayers)
            { Reject(peer, "server_full", "Player limit reached."); return; }
            peer.Connection.SetAuthenticationKey(DedicatedProtocol.DeriveSessionKey(peer.Hello, join, secret));
            peer.Role = join.Role; peer.Name = join.Name;
            if (worker) { _worker = peer; _bulkGranted = _controlGranted = 0; _apartmentLoads.Reset(); SetReady(false, "Unity worker is loading the server world."); }
            Send(peer, "joinAccepted", new JoinAccepted { PlayerId = peer.Id, Role = peer.Role, World = _world, Ready = _ready });
            if (worker) { SendSleepParticipantRoster(); BeginSendingSnapshot(peer); }
            else if (_ready)
            {
                peer.AwaitingCheckpoint = true; peer.LoadStarted = DateTime.UtcNow; peer.ExpectedRevision = _world.Revision;
                // A join during an existing upload requires another capture. Latest token safely coalesces all waiting joins.
                _checkpointRequest = Guid.NewGuid().ToString("N");
                Send(_worker, "checkpointRequested", new CheckpointRequest { RequestId = _checkpointRequest });
                Send(_worker, "sleepRecoveryPresence", new PlayerPresence { PlayerId = peer.Id, Name = peer.Name, Joined = true });
            }
            else Send(peer, "simulationState", new SimulationState { Ready = false, Message = "Waiting for the Unity worker to load the server world." });
            Console.WriteLine("Joined " + peer.Role + " " + peer.Id + "; players=" + _peers.Count(p => p.Role == "player"));
        }

        private void WorkerMessage(Peer peer, WireMessage message, DateTime now)
        {
            if(message.Type=="appearanceCatalog"){
                var catalog=message.Read<AccountAppearanceCatalog>();
                if(catalog?.Options==null||catalog.Options.Length<1||catalog.Options.Length>512)throw new InvalidDataException("Invalid appearance catalog.");
                var unique=new HashSet<string>(StringComparer.Ordinal);
                foreach(var option in catalog.Options)if(option==null||!DedicatedProtocol.Text(option.Id,128)||!DedicatedProtocol.Text(option.Name,128)||!unique.Add(option.Id))throw new InvalidDataException("Invalid appearance option.");
                _appearances=catalog.Options.OrderBy(x=>x.Id,StringComparer.Ordinal).ToArray();
                foreach(var account in _peers.Where(p=>p.Role=="account"))Send(account,"appearanceCatalog",new AccountAppearanceCatalog {Options=_appearances});return;
            }
            if(message.Type=="accountDataBegin"){
                var begin=message.Read<AccountDataBegin>();
                if(_incoming!=null||_accountIncoming.Completed!=null||begin==null||begin.Revision<=_world.Revision||_apartmentLoads.TransitionActive)throw new InvalidDataException("Account checkpoint cannot overlap world transfer or transition.");
                _accountDialogueGeneration = _dialogueLoads.BeginSnapshot();
                _accountSleepGeneration = _sleepLoads.BeginSnapshot();
                _accountIncoming.BeginReceive(begin,_world.WorldId,"ledger");_accountIncomingStarted=now;return;
            }
            if(message.Type=="accountDataChunk"){_accountIncoming.ReceiveChunk(message.Read<SnapshotChunk>());return;}
            if(message.Type=="accountDataEnd"){
                _accountIncoming.EndReceive(message.Read<SnapshotEnd>());
                var ledger=JsonSerializer.Deserialize<AccountWorldState>(_accountIncoming.CompletedBytes,DedicatedProtocol.Json);
                AccountProtocol.Validate(ledger,_world.WorldId,_accountIncoming.Completed.Revision);_accounts.ValidateLedgerAccounts(ledger);
                if(ledger.LegacyEscrow==null)throw new InvalidDataException("Worker must initialize legacy escrow before publishing an account checkpoint.");
                if(_ledger.StarterPropertyId.Length>0&&ledger.StarterPropertyId!=_ledger.StarterPropertyId)throw new InvalidDataException("Starter property cannot change.");
                if(_ledger.LegacyEscrow!=null&&ledger.LegacyEscrow==null)throw new InvalidDataException("Legacy escrow cannot disappear after migration.");
                foreach(var previous in _ledger.Accounts){var current=ledger.Accounts.FirstOrDefault(x=>x.AccountId==previous.AccountId);if(current==null||current.StateRevision<previous.StateRevision)throw new InvalidDataException("Account state was removed or replayed.");}
                return;
            }
            if (message.Type == "snapshotBegin")
            {
                SnapshotBegin begin = message.Read<SnapshotBegin>();
                if (_incoming != null || begin.WorldId != _world.WorldId || begin.Revision <= _world.Revision || begin.Length < 1 || begin.Length > DedicatedProtocol.MaxSnapshotBytes ||
                    !Guid.TryParseExact(begin.TransferId, "N", out _) || begin.Sha256 == null || begin.Sha256.Length != 64 || !DedicatedProtocol.Text(begin.Format, 64) ||
                    begin.CheckpointRequestId == null || (begin.CheckpointRequestId.Length > 0 && !Guid.TryParseExact(begin.CheckpointRequestId, "N", out _))||
                    _accountIncoming.Completed==null||_accountIncoming.Completed.Revision!=begin.Revision||
                    !DedicatedProtocol.EqualHex(begin.AccountLedgerHash,_accountIncoming.Completed.Sha256)) throw new InvalidDataException();
                long generation = _apartmentLoads.BeginSnapshot(begin.Revision);
                _incoming = new IncomingSnapshot { Begin = begin, ApartmentGeneration = generation, SleepGeneration = _accountSleepGeneration, Stream = new MemoryStream(begin.Length),
                    AccountBytes=_accountIncoming.CompletedBytes,Accounts=JsonSerializer.Deserialize<AccountWorldState>(_accountIncoming.CompletedBytes,DedicatedProtocol.Json) };
                _accountIncoming.Reset();return;
            }
            if (message.Type == "snapshotChunk")
            {
                SnapshotChunk chunk = message.Read<SnapshotChunk>();
                if (_incoming == null || chunk.TransferId != _incoming.Begin.TransferId || chunk.Offset != _incoming.Stream.Length || chunk.Data == null ||
                    chunk.Data.Length < 1 || chunk.Data.Length > DedicatedProtocol.SnapshotChunkBytes || _incoming.Stream.Length + chunk.Data.Length > _incoming.Begin.Length) throw new InvalidDataException();
                _incoming.Stream.Write(chunk.Data, 0, chunk.Data.Length); return;
            }
            if (message.Type == "snapshotEnd")
            {
                SnapshotEnd end = message.Read<SnapshotEnd>();
                if (_incoming == null || end.TransferId != _incoming.Begin.TransferId || _incoming.Stream.Length != _incoming.Begin.Length) throw new InvalidDataException();
                byte[] bytes = _incoming.Stream.GetBuffer(); SnapshotBegin begin = _incoming.Begin;
                if (!DedicatedProtocol.EqualHex(Hash(bytes), begin.Sha256)) throw new InvalidDataException();
                _apartmentLoads.CommitSnapshot(begin.Revision, _incoming.ApartmentGeneration);
                _dialogueLoads.CommitSnapshot(begin.Revision, _accountDialogueGeneration);
                _sleepLoads.CommitSnapshot(begin.Revision, _incoming.SleepGeneration);
                CommitSnapshot(begin, bytes,_incoming.Accounts,_incoming.AccountBytes); _incoming.Dispose(); _incoming = null;
                Send(peer, "snapshotCommitted", new WorldLoadComplete { WorldId = _world.WorldId, Revision = _world.Revision });
                if (_ready && _checkpointRequest.Length > 0 && begin.CheckpointRequestId == _checkpointRequest)
                {
                    _checkpointRequest = "";
                    foreach (Peer player in _peers.Where(p => p.Role == "player" && p.AwaitingCheckpoint)) BeginSendingSnapshot(player);
                }
                return;
            }
            if (message.Type == "workerReady")
            {
                WorkerReady ready = message.Read<WorkerReady>();
                if (_snapshot.Length == 0 || ready.WorldId != _world.WorldId || ready.Revision != _world.Revision || !DedicatedProtocol.Text(ready.Scene, 128)) throw new InvalidDataException();
                _lastSimulation = now;
                bool previouslyReady = _ready; SetReady(true, "Unity simulation ready.");
                if (!previouslyReady)
                {
                    // Reassert after native initialization as well as transport
                    // join. Repeated connected=true must be idempotent.
                    SendSleepParticipantRoster();
                    foreach (Peer player in _peers.Where(p => p.Role == "player")) BeginSendingSnapshot(player);
                }
                return;
            }
            if (message.Type == "worldEvent")
            {
                WorldEvent worldEvent = message.Read<WorldEvent>();
                if (worldEvent?.Kind == MapCatchupBarrier.EventKind)
                    throw new InvalidDataException("Only the service may issue map catch-up barriers.");
                peer.LastWorldKind = worldEvent != null && DedicatedProtocol.Text(worldEvent.Kind, 48) ? worldEvent.Kind : "invalid";
                if (!_ready || !DedicatedProtocol.Text(worldEvent.Kind, 48) || worldEvent.Data.ValueKind == JsonValueKind.Undefined ||
                    worldEvent.DeliveryId != 0 || Encoding.UTF8.GetByteCount(worldEvent.Data.GetRawText()) > 48000 ||
                    (worldEvent.WorldId.Length != 0 && worldEvent.WorldId != _world.WorldId)||worldEvent.TargetPlayerId==null||
                    (worldEvent.TargetPlayerId.Length>0&&!Guid.TryParseExact(worldEvent.TargetPlayerId,"N",out _))) throw new InvalidDataException();
                int cost = WorldEventFlow.Cost(worldEvent); bool control = WorldEventFlow.IsControlKind(worldEvent.Kind);
                if ((worldEvent.Kind == "group-travel" || worldEvent.Kind == "group-apartment") &&
                    worldEvent.Data.TryGetProperty("Phase", out var privatePhase) && privatePhase.ValueKind == JsonValueKind.String &&
                    (privatePhase.GetString() == "prepare" || privatePhase.GetString() == "depart" || privatePhase.GetString() == "execute" ||
                        privatePhase.GetString() == "heartbeat" || privatePhase.GetString() == "complete" || privatePhase.GetString() == "cancel") &&
                    worldEvent.TargetPlayerId.Length == 0)
                    throw new InvalidDataException("Map transitions must target their authenticated traveler.");
                GroupSleepEvent sleep = null;
                DialogueRecoveryState dialogue = null;
                if (worldEvent.Kind == DialogueRecoveryProtocol.EventKind)
                {
                    dialogue = worldEvent.Data.Deserialize<DialogueRecoveryState>(DedicatedProtocol.Json);
                    ObserveDialogueRecovery(worldEvent, dialogue, now);
                }
                if (worldEvent.Kind == SleepRecoveryProtocol.EventKind)
                {
                    sleep = worldEvent.Data.Deserialize<GroupSleepEvent>(DedicatedProtocol.Json);
                    SleepRecoveryProtocol.ValidateWire(sleep);
                    if (sleep.Sequence <= _lastSleepSequence) throw new InvalidDataException("Replayed shared sleep state.");
                    _lastSleepSequence = sleep.Sequence;
                    if (!SleepRecoveryProtocol.IsResponse(sleep))
                    {
                        bool active = sleep.Phase != "complete" && sleep.Phase != "idle" && sleep.Phase != "waiting" && sleep.Phase != "cancelled";
                        if (active && _dialogueLoads.Active) throw new InvalidDataException("Sleep overlaps active dialogue recovery.");
                        bool wasActive = _sleepLoads.TransitionActive;
                        _sleepLoads.Observe(sleep, _world.Revision);
                        _sleepTransitionActive = _sleepLoads.TransitionActive;
                        if (wasActive && !_sleepTransitionActive)
                        {
                            // A wake checkpoint is used to synchronize existing
                            // players; new joins need a capture after completion.
                            _checkpointRequest = Guid.NewGuid().ToString("N");
                            Send(_worker, "checkpointRequested", new CheckpointRequest { RequestId = _checkpointRequest });
                        }
                        foreach (var pending in _peers.Where(p => p.Role == "player" && p.AwaitingCheckpoint)) pending.LoadStarted = now;
                    }
                }
                if (worldEvent.TargetPlayerId.Length == 0 && worldEvent.Kind == "group-travel" && worldEvent.Data.TryGetProperty("Phase", out var travelPhase))
                {
                    string phase = travelPhase.GetString();
                    if (phase == "prepare" || phase == "depart")
                    { if (_dialogueLoads.Active) throw new InvalidDataException("Travel overlaps active dialogue recovery."); _travelTransitionActive = true; }
                    else if (phase == "complete" || phase == "cancel") _travelTransitionActive = false;
                }
                if (cost > (control ? _controlGranted : _bulkGranted))
                {
                    Console.WriteLine(now.ToString("O") + " Flow violation cost=" + cost + " lane=" + (control ? "control" : "bulk") +
                        " bulkGranted=" + _bulkGranted + " controlGranted=" + _controlGranted);
                    Reject(peer, "flow_violation", "Worker published a world update without delivery credit."); return;
                }
                if (control) _controlGranted -= cost; else _bulkGranted -= cost;
                if (worldEvent.Kind == PlayerSceneContext.EventKind) ObservePlayerScene(worldEvent, now);
                if (worldEvent.Kind == PlayerMapRestore.EventKind) ValidatePlayerRestore(worldEvent);
                if (worldEvent.Kind == "player-map-error") ValidatePlayerMapError(worldEvent);
                if ((worldEvent.Kind == "group-travel" || worldEvent.Kind == "group-apartment") && worldEvent.TargetPlayerId.Length > 0)
                    ValidatePrivateTransition(worldEvent);
                if (worldEvent.Kind == "group-apartment")
                { if (_dialogueLoads.Active) throw new InvalidDataException("Apartment transition overlaps active dialogue recovery."); if (worldEvent.TargetPlayerId.Length == 0) ObserveApartmentTransition(worldEvent, now); }
                worldEvent.WorldId = _world.WorldId; worldEvent.Revision = _world.Revision; _lastSimulation = now;
                foreach (Peer player in _peers.Where(p => p.Role == "player" && p.Connection.Connected &&
                    (worldEvent.TargetPlayerId.Length==0||worldEvent.TargetPlayerId==p.Id)&&
                    ((p.Loaded && dialogue == null) || (p.AwaitingCheckpoint && sleep != null &&
                        (!SleepRecoveryProtocol.IsResponse(sleep) || sleep.TargetPlayer == p.Id)) ||
                        (p.AwaitingCheckpoint && dialogue != null && dialogue.TargetPlayerId == p.Id && dialogue.OwnerAccountId == p.Account.AccountId))))
                {
                    try { player.WorldQueue.Enqueue(worldEvent, cost, control, (now - DateTime.UnixEpoch).TotalSeconds); }
                    catch (IOException)
                    {
                        // One slow recipient must not stop authoritative capture
                        // or delivery to players whose own windows remain clear.
                        Reject(player, "slow_client", "Your ordered world queue exceeded its bounded loading capacity. Reconnect to load the current world.");
                    }
                }
                return;
            }
            if (message.Type == "playerPose")
            {
                PlayerPose pose = message.Read<PlayerPose>();
                if (!DedicatedProtocol.PoseValid(pose) || !Guid.TryParseExact(pose.PlayerId, "N", out _)) throw new InvalidDataException();
                // Presence removal and a worker's echo cross separate TCP streams.
                // A player may leave after its pose was forwarded to the worker but
                // before that valid echo returns. Discard it without killing authority.
                var actor=_peers.FirstOrDefault(p => p.Role == "player" && p.Id == pose.PlayerId && p.Loaded && p.Connection.Connected);
                if (actor==null)
                {
                    if ((now - peer.LastStalePoseLog).TotalSeconds >= 5)
                    { peer.LastStalePoseLog = now; Console.WriteLine(now.ToString("O") + " Ignored stale worker pose player=" + pose.PlayerId); }
                    return;
                }
                pose.AccountId=actor.Account.AccountId;pose.AppearanceId=actor.Account.AppearanceId;pose.Name=actor.Name;
                pose.PropertyId = actor.SceneMembership.Current?.PropertyId ?? "";
                if (!actor.SceneMembership.TryAcceptPose(pose)) return;
                // Movement is replaceable; retain only the newest pose per player
                // while a recipient's reliable stream is busy.
                foreach (Peer player in _peers.Where(p => p.Role == "player" && p.Loaded)) player.PendingPoses[pose.PlayerId] = pose;
                return;
            }
            throw new InvalidDataException();
        }

        private void ObservePlayerScene(WorldEvent update, DateTime now)
        {
            var context = update.Data.Deserialize<PlayerSceneContext>(DedicatedProtocol.Json);
            PlayerSceneContext.Validate(context);
            if (update.TargetPlayerId != context.PlayerId) throw new InvalidDataException("Scene assignment must be private to its player.");
            var player = _peers.FirstOrDefault(p => p.Role == "player" && p.Id == context.PlayerId && p.Loaded && p.Connection.Connected);
            if (player == null) return; // a leave may race the authenticated worker's response
            if (!player.SceneMembership.Observe(context)) return;
            // Clear replaceable old-scene poses before a delayed worker echo can
            // recreate a departed avatar. One inactive pose hides it immediately.
            foreach (var recipient in _peers.Where(p => p.Role == "player" && p.Loaded && p.Connection.Connected))
                recipient.PendingPoses[player.Id] = new PlayerPose { PlayerId = player.Id, AccountId = player.Account.AccountId,
                    Name = player.Name, AppearanceId = player.Account.AppearanceId, ContextGeneration = context.Generation,
                    Scene = context.Scene, PropertyId = context.PropertyId, Active = false };
        }

        private void ValidatePlayerRestore(WorldEvent update)
        {
            var restore = update.Data.Deserialize<PlayerMapRestore>(DedicatedProtocol.Json);
            PlayerMapRestore.Validate(restore);
            if (update.TargetPlayerId != restore.PlayerId) throw new InvalidDataException("Map restoration must be private to its player.");
            var player = _peers.FirstOrDefault(p => p.Role == "player" && p.Id == restore.PlayerId && p.Loaded && p.Connection.Connected);
            if (player == null) return;
            var context = player.SceneMembership.Current;
            if (context == null || !context.TravelPending || context.Epoch != restore.Epoch ||
                context.Scene != restore.Location.Scene || context.PropertyId != restore.Location.PropertyId)
                throw new InvalidDataException("Map restoration has no matching scene assignment.");
        }

        private void ValidatePrivateTransition(WorldEvent update)
        {
            if (update.Kind == "group-apartment") ApartmentProtocol.Validate(update.Data.Deserialize<GroupApartmentEvent>(DedicatedProtocol.Json));
            if (!update.Data.TryGetProperty("PlayerId", out var playerId) || playerId.ValueKind != JsonValueKind.String ||
                playerId.GetString() != update.TargetPlayerId || !update.Data.TryGetProperty("Phase", out var phase) || phase.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Private transition targets a different player.");
            string state = phase.GetString();
            if (state == "accepted" || state == "denied" || state == "fatal" || state == "complete" || state == "cancel") return;
            var player = _peers.FirstOrDefault(p => p.Role == "player" && p.Id == update.TargetPlayerId && p.Loaded && p.Connection.Connected);
            if (player == null) return;
            var context = player.SceneMembership.Current;
            if (context == null || !context.TravelPending || !update.Data.TryGetProperty("Epoch", out var epoch) ||
                epoch.ValueKind != JsonValueKind.String || epoch.GetString() != context.Epoch)
                throw new InvalidDataException("Private transition has no matching scene assignment.");
        }

        private static void ValidatePlayerMapError(WorldEvent update)
        {
            var error = update.Data.Deserialize<ProtocolError>(DedicatedProtocol.Json);
            if (!Guid.TryParseExact(update.TargetPlayerId, "N", out _) || error == null || error.Code != "player_map_failed" ||
                !DedicatedProtocol.Text(error.Message, 512))
                throw new InvalidDataException("Map failure must be bounded and private to its affected player.");
        }

        private void CommitSnapshot(SnapshotBegin begin, byte[] bytes,AccountWorldState accounts,byte[] accountBytes)
        {
            string filename = "snapshot-" + begin.Revision + ".bin";
            string accountFilename="accounts-state-"+begin.Revision+".json";
            WriteAtomic(Path.Combine(_options.SaveRoot, filename), bytes);
            WriteAtomic(Path.Combine(_options.SaveRoot,accountFilename),accountBytes);
            var updated = new WorldMetadata { WorldId = _world.WorldId, CreatedUtc = _world.CreatedUtc, Seed = _world.Seed, Revision = begin.Revision,
                SnapshotFile = filename, SnapshotHash = begin.Sha256, SnapshotFormat = begin.Format, SnapshotLength = bytes.Length,
                AccountLedgerFile=accountFilename,AccountLedgerHash=begin.AccountLedgerHash,AccountLedgerLength=accountBytes.Length };
            WriteAtomic(_metadataPath, JsonSerializer.SerializeToUtf8Bytes(updated, DedicatedProtocol.Json));
            string priorFile = _world.SnapshotFile; _world = updated; _snapshot = bytes;
            _ledger=accounts;_ledgerBytes=accountBytes;
            // Keep this checkpoint and its immediate predecessor, never touch player .sav files.
            foreach (string path in Directory.EnumerateFiles(_options.SaveRoot, "snapshot-*.bin"))
            {
                string name = Path.GetFileName(path);
                if (name == filename || name == priorFile) continue;
                string number = name.Substring(9, name.Length - 13);
                if (long.TryParse(number, out long revision) && revision > 0 && revision < begin.Revision) File.Delete(path);
            }
            Console.WriteLine("Persisted world checkpoint " + _world.Revision + " (" + bytes.Length + " bytes).");
        }
        private void ObserveDialogueRecovery(WorldEvent update, DialogueRecoveryState state, DateTime now)
        {
            DialogueRecoveryProtocol.ValidateWire(state);
            if (update.TargetPlayerId != state.TargetPlayerId || state.Sequence <= _lastDialogueSequence ||
                !_ledger.Accounts.Any(a => a.AccountId == state.OwnerAccountId && a.Initialized))
                throw new InvalidDataException("Dialogue recovery must target its existing authenticated account.");
            var target = _peers.FirstOrDefault(p => p.Role == "player" && p.Id == state.TargetPlayerId);
            if (target != null && target.Account.AccountId != state.OwnerAccountId)
                throw new InvalidDataException("Dialogue recovery target belongs to a different account.");
            _lastDialogueSequence = state.Sequence;
            if (state.Phase == "state")
            {
                if (_sleepTransitionActive || _travelTransitionActive || _apartmentLoads.TransitionActive)
                    throw new InvalidDataException("Dialogue recovery overlaps another native transition.");
                if (_dialogueLoads.Active && (_recoveryConversation != state.Conversation || _recoveryAccount != state.OwnerAccountId))
                    throw new InvalidDataException("Active dialogue recovery cannot transfer to another conversation/account.");
                _recoveryConversation = state.Conversation; _recoveryAccount = state.OwnerAccountId;
                _dialogueLoads.Begin(_world.Revision);
                foreach (var pending in _peers.Where(p => p.Role == "player" && p.AwaitingCheckpoint))
                {
                    pending.LoadStarted = now;
                    if (pending.Id != state.TargetPlayerId && now >= _nextDialogueWaitNotice)
                        Send(pending, "simulationState", new SimulationState { Ready = true, Message = "Waiting for an interrupted conversation to finish before loading the saved world." });
                }
                if (now >= _nextDialogueWaitNotice) _nextDialogueWaitNotice = now.AddSeconds(10);
                // A prior native import is already obsolete; never silently let
                // it become a loaded body while this fresh-state barrier is held.
                foreach (var pending in _peers.Where(p => p.Role == "player" && !p.Loaded && !p.AwaitingCheckpoint).ToArray())
                    Reject(pending, "dialogue_load_changed", "A conversation changed while your world was loading. Reconnect after it finishes.");
            }
            else if (state.Phase == "complete" && _dialogueLoads.Active &&
                state.Conversation == _recoveryConversation && state.OwnerAccountId == _recoveryAccount)
            {
                _dialogueLoads.Complete();
                // Revoke any pre-completion capture token. A new ledger/native
                // pair must have begun after completion to reopen world loading.
                _checkpointRequest = Guid.NewGuid().ToString("N");
                Send(_worker, "checkpointRequested", new CheckpointRequest { RequestId = _checkpointRequest });
                foreach (var pending in _peers.Where(p => p.Role == "player" && p.AwaitingCheckpoint)) pending.LoadStarted = now;
            }
        }
        private void ObserveApartmentTransition(WorldEvent update, DateTime now)
        {
            var state = update.Data.Deserialize<GroupApartmentEvent>(DedicatedProtocol.Json);
            if (state?.Phase == "prepare" && _incoming != null)
                throw new InvalidDataException("Apartment preparation overlapped an earlier checkpoint upload.");
            bool started = _apartmentLoads.Observe(state, _world.Revision);
            if (started)
            {
                var members = new HashSet<string>(state.Participants, StringComparer.Ordinal);
                foreach (Peer player in _peers.Where(p => p.Role == "player" && p.Connection.Connected))
                {
                    // A clientReady can race ahead of prepare on another socket.
                    // The worker's frozen participant set remains authoritative.
                    if (player.Loaded ? !members.Contains(player.Id) :
                        (!player.AwaitingCheckpoint && player.ExpectedRevision > 0))
                    { RejectStaleApartmentLoad(player); continue; }
                    if (player.AwaitingCheckpoint) player.LoadStarted = now;
                }
                Console.WriteLine(now.ToString("O") + " Apartment load fence generation=" + _apartmentLoads.Generation +
                    " minimumRevision=" + _apartmentLoads.MinimumRevision);
            }
            else if (state.Phase == "complete" || state.Phase == "cancel")
            {
                foreach (Peer player in _peers.Where(p => p.Role == "player" && p.AwaitingCheckpoint && p.Connection.Connected))
                    player.LoadStarted = now;
            }
        }
        private static void RejectStaleApartmentLoad(Peer peer)
            => Reject(peer, "apartment_transition_load", "The party changed apartment context while this world was loading. Restart the game and reconnect for the current world.");
        private void BeginSendingSnapshot(Peer peer)
        {
            if (peer.Role == "player" && (!_apartmentLoads.CanLoad(_world.Revision) || !_dialogueLoads.CanLoad(_world.Revision) || !_sleepLoads.CanLoad(_world.Revision)))
            {
                // This client has not imported anything yet. Its ordinary fresh
                // checkpoint token remains pending until the native final phase.
                peer.AwaitingCheckpoint = true; peer.LoadStarted = DateTime.UtcNow;
                if (_checkpointRequest.Length == 0 && _worker != null)
                {
                    _checkpointRequest = Guid.NewGuid().ToString("N");
                    Send(_worker, "checkpointRequested", new CheckpointRequest { RequestId = _checkpointRequest });
                }
                return;
            }
            AccountState own=null;
            if(peer.Role=="player"){
                own=_ledger.Accounts.FirstOrDefault(x=>x.AccountId==peer.Account.AccountId);
                if(own==null||!own.Initialized||own.AppearanceId!=peer.Account.AppearanceId){
                    peer.AwaitingCheckpoint=true;
                    if(_worker!=null){
                        Send(_worker,"accountPrepare",new AccountPreparation {PlayerId=peer.Id,AccountId=peer.Account.AccountId,Username=peer.Name,AppearanceId=peer.Account.AppearanceId});
                        _checkpointRequest=Guid.NewGuid().ToString("N");Send(_worker,"checkpointRequested",new CheckpointRequest {RequestId=_checkpointRequest});
                    }return;
                }
            }
            if (peer.Role == "player" && peer.AwaitingCheckpoint && _worker != null)
            {
                Send(_worker, "sleepRecoveryPresence", Presence(peer,false));
                Send(_worker, "dialogueRecoveryPresence", Presence(peer,false));
            }
            ClearDeliveries(peer);
            peer.Loaded = false; peer.AwaitingCheckpoint = false; peer.LoadStarted = DateTime.UtcNow; peer.ExpectedRevision = _world.Revision;
            peer.AccountOutgoing.Reset();peer.AccountOutgoing.StartSend(peer.Role=="worker"?_ledgerBytes:JsonSerializer.SerializeToUtf8Bytes(own,DedicatedProtocol.Json),_world.WorldId,_world.Revision,peer.Role=="worker"?"ledger":"account");
            if (_snapshot.Length == 0) {peer.EmptyWorldPending=true;return;}
            peer.Outgoing = new OutgoingSnapshot { Bytes = _snapshot, Begin = new SnapshotBegin { TransferId = Guid.NewGuid().ToString("N"),
                WorldId = _world.WorldId, Revision = _world.Revision, Length = _snapshot.Length, Sha256 = _world.SnapshotHash, Format = _world.SnapshotFormat,AccountLedgerHash=Hash(_ledgerBytes) } };
        }
        private void PumpSnapshot(Peer peer)
        {
            peer.AccountOutgoing.PumpSend(peer.Connection);if(peer.AccountOutgoing.IsSending)return;
            if(peer.EmptyWorldPending){if(peer.Connection.Send("worldLoadComplete",new WorldLoadComplete {WorldId=_world.WorldId,Revision=0}))peer.EmptyWorldPending=false;return;}
            OutgoingSnapshot outgoing = peer.Outgoing;
            if (outgoing == null || !peer.Connection.Connected) return;
            if (!outgoing.Begun) { if (!peer.Connection.Send("snapshotBegin", outgoing.Begin)) return; outgoing.Begun = true; }
            for (int i = 0; i < 4 && outgoing.Offset < outgoing.Bytes.Length && peer.Connection.PendingSendBytes < 256 * 1024; i++)
            {
                int count = Math.Min(DedicatedProtocol.SnapshotChunkBytes, outgoing.Bytes.Length - outgoing.Offset);
                byte[] bytes = new byte[count]; Buffer.BlockCopy(outgoing.Bytes, outgoing.Offset, bytes, 0, count);
                if (!peer.Connection.Send("snapshotChunk", new SnapshotChunk { TransferId = outgoing.Begin.TransferId, Offset = outgoing.Offset, Data = bytes })) return;
                outgoing.Offset += count;
            }
            if (outgoing.Offset != outgoing.Bytes.Length) return;
            if (!peer.Connection.Send("snapshotEnd", new SnapshotEnd { TransferId = outgoing.Begin.TransferId })) return;
            if (!peer.Connection.Send("worldLoadComplete", new WorldLoadComplete { WorldId = _world.WorldId, Revision = outgoing.Begin.Revision }))
            { peer.Connection.Dispose(); return; }
            peer.Outgoing = null;
        }
        private ServerStatus Status() => new ServerStatus { Name = _options.Name, WorldId = _world.WorldId, Players = _peers.Count(p => p.Role == "player"),
            ServerInstanceId = _instanceId, ServerId=_accounts.Identity.ServerId, MaxPlayers = _options.MaxPlayers, Port = _options.Port, PasswordRequired = _options.Password.Length > 0, Ready = _ready,
            State = _ready ? "Unity simulation ready" : _worker == null ? "Waiting for Unity simulation worker" : "Unity simulation loading or unavailable" };
        private void SetReady(bool ready, string reason)
        {
            if (_ready == ready) return;
            _ready = ready;
            if (!ready) { _checkpointRequest = ""; _lastSleepSequence = _lastDialogueSequence = 0;
                _dialogueLoads.Reset(); _sleepLoads.Reset(); _recoveryConversation = _recoveryAccount = ""; _sleepTransitionActive = _travelTransitionActive = false; _nextDialogueWaitNotice = default; }
            foreach (Peer peer in _peers.Where(p => p.Role == "player"))
            { if (!ready) { ClearDeliveries(peer); peer.Loaded = false; peer.AwaitingCheckpoint = false; peer.Outgoing = null;peer.AccountOutgoing.Reset();peer.EmptyWorldPending=false; peer.ExpectedRevision = 0; } Send(peer, "simulationState", new SimulationState { Ready = ready, Message = reason }); }
            Console.WriteLine("ready=" + ready + ": " + reason);
        }
        private void Remove(Peer peer)
        {
            if (!_peers.Remove(peer)) return;
            if (peer == _worker) { _worker = null; _bulkGranted = _controlGranted = 0; _incoming?.Dispose(); _incoming = null;_accountIncoming.Reset();_appearances=Array.Empty<AppearanceOption>(); _apartmentLoads.Reset(); SetReady(false, "Unity simulation worker disconnected."); }
            else if (peer.Role == "player" && _worker != null)
            {
                Send(_worker, "sleepParticipantPresence", Presence(peer,false));
                Send(_worker, "sleepRecoveryPresence", Presence(peer,false));
                Send(_worker, "dialogueRecoveryPresence", Presence(peer,false));
                Send(_worker, "playerPresence", Presence(peer,false));
            }
            string reason = peer.Connection.Status;
            peer.Connection.Dispose(); Console.WriteLine(DateTime.UtcNow.ToString("O") + " Disconnected " + PeerDiagnostic(peer) + " transport=" + reason);
        }
        private static void ClearDeliveries(Peer peer)
        { peer.Deliveries.Clear(); peer.WorldQueue.Clear(); peer.PendingPoses.Clear(); peer.BulkInFlight = peer.ControlInFlight = 0; peer.RecoveryState = null; peer.DialogueRecovery.Reset(); peer.SceneMembership.Reset(); peer.MapCatchup.Reset(); }
        private static void AcknowledgeWorldEvents(Peer peer, WorldEventAcknowledgement ack)
        {
            // A worker loss clears Loaded and receipts before an already-applied
            // client's ACK can cross the other TCP stream. Validate its issued IDs
            // and accept it idempotently, preserving the original disconnect cause.
            if (ack?.DeliveryIds == null || ack.DeliveryIds.Length < 1 || ack.DeliveryIds.Length > WorldEventFlow.MaxAcknowledgements)
                throw new InvalidDataException();
            var unique = new HashSet<long>();
            // Validate the whole batch before releasing any credit.
            foreach (long id in ack.DeliveryIds)
                if (id < 1 || id > peer.LastDeliveryId || !unique.Add(id)) throw new InvalidDataException();
            foreach (long id in ack.DeliveryIds)
            {
                if (!peer.Deliveries.Remove(id, out var delivery)) continue; // repeated past ACK is harmless
                if (delivery.Control) peer.ControlInFlight -= delivery.Cost; else peer.BulkInFlight -= delivery.Cost;
                peer.WorldAcknowledgedCostBytes += delivery.Cost;
            }
        }
        private static string ReceiptDiagnostic(Peer peer, DateTime now)
        {
            const int maximum = 8;
            var oldest = peer.Deliveries.OrderBy(item => item.Value.Sent).ThenBy(item => item.Key).Take(maximum);
            var rows = oldest.Select(item => "id=" + item.Key + ",kind=" + item.Value.Kind + ",sequence=" + item.Value.Sequence +
                ",ageSeconds=" + Math.Max(0, (now - item.Value.Sent).TotalSeconds).ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                ",cost=" + item.Value.Cost + ",control=" + item.Value.Control);
            return "pending=" + peer.Deliveries.Count + " lastIssued=" + peer.LastDeliveryId +
                " oldestDeliveries=[" + string.Join(";", rows) + "] omitted=" + Math.Max(0, peer.Deliveries.Count - maximum);
        }
        private static void LogWorldDelivery(Peer peer, DateTime now)
        {
            if (peer.Role != "player" || !peer.Loaded || (now - peer.LastWorldDeliveryDiagnostic).TotalSeconds < 10) return;
            peer.LastWorldDeliveryDiagnostic = now;
            double queueAge = peer.WorldQueue.TryPeek(int.MaxValue, int.MaxValue, out var head)
                ? Math.Max(0, (now - DateTime.UnixEpoch).TotalSeconds - head.QueuedAt) : 0;
            double receiptAge = peer.Deliveries.Count == 0 ? 0 :
                Math.Max(0, (now - peer.Deliveries.Values.Min(delivery => delivery.Sent)).TotalSeconds);
            Console.WriteLine(FormattableString.Invariant($"{now:O} WORLD_DELIVERY player={peer.Id} queueEvents={peer.WorldQueue.Count} queueCostBytes={peer.WorldQueue.Bytes} queueOldestSeconds={queueAge:F3} issued={peer.Deliveries.Count} issuedOldestSeconds={receiptAge:F3} bulkInFlight={peer.BulkInFlight} controlInFlight={peer.ControlInFlight} playerBulkWindow={PlayerBulkWindowBytes} sentCostBytesTotal={peer.WorldSentCostBytes} acknowledgedCostBytesTotal={peer.WorldAcknowledgedCostBytes} pendingSendBytes={peer.Connection.PendingSendBytes}"));
        }
        private void QueueMapCatchupBarrier(Peer peer, DateTime now)
        {
            var marker = peer.MapCatchup.BeginBarrierRound((now - DateTime.UnixEpoch).TotalSeconds);
            var update = new WorldEvent { WorldId = _world.WorldId, Revision = _world.Revision,
                TargetPlayerId = peer.Id, Kind = MapCatchupBarrier.EventKind,
                Data = JsonSerializer.SerializeToElement(marker, DedicatedProtocol.Json) };
            peer.WorldQueue.Enqueue(update, WorldEventFlow.Cost(update), WorldEventFlow.IsControlKind(update.Kind),
                (now - DateTime.UnixEpoch).TotalSeconds);
            Console.WriteLine(now.ToString("O") + " MAP_CATCHUP_BARRIER player=" + peer.Id + " round=" + marker.Round +
                " queuedEvents=" + peer.WorldQueue.Count + " queuedCostBytes=" + peer.WorldQueue.Bytes);
        }
        private void PumpMapCatchup(Peer peer, DateTime now)
        {
            if (!peer.Connection.Connected || !peer.Loaded || !peer.MapCatchup.BarrierRequested) return;
            try
            {
                long oldest = peer.Deliveries.Count == 0 ? 0 : peer.Deliveries.Keys.Min();
                if (peer.MapCatchup.TryConfirm((now - DateTime.UnixEpoch).TotalSeconds, oldest, out var confirmation))
                {
                    Send(peer, MapCatchupConfirmation.MessageKind, confirmation);
                    Console.WriteLine(now.ToString("O") + " MAP_CATCHUP_CONFIRMED player=" + peer.Id + " round=" + confirmation.Round +
                        " barrierDelivery=" + confirmation.BarrierDeliveryId);
                }
                else if (peer.MapCatchup.NeedsNextBarrier) QueueMapCatchupBarrier(peer, now);
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException)
            { Reject(peer, "slow_client", "Your map could not reach a fresh world frontier within its bounded synchronization period."); }
        }
        private void GrantWorldCredits()
        {
            if (!_ready || _worker == null || !_worker.Connection.Connected) return;
            // Ingress has one small bounded window. Each recipient has its own
            // bounded FIFO and delivery window, so a loading player's unACKed
            // packets cannot pause NPC capture or another player's simulation.
            int bulk = WorldEventFlow.BulkWindowBytes, control = WorldEventFlow.ControlWindowBytes;
            bulk -= _bulkGranted; control -= _controlGranted;
            if (bulk < 0 || control < 0) throw new InvalidOperationException("Invalid world flow credit accounting.");
            if (bulk == 0 && control == 0) return;
            if (_worker.Connection.Send("worldFlowCredit", new WorldFlowCredit { BulkBytes = bulk, ControlBytes = control }))
            { _bulkGranted += bulk; _controlGranted += control; }
        }
        private static void PumpWorldEvents(Peer peer, DateTime now)
        {
            if (!peer.Connection.Connected || peer.WorldQueue.Count == 0) return;
            if (peer.WorldQueue.IsExpired((now - DateTime.UnixEpoch).TotalSeconds))
            { Reject(peer, "slow_client", "Your ordered world queue could not drain within its bounded loading period."); return; }
            for (int count = 0; count < 32 && peer.Connection.Connected && peer.Deliveries.Count < PlayerMaxInFlightEvents && peer.Connection.PendingSendBytes < 128 * 1024 &&
                peer.WorldQueue.TryPeek(PlayerBulkWindowBytes - peer.BulkInFlight,
                    WorldEventFlow.ControlWindowBytes - peer.ControlInFlight, out var entry); count++)
            {
                var source = entry.Event;
                var update = new WorldEvent { WorldId = source.WorldId, Revision = source.Revision, Kind = source.Kind,
                    TargetPlayerId = source.TargetPlayerId, Data = source.Data, DeliveryId = peer.LastDeliveryId + 1 };
                if (!peer.Connection.Send("worldEvent", update)) return;
                if (update.Kind == MapCatchupBarrier.EventKind)
                {
                    try { peer.MapCatchup.BarrierIssued(update.Data.Deserialize<MapCatchupBarrier>(DedicatedProtocol.Json), update.DeliveryId); }
                    catch (InvalidDataException)
                    { Reject(peer, "map_catchup_changed", "The map synchronization context changed. Reconnect to restore your character."); return; }
                }
                // IDs and timeout clocks belong only to packets actually accepted
                // by this connection. Queued unsent state has its separate bound.
                if (update.Kind == PlayerSceneContext.EventKind)
                {
                    try { peer.MapCatchup.ObserveContext(update.Data.Deserialize<PlayerSceneContext>(DedicatedProtocol.Json),
                        (now - DateTime.UnixEpoch).TotalSeconds, peer.LastDeliveryId); }
                    catch (InvalidDataException)
                    { Reject(peer, "map_catchup_changed", "The map synchronization context changed. Reconnect to restore your character."); return; }
                }
                peer.LastDeliveryId = update.DeliveryId;
                peer.WorldQueue.Remove(entry);
                if (!peer.Loaded && update.Kind == SleepRecoveryProtocol.EventKind)
                {
                    var sleep = update.Data.Deserialize<GroupSleepEvent>(DedicatedProtocol.Json);
                    if (!SleepRecoveryProtocol.IsResponse(sleep))
                    {
                        peer.RecoveryState = sleep.Phase == "complete" || sleep.Phase == "cancelled" || sleep.Phase == "idle" ? null : sleep;
                        peer.LoadStarted = now;
                    }
                }
                if (update.Kind == DialogueRecoveryProtocol.EventKind)
                { peer.DialogueRecovery.Observe(update.Data.Deserialize<DialogueRecoveryState>(DedicatedProtocol.Json)); peer.LoadStarted = now; }
                long sequence = -1;
                if (update.Data.ValueKind == JsonValueKind.Object && update.Data.TryGetProperty("Sequence", out var value) &&
                    value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var parsed)) sequence = parsed;
                peer.Deliveries.Add(update.DeliveryId, new Delivery { Cost = entry.Cost, Control = entry.Control,
                    Sent = now, Kind = update.Kind, Sequence = sequence });
                peer.WorldSentCostBytes += entry.Cost;
                if (entry.Control) peer.ControlInFlight += entry.Cost; else peer.BulkInFlight += entry.Cost;
            }
        }
        private static void PumpPoses(Peer peer)
        {
            if (!peer.Loaded || peer.PendingPoses.Count == 0 || peer.Connection.PendingSendBytes >= 64 * 1024) return;
            foreach (var pose in peer.PendingPoses.Values) if (!peer.Connection.Send("playerPose", pose)) break;
            peer.PendingPoses.Clear();
        }
        private static void Send<T>(Peer peer, string type, T payload)
        {
            if (!peer.Connection.Send(type, payload))
            { Console.WriteLine(DateTime.UtcNow.ToString("O") + " Send failed type=" + type + " " + PeerDiagnostic(peer)); peer.Connection.Dispose(); }
        }
        private static void Reject(Peer peer, string code, string message)
        {
            Console.WriteLine(DateTime.UtcNow.ToString("O") + " Reject code=" + code + " reason=" + message + " " + PeerDiagnostic(peer));
            peer.Connection.Send("error", new ProtocolError { Code = code, Message = message }); peer.Connection.Tick(); peer.Connection.Dispose();
        }
        private static string PeerDiagnostic(Peer peer) => "role=" + (peer.Role.Length == 0 ? "pending" : peer.Role) + " id=" + peer.Id +
            " lastType=" + peer.LastMessageType + " worldKind=" + peer.LastWorldKind + " wireBytes=" + peer.LastFrameBytes +
            " requests=" + peer.Requests + " interactions=" + peer.Interactions + " pendingSendBytes=" + peer.Connection.PendingSendBytes +
            " queuedWorldEvents=" + peer.WorldQueue.Count + " queuedWorldBytes=" + peer.WorldQueue.Bytes +
            " deliveries=" + peer.Deliveries.Count + " bulkInFlight=" + peer.BulkInFlight + " controlInFlight=" + peer.ControlInFlight;
        private static string Hash(byte[] bytes) { using var sha = SHA256.Create(); return Convert.ToHexString(sha.ComputeHash(bytes)); }
        private static void WriteAtomic(string path, byte[] bytes)
        {
            string temporary = path + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None)) { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        private void Discovery(DateTime now)
        {
            if (_discovery == null) return;
            if ((now - _discoveryWindow).TotalSeconds >= 1) { _discoveryWindow = now; _discoveryReplies = 0; }
            for (int i = 0; i < 8; i++)
            {
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0); int size;
                try { size = _discovery.ReceiveFrom(_discoveryBuffer, ref remote); }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock) { return; }
                catch (SocketException) { return; }
                // Discovery only replies to RFC1918/link-local/loopback clients, never acts as an internet amplifier.
                byte[] ip = ((IPEndPoint)remote).Address.GetAddressBytes();
                bool local = ip[0] == 10 || ip[0] == 127 || (ip[0] == 192 && ip[1] == 168) || (ip[0] == 172 && ip[1] >= 16 && ip[1] <= 31) || (ip[0] == 169 && ip[1] == 254);
                if (!local || _discoveryReplies >= 8 || size != "NIVALIS_DISCOVER_V1".Length || Encoding.ASCII.GetString(_discoveryBuffer, 0, size) != "NIVALIS_DISCOVER_V1") continue;
                byte[] response = JsonSerializer.SerializeToUtf8Bytes(Status(), DedicatedProtocol.Json);
                try { _discovery.SendTo(response, remote); _discoveryReplies++; } catch (SocketException) { }
            }
        }
        public void Dispose()
        { foreach (Peer peer in _peers) peer.Connection.Dispose(); _incoming?.Dispose();_accountIncoming.Reset();_accounts.Dispose(); _listener?.Stop(); _discovery?.Dispose(); _worldLock?.Dispose(); }
        public bool ConsumeStopRequest()
        {
            if (_options.StopFile.Length == 0 || !File.Exists(_options.StopFile)) return false;
            if ((File.GetAttributes(_options.StopFile) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Stop marker must be a regular file.");
            File.Delete(_options.StopFile); Console.WriteLine("Graceful service stop requested."); return true;
        }
    }
}
