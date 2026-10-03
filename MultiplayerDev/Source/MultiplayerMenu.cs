using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Nivalis;
using NivalisTogether.Dedicated;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace NivalisTogether
{
    // Shared across session/menu instances. Native lifecycle hooks discover
    // dynamically created menus; one scene-boundary seed also finds menus whose
    // inactive GameObjects have never received Start. No periodic world scans.
    internal static class NativeMainMenuRegistry
    {
        private static readonly Harmony Patches = new Harmony("local.nivalis.together.menu-registry");
        private static readonly Dictionary<IntPtr, MainMenuUI> Menus = new Dictionary<IntPtr, MainMenuUI>();
        private static readonly UnityAction<Scene, LoadSceneMode> Loaded = (UnityAction<Scene, LoadSceneMode>)(Action<Scene, LoadSceneMode>)SceneLoaded;
        private static readonly UnityAction<Scene> Unloaded = (UnityAction<Scene>)(Action<Scene>)SceneUnloaded;
        private static bool initialized, seedPending = true;
        internal static long Revision { get; private set; }
        internal static void Initialize()
        {
            if (initialized) return;
            try
            {
                Patch("Start", nameof(Observe), false);
                Patch("Show", nameof(Observe), false);
                Patch("OnDestroy", nameof(Forget), true);
                SceneManager.sceneLoaded += Loaded;
                SceneManager.sceneUnloaded += Unloaded;
                initialized = true; seedPending = true; Revision++;
            }
            catch
            {
                SceneManager.sceneLoaded -= Loaded; SceneManager.sceneUnloaded -= Unloaded;
                Patches.UnpatchSelf(); Menus.Clear(); throw;
            }
        }
        private static void Patch(string name, string hook, bool prefix)
        {
            var target = typeof(MainMenuUI).GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
            if (target == null || target.ReturnType != typeof(void)) throw new MissingMethodException(typeof(MainMenuUI).FullName, name);
            var patch = new HarmonyMethod(typeof(NativeMainMenuRegistry).GetMethod(hook, BindingFlags.Static | BindingFlags.NonPublic));
            Patches.Patch(target, prefix: prefix ? patch : null, postfix: prefix ? null : patch);
        }
        private static void SceneLoaded(Scene scene, LoadSceneMode mode) { seedPending = true; Revision++; }
        private static void SceneUnloaded(Scene scene) { Revision++; }
        private static void Observe(MainMenuUI __instance)
        {
            if (__instance == null) return;
            IntPtr identity = __instance.Pointer;
            if (!Menus.ContainsKey(identity)) Revision++;
            Menus[identity] = __instance;
        }
        private static void Forget(MainMenuUI __instance)
        {
            if (!ReferenceEquals(__instance, null) && Menus.Remove(__instance.Pointer)) Revision++;
        }
        internal static MainMenuUI[] Snapshot()
        {
            Initialize();
            if (seedPending)
            {
                // Leave pending set if discovery throws, so the existing title
                // attachment retry can recover rather than losing inactive UI.
                foreach (var menu in Object.FindObjectsOfType<MainMenuUI>(true)) Observe(menu);
                seedPending = false;
            }
            var result = new List<MainMenuUI>(); var dead = new List<IntPtr>();
            foreach (var entry in Menus)
            {
                var menu = entry.Value;
                if (menu == null) { dead.Add(entry.Key); continue; }
                var scene = menu.gameObject.scene;
                if (!scene.IsValid() || !scene.isLoaded) { dead.Add(entry.Key); continue; }
                result.Add(menu);
            }
            foreach (var identity in dead) if (Menus.Remove(identity)) Revision++;
            return result.ToArray();
        }
    }

    // Detached presentation state. The session validates/authenticates the wire;
    // this additional fence prevents stale native clicks and receipt replay.
    internal sealed class DialogueRecoveryPresentation
    {
        internal DialogueRecoveryState State { get; private set; }
        internal string LocalPlayerId { get; private set; } = "";
        internal string Notice { get; private set; } = "";
        internal string PendingRequestId { get; private set; } = "";
        internal bool Submitted { get; private set; }
        internal long Generation { get; private set; }
        private long sequence, submittedSequence;
        private bool retryAfterState;

        internal bool Apply(DialogueRecoveryState update, string localPlayerId)
        {
            if (update == null || string.IsNullOrEmpty(localPlayerId) || update.TargetPlayerId != localPlayerId || update.Sequence <= sequence) return false;
            DialogueRecoveryProtocol.ValidateWire(update);
            if (update.Phase != "state" && (State == null || update.Conversation != State.Conversation ||
                update.OwnerAccountId != State.OwnerAccountId || update.TargetId != State.TargetId ||
                (update.Phase != "complete" && update.Step != State.Step))) return false;
            if (update.Phase == "state")
            {
                bool sameStep = State != null && State.Conversation == update.Conversation && State.Step == update.Step && State.OwnerAccountId == update.OwnerAccountId;
                if (!sameStep || retryAfterState) { Submitted = false; PendingRequestId = ""; }
                retryAfterState = false;
                var choices = new List<DialogueRecoveryChoice>();
                foreach (var item in update.Choices ?? Array.Empty<DialogueRecoveryChoice>())
                    choices.Add(new DialogueRecoveryChoice { Id = item.Id, Text = item.Text, Enabled = item.Enabled, Continue = false });
                State = new DialogueRecoveryState { Sequence = update.Sequence, Conversation = update.Conversation, Step = update.Step,
                    Phase = update.Phase, OwnerAccountId = update.OwnerAccountId, TargetPlayerId = update.TargetPlayerId, TargetId = update.TargetId,
                    SpeakerName = update.SpeakerName, Text = update.Text, RequestId = update.RequestId, Message = update.Message,
                    CanContinue = update.CanContinue, Choices = choices.ToArray() };
                LocalPlayerId = localPlayerId;
                Notice = Submitted ? "Choice sent. Waiting for the server..." : update.Message ?? "";
            }
            else if (update.Phase == "complete")
            {
                State = null; Submitted = false; PendingRequestId = ""; retryAfterState = false; Notice = "";
            }
            else if (update.Phase == "accepted" || update.Phase == "denied")
            {
                if (!Submitted || PendingRequestId.Length == 0 || update.RequestId != PendingRequestId) return false;
                retryAfterState = update.Phase == "denied";
                PendingRequestId = "";
                Notice = update.Message ?? (retryAfterState ? "Waiting for refreshed choices..." : "Choice accepted. Waiting for the next line...");
            }
            else return false;
            sequence = update.Sequence; Generation++; return true;
        }

        internal bool TrySubmit(long generation, DialogueRecoveryChoice offered, out DialogueRecoveryChoice selected)
        {
            selected = null;
            if (State == null || generation != Generation || Submitted || State.TargetPlayerId != LocalPlayerId || offered == null) return false;
            if (offered.Continue)
            {
                if (!State.CanContinue || State.Choices.Length != 0) return false;
                selected = new DialogueRecoveryChoice { Continue = true, Enabled = true, Id = "", Text = "Continue" };
            }
            else
            {
                foreach (var item in State.Choices)
                    if (item.Id == offered.Id && item.Enabled) { selected = new DialogueRecoveryChoice { Id = item.Id, Text = item.Text, Enabled = true }; break; }
                if (selected == null) return false;
            }
            Submitted = true; submittedSequence = State.Sequence; PendingRequestId = "";
            Notice = "Choice sent. Waiting for the server..."; return true;
        }

        internal bool ApplyRejection(DialogueRecoveryRejection rejection)
        {
            // A service rejection is correlated to the originally submitted
            // sequence, which can precede the most recently rendered heartbeat.
            if (rejection == null || State == null || !Submitted || PendingRequestId.Length == 0 ||
                rejection.RequestId != PendingRequestId || rejection.Sequence != submittedSequence ||
                rejection.Conversation != State.Conversation || rejection.Step != State.Step ||
                rejection.OwnerAccountId != State.OwnerAccountId || rejection.TargetPlayerId != LocalPlayerId ||
                rejection.TargetPlayerId != State.TargetPlayerId) return false;
            DialogueRecoveryProtocol.ValidateRejection(rejection);
            Notice = rejection.Message ?? "Waiting for a fresh server update...";
            if (rejection.Retryable)
            {
                PendingRequestId = "";
                // Do not reuse the heartbeat that was already displayed before
                // this rejection. Apply requires a subsequent higher sequence.
                retryAfterState = true;
            }
            Generation++; return true;
        }

        internal void SetPendingRequest(string requestId)
        {
            if (Submitted && State != null && Guid.TryParseExact(requestId, "N", out _)) PendingRequestId = requestId;
        }
        internal bool ReleaseUnsent(long generation)
        {
            // Only the synchronous send callback's exception path calls this.
            // A returned/recorded request ID or intervening network update makes
            // failure ambiguous, so those cases remain fenced.
            if (State == null || generation != Generation || !Submitted || PendingRequestId.Length != 0 ||
                submittedSequence != State.Sequence) return false;
            Submitted = retryAfterState = false; submittedSequence = 0;
            Notice = "The response was not sent. Please choose again.";
            Generation++; return true;
        }
        internal void SetNotice(string message) { Notice = message ?? ""; }
        internal void Clear()
        { State = null; LocalPlayerId = Notice = PendingRequestId = ""; sequence = submittedSequence = 0; Submitted = retryAfterState = false; Generation++; }
    }

    /// <summary>A saved, discovered, or manually entered server; independent of local save slots.</summary>
    public sealed class MultiplayerServerEntry
    {
        public string Id = "";
        public string Name = "";
        public string Host = "127.0.0.1";
        public int Port = 47778;
        public int Players;
        public int Capacity;
        public int PingMilliseconds = -1;
        public bool Online;
        public bool Saved;
        public bool PasswordRequired;
        public string Description = "";
        // All endpoint IDs represented by this display row. Each bookmark stays
        // independently saved/removable; server names are never identities.
        public string[] AliasIds = Array.Empty<string>();
    }

    public sealed class MultiplayerJoinRequest
    {
        public MultiplayerServerEntry Server;
        public string PlayerName = "Explorer";
        public string Password = "";
    }

    /// <summary>
    /// Native title-menu integration. Construct once; call Tick() on Unity's main thread from
    /// the driver's Update(), and Dispose() on shutdown. Every callback runs on that thread.
    /// listServers returns current discovery/bookmark data; refresh starts discovery; join
    /// receives the selected endpoint and credentials. saveServer/removeServer own persistence.
    /// SetStatus and SetBusy can be called from the driver's Update, never a networking thread.
    /// No method reads or selects a local game save. No additional EventSystem is created.
    /// </summary>
    public sealed class MultiplayerMenu : IDisposable, IAccountClientView
    {
        private const int RowsPerPage = 5;
        private readonly Func<IReadOnlyList<MultiplayerServerEntry>> listServers;
        private readonly Action refresh;
        private readonly Action<MultiplayerJoinRequest> join;
        private readonly Action<MultiplayerServerEntry> saveServer;
        private readonly Action<string> removeServer;
        private readonly Action closed;
        private readonly Action<string> log;
        private readonly Action disconnect;
        private readonly List<MultiplayerServerEntry> entries = new List<MultiplayerServerEntry>();
        private readonly List<Button> rowButtons = new List<Button>();
        private readonly List<TextMeshProUGUI> rowLabels = new List<TextMeshProUGUI>();
        private readonly List<ShiftedButton> shiftedButtons = new List<ShiftedButton>();
        private readonly List<SessionMenu> sessionMenus = new List<SessionMenu>();
        private readonly List<DisabledButton> disabledButtons = new List<DisabledButton>();
        private MainMenuUI mainMenu;
        private Button multiplayerButton, buttonTemplate, joinButton, saveButton, removeButton;
        private Button refreshButton, previousButton, nextButton;
        private TMP_Text styleText;
        private GameObject browserRoot;
        private GameObject browserFrame, sleepRecoveryFrame;
        private UIPanel browserPanel;
        private GroupSleepEvent sleepRecoveryState;
        private string sleepRecoveryPlayer = "";
        private Action<GroupSleepChoice> sleepRecoveryChoose;
        private readonly List<Button> sleepRecoveryButtons = new List<Button>();
        private TextMeshProUGUI sleepRecoveryTitle, sleepRecoveryProgress, sleepRecoverySummary;
        private RectTransform sleepRecoverySummaryContent, sleepRecoveryChoicesContent;
        private ScrollRect sleepRecoverySummaryScroll, sleepRecoveryChoicesScroll;
        private Button sleepRecoveryBack, sleepRecoveryDisconnect;
        private bool sleepRecoverySubmitted, sleepRecoveryAutoOpen;
        private long sleepRecoveryGeneration;
        private string sleepRecoveryPresentation = "";
        private TextMeshProUGUI statusText, pageText, selectionText;
        private TMP_InputField addressInput, portInput, passwordInput, nameInput;
        private MultiplayerServerEntry selected;
        private string selectedId = "", playerName = "Explorer", status = "Choose a server or enter its address.";
        private string lastListSignature = "";
        private bool open, busy, disposed, hiding, isolated, sessionConnected, disconnectPending, refreshPersonalSlots;
        private string sessionStatus = "";
        private int page;
        private float nextScan, nextListUpdate, nextSessionScan;
        private long menuRegistryRevision = -1;
        private GameObject accountFrame;
        private bool accountActive, accountBusy;
        private OverrideableBool.OverrideLock accountShortcutLock;
        private UIPanel accountGuardPanel;
        private bool accountOriginalCancel, accountOriginalPause;
        private string accountEndpoint = "";
        private bool reopenBrowserOnTitle;
        private readonly DialogueRecoveryPresentation dialogueRecovery = new DialogueRecoveryPresentation();
        private DialogueRecoveryState dialogueRecoveryState => dialogueRecovery.State;
        private GameObject dialogueRecoveryFrame;
        private TextMeshProUGUI dialogueRecoverySpeaker, dialogueRecoveryText, dialogueRecoveryProgress;
        private RectTransform dialogueRecoveryTextContent, dialogueRecoveryChoicesContent;
        private ScrollRect dialogueRecoveryTextScroll, dialogueRecoveryChoicesScroll;
        private Button dialogueRecoveryBack, dialogueRecoveryDisconnect;
        private readonly List<Button> dialogueRecoveryButtons = new List<Button>();
        private Action<DialogueRecoveryChoice> dialogueRecoveryChoose;
        private bool dialogueRecoveryAutoOpen;
        private string dialogueRecoveryPresentation = "";
        private long accountGeneration;
        private Action pendingAccountRender, accountCancel;
        private string accountUsername = "", accountNotice = "";
        private TextMeshProUGUI accountNoticeText;
        private TMP_InputField accountUsernameInput, accountPasswordInput, accountConfirmInput, accountRoomInput;
        private readonly Dictionary<Button, bool> accountControls = new Dictionary<Button, bool>();
        private readonly Color panelColor = new Color(.025f, .045f, .06f, .98f);
        private readonly Color fieldColor = new Color(.07f, .10f, .12f, 1f);
        private readonly Color accent = new Color(.46f, .84f, .85f, 1f);
        private sealed class ShiftedButton { public RectTransform Rect; public Vector2 Position; }
        private sealed class DisabledButton { public Button Button; public bool Interactable; }
        private sealed class SessionMenu
        {
            public MainMenuUI Menu;
            public Button Disconnect;
            public Button Original;
            public Navigation OriginalNavigation;
            public Selectable Following;
            public Navigation FollowingNavigation;
            public TextMeshProUGUI Status;
            public readonly List<ShiftedButton> Shifted = new List<ShiftedButton>();
        }

        public MultiplayerMenu(
            Func<IReadOnlyList<MultiplayerServerEntry>> listServers,
            Action refresh,
            Action<MultiplayerJoinRequest> join,
            Action<MultiplayerServerEntry> saveServer,
            Action<string> removeServer,
            Action closed,
            Action<string> log = null,
            Action disconnect = null)
        {
            this.listServers = listServers;
            this.refresh = refresh;
            this.join = join;
            this.saveServer = saveServer;
            this.removeServer = removeServer;
            this.closed = closed;
            this.log = log;
            this.disconnect = disconnect;
        }

        public bool IsOpen { get { return open && browserRoot != null && browserPanel != null && browserPanel.IsVisible; } }

        public void Tick()
        {
            if (disposed) return;
            NativeMainMenuRegistry.Initialize();
            if (menuRegistryRevision != NativeMainMenuRegistry.Revision)
            {
                menuRegistryRevision = NativeMainMenuRegistry.Revision;
                nextScan = nextSessionScan = 0;
            }
            UpdateSessionMenus();
            if (mainMenu == null || multiplayerButton == null)
            {
                // Objects from the previous scene are Unity-null. Retry after each scene change.
                if (Time.unscaledTime < nextScan) return;
                nextScan = Time.unscaledTime + 1f;
                ClearStaleReferences();
                foreach (var candidate in NativeMainMenuRegistry.Snapshot())
                {
                    if (candidate != null && candidate.title && candidate.loadButton != null)
                    {
                        try { Attach(candidate); }
                        catch (Exception ex) { log?.Invoke("Multiplayer menu attachment: " + ex.Message); }
                        break;
                    }
                }
            }
            if (pendingAccountRender != null && mainMenu != null && multiplayerButton != null)
            { var render = pendingAccountRender; pendingAccountRender = null; render(); }
            if (accountActive || (dialogueRecoveryState != null && IsOpen)) AcquireAccountInputGuard();
            if (reopenBrowserOnTitle && mainMenu != null && multiplayerButton != null)
            { reopenBrowserOnTitle = false; Show(); }
            if (open && browserRoot == null) { open = false; closed?.Invoke(); }
            if (dialogueRecoveryState != null && dialogueRecoveryAutoOpen && mainMenu != null && mainMenu.IsVisible &&
                (IsOpen || !mainMenu.HasChildrenPanelOpen))
            {
                dialogueRecoveryAutoOpen = false;
                Show();
            }
            if (sleepRecoveryState != null && sleepRecoveryAutoOpen && mainMenu != null && mainMenu.IsVisible &&
                (IsOpen || !mainMenu.HasChildrenPanelOpen))
            {
                sleepRecoveryAutoOpen = false;
                if (!IsOpen) Show();
            }
            if (IsOpen && Time.unscaledTime >= nextListUpdate)
            {
                nextListUpdate = Time.unscaledTime + .5f;
                if (sleepRecoveryState == null && dialogueRecoveryState == null && !accountActive) RefreshRows(false);
            }
        }

        /// <summary>Displays a server-validated shared-day choice while the player is still at the title.
        /// This view uses only the title UIPanel lifecycle and never touches gameplay UI singletons.</summary>
        public void SetSleepRecovery(GroupSleepEvent state, string localPlayerId, Action<GroupSleepChoice> choose)
        {
            if (disposed) return;
            if (state == null || state.Phase == "complete") { ClearSleepRecovery(); return; }
            // Repeated delivery must not restore a button already submitted. The caller validates
            // wire order; this additional guard keeps stale callbacks out of the native UI too.
            if (sleepRecoveryState != null && state.Sequence <= sleepRecoveryState.Sequence) return;
            bool first = sleepRecoveryState == null;
            var choices = new List<GroupSleepChoice>();
            if (state.Choices != null)
                foreach (var choice in state.Choices)
                {
                    if (choice == null || choices.Count == 16) break;
                    choices.Add(new GroupSleepChoice { Action = choice.Action, Label = Limit(choice.Label, 120), Percentage = choice.Percentage });
                }
            sleepRecoveryState = new GroupSleepEvent
            {
                Sequence = state.Sequence, Epoch = state.Epoch, Step = state.Step, Phase = state.Phase,
                Owner = state.Owner, RecoveryOwner = state.RecoveryOwner,
                Title = Limit(state.Title, 160), Text = Limit(state.Text, 3000), Choices = choices.ToArray()
            };
            sleepRecoveryPlayer = localPlayerId ?? "";
            sleepRecoveryChoose = choose;
            sleepRecoverySubmitted = false;
            sleepRecoveryGeneration++;
            if (first) sleepRecoveryAutoOpen = true;
            if (browserRoot != null && dialogueRecoveryState == null && !accountActive) RenderSleepRecovery();
        }

        public void ClearSleepRecovery()
        {
            sleepRecoveryState = null;
            sleepRecoveryPlayer = "";
            sleepRecoveryChoose = null;
            sleepRecoverySubmitted = false;
            sleepRecoveryAutoOpen = false;
            sleepRecoveryGeneration++;
            sleepRecoveryPresentation = "";
            DestroySleepRecoveryButtons();
            if (sleepRecoveryFrame != null) sleepRecoveryFrame.SetActive(false);
            if (browserFrame != null) browserFrame.SetActive(dialogueRecoveryState == null && !accountActive);
            if (browserPanel != null && refreshButton != null) browserPanel.firstSelected = refreshButton.gameObject;
            if (IsOpen && dialogueRecoveryState == null && !accountActive)
            {
                RefreshRows(true);
                SetStatus(status);
                SetBusy(busy);
                if (refreshButton != null && EventSystem.current != null)
                    EventSystem.current.SetSelectedGameObject(refreshButton.gameObject);
            }
        }
        public void SetSleepRecoveryNotice(string value)
        {
            if(sleepRecoveryState!=null&&sleepRecoveryProgress!=null)
                sleepRecoveryProgress.text=Limit(value,256);
        }

        /// <summary>Private, server-authorized conversation recovery before world loading.
        /// The callback must capture the supplied state and validate it in the session adapter.</summary>
        public void SetDialogueRecovery(DialogueRecoveryState state, string localPlayerId, Action<DialogueRecoveryChoice> choose)
        {
            if (disposed) return;
            if (state == null) { ClearDialogueRecovery(); return; }
            bool first = dialogueRecoveryState == null;
            if (!dialogueRecovery.Apply(state, localPlayerId)) return;
            if (state.Phase == "state") dialogueRecoveryChoose = choose;
            if (dialogueRecoveryState == null) { ClearDialogueRecoveryView(); return; }
            if (first) dialogueRecoveryAutoOpen = true;
            if (IsOpen && !accountActive) RenderDialogueRecovery();
        }
        public void SetDialogueRecoveryPendingRequest(string requestId) => dialogueRecovery.SetPendingRequest(requestId);
        public void ApplyDialogueRecoveryRejection(DialogueRecoveryRejection rejection)
        {
            if (disposed || !dialogueRecovery.ApplyRejection(rejection)) return;
            UpdateDialogueRecoveryProgress();
        }
        public void SetDialogueRecoveryNotice(string message)
        {
            dialogueRecovery.SetNotice(Limit(message, 512));
            UpdateDialogueRecoveryProgress();
        }
        public void ClearDialogueRecovery()
        {
            dialogueRecovery.Clear(); dialogueRecoveryChoose = null;
            ClearDialogueRecoveryView();
        }
        private void ClearDialogueRecoveryView()
        {
            dialogueRecoveryAutoOpen = false; dialogueRecoveryPresentation = "";
            DestroyDialogueRecoveryButtons();
            if (dialogueRecoveryFrame != null) dialogueRecoveryFrame.SetActive(false);
            if (!accountActive) ReleaseAccountInputGuard();
            if (browserFrame != null && !accountActive) browserFrame.SetActive(sleepRecoveryState == null);
            if (IsOpen && !accountActive)
            {
                if (sleepRecoveryState != null) RenderSleepRecovery();
                else { SetStatus(status); SetBusy(busy); if (browserPanel != null && refreshButton != null) browserPanel.firstSelected = refreshButton.gameObject; }
            }
        }

        private static string Limit(string value, int length)
        {
            return string.IsNullOrEmpty(value) ? "" : value.Length > length ? value.Substring(0, length) : value;
        }

        public void SetStatus(string value)
        {
            status = value ?? "";
            if (statusText != null) statusText.text = status;
        }

        /// <summary>Prevents entry into ordinary single-player actions while save paths are isolated.</summary>
        public void SetIsolated(bool value)
        {
            isolated = value;
            nextSessionScan = 0;
            if (!isolated) RestoreDisabledButtons();
        }

        /// <summary>Shows native pause/title status; a connected session gains a pause DISCONNECT button.</summary>
        public void SetSessionStatus(string value, bool connected)
        {
            sessionStatus = value ?? "";
            if (sessionConnected != connected) nextSessionScan = 0;
            sessionConnected = connected;
        }

        /// <summary>Call only after native world unload and restoration of personal save paths.
        /// The session owns that lifecycle; this method resets presentation and permits rejoin.</summary>
        public void ResetForTitle(string message = "Disconnected. Choose a server to join.")
        {
            if (disposed) return;
            // Authentication and connection failures happen at the existing title.
            // Keep their explanation and retry controls visible. A world disconnect
            // still returns to the ordinary title after native unload.
            bool showBrowser = !isolated;
            ClearAccountFlow();
            if (!showBrowser) Hide();
            ClearDialogueRecovery();
            ClearSleepRecovery();
            disconnectPending = false;
            sessionConnected = false;
            sessionStatus = "";
            if (passwordInput != null) passwordInput.SetTextWithoutNotify("");
            SetIsolated(false);
            SetBusy(false);
            SetStatus(message);
            refreshPersonalSlots = true;
            RefreshPersonalSaveButtons();
            nextScan = nextSessionScan = nextListUpdate = 0;
            UpdateSessionMenus();
            if (showBrowser)
            {
                reopenBrowserOnTitle = true;
                log?.Invoke("Account presentation returned to server browser; reason remains visible.");
            }
        }

        private void RefreshPersonalSaveButtons()
        {
            if (!refreshPersonalSlots || isolated) return;
            bool refreshed = false;
            foreach (var menu in NativeMainMenuRegistry.Snapshot())
            {
                if (menu == null || !menu.title) continue;
                // Despite the callback name, this only re-queries HasAnyValidSave
                // and updates Continue/first-selection. LoadUI.Show reloads its list.
                menu.SaveSlotDeleted();
                refreshed = true;
            }
            if (refreshed) refreshPersonalSlots = false;
        }

        private void RequestDisconnect()
        {
            if (disposed || disconnectPending || disconnect == null || (!sessionConnected && sleepRecoveryState == null)) return;
            bool wasBusy = busy;
            string previousStatus = sessionStatus;
            bool previousSubmitted = sleepRecoverySubmitted;
            disconnectPending = true;
            sessionStatus = "Disconnecting from the shared world...";
            SetStatus(sessionStatus);
            SetBusy(true);
            if (sleepRecoveryState != null)
            {
                sleepRecoveryGeneration++;
                sleepRecoverySubmitted = true;
                foreach (var button in sleepRecoveryButtons) if (button != null) button.interactable = false;
                if (sleepRecoveryProgress != null) sleepRecoveryProgress.text = sessionStatus;
            }
            if (sleepRecoveryDisconnect != null) sleepRecoveryDisconnect.interactable = false;
            UpdateSessionMenus();
            try { disconnect(); }
            catch (Exception ex)
            {
                disconnectPending = false;
                sessionStatus = previousStatus;
                sleepRecoverySubmitted = previousSubmitted;
                SetBusy(wasBusy);
                if (sleepRecoveryState != null) RenderSleepRecovery();
                SetStatus("Could not disconnect: " + ex.Message);
                log?.Invoke("Multiplayer disconnect: " + ex.Message);
                UpdateSessionMenus();
            }
        }

        private void UpdateSessionMenus()
        {
            if (Time.unscaledTime >= nextSessionScan)
            {
                nextSessionScan = Time.unscaledTime + 1f;
                for (int i = sessionMenus.Count - 1; i >= 0; i--) if (sessionMenus[i].Menu == null) sessionMenus.RemoveAt(i);
                for (int i = disabledButtons.Count - 1; i >= 0; i--) if (disabledButtons[i].Button == null) disabledButtons.RemoveAt(i);
                if (isolated || sessionConnected)
                    foreach (var menu in NativeMainMenuRegistry.Snapshot())
                    {
                        if (menu == null) continue;
                        bool found = false;
                        foreach (var item in sessionMenus) if (item.Menu == menu) { found = true; break; }
                        if (!found)
                        {
                            try { sessionMenus.Add(CreateSessionMenu(menu)); }
                            catch (Exception ex) { log?.Invoke("Native session controls: " + ex.Message); }
                        }
                    }
            }
            foreach (var session in sessionMenus)
            {
                if (session.Menu == null) continue;
                if (isolated)
                {
                    if (session.Menu.title)
                    {
                        DisableOrdinaryButton(session.Menu.newGameButton);
                        DisableOrdinaryButton(session.Menu.continueGameButton);
                        DisableOrdinaryButton(session.Menu.loadButton);
                        if (!session.Menu.HasChildrenPanelOpen && session.Menu.IsVisible && multiplayerButton != null && EventSystem.current != null)
                        {
                            var selectedObject = EventSystem.current.currentSelectedGameObject;
                            var selectedButton = selectedObject == null ? null : selectedObject.GetComponent<Button>();
                            if (selectedButton != null && !selectedButton.interactable)
                                EventSystem.current.SetSelectedGameObject(multiplayerButton.gameObject);
                        }
                    }
                    else
                    {
                        // Dedicated-world saving is server-owned. Local Save/Load must not expose
                        // a player-save workflow after the storage path has been isolated.
                        DisableOrdinaryButton(session.Menu.saveButton);
                        DisableOrdinaryButton(session.Menu.loadButton);
                    }
                }
                if (session.Disconnect != null)
                {
                    session.Disconnect.gameObject.SetActive(sessionConnected);
                    session.Disconnect.interactable = sessionConnected && !disconnectPending && disconnect != null;
                }
                if (session.Status != null)
                {
                    session.Status.gameObject.SetActive(isolated || sessionConnected);
                    string text = sessionStatus;
                    if (session.Status.text != text) session.Status.text = text;
                }
            }
        }

        private SessionMenu CreateSessionMenu(MainMenuUI menu)
        {
            var session = new SessionMenu { Menu = menu };
            Button original = menu.loadButton ?? menu.settingsButton ?? menu.continueButtonInPauseMenu;
            if (original == null) return session;
            var fontReference = original.GetComponentInChildren<TMP_Text>(true);
            var oldLabel = menu.transform.Find("NivalisTogether.SessionStatus");
            if (oldLabel != null) Object.Destroy(oldLabel.gameObject);
            session.Status = Label(menu.transform, sessionStatus, 0, 0, 610, 78, 17, accent);
            session.Status.gameObject.name = "NivalisTogether.SessionStatus";
            var rect = session.Status.rectTransform;
            rect.anchorMin = rect.anchorMax = Vector2.one;
            rect.pivot = Vector2.one; rect.anchoredPosition = new Vector2(-34, -28);
            session.Status.enableWordWrapping = true;
            session.Status.alignment = TextAlignmentOptions.TopRight;
            if (fontReference != null)
            { session.Status.font = fontReference.font; session.Status.fontSharedMaterial = fontReference.fontSharedMaterial; }
            if (!menu.title)
            {
                var parent = original.transform.parent;
                var existing = parent.Find("NivalisTogether.DisconnectButton");
                if (existing != null) Object.Destroy(existing.gameObject);
                session.Disconnect = CloneNativeButton(original, parent, "DISCONNECT", RequestDisconnect);
                FitSessionButton(session.Disconnect);
                session.Disconnect.name = "NivalisTogether.DisconnectButton";
                session.Disconnect.transform.SetSiblingIndex(original.transform.GetSiblingIndex() + 1);
                session.Original = original;
                session.OriginalNavigation = original.navigation;
                session.Following = original.navigation.selectOnDown;
                if (session.Following != null) session.FollowingNavigation = session.Following.navigation;
                var before = original.navigation; before.mode = Navigation.Mode.Explicit; before.selectOnDown = session.Disconnect; original.navigation = before;
                var inserted = session.Disconnect.navigation; inserted.mode = Navigation.Mode.Explicit;
                inserted.selectOnUp = original; inserted.selectOnDown = session.Following; session.Disconnect.navigation = inserted;
                if (session.Following != null) { var after = session.Following.navigation; after.selectOnUp = session.Disconnect; session.Following.navigation = after; }
                if (parent.GetComponent<LayoutGroup>() == null)
                {
                    var sourceRect = original.GetComponent<RectTransform>();
                    if (sourceRect != null)
                    {
                        float spacing = Math.Max(42f, sourceRect.rect.height + 6f), nearest = float.MaxValue;
                        foreach (var button in parent.GetComponentsInChildren<Button>(true))
                        {
                            if (button == original || button == session.Disconnect || button.transform.parent != parent) continue;
                            var sibling = button.GetComponent<RectTransform>();
                            if (sibling == null) continue;
                            float distance = sourceRect.anchoredPosition.y - sibling.anchoredPosition.y;
                            if (distance > 1 && distance < nearest) nearest = distance;
                        }
                        if (nearest < float.MaxValue) spacing = nearest;
                        foreach (var button in parent.GetComponentsInChildren<Button>(true))
                        {
                            if (button == original || button == session.Disconnect || button.transform.parent != parent) continue;
                            var sibling = button.GetComponent<RectTransform>();
                            if (sibling == null || sibling.anchoredPosition.y >= sourceRect.anchoredPosition.y - 1f) continue;
                            session.Shifted.Add(new ShiftedButton { Rect = sibling, Position = sibling.anchoredPosition });
                            sibling.anchoredPosition -= new Vector2(0, spacing);
                        }
                        session.Disconnect.GetComponent<RectTransform>().anchoredPosition = sourceRect.anchoredPosition - new Vector2(0, spacing);
                    }
                }
            }
            return session;
        }

        private static void FitSessionButton(Button button)
        {
            // The pause LOAD clone inherits a short fixed text box. Measure its
            // native font at its native size and give DISCONNECT one complete line.
            var rect = button.GetComponent<RectTransform>();
            float width = Math.Max(180, rect.rect.width);
            var labels = button.GetComponentsInChildren<TMP_Text>(true);
            foreach (var text in labels)
            {
                text.enableWordWrapping = false;
                text.overflowMode = TextOverflowModes.Overflow;
                width = Math.Max(width, text.GetPreferredValues("DISCONNECT", 10000, 10000).x + 32);
            }
            var fitter = button.GetComponent<ContentSizeFitter>(); if (fitter != null) fitter.enabled = false;
            var aspect = button.GetComponent<AspectRatioFitter>(); if (aspect != null) aspect.enabled = false;
            rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width);
            var layout = button.GetComponent<LayoutElement>() ?? button.gameObject.AddComponent<LayoutElement>();
            layout.minWidth = layout.preferredWidth = width;
            foreach (var text in labels)
                text.rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, width - 16);
            // Preserve native highlight graphics. A transparent child supplies a
            // full-width hit area and bubbles events to the original native Button.
            var nativeTarget = button.targetGraphic;
            foreach (var graphic in button.GetComponentsInChildren<Graphic>(true)) graphic.raycastTarget = false;
            var hitArea = UiObject("MultiplayerHitArea", button.transform);
            hitArea.AddComponent<LayoutElement>().ignoreLayout = true;
            Stretch(hitArea.GetComponent<RectTransform>());
            var image = hitArea.AddComponent<Image>(); image.color = Color.clear; image.raycastTarget = true;
            image.raycastPadding = Vector4.zero; image.alphaHitTestMinimumThreshold = 0;
            image.canvasRenderer.cullTransparentMesh = false;
            button.targetGraphic = nativeTarget;
        }

        private void DisableOrdinaryButton(Button button)
        {
            if (button == null) return;
            bool found = false;
            foreach (var entry in disabledButtons) if (entry.Button == button) { found = true; break; }
            if (!found) disabledButtons.Add(new DisabledButton { Button = button, Interactable = button.interactable });
            button.interactable = false;
        }

        private void RestoreDisabledButtons()
        {
            foreach (var entry in disabledButtons) if (entry.Button != null) entry.Button.interactable = entry.Interactable;
            disabledButtons.Clear();
        }

        public void SetBusy(bool value)
        {
            busy = value;
            if (joinButton != null) joinButton.interactable = !busy;
            if (refreshButton != null) refreshButton.interactable = !busy;
            if (saveButton != null) saveButton.interactable = !busy && saveServer != null;
            if (removeButton != null) removeButton.interactable = !busy && selected != null && selected.Saved && removeServer != null;
            foreach (var b in rowButtons) if (b != null) b.interactable = !busy;
            foreach (var field in new[] { addressInput, portInput, passwordInput, nameInput })
                if (field != null) field.interactable = !busy;
        }

        public void SetPlayerName(string value)
        {
            playerName = string.IsNullOrWhiteSpace(value) ? "Explorer" : value;
        }

        public void Show()
        {
            if (disposed || mainMenu == null || multiplayerButton == null) return;
            reopenBrowserOnTitle = false;
            try
            {
                bool wasOpen = IsOpen;
                if (browserRoot == null) CreateBrowser();
                if (accountActive) AcquireAccountInputGuard();
                if (accountActive && accountFrame != null)
                {
                    browserFrame.SetActive(false);
                    if (sleepRecoveryFrame != null) sleepRecoveryFrame.SetActive(false);
                    if (dialogueRecoveryFrame != null) dialogueRecoveryFrame.SetActive(false);
                    accountFrame.SetActive(true);
                }
                else if (dialogueRecoveryState != null) RenderDialogueRecovery();
                else if (sleepRecoveryState != null) RenderSleepRecovery();
                else
                {
                    if (browserFrame != null) browserFrame.SetActive(true);
                    if (sleepRecoveryFrame != null) sleepRecoveryFrame.SetActive(false);
                    browserPanel.firstSelected = refreshButton.gameObject;
                }
                browserRoot.transform.SetAsLastSibling();
                browserRoot.SetActive(true);
                // Account stages replace content inside this same native panel.
                // Reopening it through the parent could hide the current child
                // and fire its cancellation callback during the transition.
                if (!wasOpen) mainMenu.OpenAdditionalPanel(browserPanel, mainMenu.menuCanvasGroup);
                open = true;
                sleepRecoveryAutoOpen = false;
                dialogueRecoveryAutoOpen = false;
                if (dialogueRecoveryState != null) AcquireAccountInputGuard();
                if (accountActive)
                {
                    if (EventSystem.current != null && browserPanel.firstSelected != null)
                        EventSystem.current.SetSelectedGameObject(browserPanel.firstSelected);
                }
                else if (dialogueRecoveryState != null)
                {
                    if (EventSystem.current != null && browserPanel.firstSelected != null) EventSystem.current.SetSelectedGameObject(browserPanel.firstSelected);
                }
                else if (sleepRecoveryState != null) SelectSleepRecoveryControl();
                else
                {
                    RefreshRows(true);
                    SetStatus(status);
                    SetBusy(busy);
                    refresh?.Invoke();
                }
            }
            catch (Exception ex) { SetStatus("Could not open multiplayer: " + ex.Message); log?.Invoke(ex.ToString()); }
        }

        public void Hide()
        {
            if (browserPanel != null && browserPanel.IsVisible) browserPanel.Hide();
            HandleHidden();
        }

        private void HandleHidden()
        {
            if (hiding || !open) return;
            hiding = true;
            open = false;
            sleepRecoveryAutoOpen = false;
            dialogueRecoveryAutoOpen = false;
            if (!accountActive) ReleaseAccountInputGuard();
            sleepRecoveryGeneration++;
            var cancelAccount = accountActive ? accountCancel : null;
            if (accountActive) log?.Invoke("Account panel hidden by native UI; cancelling the pending account connection.");
            if (accountActive) ClearAccountFlow();
            if (passwordInput != null) passwordInput.SetTextWithoutNotify("");
            if (EventSystem.current != null && multiplayerButton != null && multiplayerButton.gameObject.activeInHierarchy)
                EventSystem.current.SetSelectedGameObject(multiplayerButton.gameObject);
            try { cancelAccount?.Invoke(); closed?.Invoke(); }
            finally { hiding = false; }
        }

        private void Attach(MainMenuUI menu)
        {
            mainMenu = menu;
            RefreshPersonalSaveButtons();
            buttonTemplate = menu.loadButton;
            styleText = buttonTemplate.GetComponentInChildren<TMP_Text>(true);
            var parent = buttonTemplate.transform.parent;
            // Survives a driver reinitialization without inserting a second menu item.
            var existing = parent.Find("NivalisTogether.MultiplayerButton");
            if (existing != null)
            {
                multiplayerButton = existing.GetComponent<Button>();
                if (multiplayerButton != null)
                {
                    multiplayerButton.onClick = new Button.ButtonClickedEvent();
                    multiplayerButton.onClick.AddListener((UnityAction)(Action)Show);
                    multiplayerButton.interactable = true;
                    var oldBrowser = mainMenu.transform.parent.Find("NivalisTogether.ServerBrowser");
                    if (oldBrowser != null) Object.Destroy(oldBrowser.gameObject);
                    return;
                }
                Object.Destroy(existing.gameObject);
            }
            multiplayerButton = CloneNativeButton(buttonTemplate, parent, "MULTIPLAYER", Show);
            multiplayerButton.name = "NivalisTogether.MultiplayerButton";
            multiplayerButton.transform.SetSiblingIndex(buttonTemplate.transform.GetSiblingIndex() + 1);
            multiplayerButton.interactable = true; // LOAD may be disabled when there are no saves.

            if (parent.GetComponent<LayoutGroup>() == null)
            {
                var loadRect = buttonTemplate.GetComponent<RectTransform>();
                var newRect = multiplayerButton.GetComponent<RectTransform>();
                if (loadRect != null && newRect != null)
                {
                    float spacing = Math.Max(42f, loadRect.rect.height + 6f);
                    float nearest = float.MaxValue;
                    foreach (var sibling in parent.GetComponentsInChildren<Button>(true))
                    {
                        if (sibling == buttonTemplate || sibling == multiplayerButton || sibling.transform.parent != parent) continue;
                        var rect = sibling.GetComponent<RectTransform>();
                        if (rect == null) continue;
                        float delta = loadRect.anchoredPosition.y - rect.anchoredPosition.y;
                        if (delta > 1f && delta < nearest) nearest = delta;
                    }
                    if (nearest < float.MaxValue) spacing = nearest;
                    foreach (var sibling in parent.GetComponentsInChildren<Button>(true))
                    {
                        if (sibling == buttonTemplate || sibling == multiplayerButton || sibling.transform.parent != parent) continue;
                        var rect = sibling.GetComponent<RectTransform>();
                        if (rect != null && rect.anchoredPosition.y < loadRect.anchoredPosition.y - 1f)
                        {
                            shiftedButtons.Add(new ShiftedButton { Rect = rect, Position = rect.anchoredPosition });
                            rect.anchoredPosition -= new Vector2(0, spacing);
                        }
                    }
                    newRect.anchoredPosition = loadRect.anchoredPosition - new Vector2(0, spacing);
                }
            }
            var navigation = buttonTemplate.navigation;
            navigation.mode = Navigation.Mode.Explicit;
            var oldDown = navigation.selectOnDown;
            navigation.selectOnDown = multiplayerButton;
            buttonTemplate.navigation = navigation;
            var inserted = multiplayerButton.navigation;
            inserted.mode = Navigation.Mode.Explicit;
            inserted.selectOnUp = buttonTemplate;
            inserted.selectOnDown = oldDown;
            multiplayerButton.navigation = inserted;
            if (oldDown != null) { var downNav = oldDown.navigation; downNav.selectOnUp = multiplayerButton; oldDown.navigation = downNav; }
            log?.Invoke("Native MULTIPLAYER button inserted beneath LOAD.");
        }

        private Button CloneNativeButton(Button original, Transform parent, string caption, Action action)
        {
            var go = Object.Instantiate(original.gameObject, parent, false);
            go.SetActive(false);
            var button = go.GetComponent<Button>();
            // Replacing the event also removes persistent Inspector callbacks to LoadRequest.
            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener((UnityAction)action);
            foreach (var behaviour in go.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour == null) continue;
                string type = behaviour.GetIl2CppType().Name;
                if (type == "LocalizedStaticUILabel" || type == "ClickWithAction" || type == "AutoClickButton")
                { behaviour.enabled = false; Object.Destroy(behaviour); }
            }
            foreach (var text in go.GetComponentsInChildren<TMP_Text>(true)) text.text = caption;
            var nav = button.navigation; nav.mode = Navigation.Mode.Automatic; button.navigation = nav;
            button.interactable = true;
            go.SetActive(true);
            return button;
        }

        private void CreateBrowser()
        {
            browserRoot = UiObject("NivalisTogether.ServerBrowser", mainMenu.transform.parent);
            browserRoot.SetActive(false);
            var full = browserRoot.GetComponent<RectTransform>();
            Stretch(full);
            var backdrop = browserRoot.AddComponent<Image>();
            backdrop.color = new Color(.008f, .018f, .025f, .90f);
            backdrop.raycastTarget = true;
            var group = browserRoot.AddComponent<CanvasGroup>();
            var frame = UiObject("Browser", full);
            browserFrame = frame;
            var frameRect = frame.GetComponent<RectTransform>();
            frameRect.anchorMin = frameRect.anchorMax = new Vector2(.5f, .5f);
            frameRect.pivot = new Vector2(.5f, .5f);
            frameRect.sizeDelta = new Vector2(1080, 730);
            frameRect.anchoredPosition = Vector2.zero;
            var surface = frame.AddComponent<Image>();
            surface.color = panelColor;
            // Inherit the game's frame sprite where possible without cloning its LoadUI logic.
            if (mainMenu.loadPanel != null)
            {
                var source = mainMenu.loadPanel.GetComponent<Image>();
                if (source != null && source.sprite != null) { surface.sprite = source.sprite; surface.type = source.type; }
            }

            Label(frameRect, "MULTIPLAYER", 32, 28, 900, 48, 32, accent);
            Label(frameRect, "Find a server, or connect directly to a friend's world.", 34, 82, 1000, 30, 17);
            Line(frameRect, 32, 126, 1016);
            Label(frameRect, "SERVERS", 34, 146, 610, 30, 19, accent);
            Label(frameRect, "DIRECT CONNECTION", 706, 146, 340, 30, 19, accent);
            for (int i = 0; i < RowsPerPage; i++)
            {
                int row = i;
                var button = PlainButton(frameRect, "", 32, 187 + i * 71, 635, 63, () => SelectRow(row));
                rowButtons.Add(button);
                var label = button.GetComponentInChildren<TextMeshProUGUI>(true);
                label.alignment = TextAlignmentOptions.MidlineLeft;
                label.margin = new Vector4(15, 2, 12, 2);
                label.fontSize = 18;
                rowLabels.Add(label);
            }
            pageText = Label(frameRect, "No saved or discovered servers", 172, 550, 355, 34, 16);
            pageText.alignment = TextAlignmentOptions.Center;
            previousButton = ActionButton(frameRect, "PREVIOUS", 32, 551, 135, 35, () => { page = Math.Max(0, page - 1); RefreshRows(true); });
            nextButton = ActionButton(frameRect, "NEXT", 532, 551, 135, 35, () => { page++; RefreshRows(true); });

            nameInput = Field(frameRect, "SERVER NAME", "My server", 705, 188, 340, 48, false);
            addressInput = Field(frameRect, "ADDRESS", "127.0.0.1", 705, 261, 240, 253, false);
            portInput = Field(frameRect, "PORT", "47778", 958, 261, 87, 5, false);
            portInput.characterValidation = TMP_InputField.CharacterValidation.Integer;
            var accountHint = Label(frameRect, "Sign in or create an account on the selected server, then choose your character.", 705, 339, 340, 61, 17);
            accountHint.enableWordWrapping = true;
            passwordInput = Field(frameRect, "ROOM PASSWORD (IF REQUIRED)", "", 705, 407, 340, 128, true);
            joinButton = ActionButton(frameRect, "JOIN SERVER", 705, 489, 340, 47, JoinSelected);
            saveButton = ActionButton(frameRect, "SAVE", 705, 549, 161, 35, SaveSelected);
            removeButton = ActionButton(frameRect, "REMOVE", 883, 549, 162, 35, RemoveSelected);
            selectionText = Label(frameRect, "Enter an address to join directly.", 34, 596, 1011, 29, 15);
            Line(frameRect, 32, 635, 1016);
            statusText = Label(frameRect, status, 34, 651, 655, 53, 16);
            statusText.enableWordWrapping = true;
            refreshButton = ActionButton(frameRect, "REFRESH", 705, 661, 161, 40, RequestRefresh);
            ActionButton(frameRect, "BACK", 883, 661, 162, 40, Hide);

            // Actual UIPanel lifecycle supplies cursor, pause, cancellation and parent-menu blocking.
            browserPanel = browserRoot.AddComponent<UIPanel>();
            browserPanel._canvasGroup = group;
            browserPanel.requiresMouse = true;
            browserPanel.pauseTimeWhenOpen = true;
            browserPanel.pauseTimeCompletely = false;
            // Creation is lazy and happens during Show. Start runs on the following frame;
            // keep it from hiding the newly opened panel on that first frame.
            browserPanel._startVisible = true;
            browserPanel.closeWithCancel = true;
            browserPanel.closeWithPause = true;
            browserPanel.firstSelected = refreshButton.gameObject;
            browserPanel.rememberLastSelected = false;
            browserPanel.add_OnHideEvent((Il2CppSystem.Action)(Action)HandleHidden);
            browserRoot.SetActive(true);
            browserPanel.SetInvisibleImmediate();
            ApplyAccountEndpoint();
        }

        private void CreateSleepRecoveryView()
        {
            if (browserRoot == null || buttonTemplate == null) return;
            sleepRecoveryFrame = UiObject("SharedDayRecovery", browserRoot.transform);
            var frame = sleepRecoveryFrame.GetComponent<RectTransform>();
            frame.anchorMin = frame.anchorMax = new Vector2(.5f, .5f);
            frame.pivot = new Vector2(.5f, .5f);
            frame.sizeDelta = new Vector2(1080, 730);
            frame.anchoredPosition = Vector2.zero;
            sleepRecoveryFrame.AddComponent<Image>().color = panelColor;
            sleepRecoveryTitle = Label(frame, "Shared day transition", 32, 25, 1016, 47, 28, accent);
            sleepRecoveryProgress = Label(frame, "", 34, 81, 1012, 48, 17);
            sleepRecoveryProgress.enableWordWrapping = true;
            sleepRecoveryProgress.alignment = TextAlignmentOptions.TopLeft;
            Line(frame, 32, 134, 1016);
            sleepRecoverySummaryScroll = CreateRecoveryScroll(frame, "Day summary", 32, 150, 1016, 281,
                out sleepRecoverySummaryContent);
            sleepRecoverySummary = Label(sleepRecoverySummaryContent, "", 0, 0, 978, 281, 19);
            sleepRecoverySummary.enableWordWrapping = true;
            sleepRecoverySummary.overflowMode = TextOverflowModes.Overflow;
            sleepRecoverySummary.alignment = TextAlignmentOptions.TopLeft;
            sleepRecoveryChoicesScroll = CreateRecoveryScroll(frame, "Day choices", 32, 451, 1016, 146,
                out sleepRecoveryChoicesContent);
            Line(frame, 32, 615, 1016);
            var footer = Label(frame, "You can close this panel and reopen it from MULTIPLAYER.", 34, 630, 1008, 25, 15);
            footer.enableWordWrapping = true;
            sleepRecoveryBack = ActionButton(frame, "CLOSE PANEL", 640, 670, 190, 38, Hide);
            sleepRecoveryDisconnect = ActionButton(frame, "DISCONNECT", 850, 670, 196, 38, () =>
            {
                if (sleepRecoveryState == null || disconnect == null) return;
                RequestDisconnect();
            });
        }

        private ScrollRect CreateRecoveryScroll(Transform parent, string name, float x, float y, float width, float height,
            out RectTransform content)
        {
            var root = UiObject(name, parent);
            Place(root.GetComponent<RectTransform>(), x, y, width, height);
            root.AddComponent<Image>().color = fieldColor;
            var viewport = UiObject("Viewport", root.transform);
            Stretch(viewport.GetComponent<RectTransform>(), 10, 8, 28, 8);
            viewport.AddComponent<RectMask2D>();
            var contentObject = UiObject("Content", viewport.transform);
            content = contentObject.GetComponent<RectTransform>();
            Place(content, 0, 0, width - 38, height - 16);
            var barObject = UiObject("Scrollbar", root.transform);
            Place(barObject.GetComponent<RectTransform>(), width - 16, 8, 8, height - 16);
            barObject.AddComponent<Image>().color = new Color(accent.r, accent.g, accent.b, .12f);
            var handleObject = UiObject("Handle", barObject.transform);
            Stretch(handleObject.GetComponent<RectTransform>());
            var handleImage = handleObject.AddComponent<Image>();
            handleImage.color = new Color(accent.r, accent.g, accent.b, .8f);
            var scrollbar = barObject.AddComponent<Scrollbar>();
            scrollbar.handleRect = handleObject.GetComponent<RectTransform>();
            scrollbar.targetGraphic = handleImage;
            scrollbar.direction = Scrollbar.Direction.BottomToTop;
            var scroll = root.AddComponent<ScrollRect>();
            scroll.viewport = viewport.GetComponent<RectTransform>();
            scroll.content = content;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 32;
            scroll.verticalScrollbar = scrollbar;
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
            scroll.verticalNormalizedPosition = 1;
            return scroll;
        }

        private void CreateDialogueRecoveryView()
        {
            if (browserRoot == null || buttonTemplate == null) return;
            dialogueRecoveryFrame = UiObject("ConversationRecovery", browserRoot.transform);
            var frame = dialogueRecoveryFrame.GetComponent<RectTransform>();
            frame.anchorMin = frame.anchorMax = frame.pivot = new Vector2(.5f, .5f);
            frame.sizeDelta = new Vector2(1080, 730); frame.anchoredPosition = Vector2.zero;
            dialogueRecoveryFrame.AddComponent<Image>().color = panelColor;
            Label(frame, "CONTINUE YOUR CONVERSATION", 32, 25, 1016, 47, 28, accent);
            dialogueRecoverySpeaker = Label(frame, "", 34, 84, 1012, 36, 21, accent);
            Line(frame, 32, 132, 1016);
            dialogueRecoveryTextScroll = CreateRecoveryScroll(frame, "Conversation text", 32, 148, 1016, 240, out dialogueRecoveryTextContent);
            dialogueRecoveryText = Label(dialogueRecoveryTextContent, "", 0, 0, 978, 224, 19);
            dialogueRecoveryText.enableWordWrapping = true;
            dialogueRecoveryText.overflowMode = TextOverflowModes.Overflow;
            dialogueRecoveryText.alignment = TextAlignmentOptions.TopLeft;
            dialogueRecoveryChoicesScroll = CreateRecoveryScroll(frame, "Conversation choices", 32, 404, 1016, 183, out dialogueRecoveryChoicesContent);
            dialogueRecoveryProgress = Label(frame, "", 34, 604, 1012, 53, 17);
            dialogueRecoveryProgress.enableWordWrapping = true;
            dialogueRecoveryProgress.alignment = TextAlignmentOptions.TopLeft;
            dialogueRecoveryBack = ActionButton(frame, "CLOSE PANEL", 640, 670, 190, 38, Hide);
            dialogueRecoveryDisconnect = ActionButton(frame, "DISCONNECT", 850, 670, 196, 38, () =>
            {
                if (dialogueRecoveryState != null && disconnect != null) RequestDisconnect();
            });
            Label(frame, "Reopen this conversation from MULTIPLAYER.", 34, 674, 580, 28, 15);
        }

        private void DestroyDialogueRecoveryButtons()
        {
            foreach (var button in dialogueRecoveryButtons)
                if (button != null) { button.gameObject.SetActive(false); Object.Destroy(button.gameObject); }
            dialogueRecoveryButtons.Clear();
        }

        private void UpdateDialogueRecoveryProgress()
        {
            if (dialogueRecoveryProgress == null || dialogueRecoveryState == null) return;
            dialogueRecoveryProgress.text = !string.IsNullOrEmpty(dialogueRecovery.Notice) ? Limit(dialogueRecovery.Notice, 512) :
                dialogueRecovery.Submitted ? "Choice sent. Waiting for the server..." :
                "Finish this conversation to join the shared world. Your choice continues the shared story.";
        }

        private void RenderDialogueRecovery()
        {
            if (dialogueRecoveryState == null || browserRoot == null || accountActive) return;
            if (dialogueRecoveryFrame == null) CreateDialogueRecoveryView();
            if (dialogueRecoveryFrame == null) return;
            var state = dialogueRecoveryState;
            string presentation = state.Conversation + ":" + state.Step + ":" + state.SpeakerName + ":" + state.Text;
            bool unchanged = presentation == dialogueRecoveryPresentation;
            float textPosition = unchanged ? dialogueRecoveryTextScroll.verticalNormalizedPosition : 1;
            float choicePosition = unchanged ? dialogueRecoveryChoicesScroll.verticalNormalizedPosition : 1;
            dialogueRecoveryPresentation = presentation;
            browserFrame.SetActive(false);
            if (sleepRecoveryFrame != null) sleepRecoveryFrame.SetActive(false);
            dialogueRecoveryFrame.SetActive(true);
            dialogueRecoverySpeaker.text = state.SpeakerName ?? "";
            dialogueRecoveryText.text = state.Text ?? "";
            float textHeight = Math.Max(224, dialogueRecoveryText.GetPreferredValues(state.Text ?? "", 978, 100000).y + 8);
            dialogueRecoveryTextContent.sizeDelta = dialogueRecoveryText.rectTransform.sizeDelta = new Vector2(978, textHeight);
            dialogueRecoveryTextScroll.verticalNormalizedPosition = textPosition;
            DestroyDialogueRecoveryButtons();
            var offered = new List<DialogueRecoveryChoice>(state.Choices);
            if (state.CanContinue && offered.Count == 0) offered.Add(new DialogueRecoveryChoice { Continue = true, Enabled = true, Text = "Continue" });
            float offset = 0;
            long generation = dialogueRecovery.Generation;
            var choose = dialogueRecoveryChoose;
            Button firstEnabled = null;
            foreach (var choice in offered)
            {
                var captured = new DialogueRecoveryChoice { Id = choice.Id, Text = choice.Text, Enabled = choice.Enabled, Continue = choice.Continue };
                var button = ActionButton(dialogueRecoveryChoicesContent, string.IsNullOrEmpty(choice.Text) ? "(Unavailable response)" : choice.Text,
                    0, offset, 978, 50, () =>
                {
                    if (disposed || !IsOpen || choose == null || !dialogueRecovery.TrySubmit(generation, captured, out var selectedChoice)) return;
                    foreach (var item in dialogueRecoveryButtons) if (item != null) item.interactable = false;
                    UpdateDialogueRecoveryProgress();
                    try { choose(selectedChoice); }
                    catch (Exception ex)
                    {
                        if (dialogueRecovery.ReleaseUnsent(generation)) RenderDialogueRecovery();
                        else
                        {
                            dialogueRecovery.SetNotice("Could not confirm this response. Waiting for a fresh server update.");
                            UpdateDialogueRecoveryProgress();
                        }
                        log?.Invoke("Conversation recovery response could not be submitted: " + ex.GetType().Name);
                    }
                });
                var label = button.GetComponentInChildren<TMP_Text>(true);
                label.enableWordWrapping = true; label.enableAutoSizing = false; label.fontSize = 18;
                float height = Math.Max(50, label.GetPreferredValues(choice.Text ?? "", 932, 100000).y + 20);
                Place(button.GetComponent<RectTransform>(), 0, offset, 978, height);
                Stretch(label.rectTransform, 18, 6, 18, 6);
                offset += height + 8;
                button.interactable = choice.Enabled && !dialogueRecovery.Submitted && choose != null && state.TargetPlayerId == dialogueRecovery.LocalPlayerId;
                if (button.interactable && firstEnabled == null) firstEnabled = button;
                dialogueRecoveryButtons.Add(button);
            }
            dialogueRecoveryChoicesContent.sizeDelta = new Vector2(978, Math.Max(167, offset));
            dialogueRecoveryChoicesScroll.verticalNormalizedPosition = choicePosition;
            dialogueRecoveryDisconnect.interactable = disconnect != null && !disconnectPending;
            browserPanel.firstSelected = firstEnabled != null ? firstEnabled.gameObject : dialogueRecoveryBack.gameObject;
            UpdateDialogueRecoveryProgress();
            if (IsOpen)
            {
                AcquireAccountInputGuard();
                if (!unchanged && EventSystem.current != null) EventSystem.current.SetSelectedGameObject(browserPanel.firstSelected);
            }
        }

        private void DestroySleepRecoveryButtons()
        {
            foreach (var button in sleepRecoveryButtons)
                if (button != null) { button.gameObject.SetActive(false); Object.Destroy(button.gameObject); }
            sleepRecoveryButtons.Clear();
        }

        private void RenderSleepRecovery()
        {
            if (sleepRecoveryState == null || browserRoot == null) return;
            if (sleepRecoveryFrame == null) CreateSleepRecoveryView();
            if (sleepRecoveryFrame == null) return;
            var offered = sleepRecoveryState;
            var choose = sleepRecoveryChoose;
            bool owner = offered.RecoveryOwner && !string.IsNullOrEmpty(sleepRecoveryPlayer) &&
                string.Equals(offered.Owner, sleepRecoveryPlayer, StringComparison.Ordinal);
            string presentation = offered.Epoch + ":" + offered.Step + ":" + offered.Owner + ":" + owner +
                ":" + offered.Title + ":" + offered.Text;
            foreach (var choice in offered.Choices) presentation += "|" + choice.Action + ":" + choice.Label + ":" + choice.Percentage;
            bool unchanged = presentation == sleepRecoveryPresentation;
            float summaryPosition = unchanged ? sleepRecoverySummaryScroll.verticalNormalizedPosition : 1;
            float choicesPosition = unchanged ? sleepRecoveryChoicesScroll.verticalNormalizedPosition : 1;
            int selectedChoice = -1;
            if (unchanged && EventSystem.current != null)
            {
                var selectedObject = EventSystem.current.currentSelectedGameObject;
                for (int i = 0; i < sleepRecoveryButtons.Count; i++)
                    if (sleepRecoveryButtons[i] != null && selectedObject == sleepRecoveryButtons[i].gameObject) selectedChoice = i;
            }
            sleepRecoveryPresentation = presentation;
            browserFrame.SetActive(false);
            sleepRecoveryFrame.SetActive(true);
            DestroySleepRecoveryButtons();
            sleepRecoveryGeneration++;
            long generation = sleepRecoveryGeneration;
            sleepRecoveryTitle.text = string.IsNullOrWhiteSpace(offered.Title) ? "Shared day transition" : offered.Title;
            sleepRecoverySummary.text = offered.Text;
            float textHeight = Math.Max(265, sleepRecoverySummary.GetPreferredValues(offered.Text, 978, 100000).y + 8);
            sleepRecoverySummaryContent.sizeDelta = new Vector2(978, textHeight);
            sleepRecoverySummary.rectTransform.sizeDelta = new Vector2(978, textHeight);
            sleepRecoverySummaryScroll.verticalNormalizedPosition = summaryPosition;
            sleepRecoveryProgress.text = sleepRecoverySubmitted ? "Choice sent. Waiting for the server..." :
                offered.Choices.Length == 0 ? "The server is finishing the shared day. Your character will join when it is ready." :
                owner ? "Choose how to continue the shared day. This choice affects everyone in the shared world." :
                "Another player is resolving the shared day. Your character will join when it is ready.";
            if (owner && choose != null)
            {
                for (int i = 0; i < offered.Choices.Length; i++)
                {
                    var choice = offered.Choices[i];
                    // Each closure owns a copy and an exact presentation generation. A later
                    // event, closure, disconnect or clear makes any queued native click inert.
                    var capturedChoice = new GroupSleepChoice { Action = choice.Action, Label = choice.Label, Percentage = choice.Percentage };
                    var button = ActionButton(sleepRecoveryChoicesContent, choice.Label, i % 2 * 491, i / 2 * 47, 477, 38, () =>
                    {
                        if (disposed || !IsOpen || generation != sleepRecoveryGeneration || sleepRecoverySubmitted ||
                            sleepRecoveryState != offered || !offered.RecoveryOwner || offered.Owner != sleepRecoveryPlayer) return;
                        sleepRecoverySubmitted = true;
                        foreach (var item in sleepRecoveryButtons) if (item != null) item.interactable = false;
                        sleepRecoveryProgress.text = "Choice sent. Waiting for the server...";
                        try { choose(capturedChoice); }
                        catch (Exception ex)
                        {
                            sleepRecoveryProgress.text = "Could not send the choice. Waiting for a fresh server update.";
                            log?.Invoke("Shared day recovery choice: " + ex.Message);
                        }
                    });
                    button.interactable = !sleepRecoverySubmitted;
                    sleepRecoveryButtons.Add(button);
                }
            }
            float choicesHeight = Math.Max(130, (sleepRecoveryButtons.Count + 1) / 2 * 47);
            sleepRecoveryChoicesContent.sizeDelta = new Vector2(978, choicesHeight);
            sleepRecoveryChoicesScroll.verticalNormalizedPosition = choicesPosition;
            sleepRecoveryChoicesScroll.gameObject.SetActive(sleepRecoveryButtons.Count > 0);
            sleepRecoveryDisconnect.interactable = disconnect != null && !disconnectPending;
            browserPanel.firstSelected = sleepRecoveryButtons.Count > 0 && !sleepRecoverySubmitted ?
                sleepRecoveryButtons[0].gameObject : sleepRecoveryBack.gameObject;
            if (IsOpen)
            {
                if (!unchanged) SelectSleepRecoveryControl();
                else if (selectedChoice >= 0 && selectedChoice < sleepRecoveryButtons.Count && EventSystem.current != null)
                    EventSystem.current.SetSelectedGameObject(sleepRecoveryButtons[selectedChoice].gameObject);
            }
        }

        private void SelectSleepRecoveryControl()
        {
            if (browserPanel == null || EventSystem.current == null || sleepRecoveryState == null) return;
            var target = browserPanel.firstSelected;
            if (target != null && target.activeInHierarchy) EventSystem.current.SetSelectedGameObject(target);
        }

        private void RequestRefresh()
        {
            if (busy) return;
            SetStatus("Refreshing servers...");
            try { refresh?.Invoke(); RefreshRows(true); }
            catch (Exception ex) { SetStatus(ex.Message); }
        }

        private void RefreshRows(bool force)
        {
            if (browserRoot == null) return;
            entries.Clear();
            try
            {
                var current = listServers?.Invoke();
                if (current != null) foreach (var entry in current) if (entry != null) entries.Add(entry);
            }
            catch (Exception ex) { SetStatus("Could not list servers: " + ex.Message); }
            page = Math.Max(0, Math.Min(page, Math.Max(0, (entries.Count - 1) / RowsPerPage)));
            string signature = page.ToString() + ":" + selectedId + ":" + busy;
            foreach (var entry in entries)
                signature += "|" + EntryId(entry) + ":" + entry.Name + ":" + entry.Online + ":" + entry.Players + ":" + entry.Capacity + ":" + entry.PingMilliseconds + ":" + entry.Saved;
            if (!force && signature == lastListSignature) return;
            lastListSignature = signature;
            for (int i = 0; i < RowsPerPage; i++)
            {
                int index = page * RowsPerPage + i;
                bool populated = index < entries.Count;
                rowButtons[i].gameObject.SetActive(populated);
                if (!populated) continue;
                var entry = entries[index];
                string label = string.IsNullOrWhiteSpace(entry.Name) ? entry.Host : entry.Name;
                string occupancy = entry.Capacity > 0 ? entry.Players + "/" + entry.Capacity + " players" : "";
                string ping = entry.PingMilliseconds >= 0 ? "  " + entry.PingMilliseconds + " ms" : "";
                string availability = entry.Online ? "ONLINE" : "UNVERIFIED";
                rowLabels[i].text = (HasAlias(entry, selectedId) ? "> " : "") + label + (entry.Saved ? "  [SAVED]" : "") + "\n" + availability + "  " + occupancy + ping;
                rowButtons[i].interactable = !busy;
                var image = rowButtons[i].GetComponent<Image>();
                image.color = HasAlias(entry, selectedId) ? new Color(.10f, .24f, .26f, 1) : fieldColor;
            }
            pageText.text = entries.Count == 0 ? "No saved or discovered servers" : "PAGE " + (page + 1) + " / " + ((entries.Count + RowsPerPage - 1) / RowsPerPage);
            previousButton.interactable = !busy && page > 0;
            nextButton.interactable = !busy && (page + 1) * RowsPerPage < entries.Count;
        }

        private void SelectRow(int row)
        {
            int index = page * RowsPerPage + row;
            if (busy || index >= entries.Count) return;
            selected = entries[index]; selectedId = EntryId(selected);
            nameInput.SetTextWithoutNotify(selected.Name);
            addressInput.SetTextWithoutNotify(selected.Host);
            portInput.SetTextWithoutNotify(selected.Port.ToString());
            passwordInput.SetTextWithoutNotify("");
            selectionText.text = string.IsNullOrWhiteSpace(selected.Description) ? selected.Host + ":" + selected.Port : selected.Description;
            SetBusy(busy);
            RefreshRows(true);
        }

        private MultiplayerServerEntry ReadEndpoint()
        {
            var host = (addressInput.text ?? "").Trim();
            if (host.Length == 0) throw new ArgumentException("Enter the server address.");
            if (host.Contains("://") || host.Contains("/") || host.Contains("\\")) throw new ArgumentException("Enter a hostname or IP address, without a URL.");
            if (!int.TryParse(portInput.text, out int port) || port < 1 || port > 65535) throw new ArgumentException("Enter a port from 1 to 65535.");
            bool sameServer = selected != null && string.Equals(selected.Host, host, StringComparison.OrdinalIgnoreCase) && selected.Port == port;
            return new MultiplayerServerEntry
            {
                Id = sameServer ? EntryId(selected) : host.ToLowerInvariant() + ":" + port,
                Host = host,
                Port = port,
                Name = string.IsNullOrWhiteSpace(nameInput.text) ? host : nameInput.text.Trim(),
                Saved = sameServer && selected.Saved,
                PasswordRequired = sameServer && selected.PasswordRequired,
                Online = sameServer && selected.Online,
                Players = sameServer ? selected.Players : 0,
                Capacity = sameServer ? selected.Capacity : 0,
                PingMilliseconds = sameServer ? selected.PingMilliseconds : -1,
                Description = sameServer ? selected.Description : ""
            };
        }

        private void JoinSelected()
        {
            if (busy) return;
            try
            {
                var endpoint = ReadEndpoint();
                playerName = playerName.Trim();
                if (playerName.Length == 0) throw new ArgumentException("Enter your player name.");
                if (join == null) throw new InvalidOperationException("Multiplayer is not ready yet.");
                SetStatus("Connecting to " + endpoint.Name + "...");
                join(new MultiplayerJoinRequest { Server = endpoint, PlayerName = playerName, Password = passwordInput.text ?? "" });
            }
            catch (Exception ex) { SetBusy(false); SetStatus(ex.Message); }
        }

        private void SaveSelected()
        {
            if (busy || saveServer == null) return;
            try
            {
                var endpoint = ReadEndpoint(); endpoint.Saved = true;
                saveServer(endpoint); selected = endpoint; selectedId = EntryId(endpoint);
                SetStatus("Server saved."); SetBusy(false); RefreshRows(true);
            }
            catch (Exception ex) { SetStatus(ex.Message); }
        }

        private void RemoveSelected()
        {
            if (busy || selected == null || removeServer == null) return;
            try
            {
                removeServer(EntryId(selected)); selected = null; selectedId = "";
                SetStatus("Server removed from saved servers."); SetBusy(false); RefreshRows(true);
            }
            catch (Exception ex) { SetStatus(ex.Message); }
        }

        private static string EntryId(MultiplayerServerEntry entry)
        {
            return string.IsNullOrWhiteSpace(entry.Id) ? (entry.Host ?? "").ToLowerInvariant() + ":" + entry.Port : entry.Id;
        }

        private static bool HasAlias(MultiplayerServerEntry entry, string id)
        {
            if (string.Equals(EntryId(entry), id, StringComparison.OrdinalIgnoreCase)) return true;
            if (entry.AliasIds != null)
                foreach (var alias in entry.AliasIds)
                    if (string.Equals(alias, id, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public void ShowIdentityTrust(string endpoint, string fingerprint, Action<bool> respond)
        {
            RememberAccountEndpoint(endpoint);
            QueueAccountView("VERIFY SERVER IDENTITY", (frame, token) =>
            {
                Label(frame, Limit(endpoint, 300), 36, 96, 1008, 38, 20, accent);
                var explanation = Label(frame, "This is your first connection to this address. Compare this fingerprint with the server owner before trusting it. Future connections must match the saved fingerprint.", 36, 159, 1008, 118, 20);
                explanation.enableWordWrapping = true;
                Label(frame, "SERVER FINGERPRINT (SHA-256)", 36, 298, 1008, 28, 17, accent);
                string formatted = "";
                for (int i = 0; i < fingerprint.Length; i += 4)
                    formatted += (i == 32 ? "\n" : i == 0 ? "" : " ") + fingerprint.Substring(i, Math.Min(4, fingerprint.Length - i));
                var print = Label(frame, formatted, 36, 341, 1008, 115, 23);
                print.enableWordWrapping = false;
                Label(frame, "Trust is stored for this address. The sign-in window lasts up to 3 minutes. Use CANCEL to return to the server list.", 36, 491, 1008, 65, 18).enableWordWrapping = true;
                var accept = AccountButton(frame, "TRUST THIS SERVER", 36, 580, 620, () =>
                {
                    if (!AccountCurrent(token)) return;
                    SetAccountBusy(true, "Saving trusted server identity..."); respond?.Invoke(true);
                });
                AccountCancelButton(frame, token, () => respond?.Invoke(false));
                browserPanel.firstSelected = accept.gameObject;
            }, () => respond?.Invoke(false));
        }

        public void ShowAccountForm(string endpoint, string notice, Action<AccountLoginRequest> submit, Action cancel, string roomPassword = "")
        {
            RememberAccountEndpoint(endpoint);
            QueueAccountView("SERVER ACCOUNT", (frame, token) =>
            {
                Label(frame, Limit(endpoint, 300), 36, 92, 1008, 36, 20, accent);
                Label(frame, "Use your account for this server. New accounts start with their own money and inventory.", 36, 138, 1008, 65, 18).enableWordWrapping = true;
                accountUsernameInput = Field(frame, "USERNAME  (3-24 LETTERS, DIGITS, _ OR -)", accountUsername, 36, 220, 488, 24, false);
                accountPasswordInput = Field(frame, "ACCOUNT PASSWORD  (12-128 CHARACTERS)", "", 556, 220, 488, 256, true);
                accountConfirmInput = Field(frame, "CONFIRM PASSWORD  (CREATE ACCOUNT ONLY)", "", 556, 327, 488, 256, true);
                accountRoomInput = Field(frame, "ROOM PASSWORD  (IF REQUIRED)", roomPassword ?? "", 36, 327, 488, 128, true);
                roomPassword = "";
                if (passwordInput != null) passwordInput.SetTextWithoutNotify("");
                Label(frame, "Account and room passwords are separate. Passwords are cleared on submit or cancel. The server allows up to 3 minutes to sign in; use CANCEL to return to the server list.", 36, 436, 1008, 82, 18).enableWordWrapping = true;
                Action<bool> send = register =>
                {
                    if (!AccountCurrent(token)) return;
                    try
                    {
                        string username = AccountAuthentication.NormalizeUsername(accountUsernameInput.text);
                        string secret = accountPasswordInput.text ?? "";
                        AccountAuthentication.ValidatePassword(secret);
                        if (register && !string.Equals(secret, accountConfirmInput.text, StringComparison.Ordinal))
                            throw new ArgumentException("The account passwords do not match.");
                        var request = new AccountLoginRequest { Username = username, Password = secret,
                            RoomPassword = accountRoomInput.text ?? "", Register = register };
                        accountUsername = username;
                        ClearAccountSecrets();
                        SetAccountBusy(true, register ? "Creating your account..." : "Signing in...");
                        try { submit?.Invoke(request); }
                        finally { request.Password = request.RoomPassword = ""; }
                    }
                    catch (Exception ex) { SetAccountBusy(false, Limit(ex.Message, 256)); }
                };
                AccountButton(frame, "LOG IN", 36, 580, 292, () => send(false));
                AccountButton(frame, "CREATE ACCOUNT", 346, 580, 310, () => send(true));
                AccountCancelButton(frame, token, cancel);
                accountNotice = Limit(notice, 256);
                browserPanel.firstSelected = accountUsernameInput.gameObject;
            }, cancel);
        }

        public void ShowAccountProfile(AccountAuthenticated profile, Action<string> joinAppearance, Action cancel)
        {
            AccountClientFlow.ValidateOptions(profile?.AppearanceOptions);
            // Detach display data from the transport DTO. A later catalog update
            // creates a fresh presentation generation and invalidates old clicks.
            var options = new List<AppearanceOption>();
            foreach (var item in profile.AppearanceOptions) options.Add(new AppearanceOption { Id = item.Id, Name = item.Name });
            string chosen = options.Exists(o => o.Id == profile.AppearanceId) ? profile.AppearanceId : options.Count == 0 ? "" : options[0].Id;
            QueueAccountView("CHOOSE YOUR CHARACTER", (frame, token) =>
            {
                Label(frame, "SIGNED IN AS " + Limit(profile.Username, 24), 36, 92, 1008, 37, 20, accent);
                Label(frame, "Choose a character offered by this server. Your appearance will be visible in the shared world.", 36, 137, 1008, 62, 18).enableWordWrapping = true;
                var rows = new List<Button>(); var labels = new List<TextMeshProUGUI>();
                var pageLabel = Label(frame, "", 366, 505, 348, 34, 17); pageLabel.alignment = TextAlignmentOptions.Center;
                int shownPage = Math.Max(0, options.FindIndex(o => o.Id == chosen)) / 6;
                Button previous = null, next = null, joinWorld = null;
                Action render = null;
                for (int i = 0; i < 6; i++)
                {
                    int row = i;
                    var button = PlainButton(frame, "", 36 + (i % 2) * 520, 213 + (i / 2) * 87, 488, 70, () =>
                    {
                        int index = shownPage * 6 + row;
                        if (!AccountCurrent(token) || index >= options.Count) return;
                        chosen = options[index].Id; render();
                    });
                    accountControls[button] = true; rows.Add(button);
                    var text = button.GetComponentInChildren<TextMeshProUGUI>(true);
                    text.enableWordWrapping = true; text.fontSize = 19; text.margin = new Vector4(13, 3, 13, 3); labels.Add(text);
                }
                previous = AccountButton(frame, "PREVIOUS", 36, 505, 292, () => { if (AccountCurrent(token) && shownPage > 0) { shownPage--; render(); } });
                next = AccountButton(frame, "NEXT", 748, 505, 296, () => { if (AccountCurrent(token) && (shownPage + 1) * 6 < options.Count) { shownPage++; render(); } });
                joinWorld = AccountButton(frame, "JOIN WORLD", 36, 580, 620, () =>
                {
                    if (!AccountCurrent(token) || !options.Exists(o => o.Id == chosen)) return;
                    SetAccountBusy(true, "Saving your character selection..."); joinAppearance?.Invoke(chosen);
                });
                AccountCancelButton(frame, token, cancel);
                render = () =>
                {
                    for (int i = 0; i < rows.Count; i++)
                    {
                        int index = shownPage * 6 + i; bool present = index < options.Count;
                        rows[i].gameObject.SetActive(present); if (!present) continue;
                        labels[i].text = (options[index].Id == chosen ? "> " : "") + options[index].Name;
                        rows[i].GetComponent<Image>().color = options[index].Id == chosen ? new Color(.10f, .24f, .26f, 1) : fieldColor;
                    }
                    accountControls[previous] = shownPage > 0;
                    accountControls[next] = (shownPage + 1) * 6 < options.Count;
                    accountControls[joinWorld] = options.Count > 0;
                    pageLabel.text = options.Count == 0 ? "Waiting for server characters..." : "PAGE " + (shownPage + 1) + " / " + ((options.Count + 5) / 6);
                    SetAccountBusy(accountBusy, accountNotice);
                };
                render(); browserPanel.firstSelected = options.Count > 0 ? rows[0].gameObject : next.gameObject;
            }, cancel);
        }

        private void QueueAccountView(string title, Action<RectTransform, long> build, Action cancel)
        {
            // Stage changes replace content, not the native modal or its input
            // ownership. Never release the guard between trust/form/appearance.
            ClearAccountFlow(false);
            accountActive = true; accountCancel = cancel;
            long token = accountGeneration;
            pendingAccountRender = () =>
            {
                if (!accountActive || token != accountGeneration || disposed) return;
                if (browserRoot == null) CreateBrowser();
                AcquireAccountInputGuard();
                accountFrame = UiObject("ServerAccount", browserRoot.transform);
                var frame = accountFrame.GetComponent<RectTransform>();
                frame.anchorMin = frame.anchorMax = frame.pivot = new Vector2(.5f, .5f);
                frame.sizeDelta = new Vector2(1080, 730); frame.anchoredPosition = Vector2.zero;
                accountFrame.AddComponent<Image>().color = panelColor;
                Label(frame, title, 36, 28, 1008, 48, 30, accent);
                Line(frame, 36, 645, 1008);
                accountNoticeText = Label(frame, "", 36, 664, 1008, 45, 17);
                accountNoticeText.enableWordWrapping = true;
                build(frame, token); SetAccountBusy(accountBusy, accountNotice); Show();
                log?.Invoke("Account presentation: " + title + "; native text-entry shortcuts guarded.");
            };
            if (mainMenu != null && multiplayerButton != null)
            { var render = pendingAccountRender; pendingAccountRender = null; render(); }
        }
        private Button AccountButton(Transform frame, string caption, float x, float y, float width, Action action)
        {
            var button = ActionButton(frame, caption, x, y, width, 44, action);
            accountControls[button] = true; return button;
        }
        private void AccountCancelButton(Transform frame, long token, Action cancel)
        {
            ActionButton(frame, "CANCEL", 748, 580, 296, 44, () =>
            {
                if (!accountActive || token != accountGeneration) return;
                log?.Invoke("Account presentation explicitly cancelled.");
                ClearAccountFlow(); cancel?.Invoke();
            });
        }
        private bool AccountCurrent(long token) => !disposed && accountActive && !accountBusy && token == accountGeneration;
        public void SetAccountBusy(bool value, string message)
        {
            accountBusy = value; accountNotice = Limit(message, 256);
            if (accountNoticeText != null) accountNoticeText.text = accountNotice;
            foreach (var item in accountControls) if (item.Key != null) item.Key.interactable = !value && item.Value;
            foreach (var input in new[] { accountUsernameInput, accountPasswordInput, accountConfirmInput, accountRoomInput })
                if (input != null) input.interactable = !value;
        }
        private void ClearAccountSecrets()
        {
            foreach (var field in new[] { accountPasswordInput, accountConfirmInput, accountRoomInput })
                if (field != null) field.SetTextWithoutNotify("");
        }
        public void ClearAccountFlow() => ClearAccountFlow(true);
        private void ClearAccountFlow(bool releaseInput)
        {
            accountGeneration++; pendingAccountRender = null; accountCancel = null;
            accountActive = accountBusy = false; accountNotice = "";
            if (accountUsernameInput != null) accountUsername = accountUsernameInput.text ?? "";
            ClearAccountSecrets();
            if (accountFrame != null) { accountFrame.SetActive(false); Object.Destroy(accountFrame); }
            accountFrame = null; accountNoticeText = null; accountControls.Clear();
            accountUsernameInput = accountPasswordInput = accountConfirmInput = accountRoomInput = null;
            if (browserFrame != null) browserFrame.SetActive(sleepRecoveryState == null && dialogueRecoveryState == null);
            if (browserPanel != null && refreshButton != null) browserPanel.firstSelected = refreshButton.gameObject;
            if (releaseInput && (dialogueRecoveryState == null || !IsOpen)) ReleaseAccountInputGuard();
        }

        private void AcquireAccountInputGuard()
        {
            if ((!accountActive && (dialogueRecoveryState == null || !IsOpen)) || browserPanel == null) return;
            if (accountGuardPanel != browserPanel)
            {
                ReleaseAccountInputGuard();
                accountGuardPanel = browserPanel;
                accountOriginalCancel = browserPanel.closeWithCancel;
                accountOriginalPause = browserPanel.closeWithPause;
            }
            // Native UIPanel cancel/pause callbacks do not inspect TMP focus.
            // These account modals use the explicit CANCEL control instead.
            browserPanel.closeWithCancel = false;
            browserPanel.closeWithPause = false;
            if (accountShortcutLock == null && PlayerInputManager.InstanceNotNull())
            {
                var shortcuts = PlayerInputManager.Instance.PreventAllMenuShortcuts;
                if (shortcuts != null) accountShortcutLock = shortcuts.Override(browserPanel);
            }
        }

        private void ReleaseAccountInputGuard()
        {
            var held = accountShortcutLock; accountShortcutLock = null;
            if (held != null) held.Release();
            if (accountGuardPanel != null)
            {
                accountGuardPanel.closeWithCancel = accountOriginalCancel;
                accountGuardPanel.closeWithPause = accountOriginalPause;
            }
            accountGuardPanel = null;
        }

        private void RememberAccountEndpoint(string endpoint)
        {
            accountEndpoint = AccountIdentityPins.NormalizeEndpoint(endpoint);
            ApplyAccountEndpoint();
        }

        private void ApplyAccountEndpoint()
        {
            if (accountEndpoint.Length == 0 || addressInput == null || portInput == null) return;
            var uri = new Uri("tcp://" + accountEndpoint);
            string host = uri.IdnHost.Trim('[', ']');
            addressInput.SetTextWithoutNotify(host);
            portInput.SetTextWithoutNotify(uri.Port.ToString());
            if (selected == null || !string.Equals(selected.Host, host, StringComparison.OrdinalIgnoreCase) || selected.Port != uri.Port)
            {
                selected = null; selectedId = "";
                if (nameInput != null) nameInput.SetTextWithoutNotify(host);
            }
            if (selectionText != null) selectionText.text = accountEndpoint;
        }

        private TMP_InputField Field(Transform parent, string caption, string initial, float x, float y, float width, int limit, bool secret)
        {
            Label(parent, caption, x, y, width, 21, 13, accent);
            var go = UiObject(caption, parent);
            go.SetActive(false);
            Place(go.GetComponent<RectTransform>(), x, y + 26, width, 37);
            var image = go.AddComponent<Image>(); image.color = fieldColor;
            var viewport = UiObject("Text Area", go.transform);
            Stretch(viewport.GetComponent<RectTransform>(), 9, 4, 9, 4);
            viewport.AddComponent<RectMask2D>();
            var text = Label(viewport.transform, "", 0, 0, width - 18, 29, 17);
            Stretch(text.rectTransform);
            text.enableWordWrapping = false;
            var input = go.AddComponent<TMP_InputField>();
            input.targetGraphic = image;
            input.textViewport = viewport.GetComponent<RectTransform>();
            input.textComponent = text;
            input.fontAsset = text.font;
            input.pointSize = 17;
            input.characterLimit = limit;
            input.lineType = TMP_InputField.LineType.SingleLine;
            input.richText = false;
            input.contentType = secret ? TMP_InputField.ContentType.Password : TMP_InputField.ContentType.Standard;
            input.customCaretColor = true; input.caretColor = accent;
            input.selectionColor = new Color(.2f, .6f, .65f, .45f);
            input.SetTextWithoutNotify(initial);
            go.SetActive(true);
            return input;
        }

        private Button ActionButton(Transform parent, string text, float x, float y, float width, float height, Action action)
        {
            var button = CloneNativeButton(buttonTemplate, parent, text, () =>
            {
                log?.Invoke("Multiplayer browser action: " + text);
                action?.Invoke();
            });
            button.name = text;
            var rect = button.GetComponent<RectTransform>();
            // LOAD's preferred-size fitters and child graphic bounds belong to
            // its title-menu layout. Browser actions have explicit rectangles;
            // preserve their native appearance but own their input geometry.
            var contentFitter = button.GetComponent<ContentSizeFitter>();
            if (contentFitter != null) contentFitter.enabled = false;
            var aspectFitter = button.GetComponent<AspectRatioFitter>();
            if (aspectFitter != null) aspectFitter.enabled = false;
            var element = button.GetComponent<LayoutElement>(); if (element != null) element.ignoreLayout = true;
            Place(rect, x, y, width, height);
            // Preserve every native graphic's enabled/color state. Some title
            // buttons include a disabled white backing image, so reusing or
            // enabling the root graphic changes their appearance.
            var nativeTarget = button.targetGraphic;
            foreach (var graphic in button.GetComponentsInChildren<Graphic>(true)) graphic.raycastTarget = false;
            // A graphic-only child receives raycasts across the explicit button
            // rectangle; Unity bubbles those events to the existing parent Button.
            // It can never become that Button's automatically selected root graphic.
            var hitArea = UiObject("MultiplayerHitArea", button.transform);
            hitArea.AddComponent<LayoutElement>().ignoreLayout = true;
            Stretch(hitArea.GetComponent<RectTransform>());
            var hitImage = hitArea.AddComponent<Image>();
            hitImage.color = Color.clear;
            hitImage.raycastTarget = true;
            hitImage.raycastPadding = Vector4.zero;
            hitImage.alphaHitTestMinimumThreshold = 0;
            hitImage.canvasRenderer.cullTransparentMesh = false;
            button.targetGraphic = nativeTarget;
            var group = button.GetComponent<CanvasGroup>();
            if (group != null) { group.interactable = true; group.blocksRaycasts = true; group.ignoreParentGroups = false; }
            button.enabled = true;
            button.interactable = true;
            // Native menu labels are otherwise sized for the title screen's larger buttons.
            foreach (var label in button.GetComponentsInChildren<TMP_Text>(true))
            { label.enableAutoSizing = true; label.fontSizeMin = 12; label.fontSizeMax = 20; }
            return button;
        }

        private Button PlainButton(Transform parent, string text, float x, float y, float width, float height, Action action)
        {
            var go = UiObject("Server", parent);
            Place(go.GetComponent<RectTransform>(), x, y, width, height);
            var image = go.AddComponent<Image>(); image.color = fieldColor;
            var button = go.AddComponent<Button>(); button.targetGraphic = image;
            button.colors = buttonTemplate.colors;
            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener((UnityAction)action);
            var label = Label(go.transform, text, 0, 0, width, height, 18);
            Stretch(label.rectTransform);
            return button;
        }

        private TextMeshProUGUI Label(Transform parent, string text, float x, float y, float width, float height, float size, Color? color = null)
        {
            var go = UiObject("Text", parent);
            Place(go.GetComponent<RectTransform>(), x, y, width, height);
            var label = go.AddComponent<TextMeshProUGUI>();
            if (styleText != null)
            {
                label.font = styleText.font;
                label.fontSharedMaterial = styleText.fontSharedMaterial;
                label.color = styleText.color;
                label.fontStyle = styleText.fontStyle;
            }
            if (color.HasValue) label.color = color.Value;
            label.fontSize = size;
            label.alignment = TextAlignmentOptions.MidlineLeft;
            label.enableWordWrapping = false;
            label.overflowMode = TextOverflowModes.Ellipsis;
            label.richText = false;
            label.raycastTarget = false;
            label.text = text;
            return label;
        }

        private void Line(Transform parent, float x, float y, float width)
        {
            var go = UiObject("Divider", parent);
            Place(go.GetComponent<RectTransform>(), x, y, width, 1);
            go.AddComponent<Image>().color = new Color(accent.r, accent.g, accent.b, .4f);
        }

        private static GameObject UiObject(string name, Transform parent)
        {
            var go = new GameObject(name);
            go.AddComponent<RectTransform>();
            go.transform.SetParent(parent, false);
            go.layer = parent.gameObject.layer;
            return go;
        }

        private static void Place(RectTransform rect, float x, float y, float width, float height)
        {
            rect.anchorMin = rect.anchorMax = new Vector2(0, 1);
            rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(width, height);
            rect.localScale = Vector3.one;
        }

        private static void Stretch(RectTransform rect, float left = 0, float top = 0, float right = 0, float bottom = 0)
        {
            rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(left, bottom); rect.offsetMax = new Vector2(-right, -top);
            rect.localScale = Vector3.one;
        }

        private void ClearStaleReferences()
        {
            if (mainMenu != null && multiplayerButton != null) return;
            ReleaseAccountInputGuard();
            if (open) { open = false; closed?.Invoke(); }
            if (browserRoot != null) Object.Destroy(browserRoot);
            if (multiplayerButton != null) Object.Destroy(multiplayerButton.gameObject);
            mainMenu = null; multiplayerButton = null; browserRoot = null; browserPanel = null;
            browserFrame = null; sleepRecoveryFrame = null;
            dialogueRecoveryFrame = null; dialogueRecoverySpeaker = dialogueRecoveryText = dialogueRecoveryProgress = null;
            dialogueRecoveryTextContent = dialogueRecoveryChoicesContent = null;
            dialogueRecoveryTextScroll = dialogueRecoveryChoicesScroll = null;
            dialogueRecoveryBack = dialogueRecoveryDisconnect = null; dialogueRecoveryButtons.Clear();
            dialogueRecoveryPresentation = ""; dialogueRecoveryAutoOpen = dialogueRecoveryState != null;
            sleepRecoveryTitle = null; sleepRecoveryProgress = null; sleepRecoverySummary = null;
            sleepRecoverySummaryContent = null; sleepRecoveryChoicesContent = null;
            sleepRecoverySummaryScroll = null; sleepRecoveryChoicesScroll = null;
            sleepRecoveryBack = null; sleepRecoveryDisconnect = null;
            sleepRecoveryButtons.Clear();
            sleepRecoveryGeneration++;
            sleepRecoveryPresentation = "";
            sleepRecoveryAutoOpen = sleepRecoveryState != null;
            rowButtons.Clear(); rowLabels.Clear(); shiftedButtons.Clear(); lastListSignature = "";
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            ClearAccountFlow();
            ClearDialogueRecovery();
            Hide();
            sleepRecoveryState = null;
            sleepRecoveryChoose = null;
            sleepRecoveryGeneration++;
            sleepRecoveryButtons.Clear();
            RestoreDisabledButtons();
            foreach (var session in sessionMenus)
            {
                foreach (var shifted in session.Shifted) if (shifted.Rect != null) shifted.Rect.anchoredPosition = shifted.Position;
                if (session.Original != null) session.Original.navigation = session.OriginalNavigation;
                if (session.Following != null) session.Following.navigation = session.FollowingNavigation;
                if (session.Disconnect != null) Object.Destroy(session.Disconnect.gameObject);
                if (session.Status != null) Object.Destroy(session.Status.gameObject);
            }
            sessionMenus.Clear();
            foreach (var shifted in shiftedButtons) if (shifted.Rect != null) shifted.Rect.anchoredPosition = shifted.Position;
            if (multiplayerButton != null)
            {
                var nav = multiplayerButton.navigation;
                if (buttonTemplate != null) { var n = buttonTemplate.navigation; n.selectOnDown = nav.selectOnDown; buttonTemplate.navigation = n; }
                if (nav.selectOnDown != null) { var n = nav.selectOnDown.navigation; n.selectOnUp = buttonTemplate; nav.selectOnDown.navigation = n; }
                Object.Destroy(multiplayerButton.gameObject);
            }
            if (browserRoot != null) Object.Destroy(browserRoot);
            rowButtons.Clear(); rowLabels.Clear(); shiftedButtons.Clear();
        }
    }
}
