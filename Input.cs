// PoncePuck.Keybinds/KeybindRunner.Input.cs

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace PoncePuck.Keybinds
{
    public sealed partial class KeybindRunner
    {
        // Only keep command and prefill input handling
        private readonly Dictionary<KeyChord, string> _chordToCommand = new Dictionary<KeyChord, string>();
        private readonly Dictionary<KeyChord, string> _chordToPrefill = new Dictionary<KeyChord, string>();
        
        // InputAction management
        private readonly List<InputAction> _allIA = new List<InputAction>();

        // Only rebuild lookups for commands and prefills
        private void RebuildLookups()
        {
            _chordToCommand.Clear();
            _chordToPrefill.Clear();

            // commands/prefills (non-role)
            for (int i = 0; i < _cmd.extraCommands.Count; i++)
            {
                if (!ParseCommandEntryMulti(_cmd.extraCommands[i], out var cm, out var specs)) continue;
                for (int j = 0; j < specs.Count; j++)
                    if (TryParseChord(specs[j], out var kc))
                        _chordToCommand[kc] = cm;
            }
            for (int i = 0; i < _cmd.prefills.Count; i++)
            {
                if (!ParsePrefillEntryMulti(_cmd.prefills[i], out var text, out var specs)) continue;
                if (!text.EndsWith(" ")) text += " ";
                for (int j = 0; j < specs.Count; j++)
                    if (TryParseChord(specs[j], out var kc2))
                        _chordToPrefill[kc2] = text;
            }
        }

        private void BindActionList(List<string> list, string action, Dictionary<KeyChord, List<string>> target)
        {
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                var spec = list[i];
                KeyChord kc;
                if (!TryParseChord(spec, out kc)) continue;
                
                // Safely add to dictionary to prevent conflicts
                try
                {
                    List<string> a;
                    if (!target.TryGetValue(kc, out a)) { a = new List<string>(); target[kc] = a; }
                    if (!a.Contains(action)) a.Add(action);
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[PPKB] Failed to bind {spec} to {action}: {ex.Message}");
                }
            }
        }

        // ---- focus/pausing ----
        private static bool AnyTextInputFocused()
        {
            try
            {
                // Check game's UIChat focus (UI Toolkit TextField, not detected by EventSystem)
                var chat = MonoBehaviourSingleton<UIManager>.Instance?.Chat;
                if (chat != null && chat.IsFocused) return true;

                var es = EventSystem.current; if (es == null) return false;
                var go = es.currentSelectedGameObject; if (go == null) return false;

                var tmp = go.GetComponent<TMPro.TMP_InputField>(); if (tmp != null && tmp.isFocused) return true;
                var old = go.GetComponent<UnityEngine.UI.InputField>(); if (old != null && old.isFocused) return true;
            }
            catch { }
            return false;
        }
        private static bool IsLikelyPaused()
        {
            if (Time.timeScale <= 0f) return true;
            if (UnityEngine.Cursor.visible && UnityEngine.Cursor.lockState != CursorLockMode.Locked) return true;
            return false;
        }
        private static bool ShouldBlockBinds()
        {
            if (AnyTextInputFocused()) return true;

            try
            {
                // Use cached instance instead of expensive FindFirstObjectByType
                var inst = _instance;
                if (inst != null)
                {
                    // Check both style and resolvedStyle for accurate visibility
                    if (inst._ppkbPanel != null && 
                        (inst._ppkbPanel.style.display == UnityEngine.UIElements.DisplayStyle.Flex ||
                         inst._ppkbPanel.resolvedStyle.display == UnityEngine.UIElements.DisplayStyle.Flex)) return true;
                    if (inst._isCapturing) return true;
                }
            }
            catch { }

            return IsLikelyPaused();
        }

        // ---- state helpers ----
        private static bool ModsSatisfied(KeyChord kc)
        {
            var kb = Keyboard.current;
            if (kb == null) return !kc.Ctrl && !kc.Shift && !kc.Alt;

            bool ctrl = (kb.leftCtrlKey != null && kb.leftCtrlKey.isPressed) || (kb.rightCtrlKey != null && kb.rightCtrlKey.isPressed);
            bool shift = (kb.leftShiftKey != null && kb.leftShiftKey.isPressed) || (kb.rightShiftKey != null && kb.rightShiftKey.isPressed);
            bool alt = (kb.leftAltKey != null && kb.leftAltKey.isPressed) || (kb.rightAltKey != null && kb.rightAltKey.isPressed);

            return (!kc.Ctrl || ctrl) && (!kc.Shift || shift) && (!kc.Alt || alt);
        }

        private static bool IsKeyDown(KeyCode k)
        {
            var kb = Keyboard.current;
            if (kb == null) return false;

            // Letters
            if (k >= KeyCode.A && k <= KeyCode.Z)
            {
                int idx = (int)k - (int)KeyCode.A;
                var key = kb[(UnityEngine.InputSystem.Key)((int)UnityEngine.InputSystem.Key.A + idx)];
                return key != null && key.isPressed;
            }

            // Function keys
            if (k >= KeyCode.F1 && k <= KeyCode.F12)
            {
                int n = (int)k - (int)KeyCode.F1 + 1;
                var key = kb[UnityEngine.InputSystem.Key.F1 + (n - 1)];
                return key != null && key.isPressed;
            }

            switch (k)
            {
                case KeyCode.Space: return kb.spaceKey?.isPressed ?? false;
                case KeyCode.Tab: return kb.tabKey?.isPressed ?? false;
                case KeyCode.Escape: return kb.escapeKey?.isPressed ?? false;

                case KeyCode.LeftShift: return kb.leftShiftKey?.isPressed ?? false;
                case KeyCode.RightShift: return kb.rightShiftKey?.isPressed ?? false;
                case KeyCode.LeftControl: return kb.leftCtrlKey?.isPressed ?? false;
                case KeyCode.RightControl: return kb.rightCtrlKey?.isPressed ?? false;
                case KeyCode.LeftAlt: return kb.leftAltKey?.isPressed ?? false;
                case KeyCode.RightAlt: return kb.rightAltKey?.isPressed ?? false;

                case KeyCode.UpArrow: return kb.upArrowKey?.isPressed ?? false;
                case KeyCode.DownArrow: return kb.downArrowKey?.isPressed ?? false;
                case KeyCode.LeftArrow: return kb.leftArrowKey?.isPressed ?? false;
                case KeyCode.RightArrow: return kb.rightArrowKey?.isPressed ?? false;

                case KeyCode.BackQuote: return kb.backquoteKey?.isPressed ?? false;
                case KeyCode.Minus: return kb.minusKey?.isPressed ?? false;
                case KeyCode.Equals: return kb.equalsKey?.isPressed ?? false;
                case KeyCode.LeftBracket: return kb.leftBracketKey?.isPressed ?? false;
                case KeyCode.RightBracket: return kb.rightBracketKey?.isPressed ?? false;
                case KeyCode.Semicolon: return kb.semicolonKey?.isPressed ?? false;
                case KeyCode.Quote: return kb.quoteKey?.isPressed ?? false;
                case KeyCode.Comma: return kb.commaKey?.isPressed ?? false;
                case KeyCode.Period: return kb.periodKey?.isPressed ?? false;
                case KeyCode.Slash: return kb.slashKey?.isPressed ?? false;
                case KeyCode.Backslash: return kb.backslashKey?.isPressed ?? false;

                case KeyCode.Alpha0: return kb.digit0Key?.isPressed ?? false;
                case KeyCode.Alpha1: return kb.digit1Key?.isPressed ?? false;
                case KeyCode.Alpha2: return kb.digit2Key?.isPressed ?? false;
                case KeyCode.Alpha3: return kb.digit3Key?.isPressed ?? false;
                case KeyCode.Alpha4: return kb.digit4Key?.isPressed ?? false;
                case KeyCode.Alpha5: return kb.digit5Key?.isPressed ?? false;
                case KeyCode.Alpha6: return kb.digit6Key?.isPressed ?? false;
                case KeyCode.Alpha7: return kb.digit7Key?.isPressed ?? false;
                case KeyCode.Alpha8: return kb.digit8Key?.isPressed ?? false;
                case KeyCode.Alpha9: return kb.digit9Key?.isPressed ?? false;

                case KeyCode.Keypad0: return kb.numpad0Key?.isPressed ?? false;
                case KeyCode.Keypad1: return kb.numpad1Key?.isPressed ?? false;
                case KeyCode.Keypad2: return kb.numpad2Key?.isPressed ?? false;
                case KeyCode.Keypad3: return kb.numpad3Key?.isPressed ?? false;
                case KeyCode.Keypad4: return kb.numpad4Key?.isPressed ?? false;
                case KeyCode.Keypad5: return kb.numpad5Key?.isPressed ?? false;
                case KeyCode.Keypad6: return kb.numpad6Key?.isPressed ?? false;
                case KeyCode.Keypad7: return kb.numpad7Key?.isPressed ?? false;
                case KeyCode.Keypad8: return kb.numpad8Key?.isPressed ?? false;
                case KeyCode.Keypad9: return kb.numpad9Key?.isPressed ?? false;
            }

            return Mouse.current != null && (
                   (k == KeyCode.Mouse0 && Mouse.current.leftButton.isPressed) ||
                   (k == KeyCode.Mouse1 && Mouse.current.rightButton.isPressed) ||
                   (k == KeyCode.Mouse2 && Mouse.current.middleButton.isPressed) ||
                   (k == KeyCode.Mouse3 && (Mouse.current.forwardButton?.isPressed ?? false)) ||
                   (k == KeyCode.Mouse4 && (Mouse.current.backButton?.isPressed ?? false)));
        }

        private static bool ChordStillHeld(KeyChord kc)
        {
            if (!ModsSatisfied(kc)) return false;
            for (int i = 0; i < kc.Keys.Length; i++)
                if (!IsKeyDown(kc.Keys[i])) return false;
            return true;
        }

        private void ResetInputActions()
        {
            ClearInputActions();

            var all = new HashSet<KeyChord>();
            foreach (var k in _chordToCommand.Keys) all.Add(k);
            foreach (var k in _chordToPrefill.Keys) all.Add(k);

            foreach (var kc in all)
            {
                // modifier-only chords
                if (kc.Keys.Length == 0 && (kc.Ctrl || kc.Shift || kc.Alt))
                {
                    var modPaths = new List<string>();
                    if (kc.Ctrl) { modPaths.Add("<Keyboard>/leftCtrl"); modPaths.Add("<Keyboard>/rightCtrl"); }
                    if (kc.Shift) { modPaths.Add("<Keyboard>/leftShift"); modPaths.Add("<Keyboard>/rightShift"); }
                    if (kc.Alt) { modPaths.Add("<Keyboard>/leftAlt"); modPaths.Add("<Keyboard>/rightAlt"); }

                    foreach (var path in modPaths.Distinct())
                    {
                        var ia = new InputAction(type: InputActionType.Button, binding: path);

                        ia.performed += _ =>
                        {
                            if (ShouldBlockBinds()) return;
                            if (!ChordStillHeld(kc)) return;

                            // Commands/prefills only
                            if (_chordToCommand.TryGetValue(kc, out var cmd) && IsChatReady()) TrySendChat(cmd);
                            if (_chordToPrefill.TryGetValue(kc, out var pre) && !AnyTextInputFocused() && !IsLikelyPaused() && IsChatReady())
                                StartCoroutine(PrefillRoutine(pre, true, GuessTrailingKeyChar(kc)));
                        };

                        ia.Enable();
                        _allIA.Add(ia);
                    }
                    continue;
                }

                // chords with non-modifier keys
                for (int keyIdx = 0; keyIdx < kc.Keys.Length; keyIdx++)
                {
                    if (!TryGetInputPath(kc.Keys[keyIdx], out var path)) continue;

                    var ia = new InputAction(type: InputActionType.Button, binding: path);

                    ia.performed += _ =>
                    {
                        if (ShouldBlockBinds()) return;
                        if (!ChordStillHeld(kc)) return;

                        if (_chordToCommand.TryGetValue(kc, out var cmd) && IsChatReady()) TrySendChat(cmd);
                        if (_chordToPrefill.TryGetValue(kc, out var pre) && !AnyTextInputFocused() && !IsLikelyPaused() && IsChatReady())
                            StartCoroutine(PrefillRoutine(pre, true, GuessTrailingKeyChar(kc)));
                    };

                    ia.Enable();
                    _allIA.Add(ia);
                }
            }

            Debug.Log($"[PPKB] Input wired. IAs={_allIA.Count} chords={all.Count} cmds={_chordToCommand.Count} prefills={_chordToPrefill.Count}");
        }

        private void ClearInputActions()
        {
            for (int i = 0; i < _allIA.Count; i++)
            {
                var ia = _allIA[i];
                try { ia.Disable(); } catch { }
                try { ia.Dispose(); } catch { }
            }
            _allIA.Clear();
        }

        // no-op hook to keep game InputActionAsset pristine
        private void ApplyGameInputRebindings() { /* intentionally empty */ }
    }
}
