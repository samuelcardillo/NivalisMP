using System;
using Nivalis;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace NivalisTogether.Dedicated
{
    /// <summary>Client presentation for session-directed map synchronization.
    /// Owns only its canvas and override tokens. All calls run on Unity's main thread.</summary>
    public static class MapCatchupOverlay
    {
        private static GameObject root;
        private static UIPanel panel;
        private static TextMeshProUGUI label;
        private static bool requested;
        private static PlayerCharacterController controller;
        private static PlayerInteraction interaction;
        private static PlayerInputManager input;
        private static OverrideableBool menuGate;
        private static OverrideableBool.OverrideLock movementLock, cameraLock, interactionLock, menuLock;

        public static bool IsVisible => requested && root != null && root.activeInHierarchy;
        public static int OwnedInputLockCount => Active(movementLock) + Active(cameraLock) + Active(interactionLock) + Active(menuLock);

        public static void SetVisible(bool value)
        {
            if (!value) { if (requested) Reset(); return; }
            if (requested) return;
            requested = true;
            Tick();
        }

        public static void Tick()
        {
            if (!requested) return;
            try
            {
                // A replaced scene/UI object must not leave tokens attached to
                // its old owner. Rebuild only while the session still requests it.
                if (root == null || panel == null || label == null)
                {
                    ReleaseInput();
                    DestroyView();
                    Create();
                }
                if (!root.activeSelf) root.SetActive(true);
                if (!panel.IsVisible) panel.SetVisibleImmediate();
                AcquireInput();
            }
            catch
            {
                Reset();
                throw;
            }
        }

        public static void Reset()
        {
            requested = false;
            ReleaseInput();
            DestroyView();
        }

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
            var nextGate = nextInput != null ? nextInput.PreventAllMenuShortcuts : null;
            if (!SameObject(input, nextInput) || menuGate?.Pointer != nextGate?.Pointer)
            {
                Release(ref menuLock); input = nextInput; menuGate = nextGate;
            }
            if (menuGate != null && (menuLock == null || menuLock.IsReleased)) menuLock = menuGate.Override(root);
        }

        private static void ReleaseInput()
        {
            Release(ref movementLock); Release(ref cameraLock); Release(ref interactionLock); Release(ref menuLock);
            controller = null; interaction = null; input = null; menuGate = null;
        }
        private static bool SameObject(Object left, Object right) =>
            left == null ? right == null : right != null && left.Pointer == right.Pointer;
        private static int Active(OverrideableBool.OverrideLock token) => token != null && !token.IsReleased ? 1 : 0;
        private static void Release(ref OverrideableBool.OverrideLock token)
        {
            var held = token; token = null;
            if (held == null) return;
            try { if (!held.IsReleased) held.Release(); }
            catch (Exception ex) { Debug.LogWarning("Map synchronization input cleanup: " + ex.Message); }
        }
        private static void DestroyView()
        {
            var ownedPanel = panel; var ownedRoot = root;
            root = null; panel = null; label = null;
            if (ownedPanel != null)
            {
                try { ownedPanel.SetInvisibleImmediate(); }
                catch (Exception ex) { Debug.LogWarning("Map synchronization panel cleanup: " + ex.Message); }
            }
            if (ownedRoot != null)
            {
                try { ownedRoot.SetActive(false); }
                catch (Exception ex) { Debug.LogWarning("Map synchronization canvas cleanup: " + ex.Message); }
                Object.Destroy(ownedRoot);
            }
        }

        private static void Create()
        {
            root = new GameObject("NivalisTogether.MapCatchup");
            root.SetActive(false); root.layer = 5;
            root.AddComponent<RectTransform>();
            Object.DontDestroyOnLoad(root);
            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.overrideSorting = true;
            canvas.sortingOrder = 32750; // Below the independently owned sleep overlay (32760).
            var scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = .5f;
            root.AddComponent<GraphicRaycaster>();
            var group = root.AddComponent<CanvasGroup>();
            group.alpha = 1; group.blocksRaycasts = true; group.interactable = false;
            var backdrop = root.AddComponent<Image>();
            backdrop.color = new Color(.015f, .025f, .04f, 1); backdrop.raycastTarget = true;

            var font = TMP_Settings.defaultFontAsset;
            Material material = null;
            if (font == null)
            {
                // One fallback lookup per canvas creation, never a per-frame scan.
                var existing = Object.FindObjectOfType<TMP_Text>(true);
                if (existing != null) { font = existing.font; material = existing.fontSharedMaterial; }
            }
            var text = new GameObject("Status"); text.layer = 5;
            var rect = text.AddComponent<RectTransform>();
            rect.SetParent(root.transform, false);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, .5f);
            rect.anchoredPosition = Vector2.zero; rect.sizeDelta = new Vector2(1100, 100); rect.localScale = Vector3.one;
            label = text.AddComponent<TextMeshProUGUI>();
            if (font != null) label.font = font;
            if (material != null) label.fontSharedMaterial = material;
            label.text = "Synchronizing world…";
            label.fontSize = 36; label.color = new Color(.65f, .9f, .95f, 1);
            label.alignment = TextAlignmentOptions.Center; label.enableWordWrapping = false;
            label.richText = false; label.raycastTarget = false;

            panel = root.AddComponent<UIPanel>(); panel._canvasGroup = group;
            panel._startVisible = true; panel.pauseTimeWhenOpen = false; panel.pauseTimeCompletely = false;
            panel.requiresMouse = false; panel.closeWithCancel = false; panel.closeWithPause = false;
            panel.rememberLastSelected = false;
        }
    }
}
