// PoncePuck.Keybinds/KeybindRunner.Chat.cs

using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;


namespace PoncePuck.Keybinds
{
    public sealed partial class KeybindRunner : MonoBehaviour
    {
        // cached UIChat instance & methods
        private UnityEngine.Object _uiChatObj;
        private bool _chatMethodsResolved;
        private MethodInfo _chatSend1; // (string)
        private MethodInfo _chatSend2; // (string,bool)

private void TrySendChat(string text)
{
    if (string.IsNullOrEmpty(text)) return;

    // B310: Chat sending moved to ChatManager
    try
    {
        var chatMgr = NetworkBehaviourSingleton<ChatManager>.Instance;
        if (chatMgr != null)
        {
            chatMgr.Client_SendChatMessage(text, false, false);
            return;
        }
    }
    catch { }

    // Fallback: try reflection on UIChat
    var ui = GetUIChatObj();
    if (ui == null) { Debug.LogWarning("[PPKB] UIChat not found."); return; }

    ResolveChatMethods(ui.GetType());
    try
    {
        if (_chatSend2 != null)      _chatSend2.Invoke(ui, new object[] { text, false });
        else if (_chatSend1 != null) _chatSend1.Invoke(ui, new object[] { text });
        else Debug.LogWarning("[PPKB] No compatible UIChat send method.");
    }
    catch (Exception e) { Debug.LogError("[PPKB] TrySendChat failed: " + e); }
}

        /// <summary>Shared access for other helpers (e.g., SafeEcho).</summary>
        private UnityEngine.Object GetUIChatObj()
        {
            if (_uiChatObj != null) return _uiChatObj;

            // Find UIChat type by name from any loaded assembly
            Type chatType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    chatType = asm.GetType("UIChat", false)
                           ?? asm.GetTypes().FirstOrDefault(t => t.Name == "UIChat" || t.FullName?.EndsWith(".UIChat") == true);
                    if (chatType != null) break;
                }
                catch { }
            }
            if (chatType == null) return null;

            try
            {
                var arr = Resources.FindObjectsOfTypeAll(chatType);
                if (arr != null && arr.Length > 0) _uiChatObj = arr[0] as UnityEngine.Object;
            }
            catch { }

