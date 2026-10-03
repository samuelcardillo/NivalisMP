using System;

// The runner inserts the CURRENT production guard/reset method bodies here.
// These fakes replace native endpoints, not the lifecycle logic under test.
internal sealed class UIPanel { internal bool closeWithCancel, closeWithPause; }
internal sealed class OverrideableBool
{
    internal int Acquired, Released;
    internal sealed class OverrideLock
    {
        internal OverrideableBool Parent; internal bool Released;
        public void Release() { if (Released) throw new Exception("Double release"); Released = true; Parent.Released++; }
    }
    internal OverrideLock Override(UIPanel owner) { if (owner == null) throw new Exception("Owner absent"); Acquired++; return new OverrideLock { Parent = this }; }
}
internal sealed class PlayerInputManager
{
    internal static PlayerInputManager Instance;
    internal OverrideableBool PreventAllMenuShortcuts = new OverrideableBool();
    internal static bool InstanceNotNull() => Instance != null;
}
internal sealed class InputField { internal string Text = "secret"; internal void SetTextWithoutNotify(string value) { Text = value; } }
internal sealed class MenuProbe
{
    internal bool accountActive, disposed, isolated, disconnectPending, sessionConnected, refreshPersonalSlots, reopenBrowserOnTitle;
    internal UIPanel browserPanel;
    internal object dialogueRecoveryState;
    internal bool IsOpen;
    private UIPanel accountGuardPanel;
    private OverrideableBool.OverrideLock accountShortcutLock;
    private bool accountOriginalCancel, accountOriginalPause;
    internal string sessionStatus = "connected", Status;
    internal InputField passwordInput = new InputField();
    internal float nextScan, nextSessionScan, nextListUpdate;
    internal int HideCount, ClearCount, RefreshCount;
    private Action<string> log = _ => {};
    internal bool Busy = true;
    internal void Ensure() => AcquireAccountInputGuard();
    internal void Release() => ReleaseAccountInputGuard();
    private void ClearAccountFlow() { ClearCount++; accountActive = false; ReleaseAccountInputGuard(); }
    private void Hide() { HideCount++; }
    private void ClearSleepRecovery() { }
    private void ClearDialogueRecovery() { dialogueRecoveryState = null; ReleaseAccountInputGuard(); }
    private void SetIsolated(bool value) { isolated = value; }
    private void SetBusy(bool value) { Busy = value; }
    private void SetStatus(string value) { Status = value; }
    private void RefreshPersonalSaveButtons() { RefreshCount++; }
    private void UpdateSessionMenus() { }
    // PRODUCTION_METHODS
}
internal static class AccountMenuGuardTests
{
    private static int checks;
    private static void Check(bool okay, string name) { checks++; if (!okay) throw new Exception("FAIL: " + name); }
    public static int Main()
    {
        foreach (bool cancel in new[] { false, true })
        foreach (bool pause in new[] { false, true })
        {
            var native = new PlayerInputManager(); PlayerInputManager.Instance = native;
            var panel = new UIPanel { closeWithCancel = cancel, closeWithPause = pause };
            var menu = new MenuProbe { browserPanel = panel };
            menu.Ensure(); Check(native.PreventAllMenuShortcuts.Acquired == 0, "inactive browser does not acquire native input ownership");
            menu.accountActive = true; menu.Ensure();
            Check(!panel.closeWithCancel && !panel.closeWithPause && native.PreventAllMenuShortcuts.Acquired == 1, "account modal blocks both close callbacks and parent shortcut");
            for (int i = 0; i < 10; i++) menu.Ensure();
            Check(native.PreventAllMenuShortcuts.Acquired == 1 && native.PreventAllMenuShortcuts.Released == 0, "repeated frames/stage changes retain one native owner");
            menu.Release(); menu.Release();
            Check(panel.closeWithCancel == cancel && panel.closeWithPause == pause && native.PreventAllMenuShortcuts.Released == 1, "release restores exact original flags once");
        }
        {
            PlayerInputManager.Instance = null;
            var old = new UIPanel { closeWithCancel = true, closeWithPause = true };
            var menu = new MenuProbe { accountActive = true, browserPanel = old };
            menu.Ensure(); Check(!old.closeWithCancel && !old.closeWithPause, "panel protected before singleton is ready");
            var native = new PlayerInputManager(); PlayerInputManager.Instance = native;
            menu.Ensure(); Check(native.PreventAllMenuShortcuts.Acquired == 1, "delayed singleton gains owner without changing active view");
            var replacement = new UIPanel { closeWithPause = true }; menu.browserPanel = replacement; menu.Ensure();
            Check(old.closeWithCancel && old.closeWithPause && !replacement.closeWithPause, "panel replacement restores old flags and protects new panel");
            Check(native.PreventAllMenuShortcuts.Acquired == 2 && native.PreventAllMenuShortcuts.Released == 1, "panel replacement releases old native owner");
            menu.ResetForTitle("Connection refused. Retry this server.");
            Check(menu.HideCount == 0 && menu.reopenBrowserOnTitle && menu.Status.Contains("refused"), "pre-world failure keeps browser/reason available");
            Check(!menu.accountActive && !menu.Busy && menu.passwordInput.Text == "" && native.PreventAllMenuShortcuts.Released == 2, "failure clears secrets and releases modal ownership");
        }
        {
            var menu = new MenuProbe { isolated = true };
            menu.ResetForTitle("Disconnected.");
            Check(menu.HideCount == 1 && !menu.reopenBrowserOnTitle && !menu.isolated, "loaded-world disconnect returns ordinary title");
            Check(menu.refreshPersonalSlots && menu.RefreshCount == 1, "title reset still refreshes personal save controls");
        }
        {
            var native = new PlayerInputManager(); PlayerInputManager.Instance = native;
            var panel = new UIPanel { closeWithCancel = true, closeWithPause = true };
            var menu = new MenuProbe { browserPanel = panel, dialogueRecoveryState = new object() };
            menu.Ensure(); Check(native.PreventAllMenuShortcuts.Acquired == 0, "hidden recovery does not hold global input ownership");
            menu.IsOpen = true; menu.Ensure();
            Check(native.PreventAllMenuShortcuts.Acquired == 1 && !panel.closeWithCancel && !panel.closeWithPause, "visible recovery protects native modal input");
            menu.IsOpen = false; menu.Release();
            Check(native.PreventAllMenuShortcuts.Released == 1 && menu.dialogueRecoveryState != null, "closing recovery releases guard and retains reopen state");
        }
        Console.WriteLine("PASS: " + checks + " production account-menu guard/reset assertions with fake native endpoints.");
        return 0;
    }
}
