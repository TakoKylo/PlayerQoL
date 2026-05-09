// Chord capture overlay & routine
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UITK = UnityEngine.UIElements;
using System.Linq;

namespace PoncePuck.Keybinds
{
    public sealed partial class KeybindRunner
    {
        private UITK.VisualElement _captureOverlay;
        private UITK.Label _captureLabel;
        private Action<string> _onChordCaptured;
        private bool _isCapturing;
        private bool _panelHiddenForCapture;

        private void EnsureCaptureOverlay(UITK.VisualElement root)
        {
            var host = (root as UITK.VisualElement) ?? _doc?.rootVisualElement ?? _lastRoot;
            if (host == null) return;

            // If we already have an overlay, keep it but ensure it's attached to the active host.
            if (_captureOverlay != null)
            {
                if (_captureOverlay.parent != host)
                {
                    _captureOverlay.RemoveFromHierarchy();
                    host.Add(_captureOverlay);
                    AttachLast(host, _captureOverlay);
                }
                _captureOverlay.BringToFront();
                return;
            }

            _captureOverlay = new UITK.VisualElement();
            _captureOverlay.style.position = UITK.Position.Absolute;
            _captureOverlay.style.left = 0; _captureOverlay.style.right = 0;
            _captureOverlay.style.top = 0; _captureOverlay.style.bottom = 0;
            _captureOverlay.style.backgroundColor = new Color(0.1f, 0.1f, 0.15f, 0.95f); // Dark blue-gray like base game
            _captureOverlay.style.display = UITK.DisplayStyle.None;
            _captureOverlay.style.flexShrink = 0; // Prevent flexing
            _captureOverlay.style.flexGrow = 0;   // Prevent flexing
            _captureOverlay.pickingMode = UITK.PickingMode.Position;
            ForceUIFont(_captureOverlay);

            // Container for centered content
            var centerContainer = new UITK.VisualElement();
            centerContainer.style.position = UITK.Position.Absolute;
            centerContainer.style.left = new UITK.Length(50, UITK.LengthUnit.Percent);
            centerContainer.style.top = new UITK.Length(50, UITK.LengthUnit.Percent);
            centerContainer.style.translate = new UITK.Translate(
                new UITK.Length(-50, UITK.LengthUnit.Percent),
                new UITK.Length(-50, UITK.LengthUnit.Percent), 0);
            centerContainer.style.alignItems = UITK.Align.Center;
            centerContainer.style.justifyContent = UITK.Justify.Center;
            centerContainer.style.flexDirection = UITK.FlexDirection.Column;
            centerContainer.style.flexShrink = 0; // Prevent flexing
            centerContainer.style.flexGrow = 0;   // Prevent flexing

            // Main title (like base game)
            var title = new UITK.Label("KEY REBIND");
            title.style.fontSize = 72;
            title.style.color = Color.white;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
            title.style.marginBottom = 32;
            title.style.flexShrink = 0; // Prevent flexing
            ForceUIFont(title);
            centerContainer.Add(title);

            // Instruction text
            _captureLabel = new UITK.Label("Press a key or combination of keys to rebind.");
            _captureLabel.style.fontSize = 24;
            _captureLabel.style.color = new Color(0.9f, 0.9f, 0.9f, 1f);
            _captureLabel.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
            _captureLabel.style.whiteSpace = UITK.WhiteSpace.Normal;
            _captureLabel.style.maxWidth = 600;
            _captureLabel.style.flexShrink = 0; // Prevent flexing
            ForceUIFont(_captureLabel);
            centerContainer.Add(_captureLabel);

            _captureOverlay.Add(centerContainer);
            host.Add(_captureOverlay);
            AttachLast(host, _captureOverlay);

            _captureOverlay.BringToFront();
        }

