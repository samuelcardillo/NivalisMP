using System;
using System.Collections.Generic;
using Nivalis;
using Nivalis.Dialogue;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Object = UnityEngine.Object;
using PlayerInputManager = Nivalis.PlayerInputManager;

namespace NivalisTogether.Dedicated
{
    // The same state machine serves TMP submit events and Update polling. Native
    // EventSystem order can differ, so an opening Enter or a duplicate callback
    // must never send a message or reopen the field on the closing frame.
    internal sealed class ChatInputLifecycle
    {
        internal bool IsTyping { get; private set; }
        internal bool HoldsClosingGuard { get; private set; }
        internal long Generation { get; private set; }
        internal bool HasSubmission { get; private set; }
        private int openedFrame = -1, closedFrame = -1, submittedFrame = -1;
        private string submission;
        internal bool Open(int frame)
        {
            if (IsTyping || HoldsClosingGuard || frame == closedFrame) return false;
            IsTyping = true; HasSubmission = false; submission = null;
            openedFrame = frame; submittedFrame = -1; Generation++; return true;
        }
        internal bool Submit(string text, int frame, long generation)
        {
            if (!IsTyping || frame <= openedFrame || frame <= submittedFrame || generation != Generation || HasSubmission) return false;
            HasSubmission = true; submittedFrame = frame; submission = text ?? ""; return true;
        }
        internal string TakeSubmission()
        {
            if (!HasSubmission) return null;
            HasSubmission = false; string value = submission; submission = null; return value;
        }
        internal void Close(int frame, bool protectClosingKey)
        {
            IsTyping = false; HasSubmission = false; submission = null;
            closedFrame = frame; HoldsClosingGuard = protectClosingKey; Generation++;
        }
        internal bool ReleaseClosingGuard(int frame, bool enterHeld, bool escapeHeld)
        {
            if (!HoldsClosingGuard || frame <= closedFrame || enterHeld || escapeHeld) return false;
            HoldsClosingGuard = false; return true;
        }
        internal void Stop()
        {
            IsTyping = HoldsClosingGuard = HasSubmission = false; submission = null;
            openedFrame = closedFrame = submittedFrame = -1; Generation++;
        }
    }

    /// <summary>Client-only chat UI. Every method runs on Unity's main thread.
    /// Call Tick(false) while gameplay is unavailable, and Stop on disconnect.</summary>
    public static class PlayerChatOverlay
    {
        private const int HistoryLimit = 100;
        private const string Help = "Enter: chat   /s local   /g global   /w name message   Esc: cancel";
        private static readonly ChatInputLifecycle typing = new ChatInputLifecycle();
        private static readonly List<HistoryEntry> history = new List<HistoryEntry>();
        private static readonly List<HistoryRow> historyRows = new List<HistoryRow>(HistoryLimit);
        private static readonly HashSet<string> received = new HashSet<string>(StringComparer.Ordinal);
        private static GameObject root, historyRoot, inputRoot;
        private static CanvasGroup historyGroup;
        private static UIPanel inputPanel;
        private static RectTransform historyRect, content;
        private static ScrollRect scroll;
        private static TMP_InputField field;
        private static TextMeshProUGUI hint;
        private static TMP_FontAsset font;
        private static Material fontMaterial;
        private static UnityAction<string> submitCallback;
        private static OverrideableBool.OverrideLock movementLock, cameraLock, interactionLock, menuLock;
        private static PlayerCharacterController controller;
        private static PlayerInteraction interaction;
        private static PlayerInputManager input;
        private static Action<ChatRequest> send;
        private static string localPlayerId = "";
        private static bool dirty, allowedLastTick;
        private static float lastMessageAt;
        private static int focusAfterFrame = -1;
        public static bool IsTyping => typing.IsTyping;
        public static int OwnedInputLockCount => Active(movementLock) + Active(cameraLock) + Active(interactionLock) + Active(menuLock);
        public static int HistoryCount => history.Count;

        public static void Configure(string playerId, Action<ChatRequest> sender)
        {
            Stop();
            localPlayerId = playerId ?? "";
            send = sender ?? throw new ArgumentNullException(nameof(sender));
        }

