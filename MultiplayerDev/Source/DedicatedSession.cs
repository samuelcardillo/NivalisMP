using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using Nivalis;
using NivalisTogether.Dedicated;
using UnityEngine;

namespace NivalisTogether
{
    // Unity objects are accessed exclusively by Tick on Unity's main thread.
    internal sealed class DedicatedSession : IDisposable
    {
        private readonly bool worker = Plugin.Worker;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly WorldSnapshotTransfer transfer = new WorldSnapshotTransfer();
        private readonly AccountDataTransfer accountTransfer = new AccountDataTransfer();
        private readonly AccountStateChunks accountUpdates = new AccountStateChunks();
        private readonly Dictionary<string, string> playerAccounts = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Queue<AccountPreparation> accountPreparations = new Queue<AccountPreparation>();
        private readonly List<PreparedPropertyChange> pendingProperties = new List<PreparedPropertyChange>();
        private readonly List<(long Revision, WorldEvent Update)> pendingTransactionResults = new List<(long, WorldEvent)>();
        private AccountClientFlow accountFlow;
        private AccountWorldState accountLedger, committingLedger;
        private AccountState ownAccount;
        private string accountId = "", endpoint = "", disconnectReason = "Disconnected. Choose a server to reconnect.";
        private string lastDisconnectWait = "";
        private bool transactionDirty, capturingAccountTransaction, publishingCommit, sawAccountDirty;
        private readonly Dictionary<string, PlayerPose> players = new Dictionary<string, PlayerPose>();
        private readonly HashSet<string> present = new HashSet<string>();
        private readonly WorldEventScheduler events = new WorldEventScheduler();
        private readonly List<long> appliedDeliveries = new List<long>();
        private readonly Dictionary<long, (string Kind, double Received)> pendingReceiptDiagnostics = new Dictionary<long, (string, double)>();
        private readonly HashSet<string> firstAppliedKinds = new HashSet<string>(StringComparer.Ordinal);
        private int bulkCredit, controlCredit, captureTurn;
        private double nextWorldAcknowledgement;
        private readonly Dictionary<string, TrafficTally> queuedTraffic = new Dictionary<string, TrafficTally>(StringComparer.Ordinal);
        private readonly Dictionary<string, TrafficTally> sentTraffic = new Dictionary<string, TrafficTally>(StringComparer.Ordinal);
        private readonly Dictionary<string, TrafficTally> receivedTraffic = new Dictionary<string, TrafficTally>(StringComparer.Ordinal);
        private sealed class TrafficTally { public long Count, Bytes; }
        private long snapshotWireBytes, acknowledgedEvents;
        private double lastTrafficDiagnostic;
        private readonly Queue<(WorldEvent Update, int Bytes)> deferredTravelEvents = new Queue<(WorldEvent, int)>();
        // Keep one fence/worker lease across all snapshot families. The existing
        // field and pump names remain stable for session diagnostics/profilers.
        private readonly BoundedPreparationFence<IPreparedWorldUpdate, WorldEvent> economyPreparation =
            new BoundedPreparationFence<IPreparedWorldUpdate, WorldEvent>();
        private int deferredTravelBytes;
        // Native entity steps can be much cheaper than packet dispatch. Keep
        // the shared 4ms deadline while allowing a cheap roster to finish sooner;
        // receive/replay loops retain their independent message-count limits.
        private ClientMutationBudget clientMutationBudget = new ClientMutationBudget(Stopwatch.Frequency, maximumActions: 256);
        private ClientMutationBudget normalClientMutationBudget;
        private readonly ClientMutationBudget mapCatchupMutationBudget = new ClientMutationBudget(Stopwatch.Frequency, milliseconds: 12, maximumActions: 1024);
        private bool mutationFrameSelected;
        private int mutationFrame;
        private readonly HashSet<long> retainedMapDeliveries = new HashSet<long>();
        private string mapCatchupEpoch = "";
        private long mapCatchupGeneration, mapCatchupAppliedDelivery, mapCatchupReportedDelivery;
        private bool mapCatchupBarrierRequested, mapCatchupBarrierReported;
        private int mapCatchupBarrierRound;
        private long mapCatchupBarrierDelivery;
        private double mapCatchupSettledAt = -1, nextMapCatchupProgress;
        private IPreparedWorldUpdate progressingWorldUpdate;
        private string progressingWorldScene, progressingWorldProperty;
        private ServerCatalog catalog;
        private MultiplayerMenu menu;
        private PlayerReplicaRenderer avatars;
        private DedicatedConnection connection;
        private Task<DedicatedConnection> connecting;
        private CancellationTokenSource connectingCancellation;
        private WorldMetadata world;
        private string name, secret = "", playerId = "", status = "", logPath;
        private string checkpointRequest = "", stopFile = "", workerCrowdScene = "";
        private bool accepted, haveWorld, loading, worldStarted, loaded, ready, awaitingCommit, shuttingDown, disposed;
        private long clientRevision, finalRevision;
        private int capturedNpcCount;
        private double nextPing, nextCheckpoint, nextNpc, nextWeather, nextReady, nextPose, nextDiagnostic, nextGameplay, nextEconomy, nextCrowd, nextBusiness;
        private bool autoJoin, terminalSession, returningToTitle, interactionStateReset, travelEnabled, travelWasBusy, committedThisSession;
        private bool sleepEnabled, sleepWasBusy;
        private readonly SleepWakeBarrier sleepWake = new SleepWakeBarrier();
        private SleepWakeMarker pendingWakeMarker;
        private string acknowledgedWakeToken = "";
        private long wakeCheckpoint;
        private int wakeCaptureFamily;
        private double wakeDeadline;
        private bool apartmentEnabled, apartmentWasBusy, apartmentTravelContextPending;
        private long apartmentCheckpointRevision;
        private WorldEvent orderedTransitionEvent;
        private readonly PlayerSceneMembership clientScene = new PlayerSceneMembership();
        private PlayerMapRestore clientMapRestore;
        private bool clientMapRestoreAcknowledged;
        private bool npcMapPresentationPending;
        private sealed class PendingMapInteraction
        {
            public InteractionRequest Request;
            public PlayerMapLocation Location;
            public double Expires;
        }
        private readonly Queue<PendingMapInteraction> mapInteractions = new Queue<PendingMapInteraction>();
        private PendingMapInteraction focusingInteraction;
        public DedicatedSession()
        {
            ClientInputDiagnostics.Reset();
            ClientInputSchemeGuard.SetEnabled(false);
            name = Plugin.Argument("-nivalis-name", Plugin.Instance.PlayerName.Value);
            NpcWorldReplication.Diagnostic = Log;
            WorkerNpcSimulation.Diagnostic = Log;
            CrowdReplication.Diagnostic = Log;
            QuestReplication.Diagnostic = Log;
            BusinessStateReplication.Diagnostic = Log;
            BusinessCraftStateReplication.Diagnostic = Log;
            SharedEconomyReplication.Diagnostic = Log;
            GameplayReplication.Notice = Status;
            GameplayReplication.OutboundInteraction = SendInteraction;
            GameplayReplication.OutboundWorldEvent = QueueEvent;
            DialogueReplication.Reset(); BusinessReplication.Reset();
            DialogueReplication.Notice = Status; BusinessReplication.Notice = Status;
            DialogueReplication.OutboundInteraction = SendInteraction;
            DialogueReplication.OutboundWorldEvent = QueueEvent;
            BusinessReplication.OutboundInteraction = SendInteraction;
            BusinessReplication.OutboundWorldEvent = QueueEvent;
            PropertyOwnershipReplication.OutboundInteraction = SendInteraction;
            PropertyOwnershipReplication.OutboundWorldEvent = QueueEvent;
            PropertyOwnershipReplication.Notice = Status;
            PropertyOwnershipReplication.BeginTransaction = BeginPropertyTransaction;
            GameplayReplication.ClientInteractionExtension = focus =>
                PropertyOwnershipReplication.GuardClientInteraction(focus) ??
                ApartmentReplication.RouteClientInteraction(focus) ?? SleepReplication.RouteClientInteraction(focus) ??
                BoatReplication.TryClientInteraction(focus) ??
                DialogueReplication.RouteClientInteraction(focus) ?? BusinessReplication.TryRouteClientInteraction(focus);
            if (worker)
            {
                // Plugin.Load already selected isolated worker storage. Install
                // spatial authority before any fresh world or snapshot is loaded.
                SpatialQuestReplication.Configure(true, () => WorkerSceneResidency.IsBusy || TravelReplication.IsBusy || SleepReplication.IsBusy || ApartmentReplication.IsBusy || shuttingDown || terminalSession || disposed);
                // Install native dialogue choice guards before a new/load world can
                // start an automatic conversation with no player to answer it.
                DialogueReplication.SetClientAuthority(false); BusinessReplication.SetClientAuthority(false);
                Application.runInBackground = true; Application.targetFrameRate = 30;
                logPath = Path.Combine(DedicatedWorldRuntime.StorageDirectory, "multiplayer-session.log");
                stopFile = Plugin.Argument("-nivalis-stop-file");
                Connect(Plugin.Argument("-nivalis-server", "127.0.0.1"), int.Parse(Plugin.Argument("-nivalis-port", "47778")),
                    "Simulation", File.ReadAllText(Plugin.Argument("-nivalis-authority-file")).Trim());
            }
            else
            {
                catalog = new ServerCatalog(Path.Combine(Paths.ConfigPath, "NivalisTogether.servers.json"));
                menu = new MultiplayerMenu(catalog.Entries, catalog.Refresh, Join, catalog.Save, catalog.Remove, () => {}, Log, Disconnect);
                accountFlow = new AccountClientFlow(menu, Path.Combine(Paths.ConfigPath, "NivalisTogether.server-identities.json"),
                    profile => { accountId = profile.AccountId; name = profile.Username;
                        if (!connection.Send("accountJoin", new {})) throw new IOException("Cannot join the account world."); },
                    message => Fail(new IOException(message)), Disconnect);
                menu.SetPlayerName(name); catalog.Refresh(); avatars = new PlayerReplicaRenderer(Log);
                autoJoin = !string.IsNullOrEmpty(Plugin.Argument("-nivalis-connect"));
            }
        }
        private double Now => clock.Elapsed.TotalSeconds;
        private void Log(string value)
        {
            Plugin.Instance.Log.LogInfo(value);
            if (logPath != null) File.AppendAllText(logPath, DateTime.UtcNow.ToString("O") + " " + value + Environment.NewLine);
        }
        private void Status(string value)
        { if (status == value) return; status = value; Log(value); menu?.SetStatus(value); menu?.SetSessionStatus(value, accepted && connection != null && connection.Connected); }
        private void Join(MultiplayerJoinRequest request)
        {
            try
            {
                if (terminalSession || DedicatedWorldRuntime.IsIsolated) throw new InvalidOperationException("Wait for the current world to finish disconnecting.");
                if (!DedicatedWorldRuntime.IsClientTitleReady())
                    throw new InvalidOperationException("Join multiplayer from the title screen.");
                if (!DedicatedProtocol.Text(request.PlayerName.Trim(), 24)) throw new ArgumentException("Enter a player name (1-24 characters).");
                Plugin.Instance.PlayerName.Value = request.PlayerName.Trim();
                Connect(request.Server.Host, request.Server.Port, request.PlayerName.Trim(), request.Password);
            }
            catch (Exception ex) { Status(ex.Message); menu?.SetBusy(false); }
        }
        private void Connect(string host, int port, string playerName, string password)
        {
            if (connecting != null || connection != null) throw new InvalidOperationException("A connection is already active.");
            interactionStateReset = false;
            WorkerSceneResidency.Configure(reason => Fail(new IOException(reason)));
            WorkerSceneResidency.Diagnostic = Log;
            name = playerName; secret = password;
            string endpointHost = host.Trim().Trim('[', ']').ToLowerInvariant();
            endpoint = (endpointHost.Contains(":") ? "[" + endpointHost + "]" : endpointHost) + ":" + port; menu?.SetBusy(true);
            Status("Connecting to " + host + ":" + port + "...");
            connectingCancellation = new CancellationTokenSource();
            connecting = DedicatedConnection.ConnectAsync(host, port, cancellationToken: connectingCancellation.Token);
        }
        private void SendInteraction(InteractionRequest request)
        {
            bool recovery = !worker && accepted && !loaded &&
                (SleepReplication.CanSendRecovery(request) || DialogueReplication.CanSendRecovery(request));
            if (worker || (!ready && !recovery) || connection == null || !connection.Connected)
                throw new InvalidOperationException("The shared world connection is not ready.");
            request.PlayerId = playerId;
            request.ContextGeneration = clientScene.Current?.Generation ?? 0;
            if (!recovery && !clientScene.AllowsInteraction(request))
                throw new InvalidOperationException("Wait for your character's map to finish loading.");
            if (!recovery && deferredTravelEvents.Count > 0 && !PlayerSceneMembership.IsTransitionAcknowledgement(request.Kind))
                throw new InvalidOperationException("Your map is catching up with the shared world.");
            if (!recovery && IsNativeMapCatchup && !SessionTransitionPolicy.IsCatchupAcknowledgement(request, pendingWakeMarker))
                throw new InvalidOperationException("Your map is catching up with the shared world.");
            if (economyPreparation.IsFenced && !SessionTransitionPolicy.IsOrderedAcknowledgement(orderedTransitionEvent, request, economyPreparation.IsPending))
                throw new InvalidOperationException("Wait for the shared inventory update to finish.");
            if (ApartmentReplication.IsBusy && !ApartmentReplication.Handles(request))
                throw new InvalidOperationException("Finish the party's apartment transition first.");
            if (TravelReplication.IsBusy && !TravelReplication.Handles(request))
                throw new InvalidOperationException("Wait for party travel to finish.");
            if (SleepReplication.IsBusy && !SleepReplication.Handles(request) && request.Kind != SleepWakeProtocol.ReadyKind)
                throw new InvalidOperationException("Finish the shared day transition first.");
            if (!connection.Send("interaction", request))
                throw new IOException("The connection is busy; try the interaction again.");
        }
        private void SendChat(ChatRequest request)
        {
            if (worker || !accepted || !loaded || terminalSession || shuttingDown ||
                connection == null || !connection.Connected)
                throw new InvalidOperationException("The shared world connection is not ready.");
            if (!ChatProtocol.TryValidateRequest(request, out string error))
                throw new ArgumentException(error);
            // Chat is service-routed and does not wait for simulation transactions.
            if (!connection.Send(ChatProtocol.SendKind, request))
                throw new IOException("The connection is busy; try the message again.");
        }
        public void Tick()
        {
            if (disposed) return;
            try
            {
                menu?.Tick();
                if (!worker) SleepLoadingOverlay.Tick();
                if (!worker) PlayerChatOverlay.Tick(accepted && loaded && ready && !loading &&
                    !terminalSession && !returningToTitle && !shuttingDown && connection != null && connection.Connected &&
                    DedicatedWorldRuntime.IsWorldReady && clientScene.IsAssigned && !clientScene.TravelPending &&
                    !WorkerSceneResidency.IsRestoringClient && !TravelReplication.IsBusy && !ApartmentReplication.IsBusy &&
                    !IsNativeMapCatchup &&
                    !(menu?.IsOpen ?? false));
                if (terminalSession) { FinishDisconnectedWorld(); return; }
                if (autoJoin && DedicatedWorldRuntime.IsClientTitleReady())
                {
                    autoJoin = false;
                    string endpoint = Plugin.Argument("-nivalis-connect");
                    int split = endpoint.LastIndexOf(':');
                    string passwordFile = Plugin.Argument("-nivalis-password-file");
                    Join(new MultiplayerJoinRequest { Server = new MultiplayerServerEntry { Host = split < 0 ? endpoint : endpoint.Substring(0, split),
                        Port = split < 0 ? 47778 : int.Parse(endpoint.Substring(split + 1)) }, PlayerName = name,
                        Password = passwordFile.Length == 0 ? "" : File.ReadAllText(passwordFile).Trim() });
                }
                if (connecting != null && connecting.IsCompleted)
                {
                    var task = connecting; connecting = null;
                    connectingCancellation?.Dispose(); connectingCancellation = null;
                    connection = task.GetAwaiter().GetResult(); nextPing = Now + 5;
                    Status("Connected; authenticating...");
                }
                if (connection == null) return;
                connection.Tick();
                if (!connection.Connected) throw new IOException(connection.Status);
                if (!worker)
                {
                    BeginClientMutationFrame(Time.frameCount);
                    UpdateMapCatchupPresentation();
                    // Older prepared state and its FIFO precede new world work.
                    // A dead socket above must never commit a stale preparation.
                    PumpEconomyPreparation();
                    if (disposed || connection == null || terminalSession) return;
                }
                // Preserve receive order, but yield between atomic messages so a
                // backlog cannot consume an entire rendered frame. Receive still
                // runs after an expensive commit: exhausted world mutations join
                // the unACKed FIFO while poses, errors and pings keep progressing.
                long receiveStarted = Stopwatch.GetTimestamp();
                for (int received = 0; received < 32 && connection != null && !disposed &&
                    (received == 0 || ElapsedMilliseconds(receiveStarted) < 4) && connection.TryReceive(out var message); received++)
                    Receive(message);
                accountFlow?.Tick();
                if (disposed || connection == null) return;
                if (!connection.Connected) throw new IOException(connection.Status);
                FlushWorldAcknowledgements();
                if (Now >= nextPing && (accepted || accountFlow?.Profile != null)) { connection.Send("ping", new {}); nextPing = Now + 5; }
                if ((DateTime.UtcNow - connection.LastReceiveUtc).TotalSeconds > (accountFlow?.IsActive == true ? 180 : 40))
                    throw new IOException("The server stopped responding.");
                if (worker) TickWorker(); else TickClient();
                if (disposed || connection == null) return;
                for (int i = 0; i < 24 && connection.PendingSendBytes < 128 * 1024 && events.TryPeek(bulkCredit, controlCredit, out var pending); i++)
                {
                    var update = pending.Event; int beforeEvent = connection.PendingSendBytes;
                    if (!connection.Send("worldEvent", update)) break;
                    if (WorldEventFlow.IsControlKind(update.Kind)) controlCredit -= pending.Cost; else bulkCredit -= pending.Cost;
                    AddTraffic(sentTraffic, update.Kind, connection.PendingSendBytes - beforeEvent);
                    events.Remove(pending);
                }
                // Put live controls ahead of the next bounded snapshot slice.
                if (connection.PendingSendBytes < 64 * 1024)
                {
                    int beforeSnapshot = connection.PendingSendBytes;
                    accountTransfer.PumpSend(connection);
                    if (!accountTransfer.IsSending) transfer.PumpSend(connection);
                    snapshotWireBytes += connection.PendingSendBytes - beforeSnapshot;
                }
                FlushWorldAcknowledgements();
                if (!worker) ReportMapCatchupProgress();
                connection.Tick();
                if (Now >= nextDiagnostic)
                {
                    nextDiagnostic = Now + 10;
                    Log("STATE accepted=" + accepted + " boot=" + DedicatedWorldRuntime.IsBootReady + " world=" + DedicatedWorldRuntime.IsWorldReady +
                        " loaded=" + loaded + " ready=" + ready + " revision=" + (world?.Revision ?? -1) + " scene=" + DedicatedWorldRuntime.Scene + " outgoing=" + connection.PendingSendBytes);
                    LogTraffic();
                    if (loaded && !TravelReplication.IsBusy && DedicatedWorldRuntime.IsWorldReady)
                    {
                        var currentTime = TimeOfDayManager._instance?.currentTime;
                        var position = ReadPose();
                        Log("SIM gameSeconds=" + (currentTime == null ? -1 : TimeOfDayManager.CurrentTime.TotalGameSeconds) +
                            " npcRecords=" + (worker ? capturedNpcCount : NpcWorldReplication.ReplicaCount) +
                            " npcSequence=" + NpcWorldReplication.LastCompleteSequence +
                            " crowdRecords=" + CrowdReplication.LastCapturedCount + " crowdViews=" + CrowdReplication.RenderedCount +
                            " position=" + position.X + "," + position.Y + "," + position.Z +
                            " economyHash=" + SharedEconomyReplication.LastCaptureHash);
                    }
                }
            }
            catch (Exception ex) { Fail(ex); }
        }
        private void Receive(WireMessage message)
        {
            string trafficKind = message.Type;
            if (message.Type == "worldEvent" && message.Payload.TryGetProperty("Kind", out var eventKind) && eventKind.ValueKind == JsonValueKind.String)
                trafficKind = "world/" + eventKind.GetString();
            // The validated transport already knows the frame length. Traffic
            // diagnostics must not allocate a payload-sized UTF-16 string.
            AddTraffic(receivedTraffic, trafficKind, message.ReceivedFrameBytes);
            switch (message.Type)
            {
                case "serverHello":
                    var hello = message.Read<ServerHello>();
                    if (hello.Version != DedicatedProtocol.Version || hello.BuildId != DedicatedProtocol.BuildId) throw new InvalidDataException("Server game version is incompatible.");
                    if (!worker) { accountFlow.Begin(connection, hello, endpoint, secret); secret = ""; break; }
                    var join = DedicatedProtocol.CreateJoin(hello, worker ? "worker" : "player", name, secret);
                    if (!connection.Send("join", join)) throw new IOException("Cannot send login.");
                    connection.SetAuthenticationKey(DedicatedProtocol.DeriveSessionKey(hello, join, secret)); secret = "";
                    break;
                case "joinAccepted":
                    var result = message.Read<JoinAccepted>();
                    if (result == null || result.World == null || result.Role != (worker ? "worker" : "player") ||
                        !Guid.TryParseExact(result.PlayerId, "N", out _) || (!worker && result.AccountId != accountId))
                        throw new InvalidDataException("World join does not match the authenticated account.");
                    world = result.World; playerId = result.PlayerId; accepted = true;
                    if (!Guid.TryParseExact(world.WorldId, "N", out _)) throw new InvalidDataException("Invalid server world identity.");
                    if (!worker)
                    {
                        accountFlow.MarkJoined();
                        PlayerChatOverlay.Configure(playerId, SendChat);
                        string root = Path.Combine(Paths.GameRootPath, "MultiplayerDev", "client-worlds", world.WorldId, Guid.NewGuid().ToString("N"));
                        DedicatedWorldRuntime.ConfigureIsolation(root);
                        SpatialQuestReplication.Configure(false);
                        logPath = Path.Combine(root, "multiplayer-session.log"); menu?.SetIsolated(true);
                        ClientInputSchemeGuard.SetEnabled(true, Log);
                    }
                    GameplayReplication.LocalPlayerId = playerId;
                    PropertyOwnershipReplication.Configure(worker, accountId);
                    DialogueReplication.LocalPlayerId = playerId; BusinessReplication.LocalPlayerId = playerId;
                    TravelReplication.Configure(worker, playerId, SendInteraction, QueueEvent, Status,
                        reason => Fail(new InvalidOperationException(reason)));
                    BoatReplication.Configure(worker, accountId, SendBoatPose, SendInteraction, TravelReplication.RequestBoatTravel, Status);
                    TravelReplication.CaptureBoatArrival = CaptureBoatArrival;
                    SleepReplication.Configure(worker, playerId, SendInteraction, QueueEvent,
                        message => { Status(message); menu?.SetSleepRecoveryNotice(message); },
                        reason => Fail(new InvalidOperationException(reason)));
                    ApartmentReplication.Configure(worker, playerId, SendInteraction, QueueEvent,
                        Status, reason => Fail(new InvalidOperationException(reason)),
                        () => shuttingDown || terminalSession || disposed ||
                            (worker && (awaitingCommit || transfer.IsSending || transactionDirty || pendingProperties.Count > 0 || apartmentCheckpointRevision > 0)) ||
                            (!worker && (economyPreparation.IsPending ||
                                (economyPreparation.IsFenced && orderedTransitionEvent == null))));
                    if (worker)
                    {
                        PlayerMapRuntime.Configure(QueueEvent, RejectPlayerMap);
                        WorkerNpcSimulation.Configure(PlayerMapRuntime.IsSceneOccupied);
                        BoatAuthority.Configure(QueueEvent, RejectPlayerMap);
                        TravelReplication.BeginPlayerTransition = ApartmentReplication.BeginPlayerTransition = PlayerMapRuntime.BeginTransition;
                        TravelReplication.CancelPlayerTransition = ApartmentReplication.CancelPlayerTransition = PlayerMapRuntime.CancelTransition;
                        TravelReplication.CommitPlayerContext = ApartmentReplication.CommitPlayerContext = PlayerMapRuntime.CommitContext;
                        TravelReplication.FailPlayerTransition = ApartmentReplication.FailPlayerTransition = RejectPlayerMap;
                    }
                    Log("Joined server world " + world.WorldId + "."); Status("Receiving the shared world..."); break;
                case ChatProtocol.MessageKind:
                    if (worker || !accepted) throw new InvalidDataException("Chat arrived outside a player session.");
                    PlayerChatOverlay.Receive(message.Read<ChatMessage>()); break;
                case ChatProtocol.RejectedKind:
                    if (worker || !accepted) throw new InvalidDataException("Chat rejection arrived outside a player session.");
                    PlayerChatOverlay.Reject(message.Read<ChatRejection>()); break;
                case "accountPrepare":
                    if (!worker || !accepted) throw new InvalidDataException("Account preparation requires the simulation worker.");
                    var preparation = message.Read<AccountPreparation>();
                    if (preparation == null || !Guid.TryParseExact(preparation.AccountId, "N", out _) ||
                        !Guid.TryParseExact(preparation.PlayerId, "N", out _) || accountPreparations.Count >= AccountProtocol.MaxAccounts)
                        throw new InvalidDataException("Invalid account preparation.");
                    accountPreparations.Enqueue(preparation); nextCheckpoint = 0; break;
                case "accountDataBegin":
                    if (!accepted || (!worker && loaded)) throw new InvalidDataException("Account transfer arrived outside its world load barrier.");
                    accountTransfer.BeginReceive(message.Read<AccountDataBegin>(), world.WorldId, worker ? "ledger" : "account"); break;
                case "accountDataChunk": accountTransfer.ReceiveChunk(message.Read<SnapshotChunk>()); break;
                case "accountDataEnd":
                    accountTransfer.EndReceive(message.Read<SnapshotEnd>());
                    if (worker)
                    {
                        accountLedger = JsonSerializer.Deserialize<AccountWorldState>(accountTransfer.CompletedBytes, DedicatedProtocol.Json);
                        AccountProtocol.Validate(accountLedger, world.WorldId, accountTransfer.Completed.Revision);
                    }
                    else
                    {
                        ownAccount = JsonSerializer.Deserialize<AccountState>(accountTransfer.CompletedBytes, DedicatedProtocol.Json);
                        AccountProtocol.Validate(ownAccount);
                        if (ownAccount.AccountId != accountId || !ownAccount.Initialized)
                            throw new InvalidDataException("Server supplied a different or uninitialized account.");
                    }
                    break;
                case "worldFlowCredit":
                    var credit = message.Read<WorldFlowCredit>();
                    if (!worker || credit == null || credit.BulkBytes < 0 || credit.ControlBytes < 0 ||
                        credit.BulkBytes > WorldEventFlow.BulkWindowBytes - bulkCredit || credit.ControlBytes > WorldEventFlow.ControlWindowBytes - controlCredit)
                        throw new InvalidDataException("Invalid world delivery credit.");
                    bulkCredit += credit.BulkBytes; controlCredit += credit.ControlBytes; break;
                case "snapshotBegin":
                    if (!worker && loaded) throw new InvalidDataException("A replacement world snapshot arrived after the active-world barrier.");
                    if (!worker) { menu?.ClearSleepRecovery(); SleepReplication.ClearRecovery(); }
                    var snapshotHeader = message.Read<SnapshotBegin>();
                    if (accountTransfer.Completed == null || accountTransfer.Completed.Revision != snapshotHeader.Revision ||
                        (worker && !DedicatedProtocol.EqualHex(accountTransfer.Completed.Sha256, snapshotHeader.AccountLedgerHash)))
                        throw new InvalidDataException("World snapshot and account data do not share a committed revision.");
                    transfer.BeginReceive(snapshotHeader, world.WorldId); Status("Receiving server world..."); break;
                case "snapshotChunk": transfer.ReceiveChunk(message.Read<SnapshotChunk>()); break;
                case "snapshotEnd": transfer.EndReceive(message.Read<SnapshotEnd>()); break;
                case "worldLoadComplete":
                    if (!worker && loaded) throw new InvalidDataException("A replacement world load arrived after the active-world barrier.");
                    var complete = message.Read<WorldLoadComplete>();
                    if (complete.WorldId != world.WorldId || (complete.Revision > 0 && (transfer.Completed == null || complete.Revision != transfer.Completed.Revision)))
                        throw new InvalidDataException("Server world transfer does not match its revision.");
                    if (accountTransfer.Completed == null || complete.Revision != accountTransfer.Completed.Revision)
                        throw new InvalidDataException("Account data does not match the world load revision.");
                    clientRevision = complete.Revision; haveWorld = true; break;
                case "snapshotCommitted":
                    var committed = message.Read<WorldLoadComplete>();
                    if (!worker || !awaitingCommit || committed.WorldId != world.WorldId || committed.Revision != world.Revision + 1)
                        throw new InvalidDataException("Unexpected checkpoint acknowledgement.");
                    world.Revision = committed.Revision; awaitingCommit = false; committedThisSession = true; nextCheckpoint = checkpointRequest.Length > 0 ? 0 : Now + 30;
                    CommitAccountCheckpoint();
                    if (apartmentCheckpointRevision > 0)
                    {
                        if (world.Revision >= apartmentCheckpointRevision) apartmentCheckpointRevision = 0;
                        else nextCheckpoint = 0;
                    }
                    Log("Committed server checkpoint " + world.Revision);
                    if (shuttingDown && world.Revision >= finalRevision)
                    {
                        Status("Final checkpoint saved; worker stopping.");
                        connection.Send("leave", new {}); connection.Tick(); connection.Dispose(); connection = null;
                        // The service has durably acknowledged this checkpoint. The
                        // headless native teardown has a known null job singleton;
                        // leave the isolated worker directly after the durable ACK.
                        Environment.Exit(0); return;
                    }
                    if (shuttingDown) nextCheckpoint = 0;
                    if (!TravelReplication.IsBusy && !ApartmentReplication.IsBusy) SendWorkerReady(); break;
                case "checkpointRequested":
                    if (!worker) throw new InvalidDataException("A player cannot commit a server checkpoint.");
                    checkpointRequest = message.Payload.GetProperty("RequestId").GetString(); nextCheckpoint = 0; break;
                case "clientReadyAccepted": ready = true; Status("Connected to shared world. Player " + name); break;
                case "playerSceneRejected":
                    if (worker) throw new InvalidDataException("Player map rejection reached the worker.");
                    Status(message.Read<ProtocolError>().Message); break;
                case "sleepRecoveryRejected":
                    var rejection = message.Read<ProtocolError>();
                    if (worker || rejection == null || rejection.Code != "sleep_choice_rejected" || !DedicatedProtocol.Text(rejection.Message, 256))
                        throw new InvalidDataException("Invalid pending shared day response.");
                    Status(rejection.Message); menu?.SetSleepRecoveryNotice(rejection.Message); break;
                case "dialogueRecoveryRejected":
                    var dialogueRejection = message.Read<DialogueRecoveryRejection>();
                    DialogueRecoveryProtocol.ValidateRejection(dialogueRejection);
                    if (worker || !accepted || loaded || dialogueRejection.OwnerAccountId != accountId ||
                        dialogueRejection.TargetPlayerId != playerId)
                        throw new InvalidDataException("Invalid pending dialogue response.");
                    DialogueReplication.ApplyRecoveryRejection(dialogueRejection);
                    menu?.ApplyDialogueRecoveryRejection(dialogueRejection);
                    Status(dialogueRejection.Message); break;
                case "simulationState":
                    var state = message.Read<SimulationState>();
                    if (!state.Ready && loaded && !worker) throw new IOException(state.Message);
                    if (!ready) Status(state.Message); break;
                case "playerPresence":
                    if (!worker) break;
                    var presence = message.Read<PlayerPresence>();
                    if (presence == null || !Guid.TryParseExact(presence.PlayerId, "N", out _) || !Guid.TryParseExact(presence.AccountId, "N", out _))
                        throw new InvalidDataException("Player presence has no persistent account.");
                    TravelReplication.SetPlayerPresent(presence.PlayerId, presence.Joined);
                    SleepReplication.SetPlayerPresent(presence.PlayerId, presence.Joined);
                    ApartmentReplication.SetPlayerPresent(presence.PlayerId, presence.Joined);
                    SpatialQuestReplication.SetPresent(presence.PlayerId, presence.Joined);
                    if (presence.Joined)
                    { playerAccounts[presence.PlayerId] = presence.AccountId; present.Add(presence.PlayerId); PlayerMapRuntime.Register(presence, Now); nextNpc = nextWeather = nextGameplay = nextEconomy = nextCrowd = nextBusiness = 0;
                        BoatAuthority.SetPlayerPresent(presence);
                        foreach (var update in PropertyOwnershipReplication.CaptureEvents()) QueueEvent(update);
                        foreach (var pose in players.Values) connection.Send("playerPose", pose); }
                    else
                    {
                        BoatAuthority.SetPlayerPresent(presence);
                        PlayerMapRuntime.Remove(presence.PlayerId);
                        present.Remove(presence.PlayerId); playerAccounts.Remove(presence.PlayerId); players.Remove(presence.PlayerId); GameplayReplication.RemovePlayer(presence.PlayerId);
                        PropertyOwnershipReplication.RemovePlayer(presence.PlayerId);
                        DialogueReplication.RemovePlayer(presence.PlayerId); BusinessReplication.RemovePlayer(presence.PlayerId); Emit("playerLeft", presence);
                    }
                    break;
                case "sleepParticipantPresence":
                    if (!worker) throw new InvalidDataException("Sleep membership is worker-only.");
                    var sleepParticipant = message.Read<PlayerPresence>();
                    if (sleepParticipant == null || !Guid.TryParseExact(sleepParticipant.PlayerId,"N",out _) ||
                        !Guid.TryParseExact(sleepParticipant.AccountId,"N",out _)) throw new InvalidDataException("Invalid authenticated sleep participant.");
                    SleepReplication.SetParticipantConnected(sleepParticipant.PlayerId,sleepParticipant.Joined);
                    if (!sleepParticipant.Joined) sleepWake.Remove(sleepParticipant.PlayerId);
                    break;
                case "sleepRecoveryPresence":
                    if (!worker) throw new InvalidDataException("Pending-player presence is worker-only.");
                    var pendingPlayer = message.Read<PlayerPresence>();
                    if (pendingPlayer == null || !Guid.TryParseExact(pendingPlayer.PlayerId,"N",out _)) throw new InvalidDataException("Invalid pending shared day participant.");
                    SleepReplication.SetRecoveryPlayerPresent(pendingPlayer.PlayerId,pendingPlayer.Joined);
                    break;
                case "dialogueRecoveryPresence":
                    if (!worker) throw new InvalidDataException("Pending dialogue presence is worker-only.");
                    var pendingDialoguePlayer = message.Read<PlayerPresence>();
                    if (pendingDialoguePlayer == null || !Guid.TryParseExact(pendingDialoguePlayer.PlayerId, "N", out _) ||
                        !Guid.TryParseExact(pendingDialoguePlayer.AccountId, "N", out _))
                        throw new InvalidDataException("Invalid pending dialogue account.");
                    if (playerAccounts.TryGetValue(pendingDialoguePlayer.PlayerId, out var existingAccount) && existingAccount != pendingDialoguePlayer.AccountId)
                        throw new InvalidDataException("Pending dialogue account changed.");
                    if (pendingDialoguePlayer.Joined) playerAccounts[pendingDialoguePlayer.PlayerId] = pendingDialoguePlayer.AccountId;
                    else if (!present.Contains(pendingDialoguePlayer.PlayerId)) playerAccounts.Remove(pendingDialoguePlayer.PlayerId);
                    DialogueReplication.SetRecoveryPlayerPresent(pendingDialoguePlayer.PlayerId, pendingDialoguePlayer.AccountId, pendingDialoguePlayer.Joined);
                    break;
                case "playerPose":
                    var remote = message.Read<PlayerPose>();
                    if (!DedicatedProtocol.PoseValid(remote)) throw new InvalidDataException("Invalid player pose.");
                    if (worker)
                    {
                        if (!present.Contains(remote.PlayerId) || !PlayerMapRuntime.TryAcceptPose(remote)) break;
                        players[remote.PlayerId] = remote; GameplayReplication.UpdatePlayerPose(remote);
                        BoatAuthority.UpdatePlayerPose(remote, Now);
                        PropertyOwnershipReplication.UpdatePlayerPose(remote);
                        // Pending story travel must still receive fresh positions.
                        TravelReplication.UpdatePlayerPose(remote, Now);
                        SleepReplication.UpdatePlayerPose(remote, Now);
                        ApartmentReplication.UpdatePlayerPose(remote, Now);
                        SpatialQuestReplication.UpdatePlayerPose(remote, Now);
                        DialogueReplication.UpdatePlayerPose(remote); BusinessReplication.UpdatePlayerPose(remote); connection.Send("playerPose", remote);
                    }
                    else if (remote.PlayerId != playerId) { players[remote.PlayerId] = remote; avatars?.Apply(remote); }
                    break;
                case BoatProtocol.PoseKind:
                    if (!worker || !ready) break;
                    var boatPose = message.Read<BoatPose>();
                    BoatProtocol.Validate(boatPose);
                    if (!playerAccounts.TryGetValue(boatPose.PlayerId, out var boatAccount) || boatAccount != boatPose.AccountId) break;
                    BoatAuthority.AcceptPose(boatPose, Now);
                    break;
                case "worldEvent": Apply(message.Read<WorldEvent>()); break;
                case MapCatchupConfirmation.MessageKind:
                    if (worker) throw new InvalidDataException("A simulation worker cannot receive client map confirmation.");
                    AcceptMapCatchupConfirmation(message.Read<MapCatchupConfirmation>()); break;
                case "interaction":
                    if (worker && ready)
                    {
                        var request = message.Read<InteractionRequest>();
                        if (request != null && request.Kind == DialogueRecoveryProtocol.ActionKind)
                            HandleAccountInteraction(request);
                        else if (request?.Kind == PlayerMapRestore.AcknowledgementKind) PlayerMapRuntime.HandleRestoreAck(request, Now);
                        else if (request != null && PlayerSceneMembership.IsTransitionAcknowledgement(request.Kind))
                        {
                            if (TravelReplication.Handles(request)) TravelReplication.Handle(request, Now);
                            else if (ApartmentReplication.Handles(request)) ApartmentReplication.Handle(request, Now);
                        }
                        else if (request?.Kind == SleepWakeProtocol.ReadyKind) HandleWakeAcknowledgement(request);
                        else if (SleepReplication.Handles(request) && request.Kind != SleepReplication.RequestKind)
                            SleepReplication.Handle(request, Now);
                        else QueueMapInteraction(request);
                    }
                    break;
                case "ping": connection.Send("pong", new {}); break;
                case "pong": break;
                case "error": throw new IOException(message.Read<ProtocolError>().Message);
                default:
                    if (worker || !accountFlow.Receive(message)) Log("Ignored protocol message: " + message.Type);
                    break;
            }
        }
        private void StartWorldIfReady()
        {
            if (!haveWorld || worldStarted || !DedicatedWorldRuntime.IsBootReady) return;
            menu?.ClearDialogueRecovery();
            menu?.Hide();
            if (clientRevision == 0)
            {
                if (!worker) throw new InvalidDataException("A player cannot create the server world.");
                UnityEngine.Random.InitState(world.Seed); Status("Creating a fresh dedicated world..."); DedicatedWorldRuntime.BeginNewWorld();
            }
            else
            {
                // Prevent local economy/quest timers from starting during authoritative save import.
                if (!worker)
                {
                    GameplayReplication.SetClientAuthority(true);
                    DialogueReplication.SetClientAuthority(true); BusinessReplication.SetClientAuthority(true);
                }
                string path = Path.Combine(DedicatedWorldRuntime.StorageDirectory, "received-world.sav");
                File.WriteAllBytes(path, transfer.CompletedBytes); Log("Loading server world revision " + clientRevision + "."); Status("Loading the shared world...");
                DedicatedWorldRuntime.LoadSnapshot(path); transfer.Reset();
            }
            worldStarted = loading = true;
        }
        private void TickWorker()
        {
            // Native graphics settings are reapplied during world loads and can
            // undo the startup cap. Keep the headless simulation at its intended
            // rate instead of competing with the graphical client at hundreds
            // of updates per second. This path never runs on a player's client.
            if (Application.targetFrameRate != 30) Application.targetFrameRate = 30;
            if (QualitySettings.vSyncCount != 0) QualitySettings.vSyncCount = 0;
            bool probe = !loaded && haveWorld && Environment.GetEnvironmentVariable("NIVALIS_TRACE_STARTUP") == "1";
            if (probe) Log("PROBE before StartWorldIfReady");
            if (!loaded && stopFile.Length > 0 && File.Exists(stopFile))
            { Status("Stopping worker before gameplay started."); connection.Send("leave", new {}); connection.Tick(); connection.Dispose(); connection = null; Environment.Exit(0); return; }
            StartWorldIfReady();
            if (probe) Log("PROBE after StartWorldIfReady; loading=" + loading);
            MultiplayerSandboxRuntime.Tick();
            bool worldReady = loading && DedicatedWorldRuntime.IsWorldReady;
            if (probe) Log("PROBE after IsWorldReady=" + worldReady);
            if (!loaded && worldReady)
            {
                GameplayReplication.SetClientAuthority(false);
                if (accountLedger == null) throw new InvalidDataException("The native world has no account ledger.");
                PropertyOwnershipReplication.InitializeWorkerLedger(accountLedger);
                PlayerAccountRuntime.InitializeWorker(accountLedger);
                PlayerMapRuntime.InitializeWorker();
                if (MultiplayerSandboxRuntime.Active) accountLedger.DialogueOwnerAccountId = "";
                DialogueReplication.RestoreConversationAccount(accountLedger.DialogueOwnerAccountId);
                PlayerAppearance.ResetCatalog();
                if (!connection.Send("appearanceCatalog", new AccountAppearanceCatalog { Options = PlayerAppearance.GetCatalog() }))
                    throw new IOException("Cannot publish the server appearance catalog.");
                loaded = true; loading = false; nextCheckpoint = 0;
                Status("Dedicated world running in " + DedicatedWorldRuntime.Scene);
            }
            if (!loaded) return;
            PlayerMapRuntime.Tick(Now);
            if (!SleepReplication.IsBusy && Now < nextCheckpoint && !events.Contains(BoatProtocol.EventKind) && !awaitingCommit && !shuttingDown)
                BoatAuthority.Tick(Now);
            bool wasFocusing = WorkerSceneResidency.IsBusy;
            WorkerSceneResidency.Tick(Now);
            if (connection == null || terminalSession) return;
            if (wasFocusing && !WorkerSceneResidency.IsBusy)
            {
                TravelReplication.AcceptTravelContext(); ApartmentReplication.AcceptTravelContext();
                nextNpc = nextWeather = nextGameplay = nextEconomy = nextCrowd = nextBusiness = 0;
            }
            if (WorkerSceneResidency.IsBusy)
            {
                TravelReplication.Tick(Now); ApartmentReplication.Tick(Now);
                CaptureResidencyNpcs();
                return;
            }
            PlayerAccountRuntime.Tick();
            while (accountPreparations.Count > 0)
            {
                var preparedAccount = PlayerAccountRuntime.EnsureAccount(accountPreparations.Dequeue());
                BoatAuthority.EnsureAccountBoat(preparedAccount.AccountId);
                transactionDirty = true; nextCheckpoint = 0;
            }
            if (PlayerAccountRuntime.FailClosed) throw new InvalidOperationException("Account inventory authority failed; no partial checkpoint was saved.");
            if (PlayerAccountRuntime.IsDirty && !sawAccountDirty) nextCheckpoint = 0;
            sawAccountDirty = PlayerAccountRuntime.IsDirty;
            NativeWorldDiagnostics.Tick(Log);
            if (SleepReplication.IsBusy && TravelReplication.IsBusy)
                throw new InvalidOperationException("Native sleep requested travel outside the supported shared bed flow; no partial checkpoint was saved.");
            if (ApartmentReplication.IsBusy && TravelReplication.IsBusy)
                throw new InvalidOperationException("Apartment and district transitions overlapped; no partial checkpoint was saved.");
            if (!SleepReplication.IsBusy) TravelReplication.Tick(Now);
            if (connection == null || terminalSession) return;
            bool busy = TravelReplication.IsBusy;
            AcceptApartmentTravelContext();
            if (travelWasBusy && !busy)
            {
                nextCheckpoint = nextReady = nextNpc = nextWeather = nextGameplay = nextEconomy = nextCrowd = nextBusiness = 0;
            }
            travelWasBusy = busy;
            if (busy) { SpatialQuestReplication.Tick(Now, true); return; } // TCP/event/checkpoint-upload pumping remains in Tick.
            // Detect unapproved context before a new curfew publication, native
            // persistence or capture. Preparation may cancel safely for curfew.
            ApartmentReplication.Tick(Now);
            if (connection == null || terminalSession) return;
            if (ApartmentReplication.ConsumeCheckpointRequest())
            {
                apartmentCheckpointRevision = Math.Max(apartmentCheckpointRevision, world.Revision + (awaitingCommit ? 2 : 1));
                nextCheckpoint = 0;
            }
            bool apartmentBusy = ApartmentReplication.IsBusy;
            if (apartmentWasBusy && !apartmentBusy)
                nextCheckpoint = nextReady = nextNpc = nextWeather = nextGameplay = nextEconomy = nextCrowd = nextBusiness = 0;
            apartmentWasBusy = apartmentBusy;
            if (apartmentBusy) { SpatialQuestReplication.Tick(Now, true); return; }
            var sleepFocus = SleepReplication.GetPendingSleepFocus(Now);
            if (sleepFocus != null && !SleepReplication.IsBusy && !TravelReplication.HasPendingOperations &&
                !ApartmentReplication.HasPendingOperations && !PlayerMapRuntime.HasPendingRestores)
            {
                try { if (!WorkerSceneResidency.Ensure(sleepFocus, Now)) return; }
                catch (InvalidOperationException ex)
                { if (connection != null && !terminalSession) SleepReplication.CancelPendingSleepFocus(ex.Message); return; }
                TravelReplication.AcceptTravelContext(); ApartmentReplication.AcceptTravelContext();
            }
            SleepReplication.Tick(Now);
            if (connection == null || terminalSession) return;
            bool sleeping = SleepReplication.IsBusy;
            bool waking = SleepReplication.IsAwaitingWakeRelease;
            if (waking && sleepWake.Epoch != SleepReplication.CurrentState.Epoch)
            {
                sleepWake.Begin(SleepReplication.CurrentState.Epoch, SleepReplication.WakeParticipants);
                wakeCheckpoint = world.Revision + (awaitingCommit ? 2 : 1);
                wakeCaptureFamily = 0; wakeDeadline = Now + 180; nextCheckpoint = 0;
                Log("SLEEP_WAKE_BEGIN epoch=" + sleepWake.Epoch + " checkpoint=" + wakeCheckpoint + " players=" + sleepWake.PendingCount);
            }
            if (sleepWasBusy && !sleeping)
                nextCheckpoint = nextReady = nextNpc = nextWeather = nextGameplay = nextEconomy = nextCrowd = nextBusiness = 0;
            sleepWasBusy = sleeping;
            if (sleeping && !waking)
            {
                // Preserve native daily calculations/coroutines. Required financial
                // choices wait for an authenticated player; no partial sleep checkpoint.
                SpatialQuestReplication.Tick(Now, true);
                if (ready && Now >= nextWeather && !events.Contains("timeWeather"))
                { nextWeather = Now + 1; Emit("timeWeather", TimeWeatherReplication.Capture()); }
                return;
            }
            // Crowd capture also prepares the native worker population once per
            // district. Preserve that lifecycle even when there is no audience.
            if (DedicatedWorldRuntime.IsWorldReady && workerCrowdScene != DedicatedWorldRuntime.Scene && CrowdReplication.Capture() != null)
                workerCrowdScene = DedicatedWorldRuntime.Scene;
            DialogueReplication.Tick(); BusinessReplication.Tick();
            if (connection == null || terminalSession || TravelReplication.IsBusy || ApartmentReplication.IsBusy) return;
            if (!shuttingDown && stopFile.Length > 0 && File.Exists(stopFile))
            { shuttingDown = true; finalRevision = world.Revision + (awaitingCommit ? 2 : 1); nextCheckpoint = 0; Status("Saving before dedicated worker shutdown..."); }
            // Changed native area variables notify the game's real quest listeners.
            // This precedes both persistence and journal capture, including zero-player
            // ticks that clear occupancy after the last participant leaves.
            SpatialQuestReplication.Tick(Now, TravelReplication.IsBusy || ApartmentReplication.IsBusy || shuttingDown);
            if (connection == null || terminalSession || TravelReplication.IsBusy || ApartmentReplication.IsBusy) return;
            // Older updates enter the ordered TCP stream before the checkpoint/load barrier.
            if (!awaitingCommit && !transfer.IsSending && !accountTransfer.IsSending && events.Count == 0 && Now >= nextCheckpoint &&
                !DialogueReplication.IsConversationActive && DedicatedWorldRuntime.IsWorldReady)
            {
                BusinessReplication.PrepareForCheckpoint();
                if (connection == null || terminalSession || TravelReplication.IsBusy || ApartmentReplication.IsBusy) return;
                // Quest/business callbacks above may invoke native property
                // changes inline. Recheck at the final persistence boundary.
                ApartmentReplication.Tick(Now);
                if (connection == null || terminalSession || TravelReplication.IsBusy || ApartmentReplication.IsBusy) return;
                // Requeue/cancel notifications must precede the checkpoint's load barrier.
                if (events.Count != 0) return;
                string path = Path.Combine(DedicatedWorldRuntime.StorageDirectory, "server-checkpoint.sav");
                accountLedger.DialogueOwnerAccountId = DialogueReplication.ConversationAccountId;
                BoatAuthority.FlushPositions();
                PlayerMapRuntime.FlushLocations();
                if (DedicatedWorldRuntime.WriteSnapshot(path))
                {
                    // Both captures execute synchronously on this Unity tick.
                    // The service advances its durable pointer only for this pair.
                    committingLedger = PlayerAccountRuntime.CaptureLedger(world.Revision + 1);
                    AccountProtocol.Validate(committingLedger, world.WorldId, world.Revision + 1);
                    byte[] ledgerBytes = JsonSerializer.SerializeToUtf8Bytes(committingLedger, DedicatedProtocol.Json);
                    string ledgerHash = Convert.ToHexString(SHA256.HashData(ledgerBytes));
                    accountTransfer.StartSend(ledgerBytes, world.WorldId, world.Revision + 1, "ledger");
                    transfer.StartSend(File.ReadAllBytes(path), world.WorldId, world.Revision + 1, checkpointRequest, ledgerHash);
                    checkpointRequest = ""; awaitingCommit = true; transactionDirty = false;
                    Log("Uploading paired account/native world checkpoint " + (world.Revision + 1));
                }
                else nextCheckpoint = Now + 2;
            }
            // A commit can arrive before native scene finalization. Retry initial
            // readiness too, rather than leaving ready=false forever after that edge.
            if ((ready || committedThisSession) && Now >= nextReady && !awaitingCommit) SendWorkerReady();
            if (!ready || shuttingDown) return;
            if (waking) { TickWakeSynchronization(); return; }
            PumpMapInteractions();
            if (WorkerSceneResidency.IsBusy || connection == null || terminalSession) return;
            GameplayReplication.Tick();
            if (connection == null || terminalSession || TravelReplication.IsBusy || ApartmentReplication.IsBusy) return;
            // Maintain cheap simulation liveness even while a checkpoint uploads.
            // The service does not retain these full live captures for later joins.
            if (Now >= nextWeather && !events.Contains("timeWeather")) { nextWeather = Now + 1; Emit("timeWeather", TimeWeatherReplication.Capture()); }
            if (present.Count == 0) return;
            // Drain an existing capture before a due checkpoint. Continuously
            // refreshing other families here could otherwise starve late joins.
            if (!awaitingCommit && !transfer.IsSending && !DialogueReplication.IsConversationActive && Now >= nextCheckpoint) return;
            CaptureNextFamily();
        }
        private void CaptureResidencyNpcs()
        {
            // Scene residency changes suspend interaction/checkpoint captures,
            // but native offstage NPC simulation keeps advancing. Capture only
            // its persistent ghost records, never unloading scene objects.
            if (!worker || !loaded || connection == null || terminalSession || present.Count == 0 ||
                !WorkerSceneResidency.IsBusy || events.PayloadBytes >= 24 * 1024 * 1024 ||
                Now < nextNpc || events.Contains("npcs")) return;
            nextNpc = Now + .5;
            var frame = NpcWorldReplication.CaptureDuringWorkerResidency();
            if (frame == null) return;
            QueueNpcFrame(frame);
            Log("NPC_RESIDENCY_CAPTURE sequence=" + frame.Sequence + " records=" + frame.Npcs.Length);
        }
        private void CaptureNextFamily()
        {
            // One family per Unity tick bounds capture work. A queued family must
            // drain before another full refresh; partial logical batches are never
            // replaced. Business also produces economy, so it shares that gate.
            if (events.PayloadBytes >= 24 * 1024 * 1024) return;
            for (int attempt = 0; attempt < 7; attempt++)
            {
                int family = captureTurn; captureTurn = (captureTurn + 1) % 7;
                switch (family)
                {
                    case 0:
                        if (Now < nextNpc || events.Contains("npcs")) continue;
                        nextNpc = Now + .5; var frame = NpcWorldReplication.Capture();
                        if (frame != null) QueueNpcFrame(frame); return;
                    case 1:
                        if (Now < nextCrowd || events.Contains("crowds")) continue;
                        nextCrowd = Now + .5; foreach (var update in CrowdReplication.CaptureEvents()) QueueEvent(update); return;
                    case 2:
                        if (events.Contains("quests")) continue;
                        foreach (var update in QuestReplication.CaptureEvents()) QueueEvent(update); return;
                    case 3:
                        if (events.Contains("dialogue-people") || events.Contains("dialogue-state")) continue;
                        foreach (var update in DialogueReplication.CaptureEvents()) QueueEvent(update); return;
                    case 4:
                        if (Now < nextGameplay || events.Contains("gameplay-state")) continue;
                        nextGameplay = Now + 2; foreach (var update in GameplayReplication.CaptureEvents(GameplayReplication.Capture())) QueueEvent(update); return;
                    case 5:
                        if (Now < nextEconomy || events.Contains("shared-economy-chunk")) continue;
                        nextEconomy = Now + 5; foreach (var update in SharedEconomyReplication.CaptureEvents(SharedEconomyReplication.Capture())) QueueEvent(update); return;
                    default:
                        if (Now < nextBusiness || events.Contains("business-state-chunk") || events.Contains("business-craft-chunk") || events.Contains("shared-economy-chunk")) continue;
                        nextBusiness = Now + 2; foreach (var update in BusinessReplication.CaptureEvents()) QueueEvent(update); return;
                }
            }
        }
        private void TickWakeSynchronization()
        {
            if (!SleepReplication.IsAwaitingWakeRelease) return;
            if (Now > wakeDeadline) throw new IOException("Morning synchronization timed out; reconnect to the saved morning world.");
            if (sleepWake.Complete)
            {
                Log("SLEEP_WAKE_RELEASE epoch=" + sleepWake.Epoch + " checkpoint=" + sleepWake.Marker.Revision);
                SleepReplication.ReleaseCompletedSleep(); sleepWake.Reset();
                nextCheckpoint = nextReady = nextNpc = nextWeather = nextGameplay = nextEconomy = nextCrowd = nextBusiness = 0;
                return;
            }
            if (sleepWake.Marker != null || world.Revision < wakeCheckpoint || awaitingCommit || transfer.IsSending ||
                accountTransfer.IsSending || events.Count != 0) return;
            // One full family per frame. Each batch drains before the next; the
            // final ordering marker cannot pass any preceding bulk chunk.
            switch (wakeCaptureFamily)
            {
                case 0:
                    var npcs = NpcWorldReplication.Capture() ?? throw new InvalidOperationException("Morning NPC registry unavailable.");
                    QueueNpcFrame(npcs); break;
                case 1:
                    foreach (var update in CrowdReplication.CaptureEvents()) QueueEvent(update); break;
                case 2:
                    var quests = QuestReplication.CaptureEvents(); if (quests.Length == 0) return;
                    foreach (var update in quests) QueueEvent(update); break;
                case 3:
                    var dialogue = DialogueReplication.CaptureEvents(); if (dialogue.Length == 0) return;
                    foreach (var update in dialogue) QueueEvent(update); break;
                case 4:
                    foreach (var update in GameplayReplication.CaptureEvents(GameplayReplication.Capture())) QueueEvent(update); break;
                case 5:
                    foreach (var update in SharedEconomyReplication.CaptureEvents(SharedEconomyReplication.Capture())) QueueEvent(update); break;
                case 6:
                    foreach (var update in BusinessReplication.CaptureEvents()) QueueEvent(update); break;
                case 7:
                    foreach (var update in PropertyOwnershipReplication.CaptureEvents()) QueueEvent(update); break;
                default:
                    var morning = TimeWeatherReplication.Capture();
                    if (!morning.HasTime || CurfewManager.IsCurfewInEffect) throw new InvalidOperationException("Native morning has not finished.");
                    Emit("timeWeather",morning);
                    var marker = new SleepWakeMarker { Epoch=sleepWake.Epoch, Token=Guid.NewGuid().ToString("N"), Revision=world.Revision, GameSeconds=morning.TotalGameSeconds };
                    sleepWake.Arm(marker); Emit(SleepWakeProtocol.EventKind,marker);
                    Log("SLEEP_WAKE_MARKER epoch=" + marker.Epoch + " checkpoint=" + marker.Revision + " gameSeconds=" + marker.GameSeconds);
                    break;
            }
            wakeCaptureFamily++;
        }
        private void HandleWakeAcknowledgement(InteractionRequest request)
        {
            if (!SleepReplication.IsAwaitingWakeRelease || !present.Contains(request.PlayerId) ||
                !playerAccounts.TryGetValue(request.PlayerId,out var bound) || bound != request.AccountId || request.TargetId != sleepWake.Epoch)
            { RejectTravelBusy(request); return; }
            try
            {
                var marker = request.Data.Deserialize<SleepWakeMarker>(DedicatedProtocol.Json);
                SleepWakeProtocol.Validate(marker);
                if (!sleepWake.Acknowledge(request.PlayerId,marker)) { RejectTravelBusy(request); return; }
                Log("SLEEP_WAKE_ACK epoch=" + marker.Epoch + " player=" + request.PlayerId + " remaining=" + sleepWake.PendingCount);
            }
            catch (Exception ex) when (ex is JsonException || ex is InvalidDataException || ex is ArgumentException)
            { RejectTravelBusy(request); }
        }
        private void SendWorkerReady()
        {
            if (WorkerSceneResidency.IsBusy || TravelReplication.IsBusy || ApartmentReplication.IsBusy || apartmentCheckpointRevision > 0 || !DedicatedWorldRuntime.IsWorldReady) return;
            if (!travelEnabled) { TravelReplication.SetEnabled(true); travelEnabled = true; }
            // Do not start a zero-audience day-choice epoch before the first durable
            // snapshot has made the worker joinable.
            if (!sleepEnabled) { SleepReplication.SetEnabled(true); sleepEnabled = true; }
            if (!apartmentEnabled) { ApartmentReplication.SetEnabled(true); apartmentEnabled = true; }
            connection.Send("workerReady", new WorkerReady { WorldId = world.WorldId, Revision = world.Revision, Scene = DedicatedWorldRuntime.Scene });
            ready = true; nextReady = Now + 5;
        }
        private void RejectPlayerMap(string actor, string reason)
        {
            reason = string.IsNullOrWhiteSpace(reason) ? "Your map could not finish loading. Reconnect to try again." : reason;
            var plain = new StringBuilder(Math.Min(reason.Length, 512));
            foreach (char letter in reason) { if (plain.Length == 512) break; plain.Append(char.IsControl(letter) ? ' ' : letter); }
            QueueEvent(new WorldEvent { Kind = "player-map-error", TargetPlayerId = actor,
                Data = JsonSerializer.SerializeToElement(new ProtocolError { Code = "player_map_failed", Message = plain.ToString() }, DedicatedProtocol.Json) });
        }
        private bool CurrentMapRequest(InteractionRequest request)
        {
            var context = PlayerMapRuntime.GetContext(request.PlayerId);
            return present.Contains(request.PlayerId) && context != null && !context.TravelPending &&
                context.Generation == request.ContextGeneration;
        }
        private void QueueMapInteraction(InteractionRequest request)
        {
            RequireAccountActor(request);
            if (!CurrentMapRequest(request) || shuttingDown || SleepReplication.IsBusy)
            { RejectTravelBusy(request); return; }
            if (mapInteractions.Count >= 128) { RejectCheckpointBusy(request); return; }
            int ownPending = focusingInteraction?.Request.PlayerId == request.PlayerId ? 1 : 0;
            foreach (var pending in mapInteractions) if (pending.Request.PlayerId == request.PlayerId) ownPending++;
            if (ownPending >= 4) { RejectCheckpointBusy(request); return; }
            mapInteractions.Enqueue(new PendingMapInteraction { Request = request, Expires = Now + 120 });
        }
        private void PumpMapInteractions()
        {
            if (WorkerSceneResidency.IsBusy || SleepReplication.IsBusy || shuttingDown || awaitingCommit ||
                transfer.IsSending || accountTransfer.IsSending || transactionDirty || pendingProperties.Count > 0) return;
            if (focusingInteraction == null && mapInteractions.Count > 0) focusingInteraction = mapInteractions.Dequeue();
            if (focusingInteraction == null) return;
            var pending = focusingInteraction;
            var request = pending.Request;
            if (Now > pending.Expires || !CurrentMapRequest(request))
            { Log("MAP_CONTEXT rejected player=" + request.PlayerId + " reason=expired-or-changed-membership"); focusingInteraction = null; RejectTravelBusy(request); return; }
            if (SleepReplication.Handles(request) &&
                (TravelReplication.HasPendingOperations || ApartmentReplication.HasPendingOperations || PlayerMapRuntime.HasPendingRestores))
            { focusingInteraction = null; RejectTravelBusy(request); return; }
            var location = PlayerMapRuntime.GetLocation(request.PlayerId);
            if (location == null || !location.HasPosition)
            { Log("MAP_CONTEXT rejected player=" + request.PlayerId + " reason=awaiting-first-active-pose"); focusingInteraction = null; RejectTravelBusy(request); return; }
            // A live conversation owns the singleton until its choice finishes.
            if (DialogueReplication.IsConversationActive && (location.Scene != DedicatedWorldRuntime.Scene ||
                location.PropertyId != WorkerSceneResidency.CurrentProperty))
            { Log("MAP_CONTEXT rejected player=" + request.PlayerId + " reason=active-conversation"); focusingInteraction = null; RejectTravelBusy(request); return; }
            pending.Location ??= location;
            try { if (!WorkerSceneResidency.Ensure(pending.Location, Now)) return; }
            catch (InvalidOperationException ex)
            {
                focusingInteraction = null;
                if (connection != null && !terminalSession)
                { Log("MAP_CONTEXT rejected player=" + request.PlayerId + " reason=" + ex.Message); RejectTravelBusy(request); }
                return;
            }
            TravelReplication.AcceptTravelContext(); ApartmentReplication.AcceptTravelContext();
            // Recheck the latest pose after asynchronous loading. A player may
            // have walked away while another district was being loaded.
            double dx = location.X - pending.Location.X, dy = location.Y - pending.Location.Y, dz = location.Z - pending.Location.Z;
            if (dx * dx + dy * dy + dz * dz > 1)
            { pending.Location = location; return; }
            focusingInteraction = null;
            if (ApartmentReplication.Handles(request)) ApartmentReplication.Handle(request, Now);
            else if (SleepReplication.Handles(request)) SleepReplication.Handle(request, Now);
            else if (TravelReplication.Handles(request)) TravelReplication.Handle(request, Now);
            else if (PropertyOwnershipReplication.Handles(request)) PropertyOwnershipReplication.HandleInteraction(request);
            else HandleAccountInteraction(request);
        }
        private void QueueNpcFrame(NpcWorldFrame frame)
        {
            capturedNpcCount = frame.Npcs.Length;
            var chunks = new List<NpcWorldFrame>(); var group = new List<NpcWorldState>(); int bytes = 256;
            foreach (var npc in frame.Npcs)
            {
                int size = CanonicalJson.ByteCount(npc) + 1;
                if (size > 45000) throw new InvalidDataException("NPC state exceeds supported payload size.");
                if (bytes + size > 45000) { chunks.Add(new NpcWorldFrame { Sequence = frame.Sequence, CapturedAt = frame.CapturedAt, Npcs = group.ToArray() }); group.Clear(); bytes = 256; }
                group.Add(npc); bytes += size;
            }
            if (group.Count > 0 || chunks.Count == 0) chunks.Add(new NpcWorldFrame { Sequence = frame.Sequence, CapturedAt = frame.CapturedAt, Npcs = group.ToArray() });
            if (chunks.Count > 4096) throw new InvalidDataException("NPC roster exceeds supported size.");
            for (int i = 0; i < chunks.Count; i++) { chunks[i].ChunkIndex = i; chunks[i].ChunkCount = chunks.Count; Emit("npcs", chunks[i]); }
        }
        private void TickClient()
        {
            StartWorldIfReady();
            MultiplayerSandboxRuntime.Tick();
            if (!loaded && loading && DedicatedWorldRuntime.IsWorldReady)
            {
                NpcWorldReplication.SetClientAuthority(true); TimeWeatherReplication.SetClientAuthority(true); GameplayReplication.SetClientAuthority(true); CrowdReplication.SetClientAuthority(true); QuestReplication.SetClientAuthority(true);
                DialogueReplication.SetClientAuthority(true); BusinessReplication.SetClientAuthority(true);
                if (ownAccount == null) throw new InvalidDataException("Account inventory is missing from the world load.");
                PlayerAccountRuntime.ApplyClientAccount(ownAccount);
                BoatReplication.SetOwnState(ownAccount.Boat);
                loaded = true; loading = false;
                TravelReplication.SetEnabled(true); travelEnabled = true;
                SleepReplication.SetEnabled(true); sleepEnabled = true;
                ApartmentReplication.SetEnabled(true); apartmentEnabled = true;
                connection.Send("clientReady", new WorldLoadComplete { WorldId = world.WorldId, Revision = clientRevision });
                Status("World loaded; synchronizing simulation...");
            }
            if (!loaded) return;
            ClientInputDiagnostics.Tick(Now, Log);
            if (!economyPreparation.IsFenced)
            {
                BoatReplication.Tick();
                WorkerSceneResidency.Tick(Now);
            }
            if (connection == null || terminalSession) return;
            // Completion runs outside ordered world dispatch. Wait for both a
            // pending preparation and its budget-deferred FIFO before attaching
            // the player or sending the asynchronous restoration acknowledgement.
            if (clientMapRestore != null && WorkerSceneResidency.ClientRestoreComplete && !clientMapRestoreAcknowledged && !economyPreparation.IsFenced &&
                BoatReplication.TryRestorePlayerAttachment())
            {
                TravelReplication.AcceptTravelContext(); ApartmentReplication.AcceptTravelContext();
                SendInteraction(new InteractionRequest { Kind = PlayerMapRestore.AcknowledgementKind,
                    RequestId = Guid.NewGuid().ToString("N"), TargetId = clientMapRestore.Epoch,
                    Data = JsonSerializer.SerializeToElement(new PlayerMapRestoreAcknowledgement { Success = true,
                        Scene = DedicatedWorldRuntime.Scene, PropertyId = WorkerSceneResidency.CurrentProperty }, DedicatedProtocol.Json) });
                clientMapRestoreAcknowledged = true;
                Log("PLAYER_MAP_RESTORED scene=" + DedicatedWorldRuntime.Scene + " property=" + WorkerSceneResidency.CurrentProperty);
            }
            if (WorkerSceneResidency.IsRestoringClient || !clientScene.IsAssigned || clientMapRestore != null)
            { avatars?.Tick("", false); return; }
            if (economyPreparation.IsFenced)
            {
                // Native replication ticks can mutate inventories or change the
                // scene. Keep only rendering/input diagnostics and fresh poses
                // moving until the older economy commit has finished.
                avatars?.Tick(DedicatedWorldRuntime.Scene, DedicatedWorldRuntime.IsWorldReady, WorkerSceneResidency.CurrentProperty);
                NpcWorldReplication.TickRendering();
                SendClientPose(); return;
            }
            TravelReplication.Tick(Now);
            if (connection == null || terminalSession) return;
            if (TravelReplication.IsBusy) { travelWasBusy = true; avatars?.Tick("", false); return; }
            if (travelWasBusy) { travelWasBusy = false; nextPose = 0; }
            AcceptApartmentTravelContext();
            ApartmentReplication.Tick(Now);
            if (connection == null || terminalSession) return;
            if (ApartmentReplication.IsBusy)
            {
                apartmentWasBusy = true;
                avatars?.Tick(DedicatedWorldRuntime.Scene, DedicatedWorldRuntime.IsWorldReady, WorkerSceneResidency.CurrentProperty);
                SendClientPose(); return;
            }
            if (apartmentWasBusy) { apartmentWasBusy = false; nextPose = 0; }
            // Keep poses fresh while sleep choices are open so their authenticated
            // owner can continue, disconnect, or transfer ownership normally.
            SleepReplication.Tick(Now);
            if (connection == null || terminalSession) return;
            FlushDeferredTravelEvents();
            if (connection == null || terminalSession) return;
            if (economyPreparation.IsFenced) { NpcWorldReplication.TickRendering(); SendClientPose(); return; }
            if (npcMapPresentationPending && deferredTravelEvents.Count == 0)
            {
                // Historical map catch-up has completed. Place the first visible
                // live pose once; ordinary live/economy updates never reset it.
                NpcWorldReplication.ResetPlaybackAfterMapLoad(); npcMapPresentationPending = false;
            }
            NpcWorldReplication.Tick(); TimeWeatherReplication.Tick(); GameplayReplication.Tick(); CrowdReplication.Tick(); QuestReplication.Tick();
            DialogueReplication.Tick(); BusinessReplication.Tick();
            avatars?.Tick(DedicatedWorldRuntime.Scene, DedicatedWorldRuntime.IsWorldReady, WorkerSceneResidency.CurrentProperty);
            SendClientPose();
            if (pendingWakeMarker != null && acknowledgedWakeToken != pendingWakeMarker.Token &&
                !economyPreparation.IsFenced && deferredTravelEvents.Count == 0 && DedicatedWorldRuntime.IsWorldReady &&
                TimeOfDayManager.TotalGameSeconds >= pendingWakeMarker.GameSeconds && !CurfewManager.IsCurfewInEffect)
            {
                SendInteraction(new InteractionRequest { Kind=SleepWakeProtocol.ReadyKind, PlayerId=playerId,
                    RequestId=Guid.NewGuid().ToString("N"), TargetId=pendingWakeMarker.Epoch,
                    Data=JsonSerializer.SerializeToElement(pendingWakeMarker,DedicatedProtocol.Json) });
                acknowledgedWakeToken=pendingWakeMarker.Token;
                Log("SLEEP_WAKE_APPLIED epoch=" + pendingWakeMarker.Epoch + " checkpoint=" + pendingWakeMarker.Revision);
            }
        }
        private void SendClientPose()
        { if (ready && clientScene.IsAssigned && !clientScene.TravelPending && deferredTravelEvents.Count == 0 && !WorkerSceneResidency.IsRestoringClient && connection != null && Now >= nextPose)
            { nextPose = Now + .1; var pose = ReadPose(); if (clientScene.TryAcceptPose(pose)) connection.Send("playerPose", pose); } }
        private void AcceptApartmentTravelContext()
        {
            if (!apartmentTravelContextPending || TravelReplication.IsBusy || !DedicatedWorldRuntime.IsWorldReady) return;
            ApartmentReplication.AcceptTravelContext(); apartmentTravelContextPending = false;
        }
        private PlayerPose ReadPose()
        {
            var pose = new PlayerPose { Name = name, PlayerId = playerId, ContextGeneration = clientScene.Current?.Generation ?? 0,
                PropertyId = WorkerSceneResidency.CurrentProperty };
            if (!DedicatedWorldRuntime.IsWorldReady) return pose;
            var character = PlayerManager.Instance.LocalPlayer.Character;
            if (character.Transform == null || !character.gameObject.activeInHierarchy) return pose;
            var pos = character.Transform.position;
            pose.Scene = DedicatedWorldRuntime.Scene; pose.Active = true; pose.X = pos.x; pose.Y = pos.y; pose.Z = pos.z;
            pose.Yaw = character.Controller != null ? character.Controller.Rotation.eulerAngles.y : character.Transform.eulerAngles.y;
            return pose;
        }
        private void SendBoatPose(BoatPose pose)
        {
            var context = clientScene.Current;
            if (worker || pose == null || connection == null || !ready || terminalSession || !loaded || context == null ||
                context.TravelPending || context.PropertyId.Length != 0 || deferredTravelEvents.Count != 0 || economyPreparation.IsFenced ||
                WorkerSceneResidency.IsRestoringClient || clientMapRestore != null || TravelReplication.IsBusy || SleepReplication.IsBusy ||
                ApartmentReplication.IsBusy || !DedicatedWorldRuntime.IsWorldReady) return;
            pose.PlayerId = playerId; pose.AccountId = accountId; pose.ContextGeneration = context.Generation;
            if (pose.Scene != context.Scene) return;
            BoatProtocol.Validate(pose);
            connection.Send(BoatProtocol.PoseKind, pose);
        }
        private BoatPose CaptureBoatArrival()
        {
            if (worker || clientScene.Current == null) throw new InvalidOperationException("Boat arrival has no authenticated map context.");
            var pose = BoatReplication.CaptureOwnPose() ?? throw new InvalidOperationException("Your boat has not finished arriving.");
            pose.PlayerId = playerId; pose.AccountId = accountId; pose.ContextGeneration = clientScene.Current.Generation;
            BoatProtocol.Validate(pose); return pose;
        }
        private void Emit<T>(string kind, T data)
        {
            QueueEvent(new WorldEvent { Kind = kind, Data = JsonSerializer.SerializeToElement(data, DedicatedProtocol.Json) });
        }
        private void QueueEvent(WorldEvent update)
        {
            if (update == null) return;
            if (!worker || world == null) throw new InvalidOperationException("Only the simulation worker can publish world changes.");
            if (!publishingCommit && IsSuccessfulTransactionResult(update))
            {
                if (pendingTransactionResults.Count >= 256) throw new IOException("Too many account transactions await a checkpoint.");
                pendingTransactionResults.Add((world.Revision + (awaitingCommit ? 2 : 1), update));
                transactionDirty = true; nextCheckpoint = 0; return;
            }
            update.WorldId = world.WorldId; update.Revision = world.Revision;
            var queued = events.Enqueue(update);
            AddTraffic(queuedTraffic, update.Kind, queued.PayloadBytes);
        }
        private static void AddTraffic(Dictionary<string, TrafficTally> traffic, string kind, int bytes)
        {
            if (!DedicatedProtocol.Text(kind, 96)) kind = "other";
            if (!traffic.TryGetValue(kind, out var tally))
            {
                if (traffic.Count >= 64) kind = "other";
                if (!traffic.TryGetValue(kind, out tally)) { tally = new TrafficTally(); traffic[kind] = tally; }
            }
            tally.Count++; tally.Bytes += bytes;
        }
        private static string DescribeTraffic(Dictionary<string, TrafficTally> traffic)
        {
            var entries = new List<KeyValuePair<string, TrafficTally>>(traffic);
            entries.Sort((a, b) => b.Value.Bytes.CompareTo(a.Value.Bytes));
            var values = new List<string>();
            foreach (var entry in entries) values.Add(entry.Key + ":" + entry.Value.Count + "/" + entry.Value.Bytes);
            traffic.Clear(); return string.Join(",", values);
        }
        private void LogTraffic()
        {
            double at = Now, elapsed = at - lastTrafficDiagnostic; lastTrafficDiagnostic = at;
            if (!worker && pendingReceiptDiagnostics.Count > 0)
            {
                var oldest = new List<KeyValuePair<long, (string Kind, double Received)>>(pendingReceiptDiagnostics);
                oldest.Sort((a, b) => a.Value.Received.CompareTo(b.Value.Received));
                var receipts = new List<string>();
                for (int i = 0; i < Math.Min(8, oldest.Count); i++)
                {
                    var entry = oldest[i];
                    receipts.Add(entry.Key + ":" + entry.Value.Kind + ":" + (at - entry.Value.Received).ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
                        "s:" + (appliedDeliveries.Contains(entry.Key) ? "ackQueued" : "pendingApply"));
                }
                Log("WORLD_RECEIPTS pending=" + pendingReceiptDiagnostics.Count + " oldest=[" + string.Join(",", receipts) + "]");
            }
            Log("NETWORK intervalSeconds=" + elapsed.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
                " players=" + present.Count + " tcpSendBytes=" + connection.PendingSendBytes +
                " eventQueue=" + events.Count + " eventPayloadBytes=" + events.PayloadBytes + " pendingKinds=[" + events.Describe() + "]" +
                " bulkCredit=" + bulkCredit + " controlCredit=" + controlCredit + " acknowledgedEvents=" + acknowledgedEvents +
                " deferredEvents=" + deferredTravelEvents.Count + " deferredBytes=" + deferredTravelBytes +
                " economyPreparing=" + economyPreparation.IsPending + " economyDeferred=" + economyPreparation.Count + " economyDeferredBytes=" + economyPreparation.Bytes +
                " snapshotSending=" + transfer.IsSending + " awaitingCommit=" + awaitingCommit +
                " travelBusy=" + TravelReplication.IsBusy + " sleepBusy=" + SleepReplication.IsBusy + " apartmentBusy=" + ApartmentReplication.IsBusy +
                " apartmentCheckpointRevision=" + apartmentCheckpointRevision + " snapshotQueuedWireBytes=" + snapshotWireBytes +
                " producedCount/payloadBytes=[" + DescribeTraffic(queuedTraffic) + "]" +
                " tcpQueuedCount/wireBytes=[" + DescribeTraffic(sentTraffic) + "]" +
                " receivedCount/wireBytes=[" + DescribeTraffic(receivedTraffic) + "]");
            snapshotWireBytes = acknowledgedEvents = 0;
        }
        private void Apply(WorldEvent update)
        {
            // Bounded receipt-only diagnostics: never log payloads or grant credit.
            if (!worker && update != null && update.DeliveryId > 0 && pendingReceiptDiagnostics.Count < 256 && !pendingReceiptDiagnostics.ContainsKey(update.DeliveryId))
                pendingReceiptDiagnostics.Add(update.DeliveryId, (update.Kind, Now));
            if (!worker && accepted && !loaded && update != null && update.Kind == DialogueRecoveryProtocol.EventKind &&
                update.WorldId == world.WorldId && update.DeliveryId > 0)
            {
                long recoveryStarted = Stopwatch.GetTimestamp();
                var recovery = update.Data.Deserialize<DialogueRecoveryState>(DedicatedProtocol.Json);
                DialogueRecoveryProtocol.ValidateWire(recovery);
                if (update.TargetPlayerId != playerId || recovery.TargetPlayerId != playerId || recovery.OwnerAccountId != accountId)
                    throw new InvalidDataException("Dialogue recovery belongs to a different account or connection.");
                DialogueReplication.ApplyRecovery(update);
                menu?.SetDialogueRecovery(recovery, playerId, choice =>
                    menu?.SetDialogueRecoveryPendingRequest(DialogueReplication.SendRecoveryChoice(recovery, choice)));
                AppliedWorldEvent(update, recoveryStarted); return;
            }
            if (!worker && accepted && !loaded && update != null && update.Kind == SleepRecoveryProtocol.EventKind &&
                update.WorldId == world.WorldId && update.DeliveryId > 0)
            {
                long recoveryStarted = Stopwatch.GetTimestamp();
                SleepReplication.ApplyRecovery(update, Now, (state, choose) => menu?.SetSleepRecovery(state, playerId, choose));
                AppliedWorldEvent(update, recoveryStarted); return;
            }
            if (worker || !loaded || update == null || update.WorldId != world.WorldId || update.DeliveryId < 1)
                throw new InvalidDataException("World delivery arrived outside its load barrier.");
            // Fence every later world event before travel, sleep, or any future
            // control handler. Completed work with an undrained FIFO is fenced too.
            if (economyPreparation.IsFenced || !clientMutationBudget.CanStart(Stopwatch.GetTimestamp()))
            { economyPreparation.Enqueue(update, WorldEventFlow.Cost(update)); return; }
            DispatchWorldEvent(update, false);
        }
        private void DispatchWorldEvent(WorldEvent update, bool replay)
        {
            if (!clientMutationBudget.TryStart(Stopwatch.GetTimestamp()))
            { QueueBudgetDeferredEvent(update, replay); return; }
            long started = Stopwatch.GetTimestamp();
            if (update.TargetPlayerId.Length > 0 && update.TargetPlayerId != playerId)
                throw new InvalidDataException("A private world event belongs to another player.");
            if (update.Kind == PlayerSceneContext.EventKind)
            {
                var context = update.Data.Deserialize<PlayerSceneContext>(DedicatedProtocol.Json);
                if (context.PlayerId != playerId) throw new InvalidDataException("The map assignment belongs to another player.");
                if (clientScene.Observe(context)) ObserveMapCatchupContext(context);
                if (!context.TravelPending && clientMapRestoreAcknowledged)
                { clientMapRestore = null; clientMapRestoreAcknowledged = false; nextPose = 0; }
                AppliedWorldEvent(update, started); return;
            }
            if (update.Kind == PlayerMapRestore.EventKind)
            {
                var restore = update.Data.Deserialize<PlayerMapRestore>(DedicatedProtocol.Json);
                PlayerMapRestore.Validate(restore);
                var context = clientScene.Current;
                if (restore.PlayerId != playerId || context == null || !context.TravelPending || context.Epoch != restore.Epoch ||
                    context.Scene != restore.Location.Scene || context.PropertyId != restore.Location.PropertyId)
                    throw new InvalidDataException("The account map restore does not match its authorized context.");
                if (clientMapRestore != null) throw new InvalidDataException("Duplicate account map restoration.");
                clientMapRestore = restore; clientMapRestoreAcknowledged = false; npcMapPresentationPending = true;
                WorkerSceneResidency.BeginClientRestore(restore.Location, restore.Epoch, Now);
                Status("Loading your character's saved location...");
                AppliedWorldEvent(update, started); return;
            }
            if (update.Kind == "player-map-error") throw new IOException(update.Data.Deserialize<ProtocolError>(DedicatedProtocol.Json).Message);
            if (SessionTransitionPolicy.HasApartmentOverlap(update, ApartmentReplication.IsBusy, TravelReplication.IsBusy, SleepReplication.IsBusy))
                throw new InvalidDataException("Incompatible party transitions arrived out of order.");
            var previousControl = orderedTransitionEvent;
            orderedTransitionEvent = update.Kind == TravelReplication.EventKind || update.Kind == SleepReplication.EventKind ||
                update.Kind == ApartmentReplication.EventKind ? update : null;
            try
            {
                bool wasTraveling = TravelReplication.IsBusy;
                if (TravelReplication.Apply(update, Now))
                {
                    if (wasTraveling && !TravelReplication.IsBusy && connection != null && !terminalSession && SessionTransitionPolicy.IsCompletedTravel(update))
                    { apartmentTravelContextPending = true; AcceptApartmentTravelContext(); }
                    AppliedWorldEvent(update, started); return;
                }
                if (update.Kind == SleepReplication.EventKind)
                {
                    var sleepState=update.Data.Deserialize<GroupSleepEvent>(DedicatedProtocol.Json);
                    if (sleepState.Phase=="complete" && sleepState.NightTransition &&
                        (pendingWakeMarker==null || pendingWakeMarker.Epoch!=sleepState.Epoch || acknowledgedWakeToken!=pendingWakeMarker.Token))
                        throw new InvalidDataException("Morning released before this client finished synchronization.");
                }
                if (SleepReplication.Apply(update, Now)) { AppliedWorldEvent(update, started); return; }
                if (ApartmentReplication.Apply(update, Now)) { AppliedWorldEvent(update, started); return; }
            }
            finally { orderedTransitionEvent = previousControl; }
            bool transitionBusy = TravelReplication.IsBusy || ApartmentReplication.IsBusy || WorkerSceneResidency.IsRestoringClient || clientMapRestore != null;
            if (transitionBusy && ApplyTravelNotice(update)) { AppliedWorldEvent(update, started); return; }
            if (transitionBusy && update.Kind != "playerLeft")
            {
                RetainMapEvent(update, started); return;
            }
            bool appliedOlderMapWork = !transitionBusy && FlushDeferredTravelEvents();
            if (connection == null || terminalSession) return;
            if (deferredTravelEvents.Count > 0)
            { RetainMapEvent(update, started); return; }
            if (economyPreparation.IsPending ||
                (appliedOlderMapWork && !clientMutationBudget.CanStart(Stopwatch.GetTimestamp())))
            {
                // Flushing an older travel backlog can start a preparation.
                // It may also exhaust this frame's mutation budget. The current
                // replay stays ahead of its remaining peers and is not ACKed.
                QueueBudgetDeferredEvent(update, replay);
                return;
            }
            ApplyWorldEvent(update);
        }
        private void QueueBudgetDeferredEvent(WorldEvent update, bool replay)
        {
            if (replay) economyPreparation.EnqueueFront(update, WorldEventFlow.Cost(update));
            else economyPreparation.Enqueue(update, WorldEventFlow.Cost(update));
        }
        private bool ApplyTravelNotice(WorldEvent update)
        {
            bool notice = update.Kind == "business-result" || update.Kind == "dialogue-result" ||
                (update.Kind == "interaction-result" && update.Data.TryGetProperty("Success", out var success) && success.ValueKind == JsonValueKind.False);
            if (!notice) return false;
            // These result messages contain no world mutation. Consume the notice
            // through session UI while native district UI is being unloaded.
            if (update.Data.TryGetProperty("PlayerId", out var actor) && actor.GetString() == playerId &&
                update.Data.TryGetProperty("Message", out var message) && message.ValueKind == JsonValueKind.String)
            {
                string value = message.GetString();
                if (!string.IsNullOrEmpty(value)) Status(value.Length <= 256 ? value : value.Substring(0, 256));
            }
            return true;
        }
        private bool FlushDeferredTravelEvents()
        {
            bool applied = false;
            while (!economyPreparation.IsPending && !TravelReplication.IsBusy && !ApartmentReplication.IsBusy && !WorkerSceneResidency.IsRestoringClient && clientMapRestore == null &&
                deferredTravelEvents.Count > 0 && connection != null && connection.Connected && !terminalSession &&
                clientMutationBudget.TryStart(Stopwatch.GetTimestamp()))
            {
                var entry = deferredTravelEvents.Dequeue(); deferredTravelBytes -= entry.Bytes;
                applied = true;
                ApplyWorldEvent(entry.Update);
            }
            return applied;
        }
        private void RetainMapEvent(WorldEvent update, long started)
        {
            int bytes = CanonicalJson.ByteCount(update);
            if (!SessionTransitionPolicy.CanRetainMapEvent(deferredTravelEvents.Count, deferredTravelBytes, bytes))
            {
                Log("MAP_BUFFER_LIMIT events=" + deferredTravelEvents.Count + " retainedBytes=" + deferredTravelBytes +
                    " incomingBytes=" + bytes + " maxEvents=" + SessionTransitionPolicy.MaxRetainedMapEvents +
                    " maxBytes=" + SessionTransitionPolicy.MaxRetainedMapBytes);
                throw new IOException("Your map could not catch up within its loading buffer. Reconnect to reload the current world.");
            }
            deferredTravelEvents.Enqueue((update, bytes)); deferredTravelBytes += bytes;
            retainedMapDeliveries.Add(update.DeliveryId);
            npcMapPresentationPending = true;
            // During this client's bounded native load, delivery credit means
            // retained in the ordered FIFO. It does not claim native application.
            // Other clients can keep receiving the shared simulation. Native
            // sleep-wake acknowledgement still waits for full application below.
            AppliedWorldEvent(update, started, true);
        }
        private void PumpEconomyPreparation()
        {
            if (worker || !loaded || terminalSession || disposed || connection == null || !connection.Connected) return;
            long commitStarted = Stopwatch.GetTimestamp();
            if (economyPreparation.IsPending)
                economyPreparation.TryProgress(Now, prepared => {
                    if (progressingWorldUpdate == null)
                    {
                        progressingWorldUpdate = prepared;
                        progressingWorldScene = DedicatedWorldRuntime.Scene;
                        progressingWorldProperty = WorkerSceneResidency.CurrentProperty;
                    }
                    if (!ReferenceEquals(progressingWorldUpdate, prepared) || progressingWorldScene != DedicatedWorldRuntime.Scene ||
                        progressingWorldProperty != WorkerSceneResidency.CurrentProperty)
                        throw new InvalidOperationException("Native map changed during a client world update.");
                    bool complete = PreparedWorldUpdate.Advance(prepared, clientMutationBudget);
                    if (disposed || terminalSession || connection == null || !connection.Connected) return false;
                    if (progressingWorldScene != DedicatedWorldRuntime.Scene || progressingWorldProperty != WorkerSceneResidency.CurrentProperty)
                        throw new InvalidOperationException("Native map changed during a client world update.");
                    return complete;
                }, update => {
                    progressingWorldUpdate = null; progressingWorldScene = progressingWorldProperty = null;
                    AppliedWorldEvent(update, commitStarted);
                });
            if (connection == null || !connection.Connected || terminalSession || disposed) return;
            // Commit, replay, live delivery and map catch-up all spend the same
            // frame budget. Retained map state remains older than this FIFO.
            for (int i = 0; i < 24 && !economyPreparation.IsPending && clientMutationBudget.CanStart(Stopwatch.GetTimestamp()); i++)
            {
                if (!TravelReplication.IsBusy && !ApartmentReplication.IsBusy) FlushDeferredTravelEvents();
                if (connection == null || !connection.Connected || terminalSession || disposed ||
                    !clientMutationBudget.CanStart(Stopwatch.GetTimestamp()) || !economyPreparation.TryDequeue(out var update)) return;
                DispatchWorldEvent(update, true);
                if (connection == null || terminalSession) return;
            }
        }
        private static double ElapsedMilliseconds(long started) =>
            (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;
        private bool IsNativeMapCatchup => mapCatchupEpoch.Length > 0 && mapCatchupSettledAt >= 0 &&
            !TravelReplication.IsBusy && !ApartmentReplication.IsBusy && !WorkerSceneResidency.IsRestoringClient && clientMapRestore == null;
        private void BeginClientMutationFrame(int frame)
        {
            // Select once, even if a nested pump changes transition state during
            // this frame. Both modes remain shared by replay and live dispatch.
            if (!mutationFrameSelected || mutationFrame != frame)
            {
                normalClientMutationBudget ??= clientMutationBudget;
                clientMutationBudget = IsNativeMapCatchup ? mapCatchupMutationBudget : normalClientMutationBudget;
                mutationFrameSelected = true; mutationFrame = frame;
            }
            clientMutationBudget.BeginFrame(frame);
        }
        private void ObserveMapCatchupContext(PlayerSceneContext context)
        {
            if (context.TravelPending)
            {
                if (retainedMapDeliveries.Count != 0 || (mapCatchupEpoch.Length > 0 && mapCatchupSettledAt >= 0))
                    throw new InvalidDataException("A new map transition crossed unapplied retained state.");
                mapCatchupEpoch = context.Epoch; mapCatchupGeneration = context.Generation;
                mapCatchupSettledAt = -1; mapCatchupAppliedDelivery = mapCatchupReportedDelivery = 0; nextMapCatchupProgress = 0;
                mapCatchupBarrierRequested = mapCatchupBarrierReported = false; mapCatchupBarrierRound = 0; mapCatchupBarrierDelivery = 0;
            }
            else if (mapCatchupEpoch.Length > 0 && mapCatchupSettledAt < 0)
            {
                if (context.Generation != mapCatchupGeneration + 1)
                    throw new InvalidDataException("Map synchronization belongs to a different assignment.");
                // Native cab/boat travel has its own longer deadline. The bounded
                // catch-up phase starts only once the worker settles this route.
                mapCatchupSettledAt = Now;
            }
        }
        private void UpdateMapCatchupPresentation()
        {
            bool catchingUp = IsNativeMapCatchup;
            if (catchingUp && Now - mapCatchupSettledAt >= MapCatchupDeadline.MaximumSeconds)
                throw new IOException("Your map could not finish synchronizing within its bounded catch-up period. Reconnect to reload the current world.");
            MapCatchupOverlay.SetVisible(catchingUp);
            MapCatchupOverlay.Tick();
        }
        private void ReportMapCatchupProgress()
        {
            UpdateMapCatchupPresentation();
            if (mapCatchupEpoch.Length == 0 || mapCatchupSettledAt < 0 || connection == null || !connection.Connected || terminalSession) return;
            bool localReady = retainedMapDeliveries.Count == 0 && deferredTravelEvents.Count == 0 &&
                !TravelReplication.IsBusy && !ApartmentReplication.IsBusy && !WorkerSceneResidency.IsRestoringClient && clientMapRestore == null;
            bool requestBarrier = localReady && !mapCatchupBarrierRequested;
            bool complete = mapCatchupBarrierDelivery > 0 && !mapCatchupBarrierReported;
            if (!requestBarrier && !complete && (mapCatchupAppliedDelivery == 0 || Now < nextMapCatchupProgress ||
                mapCatchupAppliedDelivery <= mapCatchupReportedDelivery)) return;
            if (requestBarrier || complete)
            {
                // Both requests follow actual ACKs on this TCP stream. Only the
                // service may confirm the complete, sufficiently fresh prefix.
                FlushWorldAcknowledgements(true);
                if (appliedDeliveries.Count != 0) return;
            }
            long applied = complete ? mapCatchupBarrierDelivery : mapCatchupAppliedDelivery;
            if (!connection.Send(MapCatchupProgress.MessageKind, new MapCatchupProgress { Epoch = mapCatchupEpoch,
                Generation = mapCatchupGeneration, AppliedDeliveryId = applied, RequestBarrier = requestBarrier, Complete = complete })) return;
            mapCatchupReportedDelivery = Math.Max(mapCatchupReportedDelivery, applied); nextMapCatchupProgress = Now + 1;
            if (requestBarrier) mapCatchupBarrierRequested = true;
            if (complete) mapCatchupBarrierReported = true;
        }
        private void TrackAppliedRetainedMapDelivery(WorldEvent update, bool retained)
        {
            if (!retained && (retainedMapDeliveries.Remove(update.DeliveryId) || mapCatchupBarrierRequested))
                mapCatchupAppliedDelivery = Math.Max(mapCatchupAppliedDelivery, update.DeliveryId);
        }
        private void ApplyMapCatchupBarrier(WorldEvent update)
        {
            var marker = update.Data.Deserialize<MapCatchupBarrier>(DedicatedProtocol.Json);
            if (!mapCatchupBarrierRequested || !IsNativeMapCatchup || marker == null || marker.Epoch != mapCatchupEpoch ||
                marker.Generation != mapCatchupGeneration || marker.Round != mapCatchupBarrierRound + 1 ||
                marker.Round > MapCatchupDeadline.MaximumBarrierRounds || update.TargetPlayerId != playerId ||
                update.DeliveryId <= mapCatchupBarrierDelivery || retainedMapDeliveries.Count != 0 || deferredTravelEvents.Count != 0)
                throw new InvalidDataException("The ordered map catch-up barrier does not match this synchronization phase.");
            mapCatchupBarrierRound = marker.Round; mapCatchupBarrierDelivery = update.DeliveryId; mapCatchupBarrierReported = false;
        }
        private void AcceptMapCatchupConfirmation(MapCatchupConfirmation confirmation)
        {
            if (confirmation == null || !mapCatchupBarrierRequested || !mapCatchupBarrierReported || !IsNativeMapCatchup ||
                confirmation.Epoch != mapCatchupEpoch || confirmation.Generation != mapCatchupGeneration ||
                confirmation.Round != mapCatchupBarrierRound || confirmation.BarrierDeliveryId != mapCatchupBarrierDelivery ||
                retainedMapDeliveries.Count != 0 || deferredTravelEvents.Count != 0)
                throw new InvalidDataException("The server confirmed a different map synchronization frontier.");
            Log("MAP_CATCHUP_COMPLETE appliedDelivery=" + mapCatchupAppliedDelivery + " barrierDelivery=" + mapCatchupBarrierDelivery +
                " barrierRound=" + mapCatchupBarrierRound + " elapsedSeconds=" +
                (Now - mapCatchupSettledAt).ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
            mapCatchupEpoch = "";
            MapCatchupOverlay.SetVisible(false);
        }
        private void ResetMapCatchup()
        {
            clientMutationBudget.Reset(); normalClientMutationBudget?.Reset(); mapCatchupMutationBudget.Reset(); mutationFrameSelected = false;
            retainedMapDeliveries.Clear(); mapCatchupEpoch = ""; mapCatchupGeneration = mapCatchupAppliedDelivery = mapCatchupReportedDelivery = 0;
            mapCatchupSettledAt = -1; nextMapCatchupProgress = 0;
            mapCatchupBarrierRequested = mapCatchupBarrierReported = false; mapCatchupBarrierRound = 0; mapCatchupBarrierDelivery = 0;
            if (!worker) MapCatchupOverlay.Reset();
        }
        private void ApplyWorldEvent(WorldEvent update)
        {
            long started = Stopwatch.GetTimestamp();
            switch (update.Kind)
            {
                case MapCatchupBarrier.EventKind:
                    ApplyMapCatchupBarrier(update); break;
                case SleepWakeProtocol.EventKind:
                    var wake = update.Data.Deserialize<SleepWakeMarker>(DedicatedProtocol.Json);
                    SleepWakeProtocol.Validate(wake);
                    var sleep = SleepReplication.CurrentState;
                    if (sleep == null || sleep.Phase != "synchronizing" || sleep.Epoch != wake.Epoch ||
                        wake.Revision < clientRevision || wake.GameSeconds < sleep.GameSeconds)
                        throw new InvalidDataException("Morning marker is outside its native sleep transition.");
                    if (pendingWakeMarker != null && pendingWakeMarker.Epoch == wake.Epoch &&
                        !SleepWakeProtocol.Matches(pendingWakeMarker,wake)) throw new InvalidDataException("Morning marker changed within its epoch.");
                    pendingWakeMarker = wake; break;
                case AccountStateChunks.EventKind:
                    var personal = accountUpdates.Accept(update, playerId, accountId);
                    if (personal != null)
                    {
                        if (ownAccount != null && personal.StateRevision < ownAccount.StateRevision)
                            throw new InvalidDataException("Account state went backwards.");
                        PlayerAccountRuntime.ApplyClientAccount(personal); ownAccount = personal;
                        BoatReplication.SetOwnState(personal.Boat);
                    }
                    break;
                case "property-state-chunk":
                case "property-result": PropertyOwnershipReplication.Apply(update); break;
                case "shared-economy-chunk":
                    var encoded = SharedEconomyReplication.AcceptChunk(update);
                    if (encoded != null)
                    {
                        economyPreparation.Start(token => SharedEconomyReplication.PrepareManaged(encoded, token), update, Now);
                        return; // Final ACK belongs exclusively to successful native commit.
                    }
                    break;
                case BoatProtocol.EventKind: BoatReplication.Apply(update.Data.Deserialize<BoatWorldState>(DedicatedProtocol.Json)); break;
                case "npcs": NpcWorldReplication.Apply(update.Data.Deserialize<NpcWorldFrame>(DedicatedProtocol.Json)); break;
                case "timeWeather": TimeWeatherReplication.Apply(update.Data.Deserialize<TimeWeatherSnapshot>(DedicatedProtocol.Json)); break;
                case "crowds": CrowdReplication.Apply(update); break;
                case "quests": QuestReplication.Apply(update); break;
                case "dialogue-state":
                case "dialogue-people":
                case "dialogue-result": DialogueReplication.Apply(update); break;
                case "business-state-chunk":
                    var business = BusinessStateReplication.AcceptChunk(update);
                    if (business != null)
                    {
                        economyPreparation.Start(token => BusinessStateReplication.PrepareManaged(business, token), update, Now);
                        return; // Native commit owns this final chunk's ACK.
                    }
                    break;
                case "business-craft-chunk":
                    var craft = BusinessCraftStateReplication.AcceptChunk(update);
                    if (craft != null)
                    {
                        economyPreparation.Start(token => BusinessCraftStateReplication.PrepareManaged(craft, token), update, Now);
                        return; // Preserve ordering before results/map/sleep controls.
                    }
                    break;
                case "business-result": BusinessReplication.Apply(update); break;
                case "playerLeft":
                    string id = update.Data.Deserialize<PlayerPresence>(DedicatedProtocol.Json).PlayerId;
                    players.Remove(id); avatars?.Remove(id); break;
                default: GameplayReplication.Apply(update); break;
            }
            AppliedWorldEvent(update, started);
        }
        private void AppliedWorldEvent(WorldEvent update, long started, bool retained = false)
        {
            // ACK after the native application/assembler accepted this chunk, not
            // after an entire multi-chunk snapshot (which would deadlock credit).
            if (connection == null || terminalSession) return;
            TrackAppliedRetainedMapDelivery(update, retained);
            double milliseconds = (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;
            bool first = firstAppliedKinds.Add((retained ? "retained/" : "") + update.Kind);
            if (first || milliseconds >= 100)
                Log((retained ? "RETAINED kind=" : "APPLIED kind=") + update.Kind + " delivery=" + update.DeliveryId + " canonicalPayloadBytes=" + CanonicalJson.ByteCount(update.Data) +
                    " milliseconds=" + milliseconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " first=" + first);
            if (appliedDeliveries.Count >= WorldEventFlow.MaxAcknowledgements) FlushWorldAcknowledgements(true);
            if (appliedDeliveries.Count >= WorldEventFlow.MaxAcknowledgements) throw new IOException("World acknowledgement queue is blocked.");
            if (!appliedDeliveries.Contains(update.DeliveryId)) appliedDeliveries.Add(update.DeliveryId);
        }
        private void FlushWorldAcknowledgements(bool full = false)
        {
            if (worker || appliedDeliveries.Count == 0 || connection == null || !connection.Connected) return;
            if (!full && Now < nextWorldAcknowledgement) return;
            if (!connection.Send("worldEventAck", new WorldEventAcknowledgement { DeliveryIds = appliedDeliveries.ToArray() })) return;
            foreach (long id in appliedDeliveries) pendingReceiptDiagnostics.Remove(id);
            acknowledgedEvents += appliedDeliveries.Count; appliedDeliveries.Clear(); nextWorldAcknowledgement = Now + .025;
        }
        private void RejectTravelBusy(InteractionRequest request)
        {
            const string message = "Wait for the current map or shared-day transition to finish.";
            if (request.Kind != null && request.Kind.StartsWith("dialogue-", StringComparison.Ordinal))
                Emit("dialogue-result", new DialogueResult { PlayerId = request.PlayerId, RequestId = request.RequestId, Success = false, Message = message });
            else if (request.Kind != null && request.Kind.StartsWith("business-", StringComparison.Ordinal))
                Emit("business-result", new BusinessResult { PlayerId = request.PlayerId, RequestId = request.RequestId, Success = false, Message = message });
            else Emit("interaction-result", new GameplayState { PlayerId = request.PlayerId, RequestId = request.RequestId, Success = false, Message = message });
        }
        private static bool IsSuccessfulTransactionResult(WorldEvent update)
            => SessionTransitionPolicy.RequiresDurableResult(update);
        private void RequireAccountActor(InteractionRequest request)
        {
            if (request == null || !playerAccounts.TryGetValue(request.PlayerId, out var bound) || bound != request.AccountId ||
                !Guid.TryParseExact(bound, "N", out _))
                throw new InvalidDataException("Interaction has no authenticated account binding.");
        }
        private void HandleAccountInteraction(InteractionRequest request)
        {
            RequireAccountActor(request);
            if (MultiplayerSandboxRuntime.Active && request.Kind != null && request.Kind.StartsWith("dialogue-", StringComparison.Ordinal))
            {
                // A rejected campaign request must not activate or age an account inventory.
                var rejected = request.Kind == DialogueRecoveryProtocol.ActionKind
                    ? DialogueReplication.HandleRecoveryInteraction(request)
                    : DialogueReplication.HandleInteraction(request);
                QueueEvent(rejected);
                return;
            }
            WorldEvent result;
            capturingAccountTransaction = true;
            try
            {
                using (PlayerAccountRuntime.WithAccount(request.AccountId))
                {
                    if (request.Kind == DialogueRecoveryProtocol.ActionKind) result = DialogueReplication.HandleRecoveryInteraction(request);
                    else if (request.Kind != null && request.Kind.StartsWith("dialogue-", StringComparison.Ordinal)) result = DialogueReplication.HandleInteraction(request);
                    else if (request.Kind != null && request.Kind.StartsWith("business-", StringComparison.Ordinal)) result = BusinessReplication.HandleInteraction(request);
                    else if (BoatAuthority.Handles(request)) result = BoatAuthority.Handle(request, Now);
                    else result = GameplayReplication.HandleInteraction(request);
                }
                if (PlayerAccountRuntime.FailClosed) throw new InvalidOperationException("Account inventory could not be captured safely.");
                QueueEvent(result);
                if (PlayerAccountRuntime.IsDirty) { transactionDirty = true; nextCheckpoint = 0; }
            }
            finally { capturingAccountTransaction = false; }
        }
        private void BeginPropertyTransaction(PreparedPropertyChange change)
        {
            if (!worker || !ready || awaitingCommit || transactionDirty || pendingProperties.Count != 0 ||
                !playerAccounts.TryGetValue(change.PlayerId, out var bound) || bound != change.AccountId)
                throw new InvalidOperationException("Wait for the current account transaction to finish saving.");
            using (PlayerAccountRuntime.WithAccount(change.AccountId))
                PropertyOwnershipReplication.ExecuteNative(change, PlayerAccountRuntime.CurrentAccountId);
            if (PlayerAccountRuntime.FailClosed) throw new InvalidOperationException("Property account could not be captured safely.");
            PropertyOwnershipReplication.ApplyToPendingLedger(change, accountLedger);
            pendingProperties.Add(change); transactionDirty = true; nextCheckpoint = 0;
        }
        private void CommitAccountCheckpoint()
        {
            if (committingLedger == null || committingLedger.Revision != world.Revision)
                throw new InvalidDataException("Checkpoint acknowledgement has no matching account ledger.");
            PlayerAccountRuntime.MarkCommitted(world.Revision);
            accountLedger.Revision = world.Revision;
            var propertyReplies = new List<WorldEvent>();
            PropertyOwnershipReplication.OutboundWorldEvent = propertyReplies.Add;
            try { foreach (var change in pendingProperties) PropertyOwnershipReplication.Committed(change); }
            finally { PropertyOwnershipReplication.OutboundWorldEvent = QueueEvent; }
            pendingProperties.Clear();
            PropertyOwnershipReplication.ApplyOwners(accountLedger.StarterPropertyId, accountLedger.Owners);
            publishingCommit = true;
            try
            {
                foreach (var binding in playerAccounts)
                    foreach (var saved in committingLedger.Accounts)
                        if (saved.AccountId == binding.Value && saved.Initialized)
                        { foreach (var update in AccountStateChunks.Capture(saved, binding.Key)) QueueEvent(update); break; }
                if (propertyReplies.Count > 0)
                    foreach (var update in PropertyOwnershipReplication.CaptureEvents()) QueueEvent(update);
                foreach (var reply in propertyReplies) QueueEvent(reply);
                BoatAuthority.MarkCommitted(committingLedger);
                for (int i = 0; i < pendingTransactionResults.Count;)
                {
                    var pending = pendingTransactionResults[i];
                    if (pending.Revision > world.Revision) { i++; continue; }
                    QueueEvent(pending.Update); pendingTransactionResults.RemoveAt(i);
                }
            }
            finally { publishingCommit = false; committingLedger = null; }
            if (transactionDirty || pendingTransactionResults.Count > 0 || PlayerAccountRuntime.IsDirty || accountPreparations.Count > 0)
                nextCheckpoint = 0;
        }
        private void RejectCheckpointBusy(InteractionRequest request)
        {
            const string message = "Your server is saving an account transaction. Please try again in a moment.";
            if (request.Kind != null && request.Kind.StartsWith("dialogue-", StringComparison.Ordinal))
                Emit("dialogue-result", new DialogueResult { PlayerId = request.PlayerId, RequestId = request.RequestId, Success = false, Message = message });
            else if (request.Kind != null && request.Kind.StartsWith("business-", StringComparison.Ordinal))
                Emit("business-result", new BusinessResult { PlayerId = request.PlayerId, RequestId = request.RequestId, Success = false, Message = message });
            else Emit("interaction-result", new GameplayState { PlayerId = request.PlayerId, RequestId = request.RequestId, Success = false, Message = message });
        }
        private void Disconnect()
        {
            if (worker || terminalSession) return;
            disconnectReason = "Disconnected. Choose a server to reconnect.";
            CancelPendingConnection();
            connection?.Send("leave", new {}); connection?.Tick(); connection?.Dispose(); connection = null;
            ready = false; terminalSession = true; returningToTitle = false;
            menu?.SetSessionStatus("Disconnecting...", false);
            ResetInteractionState();
            FinishDisconnectedWorld();
        }
        private void FinishDisconnectedWorld()
        {
            if (!terminalSession || worker) return;
            if (DedicatedWorldRuntime.IsIsolated)
            {
                NpcWorldReplication.SetClientAuthority(true); TimeWeatherReplication.SetClientAuthority(true); GameplayReplication.SetClientAuthority(true);
                DialogueReplication.SetClientAuthority(true); BusinessReplication.SetClientAuthority(true);
                if (!returningToTitle && DedicatedWorldRuntime.TryBeginClientUnload())
                { returningToTitle = true; avatars?.Dispose(); }
                if (!DedicatedWorldRuntime.TryReleaseClientIsolationAtTitle())
                {
                    string reason = DedicatedWorldRuntime.ClientIsolationReleaseWaitReason;
                    if (reason != lastDisconnectWait) { lastDisconnectWait = reason; Log("Disconnect waiting: " + reason); }
                    return;
                }
            }
            // No gameplay scene can now write through the restored personal path.
            NpcWorldReplication.SetClientAuthority(false); TimeWeatherReplication.SetClientAuthority(false);
            GameplayReplication.SetClientAuthority(false); CrowdReplication.SetClientAuthority(false);
            QuestReplication.SetClientAuthority(false); DialogueReplication.SetClientAuthority(false);
            BusinessReplication.SetClientAuthority(false); GameplayReplication.Reset();
            ClientInputDiagnostics.Reset();
            players.Clear(); present.Clear(); events.Clear(); transfer.Reset(); accountTransfer.Reset(); accountUpdates.Reset();
            accountFlow?.Reset(); PlayerAccountRuntime.Reset(); PropertyOwnershipReplication.Stop(); PlayerAppearance.ResetCatalog();
            playerAccounts.Clear(); accountPreparations.Clear(); pendingProperties.Clear(); pendingTransactionResults.Clear();
            accountLedger = committingLedger = null; ownAccount = null; accountId = endpoint = "";
            transactionDirty = capturingAccountTransaction = publishingCommit = sawAccountDirty = false;
            firstAppliedKinds.Clear(); queuedTraffic.Clear(); sentTraffic.Clear(); receivedTraffic.Clear();
            world = null; playerId = secret = checkpointRequest = workerCrowdScene = "";
            accepted = haveWorld = loading = worldStarted = loaded = ready = awaitingCommit = shuttingDown = false;
            committedThisSession = returningToTitle = terminalSession = travelEnabled = travelWasBusy = false;
            sleepEnabled = sleepWasBusy = apartmentEnabled = apartmentWasBusy = false;
            sleepWake.Reset(); pendingWakeMarker=null; acknowledgedWakeToken=""; wakeCheckpoint=0; wakeCaptureFamily=0; wakeDeadline=0;
            clientRevision = finalRevision = acknowledgedEvents = snapshotWireBytes = 0;
            bulkCredit = controlCredit = captureTurn = capturedNpcCount = 0;
            nextPing = nextCheckpoint = nextNpc = nextWeather = nextReady = nextPose = nextDiagnostic = 0;
            nextGameplay = nextEconomy = nextCrowd = nextBusiness = nextWorldAcknowledgement = lastTrafficDiagnostic = 0;
            GameplayReplication.LocalPlayerId = DialogueReplication.LocalPlayerId = BusinessReplication.LocalPlayerId = "";
            avatars?.Dispose(); avatars = new PlayerReplicaRenderer(Log);
            Log("Disconnected; native title ready and personal storage restored."); logPath = null;
            lastDisconnectWait = "";
            menu?.ResetForTitle(disconnectReason);
            catalog?.Refresh();
        }
        private void CancelPendingConnection()
        {
            connectingCancellation?.Cancel(); connectingCancellation?.Dispose(); connectingCancellation = null;
            var pending = connecting; connecting = null;
            if (pending != null) _ = pending.ContinueWith(task =>
            {
                if (task.Status == TaskStatus.RanToCompletion) task.Result.Dispose();
                else if (task.IsFaulted) { _ = task.Exception; }
            }, TaskScheduler.Default);
        }
        private void Fail(Exception ex)
        {
            disconnectReason = "Disconnected: " + ex.Message;
            Plugin.Instance.Log.LogError(ex.ToString());
            // Each game process owns this isolated log; the loader's shared log
            // can be buffered or locked by another client or the worker.
            if (logPath != null) File.AppendAllText(logPath, DateTime.UtcNow.ToString("O") + " Session failure: " + ex + Environment.NewLine);
            ready = false; CancelPendingConnection(); connection?.Dispose(); connection = null;
            if (!worker && accepted)
            {
                Log("Multiplayer stopped: " + ex.Message);
                Status("Disconnected from the server. Returning to title...");
                // The native title/pause panel adds its own single-player restart
                // instruction; keep that panel concise without duplicating it.
                menu?.SetSessionStatus("Disconnected from the server.", false);
            }
            else Status("Multiplayer stopped: " + ex.Message);
            menu?.SetBusy(false); events.Clear(); terminalSession = !worker; returningToTitle = false;
            ResetInteractionState();
            // A lost connection never enables local simulation before native unload.
            if (worker) Environment.Exit(2);
            else FinishDisconnectedWorld();
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            CancelPendingConnection();
            connection?.Dispose(); connection = null; transfer.Reset(); accountTransfer.Reset(); accountUpdates.Reset(); accountFlow?.Dispose(); catalog?.Dispose(); menu?.Dispose(); avatars?.Dispose();
            PlayerAccountRuntime.Reset(); PropertyOwnershipReplication.Stop(); PlayerAppearance.ResetCatalog();
            ResetInteractionState();
            DialogueReplication.OutboundInteraction = null; BusinessReplication.OutboundInteraction = null; BusinessReplication.OutboundWorldEvent = null;
            DialogueReplication.OutboundWorldEvent = null;
            DialogueReplication.Notice = null; BusinessReplication.Notice = null; GameplayReplication.ClientInteractionExtension = null;
        }
        private void ResetInteractionState()
        {
            if (!worker) PlayerChatOverlay.Stop();
            CancelPreparedWorldUpdate();
            ResetMapCatchup();
            ClientInputSchemeGuard.SetEnabled(false);
            if (interactionStateReset) return;
            interactionStateReset = true;
            WorkerNpcSimulation.Reset();
            WorkerSceneResidency.Stop(); PlayerMapRuntime.Reset(); clientScene.Reset();
            clientMapRestore = null; clientMapRestoreAcknowledged = false; npcMapPresentationPending = false; mapInteractions.Clear(); focusingInteraction = null;
            // Keep client authority guards enabled while this isolated process
            // finishes returning to title; only clear owned UI/jobs/session data.
            DialogueReplication.Reset(); BusinessReplication.Reset(); TravelReplication.Stop(); SleepReplication.Stop(); ApartmentReplication.Stop(); SpatialQuestReplication.Stop();
            BoatReplication.Reset(); BoatAuthority.Reset();
            orderedTransitionEvent = null; apartmentTravelContextPending = false; apartmentCheckpointRevision = 0;
            menu?.ClearSleepRecovery();
            deferredTravelEvents.Clear(); deferredTravelBytes = 0;
            appliedDeliveries.Clear();
            pendingReceiptDiagnostics.Clear();
            SharedEconomyReplication.Reset();
        }
        private void CancelPreparedWorldUpdate()
        {
            var previous=progressingWorldUpdate;
            progressingWorldUpdate=null;progressingWorldScene=progressingWorldProperty=null;
            try{(previous as IIncrementalPreparedWorldUpdate)?.Abort();}
            finally{economyPreparation.Cancel();}
        }
    }

    // Pure policy shared by the actual dispatch/send paths and detached tests.
    internal static class SessionTransitionPolicy
    {
        public const int MaxRetainedMapEvents = 16384;
        // A valid native cab route exceeded 64 MiB before its arrival cinematic
        // completed. Allow 120 seconds at 2 MiB/s plus 16 MiB of headroom while
        // retaining an explicit cap; this is not an unbounded slow-load promise.
        public const int MaxRetainedMapBytes = 256 * 1024 * 1024;
        public static bool CanRetainMapEvent(int count, int retainedBytes, int incomingBytes) =>
            count >= 0 && count < MaxRetainedMapEvents && retainedBytes >= 0 && retainedBytes <= MaxRetainedMapBytes &&
            incomingBytes > 0 && incomingBytes <= MaxRetainedMapBytes - retainedBytes;

        internal static bool WaitForAccountCheckpoint(bool busy, bool activeConversation, string requestKind) => busy &&
            !(activeConversation && requestKind != null && requestKind.StartsWith("dialogue-", StringComparison.Ordinal));
        internal static bool RequiresDurableResult(WorldEvent update)
        {
            if (update?.Kind == BoatProtocol.EventKind && update.Data.ValueKind == JsonValueKind.Object)
                return update.Data.TryGetProperty("Operation", out var operation) && operation.ValueKind == JsonValueKind.String &&
                    operation.GetString() != "snapshot" && update.Data.TryGetProperty("Success", out var boatSuccess) && boatSuccess.ValueKind == JsonValueKind.True;
            if (update == null || update.Data.ValueKind != JsonValueKind.Object ||
                (update.Kind != "interaction-result" && update.Kind != "business-result" && update.Kind != "dialogue-result")) return false;
            if (update.Data.TryGetProperty("NeedsCheckpoint", out var checkpoint) && checkpoint.ValueKind == JsonValueKind.False) return false;
            return update.Data.TryGetProperty("Success", out var success) && success.ValueKind == JsonValueKind.True;
        }
        private static string Field(WorldEvent update, string name) => update != null && update.Data.ValueKind == JsonValueKind.Object &&
            update.Data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        internal static bool IsCompletedTravel(WorldEvent update) => update?.Kind == "group-travel" && Field(update, "Phase") == "complete";
        internal static bool IsCatchupAcknowledgement(InteractionRequest request, SleepWakeMarker wake)
        {
            if (request == null) return false;
            if (PlayerSceneMembership.IsTransitionAcknowledgement(request.Kind)) return true;
            if (request.Kind != SleepWakeProtocol.ReadyKind || wake == null || request.TargetId != wake.Epoch) return false;
            try { return SleepWakeProtocol.Matches(wake, request.Data.Deserialize<SleepWakeMarker>(DedicatedProtocol.Json)); }
            catch (Exception ex) when (ex is JsonException || ex is InvalidDataException || ex is InvalidOperationException) { return false; }
        }
        internal static bool IsOrderedAcknowledgement(WorldEvent current, InteractionRequest request, bool preparationPending)
        {
            if (preparationPending || current == null || request == null) return false;
            string epoch = Field(current, "Epoch"), phase = Field(current, "Phase");
            if (!Guid.TryParseExact(epoch, "N", out _) || request.TargetId != epoch) return false;
            return current.Kind == "group-apartment" &&
                ((phase == "prepare" && request.Kind == "group-apartment-prepared") || (phase == "execute" && request.Kind == "group-apartment-arrived")) ||
                current.Kind == "group-travel" &&
                ((phase == "prepare" && request.Kind == "group-travel-prepared") || (phase == "depart" && request.Kind == "group-travel-arrived"));
        }
        internal static bool HasApartmentOverlap(WorldEvent update, bool apartmentBusy, bool travelBusy, bool sleepBusy)
        {
            string phase = Field(update, "Phase");
            if (phase == null || phase == "accepted" || phase == "denied" || phase == "fatal") return false;
            // Readiness votes do not start native sleep. Other players may still
            // enter or leave an apartment while a waiting vote is withdrawn.
            if (update.Kind == "group-sleep" && (phase == "waiting" || phase == "cancelled")) return false;
            return update.Kind == "group-apartment" ? travelBusy || sleepBusy :
                apartmentBusy && (update.Kind == "group-travel" || update.Kind == "group-sleep");
        }
    }
}
