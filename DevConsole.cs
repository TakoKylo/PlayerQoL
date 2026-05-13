// In-game developer console for PlayerQoL.
//
// When `enableDevConsole` is set, pressing backtick (`) toggles a movable,
// resizable window with a live log feed, level filters, search box, and a
// command input. Commands either drive the mod (set/get/toggle config),
// query game state (players, version, fps), or forward to game chat.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.UIElements;
using UITK = UnityEngine.UIElements;

namespace PoncePuck.Keybinds
{
    public sealed class DevConsole : MonoBehaviour
    {
        public static DevConsole Instance { get; private set; }

        // ---- Captured log buffer ----------------------------------------
        private struct Entry
        {
            public LogType Level;
            public string Time;
            public string Text;
        }

        private const int MAX_ENTRIES = 600;
        private readonly Queue<Entry> _entries = new Queue<Entry>(MAX_ENTRIES);
        private readonly ConcurrentQueue<Entry> _pending = new ConcurrentQueue<Entry>();
        private readonly List<string> _history = new List<string>();
        private int _historyCursor = -1;

        // Filter state
        private bool _showInfo = true;
        private bool _showWarn = true;
        private bool _showError = true;
        private string _search = "";

        // ---- UI -----------------------------------------------------------
        private VisualElement _root;
        private VisualElement _panel;
        private VisualElement _titleBar;
        private VisualElement _resizeGrip;
        private ScrollView _scroll;
        private VisualElement _logRoot;
        private TextField _inputField;
        private TextField _searchField;
        private Toggle _toggleInfo, _toggleWarn, _toggleError;
        private bool _open;
        private bool _backtickWasPressed;

        // Drag/resize state
        private bool _draggingMove, _draggingResize;
        private Vector2 _dragStart;
        private Rect _dragOriginRect;

        public bool IsOpen => _open;

        public static DevConsole AttachTo(GameObject host)
        {
            if (Instance != null) return Instance;
            return host.AddComponent<DevConsole>();
        }

        private void Awake()
        {
            Instance = this;
            Application.logMessageReceivedThreaded += OnLogReceived;
        }

        private void OnDestroy()
        {
            try { Application.logMessageReceivedThreaded -= OnLogReceived; } catch { }
            DetachUI();
            if (Instance == this) Instance = null;
        }

        private void OnLogReceived(string message, string stackTrace, LogType type)
        {
            _pending.Enqueue(new Entry
            {
                Level = type,
                Time = DateTime.Now.ToString("HH:mm:ss"),
                Text = message ?? "",
            });
        }

        private void Update()
        {
            // Drain background-captured logs into the visible buffer.
            bool added = false;
            while (_pending.TryDequeue(out var e))
            {
                if (_entries.Count >= MAX_ENTRIES) _entries.Dequeue();
                _entries.Enqueue(e);
                added = true;
            }
            if (added && _open && _logRoot != null) RefreshLogList();

            var cfg = KeybindRunner.Instance?.CommandConfig;
            if (cfg == null || !cfg.enableDevConsole) { if (_open) Close(); return; }

            var kb = Keyboard.current;
            if (kb == null) return;

            bool pressed = ((ButtonControl)kb.backquoteKey).isPressed;
            if (pressed && !_backtickWasPressed)
            {
                if (!IsOurFieldFocused() && IsForeignTextInputFocused()) { _backtickWasPressed = pressed; return; }
                Toggle();
            }
            _backtickWasPressed = pressed;

            if (_open && _inputField != null && _inputField.focusController?.focusedElement == _inputField)
            {
                if (((ButtonControl)kb.upArrowKey).wasPressedThisFrame) StepHistory(-1);
                else if (((ButtonControl)kb.downArrowKey).wasPressedThisFrame) StepHistory(1);
            }
        }

        private static bool IsForeignTextInputFocused()
        {
            try
            {
                var ui = MonoBehaviourSingleton<UIManager>.Instance;
                if (ui != null && ui.Chat != null && ui.Chat.IsFocused) return true;
            }
            catch { }
            var es = UnityEngine.EventSystems.EventSystem.current;
            var go = es != null ? es.currentSelectedGameObject : null;
            if (go == null) return false;
            if (go.GetComponent<TMPro.TMP_InputField>()?.isFocused ?? false) return true;
            if (go.GetComponent<UnityEngine.UI.InputField>()?.isFocused ?? false) return true;
            return false;
        }