        public static void Receive(ChatMessage message)
        {
            if (send == null || message == null) return;
            ChatProtocol.ValidateMessage(message);
            if (!VisibleToLocal(message, localPlayerId) || received.Contains(message.MessageId)) return;
            string label;
            Color color;
            switch (message.Channel)
            {
                case "global": label = "[Global] " + message.SenderName; color = new Color(.63f, .82f, 1f, 1); break;
                case "whisper":
                    label = message.SenderPlayerId == localPlayerId ? "[To " + message.TargetName + "]" : "[From " + message.SenderName + "]";
                    color = new Color(.87f, .66f, 1f, 1); break;
                default: label = "[Local] " + message.SenderName; color = new Color(.69f, .96f, .87f, 1); break;
            }
            received.Add(message.MessageId);
            Append(new HistoryEntry { Id = message.MessageId, Text = Literal(label, 80) + ": " + Literal(message.Text, 512), Color = color });
        }

        public static void Reject(ChatRejection rejection)
        {
            if (send == null || rejection == null) return;
            ChatProtocol.ValidateRejection(rejection);
            Notice(rejection.Message ?? "The message could not be sent.");
        }

        internal static bool VisibleToLocal(ChatMessage message, string playerId) => message != null &&
            !string.IsNullOrEmpty(playerId) && (message.Channel != "whisper" ||
            message.SenderPlayerId == playerId || message.TargetPlayerId == playerId);

        public static void Tick(bool canType)
        {
            if (send == null) return;
            var keyboard = Keyboard.current;
            bool enterHeld = keyboard != null && (keyboard.enterKey.isPressed || keyboard.numpadEnterKey.isPressed);
            bool escapeHeld = keyboard != null && keyboard.escapeKey.isPressed;
            bool enterDown = keyboard != null && (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame);
            bool escapeDown = keyboard != null && keyboard.escapeKey.wasPressedThisFrame;
            bool allowed = canType && Application.isFocused && NativeUiAllowsChat();
            allowedLastTick = allowed;
            if (!allowed)
            {
                Close(false);
                if (root != null) root.SetActive(false);
                return;
            }
            if (root == null)
            {
                ReleaseInput(false); typing.Stop();
                Create();
            }
            root.SetActive(true);
            if (typing.ReleaseClosingGuard(Time.frameCount, enterHeld, escapeHeld)) Release(ref menuLock);
            if (!typing.IsTyping)
            {
                if (enterDown && typing.Open(Time.frameCount)) Open();
            }
            else
            {
                AcquireInput();
                // Preserve the menu token until a later released-key frame;
                // native menu shortcuts may update after this component.
                if (escapeDown) Close(true);
                else
                {
                    if (enterDown) typing.Submit(field.text, Time.frameCount, typing.Generation);
                    string submitted = typing.TakeSubmission();
                    if (submitted != null) Send(submitted);
                    else if (Time.frameCount > focusAfterFrame && !field.isFocused) Close(false);
                }
            }
            if (typing.IsTyping && Time.frameCount == focusAfterFrame)
            {
                field.Select(); field.ActivateInputField();
            }
            RenderHistory();
            historyRect.sizeDelta = new Vector2(620, typing.IsTyping ? 330 : 196);
            historyGroup.blocksRaycasts = historyGroup.interactable = typing.IsTyping;
            historyGroup.alpha = typing.IsTyping ? 1 : Mathf.Clamp01((18 - (Time.unscaledTime - lastMessageAt)) / 4);
            hint.text = Help;
            hint.color = new Color(.74f, .85f, .9f, typing.IsTyping ? 1 : .6f);
        }

        private static bool NativeUiAllowsChat()
        {
            if (LoadingScreenUI.showing || SleepLoadingOverlay.IsVisible) return false;
            if (GameSceneManager.InstanceNotNull() && (GameSceneManager.Instance.levelLoading || GameSceneManager.Instance._IsLoadingExternal_k__BackingField)) return false;
            if (GameSceneManager._IsUnloadingGameplay_k__BackingField) return false;
            if (PopupDialog.IsPopupVisible) return false;
            if (DialogueUI._instance != null && DialogueUI._instance.IsOpen) return false;
            if (UIWindowManager._instance?._openWindows?.Count > 0) return false;
            var manager = UIManager.InstanceNotNull() ? UIManager.Instance : null;
            if (manager != null)
            {
                var panels = manager._openPanels;
                for (int i = 0; i < Math.Min(panels?.Count ?? 0, 128); i++)
                {
                    var panel = panels[i];
                    if (panel == null || SameObject(panel, inputPanel) || !panel.IsVisible) continue;
                    if (panel.requiresMouse || panel.pauseTimeWhenOpen || panel.pauseTimeCompletely) return false;
                }
            }
            if (PlayerInputManager.InstanceNotNull() && PlayerInputManager.Instance.isRebind) return false;
            var selected = EventSystem.current?.currentSelectedGameObject;
            if (selected != null && (field == null || !SameObject(selected, field.gameObject)))
            {
                if (selected.GetComponentInParent<TMP_InputField>() != null || selected.GetComponentInParent<InputField>() != null) return false;
            }
            return true;
        }

