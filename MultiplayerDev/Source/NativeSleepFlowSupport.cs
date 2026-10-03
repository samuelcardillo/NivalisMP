using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Nivalis;
using Nivalis.UI;
using Nivalis.UI.EndOfDay;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace NivalisTogether.Dedicated
{
    public sealed class NativeSleepStage
    {
        public string Phase { get; set; } = "idle";
        public string Text { get; set; } = "";
        // Native percent values: 25 means 25 percent, not a fraction.
        public float[] Percentages { get; set; } = Array.Empty<float>();
        public int Money { get; set; } // Native minor units.
        public int Debt { get; set; } // Native whole units.
        public bool Busy { get; set; }
    }

    /// <summary>Unity main-thread native worker continuation. Caller validates actor,
    /// request epoch, deduplication and explicit financial choices. Never writes time.</summary>
    public static class NativeSleepFlowSupport
    {
        private static bool tracking, observedSleep, completed;
        private static IntPtr baselinePause;
        private static int expectedWakeSeconds;
        private const int MaximumTextCharacters = 3000;
        private static SupplyTrendWindow supply;
        private static readonly Regex Markup = new Regex("<[^>]*>", RegexOptions.CultureInvariant);
        public static bool IsTracking => tracking;
        public static bool HasObservedSleep { get { ObserveSleep(); return observedSleep; } }
        public static bool CompletionObserved { get { ObserveSleep(); return completed; } }
        public static bool IsRunning => ObserveSleep();

        public static void BeginTracking()
        {
            RequireWorker();
            var manager = RequireSleepManager();
            if (RunningLocks(manager))
                throw new InvalidOperationException("A native sleep sequence is already running.");
            baselinePause = manager._timePauseLock == null ? IntPtr.Zero : manager._timePauseLock.Pointer;
            int seconds = TimeOfDayManager.TotalGameSeconds;
            if (seconds < 0 || seconds > int.MaxValue - 115200)
                throw new InvalidOperationException("The native sleep clock is outside its supported range.");
            expectedWakeSeconds = seconds - seconds % 86400 + (TimeOfDayManager.ClockHour > 8 ? 86400 : 0) + 28800;
            tracking = true;
            observedSleep = completed = false;
            supply = null;
        }
        public static void EndTracking()
        {
            // Forget managed tracking only. Losing an owner does not cancel native sleep.
            tracking = observedSleep = completed = false;
            baselinePause = IntPtr.Zero;
            expectedWakeSeconds = 0;
            supply = null;
        }
        public static void Reset() => EndTracking();

        public static NativeSleepStage Capture()
        {
            RequireWorker();
            bool running = ObserveSleep();
            var state = new NativeSleepStage { Money = CurrentMoney(), Busy = running || DayTransitionController.Transitioning };
            var day = DayController();
            var summary = day == null ? null : day.endOfDaySummary;
            if (summary != null && summary.IsOpen)
            {
                var debt = summary.debtSection;
                if (debt != null) state.Debt = debt.DebtValue;
                if (debt != null && debt.IsVisible)
                {
                    state.Phase = "debt";
                    state.Text = "Legacy world debt is paid from the preserved shared assets.\n" + PanelText(debt);
                    state.Percentages = DebtPercentages(debt);
                }
                else { state.Phase = "summary"; state.Text = PanelText(summary, debt); }
                return state;
            }
            var ranking = day == null ? null : day.rankingListWindow;
            if (ranking != null && ranking.IsOpen)
            {
                state.Phase = "ranking"; state.Text = PanelText(ranking); return state;
            }
            if (tracking && observedSleep && running)
            {
                var window = OpenSupplyWindow();
                if (window != null) { state.Phase = "supply"; state.Text = PanelText(window); return state; }
                var paper = Hands()?.Newspaper;
                if (paper != null && (paper.Equipped || paper.Visible))
                { state.Phase = "newspaper"; state.Text = PanelText(paper); return state; }
            }
            if (running) state.Phase = "sleeping";
            else if (DayTransitionController.Transitioning) state.Phase = "transition";
            else if (tracking && completed) state.Phase = "complete";
            else if (CurfewManager.IsCurfewInEffect) state.Phase = "curfew";
            return state;
        }

        public static void InvokeNext()
        {
            RequireWorker(); RequirePhase("summary");
            var summary = RequireSummary();
            if (summary.nextButton == null || !summary.nextButton.interactable)
                throw new InvalidOperationException("The native summary is not ready to continue.");
            // Scripted world debt belongs to the preserved pre-account assets.
            // Native Next decides whether to offer payment from this wallet.
            using (PlayerAccountRuntime.WithAccount(AccountProtocol.LegacyOwnerId))
                summary.NextButtonClickedListener();
        }
        public static void InvokeRankingContinue()
        {
            RequireWorker(); RequirePhase("ranking");
            var ranking = DayController()?.rankingListWindow;
            if (ranking == null || !ranking.IsOpen || ranking.okButton == null || !ranking.okButton.interactable)
                throw new InvalidOperationException("The native ranking summary is not ready to continue.");
            ranking.Close(); // The exact callback bound to okButton by native Awake.
        }
        public static void InvokeDebtChoice(float percentage)
        {
            RequireWorker(); RequirePhase("debt");
            if (!Finite(percentage) || percentage < 0 || percentage > 100)
                throw new InvalidOperationException("Invalid debt payment percentage.");
            var debt = RequireSummary().debtSection;
            var pair = FindDebtChoice(debt, percentage);
            if (ReferenceEquals(pair, null)) throw new InvalidOperationException("That debt payment choice is not available.");
            // Native ChoicePicked uses the balance cached by Show. Refresh the
            // preserved legacy wallet before its native debt payment callback.
            debt.Show(CurrentMoney());
            pair = FindDebtChoice(debt, percentage);
            if (ReferenceEquals(pair, null)) throw new InvalidOperationException("That debt payment choice is no longer available.");
            pair.toggle.isOn = true;
            debt.SetPercentage(pair.percentage);
            if (debt.moveOnButton == null || !debt.moveOnButton.interactable || Math.Abs(debt._currentPercentage - pair.percentage) > 0.0001f)
                throw new InvalidOperationException("The native debt choice is not ready to submit.");
            debt.MoveOnButtonClicked(); // Applies payment and invokes normal summary callback once.
        }

        /// <summary>Caller first publishes this informational phase for at least one second.
        /// Automatic handling is confined to an explicitly requested, observed native sleep.</summary>
        public static bool TickAuthorizedSleep(bool authorized = true)
        {
            RequireWorker();
            if (!authorized || !tracking || !ObserveSleep() || !observedSleep) return false;
            string phase = Capture().Phase;
            if (phase == "newspaper")
            {
                var hands = Hands();
                var paper = hands?.Newspaper;
                if (paper == null || !paper.Equipped || !paper.FullyVisible) return false;
                hands.TryUnequipNewspaper();
                return true;
            }
            if (phase == "supply")
            {
                var window = OpenSupplyWindow();
                if (window == null || !window.IsOpen) return false;
                window.Close(); return true;
            }
            return false;
        }
        private static bool ObserveSleep()
        {
            if (!DedicatedWorldRuntime.IsDedicatedServer || !SleepManager.InstanceNotNull()) return false;
            var manager = SleepManager.Instance;
            bool pause = Active(manager._timePauseLock);
            bool running = RunningLocks(manager);
            if (tracking && pause && manager._timePauseLock.Pointer != baselinePause) observedSleep = true;
            // Native sleep can release its locks while the morning supply report
            // remains open and UIManager still pauses the world. Keep that exact
            // sleep presentation in the tracked flow so its normal Close action
            // runs before publishing completion or allowing another checkpoint.
            if (tracking && observedSleep && OpenSupplyWindow() != null) running = true;
            completed = tracking && observedSleep && !running && !DayTransitionController.Transitioning &&
                TimeOfDayManager.TotalGameSeconds >= expectedWakeSeconds;
            return running;
        }
        // Native PlayerLock dereferences two nullable fields and returns their release
        // flags; it is neither null-safe nor a reliable "sleep is running" predicate.
        private static bool RunningLocks(SleepManager manager) =>
            Active(manager._timePauseLock) || Active(manager._playerInteractionLock) ||
            Active(manager._playerLookLock) || Active(manager._movementLock) ||
            Active(manager._cameraLock) || Active(manager._preventOpeningMenuLock);
        private static bool Active(OverrideableBool.OverrideLock value) => value != null && !value.IsReleased;
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static DayTransitionController DayController() => DayTransitionController.InstanceNotNull() ? DayTransitionController.Instance : null;
        private static SleepManager RequireSleepManager() => SleepManager.InstanceNotNull() ? SleepManager.Instance : throw new InvalidOperationException("The native sleep manager is not loaded.");
        private static EndOfDayWindow RequireSummary() => DayController()?.endOfDaySummary ?? throw new InvalidOperationException("The native daily summary is not loaded.");
        private static void RequireWorker()
        {
            if (!DedicatedWorldRuntime.IsDedicatedServer || !DedicatedWorldRuntime.IsWorldReady)
                throw new InvalidOperationException("Native sleep flow requires the loaded authoritative worker.");
        }
        private static void RequirePhase(string expected)
        {
            if (Capture().Phase != expected) throw new InvalidOperationException("The daily transition has moved to another step.");
        }
        private static int CurrentMoney()
        {
            if (!PlayerManager.InstanceNotNull() || PlayerManager.Instance.LocalPlayer?.Inventory == null)
                throw new InvalidOperationException("The native shared player inventory is not loaded.");
            return PlayerAccountRuntime.LegacyEscrowMoney;
        }
        private static PlayerHandsAnimator Hands() => PlayerManager.InstanceNotNull() ? PlayerManager.Instance.LocalPlayer?.Character?.Controller?.handsAnimator : null;
        private static SupplyTrendWindow OpenSupplyWindow()
        {
            if (supply != null && supply.IsOpen) return supply;
            supply = null;
            var windows = Object.FindObjectsOfType<SupplyTrendWindow>(true);
            for (int i = 0; i < windows.Length; i++)
                if (windows[i] != null && windows[i].IsOpen) { supply = windows[i]; break; }
            return supply;
        }
        private static float[] DebtPercentages(EndOfDayDebtSection debt)
        {
            var choices = new List<float>();
            var buttons = debt.buttons;
            if (buttons == null) return choices.ToArray();
            if (buttons.Length > 64) throw new InvalidOperationException("Too many native debt choices.");
            for (int i = 0; i < buttons.Length; i++)
            {
                var pair = buttons[i];
                if (ReferenceEquals(pair, null) || pair.toggle == null || !pair.toggle.interactable || !pair.toggle.gameObject.activeInHierarchy) continue;
                float value = pair.percentage;
                if (Finite(value) && value >= 0 && value <= 100 && !choices.Contains(value)) choices.Add(value);
            }
            if (choices.Count > 16) throw new InvalidOperationException("Too many available native debt choices for the shared dialog.");
            return choices.ToArray();
        }
        private static EndOfDayDebtSection.DebtRelieveButtonPair FindDebtChoice(EndOfDayDebtSection debt, float percentage)
        {
            var buttons = debt.buttons;
            if (buttons == null || buttons.Length > 64) return null;
            for (int i = 0; i < buttons.Length; i++)
            {
                var pair = buttons[i];
                if (!ReferenceEquals(pair, null) && pair.toggle != null && pair.toggle.interactable && pair.toggle.gameObject.activeInHierarchy && Finite(pair.percentage) && Math.Abs(pair.percentage - percentage) <= 0.0001f) return pair;
            }
            return null;
        }
        private static string PanelText(Component component, Component excluded = null)
        {
            if (component == null) return "";
            var text = new StringBuilder();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var labels = component.GetComponentsInChildren<TMP_Text>(false);
            for (int i = 0; i < labels.Length && i < 512 && text.Length < MaximumTextCharacters; i++)
            {
                var label = labels[i];
                if (label == null || !label.gameObject.activeInHierarchy) continue;
                if (excluded != null && label.transform.IsChildOf(excluded.transform)) continue;
                string value = Markup.Replace(label.text ?? "", "").Trim();
                if (value.Length == 0 || !seen.Add(value)) continue;
                if (text.Length != 0) text.Append('\n');
                int room = MaximumTextCharacters - text.Length;
                text.Append(value.Length <= room ? value : value.Substring(0, room));
            }
            return text.ToString();
        }
    }
}