        private bool IsOurFieldFocused()
        {
            if (!_open) return false;
            var fc = _inputField?.focusController;
            return fc != null && (fc.focusedElement == _inputField || fc.focusedElement == _searchField);
        }

        public void Toggle() { if (_open) Close(); else Open(); }

        public void Open()
        {
            if (_open) return;
            EnsureUI();
            if (_panel == null) return;
            _panel.style.display = DisplayStyle.Flex;
            _open = true;
            RefreshLogList();
            _inputField?.Focus();
        }

        public void Close()
        {
            if (!_open) return;
            if (_panel != null) _panel.style.display = DisplayStyle.None;
            _open = false;
            _inputField?.Blur();
        }

        // ---- UI build -----------------------------------------------------
        private void EnsureUI()
        {
            if (_panel != null) return;

            var ui = MonoBehaviourSingleton<UIManager>.Instance;
            _root = ui?.UIDocument?.rootVisualElement;
            if (_root == null) return;

            var cfg = KeybindRunner.Instance?.CommandConfig;
            float x = cfg?.devConsoleX ?? 40f;
            float y = cfg?.devConsoleY ?? 40f;
            float w = Mathf.Max(420f, cfg?.devConsoleW ?? 900f);
            float h = Mathf.Max(220f, cfg?.devConsoleH ?? 460f);

            _panel = new VisualElement
            {
                name = "PPKB_DevConsole",
                style =
                {
                    position = Position.Absolute,
                    left = x, top = y, width = w, height = h,
                    backgroundColor = new StyleColor(new Color(0.05f, 0.05f, 0.05f, 0.94f)),
                    flexDirection = FlexDirection.Column,
                    borderTopWidth = 1, borderBottomWidth = 1, borderLeftWidth = 1, borderRightWidth = 1,
                    borderTopColor = new StyleColor(new Color(1f,1f,1f,0.18f)),
                    borderBottomColor = new StyleColor(new Color(1f,1f,1f,0.18f)),
                    borderLeftColor = new StyleColor(new Color(1f,1f,1f,0.18f)),
                    borderRightColor = new StyleColor(new Color(1f,1f,1f,0.18f)),
                    display = DisplayStyle.None,
                }
            };

            // Title bar (drag to move)
            _titleBar = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row, alignItems = Align.Center,
                    height = 28, flexShrink = 0,
                    backgroundColor = new StyleColor(new Color(0.13f, 0.13f, 0.13f, 1f)),
                    paddingLeft = 8, paddingRight = 4,
                }
            };
            _titleBar.Add(MakeLabel("DEV CONSOLE", 14, FontStyle.Bold, new Color(0.9f, 0.9f, 0.9f)));
            var spacer = new VisualElement { style = { flexGrow = 1 } };
            _titleBar.Add(spacer);
            var closeX = new Button(Close) { text = "X" };
            closeX.style.width = 24; closeX.style.height = 22;
            closeX.style.marginLeft = 4; closeX.style.marginRight = 0;
            closeX.style.backgroundColor = new StyleColor(new Color(0.6f, 0.15f, 0.15f, 1f));
            closeX.style.color = Color.white;
            closeX.style.unityFontStyleAndWeight = FontStyle.Bold;
            _titleBar.Add(closeX);

            _titleBar.RegisterCallback<PointerDownEvent>(OnTitlePointerDown);
            _titleBar.RegisterCallback<PointerMoveEvent>(OnTitlePointerMove);
            _titleBar.RegisterCallback<PointerUpEvent>(OnTitlePointerUp);
            _panel.Add(_titleBar);

            // Body
            var body = new VisualElement { style = { flexDirection = FlexDirection.Column, flexGrow = 1, paddingTop = 6, paddingBottom = 6, paddingLeft = 8, paddingRight = 8 } };

