// Production methods are inserted in memory by Test-SleepConfirmation.ps1.
// UI and native adapters below record operations; this is not a Unity test.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace Il2CppSystem
{
    public sealed class Action
    {
        private System.Action callback;
        public static explicit operator Action(System.Action action) => new Action { callback = action };
        public void Invoke() => callback();
    }
}
namespace Il2CppInterop.Runtime.InteropTypes.Arrays
{
    public sealed class Il2CppReferenceArray<T>
    {
        private readonly T[] values;
        public Il2CppReferenceArray(int count) { values = new T[count]; }
        public T this[int index] { get => values[index]; set => values[index] = value; }
    }
    public sealed class Il2CppStringArray
    {
        public readonly string[] Values;
        public Il2CppStringArray(string[] values) { Values = values; }
    }
}
namespace NivalisTogether.Dedicated
{
    public class IInteractable { public T TryCast<T>() where T : class => this as T; }
    public sealed class Bed : IInteractable
    {
        public bool IsActive = true, Allowed = true;
        public string Id = "native-bed";
        public bool CanInteract() => Allowed;
    }
    public sealed class InteractionRequest
    {
        public string Kind = "", PlayerId = "", TargetId = "", RequestId = "";
        public JsonElement Data;
    }
    public sealed class WorldEvent { public string Kind = ""; public JsonElement Data; }
    internal static class DedicatedProtocol { internal static readonly JsonSerializerOptions Json = new JsonSerializerOptions(); }
    internal static class NativeSleepFlowSupport { internal static bool IsRunning; }
    internal static class DayTransitionController { internal static bool Transitioning; }
    internal static class TravelReplication { internal static bool IsBusy; }
    internal sealed class RuntimeBool { internal bool _runtimeValue, _isRuntimeValueInitialized; }
    internal sealed class CurfewManager
    {
        internal static bool IsCurfewInEffect;
        internal static CurfewManager _instance = new CurfewManager();
        internal RuntimeBool isNightMode = new RuntimeBool();
    }
    internal static class TimeWeatherReplication { internal static int RefreshCalls; internal static void RefreshClientClockDisplay() { RefreshCalls++; } }
    internal sealed class TextStub { internal string text = ""; }
    internal sealed class PopupDialog
    {
        internal static PopupDialog _instance = new PopupDialog();
        internal static bool IsPopupVisible;
        internal static int HideCalls, DisplayCalls;
        internal TextStub titleText = new TextStub();
        internal string[] Labels = Array.Empty<string>();
        internal Il2CppReferenceArray<Il2CppSystem.Action> Actions;
        internal static void Display(string title, string text, Il2CppStringArray labels, Il2CppReferenceArray<Il2CppSystem.Action> actions, bool modal)
        { _instance.titleText.text = title; _instance.Labels = labels.Values; _instance.Actions = actions; IsPopupVisible = true; DisplayCalls++; }
        internal void Hide() { IsPopupVisible = false; HideCalls++; }
        // Native onClick invokes the supplied delegate without first hiding
        // the modal. Production must dismiss the popup it owns itself.
        internal void Click(int index) { Actions[index].Invoke(); }
        internal static void ShowForeign() { IsPopupVisible = true; _instance.titleText.text = "Unrelated inventory confirmation"; }
        internal static void Reset() { _instance = new PopupDialog(); IsPopupVisible = false; HideCalls = DisplayCalls = 0; }
    }
    internal static class SleepLoadingOverlay
    {
        internal static bool Visible;
        internal static string Title = "", Detail = "", Identity = "";
        internal static string[] Labels = Array.Empty<string>();
        internal static Action[] Actions = Array.Empty<Action>();
        internal static int Ticks, RetryCalls;
        internal static void Tick() { Ticks++; }
        internal static void Set(string title, string detail, string[] labels = null, Action[] actions = null, string identity = "")
        { Visible = true; Title = title; Detail = detail; Labels = labels ?? Array.Empty<string>(); Actions = actions ?? Array.Empty<Action>(); Identity = identity; }
        internal static void Hide() { Visible = false; Labels = Array.Empty<string>(); Actions = Array.Empty<Action>(); }
        internal static void RetryActions() { RetryCalls++; }
        internal static void Reset() { Hide(); Title = Detail = Identity = ""; Ticks = RetryCalls = 0; }
    }
    public static class SleepReplication
    {
        private const string EventKind = "group-sleep", RequestKind = "group-sleep-request", ActionKind = "group-sleep-action", CancelKind = "group-sleep-cancel";
        private const string PlayerA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", PlayerB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Epoch = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        private static bool configured, enabled, worker, terminal, Authority, popupOwned, waitingForPlayers;
        private static string localPlayer = "", epoch = "", displayed = "", popupTitle = "";
        private static double now, nextRequest;
        private static long lastSequence, popupGeneration;
        private static GroupSleepEvent replica;
        private static Action<InteractionRequest> send;
        private static Action<string> notice;
        private static readonly List<Il2CppSystem.Action> callbacks = new List<Il2CppSystem.Action>();
        private static readonly List<InteractionRequest> sent = new List<InteractionRequest>();
        private static readonly List<string> notices = new List<string>();
        private static int checks, sendsBeforePopupDismissal;
        private static string GetBedId(Bed bed) => bed.Id;