        private void StartChordCapture(string prompt, Action<string> onCaptured)
        {
            _onChordCaptured = onCaptured;
            _isCapturing = true;

            var attachRoot = (_blockScope as UITK.VisualElement) ?? ChooseAttachRoot();
            EnsureCaptureOverlay(attachRoot);

            if (_captureOverlay == null)
            {
                // Do not hide the panel if capture overlay could not be created.
                Debug.LogWarning("[PPKB] Capture overlay was not created; aborting bind capture.");
                _isCapturing = false;
                _onChordCaptured = null;
                return;
            }

            HidePanelDuringCapture(true);
            ForceCleanupUI();
            // Note: Menu buttons already hidden by ModMenuHub

            if (_captureLabel != null) _captureLabel.text = "Press a key or combination of modifier + key to rebind.";
            _captureOverlay.style.display = UITK.DisplayStyle.Flex;
            _captureOverlay.BringToFront();
            StartCoroutine(CaptureChordRoutine());
        }

        private void CancelChordCapture()
        {
            _isCapturing = false;
            if (_captureOverlay != null) _captureOverlay.style.display = UITK.DisplayStyle.None;
            HidePanelDuringCapture(false); // Restore panel visibility
            _onChordCaptured = null;
            
            // Keep the panel state active - don't call ForceCleanupUI()
            // Ensure cursor stays visible and mouse active
            var uiState = GlobalStateManager.UIState;
            uiState.IsMouseRequired = true;
            GlobalStateManager.UIState = uiState;
            UnityEngine.Cursor.lockState = CursorLockMode.None;
            UnityEngine.Cursor.visible = true;
            
            // Buttons remain hidden - UpdateButtonVisibility will handle showing them when appropriate
        }

        private static bool IsModifier(KeyCode k) =>
            k == KeyCode.LeftShift || k == KeyCode.RightShift ||
            k == KeyCode.LeftControl || k == KeyCode.RightControl ||
            k == KeyCode.LeftAlt || k == KeyCode.RightAlt;

        private static IEnumerable<KeyCode> AllBindableKeys()
        {
            foreach (KeyCode k in Enum.GetValues(typeof(KeyCode)))
                if (IsAllowedKey(k)) yield return k;
        }

        private KeyChord SnapshotChord()
        {
            bool ctrl = (UnityEngine.InputSystem.Keyboard.current?.leftCtrlKey?.isPressed ?? false) || (UnityEngine.InputSystem.Keyboard.current?.rightCtrlKey?.isPressed ?? false);
            bool shift = (UnityEngine.InputSystem.Keyboard.current?.leftShiftKey?.isPressed ?? false) || (UnityEngine.InputSystem.Keyboard.current?.rightShiftKey?.isPressed ?? false);
            bool alt = (UnityEngine.InputSystem.Keyboard.current?.leftAltKey?.isPressed ?? false) || (UnityEngine.InputSystem.Keyboard.current?.rightAltKey?.isPressed ?? false);

            var keys = new List<KeyCode>();
            foreach (var k in AllBindableKeys())
            {
                if (IsModifier(k)) continue;
                if (IsKeyDown(k)) keys.Add(k);
            }
            keys.Sort((a, b) => a.CompareTo(b));
            return new KeyChord { Keys = keys.ToArray(), Ctrl = ctrl, Shift = shift, Alt = alt };
        }

        private static string KeyChordToSpec(KeyChord kc)
        {
            var sb = new System.Text.StringBuilder();
            if (kc.Ctrl) sb.Append("Ctrl+");
            if (kc.Shift) sb.Append("Shift+");
            if (kc.Alt) sb.Append("Alt+");
            if (kc.Keys != null && kc.Keys.Length > 0)
                sb.Append(string.Join("+", kc.Keys.Select(k => GetFriendlyKeyName(k))));
            return sb.ToString();
        }
        
        private static string GetFriendlyKeyName(KeyCode k)
        {
            switch (k)
            {
                case KeyCode.Mouse0: return "LMB";
                case KeyCode.Mouse1: return "RMB";
                case KeyCode.Mouse2: return "MMB";
                case KeyCode.Mouse3: return "MB4";
                case KeyCode.Mouse4: return "MB5";
                default: return k.ToString();
            }
        }