            // Filter row + search
            var header = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 6, flexShrink = 0 } };
            _toggleInfo = MakeFilterToggle("INFO", _showInfo, v => { _showInfo = v; RefreshLogList(); }, new Color(0.85f,0.85f,0.85f));
            _toggleWarn = MakeFilterToggle("WARN", _showWarn, v => { _showWarn = v; RefreshLogList(); }, new Color(1f, 0.85f, 0.4f));
            _toggleError = MakeFilterToggle("ERROR", _showError, v => { _showError = v; RefreshLogList(); }, new Color(1f, 0.45f, 0.45f));
            header.Add(_toggleInfo); header.Add(_toggleWarn); header.Add(_toggleError);
            header.Add(new VisualElement { style = { flexGrow = 1 } });

            header.Add(MakeLabel("SEARCH", 13, FontStyle.Normal, new Color(0.8f,0.8f,0.8f)));
            _searchField = new TextField { value = "" };
            _searchField.label = "";
            _searchField.style.width = 240; _searchField.style.height = 24;
            StyleField(_searchField);
            _searchField.RegisterValueChangedCallback(ev => { _search = ev.newValue ?? ""; RefreshLogList(); });
            header.Add(_searchField);
            body.Add(header);

            // Log scroll
            _scroll = new ScrollView(ScrollViewMode.Vertical)
            {
                style =
                {
                    flexGrow = 1,
                    backgroundColor = new StyleColor(new Color(0f, 0f, 0f, 0.45f)),
                    paddingTop = 4, paddingBottom = 4, paddingLeft = 6, paddingRight = 6,
                }
            };
            _logRoot = _scroll.contentContainer;
            body.Add(_scroll);

            // Input row
            var inputRow = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginTop = 6, flexShrink = 0 } };
            var prompt = MakeLabel(">", 18, FontStyle.Bold, new Color(0.5f, 1f, 0.5f));
            prompt.style.marginRight = 6;
            inputRow.Add(prompt);

            _inputField = new TextField { value = "" };
            _inputField.label = "";
            _inputField.style.flexGrow = 1; _inputField.style.height = 28;
            StyleField(_inputField);
            _inputField.RegisterCallback<NavigationSubmitEvent>(_ => SubmitInput(), TrickleDown.TrickleDown);
            inputRow.Add(_inputField);

            var sendBtn = new Button(SubmitInput) { text = "SEND" };
            sendBtn.style.height = 28; sendBtn.style.width = 80; sendBtn.style.marginLeft = 6;
            sendBtn.style.unityFontStyleAndWeight = FontStyle.Bold;
            inputRow.Add(sendBtn);

            body.Add(inputRow);
            _panel.Add(body);

            // Resize grip (bottom-right corner)
            _resizeGrip = new VisualElement
            {
                name = "PPKB_DevConsoleGrip",
                style =
                {
                    position = Position.Absolute,
                    right = 0, bottom = 0,
                    width = 18, height = 18,
                    backgroundColor = new StyleColor(new Color(1f, 1f, 1f, 0.25f)),
                }
            };
            _resizeGrip.RegisterCallback<PointerDownEvent>(OnGripPointerDown);
            _resizeGrip.RegisterCallback<PointerMoveEvent>(OnGripPointerMove);
            _resizeGrip.RegisterCallback<PointerUpEvent>(OnGripPointerUp);
            _panel.Add(_resizeGrip);

            _root.Add(_panel);
            _panel.BringToFront();
        }

        private void DetachUI()
        {
            try { _panel?.RemoveFromHierarchy(); } catch { }
            _panel = null; _root = null; _scroll = null; _logRoot = null;
            _inputField = null; _searchField = null;
            _toggleInfo = _toggleWarn = _toggleError = null;
            _titleBar = null; _resizeGrip = null;
        }

        // ---- Drag / resize ------------------------------------------------
        private void OnTitlePointerDown(PointerDownEvent evt)
        {
            if (evt.button != 0) return;
            _draggingMove = true;
            _dragStart = evt.position;
            _dragOriginRect = new Rect(_panel.style.left.value.value, _panel.style.top.value.value, _panel.layout.width, _panel.layout.height);
            _titleBar.CapturePointer(evt.pointerId);
            evt.StopPropagation();
        }
        private void OnTitlePointerMove(PointerMoveEvent evt)
        {
            if (!_draggingMove) return;
            Vector2 delta = (Vector2)evt.position - _dragStart;
            float nx = Mathf.Max(0, _dragOriginRect.x + delta.x);
            float ny = Mathf.Max(0, _dragOriginRect.y + delta.y);
            _panel.style.left = nx;
            _panel.style.top = ny;
        }
        private void OnTitlePointerUp(PointerUpEvent evt)
        {
            if (!_draggingMove) return;
            _draggingMove = false;
            _titleBar.ReleasePointer(evt.pointerId);
            PersistRect();
        }

        private void OnGripPointerDown(PointerDownEvent evt)
        {
            if (evt.button != 0) return;
            _draggingResize = true;
            _dragStart = evt.position;
            _dragOriginRect = new Rect(_panel.style.left.value.value, _panel.style.top.value.value, _panel.layout.width, _panel.layout.height);
            _resizeGrip.CapturePointer(evt.pointerId);
            evt.StopPropagation();
        }
        private void OnGripPointerMove(PointerMoveEvent evt)
        {
            if (!_draggingResize) return;
            Vector2 delta = (Vector2)evt.position - _dragStart;
            float nw = Mathf.Max(420f, _dragOriginRect.width + delta.x);
            float nh = Mathf.Max(220f, _dragOriginRect.height + delta.y);
            _panel.style.width = nw;
            _panel.style.height = nh;
        }
        private void OnGripPointerUp(PointerUpEvent evt)
        {
            if (!_draggingResize) return;
            _draggingResize = false;
            _resizeGrip.ReleasePointer(evt.pointerId);
            PersistRect();
        }

        private void PersistRect()
        {
            try
            {
                var cfg = KeybindRunner.Instance?.CommandConfig;
                if (cfg == null) return;
                cfg.devConsoleX = _panel.style.left.value.value;
                cfg.devConsoleY = _panel.style.top.value.value;
                cfg.devConsoleW = _panel.layout.width;
                cfg.devConsoleH = _panel.layout.height;
            }
            catch { }
        }

        // ---- Helpers ------------------------------------------------------
        private static Label MakeLabel(string text, int size, FontStyle weight, Color color)
        {
            return new Label(text)
            {
                style =
                {
                    color = color,
                    fontSize = size,
                    unityFontStyleAndWeight = weight,
                    marginRight = 6,
                }
            };
        }

        private static Toggle MakeFilterToggle(string label, bool initial, Action<bool> onChange, Color labelColor)
        {
            var t = new Toggle(label) { value = initial };
            t.RegisterValueChangedCallback(ev => onChange(ev.newValue));
            t.style.marginRight = 10;
            var lab = t.Q<Label>(className: "unity-toggle__label");
            if (lab != null) { lab.style.color = labelColor; lab.style.unityFontStyleAndWeight = FontStyle.Bold; lab.style.fontSize = 13; }
            return t;
        }

        private static void StyleField(TextField tf)
        {
            tf.style.backgroundColor = new StyleColor(new Color(0.12f, 0.12f, 0.12f, 1f));
            tf.style.color = Color.white;
            var input = tf.childCount > 0 ? tf.ElementAt(0) : null;
            if (input != null)
            {
                input.style.backgroundColor = new StyleColor(new Color(0.12f, 0.12f, 0.12f, 1f));
                input.style.color = Color.white;
                input.style.paddingLeft = 6; input.style.paddingRight = 6;
            }
        }

        // ---- Log render ---------------------------------------------------
        private void RefreshLogList()
        {
            if (_logRoot == null) return;
            _logRoot.Clear();
            string needle = (_search ?? "").Trim();
            bool hasNeedle = !string.IsNullOrEmpty(needle);
            foreach (var e in _entries)
            {
                if (!PassesFilter(e.Level)) continue;
                if (hasNeedle && e.Text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;
                _logRoot.Add(BuildLogLine(e));
            }
            _scroll.schedule.Execute(() => _scroll.verticalScroller.value = _scroll.verticalScroller.highValue).StartingIn(0);
        }

        private bool PassesFilter(LogType lvl)
        {
            switch (lvl)
            {
                case LogType.Log: return _showInfo;
                case LogType.Warning: return _showWarn;
                default: return _showError;
            }
        }

        private static Label BuildLogLine(Entry e)
        {
            var color = e.Level == LogType.Warning ? new Color(1f, 0.85f, 0.4f)
                      : e.Level == LogType.Log ? new Color(0.85f, 0.85f, 0.85f)
                      : new Color(1f, 0.45f, 0.45f);
            return new Label($"[{e.Time}] {e.Text}")
            {
                style =
                {
                    color = color,
                    whiteSpace = WhiteSpace.Normal,
                    fontSize = 12,
                    marginBottom = 1,
                }
            };
        }

        // ---- Input / history --------------------------------------------
        private void SubmitInput()
        {
            if (_inputField == null) return;
            var raw = (_inputField.value ?? "").Trim();
            if (raw.Length == 0) return;
            _inputField.value = "";
            if (_history.Count == 0 || _history[_history.Count - 1] != raw) _history.Add(raw);
            _historyCursor = _history.Count;
            EchoCommand(raw);
            HandleCommand(raw);
            _inputField.Focus();
        }

        private void StepHistory(int delta)
        {
            if (_history.Count == 0) return;
            _historyCursor = Mathf.Clamp(_historyCursor + delta, 0, _history.Count);
            if (_inputField == null) return;
            _inputField.value = _historyCursor >= _history.Count ? "" : _history[_historyCursor];
        }

        private void EchoCommand(string cmd) => Print("> " + cmd, LogType.Log);

        private void Print(string text, LogType lvl = LogType.Log)
        {
            if (_entries.Count >= MAX_ENTRIES) _entries.Dequeue();
            _entries.Enqueue(new Entry { Level = lvl, Time = DateTime.Now.ToString("HH:mm:ss"), Text = text });
            if (_open) RefreshLogList();
        }

        // ---- Commands -----------------------------------------------------
        private void HandleCommand(string raw)
        {
            try
            {
                if (raw.StartsWith("/"))
                {
                    KeybindRunner.Instance?.SendChatMessage(raw);
                    Print("[chat] sent: " + raw);
                    return;
                }

                var parts = raw.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                var head = parts[0].ToLowerInvariant();
                switch (head)
                {
                    case "clear": case "cls": _entries.Clear(); RefreshLogList(); break;
                    case "help": CmdHelp(); break;
                    case "echo": Print(string.Join(" ", parts.Skip(1))); break;
                    case "chat": CmdChat(parts); break;

                    case "set": CmdSet(parts); break;
                    case "get": CmdGet(parts); break;
                    case "toggle": CmdToggle(parts); break;
                    case "list": CmdList(parts); break;
                    case "save": CmdSave(); break;
                    case "reload": CmdReload(); break;

                    case "fov": CmdFov(parts); break;
                    case "players": CmdPlayers(); break;
                    case "version": Print($"PoncePlayerInput build {typeof(DevConsole).Assembly.GetName().Version}"); break;
                    case "fps": Print($"FPS: {1f / Mathf.Max(0.0001f, Time.smoothDeltaTime):F1}"); break;
                    case "time": Print($"Time: {DateTime.Now:HH:mm:ss.fff} (game: {Time.timeAsDouble:F2}s)"); break;

                    case "close": Close(); break;
                    case "pos": CmdMove(parts); break;
                    case "size": CmdSize(parts); break;

                    default: Print("Unknown command: " + head + " (try 'help')", LogType.Warning); break;
                }
            }
            catch (Exception e) { Print("Command error: " + e.Message, LogType.Error); }
        }

        private void CmdHelp()
        {
            Print("Commands:");
            Print("  /<chat>             — forward to game chat");
            Print("  set <field> <value> — change a CommandKeybindConfig field");
            Print("  get <field>         — show current value");
            Print("  toggle <field>      — flip a bool field");
            Print("  list [filter]       — list config fields, optional name substring");
            Print("  save | reload       — save / reload mod config from disk");
            Print("  fov <60-150>        — set FOV via PlayerPrefs");
            Print("  chat <text>         — send to game chat");
            Print("  players             — list current players");
            Print("  version | fps | time");
            Print("  pos <x> <y>         — move console window");
            Print("  size <w> <h>        — resize console window");
            Print("  clear | close       — clear log / close console");
            Print("  Up/Down arrows cycle input history.");
        }

        private static FieldInfo FindField(string name)
        {
            return typeof(CommandKeybindConfig).GetField(name,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        }

        private void CmdSet(string[] parts)
        {
            if (parts.Length < 3) { Print("Usage: set <field> <value>", LogType.Warning); return; }
            var cfg = KeybindRunner.Instance?.CommandConfig;
            var field = FindField(parts[1]);
            if (cfg == null || field == null) { Print($"No such field: {parts[1]}", LogType.Warning); return; }
            object converted;
            try { converted = ConvertValue(string.Join(" ", parts.Skip(2)), field.FieldType); }
            catch (Exception e) { Print($"Bad value for {field.Name} ({field.FieldType.Name}): {e.Message}", LogType.Warning); return; }
            field.SetValue(cfg, converted);
            KeybindRunner.Instance?.SaveConfigsAndRefresh();
            Print($"set {field.Name} = {converted}");
        }

        private void CmdGet(string[] parts)
        {
            if (parts.Length < 2) { Print("Usage: get <field>", LogType.Warning); return; }
            var cfg = KeybindRunner.Instance?.CommandConfig;
            var field = FindField(parts[1]);
            if (cfg == null || field == null) { Print($"No such field: {parts[1]}", LogType.Warning); return; }
            Print($"{field.Name} = {field.GetValue(cfg)}");
        }

        private void CmdToggle(string[] parts)
        {
            if (parts.Length < 2) { Print("Usage: toggle <field>", LogType.Warning); return; }
            var cfg = KeybindRunner.Instance?.CommandConfig;
            var field = FindField(parts[1]);
            if (cfg == null || field == null || field.FieldType != typeof(bool)) { Print($"No bool field: {parts[1]}", LogType.Warning); return; }
            bool cur = (bool)field.GetValue(cfg);
            field.SetValue(cfg, !cur);
            KeybindRunner.Instance?.SaveConfigsAndRefresh();
            Print($"{field.Name} = {!cur}");
        }

        private void CmdList(string[] parts)
        {
            string filter = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";
            var sb = new StringBuilder("Config fields:");
            foreach (var f in typeof(CommandKeybindConfig).GetFields(BindingFlags.Public | BindingFlags.Instance)
                              .OrderBy(f => f.Name))
            {
                if (filter.Length > 0 && f.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                sb.Append("\n  ").Append(f.Name).Append(" : ").Append(f.FieldType.Name);
            }
            Print(sb.ToString());
        }

        private void CmdSave()
        {
            KeybindRunner.Instance?.SaveConfigsAndRefresh();
            Print("Config saved.");
        }

        private void CmdReload()
        {
            KeybindRunner.Instance?.DoReload();
            Print("Config reloaded.");
        }

        private void CmdChat(string[] parts)
        {
            if (parts.Length < 2) { Print("Usage: chat <text>", LogType.Warning); return; }
            var msg = string.Join(" ", parts.Skip(1));
            KeybindRunner.Instance?.SendChatMessage(msg);
            Print("[chat] " + msg);
        }

        private void CmdFov(string[] parts)
        {
            if (parts.Length < 2 || !float.TryParse(parts[1], out var v))
            { Print("Usage: fov <60-150>", LogType.Warning); return; }
            v = Mathf.Clamp(v, 60f, 150f);
            try { PlayerPrefs.SetFloat("fov", v); PlayerPrefs.Save(); Print($"FOV set to {v} (rejoin or respawn to apply)."); }
            catch (Exception e) { Print("Failed: " + e.Message, LogType.Error); }
        }

        private void CmdPlayers()
        {
            try
            {
                var pm = UnityEngine.Object.FindFirstObjectByType<PlayerManager>();
                if (pm == null) { Print("PlayerManager not in scene."); return; }
                var local = pm.GetLocalPlayer();
                var players = pm.GetPlayers();
                Print($"Players ({players.Count}):");
                foreach (var p in players)
                {
                    if (p == null) continue;
                    string mark = (local != null && p == local) ? " (you)" : "";
                    Print($"  {p.Username.Value} — Team={p.Team} Role={p.Role}{mark}");
                }
            }
            catch (Exception e) { Print("players failed: " + e.Message, LogType.Error); }
        }

        private void CmdMove(string[] parts)
        {
            if (parts.Length < 3 || !float.TryParse(parts[1], out var x) || !float.TryParse(parts[2], out var y))
            { Print("Usage: pos <x> <y>", LogType.Warning); return; }
            if (_panel == null) return;
            _panel.style.left = Mathf.Max(0f, x);
            _panel.style.top = Mathf.Max(0f, y);
            PersistRect();
        }

        private void CmdSize(string[] parts)
        {
            if (parts.Length < 3 || !float.TryParse(parts[1], out var w) || !float.TryParse(parts[2], out var h))
            { Print("Usage: size <w> <h>", LogType.Warning); return; }
            if (_panel == null) return;
            _panel.style.width = Mathf.Max(420f, w);
            _panel.style.height = Mathf.Max(220f, h);
            PersistRect();
        }

        private static object ConvertValue(string raw, Type t)
        {
            if (t == typeof(string)) return raw;
            if (t == typeof(bool))
            {
                var v = raw.Trim().ToLowerInvariant();
                if (v == "true" || v == "1" || v == "yes" || v == "on") return true;
                if (v == "false" || v == "0" || v == "no" || v == "off") return false;
                return bool.Parse(raw);
            }
            if (t == typeof(int)) return int.Parse(raw);
            if (t == typeof(float)) return float.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
            if (t == typeof(double)) return double.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
            if (t.IsEnum) return Enum.Parse(t, raw, ignoreCase: true);
            throw new InvalidCastException($"unsupported type {t.Name}");
        }
    }
}