        private static void Open()
        {
            inputRoot.SetActive(true);
            inputPanel.SetVisibleImmediate();
            AcquireInput();
            field.SetTextWithoutNotify("");
            long generation = typing.Generation;
            if (submitCallback != null) field.onSubmit.RemoveListener(submitCallback);
            submitCallback = Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<UnityAction<string>>((Action<string>)(text =>
            {
                if (!allowedLastTick || !Application.isFocused) return;
                // TMP submit may run before or after our Update. Captured open
                // generation makes delayed callbacks from another field inert.
                typing.Submit(text, Time.frameCount, generation);
            }));
            field.onSubmit.AddListener(submitCallback);
            focusAfterFrame = Time.frameCount + 1;
            field.Select(); field.ActivateInputField();
            if (UIManager.InstanceNotNull()) UIManager.Instance.UpdateCursorState();
            lastMessageAt = Time.unscaledTime;
            if (scroll != null) scroll.verticalNormalizedPosition = 0;
        }

        private static void Send(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) { Close(true); return; }
            if (!ChatProtocol.TryParseInput(text, out ChatRequest request, out string error))
            {
                Notice(error); focusAfterFrame = Time.frameCount + 1;
                field.Select(); field.ActivateInputField(); return;
            }
            // Clear and close before calling the transport: a synchronous
            // rejection or disconnect cannot leave typing locks behind.
            var callback = send;
            Close(true);
            try { callback?.Invoke(request); }
            catch (Exception) { Notice("The message could not be sent. Please try again."); }
        }

        private static void Close(bool protectKey)
        {
            // A frame without gameplay also cancels a pending close guard.
            if (!typing.IsTyping && !typing.HoldsClosingGuard) { ReleaseInput(false); return; }
            typing.Close(Time.frameCount, protectKey);
            focusAfterFrame = -1;
            if (field != null)
            {
                if (submitCallback != null) field.onSubmit.RemoveListener(submitCallback);
                submitCallback = null;
                field.DeactivateInputField(); field.SetTextWithoutNotify("");
            }
            var eventSystem = EventSystem.current;
            if (eventSystem != null && field != null && SameObject(eventSystem.currentSelectedGameObject, field.gameObject))
                eventSystem.SetSelectedGameObject(null);
            if (inputPanel != null) inputPanel.SetInvisibleImmediate();
            if (inputRoot != null) inputRoot.SetActive(false);
            ReleaseInput(protectKey);
        }

        public static void Stop()
        {
            Close(false); typing.Stop(); send = null; localPlayerId = ""; allowedLastTick = false;
            history.Clear(); received.Clear(); dirty = false; lastMessageAt = 0;
            if (root != null) { root.SetActive(false); Object.Destroy(root); }
            historyRows.Clear();
            root = historyRoot = inputRoot = null; inputPanel = null; field = null;
            historyGroup = null; historyRect = content = null; scroll = null; hint = null;
            font = null; fontMaterial = null;
        }

        private static void AcquireInput()
        {
            var character = PlayerManager.InstanceNotNull() ? PlayerManager.Instance._localPlayer?._character : null;
            var nextController = character?._controller;
            if (!SameObject(controller, nextController)) { Release(ref movementLock); Release(ref cameraLock); controller = nextController; }
            if (controller != null)
            {
                if (movementLock == null || movementLock.IsReleased) movementLock = controller.DisableMovement(inputRoot);
                if (cameraLock == null || cameraLock.IsReleased) cameraLock = controller.LockCamera(inputRoot);
            }
            var nextInteraction = character?._interaction;
            if (!SameObject(interaction, nextInteraction)) { Release(ref interactionLock); interaction = nextInteraction; }
            if (interaction != null && (interactionLock == null || interactionLock.IsReleased)) interactionLock = interaction.DisableInteractions(inputRoot);
            var nextInput = PlayerInputManager.InstanceNotNull() ? PlayerInputManager.Instance : null;
            if (!SameObject(input, nextInput)) { Release(ref menuLock); input = nextInput; }
            if (input?.PreventAllMenuShortcuts != null && (menuLock == null || menuLock.IsReleased)) menuLock = input.PreventAllMenuShortcuts.Override(inputRoot);
        }
        private static void ReleaseInput(bool keepMenuGuard)
        {
            Release(ref movementLock); Release(ref cameraLock); Release(ref interactionLock);
            controller = null; interaction = null;
            if (!keepMenuGuard) { Release(ref menuLock); input = null; }
        }
        private static int Active(OverrideableBool.OverrideLock token) => token != null && !token.IsReleased ? 1 : 0;
        private static bool SameObject(Object a, Object b) => a == null ? b == null : b != null && a.Pointer == b.Pointer;
        private static void Release(ref OverrideableBool.OverrideLock token)
        {
            var held = token; token = null;
            try { if (held != null && !held.IsReleased) held.Release(); }
            catch (Exception ex) { Debug.LogWarning("Chat input cleanup: " + ex.Message); }
        }

