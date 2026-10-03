using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Stopwatch = System.Diagnostics.Stopwatch;
using HarmonyLib;
using Nivalis;
using Nivalis.Dialogue;
using Nivalis.GhostSystem;
using Nivalis.GhostSystem.Ai;
using Nivalis.Localization;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace NivalisTogether.Dedicated
{
    public sealed class DialogueIntent
    {
        public string Conversation { get; set; } = "";
        public long Step { get; set; }
        public int Choice { get; set; } = -1;
    }
    public sealed class DialogueChoiceState
    {
        public string Id { get; set; } = "";
        public string Text { get; set; } = "";
        public bool Enabled { get; set; }
    }
    public sealed class SharedDialogueState
    {
        public long Sequence { get; set; }
        public string Owner { get; set; } = "";
        public string TargetId { get; set; } = "";
        public string Conversation { get; set; } = "";
        public long Step { get; set; }
        public bool Active { get; set; }
        public string TreeId { get; set; } = "";
        public string NodeId { get; set; } = "";
        public string CharacterId { get; set; } = "";
        public string SpeakerId { get; set; } = "";
        public string SpeakerName { get; set; } = "";
        public string Text { get; set; } = "";
        public string VoiceFile { get; set; } = "";
        public int Emotion { get; set; }
        public bool IsMessage { get; set; }
        public DialogueChoiceState[] Choices { get; set; } = Array.Empty<DialogueChoiceState>();
    }
    public sealed class SharedDialoguePerson
    {
        public string PersonId { get; set; } = "";
        public string TargetId { get; set; } = "";
        public bool CanTalk { get; set; }
        public bool Met { get; set; }
        public int Business { get; set; }
        public int Romance { get; set; }
        public int Friend { get; set; }
        public int Enemy { get; set; }
    }
    public sealed class DialoguePeopleState
    {
        public long Sequence { get; set; }
        public SharedDialoguePerson[] People { get; set; } = Array.Empty<SharedDialoguePerson>();
    }
    // Only managed target IDs survive a frame. World identities are comparison
    // tokens, never native references to dereference on a later frame.
    internal sealed class DialoguePeopleCursor
    {
        private readonly List<string> targets = new List<string>();
        private readonly ClientMutationBudget budget;
        private readonly Func<long> timestamp;
        private long manager, registry;
        private string scene;
        private int index, epoch;
        private bool active;
        private double nextPass;
        internal DialoguePeopleCursor(long frequency, Func<long> timestamp)
        { budget = new ClientMutationBudget(frequency, 2, 64); this.timestamp = timestamp; }
        internal void Reset()
        { epoch++; targets.Clear(); index = 0; active = false; nextPass = 0; manager = registry = 0; scene = null; budget.Reset(); }
        internal bool Advance(IEnumerable<string> currentTargets, double now, int frame, long currentManager,
            long currentRegistry, string currentScene, Func<string, bool> apply)
        {
            if (currentManager == 0 || currentRegistry == 0 || !double.IsFinite(now)) { Reset(); return false; }
            if (manager != currentManager || registry != currentRegistry || scene != currentScene)
            { Reset(); manager = currentManager; registry = currentRegistry; scene = currentScene; }
            budget.BeginFrame(frame);
            if (!active)
            {
                if (now < nextPass) return false;
                targets.Clear(); targets.AddRange(currentTargets); index = 0; active = true; nextPass = now + 1;
            }
            int generation = epoch;
            while (index < targets.Count && budget.TryStart(timestamp()))
            {
                string id = targets[index++];
                bool current = apply(id);
                if (generation != epoch) return false;
                if (!current) { Reset(); return false; }
            }
            if (index < targets.Count) return false;
            targets.Clear(); index = 0; active = false; return true;
        }
    }
    public sealed class DialogueResult
    {
        public string PlayerId { get; set; } = "";
        public string RequestId { get; set; } = "";
        public bool Success { get; set; }
        // Acknowledge the native step so the owner can continue while dialogue
        // blocks saving. Personal rewards still await the paired checkpoint.
        public bool NeedsCheckpoint { get; set; } = false;
        public string Message { get; set; } = "";
    }

    // Shared by native timer callbacks and the client-only Continue control.
    // No native references: a callback may submit only the state it displayed.
    internal static class DialogueInputGate
    {
        // Native DisplayChoices also hides a sole choice whose text is empty.
        // Its timeout reports -1 and the native manager follows that hidden link.
        internal static bool IsContinuation(int choiceCount, string onlyChoiceText)
            => choiceCount == 0 || (choiceCount == 1 && string.IsNullOrEmpty(onlyChoiceText));
        internal static bool IsContinuation(DialogueChoiceState[] choices)
            => choices != null && (choices.Length != 1 || choices[0] != null) &&
                IsContinuation(choices.Length, choices.Length == 1 ? choices[0].Text : null);

        internal static bool TryResolve(SharedDialogueState shown, SharedDialogueState current, string player, bool pending,
            int index, bool continuationControl, out int choice)
            => TryResolve(shown, current, player, pending, index, continuationControl, out choice, out _);

        internal static bool TryResolve(SharedDialogueState shown, SharedDialogueState current, string player, bool pending,
            int index, bool continuationControl, out int choice, out string rejected)
        {
            choice = -1; rejected = "";
            if (pending) rejected = "pending";
            else if (shown == null || current == null) rejected = "missing-state";
            else if (!shown.Active || !current.Active) rejected = "inactive";
            else if (string.IsNullOrEmpty(player)) rejected = "missing-local-player";
            else if (current.Owner != player || shown.Owner != player) rejected = "owner";
            else if (string.IsNullOrEmpty(shown.Conversation) || shown.Conversation != current.Conversation) rejected = "conversation";
            else if (shown.Step != current.Step) rejected = "step";
            else if (shown.TargetId != current.TargetId) rejected = "target";
            else if (shown.Choices == null || current.Choices == null) rejected = "missing-choices";
            else if (shown.Choices.Length != current.Choices.Length) rejected = "choice-count";
            if (rejected.Length != 0) return false;
            if (IsContinuation(shown.Choices))
            {
                if (!IsContinuation(current.Choices)) rejected = "continuation-changed";
                else if (shown.Choices.Length == 1 && shown.Choices[0].Id != current.Choices[0].Id) rejected = "continuation-id";
                else if (index != (continuationControl ? 0 : -1)) rejected = "continuation-index";
                return rejected.Length == 0;
            }
            if (continuationControl) rejected = "response-required";
            else if (IsContinuation(current.Choices)) rejected = "response-changed";
            else if (index < 0 || index >= shown.Choices.Length) rejected = "response-index";
            else if (shown.Choices[index] == null || current.Choices[index] == null) rejected = "missing-response";
            else if (!shown.Choices[index].Enabled || !current.Choices[index].Enabled) rejected = "response-disabled";
            else if (shown.Choices[index].Id != current.Choices[index].Id) rejected = "response-id";
            if (rejected.Length != 0) return false;
            choice = index; return true;
        }
    }

    // Pure account/session binding. Releasing a disconnected character does not
    // transfer a partly evaluated story branch to a different account.
    internal sealed class DialogueAccountBinding
    {
        internal string AccountId { get; private set; } = "";
        internal string PlayerId { get; private set; } = "";
        internal bool CanBind(string accountId) => Guid.TryParseExact(accountId, "N", out _) &&
            (AccountId.Length == 0 || AccountId == accountId);
        internal bool Bind(string playerId, string accountId)
        {
            if (!Guid.TryParseExact(playerId, "N", out _) || !CanBind(accountId)) return false;
            AccountId = accountId; PlayerId = playerId; return true;
        }
        internal bool Matches(string playerId, string accountId) =>
            PlayerId.Length != 0 && PlayerId == playerId && AccountId == accountId;
        internal void Release() { PlayerId = ""; }
        internal void Clear() { PlayerId = AccountId = ""; }
        internal void Restore(string accountId)
        {
            if (accountId == null || (accountId.Length != 0 && !Guid.TryParseExact(accountId, "N", out _)))
                throw new InvalidDataException("Invalid persisted conversation account.");
            AccountId = accountId; PlayerId = "";
        }
    }

    // A newer heartbeat sequence is not another permission to execute a node.
    internal sealed class DialogueRecoveryStepGate
    {
        private string conversation = "", account = "";
        private long step = -1;
        internal bool CanConsume(DialogueRecoveryState state) => state != null &&
            (conversation != state.Conversation || account != state.OwnerAccountId || step != state.Step);
        internal void Consume(DialogueRecoveryState state)
        {
            if (!CanConsume(state)) throw new InvalidOperationException("That dialogue step was already submitted.");
            conversation = state.Conversation; account = state.OwnerAccountId; step = state.Step;
        }
        internal void Reset() { conversation = account = ""; step = -1; }
    }

    /// <summary>
    /// Main-thread, single-owner cooperative story dialogue. Only the worker's
    /// actual DialogueTreeProgressManager evaluates nodes and executes actions.
    /// Clients display inert native dialogue data whose callback only sends an
    /// intent. NPC starts require a loaded worker interaction and a fresh accepted
    /// player pose in the same district within eight metres. Phone/automatic
    /// conversations bind to an authenticated loaded account. Disconnecting
    /// retains that account's branch for its returning character to resume.
    /// </summary>
    public static class DialogueReplication
    {
        private static readonly Harmony Harmony = new Harmony("local.nivalis.together.dialogue-authority");
        private static readonly Dictionary<string, TimedPose> Players = new Dictionary<string, TimedPose>();
        private static readonly Dictionary<string, StartDialogueInteraction> Targets = new Dictionary<string, StartDialogueInteraction>();
        private static readonly Dictionary<string, SharedDialoguePerson> ReplicaPeople = new Dictionary<string, SharedDialoguePerson>();
        private static readonly Dictionary<string, long> PersonSequences = new Dictionary<string, long>();
        private static readonly Dictionary<string, long> TargetPersonSequences = new Dictionary<string, long>();
        private static readonly DialoguePeopleCursor PeopleCursor = new DialoguePeopleCursor(Stopwatch.Frequency, Stopwatch.GetTimestamp);
        private static int peopleGeneration;
        private static readonly Dictionary<string, WorldEvent> Results = new Dictionary<string, WorldEvent>();
        private static readonly Queue<string> ResultOrder = new Queue<string>();
        private static readonly HashSet<string> Dismissed = new HashSet<string>();
        private static readonly DialogueAccountBinding AccountBinding = new DialogueAccountBinding();
        private static readonly Dictionary<string, string> RecoveryPlayers = new Dictionary<string, string>();
        private static readonly Dictionary<string, WorldEvent> RecoveryResults = new Dictionary<string, WorldEvent>();
        private static readonly Dictionary<string, string> RecoveryRequestBindings = new Dictionary<string, string>();
        private static readonly Queue<string> RecoveryResultOrder = new Queue<string>();
        private static readonly DialogueRecoveryStepGate RecoveryStepGate = new DialogueRecoveryStepGate();
        private static DialogueRecoveryState recoveryIssued, recoveryReplica;
        private static long recoverySequence, lastRecoverySequence, pendingRecoverySequence;
        private static float nextRecovery;
        private static string pendingRecoveryRequest = "";
        private static bool recoveryAwaitingState, recoveryRetryAfterDenial;
        private sealed class TimedPose { internal PlayerPose Pose; internal float Time; }
        private static bool patched, clientAuthority, executingChoice, rendering, closing, choicePending;
        private static string owner = "", targetId = "", conversation = "", lastNativeNode = "";
        private static string pendingChoiceRequest = "";
        private static long stepRevision, eventSequence, lastClientSequence, displayedStep, displayGeneration;
        private static IntPtr lastNativeStep, displayedUi;
        private static float nextCapture, nextScan, lastActivity, nextIntent;
        private static float nextPauseDiagnostic;
        private static string lastPauseDiagnostic = "";
        private static int clientDiagnosticCount;
        private static string lastContinueDiagnostic = "";
        private static string lastResponseDiagnostic = "";
        private static SharedDialogueState displayedReplica;
        private sealed class ResponseBinding
        {
            internal int Index;
            internal Il2CppSystem.Action<int> Callback;
        }
        private static readonly Dictionary<IntPtr, ResponseBinding> ResponseBindings = new Dictionary<IntPtr, ResponseBinding>();
        private static SharedDialogueState replica;
        private static DialogueStepDisplayData inertStep;
        private static Il2CppSystem.Action<int> choiceCallback;
        private static Il2CppSystem.Action<int> continueCallback;
        private static DialogueChoiceUI continueControl;
        private static Person shownSpeaker;
        private static bool sandboxManagedCleared;
        private static IntPtr sandboxManager, sandboxUi;
        private const string SandboxDialogueMessage = "Campaign conversations are disabled in multiplayer sandbox.";
        public static Action<InteractionRequest> OutboundInteraction { get; set; }
        public static Action<WorldEvent> OutboundWorldEvent { get; set; }
        public static Action<string> Notice { get; set; }
        public static string LocalPlayerId { get; set; } = "";
        public static bool ClientAuthority => clientAuthority;
        public static bool IsConversationActive => DialogueTreeProgressManager.InstanceNotNull() && DialogueTreeProgressManager.Instance.IsInDialogue;
        public static string ConversationAccountId => AccountBinding.AccountId;
        public static DialogueRecoveryState RecoveryState => recoveryReplica;
        public static void RestoreConversationAccount(string accountId)
        {
            if (MultiplayerSandboxRuntime.Active) { AccountBinding.Clear(); owner = ""; return; }
            AccountBinding.Restore(accountId); owner = "";
            // A completed native conversation cannot retain the previous recipient.
            if (!IsConversationActive) AccountBinding.Clear();
            nextCapture = 0;
        }

        public static void SetClientAuthority(bool enabled)
        {
            InstallPatches();
            if(enabled) ReplicatedWorldTargets.Initialize();
            if (clientAuthority != enabled) { peopleGeneration++; PeopleCursor.Reset(); }
            clientAuthority = enabled;
            if (!enabled) { CloseClientPanel(); ReplicaPeople.Clear(); PersonSequences.Clear(); TargetPersonSequences.Clear(); replica = null; lastClientSequence = 0; }
        }
        public static void Reset()
        {
            peopleGeneration++; PeopleCursor.Reset();
            CloseClientPanel(); Players.Clear(); Targets.Clear(); ReplicaPeople.Clear(); PersonSequences.Clear(); TargetPersonSequences.Clear();
            Results.Clear(); ResultOrder.Clear(); Dismissed.Clear(); owner = targetId = conversation = lastNativeNode = "";
            AccountBinding.Clear();
            RecoveryPlayers.Clear(); RecoveryResults.Clear(); RecoveryRequestBindings.Clear(); RecoveryResultOrder.Clear(); RecoveryStepGate.Reset();
            recoveryIssued = recoveryReplica = null; recoverySequence = lastRecoverySequence = pendingRecoverySequence = 0;
            nextRecovery = 0; pendingRecoveryRequest = ""; recoveryAwaitingState = recoveryRetryAfterDenial = false;
            stepRevision = eventSequence = lastClientSequence = displayedStep = 0; lastNativeStep = IntPtr.Zero;
            nextCapture = nextScan = nextIntent = 0; replica = null;
            nextPauseDiagnostic = 0; lastPauseDiagnostic = "";
            sandboxManagedCleared = false; sandboxManager = sandboxUi = IntPtr.Zero;
        }

        /// <summary>Install safe reference/primitive dialogue guards before loading an isolated world.</summary>
        public static void InstallSandboxPolicy() => InstallPatches();

        /// <summary>
        /// Main-thread sandbox cleanup. No choice, end-of-dialogue event, quest
        /// completion or reward is invoked. Relationship replicas remain intact.
        /// </summary>
        public static void ApplySandboxPolicy()
        {
            if (!MultiplayerSandboxRuntime.Active) return;
            InstallPatches();
            if (!sandboxManagedCleared || owner.Length != 0 || conversation.Length != 0 || AccountBinding.AccountId.Length != 0 ||
                replica != null || recoveryIssued != null || recoveryReplica != null || inertStep != null)
            {
                if (inertStep != null) inertStep.OnChoicePicked = null;
                Results.Clear(); ResultOrder.Clear(); Dismissed.Clear(); AccountBinding.Clear();
                owner = targetId = conversation = lastNativeNode = ""; replica = null;
                RecoveryPlayers.Clear(); RecoveryResults.Clear(); RecoveryRequestBindings.Clear(); RecoveryResultOrder.Clear(); RecoveryStepGate.Reset();
                recoveryIssued = recoveryReplica = null; pendingRecoveryRequest = ""; pendingRecoverySequence = 0;
                recoveryAwaitingState = recoveryRetryAfterDenial = false; nextRecovery = 0;
                stepRevision = 0; lastNativeStep = IntPtr.Zero; executingChoice = false;
                ClearPresentationState(); sandboxManagedCleared = true;
            }
            if (DialogueTreeProgressManager.InstanceNotNull())
            {
                var manager = DialogueTreeProgressManager.Instance;
                if (sandboxManager != manager.Pointer || manager.IsInDialogue || manager._stepDisplayData != null)
                {
                    if (manager._stepDisplayData != null) manager._stepDisplayData.OnChoicePicked = null;
                    if (manager._debugStepDisplayData != null) manager._debugStepDisplayData.OnChoicePicked = null;
                    // Native IsInDialogue (9BF2E0) tests the current-tree pointer.
                    // Keep the inactive Nullable<DialogueEventArgs> payload alone:
                    // generated boxed nullable copies are not safe cleanup APIs.
                    manager._currentDialogueTree = null; manager._currentDialogueNode = null; manager._stepDisplayData = null;
                    manager._debugStepDisplayData = null; manager._currentDebugDialogueTree = null; manager._debugCallbackOnDone = null;
                    manager._debugDialogueWithChoicesList?.Clear(); sandboxManager = manager.Pointer;
                    if (manager.IsInDialogue) throw new InvalidOperationException("Sandbox dialogue cleanup left an active native tree.");
                }
            }
            var ui = DialogueUI.Instance;
            if (ui != null && (sandboxUi != ui.Pointer || ui.IsOpen || ui._currentStepData != null ||
                ui._printTextRoutine != null || ui._timeoutRoutine != null || ui._timeoutWaitForPlayerRoutine != null ||
                ui._choiceAnimationSequence != null || ui._preventMenuShortcutsLock != null || ui._preventSavingLock != null))
            {
                CloseSandboxPanel(ui); sandboxUi = ui.Pointer;
            }
        }

        private static void CloseSandboxPanel(DialogueUI ui)
        {
            closing = true;
            try
            {
                // Cancel only this dialogue window's exact coroutine handles.
                if (ui._printTextRoutine != null) { ui.StopCoroutine(ui._printTextRoutine); ui._printTextRoutine = null; }
                if (ui._timeoutRoutine != null) { ui.StopCoroutine(ui._timeoutRoutine); ui._timeoutRoutine = null; }
                if (ui._timeoutWaitForPlayerRoutine != null) { ui.StopCoroutine(ui._timeoutWaitForPlayerRoutine); ui._timeoutWaitForPlayerRoutine = null; }
                if (ui._currentStepData != null) ui._currentStepData.OnChoicePicked = null;
                ui._currentStepData = null;
                if (ui._choiceAnimationSequence != null)
                {
                    // Kill without completion so a pending presentation tween
                    // cannot reacquire dialogue pause/input after Close.
                    DG.Tweening.TweenExtensions.Kill(ui._choiceAnimationSequence, false); ui._choiceAnimationSequence = null;
                }
                if (ui._choiceInstances != null) foreach (var choice in ui._choiceInstances)
                    if (choice != null) choice._callback = null;
                // Native Close (9C50C0) closes this UIWindow and releases only
                // its own menu-shortcut/save handles. It does not finish a tree.
                ui.Close();
            }
            finally { closing = false; }
        }

        private static void ClearPresentationState()
        {
            ++displayGeneration; inertStep = null; displayedReplica = null; ResponseBindings.Clear();
            choiceCallback = continueCallback = null; continueControl = null; shownSpeaker = null;
            displayedUi = IntPtr.Zero; displayedStep = 0; choicePending = false; pendingChoiceRequest = "";
        }
        public static void UpdatePlayerPose(PlayerPose pose)
        {
            if (pose != null && DedicatedProtocol.PoseValid(pose) && !string.IsNullOrEmpty(pose.PlayerId))
                Players[pose.PlayerId] = new TimedPose { Pose = pose, Time = Time.realtimeSinceStartup };
        }
        public static void RemovePlayer(string id)
        {
            if (id == null) return;
            Players.Remove(id); Dismissed.Remove(id);
            if (owner == id) ReleaseOwner(false);
        }
        public static void SetRecoveryPlayerPresent(string playerId, string accountId, bool joined)
        {
            if (MultiplayerSandboxRuntime.Active) return;
            if (!Guid.TryParseExact(playerId, "N", out _) || !Guid.TryParseExact(accountId, "N", out _))
                throw new InvalidDataException("Invalid pending dialogue account presence.");
            if (joined)
            {
                if (RecoveryPlayers.TryGetValue(playerId, out var previous) && previous != accountId)
                    throw new InvalidDataException("Pending dialogue account identity changed.");
                if (!RecoveryPlayers.ContainsKey(playerId) && RecoveryPlayers.Count >= 32)
                    throw new InvalidDataException("Too many pending dialogue participants.");
                RecoveryPlayers[playerId] = accountId;
            }
            else
            {
                if (RecoveryPlayers.TryGetValue(playerId, out var previous) && previous != accountId)
                    throw new InvalidDataException("Pending dialogue account removal changed identity.");
                RecoveryPlayers.Remove(playerId);
                if (owner == playerId && !Players.ContainsKey(playerId)) ReleaseOwner(false);
            }
            nextRecovery = 0;
        }

        public static string SendRecoveryChoice(DialogueRecoveryState shown, DialogueRecoveryChoice choice)
        {
            if (MultiplayerSandboxRuntime.Active) throw new InvalidOperationException(SandboxDialogueMessage);
            if (DedicatedWorldRuntime.IsDedicatedServer || OutboundInteraction == null || shown == null || choice == null ||
                recoveryReplica == null || recoveryReplica.Phase != "state" || pendingRecoveryRequest.Length != 0 || recoveryAwaitingState ||
                shown.Sequence != recoveryReplica.Sequence || shown.Conversation != recoveryReplica.Conversation ||
                shown.Step != recoveryReplica.Step || shown.TargetPlayerId != LocalPlayerId || shown.OwnerAccountId != recoveryReplica.OwnerAccountId)
                throw new InvalidOperationException("This conversation recovery view has changed. Wait for its current choices.");
            var intent = new DialogueRecoveryIntent { Sequence = shown.Sequence, Conversation = shown.Conversation, Step = shown.Step,
                Continue = choice.Continue, ChoiceId = choice.Continue ? "" : choice.Id };
            var request = new InteractionRequest { PlayerId = LocalPlayerId, AccountId = shown.OwnerAccountId, RequestId = Guid.NewGuid().ToString("N"),
                Kind = DialogueRecoveryProtocol.ActionKind, TargetId = shown.TargetId, Data = JsonSerializer.SerializeToElement(intent, DedicatedProtocol.Json) };
            if (!DialogueRecoveryProtocol.AllowsPendingAction(recoveryReplica, request, LocalPlayerId, shown.OwnerAccountId))
                throw new InvalidOperationException("That conversation recovery choice is no longer available.");
            pendingRecoveryRequest = request.RequestId; pendingRecoverySequence = shown.Sequence;
            try { OutboundInteraction(request); return request.RequestId; }
            catch { pendingRecoveryRequest = ""; pendingRecoverySequence = 0; throw; }
        }
        public static bool CanSendRecovery(InteractionRequest request) => !MultiplayerSandboxRuntime.Active && !DedicatedWorldRuntime.IsDedicatedServer &&
            recoveryReplica != null && DialogueRecoveryProtocol.AllowsPendingAction(recoveryReplica, request, LocalPlayerId, recoveryReplica.OwnerAccountId);
        public static bool CanContinueDuringShutdown(InteractionRequest request) => IsConversationActive && request != null &&
            RecoveryPlayers.TryGetValue(request.PlayerId, out var account) && account == AccountBinding.AccountId &&
            DialogueRecoveryProtocol.AllowsPendingAction(recoveryIssued, request, request.PlayerId, account);

        public static bool ApplyRecoveryRejection(DialogueRecoveryRejection rejection)
        {
            if (MultiplayerSandboxRuntime.Active) return false;
            DialogueRecoveryProtocol.ValidateRejection(rejection);
            if (DedicatedWorldRuntime.IsDedicatedServer || !rejection.Retryable || recoveryReplica == null ||
                pendingRecoveryRequest.Length == 0 || rejection.RequestId != pendingRecoveryRequest || rejection.Sequence != pendingRecoverySequence ||
                rejection.TargetPlayerId != LocalPlayerId || rejection.TargetPlayerId != recoveryReplica.TargetPlayerId ||
                rejection.OwnerAccountId != recoveryReplica.OwnerAccountId || rejection.Conversation != recoveryReplica.Conversation ||
                rejection.Step != recoveryReplica.Step) return false;
            pendingRecoveryRequest = ""; pendingRecoverySequence = 0;
            recoveryAwaitingState = recoveryRetryAfterDenial = true;
            return true;
        }

        public static void ApplyRecovery(WorldEvent update)
        {
            if (MultiplayerSandboxRuntime.Active) { ApplySandboxPolicy(); return; }
            if (update?.Kind != DialogueRecoveryProtocol.EventKind || DedicatedWorldRuntime.IsDedicatedServer)
                throw new InvalidOperationException("Dialogue recovery is outside the pending client load.");
            var state = update.Data.Deserialize<DialogueRecoveryState>(DedicatedProtocol.Json);
            DialogueRecoveryProtocol.ValidateWire(state);
            if (state.TargetPlayerId != LocalPlayerId || update.TargetPlayerId != state.TargetPlayerId)
                throw new InvalidDataException("Dialogue recovery was addressed to another character.");
            if (state.Sequence <= lastRecoverySequence) return;
            lastRecoverySequence = state.Sequence;
            if (DialogueRecoveryProtocol.IsResponse(state))
            {
                if (pendingRecoveryRequest.Length != 0 && state.RequestId == pendingRecoveryRequest && recoveryReplica != null &&
                    state.Conversation == recoveryReplica.Conversation && state.Step == recoveryReplica.Step &&
                    state.OwnerAccountId == recoveryReplica.OwnerAccountId && state.TargetId == recoveryReplica.TargetId)
                { pendingRecoveryRequest = ""; pendingRecoverySequence = 0; recoveryAwaitingState = true; recoveryRetryAfterDenial = state.Phase == "denied"; }
                return;
            }
            if (state.Phase == "complete")
            { recoveryReplica = state; pendingRecoveryRequest = ""; pendingRecoverySequence = 0; recoveryAwaitingState = recoveryRetryAfterDenial = false; return; }
            bool changed = recoveryReplica == null || state.Conversation != recoveryReplica.Conversation || state.Step != recoveryReplica.Step;
            recoveryReplica = state;
            if (changed || (recoveryAwaitingState && recoveryRetryAfterDenial))
            { pendingRecoveryRequest = ""; pendingRecoverySequence = 0; recoveryAwaitingState = recoveryRetryAfterDenial = false; }
        }

        public static WorldEvent HandleRecoveryInteraction(InteractionRequest request)
        {
            if (request == null || request.Kind != DialogueRecoveryProtocol.ActionKind || !Guid.TryParseExact(request.PlayerId, "N", out _) ||
                !Guid.TryParseExact(request.AccountId, "N", out _) || !Guid.TryParseExact(request.RequestId, "N", out _))
                throw new InvalidDataException("Invalid dialogue recovery request.");
            var intent = request.Data.Deserialize<DialogueRecoveryIntent>(DedicatedProtocol.Json);
            DialogueRecoveryProtocol.ValidateIntent(intent);
            string key = request.PlayerId + ":" + request.RequestId;
            string binding = RecoveryRequestBinding(request, intent);
            WorldEvent previous = null;
            if (!MultiplayerSandboxRuntime.Active && RecoveryResults.TryGetValue(key, out previous) && RecoveryRequestBindings[key] == binding)
            {
                var replay = previous.Data.Deserialize<DialogueRecoveryState>(DedicatedProtocol.Json);
                replay.Sequence = ++recoverySequence;
                return RecoveryEvent(replay);
            }
            var response = new DialogueRecoveryState { Sequence = ++recoverySequence, Phase = "denied", Conversation = intent.Conversation,
                Step = intent.Step, OwnerAccountId = request.AccountId, TargetPlayerId = request.PlayerId, TargetId = request.TargetId, RequestId = request.RequestId };
            if (previous != null)
            {
                response.Message = "This request identity was already used for another conversation response.";
                nextRecovery = 0; return RecoveryEvent(response);
            }
            bool invoked = false;
            try
            {
                if (MultiplayerSandboxRuntime.Active) throw new InvalidOperationException(SandboxDialogueMessage);
                if (clientAuthority || !DedicatedWorldRuntime.IsDedicatedServer || !DedicatedWorldRuntime.IsWorldReady || PlayerAccountRuntime.FailClosed ||
                    TravelReplication.IsBusy || SleepReplication.IsBusy || ApartmentReplication.IsBusy)
                    throw new InvalidOperationException("Wait for the native world transition before continuing this conversation.");
                if (!RecoveryPlayers.TryGetValue(request.PlayerId, out var account) || account != request.AccountId ||
                    AccountBinding.AccountId != account || !IsConversationActive ||
                    !DialogueRecoveryProtocol.AllowsPendingAction(recoveryIssued, request, request.PlayerId, account) || !RecoveryStepGate.CanConsume(recoveryIssued))
                    throw new InvalidOperationException("This conversation is not waiting for this account or this step.");
                UpdateNativeStep();
                if (conversation != intent.Conversation || stepRevision != intent.Step)
                    throw new InvalidOperationException("The native conversation step changed. Wait for its current choices.");
                var manager = DialogueTreeProgressManager.Instance;
                var data = manager._stepDisplayData ?? throw new InvalidOperationException("The native conversation has no displayed step yet.");
                int count = data.Choices?.Count ?? 0;
                var only = count == 1 ? data.Choices[0] : null;
                bool continuation = (count != 1 || only != null) && DialogueInputGate.IsContinuation(count, only?.Text);
                int choiceIndex = -1;
                if (intent.Continue)
                {
                    if (!continuation) throw new InvalidOperationException("This native step requires an explicit response.");
                    if (DialogueUI.Instance == null) throw new InvalidOperationException("The native dialogue display is unavailable.");
                }
                else
                {
                    if (continuation) throw new InvalidOperationException("This native step requires its continuation acknowledgement.");
                    for (int i = 0; i < count; i++)
                        if (data.Choices[i] != null && data.Choices[i].articyID == intent.ChoiceId)
                        { if (choiceIndex >= 0) throw new InvalidOperationException("The native choice identity is ambiguous."); choiceIndex = i; }
                    if (choiceIndex < 0 || !data.Choices[choiceIndex].Enabled) throw new InvalidOperationException("That native response is no longer enabled.");
                }
                BindOwner(request.PlayerId, account);
                using (PlayerAccountRuntime.WithAccount(account))
                {
                    RecoveryStepGate.Consume(recoveryIssued);
                    executingChoice = true;
                    try
                    {
                        invoked = true;
                        if (continuation)
                        { var ui = DialogueUI.Instance; ui._currentStepData = data; ui._HandleNoChoices_b__59_0(); }
                        else data.ReportChoicePicked(choiceIndex);
                    }
                    catch { PlayerAccountRuntime.Fault("A resumed native dialogue step failed after execution began."); throw; }
                    finally { executingChoice = false; }
                }
                if (PlayerAccountRuntime.FailClosed) throw new InvalidOperationException("The resumed account could not be captured safely.");
                UpdateNativeStep();
                // Only an observed native node/display-data transition creates
                // another permission. A returned callback alone proves no step.
                response.Phase = "accepted"; response.Message = "Conversation response accepted.";
                lastActivity = Time.realtimeSinceStartup; nextCapture = 0;
            }
            catch (Exception ex)
            {
                if (invoked) PlayerAccountRuntime.Fault("Native dialogue recovery did not finish safely.");
                response.Message = RecoveryMessage(ex.Message);
            }
            nextRecovery = 0;
            var result = RecoveryEvent(response); RecoveryResults[key] = result; RecoveryRequestBindings[key] = binding; RecoveryResultOrder.Enqueue(key);
            while (RecoveryResultOrder.Count > 512)
            { string expired = RecoveryResultOrder.Dequeue(); RecoveryResults.Remove(expired); RecoveryRequestBindings.Remove(expired); }
            return result;
        }

        private static string RecoveryRequestBinding(InteractionRequest request, DialogueRecoveryIntent intent)
            => JsonSerializer.Serialize(new { request.AccountId, request.TargetId, intent.Sequence, intent.Conversation, intent.Step, intent.ChoiceId, intent.Continue }, DedicatedProtocol.Json);

        private static WorldEvent RecoveryEvent(DialogueRecoveryState state)
        {
            DialogueRecoveryProtocol.ValidateWire(state);
            return new WorldEvent { Kind = DialogueRecoveryProtocol.EventKind, TargetPlayerId = state.TargetPlayerId,
                Data = JsonSerializer.SerializeToElement(state, DedicatedProtocol.Json) };
        }
        private static string RecoveryMessage(string message)
        {
            if (string.IsNullOrEmpty(message)) return "The native conversation is not ready to continue.";
            var bounded = new StringBuilder(Math.Min(message.Length, 512));
            foreach (char c in message)
            {
                if (bounded.Length == 512) break;
                bounded.Append(char.IsControl(c) && c != '\n' && c != '\r' && c != '\t' ? ' ' : c);
            }
            return bounded.ToString();
        }
        private static DialogueRecoveryState RecoveryHeader(DialogueRecoveryState source, string phase) => new DialogueRecoveryState {
            Sequence = ++recoverySequence, Phase = phase, Conversation = source.Conversation, Step = source.Step,
            OwnerAccountId = source.OwnerAccountId, TargetPlayerId = source.TargetPlayerId, TargetId = source.TargetId };
        private static void TickRecovery()
        {
            if (OutboundWorldEvent == null) return;
            if (!IsConversationActive)
            {
                if (recoveryIssued != null)
                {
                    var completed = RecoveryHeader(recoveryIssued, "complete");
                    completed.Message = "The conversation is complete. Waiting for the fresh saved world.";
                    OutboundWorldEvent(RecoveryEvent(completed)); recoveryIssued = null; RecoveryStepGate.Reset();
                }
                return;
            }
            if (Time.realtimeSinceStartup < nextRecovery || AccountBinding.AccountId.Length == 0) return;
            nextRecovery = Time.realtimeSinceStartup + 2;
            var candidates = new List<string>(RecoveryPlayers.Keys); candidates.Sort(StringComparer.Ordinal);
            string selected = "";
            foreach (var id in candidates) if (RecoveryPlayers[id] == AccountBinding.AccountId) { selected = id; break; }
            if (selected.Length == 0) return;
            BindOwner(selected, AccountBinding.AccountId);
            var state = new DialogueRecoveryState { Sequence = ++recoverySequence, Phase = "state", Conversation = conversation,
                Step = stepRevision, OwnerAccountId = AccountBinding.AccountId, TargetPlayerId = selected,
                TargetId = string.IsNullOrEmpty(targetId) ? "dialogue" : targetId };
            try
            {
                var manager = DialogueTreeProgressManager.Instance;
                if (manager._stepDisplayData == null) state.Message = "Waiting for the current native conversation line.";
                else
                {
                    var native = CaptureDialogue();
                    state.SpeakerName = native.SpeakerName; state.Text = native.Text;
                    state.CanContinue = DialogueInputGate.IsContinuation(native.Choices);
                    if (!state.CanContinue)
                    {
                        state.Choices = new DialogueRecoveryChoice[native.Choices.Length];
                        for (int i = 0; i < state.Choices.Length; i++) state.Choices[i] = new DialogueRecoveryChoice {
                            Id = native.Choices[i].Id, Text = native.Choices[i].Text, Enabled = native.Choices[i].Enabled };
                    }
                    DialogueRecoveryProtocol.ValidateWire(state);
                }
            }
            catch (Exception)
            {
                state.SpeakerName = state.Text = ""; state.CanContinue = false; state.Choices = Array.Empty<DialogueRecoveryChoice>();
                state.Message = "This native dialogue line cannot be represented safely for recovery. The conversation remains paused.";
            }
            OutboundWorldEvent(RecoveryEvent(state)); recoveryIssued = state;
        }

        /// <summary>Gameplay extension: null means unhandled; false means an RPC was handled.</summary>
        public static bool? RouteClientInteraction(IInteractable focus)
        {
            var target = focus == null ? null : focus.TryCast<StartDialogueInteraction>();
            if (target == null) return null;
            // Leave non-dialogue business routing to its own extension. The
            // native StartDialogue barrier still rejects every campaign start.
            if (MultiplayerSandboxRuntime.Active) return null;
            if (!clientAuthority) return null;
            try
            {
                var ghost = target.MyAgent == null ? null : target.MyAgent.MyGhost;
                if (ghost == null || string.IsNullOrEmpty(ghost.Id)) throw new InvalidOperationException("This character is not ready to talk.");
                Send("dialogue-start", ghost.Id, new DialogueIntent());
            }
            catch (Exception ex) { Notice?.Invoke(ex.Message); }
            return false;
        }
        private static bool Send(string kind, string target, DialogueIntent intent)
            => Send(kind, target, intent, out _);
        private static bool Send(string kind, string target, DialogueIntent intent, out string requestId)
        {
            requestId = "";
            if (MultiplayerSandboxRuntime.Active) throw new InvalidOperationException(SandboxDialogueMessage);
            if (OutboundInteraction == null) throw new InvalidOperationException("The shared world connection is not ready.");
            if (Time.realtimeSinceStartup < nextIntent) return false;
            nextIntent = Time.realtimeSinceStartup + 0.15f;
            requestId = Guid.NewGuid().ToString("N");
            OutboundInteraction(new InteractionRequest { RequestId = requestId, Kind = kind, TargetId = string.IsNullOrEmpty(target) ? "dialogue" : target,
                Data = JsonSerializer.SerializeToElement(intent, DedicatedProtocol.Json) });
            return true;
        }

        /// <summary>Worker-only; return/broadcast the result. CaptureEvents carries updated panels.</summary>
        public static WorldEvent HandleInteraction(InteractionRequest request)
        {
            InstallPatches();
            if (!DedicatedWorldRuntime.IsDedicatedServer || clientAuthority) throw new InvalidOperationException("Dialogue intents require the dedicated worker.");
            if (request == null || !DedicatedProtocol.Text(request.PlayerId, 128) || !DedicatedProtocol.Text(request.RequestId, 64) || !DedicatedProtocol.Text(request.TargetId, 128))
                throw new ArgumentException("Invalid dialogue intent.");
            string key = request.PlayerId + ":" + request.RequestId;
            if (!MultiplayerSandboxRuntime.Active && Results.TryGetValue(key, out var old)) return old;
            var result = new DialogueResult { PlayerId = request.PlayerId, RequestId = request.RequestId };
            try
            {
                if (MultiplayerSandboxRuntime.Active) throw new InvalidOperationException(SandboxDialogueMessage);
                if (!DedicatedWorldRuntime.IsWorldReady || !DialogueTreeProgressManager.InstanceNotNull()) throw new InvalidOperationException("The shared story is loading.");
                var actor = RequirePlayer(request.PlayerId);
                if (!Guid.TryParseExact(request.AccountId, "N", out _) || actor.Pose.AccountId != request.AccountId)
                    throw new InvalidOperationException("The character is not bound to this account.");
                UpdateNativeStep();
                if (!AccountBinding.CanBind(request.AccountId))
                    throw new InvalidOperationException("This conversation is waiting for its original account.");
                var manager = DialogueTreeProgressManager.Instance;
                RefreshTargets();
                var intent = request.Data.Deserialize<DialogueIntent>(DedicatedProtocol.Json) ?? throw new InvalidDataException("Invalid dialogue data.");
                if (request.Kind == "dialogue-start")
                {
                    if (!Targets.TryGetValue(request.TargetId, out var target) || target == null)
                        throw new InvalidOperationException("This character's district is not loaded on the server yet.");
                    var ghost = GhostManager.HasInstance ? GhostManager.Instance.GetGhostById(request.TargetId)?.TryCast<AgentGhost>() : null;
                    if (ghost == null || !ghost.IsValid || !ghost.IsActive || ghost.Person == null) throw new InvalidOperationException("This character is not available.");
                    RequireNearby(actor.Pose, ghost.SceneIndex, ghost.Position);
                    if (!string.IsNullOrEmpty(owner) && owner != request.PlayerId) throw new InvalidOperationException("Another player is speaking. Please wait.");
                    if (manager.IsInDialogue)
                    {
                        if (!string.IsNullOrEmpty(targetId) && targetId != request.TargetId) throw new InvalidOperationException("Finish the current shared conversation first.");
                        BindOwner(request.PlayerId, request.AccountId); Dismissed.Remove(owner); lastActivity = Time.realtimeSinceStartup;
                    }
                    else
                    {
                        target.TryToFindActiveDialogue();
                        if (!target.IsActive || !target.CanInteract() || target.currentDialogueTree == null || target.currentDialogueTree.Value == null)
                            throw new InvalidOperationException("This character has nothing to say right now.");
                        BindOwner(request.PlayerId, request.AccountId); targetId = request.TargetId; conversation = Guid.NewGuid().ToString("N");
                        lastNativeStep = IntPtr.Zero; lastNativeNode = ""; stepRevision = 0; Dismissed.Clear(); lastActivity = Time.realtimeSinceStartup;
                        target.StartDialogue(PlayerManager.Instance.LocalPlayer);
                        if (!manager.IsInDialogue) { ReleaseOwner(false); throw new InvalidOperationException("The character could not begin this conversation."); }
                    }
                }
                else if (request.Kind == "dialogue-resume")
                {
                    UpdateNativeStep();
                    if (!manager.IsInDialogue || intent.Conversation != conversation || intent.Step != stepRevision)
                        throw new InvalidOperationException("That conversation is no longer waiting.");
                    if (!string.IsNullOrEmpty(owner) && owner != request.PlayerId) throw new InvalidOperationException("Another player is speaking. Please wait.");
                    if (!string.IsNullOrEmpty(targetId))
                    {
                        var ghost = GhostManager.Instance.GetGhostById(targetId)?.TryCast<AgentGhost>();
                        if (ghost == null) throw new InvalidOperationException("The character is no longer available.");
                        RequireNearby(actor.Pose, ghost.SceneIndex, ghost.Position);
                    }
                    BindOwner(request.PlayerId, request.AccountId); Dismissed.Remove(owner); lastActivity = Time.realtimeSinceStartup;
                }
                else if (request.Kind == "dialogue-choice" || request.Kind == "dialogue-close")
                {
                    UpdateNativeStep();
                    if (!AccountBinding.Matches(request.PlayerId, request.AccountId) || owner != request.PlayerId ||
                        string.IsNullOrEmpty(conversation) || intent.Conversation != conversation || intent.Step != stepRevision)
                        throw new InvalidOperationException("The conversation changed. Wait for the current response.");
                    if (request.Kind == "dialogue-close") ReleaseOwner(true);
                    else
                    {
                        if (!manager.IsInDialogue || manager._stepDisplayData == null) throw new InvalidOperationException("This conversation has ended.");
                        if (!string.IsNullOrEmpty(targetId))
                        {
                            var ghost = GhostManager.Instance.GetGhostById(targetId)?.TryCast<AgentGhost>();
                            if (ghost == null) throw new InvalidOperationException("The character is no longer available.");
                            RequireNearby(actor.Pose, ghost.SceneIndex, ghost.Position);
                        }
                        var data = manager._stepDisplayData;
                        int count = data.Choices == null ? 0 : data.Choices.Count;
                        var onlyChoice = count == 1 ? data.Choices[0] : null;
                        bool continuation = (count != 1 || onlyChoice != null) && DialogueInputGate.IsContinuation(count, onlyChoice?.Text);
                        if (continuation && intent.Choice != -1)
                            throw new InvalidOperationException("That line requires its continuation acknowledgement.");
                        if (!continuation && (intent.Choice < 0 || intent.Choice >= count || data.Choices[intent.Choice] == null || !data.Choices[intent.Choice].Enabled))
                            throw new InvalidOperationException("That response is not available.");
                        executingChoice = true;
                        try
                        {
                            if (continuation)
                            {
                                // Use the game's exact continuation callback; do not guess
                                // the sentinel index or manufacture a terminal story node.
                                var ui = DialogueUI.Instance;
                                if (ui == null) throw new InvalidOperationException("The server dialogue display is not initialized.");
                                ui._currentStepData = data; ui._HandleNoChoices_b__59_0();
                            }
                            else data.ReportChoicePicked(intent.Choice);
                        }
                        finally { executingChoice = false; }
                        lastActivity = Time.realtimeSinceStartup;
                        Trace("worker accepted choice=" + intent.Choice + " conversation=" + intent.Conversation + " step=" + intent.Step +
                            " nativeChoices=" + count + " continuation=" + continuation);
                    }
                }
                else throw new InvalidOperationException("Unknown dialogue operation.");
                result.Success = true; nextCapture = 0; UpdateNativeStep();
            }
            catch (Exception ex) { result.Message = ex.Message; Trace("worker rejected request=" + request.RequestId + " kind=" + request.Kind + " reason=" + ex.GetType().Name + ": " + ex.Message); }
            var evt = Event("dialogue-result", result); evt.TargetPlayerId = request.PlayerId; Results[key] = evt; ResultOrder.Enqueue(key);
            while (ResultOrder.Count > 4096) Results.Remove(ResultOrder.Dequeue());
            return evt;
        }

        private static TimedPose RequirePlayer(string player)
        {
            if (!Players.TryGetValue(player, out var pose) || !pose.Pose.Active || Time.realtimeSinceStartup - pose.Time > 3)
                throw new InvalidOperationException("Your character position is not available to the server.");
            return pose;
        }
        private static void RequireNearby(PlayerPose pose, int scene, Vector3 position)
        {
            string name = scene < 0 ? "" : Path.GetFileNameWithoutExtension(SceneUtility.GetScenePathByBuildIndex(scene));
            if (string.IsNullOrEmpty(name) || name != pose.Scene) throw new InvalidOperationException("Your character is in a different district.");
            if ((new Vector3(pose.X, pose.Y, pose.Z) - position).sqrMagnitude > 64) throw new InvalidOperationException("Move closer to speak to this character.");
        }
        private static void ReleaseOwner(bool dismiss)
        {
            if (dismiss && !string.IsNullOrEmpty(owner)) Dismissed.Add(owner);
            owner = ""; AccountBinding.Release(); nextCapture = 0;
            NormalizeWorkerDialoguePause();
        }
        private static void UpdateNativeStep()
        {
            if (!DialogueTreeProgressManager.InstanceNotNull()) return;
            var manager = DialogueTreeProgressManager.Instance;
            if (!manager.IsInDialogue)
            {
                owner = targetId = conversation = lastNativeNode = ""; AccountBinding.Clear(); lastNativeStep = IntPtr.Zero; Dismissed.Clear(); return;
            }
            if (string.IsNullOrEmpty(conversation)) { conversation = Guid.NewGuid().ToString("N"); targetId = ""; stepRevision = 0; }
            var data = manager._stepDisplayData;
            string node = data?.Dialogue?.TryCast<IArticyGuidHolder>()?.ArticyGUID ?? "";
            IntPtr pointer = data == null ? IntPtr.Zero : data.Pointer;
            if (pointer != lastNativeStep || node != lastNativeNode)
            { lastNativeStep = pointer; lastNativeNode = node; stepRevision++; nextCapture = 0; lastActivity = Time.realtimeSinceStartup; }
        }

        /// <summary>Worker-only. Native dialogue and shared relationship/availability refresh; each event is <=48 KB.</summary>
        public static WorldEvent[] CaptureEvents()
        {
            if (clientAuthority || !DedicatedWorldRuntime.IsDedicatedServer || !DedicatedWorldRuntime.IsWorldReady) return Array.Empty<WorldEvent>();
            InstallPatches(); Tick();
            if (Time.realtimeSinceStartup < nextCapture) return Array.Empty<WorldEvent>();
            nextCapture = Time.realtimeSinceStartup + 1;
            RefreshTargets();
            var events = new List<WorldEvent>();
            events.Add(Event("dialogue-state", CaptureDialogue()));
            var people = new List<SharedDialoguePerson>();
            if (GhostManager.HasInstance) foreach (var ghost in GhostManager.Instance._agentGhosts)
            {
                var person = ghost?.Person; var runtime = person?.RuntimeData;
                if (runtime == null || string.IsNullOrEmpty(person.Guid)) continue;
                var relation = runtime.relationshipVector;
                bool available = !MultiplayerSandboxRuntime.Active && Targets.TryGetValue(ghost.Id, out var target) && target != null && target.IsActive && target.currentDialogueTree != null && target.currentDialogueTree.Value != null;
                people.Add(new SharedDialoguePerson { PersonId = person.Guid, TargetId = ghost.Id, CanTalk = available, Met = runtime._hasBeenMet,
                    Business = relation.Business, Romance = relation.Romance, Friend = relation.Friend, Enemy = relation.Enemy });
                if (people.Count == 64) { events.Add(Event("dialogue-people", new DialoguePeopleState { Sequence = ++eventSequence, People = people.ToArray() })); people.Clear(); }
            }
            if (people.Count > 0) events.Add(Event("dialogue-people", new DialoguePeopleState { Sequence = ++eventSequence, People = people.ToArray() }));
            return events.ToArray();
        }
        private static SharedDialogueState CaptureDialogue()
        {
            var state = new SharedDialogueState { Sequence = ++eventSequence, Owner = owner, TargetId = targetId, Conversation = conversation, Step = stepRevision };
            if (!IsConversationActive || string.IsNullOrEmpty(owner)) return state;
            var data = DialogueTreeProgressManager.Instance._stepDisplayData;
            if (data == null) return state;
            state.Active = true; state.TreeId = data.DialogueTree?.Guid ?? ""; state.NodeId = lastNativeNode;
            state.CharacterId = data.Character?.Guid ?? ""; state.SpeakerId = data.Speaker?.Guid ?? ""; state.SpeakerName = data.Speaker?.DisplayedName ?? "";
            state.Text = data.Text ?? ""; state.VoiceFile = data.VOFileName ?? ""; state.Emotion = (int)data.Emotion; state.IsMessage = data.IsMessage;
            if (data.Choices != null)
            {
                if (data.Choices.Count > 64) throw new InvalidDataException("Too many dialogue choices.");
                state.Choices = new DialogueChoiceState[data.Choices.Count];
                for (int i = 0; i < state.Choices.Length; i++) { var choice = data.Choices[i]; state.Choices[i] = new DialogueChoiceState { Id = choice.articyID ?? "", Text = choice.Text ?? "", Enabled = choice.Enabled }; }
            }
            return state;
        }
        private static WorldEvent Event<T>(string kind, T value)
        {
            var data = JsonSerializer.SerializeToElement(value, DedicatedProtocol.Json);
            if (CanonicalJson.ByteCount(data) > 48000) throw new InvalidDataException("Dialogue update exceeds protocol size.");
            return new WorldEvent { Kind = kind, Data = data };
        }

        public static void Apply(WorldEvent evt)
        {
            if (MultiplayerSandboxRuntime.Active && evt?.Kind != "dialogue-people") { ApplySandboxPolicy(); return; }
            if (!clientAuthority || evt == null) return;
            if (evt.Kind == "dialogue-result")
            {
                var result = evt.Data.Deserialize<DialogueResult>(DedicatedProtocol.Json);
                if (result != null && result.PlayerId == LocalPlayerId)
                {
                    Trace("client result request=" + result.RequestId + " success=" + result.Success);
                    if (!result.Success)
                    {
                        if (result.RequestId == pendingChoiceRequest) { choicePending = false; pendingChoiceRequest = ""; SetButtonsEnabled(true); }
                        Notice?.Invoke(result.Message);
                    }
                }
            }
            else if (evt.Kind == "dialogue-state")
            {
                var state = evt.Data.Deserialize<SharedDialogueState>(DedicatedProtocol.Json);
                if (state == null || state.Sequence <= 0 || state.Choices == null || state.Choices.Length > 64 || state.Text == null || state.Text.Length > 24000 ||
                    state.Owner == null || state.Owner.Length > 128 || state.Conversation == null || state.Conversation.Length > 128)
                    throw new InvalidDataException("Invalid shared dialogue state.");
                if (state.Sequence <= lastClientSequence) return;
                lastClientSequence = state.Sequence; replica = state; Tick();
            }
            else if (evt.Kind == "dialogue-people")
            {
                var state = evt.Data.Deserialize<DialoguePeopleState>(DedicatedProtocol.Json);
                AcceptPeople(state);
            }
        }

        private static void AcceptPeople(DialoguePeopleState state)
        {
            if (state?.People == null || state.Sequence <= 0 || state.People.Length > 64)
                throw new InvalidDataException("Invalid shared relationship state.");
            // Validate the entire packet before publishing any of its rows.
            foreach (var person in state.People)
                if (person == null || !DedicatedProtocol.Text(person.PersonId, 128) || !DedicatedProtocol.Text(person.TargetId, 128))
                    throw new InvalidDataException("Invalid shared character.");
            foreach (var person in state.People)
            {
                if (PersonSequences.TryGetValue(person.PersonId, out long seq) && seq >= state.Sequence) continue;
                PersonSequences[person.PersonId] = state.Sequence; ReplicaPeople[person.TargetId] = person;
                TargetPersonSequences[person.TargetId] = state.Sequence;
            }
        }

        public static void Tick()
        {
            if (MultiplayerSandboxRuntime.Active)
            {
                ApplySandboxPolicy();
                if (clientAuthority)
                {
                    // Campaign dialogue is disabled here. Relationship updates
                    // resolve their own current ghosts and do not use Targets;
                    // avoid rebuilding the unused conversation interaction map.
                    ApplyPeople();
                }
                return;
            }
            if (!DedicatedWorldRuntime.IsWorldReady) { PeopleCursor.Reset(); return; }
            InstallPatches();
            if (!clientAuthority)
            {
                if (!DedicatedWorldRuntime.IsDedicatedServer) return;
                NormalizeWorkerDialoguePause();
                DiagnoseWorkerPause();
                UpdateNativeStep();
                TickRecovery();
                if (string.IsNullOrEmpty(conversation)) return;
                bool pendingOwner = owner.Length != 0 && RecoveryPlayers.TryGetValue(owner, out var recoveryAccount) &&
                    AccountBinding.Matches(owner, recoveryAccount);
                if (!string.IsNullOrEmpty(owner) && !pendingOwner && (!Players.TryGetValue(owner, out var pose) || !pose.Pose.Active ||
                    !AccountBinding.Matches(owner, pose.Pose.AccountId) || Time.realtimeSinceStartup - pose.Time > 5 ||
                    Time.realtimeSinceStartup - lastActivity > 180)) ReleaseOwner(true);
                // An active tree with no persisted/observed account binding is
                // never awarded to whichever character happens to arrive next.
                if (AccountBinding.AccountId.Length != 0) TrySelectAccountOwner(false);
                return;
            }
            if (Time.realtimeSinceStartup >= nextScan) { nextScan = Time.realtimeSinceStartup + 1; RefreshTargets(); }
            ApplyPeople();
            if (replica == null || !replica.Active || replica.Owner != LocalPlayerId) { CloseClientPanel(); return; }
            var ui = DialogueUI.Instance;
            if (ui == null) return;
            if (inertStep != null && displayedStep == replica.Step && displayedUi == ui.Pointer && inertStep.DialogueTree?.Guid == replica.Conversation)
            { EnsureContinueControl(ui); EnsureResponseControls(ui); return; }
            Display(ui, replica);
        }
        private static void NormalizeWorkerDialoguePause()
        {
            if(clientAuthority||!DedicatedWorldRuntime.IsDedicatedServer)return;
            var ui=DialogueUI.Instance;
            if(ui==null||(!ui.pauseTimeWhenOpen&&!ui.pauseTimeCompletely))return;
            ui.pauseTimeWhenOpen=false;ui.pauseTimeCompletely=false;
            // Native SwitchRequiresPause refreshes UIManager's aggregated locks
            // for an open window even if the flags already equal the arguments.
            // Other windows and non-UI pause owners retain their own locks.
            ui.SwitchRequiresPause(false,false);
            UnityEngine.Debug.Log("[Nivalis Together] Cleared worker dialogue window pause; pending conversation preserved.");
        }
        private static void DiagnoseWorkerPause()
        {
            if(Time.realtimeSinceStartup<nextPauseDiagnostic)return;
            nextPauseDiagnostic=Time.realtimeSinceStartup+10;
            // Observe the actual owners; never release unrelated story, travel,
            // tutorial or UI pauses merely because the worker has no players.
            try{
                var clock=TimeOfDayManager._instance;
                if(clock==null)return;
                var owners=new List<string>();var nativeOwners=clock._isPaused?._overrideOwners;
                int count=nativeOwners?.Count??0;
                if(count>0){
                    int take=Math.Min(count,16);
                    var snapshot=new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Il2CppSystem.Object>(take);
                    nativeOwners.CopyTo(snapshot,0,take);
                    foreach(var item in snapshot){
                        if(item==null){owners.Add("null");continue;}
                        string type=item.GetIl2CppType()?.FullName??"unknown";
                        if(type.Length>160)type=type.Substring(0,160);
                        var unity=item.TryCast<Object>();string name=unity==null?"":unity.name;
                        if(name.Length>80)name=name.Substring(0,80);
                        owners.Add(type+(name.Length==0?"":"("+name+")"));
                    }
                }
                var ui=DialogueUI.Instance;var manager=DialogueTreeProgressManager.Instance;
                string state="paused="+clock.IsPaused+" unityScale="+Time.timeScale+" clockScale="+clock.TimeScaleOverride+" tickMultiplier="+clock.timeTickMultiplier+
                    " owners="+count+"["+string.Join(",",owners)+"] loadedPlayer="+HasLoadedPlayer()+" activeDialogue="+(manager?.IsInDialogue??false)+
                    " queued="+(manager?._phoneDialogueQueue?.Count??0)+" owner="+owner+" uiOpen="+(ui?.IsOpen??false)+" uiPause="+(ui?.pauseTimeWhenOpen??false)+
                    " uiCompletePause="+(ui?.pauseTimeCompletely??false)+DescribeWorkerUiPause();
                if(state==lastPauseDiagnostic)return;
                lastPauseDiagnostic=state;
                UnityEngine.Debug.Log("[Nivalis Together] WORKER_PAUSE gameSeconds="+TimeOfDayManager.TotalGameSeconds+" "+state);
            }
            catch(Exception ex){UnityEngine.Debug.LogWarning("[Nivalis Together] Worker pause diagnostic unavailable: "+ex.GetType().Name+": "+ex.Message);}
        }
        private static string DescribeWorkerUiPause()
        {
            // Indexed, bounded reads preserve native window order and include
            // pointer identity so a replacement panel changes the diagnostic.
            string panels,windows;
            try{
                var manager=UIManager.InstanceNotNull()?UIManager.Instance:null;
                var list=manager?._pauseTimePanels;
                panels=list==null?"missing":DescribeWorkerUiRows(list.Count,index=>list[index]);
            }catch(Exception ex){panels="unavailable:"+DiagnosticLabel(ex.GetType().Name,80);}
            try{
                var list=UIWindowManager._instance?._openWindows;
                windows=list==null?"missing":DescribeWorkerUiRows(list.Count,index=>list[index]);
            }catch(Exception ex){windows="unavailable:"+DiagnosticLabel(ex.GetType().Name,80);}
            return " pausePanels="+panels+" openWindows="+windows;
        }
        private static string DescribeWorkerUiRows(int count,Func<int,UIPanel> read)
        {
            var rows=new List<string>();int take=Math.Min(Math.Max(count,0),12);
            for(int index=0;index<take;index++){
                try{
                    var panel=read(index);
                    if(panel==null){rows.Add(index+":null");continue;}
                    var window=panel.TryCast<UIWindow>();
                    rows.Add(index+":"+DiagnosticLabel(panel.GetIl2CppType()?.FullName,160)+
                        "("+DiagnosticLabel(panel.name,80)+")@"+panel.Pointer.ToInt64().ToString("X")+
                        ":visible="+panel.IsVisible+":open="+(window==null?"n/a":window.IsOpen.ToString())+
                        ":pause="+panel.PauseTime+":pauseWhenOpen="+panel.pauseTimeWhenOpen+
                        ":completePause="+panel.pauseTimeCompletely);
                }catch(Exception ex){rows.Add(index+":unavailable:"+DiagnosticLabel(ex.GetType().Name,80));}
            }
            if(count>take)rows.Add("..."+(count-take)+" more");
            return count+"["+string.Join(",",rows)+"]";
        }
        private static string DiagnosticLabel(string value,int limit)
        {
            value=(value??"").Replace('\r',' ').Replace('\n',' ').Replace('\t',' ');
            return value.Length<=limit?value:value.Substring(0,limit);
        }
        private static void RefreshTargets()
        {
            Targets.Clear();
            foreach (var target in ReplicatedWorldTargets.Snapshot<StartDialogueInteraction>())
            {
                if (target == null || !target.gameObject.scene.IsValid() || !target.gameObject.scene.isLoaded) continue;
                var ghost = target.MyAgent?.MyGhost;
                if (ghost != null && !string.IsNullOrEmpty(ghost.Id)) Targets[ghost.Id] = target;
            }
        }
        private static void ApplyPeople()
        {
            if (!clientAuthority || !DedicatedWorldRuntime.IsWorldReady || !GhostManager.HasInstance)
            { PeopleCursor.Reset(); return; }
            var manager = GhostManager.Instance;
            var registry = manager._ghostsDictionary;
            if (registry == null) { PeopleCursor.Reset(); return; }
            long managerIdentity = manager.Pointer.ToInt64(), registryIdentity = registry.Pointer.ToInt64();
            string scene = DedicatedWorldRuntime.Scene;
            int generation = peopleGeneration;
            bool complete = PeopleCursor.Advance(ReplicaPeople.Keys, Time.realtimeSinceStartup, Time.frameCount,
                managerIdentity, registryIdentity, scene, id => ApplyPerson(id, manager, generation));
            if (!PeopleWorldCurrent(generation, managerIdentity, registryIdentity, scene)) { PeopleCursor.Reset(); return; }
            if (!complete) return;
            // Preserve the native conversation's relationship display cadence.
            var ui = DialogueUI.Instance;
            var speaker = inertStep == null ? null : shownSpeaker?.RuntimeData;
            var matrix = speaker == null ? null : ui?.miniEmotionMatrix;
            if (matrix != null) matrix.SetVector(speaker.relationshipVector);
        }
        private static bool PeopleWorldCurrent(int generation, long manager, long registry, string scene)
        {
            if (!clientAuthority || generation != peopleGeneration || !DedicatedWorldRuntime.IsWorldReady ||
                DedicatedWorldRuntime.Scene != scene || !GhostManager.HasInstance) return false;
            var current = GhostManager.Instance;
            return current.Pointer.ToInt64() == manager && current._ghostsDictionary?.Pointer.ToInt64() == registry;
        }
        private static bool ApplyPerson(string id, GhostManager manager, int generation)
        {
            if (!clientAuthority || generation != peopleGeneration) return false;
            if (!ReplicaPeople.TryGetValue(id, out var update)) return true;
            // A person may acquire another target ID. Its old cached entry must
            // not overwrite the newer relationship through a still-live alias.
            if (!TargetPersonSequences.TryGetValue(id, out long targetSequence) ||
                !PersonSequences.TryGetValue(update.PersonId, out long personSequence) || targetSequence != personSequence) return true;
            // The manager is fresh for this invocation only. Lookup/read paths
            // and raw relationship fields do not dispatch gameplay callbacks;
            // session resets are still checked before and after the native read.
            var ghost = manager.GetGhostById(id)?.TryCast<AgentGhost>();
            var person = ghost?.Person;
            if (person == null || person.Guid != update.PersonId) return true;
            var data = person.RuntimeData;
            if (data == null) return true;
            if (!clientAuthority || generation != peopleGeneration) return false;
            if (data._hasBeenMet != update.Met) data._hasBeenMet = update.Met;
            var relationship = data.relationshipVector;
            if (relationship.Business != update.Business || relationship.Romance != update.Romance ||
                relationship.Friend != update.Friend || relationship.Enemy != update.Enemy)
                data.relationshipVector = new RelationshipVector { Business = update.Business, Romance = update.Romance, Friend = update.Friend, Enemy = update.Enemy };
            return generation == peopleGeneration && clientAuthority;
        }
        private static void Display(DialogueUI ui, SharedDialogueState state)
        {
            RefreshTargets();
            var character=ResolveDisplayPerson(state.CharacterId);var speaker=ResolveDisplayPerson(state.SpeakerId);
            var node = new DialogueWithChoices { articyGuid = state.NodeId ?? "", dialogueText = state.Text, text = new PronounVersionsString(state.Text), speaker = speaker,
                emotion = (CharacterCommunication.EmotionType)state.Emotion, voFileName = state.VoiceFile ?? "", pausesTime = false };
            var tree = new DialogueTree { Guid = state.Conversation, articyGuid = state.TreeId ?? "", isPhoneCall = state.IsMessage };
            inertStep = new DialogueStepDisplayData { Character = character, DialogueTree = tree.Cast<IDialogueTree>(), Dialogue = node.Cast<IDialogue>(), IsMessage = state.IsMessage,
                Choices = new Il2CppSystem.Collections.Generic.List<DialogueStepDisplayData.Choice>() };
            foreach (var choice in state.Choices)
            {
                if (choice == null || choice.Text == null || choice.Text.Length > 8000 || choice.Id == null || choice.Id.Length > 128) throw new InvalidDataException("Invalid shared dialogue choice.");
                inertStep.Choices.Add(new DialogueStepDisplayData.Choice { Text = choice.Text, Enabled = choice.Enabled, articyID = choice.Id, ToDialogue = null });
            }
            long generation = ++displayGeneration;
            clientDiagnosticCount = 0; lastContinueDiagnostic = lastResponseDiagnostic = "";
            displayedReplica = state; ResponseBindings.Clear();
            TraceClientDiagnostic("client display generation=" + generation + " local=" + DiagnosticId(LocalPlayerId) + " " + StateDiagnostic(state));
            choiceCallback = (Il2CppSystem.Action<int>)(Action<int>)(index => SendChoice(state, generation, index, false));
            continueCallback = (Il2CppSystem.Action<int>)(Action<int>)(index => SendChoice(state, generation, index, true));
            continueControl = null;
            inertStep.OnChoicePicked = choiceCallback;
            choicePending = false; pendingChoiceRequest = ""; displayedStep = state.Step; displayedUi = ui.Pointer; shownSpeaker = speaker;
            rendering = true;
            try
            {
                ui._currentStepData = inertStep;
                ui.pauseTimeWhenOpen = false; ui.pauseTimeCompletely = false;
                ui.SwitchRequiresPause(false, false);
                if (!ui.IsOpen) ui.OnDialogueStart(state.IsMessage);
                ui.OnDialogueStep(inertStep);
                ui.Show();
                // A submitted choice disables the persistent continuation button.
                // A new authoritative step must allow its own choices again.
                SetButtonsEnabled(true);
                EnsureContinueControl(ui);
                EnsureResponseControls(ui);
                if (ui.speakerName != null) ui.speakerName.text = state.SpeakerName ?? "";
            }
            finally { rendering = false; }
        }
        private static Person ResolveDisplayPerson(string id)
        {
            if(string.IsNullOrEmpty(id))return null;
            var person=ScriptableObjectDatabase.Instance.Provide<Person>(id);
            return person!=null&&person.Guid==id&&person.RuntimeData!=null?person:null;
        }
        private static void EnsureContinueControl(DialogueUI ui)
        {
            string unavailable = inertStep == null ? "missing-inert-step" : replica == null ? "missing-replica" :
                !DialogueInputGate.IsContinuation(replica.Choices) ? "response-required" : !ui.IsOpen ? "closed" : !ui.IsVisible ? "hidden" :
                ui._currentStepData == null ? "missing-current-step" : ui._currentStepData.Pointer != inertStep.Pointer ? "different-current-step" :
                ui._choiceInstances == null ? "missing-control-list" : ui.choicePrefab == null ? "missing-prefab" :
                ui.choicesParent == null ? "missing-parent" : continueCallback == null ? "missing-callback" : "";
            if (unavailable.Length != 0) { TraceContinueState(ui, unavailable); return; }
            // DisplayLine may run later after the initial opening animation, in
            // which case it destroys the previous controls. Recreate only once
            // its real no-choice display and native timeout have been installed.
            if (ui.fullScreenButton == null || !ui.fullScreenButton.gameObject.activeSelf) { TraceContinueState(ui, "awaiting-native-no-choice-ui"); return; }
            if (continueControl != null)
                foreach (var item in ui._choiceInstances) if (item != null && item.Pointer == continueControl.Pointer)
                {
                    // Native OnChoiceSelected clears both fields after invoking
                    // us, even for a throttled or denied send. Rearm on a later
                    // Unity tick; doing it inside the callback would be erased.
                    if (item._choice == null || item._callback == null || item._choice.Enabled == choicePending) BindContinueControl(item);
                    item.Interactable = !choicePending && ContinueAnimationFinished(ui);
                    TraceContinueState(ui, choicePending ? "pending" : ContinueAnimationFinished(ui) ? "ready" : "animating");
                    return;
                }
            if (ui._choiceInstances.Count != 0) { TraceContinueState(ui, "native-controls-present"); return; }
            continueControl = Object.Instantiate(ui.choicePrefab, ui.choicesParent);
            BindContinueControl(continueControl);
            ui._choiceInstances.Add(continueControl);
            // The native zero-choice path deactivates this object; its choice
            // animation only scales the parent and does not reactivate it.
            ui.choicesParent.gameObject.SetActive(true);
            // Keep the native zero-choice or single-empty-link list. TimeoutRoutine keeps
            // its original duration, visibility/skip gate and -1 callback. This
            // animation requests native mouse/menu input and does not stop it.
            ui.AnimateToChoices(null);
            ui.SwitchRequiresPause(false, true);
            // Native animation completion reads each control's _choice. Do not
            // allow an early click to clear that field before completion.
            continueControl.Interactable = !choicePending && ContinueAnimationFinished(ui);
            Trace("client continuation available conversation=" + replica.Conversation + " step=" + replica.Step + " nativeTimerHandle=" + (ui._timeoutRoutine != null));
        }
        private static void BindContinueControl(DialogueChoiceUI control)
        {
            var presentation = new DialogueStepDisplayData.Choice { Text = "Continue", Enabled = !choicePending, articyID = "", ToDialogue = null };
            control.SetChoice(presentation, 0, continueCallback, replica.IsMessage);
            control.Interactable = !choicePending;
        }
        private static bool ContinueAnimationFinished(DialogueUI ui)
            => ui._choiceAnimationSequence == null || !DG.Tweening.TweenExtensions.IsActive(ui._choiceAnimationSequence) ||
                DG.Tweening.TweenExtensions.IsComplete(ui._choiceAnimationSequence);
        private static void EnsureResponseControls(DialogueUI ui)
        {
            if (inertStep == null || displayedReplica == null || replica == null || DialogueInputGate.IsContinuation(displayedReplica.Choices) ||
                !ui.IsOpen || !ui.IsVisible || ui._currentStepData == null || ui._currentStepData.Pointer != inertStep.Pointer ||
                ui._choiceInstances == null || inertStep.Choices == null || choiceCallback == null) return;
            TraceResponseControls(ui, "observed");
            // Native DisplayChoices emits controls in list order. Never invent a
            // missing response or assign a callback to a different display's UI.
            if (ui._choiceInstances.Count != inertStep.Choices.Count || ui._choiceInstances.Count != displayedReplica.Choices.Length) return;
            bool finished = ContinueAnimationFinished(ui);
            for (int i = 0; i < ui._choiceInstances.Count; i++)
            {
                var control = ui._choiceInstances[i];
                var expected = inertStep.Choices[i];
                if (control == null || expected == null || control._choiceIndex != i) continue;
                if (!ResponseBindings.TryGetValue(control.Pointer, out var binding))
                {
                    if (control._choice != null && control._choice.Pointer != expected.Pointer) continue;
                    // Keep the game's original response callback/history path
                    // when available. A missing native callback can use the same
                    // rooted, generation-checked input callback as the step.
                    binding = new ResponseBinding { Index = i, Callback = control._callback ?? choiceCallback };
                    ResponseBindings.Add(control.Pointer, binding);
                }
                if (binding.Index != i) continue;
                if (control._choice == null || control._callback == null)
                {
                    control.SetChoice(expected, i, binding.Callback, displayedReplica.IsMessage);
                    TraceClientDiagnostic("client response rearmed generation=" + displayGeneration + " index=" + i + " pending=" + choicePending);
                }
                // A rejected/throttled click is retryable on a later Unity tick.
                // Rebinding never invokes the callback or selects a response.
                control.Interactable = finished && DialogueInputGate.TryResolve(displayedReplica, replica, LocalPlayerId, choicePending, i, false, out _);
            }
            TraceResponseControls(ui, "ready");
        }
        private static void SendChoice(SharedDialogueState shown, long generation, int index, bool continuationControl)
        {
            string source = continuationControl ? "continue-control" : "native";
            string rejected = !clientAuthority ? "not-client-authority" : generation != displayGeneration ? "generation" : "";
            int choice = -1;
            if (rejected.Length != 0 || !DialogueInputGate.TryResolve(shown, replica, LocalPlayerId, choicePending, index, continuationControl, out choice, out rejected))
            {
                TraceClientDiagnostic("client callback ignored reason=" + rejected + " source=" + source + " index=" + index +
                    " generation=" + generation + " current=" + displayGeneration + " pending=" + choicePending +
                    " local=" + DiagnosticId(LocalPlayerId) + " shown={" + StateDiagnostic(shown) + "} replica={" + StateDiagnostic(replica) + "}");
                return;
            }
            try
            {
                if (!Send("dialogue-choice", shown.TargetId, new DialogueIntent { Conversation = shown.Conversation, Step = shown.Step, Choice = choice }, out string requestId))
                { Trace("client callback throttled source=" + source + " step=" + shown.Step); return; }
                pendingChoiceRequest = requestId; choicePending = true; SetButtonsEnabled(false);
                Trace("client acknowledgement sent source=" + source + " choice=" + choice + " conversation=" + shown.Conversation + " step=" + shown.Step);
            }
            catch (Exception ex)
            {
                TraceClientDiagnostic("client acknowledgement failed source=" + source + " index=" + index + " reason=" + ex.GetType().Name + ": " + DiagnosticId(ex.Message));
                Notice?.Invoke(ex.Message);
            }
        }
        private static void Trace(string message) => UnityEngine.Debug.Log("[Nivalis Together] DIALOGUE_ACK " + message);
        private static void TraceClientDiagnostic(string message)
        {
            if (clientDiagnosticCount >= 32) return;
            clientDiagnosticCount++; Trace(message);
        }
        private static string DiagnosticId(string value)
            => value == null ? "<null>" : (value.Length > 128 ? value.Substring(0, 128) : value).Replace('\r', ' ').Replace('\n', ' ');
        private static string StateDiagnostic(SharedDialogueState state)
        {
            if (state == null) return "null";
            string choices = state.Choices == null ? "null" : state.Choices.Length.ToString();
            if (state.Choices != null)
                for (int i = 0; i < Math.Min(state.Choices.Length, 4); i++)
                {
                    var value = state.Choices[i];
                    choices += value == null ? ";" + i + "=null" : ";" + i + "={id=" + DiagnosticId(value.Id) +
                        ",textLength=" + (value.Text == null ? -1 : value.Text.Length) + ",enabled=" + value.Enabled + "}";
                }
            return "seq=" + state.Sequence + " active=" + state.Active + " owner=" + DiagnosticId(state.Owner) +
                " conversation=" + DiagnosticId(state.Conversation) + " step=" + state.Step + " target=" + DiagnosticId(state.TargetId) +
                " continuation=" + DialogueInputGate.IsContinuation(state.Choices) + " choices=[" + choices + "]";
        }
        private static void TraceContinueState(DialogueUI ui, string status)
        {
            if (status == lastContinueDiagnostic || clientDiagnosticCount >= 32) return;
            lastContinueDiagnostic = status;
            TraceClientDiagnostic("client continuation state=" + status + " generation=" + displayGeneration + " pending=" + choicePending +
                " open=" + ui.IsOpen + " visible=" + ui.IsVisible + " currentMatches=" +
                (inertStep != null && ui._currentStepData != null && ui._currentStepData.Pointer == inertStep.Pointer) +
                " controls=" + (ui._choiceInstances == null ? -1 : ui._choiceInstances.Count) +
                " fullScreenActive=" + (ui.fullScreenButton != null && ui.fullScreenButton.gameObject.activeSelf) +
                " parentActive=" + (ui.choicesParent != null && ui.choicesParent.gameObject.activeInHierarchy) + " " + StateDiagnostic(replica));
        }
        private static void TraceResponseControls(DialogueUI ui, string stage)
        {
            if (clientDiagnosticCount >= 32) return;
            string state = "pending=" + choicePending + " animationFinished=" + ContinueAnimationFinished(ui) +
                " count=" + ui._choiceInstances.Count + " stepCallback=" + (inertStep.OnChoicePicked != null) +
                " parentActive=" + (ui.choicesParent != null && ui.choicesParent.gameObject.activeInHierarchy);
            for (int i = 0; i < Math.Min(ui._choiceInstances.Count, 4); i++)
            {
                var item = ui._choiceInstances[i];
                if (item == null) { state += " control" + i + "=null"; continue; }
                state += " control" + i + "={pointer=" + item.Pointer.ToInt64().ToString("X") + " index=" + item._choiceIndex +
                    " choice=" + (item._choice != null) + " callback=" + (item._callback != null) +
                    " interactable=" + (item.button != null && item.button.interactable) +
                    " effectiveInteractable=" + (item.button != null && item.button.IsInteractable()) +
                    " active=" + item.gameObject.activeInHierarchy + "}";
            }
            if (state == lastResponseDiagnostic) return;
            lastResponseDiagnostic = state;
            TraceClientDiagnostic("client response controls stage=" + stage + " generation=" + displayGeneration + " " + state);
        }
        private static void ChoiceControlClicked(DialogueChoiceUI __instance)
        {
            if (!clientAuthority || inertStep == null) return;
            var ui = DialogueUI.Instance;
            if (ui?._choiceInstances == null) return;
            foreach (var item in ui._choiceInstances)
                if (item != null && item.Pointer == __instance.Pointer)
                {
                    TraceClientDiagnostic("client native control clicked generation=" + displayGeneration + " index=" + item._choiceIndex +
                        " choice=" + (item._choice != null) + " callback=" + (item._callback != null) +
                        " pending=" + choicePending + " currentMatches=" + (ui._currentStepData != null && ui._currentStepData.Pointer == inertStep.Pointer));
                    return;
                }
        }
        private static void SetButtonsEnabled(bool value)
        {
            var ui = DialogueUI.Instance; if (ui == null) return;
            if (ui._choiceInstances != null) foreach (var choice in ui._choiceInstances) if (choice != null)
                choice.Interactable = value && choice._choice != null && choice._choice.Enabled;
            if (ui.fullScreenButton != null) ui.fullScreenButton.interactable = value;
        }
        private static void CloseClientPanel()
        {
            if (inertStep == null) return;
            var ui = DialogueUI.Instance; closing = true;
            try { if (ui != null) ui.Close(); }
            finally { closing = false; ClearPresentationState(); }
        }

        private static void InstallPatches()
        {
            if (patched) return;
            Patch(typeof(DialogueTreeProgressManager), "Update", nameof(AccountDialogueQueuePrefix));
            PatchAccountScope(typeof(DialogueTreeProgressManager), "HandleCurrentDialogueNode", nameof(AccountDialoguePrefix));
            // DialogueEventArgs is a generated native value wrapper. Detouring
            // methods with that input has an unsafe native ABI; sandbox guards
            // use the reference/primitive outer routes instead.
            Patch(typeof(DialogueTreeProgressManager), "GetDialogueStart", nameof(SandboxGetDialogueStart));
            foreach (string name in new[] { "ForcePlayAllNodesOfSpeaker", "ForcePlayAllNodesOfArc", "ForcePlayAllNodesOfConversation", "DebugOnChoicePicked" })
                Patch(typeof(DialogueTreeProgressManager), name, nameof(SandboxNarrativePrefix));
            Patch(typeof(DialogueTreeProgressManager), "OnCurrentStepChoicePicked", nameof(ChoiceAuthority));
            Patch(typeof(DialogueStepDisplayData), "ReportChoicePicked", nameof(ReportChoiceAuthority));
            Patch(typeof(DialogueStepDisplayData), "get_PausesTime", nameof(NoNetworkPause));
            Patch(typeof(DialogueChoiceUI), "OnChoiceSelected", nameof(ChoiceControlClicked));
            Patch(typeof(DialogueUI), "Close", nameof(ClosePrefix));
            Patch(typeof(DialogueUI), "ResumeDialogue", nameof(ResumePrefix));
            Patch(typeof(DialogueUI), "OnDialogueStart", nameof(WorkerDialogueStarting));
            Patch(typeof(DialogueUI), "Show", nameof(WorkerDialogueStarting));
            Patch(typeof(UIWindow), "SwitchRequiresPause", nameof(WorkerDialoguePauseRequest));
            Patch(typeof(DialogueUI), "OnDialogueStep", nameof(ClientUiEvent));
            Patch(typeof(DialogueUI), "_HandleNoChoices_b__59_0", nameof(SandboxNarrativePrefix));
            Patch(typeof(AgentsDialogueManager), "Update", nameof(NarrativeWorkerOnly));
            Patch(typeof(StoryArcDialogueAssigner), "Tick", nameof(WorkerOnly));
            Patch(typeof(StartDialoguePhone), "Update", nameof(WorkerOnly));
            Patch(typeof(CharacterRelationshipManager), "ChangeRelationship", nameof(WorkerOnly));
            foreach (string name in new[] { "Update", "TryToFindActiveDialogue", "CheckIfDialogueStillActive" }) Patch(typeof(StartDialogueInteraction), name, nameof(NarrativeWorkerOnly));
            Patch(typeof(StartDialogueInteraction), "StartDialogue", nameof(SandboxNarrativePrefix));
            Patch(typeof(StartDialogueInteraction), "CanInteract", nameof(CanTalkPrefix));
            Patch(typeof(StartDialogueInteraction), "get_IsActive", nameof(CanTalkPrefix));
            patched = true;
        }
        private static void Patch(Type type, string name, string prefix)
        {
            var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            if (method == null) throw new MissingMethodException(type.FullName, name);
            Harmony.Patch(method, prefix: new HarmonyMethod(typeof(DialogueReplication).GetMethod(prefix, BindingFlags.NonPublic | BindingFlags.Static)));
        }
        private static bool WorkerOnly() => !clientAuthority;
        private static bool NarrativeWorkerOnly() => !MultiplayerSandboxRuntime.Active && !clientAuthority;
        private static bool SandboxNarrativePrefix() => !MultiplayerSandboxRuntime.Active;
        private static bool SandboxGetDialogueStart(ref IDialogue __result)
        {
            if (!MultiplayerSandboxRuntime.Active) return true;
            __result = null; return false;
        }
        private static void PatchAccountScope(Type type, string name, string prefix)
        {
            var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (method == null) throw new MissingMethodException(type.FullName, name);
            Harmony.Patch(method,
                prefix: new HarmonyMethod(typeof(DialogueReplication).GetMethod(prefix, BindingFlags.NonPublic | BindingFlags.Static)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(DialogueReplication).GetMethod(nameof(AccountDialogueFinalizer), BindingFlags.NonPublic | BindingFlags.Static)));
        }
        private static void BindOwner(string playerId, string accountId)
        {
            if (!AccountBinding.Bind(playerId, accountId)) throw new InvalidOperationException("The conversation belongs to another account.");
            owner = playerId;
        }
        private static bool TrySelectAccountOwner(bool allowNewAccount)
        {
            string currentScope = PlayerAccountRuntime.CurrentAccountId;
            if (IsConversationActive && owner.Length != 0 && RecoveryPlayers.TryGetValue(owner, out var pendingAccount) &&
                AccountBinding.Matches(owner, pendingAccount))
                return currentScope.Length == 0 || currentScope == pendingAccount;
            if (owner.Length != 0 && Players.TryGetValue(owner, out var current) && current.Pose.Active &&
                Time.realtimeSinceStartup - current.Time <= 3 && AccountBinding.Matches(owner, current.Pose.AccountId))
                return currentScope.Length == 0 || currentScope == AccountBinding.AccountId;
            if (AccountBinding.AccountId.Length == 0 && !allowNewAccount) return false;
            // Stable ordering makes automatic phone ownership reproducible, while
            // an existing synchronous account scope can select only its own actor.
            var candidates = new List<string>(Players.Keys); candidates.Sort(StringComparer.Ordinal);
            foreach (var id in candidates)
            {
                var candidate = Players[id];
                if (!candidate.Pose.Active || Time.realtimeSinceStartup - candidate.Time > 3 || Dismissed.Contains(id) ||
                    !AccountBinding.CanBind(candidate.Pose.AccountId) ||
                    (currentScope.Length != 0 && currentScope != candidate.Pose.AccountId)) continue;
                if (targetId.Length != 0)
                {
                    var ghost = GhostManager.Instance.GetGhostById(targetId);
                    try { if (ghost == null) continue; RequireNearby(candidate.Pose, ghost.SceneIndex, ghost.Position); }
                    catch { continue; }
                }
                BindOwner(id, candidate.Pose.AccountId); lastActivity = Time.realtimeSinceStartup; nextCapture = 0; return true;
            }
            return false;
        }
        private static bool AccountDialoguePrefix(DialogueTreeProgressManager __instance, out IDisposable __state)
        {
            __state = null;
            if (MultiplayerSandboxRuntime.Active) return false;
            if (clientAuthority) return false;
            if (!DedicatedWorldRuntime.IsDedicatedServer) return true;
            if (!DedicatedWorldRuntime.IsWorldReady || PlayerAccountRuntime.FailClosed) return false;
            try
            {
                if (!TrySelectAccountOwner(!__instance.IsInDialogue)) return false;
                __state = PlayerAccountRuntime.WithAccount(AccountBinding.AccountId); return true;
            }
            catch (Exception ex) { PlayerAccountRuntime.Fault("Dialogue account scope failed: " + ex.GetType().Name); return false; }
        }
        private static bool AccountDialogueQueuePrefix(DialogueTreeProgressManager __instance)
        {
            if (MultiplayerSandboxRuntime.Active) return false;
            if (clientAuthority) return false;
            if (!DedicatedWorldRuntime.IsDedicatedServer) return true;
            if (!DedicatedWorldRuntime.IsWorldReady || PlayerAccountRuntime.FailClosed || __instance.IsInDialogue ||
                __instance._phoneDialogueQueue == null || __instance._phoneDialogueQueue.Count == 0) return false;
            // Native Update (0x9C0390) only checks cooldown/UI, dequeues one
            // request, and calls ProcessDialogueRequestNow. That call installs
            // the scope before GetDialogueStart or any recursive node action.
            // Do not swap the account inventory on each idle cooldown frame.
            try { return TrySelectAccountOwner(true); }
            catch (Exception ex) { PlayerAccountRuntime.Fault("Dialogue queue ownership failed: " + ex.GetType().Name); return false; }
        }
        private static Exception AccountDialogueFinalizer(Exception __exception, IDisposable __state)
        {
            if (__state == null) return __exception;
            if (__exception != null) PlayerAccountRuntime.Fault("Native dialogue failed inside its account scope.");
            try { __state.Dispose(); }
            catch (Exception ex) { PlayerAccountRuntime.Fault("Dialogue account scope could not be restored."); return __exception ?? ex; }
            return __exception;
        }
        private static bool HasLoadedPlayer()
        {
            foreach (var player in Players.Values)
                if (player.Pose.Active && Time.realtimeSinceStartup - player.Time <= 3) return true;
            return false;
        }
        private static bool DialogueRequestPrefix(DialogueEventArgs __0, out IDisposable __state)
        {
            __state = null;
            if (clientAuthority) return false;
            if (!DedicatedWorldRuntime.IsDedicatedServer) return true;
            if (DedicatedWorldRuntime.IsWorldReady && !PlayerAccountRuntime.FailClosed)
            {
                try
                {
                    if (TrySelectAccountOwner(!IsConversationActive))
                    { __state = PlayerAccountRuntime.WithAccount(AccountBinding.AccountId); return true; }
                }
                catch (Exception ex) { PlayerAccountRuntime.Fault("Dialogue request account scope failed: " + ex.GetType().Name); return false; }
            }
            // The first player needs a checkpoint before it can send an accepted
            // pose. Starting a blocking phone/UI conversation before that point
            // can acquire a native save lock and deadlock the load barrier.
            // Keep the request in the game's own persisted queue; do not execute
            // story nodes or put the only copy in a managed, unsaved queue.
            var manager = DialogueTreeProgressManager.Instance;
            if (manager == null || __0?.DialogueTree == null) return false;
            if (manager._phoneDialogueQueue == null) manager._phoneDialogueQueue = new Il2CppSystem.Collections.Generic.Queue<DialogueEventArgs>();
            foreach (var queued in manager._phoneDialogueQueue)
                if (queued.DialogueTree?.Guid == __0.DialogueTree.Guid && queued.Character?.Guid == __0.Character?.Guid && queued.prevDialGUID == __0.prevDialGUID) return false;
            NativeValueList.Enqueue(manager._phoneDialogueQueue, __0);
            return false;
        }
        private static bool UiDialogueRequestPrefix()
        {
            if (clientAuthority) return rendering || closing;
            return !DedicatedWorldRuntime.IsDedicatedServer || HasLoadedPlayer();
        }
        private static bool WorkerDialogueStarting(DialogueUI __instance)
        {
            if (MultiplayerSandboxRuntime.Active) return false;
            if(clientAuthority||!DedicatedWorldRuntime.IsDedicatedServer)return true;
            __instance.pauseTimeWhenOpen=false;__instance.pauseTimeCompletely=false;
            return true;
        }
        private static void WorkerDialoguePauseRequest(UIWindow __instance,ref bool __0,ref bool __1)
        {
            if(clientAuthority||!DedicatedWorldRuntime.IsDedicatedServer)return;
            var ui=__instance.TryCast<DialogueUI>();if(ui==null)return;
            // AnimateToChoices requests (true,true) regardless of PausesTime.
            // The worker's invisible story UI must not stop world simulation.
            __0=false;__1=false;ui.pauseTimeWhenOpen=false;ui.pauseTimeCompletely=false;
        }
        private static bool ClientUiEvent() => !MultiplayerSandboxRuntime.Active && (!clientAuthority || rendering || closing);
        private static bool ChoiceAuthority() => !MultiplayerSandboxRuntime.Active && !clientAuthority && (!DedicatedWorldRuntime.IsDedicatedServer || executingChoice);
        private static bool ReportChoiceAuthority(DialogueStepDisplayData __instance)
        {
            if (MultiplayerSandboxRuntime.Active) return false;
            if (clientAuthority)
            {
                bool matches = inertStep != null && __instance.Pointer == inertStep.Pointer;
                Trace("client native report allowed=" + matches + " generation=" + displayGeneration);
                return matches;
            }
            return !DedicatedWorldRuntime.IsDedicatedServer || executingChoice;
        }
        private static bool NoNetworkPause(ref bool __result)
        {
            if (!clientAuthority && !DedicatedWorldRuntime.IsDedicatedServer) return true;
            __result = false; return false;
        }
        private static bool CanTalkPrefix(StartDialogueInteraction __instance, ref bool __result)
        {
            if (!clientAuthority) return true;
            var ghost = __instance.MyAgent?.MyGhost;
            if (ghost == null || !ReplicaPeople.TryGetValue(ghost.Id, out var person)) { __result = false; return false; }
            __result = person.CanTalk; return false;
        }
        private static bool ClosePrefix()
        {
            if (MultiplayerSandboxRuntime.Active) return true;
            if (!clientAuthority || closing || inertStep == null) return true;
            try { if (replica != null) Send("dialogue-close", replica.TargetId, new DialogueIntent { Conversation = replica.Conversation, Step = replica.Step }); }
            catch (Exception ex) { Notice?.Invoke(ex.Message); }
            return false;
        }
        public static void ResumeDialogue()
        {
            if (MultiplayerSandboxRuntime.Active) return;
            if (!clientAuthority || replica == null || string.IsNullOrEmpty(replica.Conversation)) return;
            try { Send("dialogue-resume", replica.TargetId, new DialogueIntent { Conversation = replica.Conversation, Step = replica.Step }); }
            catch (Exception ex) { Notice?.Invoke(ex.Message); }
        }
        private static bool ResumePrefix()
        {
            if (MultiplayerSandboxRuntime.Active) return false;
            if (!clientAuthority || rendering) return true;
            ResumeDialogue(); return false;
        }
    }
}
