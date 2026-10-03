// Consumed by Test-SleepIdleContinuation.ps1: production methods are inserted at
// the marker below. This fixture is intentionally not a native runtime test.
using System;
using System.Collections.Generic;
using System.IO;

namespace NivalisTogether.Dedicated
{
    internal sealed class NativeSleepStage
    {
        internal string Phase = "idle", Text = "";
        internal bool Busy;
        internal float Money, Debt;
        internal float[] Percentages = Array.Empty<float>();
    }
    internal static class NativeSleepFlowSupport
    {
        internal static NativeSleepStage Stage;
        internal static bool IsRunning, CompletionObserved, ThrowNext;
        internal static int NextCalls, RankingCalls, AuthorizedTicks, BeginCalls, EndCalls;
        internal static NativeSleepStage Capture() => Stage;
        internal static void InvokeNext() { NextCalls++; if (ThrowNext) throw new InvalidOperationException("native callback rejected"); }
        internal static void InvokeRankingContinue() { RankingCalls++; }
        internal static void TickAuthorizedSleep(bool accepted) { if (!accepted) throw new InvalidOperationException(); AuthorizedTicks++; }
        internal static void BeginTracking() { BeginCalls++; }
        internal static void EndTracking() { EndCalls++; }
        internal static void Reset()
        {
            Stage = new NativeSleepStage(); IsRunning = CompletionObserved = ThrowNext = false;
            NextCalls = RankingCalls = AuthorizedTicks = BeginCalls = EndCalls = 0;
        }
    }
    internal static class DedicatedWorldRuntime { internal static bool IsWorldReady = true; internal static string Scene = "Meridian"; }
    internal static class DayTransitionController { internal static bool Transitioning; }
    internal static class TimeOfDayManager { internal static double TotalGameSeconds = 352921; }
    internal static class CurfewManager { internal static bool IsCurfewInEffect = true; }
    internal sealed class GroupSleepChoice { public string Action = "", Label = ""; public float Percentage; }
    internal sealed class GroupSleepEvent
    {
        public string Epoch = "", Phase = "", Owner = "", Scene = "", Title = "", Text = "";
        public long Step;
        public bool RecoveryOwner;
        public GroupSleepChoice[] Choices = Array.Empty<GroupSleepChoice>();
    }
    internal sealed class MembershipStub { internal int ParticipantCount, ClearCalls; internal void ClearReadiness() { ClearCalls++; } }

    public static class SleepReplication
    {
        private static bool enabled, terminal, worker, authorizedSleep, choicePending, waitingForPlayers, awaitingWakeSynchronization;
        private static bool Authority, MapOperationsPending;
        private static string epoch = "", owner = "", bedId = "", scene = "", stageSignature = "", ownerWaitDiagnostic = "", preferredSleeper = "";
        private static long step;
        private static double now, nextCapture, stageSince, transitionDeadline;
        private static GroupSleepEvent current, replica;
        private static Action<string> notice, fail;
        private static readonly HashSet<string> present = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> recoveryPresent = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, object> readyBeds = new Dictionary<string, object>();
        private static readonly MembershipStub consensus = new MembershipStub();
        private static string[] sleepCohort = Array.Empty<string>();
        private static readonly List<GroupSleepEvent> published = new List<GroupSleepEvent>();
        private static readonly List<string> notices = new List<string>(), failures = new List<string>();
        private static int checks, chooseOwnerCalls, waitingCalls, replicaCalls;

        // Ownership selection, transport, consent collection, and native stage
        // capture are outside this fixture's scope. No body pose is supplied,
        // so ChooseOwner deliberately leaves ownership unavailable.
        private static void ChooseOwner() { chooseOwnerCalls++; owner = ""; }
        private static void Publish(GroupSleepEvent value) { published.Add(value); }
        private static void TickWaiting() { waitingCalls++; }
        private static void ShowReplica() { replicaCalls++; }

        /* EXTRACTED_SLEEP_METHODS */