        private IEnumerator CaptureChordRoutine()
        {
            var uiState = GlobalStateManager.UIState;
            uiState.IsMouseRequired = true;
            GlobalStateManager.UIState = uiState;
            UnityEngine.Cursor.lockState = CursorLockMode.None;
            UnityEngine.Cursor.visible = true;

            var kb = UnityEngine.InputSystem.Keyboard.current;
            var mouse = UnityEngine.InputSystem.Mouse.current;

            float startTimeout = Time.unscaledTime + 5f;
            bool started = false;
            float windowEnd = 2f;

            KeyChord best = default;
            int bestWeight = -1;
            float lastBestAt = 0f;

            bool HasAnyInputDown() =>
                (kb != null && kb.anyKey.isPressed) ||
                (mouse != null && (
                    mouse.leftButton.isPressed || mouse.rightButton.isPressed || mouse.middleButton.isPressed ||
                    (mouse.forwardButton?.isPressed ?? false) || (mouse.backButton?.isPressed ?? false)));

            int Weight(KeyChord kc)
            {
                int w = (kc.Keys?.Length ?? 0);
                if (kc.Ctrl) w++;
                if (kc.Shift) w++;
                if (kc.Alt) w++;
                return w;
            }

            while (_isCapturing && Time.unscaledTime < startTimeout)
            {
                if (kb?.escapeKey?.wasPressedThisFrame ?? false) { CancelChordCapture(); yield break; }

                if (!started)
                {
                    if ((kb?.anyKey.wasPressedThisFrame ?? false) ||
                        (mouse?.leftButton.wasPressedThisFrame ?? false) ||
                        (mouse?.rightButton.wasPressedThisFrame ?? false) ||
                        (mouse?.middleButton.wasPressedThisFrame ?? false) ||
                        (mouse?.forwardButton?.wasPressedThisFrame ?? false) == true ||
                        (mouse?.backButton?.wasPressedThisFrame ?? false) == true)
                    {
                        started = true;
                        windowEnd = Time.unscaledTime + 1.0f;
                        if (_captureLabel != null) _captureLabel.text = "Release keys to confirm binding...";
                    }
                }
                else
                {
                    var kc = SnapshotChord();
                    bool any = (kc.Keys?.Length ?? 0) > 0 || kc.Ctrl || kc.Shift || kc.Alt;
                    if (any)
                    {
                        int w = Weight(kc);
                        if (w > bestWeight)
                        {
                            best = kc; bestWeight = w; lastBestAt = Time.unscaledTime;
                            if (_captureLabel != null) _captureLabel.text = KeyChordToSpec(kc) + " - Release to confirm";
                        }
                    }

                    bool allReleased = !HasAnyInputDown();

                    if (bestWeight >= 0 && (allReleased || Time.unscaledTime >= windowEnd || Time.unscaledTime - lastBestAt > 0.15f))
                    {
                        _onChordCaptured?.Invoke(KeyChordToSpec(best));
                        _isCapturing = false;
                        if (_captureOverlay != null) _captureOverlay.style.display = UITK.DisplayStyle.None;
                        HidePanelDuringCapture(false);
                        yield break;
                    }
                }

                yield return null;
            }

            CancelChordCapture();
        }

        private void HidePanelDuringCapture(bool hide)
        {
            if (_ppkbPanel == null) return;

            if (hide)
            {
                _panelHiddenForCapture = (_ppkbPanel.style.display == UITK.DisplayStyle.Flex);
                _ppkbPanel.style.display = UITK.DisplayStyle.None;

                if (_ppkbBackdrop != null) _ppkbBackdrop.style.display = UITK.DisplayStyle.Flex;
            }
            else
            {
                if (_panelHiddenForCapture)
                {
                    _ppkbPanel.style.display = UITK.DisplayStyle.Flex;
                    _panelHiddenForCapture = false;
                }
            }
        }

       private void ForceCleanupUI()
            {
                var root = _blockScope ?? GetAttachRoot(_doc?.rootVisualElement ?? _lastRoot);
                if (root == null) return;

                // ensure overlays are hidden
                if (_captureOverlay != null) _captureOverlay.style.display = UITK.DisplayStyle.None;
                if (_ppkbBackdrop != null) _ppkbBackdrop.style.display = UITK.DisplayStyle.None;

                // ensure pickability is restored
                MaskSiblingsForClicks(root, false);
                // Note: Menu buttons managed by ModMenuHub
            }
    }
}
