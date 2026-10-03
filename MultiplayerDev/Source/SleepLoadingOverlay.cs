using System;
using System.Collections.Generic;
using Nivalis;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace NivalisTogether.Dedicated
{
    /// <summary>A client-only presentation for the shared sleep barrier. It owns
    /// its canvas and input locks; it never changes time or hides native loading UI.
    /// All calls must run on Unity's main thread.</summary>
    public static class SleepLoadingOverlay
    {
        private static GameObject root;
        private static UIPanel panel;
        private static TextMeshProUGUI heading, description;
        private static RectTransform progress;
        private static TMP_FontAsset font;
        private static Material fontMaterial;
        private static OverrideableBool.OverrideLock movementLock, cameraLock, interactionLock, menuLock;
        private static PlayerCharacterController controller;
        private static PlayerInteraction interaction;
        private static PlayerInputManager input;
        private static readonly List<Button> buttons = new List<Button>();
        private static readonly List<UnityAction> nativeCallbacks = new List<UnityAction>();
        private static Action[] actions = Array.Empty<Action>();
        private static string[] captions = Array.Empty<string>();
        private static string currentChoiceIdentity = "";
        private static int generation;
        private static bool visible, actionPending;
        private static float animationStarted;
        public static bool IsVisible => visible && root != null && root.activeInHierarchy;
        public static string Title => heading == null ? "" : heading.text;
        public static string Detail => description == null ? "" : description.text;
        public static int OwnedInputLockCount => Active(movementLock) + Active(cameraLock) + Active(interactionLock) + Active(menuLock);
        public static bool HasOwnedInputLocks => OwnedInputLockCount > 0;

        public static void Set(string title, string detail, string[] labels = null, Action[] callbacks = null, string choiceIdentity = "")
        {
            labels = labels ?? Array.Empty<string>();
            callbacks = callbacks ?? Array.Empty<Action>();
            if (title == null || title.Length > 160 || detail == null || detail.Length > 3000 ||
                labels.Length > 16 || callbacks.Length != labels.Length || choiceIdentity == null || choiceIdentity.Length > 256)
                throw new ArgumentException("Invalid shared sleep presentation.");
            for (int i = 0; i < labels.Length; i++)
                if (string.IsNullOrWhiteSpace(labels[i]) || labels[i].Length > 100 || callbacks[i] == null)
                    throw new ArgumentException("Invalid shared sleep choice.");

            bool creating = root == null;
            if (creating)
            {
                // A scene teardown may destroy native objects before disconnect
                // reaches Stop. Release only our old tokens before rebuilding.
                Hide();
                Create();
            }
            heading.text = title;
            description.text = detail;
            bool choicesChanged = ChoicePresentationChanged(currentChoiceIdentity, choiceIdentity, captions, labels);
            actions = (Action[])callbacks.Clone();
            if (creating || choicesChanged) RebuildChoices(labels);
            currentChoiceIdentity = choiceIdentity;
            if (!visible)
            {
                visible = true;
                animationStarted = Time.unscaledTime;
                root.SetActive(true);
                panel.SetVisibleImmediate();
            }
            panel.requiresMouse = labels.Length > 0;
            if ((creating || choicesChanged) && UIManager.InstanceNotNull()) UIManager.Instance.UpdateCursorState();
            AcquireInput();
            Animate();
        }

        public static void Tick()
        {
            if (!visible) return;
            if (root == null) { Hide(); return; }
            // Native informational windows can close while this barrier is still
            // active. Our independent canvas remains opaque throughout that work.
            if (!root.activeSelf) root.SetActive(true);
            if (panel != null && !panel.IsVisible) panel.SetVisibleImmediate();
            AcquireInput();
            Animate();
        }

        // A server denial reopens the current choices while the same opaque
        // canvas and all owned input locks remain in place.
        public static void RetryActions()
        {
            if (!visible) return;
            actionPending = false;
            foreach (var button in buttons) if (button != null) button.interactable = true;
        }

        public static void Hide()
        {
            visible = actionPending = false;
            generation++;
            actions = Array.Empty<Action>();
            captions = Array.Empty<string>();
            currentChoiceIdentity = "";
            Release(ref movementLock);
            Release(ref cameraLock);
            Release(ref interactionLock);
            Release(ref menuLock);
            controller = null; interaction = null; input = null;
            // The native panel lifecycle relinquishes its own cursor registration;
            // never clear global cursor flags or somebody else's override owners.
            if (panel != null)
            {
                try { panel.SetInvisibleImmediate(); }
                catch (Exception ex) { Debug.LogWarning("Shared sleep panel cleanup: " + ex.Message); }
            }
            if (root != null) { root.SetActive(false); Object.Destroy(root); }
            root = null; panel = null; heading = description = null; progress = null;
            font = null; fontMaterial = null; buttons.Clear(); nativeCallbacks.Clear();
        }
        public static void Reset() => Hide();

        private static void AcquireInput()
        {
            var character = PlayerManager.InstanceNotNull() ? PlayerManager.Instance._localPlayer?._character : null;
            var nextController = character?._controller;
            if (!SameObject(controller, nextController))
            {
                Release(ref movementLock); Release(ref cameraLock); controller = nextController;
            }
            if (controller != null)
            {
                if (movementLock == null || movementLock.IsReleased) movementLock = controller.DisableMovement(root);
                if (cameraLock == null || cameraLock.IsReleased) cameraLock = controller.LockCamera(root);
            }
            var nextInteraction = character?._interaction;
            if (!SameObject(interaction, nextInteraction)) { Release(ref interactionLock); interaction = nextInteraction; }
            if (interaction != null && (interactionLock == null || interactionLock.IsReleased))
                interactionLock = interaction.DisableInteractions(root);
            var nextInput = PlayerInputManager.InstanceNotNull() ? PlayerInputManager.Instance : null;
            if (!SameObject(input, nextInput)) { Release(ref menuLock); input = nextInput; }
            if (input?.PreventAllMenuShortcuts != null && (menuLock == null || menuLock.IsReleased))
                menuLock = input.PreventAllMenuShortcuts.Override(root);
        }

        private static bool SameObject(Object left, Object right) =>
            left == null ? right == null : right != null && left.Pointer == right.Pointer;
        private static int Active(OverrideableBool.OverrideLock token) => token != null && !token.IsReleased ? 1 : 0;
        private static void Release(ref OverrideableBool.OverrideLock token)
        {
            var held = token; token = null;
            if (held == null) return;
            try { if (!held.IsReleased) held.Release(); }
            catch (Exception ex) { Debug.LogWarning("Shared sleep input cleanup: " + ex.Message); }
        }

        private static void Create()
        {
            root = new GameObject("NivalisTogether.SleepLoading");
            root.SetActive(false);
            root.layer = 5;
            root.AddComponent<RectTransform>();
            Object.DontDestroyOnLoad(root);
            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.overrideSorting = true;
            canvas.sortingOrder = 32760;
            var scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = .5f;
            root.AddComponent<GraphicRaycaster>();
            var group = root.AddComponent<CanvasGroup>();
            group.alpha = 1; group.blocksRaycasts = true; group.interactable = true;
            var backdrop = root.AddComponent<Image>();
            backdrop.color = new Color(.015f, .025f, .04f, 1);
            backdrop.raycastTarget = true;

            // Use an already loaded game font, including its fallback characters.
            var texts = Object.FindObjectsOfType<TMP_Text>(true);
            for (int i = 0; i < Math.Min(texts.Length, 2048); i++)
                if (texts[i] != null && texts[i].font != null)
                { font = texts[i].font; fontMaterial = texts[i].fontSharedMaterial; break; }
            if (font == null) font = TMP_Settings.defaultFontAsset;
            heading = Label("Title", root.transform, "", 0, 165, 1100, 80, 46, new Color(.52f, .91f, .97f, 1));
            description = Label("Detail", root.transform, "", 0, 35, 1100, 150, 26, new Color(.85f, .9f, .94f, 1));
            description.enableWordWrapping = true;
            description.enableAutoSizing = true;
            description.fontSizeMin = 16; description.fontSizeMax = 26;
            var track = UiObject("LoadingTrack", root.transform);
            Place(track.GetComponent<RectTransform>(), 0, -78, 500, 4);
            track.AddComponent<Image>().color = new Color(.14f, .24f, .29f, 1);
            var indicator = UiObject("LoadingIndicator", track.transform);
            progress = indicator.GetComponent<RectTransform>();
            Place(progress, 0, 0, 120, 4);
            indicator.AddComponent<Image>().color = new Color(.42f, .88f, .96f, 1);

            panel = root.AddComponent<UIPanel>();
            panel._canvasGroup = group;
            panel._startVisible = true;
            panel.pauseTimeWhenOpen = false;
            panel.pauseTimeCompletely = false;
            panel.requiresMouse = false;
            panel.closeWithCancel = false;
            panel.closeWithPause = false;
            panel.rememberLastSelected = false;
        }

        private static void Animate()
        {
            if (progress == null) return;
            float position = Mathf.Sin((Time.unscaledTime - animationStarted) * 1.7f) * 190;
            progress.anchoredPosition = new Vector2(position, 0);
        }

        // Choice identity comes from the authoritative epoch/step, not its label:
        // summary and ranking may both say Continue but authorize different actions.
        internal static bool ChoicePresentationChanged(string previousIdentity, string nextIdentity, string[] previousCaptions, string[] nextCaptions)
        {
            if (!string.Equals(previousIdentity, nextIdentity, StringComparison.Ordinal) || previousCaptions.Length != nextCaptions.Length) return true;
            for (int i = 0; i < nextCaptions.Length; i++) if (nextCaptions[i] != previousCaptions[i]) return true;
            return false;
        }
        private static void RebuildChoices(string[] labels)
        {
            generation++;
            actionPending = false;
            int expectedGeneration = generation;
            foreach (var button in buttons) if (button != null)
            { button.gameObject.SetActive(false); Object.Destroy(button.gameObject); }
            buttons.Clear(); nativeCallbacks.Clear();
            captions = (string[])labels.Clone();
            int columns = Math.Min(4, labels.Length);
            for (int i = 0; i < labels.Length; i++)
            {
                int choice = i;
                int row = i / columns, column = i % columns;
                int rowCount = Math.Min(columns, labels.Length - row * columns);
                var go = UiObject("Choice " + (i + 1), root.transform);
                Place(go.GetComponent<RectTransform>(), (column - (rowCount - 1) * .5f) * 270, -155 - row * 68, 250, 54);
                var surface = go.AddComponent<Image>(); surface.color = new Color(.08f, .2f, .25f, 1);
                var button = go.AddComponent<Button>(); button.targetGraphic = surface;
                var colors = button.colors;
                colors.normalColor = Color.white;
                colors.highlightedColor = new Color(.6f, 1, 1, 1);
                colors.pressedColor = new Color(.35f, .7f, .8f, 1);
                colors.selectedColor = colors.highlightedColor;
                colors.disabledColor = new Color(.5f, .5f, .5f, 1);
                button.colors = colors;
                button.onClick = new Button.ButtonClickedEvent();
                UnityAction callback = (UnityAction)(Action)(() => InvokeChoice(choice, expectedGeneration));
                nativeCallbacks.Add(callback); button.onClick.AddListener(callback);
                Label("Caption", go.transform, labels[i], 0, 0, 230, 48, 21, Color.white);
                buttons.Add(button);
            }
            panel.firstSelected = buttons.Count > 0 ? buttons[0].gameObject : null;
            if (visible && panel.firstSelected != null) panel.ForceSelect(panel.firstSelected);
        }
        private static void InvokeChoice(int index, int expectedGeneration)
        {
            if (!visible || actionPending || generation != expectedGeneration || index < 0 || index >= actions.Length) return;
            var callback = actions[index];
            actionPending = true;
            foreach (var button in buttons) if (button != null) button.interactable = false;
            try { callback(); }
            catch (Exception ex)
            {
                if (!visible || generation != expectedGeneration) return;
                RetryActions();
                description.text = ex.Message.Length <= 300 ? ex.Message : ex.Message.Substring(0, 300);
                Debug.LogWarning("Shared sleep choice: " + ex.Message);
            }
        }
        private static GameObject UiObject(string name, Transform parent)
        {
            var value = new GameObject(name);
            value.AddComponent<RectTransform>(); value.layer = 5;
            value.transform.SetParent(parent, false);
            return value;
        }
        private static void Place(RectTransform rect, float x, float y, float width, float height)
        {
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, .5f);
            rect.anchoredPosition = new Vector2(x, y);
            rect.sizeDelta = new Vector2(width, height);
            rect.localScale = Vector3.one;
        }
        private static TextMeshProUGUI Label(string name, Transform parent, string text, float x, float y, float width, float height, float size, Color color)
        {
            var go = UiObject(name, parent);
            Place(go.GetComponent<RectTransform>(), x, y, width, height);
            var label = go.AddComponent<TextMeshProUGUI>();
            if (font != null) label.font = font;
            if (fontMaterial != null) label.fontSharedMaterial = fontMaterial;
            label.text = text; label.fontSize = size; label.color = color;
            label.alignment = TextAlignmentOptions.Center;
            label.enableWordWrapping = false; label.richText = false;
            label.overflowMode = TextOverflowModes.Ellipsis;
            label.raycastTarget = false;
            return label;
        }
    }
}