        private sealed class HistoryEntry { internal string Id = "", Text = ""; internal Color Color; }
        private sealed class HistoryRow
        {
            internal TextMeshProUGUI Text;
            internal RectTransform Rect;
            internal HistoryEntry Entry;
            internal float Height;
        }
        private static void Notice(string text) => Append(new HistoryEntry { Text = "[Chat] " + Literal(text, 512), Color = new Color(1, .8f, .55f, 1) });
        private static string Literal(string value, int length)
        {
            value = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
            return value.Length <= length ? value : value.Substring(0, length);
        }
        private static void Append(HistoryEntry entry)
        {
            history.Add(entry);
            if (history.Count > HistoryLimit)
            {
                if (history[0].Id.Length > 0) received.Remove(history[0].Id);
                history.RemoveAt(0);
                // Recycle the oldest row with the oldest entry. Existing rows
                // keep their cached text/layout when the 100-entry window moves.
                if (historyRows.Count == HistoryLimit)
                {
                    var oldest = historyRows[0]; historyRows.RemoveAt(0); historyRows.Add(oldest);
                }
            }
            dirty = true; lastMessageAt = Time.unscaledTime;
        }
        private static void RenderHistory()
        {
            if (!dirty || content == null) return;
            dirty = false;
            bool atBottom = !typing.IsTyping || scroll.verticalNormalizedPosition < .03f;
            // Grow a bounded pool once. Incoming messages never destroy TMP
            // objects; the entire pool belongs to the session's root canvas.
            while (historyRows.Count < history.Count)
            {
                var text = Label("Message", content, "", 20, Color.white);
                text.enableWordWrapping = true; text.overflowMode = TextOverflowModes.Overflow;
                var rect = text.rectTransform;
                rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
                historyRows.Add(new HistoryRow { Text = text, Rect = rect });
            }
            float height = 8;
            for (int i = 0; i < historyRows.Count; i++)
            {
                var row = historyRows[i];
                if (i >= history.Count) { if (row.Text.gameObject.activeSelf) row.Text.gameObject.SetActive(false); continue; }
                if (!row.Text.gameObject.activeSelf) row.Text.gameObject.SetActive(true);
                var entry = history[i];
                if (!ReferenceEquals(row.Entry, entry))
                {
                    row.Entry = entry; row.Text.text = entry.Text; row.Text.color = entry.Color;
                    row.Height = Mathf.Max(26, row.Text.GetPreferredValues(entry.Text, 594, 0).y + 4);
                    row.Rect.sizeDelta = new Vector2(594, row.Height);
                }
                row.Rect.anchoredPosition = new Vector2(10, -height);
                height += row.Height;
            }
            content.sizeDelta = new Vector2(0, height + 8);
            if (atBottom) { Canvas.ForceUpdateCanvases(); scroll.verticalNormalizedPosition = 0; }
        }

