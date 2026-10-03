using System;
using System.Collections.Generic;

namespace DG.Tweening {
    public sealed class Sequence { public bool Killed; public bool Completed; }
    public static class TweenExtensions { public static void Kill(Sequence s, bool complete) { s.Killed = true; s.Completed = complete; } }
}
namespace NivalisTogether.Dedicated {
    public static class MultiplayerSandboxRuntime { public static bool Active; }
    public static class DedicatedWorldRuntime { public static bool IsDedicatedServer; }
    public interface IDialogue { }
    public sealed class Node : IDialogue { }
    public sealed class DialogueStepDisplayData { public IntPtr Pointer = (IntPtr)42; public Action<int> OnChoicePicked; }
    public sealed class Choice { public Action<int> _callback; public bool Interactable = true; }
    public sealed class Button { public bool interactable = true; }
    public sealed class Lock { public bool Released; public void Release() { Released = true; } }
    public sealed class DialogueUI {
        public static DialogueUI Instance;
        public IntPtr Pointer;
        public bool IsOpen, pauseTimeWhenOpen = true, pauseTimeCompletely = true;
        public object _printTextRoutine, _timeoutRoutine, _timeoutWaitForPlayerRoutine;
        public Lock _preventMenuShortcutsLock, _preventSavingLock;
        public DialogueStepDisplayData _currentStepData;
        public DG.Tweening.Sequence _choiceAnimationSequence;
        public List<Choice> _choiceInstances = new List<Choice>();
        public Button fullScreenButton = new Button();
        public int Closes;
        public List<object> Stopped = new List<object>();
        public void StopCoroutine(object handle) { Stopped.Add(handle); }
        public void Close() {
            ++Closes; IsOpen = false;
            _preventMenuShortcutsLock?.Release(); _preventSavingLock?.Release();
            _preventMenuShortcutsLock = _preventSavingLock = null;
        }
    }
    public sealed class DialogueTreeProgressManager {
        public static DialogueTreeProgressManager Instance;
        public static bool InstanceNotNull() => Instance != null;
        public IntPtr Pointer;
        public IDialogue _currentDialogueTree, _currentDialogueNode, _currentDebugDialogueTree;
        public DialogueStepDisplayData _stepDisplayData, _debugStepDisplayData;
        public Action _debugCallbackOnDone;
        public List<IDialogue> _debugDialogueWithChoicesList = new List<IDialogue>();
        public object InactiveNullablePayload = new object();
        public bool ForceFault;
        public bool IsInDialogue => ForceFault || _currentDialogueTree != null;
    }
    public sealed class Binding { public string AccountId = "account"; public void Clear() { AccountId = ""; } }
    public sealed class Gate { public int Resets; public void Reset() { ++Resets; } }
    public static class Actual {
        private static bool sandboxManagedCleared, executingChoice, closing, choicePending, clientAuthority, rendering;
        private static IntPtr sandboxManager, sandboxUi, lastNativeStep, displayedUi;
        private static string owner = "owner", targetId = "target", conversation = "conversation", lastNativeNode = "node", pendingRecoveryRequest = "request", pendingChoiceRequest = "choice";
        private static long pendingRecoverySequence = 15, stepRevision = 7, displayGeneration, displayedStep = 7;
        private static bool recoveryAwaitingState = true, recoveryRetryAfterDenial = true;
        private static float nextRecovery = 9;
        private static object replica = new object(), recoveryIssued = new object(), recoveryReplica = new object(), displayedReplica = new object(), shownSpeaker = new object();
        private static DialogueStepDisplayData inertStep = new DialogueStepDisplayData();
        private static Action<int> choiceCallback, continueCallback;
        private static Choice continueControl;
        private static readonly Binding AccountBinding = new Binding();
        private static readonly Gate RecoveryStepGate = new Gate();
        private static readonly Dictionary<string, object> Results = new Dictionary<string, object>(), RecoveryPlayers = new Dictionary<string, object>(), RecoveryResults = new Dictionary<string, object>(), RecoveryRequestBindings = new Dictionary<string, object>();
        private static readonly Queue<string> ResultOrder = new Queue<string>(), RecoveryResultOrder = new Queue<string>();
        private static readonly HashSet<string> Dismissed = new HashSet<string>();
        private static readonly Dictionary<IntPtr, object> ResponseBindings = new Dictionary<IntPtr, object>();
        private static readonly Dictionary<string, object> ReplicaPeople = new Dictionary<string, object>(), Players = new Dictionary<string, object>();
        private static int patches, resumes;
        private static void InstallPatches() { ++patches; }
        private static void Trace(string ignored) { }
        private static void ResumeDialogue() { ++resumes; }
        // ACTUAL_METHODS
        private static int checks;
        private static void Check(bool ok, string text) { ++checks; if (!ok) throw new Exception(text); }
        public static int Main() {
            int callbacks = 0; var inactive = new object();
            var step = new DialogueStepDisplayData { OnChoicePicked = _ => ++callbacks };
            var debugStep = new DialogueStepDisplayData { OnChoicePicked = _ => ++callbacks };
            var manager = new DialogueTreeProgressManager { Pointer = (IntPtr)10, _currentDialogueTree = new Node(), _currentDialogueNode = new Node(),
                _currentDebugDialogueTree = new Node(), _stepDisplayData = step, _debugStepDisplayData = debugStep,
                _debugCallbackOnDone = () => ++callbacks, InactiveNullablePayload = inactive };
            manager._debugDialogueWithChoicesList.Add(new Node()); DialogueTreeProgressManager.Instance = manager;
            var menuLock = new Lock(); var saveLock = new Lock(); var unrelatedLock = new Lock();
            var tween = new DG.Tweening.Sequence(); var print = new object(); var timeout = new object(); var wait = new object();
            var ui = new DialogueUI { Pointer = (IntPtr)20, IsOpen = true, _currentStepData = step, _printTextRoutine = print,
                _timeoutRoutine = timeout, _timeoutWaitForPlayerRoutine = wait, _preventMenuShortcutsLock = menuLock,
                _preventSavingLock = saveLock, _choiceAnimationSequence = tween };
            var choice = new Choice { _callback = _ => ++callbacks }; ui._choiceInstances.Add(choice); DialogueUI.Instance = ui;
            ReplicaPeople["person"] = new object(); Players["player"] = new object(); Results["old"] = new object();
            RecoveryPlayers["pending"] = new object(); ResponseBindings[(IntPtr)1] = new object();
            MultiplayerSandboxRuntime.Active = false; ApplySandboxPolicy();
            Check(manager.IsInDialogue && ui.IsOpen && ui.Closes == 0 && patches == 0, "solo cleanup must be inert");
            Check(SandboxNarrativePrefix() && NarrativeWorkerOnly(), "solo narrative remains native");
            IDialogue result = new Node(); Check(SandboxGetDialogueStart(ref result) && result != null, "solo tree return untouched");
            Check(WorkerDialogueStarting(ui), "solo UI original allowed");
            MultiplayerSandboxRuntime.Active = true; ApplySandboxPolicy();
            Check(!manager.IsInDialogue && manager._currentDialogueNode == null, "actual native tree and node cleared");
            Check(manager._stepDisplayData == null && manager._debugStepDisplayData == null, "step references cleared");
            Check(manager._debugCallbackOnDone == null && manager._debugDialogueWithChoicesList.Count == 0, "debug continuation detached without completion");
            Check(ReferenceEquals(inactive, manager.InactiveNullablePayload), "inactive native nullable payload not read/copied");
            Check(step.OnChoicePicked == null && debugStep.OnChoicePicked == null && callbacks == 0, "callbacks removed without rewards");
            Check(ui.Stopped.Count == 3 && ui.Stopped.Contains(print) && ui.Stopped.Contains(timeout) && ui.Stopped.Contains(wait), "only exact three UI timers stopped");
            Check(ui._printTextRoutine == null && ui._timeoutRoutine == null && ui._timeoutWaitForPlayerRoutine == null, "timer handles cleared");
            Check(tween.Killed && !tween.Completed && ui._choiceAnimationSequence == null, "own tween canceled without completion");
            Check(ui._currentStepData == null && choice._callback == null, "native displayed callbacks detached");
            Check(ui.Closes == 1 && !ui.IsOpen && menuLock.Released && saveLock.Released, "native Close releases own handles");
            Check(!unrelatedLock.Released, "unrelated global pause/input lock preserved");
            Check(choice.Interactable && ui.fullScreenButton.interactable, "button interactability preserved for later solo reuse");
            Check(owner == "" && conversation == "" && AccountBinding.AccountId == "", "account and conversation ownership cleared");
            Check(replica == null && recoveryIssued == null && recoveryReplica == null && pendingRecoveryRequest == "", "replica/recovery presentations cleared");
            Check(Results.Count == 0 && RecoveryPlayers.Count == 0 && ResponseBindings.Count == 0, "stale result/recovery callbacks removed");
            Check(ReplicaPeople.Count == 1 && Players.Count == 1, "relationship/player replicas preserved");
            Check(!recoveryAwaitingState && !recoveryRetryAfterDenial && pendingRecoverySequence == 0 && stepRevision == 0, "pending narrative state reset");
            long generation = displayGeneration; ApplySandboxPolicy();
            Check(ui.Closes == 1 && ui.Stopped.Count == 3 && displayGeneration == generation && RecoveryStepGate.Resets == 1, "clean repeated Tick does not rebuild/close UI");
            Check(!SandboxNarrativePrefix() && !NarrativeWorkerOnly(), "native campaign entry blocked in sandbox");
            Check(!SandboxGetDialogueStart(ref result) && result == null, "native tree evaluation blocked before nodes");
            Check(!WorkerDialogueStarting(ui) && !ClientUiEvent(), "native UI starts/steps blocked");
            DedicatedWorldRuntime.IsDedicatedServer = true; executingChoice = true;
            Check(!ChoiceAuthority() && !ReportChoiceAuthority(step), "even authorized delayed choices cannot execute campaign");
            Check(WorkerOnly(), "ordinary worker relationship method remains enabled");
            Check(!ResumePrefix() && resumes == 0, "resume stays disabled with no network send");
            var late = new DialogueStepDisplayData { OnChoicePicked = _ => ++callbacks };
            manager._currentDialogueTree = new Node(); manager._stepDisplayData = late; ui._currentStepData = late;
            ApplySandboxPolicy(); Check(!manager.IsInDialogue && late.OnChoicePicked == null && ui.Closes == 2, "late native residue also removed");
            MultiplayerSandboxRuntime.Active = false;
            manager._currentDialogueTree = new Node(); ui.IsOpen = true; ApplySandboxPolicy();
            Check(manager.IsInDialogue && ui.IsOpen && ui.Closes == 2, "return to solo no cleanup side effects");
            Check(SandboxNarrativePrefix() && SandboxGetDialogueStart(ref result) && ResumePrefix(), "solo campaign entry resumes original calls");
            MultiplayerSandboxRuntime.Active = true; manager.ForceFault = true;
            bool threw = false; try { ApplySandboxPolicy(); } catch (InvalidOperationException) { threw = true; }
            Check(threw, "failed native cleanup is reported rather than masked");
            Console.WriteLine("PASS: " + checks + " actual-production sandbox dialogue checks"); return 0;
        }
    }
}