        /* EXTRACTED_SLEEP_METHODS */

        private static void Check(bool result, string name)
        { checks++; if (!result) throw new InvalidDataException("Sleep confirmation regression: " + name); }
        private static void Reset()
        {
            configured = enabled = Authority = true;
            worker = terminal = popupOwned = waitingForPlayers = false;
            localPlayer = PlayerB; epoch = displayed = popupTitle = ""; now = 10; nextRequest = 0; lastSequence = popupGeneration = 0;
            sendsBeforePopupDismissal = 0;
            replica = null; callbacks.Clear(); sent.Clear(); notices.Clear(); send = request => { if (PopupDialog.IsPopupVisible || popupOwned || callbacks.Count > 0) sendsBeforePopupDismissal++; sent.Add(request); }; notice = text => notices.Add(text);
            NativeSleepFlowSupport.IsRunning = DayTransitionController.Transitioning = TravelReplication.IsBusy = false;
            CurfewManager.IsCurfewInEffect = false; CurfewManager._instance = new CurfewManager(); TimeWeatherReplication.RefreshCalls = 0;
            PopupDialog.Reset(); SleepLoadingOverlay.Reset();
        }
        private static GroupSleepEvent State(string phase = "waiting", bool bReady = false)
        {
            return new GroupSleepEvent { Sequence = lastSequence + 1, Epoch = Epoch, Step = 1, Phase = phase, Owner = PlayerA,
                Scene = "Meridian", Title = "Shared sleep", Text = bReady ? "2 of 2 players ready" : "1 of 2 players ready", Curfew = true,
                NightTransition = phase != "waiting" && phase != "cancelled", Participants = new[] { PlayerA, PlayerB }, ReadyPlayers = bReady ? new[] { PlayerA, PlayerB } : new[] { PlayerA } };
        }
        private static void Receive(GroupSleepEvent state)
        {
            Apply(new WorldEvent { Kind = EventKind, Data = JsonSerializer.SerializeToElement(state, DedicatedProtocol.Json) }, now + 1);
        }
        private static void OpenBed()
        {
            Check(RouteClientInteraction(new Bed()) == false, "production bed route intercepts native interaction");
            Check(PopupDialog.IsPopupVisible && OwnsPopup() && callbacks.Count == 2, "production bed route opens and owns confirmation callbacks");
            Check(PopupDialog._instance.Labels[0] == "Sleep together" && PopupDialog._instance.Labels[1] == "Cancel", "production confirmation contains real choices");
        }
        public static int RunRegressionTests()
        {
            checks = 0;
            Reset(); Receive(State());
            Check(!IsBusy && !IsLocallyReady && !SleepLoadingOverlay.Visible, "A ready does not lock unready B's interactions");
            OpenBed(); var callback = callbacks[0]; int hideCalls = PopupDialog.HideCalls, noticeCount = notices.Count;
            for (int frame = 0; frame < 8; frame++) ShowReplica();
            Check(PopupDialog.IsPopupVisible && OwnsPopup(), "second player confirmation survives repeated waiting frames");
            Check(callbacks.Count == 2 && ReferenceEquals(callback, callbacks[0]) && PopupDialog.HideCalls == hideCalls, "waiting frames preserve callback identity and popup ownership");
            Check(sent.Count == 0 && notices.Count == noticeCount && !SleepLoadingOverlay.Visible, "waiting frames neither submit readiness nor replace B dialog");
            Receive(State()); Check(PopupDialog.IsPopupVisible && OwnsPopup() && ReferenceEquals(callback, callbacks[0]), "fresh waiting broadcast preserves unready B confirmation");
            PopupDialog._instance.Click(0);
            Check(!PopupDialog.IsPopupVisible && !popupOwned && callbacks.Count == 0, "confirm callback closes owned popup before authoritative readiness");
            Check(sent.Count == 1 && sent[0].Kind == RequestKind && sent[0].PlayerId == PlayerB && sent[0].TargetId == "native-bed", "real confirmation callback sends B readiness once through production Send");
            Check(sendsBeforePopupDismissal == 0, "confirmation dismisses owned native modal before sending readiness");
            Check(Guid.TryParseExact(sent[0].RequestId, "N", out _) && sent[0].Data.Deserialize<GroupSleepIntent>(DedicatedProtocol.Json).Action == "", "readiness uses valid production request and intent serialization");
            now += 1; callback.Invoke();
            Check(sent.Count == 1, "retained confirmation callback cannot submit a second readiness after dismissal");
            for (int frame = 0; frame < 8; frame++) ShowReplica();
            Check(sent.Count == 1 && !IsLocallyReady && !PopupDialog.IsPopupVisible && !SleepLoadingOverlay.Visible, "confirmation waits for authoritative readiness without duplicate send");
            Receive(State("waiting", true));
            Check(IsLocallyReady && SleepLoadingOverlay.Visible && SleepLoadingOverlay.Title == "Waiting for everyone", "B readiness acknowledgement shows waiting overlay");
            Check(!PopupDialog.IsPopupVisible && !popupOwned && callbacks.Count == 0, "acknowledged readiness clears local confirmation ownership and callbacks");
            Check(SleepLoadingOverlay.Labels.Length == 1 && SleepLoadingOverlay.Labels[0] == "Cancel sleep", "acknowledged readiness offers cancellation");
            SleepLoadingOverlay.Actions[0]();
            Check(sent.Count == 2 && sent[1].Kind == CancelKind && sent[1].TargetId == Epoch, "waiting overlay invokes production CancelLocalReadiness");
            var cancel = sent[1].Data.Deserialize<GroupSleepIntent>(DedicatedProtocol.Json);
            Check(cancel.Action == "cancel" && cancel.Epoch == Epoch && cancel.Step == replica.Step, "cancel binds authoritative epoch and step");
            Check(IsLocallyReady && SleepLoadingOverlay.Visible, "cancel request awaits authoritative response");
            Receive(State("waiting", false)); Check(!IsLocallyReady && !SleepLoadingOverlay.Visible, "readiness withdrawal releases waiting overlay");
            OpenBed(); ShowReplica(); Check(PopupDialog.IsPopupVisible && OwnsPopup(), "withdrawn player can open and retain a new confirmation");

            Reset(); Receive(State()); OpenBed(); Receive(State("waiting", true));
            Check(!PopupDialog.IsPopupVisible && !OwnsPopup() && callbacks.Count == 0 && PopupDialog.HideCalls == 1, "ready broadcast dismisses a still-owned unclicked prompt exactly once");
            ShowReplica(); Check(PopupDialog.HideCalls == 1, "ready frame cannot repeatedly hide native popup");

            foreach (string phase in new[] { "sleeping", "synchronizing", "transition", "summary" }) {
                Reset(); Receive(State()); OpenBed(); Receive(State(phase, true));
                Check(!PopupDialog.IsPopupVisible && !popupOwned && callbacks.Count == 0, phase + " dismisses owned confirmation on phase change");
                Check(SleepLoadingOverlay.Visible && SleepLoadingOverlay.Labels.Length == 0, phase + " presents blocking loading without stale confirmation actions");
                Check(sent.Count == 0, phase + " cannot execute dismissed confirmation callback");
                if (phase == "synchronizing") Check(SleepLoadingOverlay.Title == "Loading morning", "synchronization keeps morning loading visible");
            }
            foreach (string phase in new[] { "cancelled", "complete" }) {
                Reset(); Receive(State()); OpenBed(); Receive(State(phase));
                Check(!PopupDialog.IsPopupVisible && callbacks.Count == 0 && !popupOwned && !SleepLoadingOverlay.Visible && displayed == "", phase + " Apply clears owned confirmation and overlay");
                Check(notices[notices.Count - 1] == replica.Text && !IsBusy, phase + " returns to ordinary nonbusy state with notice");
                Reset(); Receive(State("waiting", true)); Receive(State(phase));
                Check(!SleepLoadingOverlay.Visible && SleepLoadingOverlay.Actions.Length == 0, phase + " releases existing waiting cancel action");
            }

            foreach (string phase in new[] { "waiting", "sleeping", "synchronizing", "cancelled", "complete" }) {
                Reset(); Receive(State()); OpenBed(); PopupDialog.ShowForeign(); int calls = PopupDialog.HideCalls;
                Receive(State(phase)); ShowReplica();
                Check(PopupDialog.IsPopupVisible && PopupDialog._instance.titleText.text == "Unrelated inventory confirmation" && PopupDialog.HideCalls == calls, phase + " never hides foreign replacement popup");
            }
            Reset(); Receive(State()); PopupDialog.ShowForeign();
            RouteClientInteraction(new Bed());
            Check(PopupDialog.IsPopupVisible && PopupDialog.DisplayCalls == 0 && sent.Count == 0 && !popupOwned, "bed route cannot replace another subsystem's existing popup");
            Check(notices.Count > 0 && notices[notices.Count - 1].Contains("Close the current dialog"), "foreign dialog rejection explains how to retry");

            Reset(); Receive(State()); OpenBed(); PopupDialog._instance.Click(1); ShowReplica();
            Check(sent.Count == 0 && !PopupDialog.IsPopupVisible && !IsLocallyReady, "native Cancel choice sends no readiness and stays unready");
            Check(!popupOwned && callbacks.Count == 0, "cancel callback clears popup ownership and callbacks");
            Reset(); Receive(State()); OpenBed(); var replaced = callbacks[0]; OpenBed(); var replacement = callbacks[0];
            int replacementHideCalls = PopupDialog.HideCalls; replaced.Invoke();
            Check(sent.Count == 0 && OwnsPopup() && PopupDialog.IsPopupVisible && ReferenceEquals(replacement, callbacks[0]) && PopupDialog.HideCalls == replacementHideCalls, "replaced same-title callback cannot submit or dismiss the replacement confirmation");
            replacement.Invoke(); Check(sent.Count == 1 && !PopupDialog.IsPopupVisible, "current replacement callback still confirms normally");
            Reset(); Receive(State()); OpenBed(); var foreignReplaced = callbacks[0]; PopupDialog.ShowForeign(); foreignReplaced.Invoke();
            Check(sent.Count == 0 && PopupDialog.IsPopupVisible && PopupDialog.HideCalls == 0, "retained callback cannot submit or dismiss a foreign replacement popup");
            Reset(); Receive(State()); OpenBed(); var cancelledCallback = callbacks[0]; Receive(State("cancelled")); now += 1; cancelledCallback.Invoke();
            Check(sent.Count == 0 && !PopupDialog.IsPopupVisible, "cancelled sleep invalidates retained old confirmation callbacks");
            Reset(); Receive(State()); OpenBed(); var denied = State("denied"); denied.TargetPlayer = PlayerB;
            Receive(denied); ShowReplica();
            Check(PopupDialog.IsPopupVisible && OwnsPopup() && SleepLoadingOverlay.RetryCalls == 1, "denied response does not destroy unready confirmation while waiting");
            return checks;
        }
    }
}