        private static void Create()
        {
            // A native scene teardown may have removed an old canvas before the
            // explicit disconnect path ran. Never retain its dead row wrappers.
            historyRows.Clear();
            root = new GameObject("NivalisTogether.PlayerChat"); root.SetActive(false); root.layer = 5;
            root.AddComponent<RectTransform>(); Object.DontDestroyOnLoad(root);
            var canvas = root.AddComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.overrideSorting = true; canvas.sortingOrder = 30000;
            var scaler = root.AddComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = .5f;
            root.AddComponent<GraphicRaycaster>();
            var texts = Object.FindObjectsOfType<TMP_Text>(true);
            for (int i = 0; i < Math.Min(texts.Length, 2048); i++)
                if (texts[i] != null && texts[i].font != null) { font = texts[i].font; fontMaterial = texts[i].fontSharedMaterial; break; }
            if (font == null) font = TMP_Settings.defaultFontAsset;

            historyRoot = UiObject("History", root.transform); historyRect = historyRoot.GetComponent<RectTransform>();
            BottomLeft(historyRect, 24, 128, 620, 196);
            historyRoot.AddComponent<Image>().color = new Color(.025f, .045f, .065f, .86f);
            historyGroup = historyRoot.AddComponent<CanvasGroup>();
            var viewport = UiObject("Viewport", historyRoot.transform); Stretch(viewport.GetComponent<RectTransform>());
            viewport.AddComponent<RectMask2D>();
            content = UiObject("Messages", viewport.transform).GetComponent<RectTransform>();
            content.anchorMin = new Vector2(0, 1); content.anchorMax = new Vector2(1, 1); content.pivot = new Vector2(.5f, 1);
            content.anchoredPosition = Vector2.zero; content.sizeDelta = Vector2.zero;
            scroll = historyRoot.AddComponent<ScrollRect>(); scroll.viewport = viewport.GetComponent<RectTransform>();
            scroll.content = content; scroll.horizontal = false; scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped; scroll.scrollSensitivity = 26;

            inputRoot = UiObject("ChatInput", root.transform); inputRoot.SetActive(false);
            BottomLeft(inputRoot.GetComponent<RectTransform>(), 24, 78, 620, 44);
            var background = inputRoot.AddComponent<Image>(); background.color = new Color(.04f, .09f, .12f, .98f);
            var area = UiObject("TextArea", inputRoot.transform); Stretch(area.GetComponent<RectTransform>(), 10, 6);
            area.AddComponent<RectMask2D>();
            var inputText = Label("Text", area.transform, "", 21, new Color(.94f, .97f, 1, 1)); Stretch(inputText.rectTransform);
            var placeholder = Label("Placeholder", area.transform, "Say something nearby...", 21, new Color(.6f, .7f, .75f, 1)); Stretch(placeholder.rectTransform);
            field = inputRoot.AddComponent<TMP_InputField>(); field.targetGraphic = background;
            field.textViewport = area.GetComponent<RectTransform>(); field.textComponent = inputText; field.placeholder = placeholder;
            field.fontAsset = inputText.font; field.pointSize = 21; field.characterLimit = ChatProtocol.MaxInputLength;
            field.lineType = TMP_InputField.LineType.SingleLine; field.richText = false;
            field.contentType = TMP_InputField.ContentType.Standard; field.customCaretColor = true;
            field.caretColor = new Color(.4f, .95f, 1, 1); field.selectionColor = new Color(.25f, .65f, .8f, .5f);
            inputPanel = inputRoot.AddComponent<UIPanel>(); inputPanel._canvasGroup = inputRoot.AddComponent<CanvasGroup>();
            inputPanel._startVisible = false; inputPanel.requiresMouse = true;
            inputPanel.pauseTimeWhenOpen = false; inputPanel.pauseTimeCompletely = false;
            inputPanel.closeWithCancel = false; inputPanel.closeWithPause = false; inputPanel.rememberLastSelected = false;
            hint = Label("Commands", root.transform, Help, 17, new Color(.74f, .85f, .9f, .6f));
            BottomLeft(hint.rectTransform, 24, 49, 650, 25);
            dirty = true;
        }
        private static GameObject UiObject(string name, Transform parent)
        {
            var result = new GameObject(name); result.layer = 5; result.AddComponent<RectTransform>(); result.transform.SetParent(parent, false); return result;
        }
        private static TextMeshProUGUI Label(string name, Transform parent, string text, float size, Color color)
        {
            var label = UiObject(name, parent).AddComponent<TextMeshProUGUI>();
            if (font != null) label.font = font; if (fontMaterial != null) label.fontSharedMaterial = fontMaterial;
            label.text = text; label.fontSize = size; label.color = color; label.alignment = TextAlignmentOptions.TopLeft;
            label.richText = false; label.enableWordWrapping = false; label.raycastTarget = false; return label;
        }
        private static void BottomLeft(RectTransform rect, float x, float y, float width, float height)
        {
            rect.anchorMin = rect.anchorMax = rect.pivot = Vector2.zero;
            rect.anchoredPosition = new Vector2(x, y); rect.sizeDelta = new Vector2(width, height); rect.localScale = Vector3.one;
        }
        private static void Stretch(RectTransform rect, float horizontal = 0, float vertical = 0)
        {
            rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(horizontal, vertical); rect.offsetMax = new Vector2(-horizontal, -vertical);
        }
    }
}