        private static void Check(bool condition, string name)
        {
            checks++; if (!condition) throw new InvalidDataException("Sleep idle continuation regression: " + name);
        }
        private static void Reset(string phase = "summary", bool busy = true)
        {
            enabled = worker = Authority = DedicatedWorldRuntime.IsWorldReady = true;
            terminal = authorizedSleep = choicePending = waitingForPlayers = awaitingWakeSynchronization = MapOperationsPending = false;
            epoch = owner = bedId = scene = stageSignature = ownerWaitDiagnostic = preferredSleeper = "";
            step = 0; now = nextCapture = stageSince = transitionDeadline = 0;
            current = replica = null;
            present.Clear(); recoveryPresent.Clear(); readyBeds.Clear(); published.Clear(); notices.Clear(); failures.Clear();
            consensus.ParticipantCount = consensus.ClearCalls = 0; sleepCohort = Array.Empty<string>();
            chooseOwnerCalls = waitingCalls = replicaCalls = 0;
            notice = value => notices.Add(value); fail = value => failures.Add(value);
            NativeSleepFlowSupport.Reset(); SetStage(phase, busy);
            DayTransitionController.Transitioning = false; DedicatedWorldRuntime.Scene = "Meridian";
            TimeOfDayManager.TotalGameSeconds = 352921; CurfewManager.IsCurfewInEffect = true;
        }
        private static void SetStage(string phase, bool busy = true)
        {
            NativeSleepFlowSupport.Stage = new NativeSleepStage { Phase = phase, Busy = busy, Percentages = phase == "debt" ? new float[] { 0, 25, 100 } : Array.Empty<float>() };
        }
        private static void CheckNoNativeContinuation(string label)
        {
            Check(NativeSleepFlowSupport.NextCalls == 0 && NativeSleepFlowSupport.RankingCalls == 0 && NativeSleepFlowSupport.AuthorizedTicks == 0, label);
        }
        public static int RunRegressionTests()
        {
            checks = 0;

            // Reproduce the unattended curfew report at the observed world time.
            Reset(); Tick(10);
            Check(IsBusy && epoch.Length > 0 && !authorizedSleep, "natural report is tracked without authorizing a bed");
            CheckNoNativeContinuation("summary waits for its one-second settling period");
            Check(notices.Count == 0, "zero-player informational report does not demand an impossible owner");
            Tick(10.999); Check(NativeSleepFlowSupport.NextCalls == 0, "summary cannot continue before one second");
            Tick(11); Check(NativeSleepFlowSupport.NextCalls == 1 && choicePending, "empty-server summary invokes native next once");
            Check(transitionDeadline == 131, "automatic native callback retains the existing 120-second watchdog");
            Tick(11.5); Tick(12); Check(NativeSleepFlowSupport.NextCalls == 1, "pending summary callback cannot be duplicated");
            SetStage("ranking"); Tick(13);
            Check(!choicePending && NativeSleepFlowSupport.RankingCalls == 0, "new report phase resets pending state and settling period");
            Tick(14); Tick(15); Check(NativeSleepFlowSupport.RankingCalls == 1 && choicePending, "empty-server ranking invokes native continuation once");
            SetStage("idle", false); Tick(16);
            Check(!IsBusy && epoch.Length == 0 && !choicePending && !authorizedSleep, "native idle after informational reports clears sleep busy state");
            Check(current.Phase == "complete" && !awaitingWakeSynchronization, "natural report completes without inventing a morning synchronization");
            Check(NativeSleepFlowSupport.BeginCalls == 0 && NativeSleepFlowSupport.AuthorizedTicks == 0 && NativeSleepFlowSupport.EndCalls == 1, "natural report never starts bed tracking or native sleep progression");
            Check(TimeOfDayManager.TotalGameSeconds == 352921 && CurfewManager.IsCurfewInEffect, "informational continuation does not write native time or curfew");
            Check(consensus.ClearCalls == 1, "completion clears stale readiness");
            Tick(17); Check(NativeSleepFlowSupport.NextCalls == 1 && NativeSleepFlowSupport.RankingCalls == 1 && NativeSleepFlowSupport.EndCalls == 1, "subsequent idle ticks cannot restart the completed report");

            Reset(); NativeSleepFlowSupport.Stage.Text = "Daily summary page one"; Tick(0); Tick(1); Tick(2);
            Check(NativeSleepFlowSupport.NextCalls == 1, "unchanged first summary page is acknowledged only once");
            NativeSleepFlowSupport.Stage.Text = "Daily summary page two"; Tick(3);
            Check(!choicePending && NativeSleepFlowSupport.NextCalls == 1, "changed same-phase summary text creates a fresh native choice");
            Tick(3.999); Check(NativeSleepFlowSupport.NextCalls == 1, "second summary page receives its own settling period");
            Tick(4); Tick(5); Check(NativeSleepFlowSupport.NextCalls == 2, "second summary page is acknowledged exactly once");
            Check(notices.Count == 2 && notices.TrueForAll(value => value.Contains("no sleep requested")), "each unattended continuation records its non-sleep diagnostic once");
            foreach (bool accepted in new[] { false, true }) {
                Check(!SleepReportContinuation.CanContinue("summary", accepted, -1, 0, 0), "negative connected count cannot authorize continuation");
                Check(!SleepReportContinuation.CanContinue("summary", accepted, 0, -1, 0), "negative loaded count cannot authorize continuation");
                Check(!SleepReportContinuation.CanContinue("ranking", accepted, 0, 0, -1), "negative recovery count cannot authorize continuation");
            }

            // Any authenticated membership blocks unattended continuation, even
            // with no active pose and consequently no choice owner available.
            foreach (string phase in new[] { "summary", "ranking" }) {
                Reset(phase); consensus.ParticipantCount = 1; Tick(0); Tick(1); Tick(200);
                CheckNoNativeContinuation(phase + ": connected peer without a pose must explicitly acknowledge");
                Check(IsBusy && !terminal && !choicePending, phase + ": missing player intent remains a wait, not native timeout");
                Check(notices.Count == 1 && notices[0].Contains("explicit"), phase + ": unavailable owner retains one diagnostic");
                Reset(phase); present.Add("loaded-peer"); Tick(0); Tick(2);
                CheckNoNativeContinuation(phase + ": loaded peer presence independently blocks empty-server policy");
                Reset(phase); recoveryPresent.Add("recovering-peer"); Tick(0); Tick(2);
                CheckNoNativeContinuation(phase + ": authenticated pending recovery independently blocks policy");
            }
            Reset(); Tick(0); consensus.ParticipantCount = 1; Tick(2);
            CheckNoNativeContinuation("peer joining during settling period blocks automatic continuation");
            consensus.ParticipantCount = 0; Tick(3);
            Check(NativeSleepFlowSupport.NextCalls == 1, "last connected peer departure releases informational wait");
            Reset("ranking"); present.Add("last-loaded"); recoveryPresent.Add("last-recovery"); Tick(0); Tick(2);
            present.Clear(); Tick(3); CheckNoNativeContinuation("loaded departure alone cannot bypass recovery peer");
            recoveryPresent.Clear(); Tick(4);
            Check(NativeSleepFlowSupport.RankingCalls == 1 && !terminal, "last recovery departure releases existing ranking report");

            foreach (bool accepted in new[] { false, true }) {
                Reset("debt"); if (accepted) { epoch = "accepted-night"; scene = "Meridian"; authorizedSleep = true; consensus.ParticipantCount = 2; }
                Tick(0); Tick(2); Tick(200);
                CheckNoNativeContinuation("debt never auto-selects even zero payment; authorized=" + accepted);
                Check(IsBusy && !choicePending && !terminal, "debt stays explicit without false native timeout; authorized=" + accepted);
                Check(current.Choices.Length == 3 && current.Choices[0].Action == "debt" && current.Choices[0].Percentage == 0, "zero payment remains an offered owner choice");
            }
            foreach (string phase in new[] { "idle", "sleeping", "transition", "newspaper", "supply" }) {
                Reset(phase); Tick(0); Tick(2);
                CheckNoNativeContinuation("unattended policy cannot start or advance native " + phase);
                Check(epoch.Length == 0 && !authorizedSleep, "non-report " + phase + " cannot create an authorized sleep");
            }

            // Accepted unanimous sleep still follows the original callback path
            // and must wait for observed native completion and the wake barrier.
            Reset(); epoch = "accepted-night"; scene = "Meridian"; authorizedSleep = true;
            consensus.ParticipantCount = 2; present.Add("one"); present.Add("two"); sleepCohort = new[] { "one", "two" };
            Tick(0); Tick(1); Tick(2);
            Check(NativeSleepFlowSupport.NextCalls == 1, "authorized sleep summary continues with connected participants");
            Check(notices.Count == 0, "authorized informational continuation does not demand an owner");
            SetStage("ranking"); Tick(3); Tick(4);
            Check(NativeSleepFlowSupport.RankingCalls == 1, "authorized sleep ranking still continues");
            SetStage("idle", false); Tick(5);
            Check(!awaitingWakeSynchronization && IsBusy, "authorized sleep requires actual observed completion");
            NativeSleepFlowSupport.CompletionObserved = true; Tick(6);
            Check(awaitingWakeSynchronization && current.Phase == "synchronizing" && IsBusy, "observed morning enters the existing wake synchronization barrier");
            Check(NativeSleepFlowSupport.EndCalls == 0 && authorizedSleep, "tick cannot release authorized night before session wake acknowledgement");
            int publishedBefore = published.Count; Tick(500);
            Check(published.Count == publishedBefore && failures.Count == 0, "wake barrier does not emit repeats or use native choice timeout");

            Reset(); Tick(10); Tick(11); Tick(131);
            Check(!terminal && NativeSleepFlowSupport.NextCalls == 1, "callback deadline is not considered exceeded at its exact boundary");
            Tick(131.001);
            Check(terminal && failures.Count == 1 && failures[0].Contains("native day choice did not advance"), "unadvanced automatic callback fails through the real watchdog");
            Tick(200); Check(failures.Count == 1 && NativeSleepFlowSupport.NextCalls == 1, "terminal failure prevents subsequent callbacks");
            Reset(); NativeSleepFlowSupport.ThrowNext = true; Tick(0); Tick(1);
            Check(terminal && failures.Count == 1 && failures[0].Contains("native day report could not continue"), "native callback exception uses real fail path");

            Reset(); MapOperationsPending = true; Tick(0); Tick(2);
            CheckNoNativeContinuation("map-operation fence still blocks a new report epoch");
            Check(epoch.Length == 0 && chooseOwnerCalls == 0, "map fence returns before native ownership work");
            Reset(); waitingForPlayers = true; Tick(0);
            Check(waitingCalls == 1 && epoch.Length == 0, "readiness collection remains separate from automatic reports");
            CheckNoNativeContinuation("waiting for unanimity cannot be bypassed by report continuation");
            return checks;
        }
    }
}