            return _uiChatObj;
        }

        private void ResolveChatMethods(Type chatType)
        {
            if (_chatMethodsResolved && (_chatSend1 != null || _chatSend2 != null)) return;
            _chatSend1 = null; _chatSend2 = null;

            foreach (var m in chatType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var ps = m.GetParameters();

                // common explicit name
                if (m.Name == "Client_SendClientChatMessage")
                {
                    if (ps.Length == 1 && ps[0].ParameterType == typeof(string)) { _chatSend1 = m; continue; }
                    if (ps.Length == 2 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(bool)) { _chatSend2 = m; continue; }
                }

                // heuristic fallback
                if (_chatSend1 == null && ps.Length == 1 && ps[0].ParameterType == typeof(string) &&
                    m.Name.IndexOf("Send", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    m.Name.IndexOf("Chat", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _chatSend1 = m;
                }
                if (_chatSend2 == null && ps.Length == 2 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(bool) &&
                    m.Name.IndexOf("Send", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    m.Name.IndexOf("Chat", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _chatSend2 = m;
                }
            }

            _chatMethodsResolved = true;
        }

        // ---- small helpers kept so other partials compile unchanged ----
        private bool IsChatReady() => GetUIChatObj() != null;

        private System.Collections.IEnumerator PrefillRoutine(string text)
        {
            // Keep the signature so existing callers build; simple behavior:
            TrySendChat(text);
            yield break;
        }
    
        private IEnumerator PrefillRoutine(string raw, bool stripTrailing, char trailing)
        {
             Debug.Log($"[PPKB] PrefillRoutine started with: '{raw}'");
            if (!IsChatReady()) 
            {
                Debug.Log("[PPKB] Chat not ready, aborting prefill");
                yield break;
            }
            if (AnyTextInputFocused() || IsLikelyPaused()) 
            {
                Debug.Log("[PPKB] Text input focused or game paused, aborting prefill");
                yield break;
            }

            string text = raw.EndsWith(" ") ? raw : (raw + " ");
            Debug.Log($"[PPKB] Processed text: '{text}'");
            yield return new WaitForEndOfFrame();

            var ui = GetUIChatObj();
            if (ui == null) 
            {
                Debug.Log("[PPKB] UIChat object not found");
                yield break;
            }
            Debug.Log("[PPKB] UIChat object found, attempting prefill...");

            if (TryNS7Prefill(ui, text))
            {
                if (stripTrailing)
                {
                    yield return new WaitForSeconds(0.03f);
                    TryNS7StripTrailing(ui, trailing, text.Length);
                }
                yield break;
            }
            var comp = ui as Component;
            TMP_InputField tmp = comp != null ? comp.GetComponentInChildren<TMP_InputField>(true) : null;
            if (tmp != null)
            {
                ActivateTree(tmp.gameObject);
                tmp.interactable = true;
                tmp.enabled = true;
                tmp.text = text;
                tmp.caretPosition = text.Length;
                tmp.selectionAnchorPosition = tmp.selectionFocusPosition = text.Length;
                tmp.ActivateInputField();
                EventSystem.current?.SetSelectedGameObject(tmp.gameObject);

                if (stripTrailing)
                {
                    yield return new WaitForSeconds(0.03f);
                    if (tmp.text.Length == text.Length + 1 && tmp.text[tmp.text.Length - 1] == trailing)
                    {
                        tmp.text = tmp.text.Substring(0, tmp.text.Length - 1);
                        tmp.caretPosition = tmp.text.Length;
                        tmp.selectionAnchorPosition = tmp.selectionFocusPosition = tmp.text.Length;
                    }
                }
                yield break;
            }
            var old = comp != null ? comp.GetComponentInChildren<UnityEngine.UI.InputField>(true) : null;
            if (old != null)
            {
                ActivateTree(old.gameObject);
                old.interactable = true;
                old.enabled = true;
                old.text = text;
                old.caretPosition = text.Length;
                old.selectionAnchorPosition = old.selectionFocusPosition = text.Length;
                old.ActivateInputField();
                EventSystem.current?.SetSelectedGameObject(old.gameObject);

                if (stripTrailing)
                {
                    yield return new WaitForSeconds(0.03f);
                    if (old.text.Length == text.Length + 1 && old.text[old.text.Length - 1] == trailing)
                    {
                        old.text = old.text.Substring(0, old.text.Length - 1);
                        old.caretPosition = old.text.Length;
                        old.selectionAnchorPosition = old.selectionFocusPosition = old.text.Length;
                    }
                }
            }
        }

        // Reflection Invoke does not fill in C# default arguments, so the args array length
        // must equal the method's parameter count. b1117 changed UIChat.StartInput(bool) to
        // StartInput(bool isTeamChat, string openCharacter) - a 1-element array now throws
        // TargetParameterCountException. Size the array from the live signature so both the
        // old (1-param) and new (2-param) builds work.
        private static void InvokeStartInput(Type uiType, object ui)
        {
            var mi = uiType.GetMethod("StartInput", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (mi == null) return;
            int n = mi.GetParameters().Length;
            object[] args = n >= 2 ? new object[] { false, null } : (n == 1 ? new object[] { false } : new object[0]);
            try { mi.Invoke(ui, args); }
            catch (Exception e) { Debug.LogWarning("[PPKB] StartInput invoke failed: " + e.Message); }
        }

        private bool TryNS7Prefill(object ui, string text)
        {
            try
            {
                // B310 flow: ensure chat input is visible/active before writing text.
                var tUi = ui.GetType();
                tUi.GetMethod("Show", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(ui, null);
                InvokeStartInput(tUi, ui);
                tUi.GetMethod("ShowTextField", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(ui, null);
                tUi.GetMethod("Focus", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(ui, null);

                // Try known field names in order: B310 (textField), then legacy fallback (chatTextField).
                foreach (var fieldName in new[] { "textField", "chatTextField" })
                {
                    var f = tUi.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (f == null) continue;

                    var tf = f.GetValue(ui);
                    if (tf == null) continue;

                    if (TrySetAnyTextValue(tf, text))
                    {
                        return true;
                    }
                }

                return false;
            }
            catch (Exception e) { Debug.LogException(e); return false; }
        }

        private void TryNS7StripTrailing(object ui, char trailing, int expectedLen)
        {
            try
            {
                var tUi = ui.GetType();
                object tf = null;
                foreach (var fieldName in new[] { "textField", "chatTextField" })
                {
                    var f = tUi.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (f == null) continue;
                    tf = f.GetValue(ui);
                    if (tf != null) break;
                }
                if (tf == null) return;

                var cur = GetAnyTextValue(tf);
                if (cur.Length == expectedLen + 1 && cur[cur.Length - 1] == trailing)
                {
                    var fix = cur.Substring(0, cur.Length - 1);
                    TrySetAnyTextValue(tf, fix);
                }
            }
            catch (Exception e) { Debug.LogException(e); }
        }

        private static string GetAnyTextValue(object tf)
        {
            if (tf == null) return "";
            var t = tf.GetType();
            var valueProp = t.GetProperty("value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (valueProp != null)
            {
                var v = valueProp.GetValue(tf, null);
                return v as string ?? v?.ToString() ?? "";
            }

            var textProp = t.GetProperty("text", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (textProp != null)
            {
                var v = textProp.GetValue(tf, null);
                return v as string ?? v?.ToString() ?? "";
            }

            return "";
        }

        private static bool TrySetAnyTextValue(object tf, string text)
        {
            if (tf == null) return false;
            var t = tf.GetType();

            var valueProp = t.GetProperty("value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (valueProp != null && valueProp.CanWrite)
            {
                valueProp.SetValue(tf, text, null);
            }
            else
            {
                var textProp = t.GetProperty("text", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (textProp == null || !textProp.CanWrite) return false;
                textProp.SetValue(tf, text, null);
            }

            t.GetMethod("Focus", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(tf, null);
            t.GetMethod("Select", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(tf, null);

            var c1 = t.GetProperty("cursorIndex", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var c2 = t.GetProperty("selectIndex", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (c1 != null && c1.CanWrite) c1.SetValue(tf, text.Length, null);
            if (c2 != null && c2.CanWrite) c2.SetValue(tf, text.Length, null);

            return true;
        }

        private static void ActivateTree(GameObject go)
        {
            var t = go != null ? go.transform : null;
            while (t != null) { t.gameObject.SetActive(true); t = t.parent; }
        }

        // Public method for other mods to use (like LocalMute)
        public static bool OpenChatWithPrefill(string text)
        {
            try
            {
                Debug.Log($"[PPKB] OpenChatWithPrefill called with text: '{text}'");
                var instance = UnityEngine.Object.FindFirstObjectByType<KeybindRunner>(UnityEngine.FindObjectsInactive.Include);
                if (instance == null) 
                {
                    Debug.LogWarning("[PPKB] KeybindRunner instance not found");
                    return false;
                }
                
                Debug.Log("[PPKB] Starting forced prefill coroutine...");
                instance.StartCoroutine(instance.ForcedPrefillRoutine(text));
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError("[PPKB] OpenChatWithPrefill failed: " + e);
                return false;
            }
        }

        // Forced prefill routine that bypasses input focus checks (for external mod use)
        private IEnumerator ForcedPrefillRoutine(string raw)
        {
            Debug.Log($"[PPKB] ForcedPrefillRoutine started with: '{raw}'");
            
            // Wait a bit for scoreboard/menus to close
            yield return new WaitForSeconds(0.1f);
            
            // Only check if chat is ready, skip input focus checks
            if (!IsChatReady()) 
            {
                Debug.Log("[PPKB] Chat not ready, aborting forced prefill");
                yield break;
            }

            string text = raw.EndsWith(" ") ? raw : (raw + " ");
            Debug.Log($"[PPKB] Processed text: '{text}'");
            yield return new WaitForEndOfFrame();

            var ui = GetUIChatObj();
            if (ui == null) 
            {
                Debug.Log("[PPKB] UIChat object not found");
                yield break;
            }
            Debug.Log("[PPKB] UIChat object found, attempting forced prefill...");

            // Try to ensure chat is open first
            try
            {
                var chatType = ui.GetType();
                chatType.GetMethod("Show", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(ui, null);
                InvokeStartInput(chatType, ui);
                chatType.GetMethod("ShowTextField", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(ui, null);

                var openMethods = chatType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(m => m.Name.ToLowerInvariant().Contains("open") || m.Name.ToLowerInvariant().Contains("show"))
                    .Where(m => m.GetParameters().Length == 0);
                
                foreach (var method in openMethods)
                {
                    try 
                    { 
                        method.Invoke(ui, null); 
                        Debug.Log($"[PPKB] Called chat open method: {method.Name}");
                        break;
                    } 
                    catch { }
                }
            }
            catch (Exception e) { Debug.Log($"[PPKB] Failed to open chat: {e.Message}"); }

            // Small delay after opening chat
            yield return new WaitForSeconds(0.05f);

            if (TryNS7Prefill(ui, text))
            {
                Debug.Log("[PPKB] NS7 prefill successful");
                yield break;
            }
            
            // Fallback to TMP if NS7 fails
            var comp = ui as Component;
            TMP_InputField tmp = comp != null ? comp.GetComponentInChildren<TMP_InputField>(true) : null;
            if (tmp != null)
            {
                Debug.Log("[PPKB] Using TMP InputField fallback");
                ActivateTree(tmp.gameObject);
                tmp.interactable = true;
                tmp.enabled = true;
                tmp.text = text;
                tmp.caretPosition = text.Length;
                tmp.Select();
                yield break;
            }
            
            Debug.Log("[PPKB] All prefill methods failed");
        }

        // public reload used by /ppkb reload
        public void DoReload()
        {
            LoadConfigs();
            RebuildLookups();
            ResetInputActions();
            ApplyGameInputRebindings();
            SendHelloRoleAware();
            RefreshSuppression();
            EnsurePositionSelectHook();
        }
    }
}