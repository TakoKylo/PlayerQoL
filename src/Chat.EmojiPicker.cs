// Chat.EmojiPicker.cs - Right-click the chat input box to open an emoji / kaomoji picker.
//
// How it fits the chat pipeline (B310):
//   * The picker inserts ASCII SHORTCODES (e.g. ":fire:" / ":kao_shrug:") into UIChat.textField,
//     never raw glyphs. The game's UIChat.ParseChatContent runs FilterStringSpecialCharacters,
//     which strips virtually every emoji/kaomoji character (only ❤️ 😭 🔥 💯 are whitelisted),
//     so raw glyphs would be destroyed on display. Shortcodes survive the filter and are rendered
//     locally by KaomojiSystem.ProcessKaomoji in the chat receive postfix.
//   * Kaomoji are inserted via the collision-proof ":kao_<name>:" namespace so they always render
//     as the text face instead of being shadowed by a same-named emoji shortcode.

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UIElements;

namespace PoncePuck.LocalMute
{
    internal static class ChatEmojiPicker
    {
        private const float PopupWidth = 470f;
        private const float PopupHeight = 260f;

        private static readonly Color PanelBg = new Color(0.18f, 0.18f, 0.18f, 0.97f);
        private static readonly Color HeaderBg = new Color(0.13f, 0.13f, 0.13f, 1f);
        private static readonly Color BorderCol = new Color(0.05f, 0.05f, 0.05f, 1f);
        private static readonly Color TabActive = new Color(0.32f, 0.32f, 0.32f, 1f);
        private static readonly Color TabIdle = new Color(0.22f, 0.22f, 0.22f, 1f);
        private static readonly Color ItemHover = new Color(0.30f, 0.30f, 0.30f, 1f);
        private static readonly Color TextCol = new Color(0.93f, 0.93f, 0.93f, 1f);

        private static UIChat _chat;           // the chat UI we're currently bound to
        private static TextField _field;       // its input text field
        private static VisualElement _chatRoot;    // UIChat's "chat" element (popup parents under it)
        private static VisualElement _hookedRoot;  // panel root we registered the right-click handler on
        private static TextField _focusHookedField;
        private static bool _loggedRightClick;
        private static VisualElement _popup;
        private static ScrollView _emojiScroll;
        private static ScrollView _kaomojiScroll;
        private static ScrollView _customScroll;
        private static Button _tabEmoji;
        private static Button _tabKaomoji;
        private static Button _tabCustom;
        private static int _activeTab; // 0 = emoji, 1 = kaomoji, 2 = custom

        // -------------------------------------------------------------------------
        // EnsureAttached: called every frame from LocalMuteRunner.Update so we bind
        // regardless of whether UIChat.Initialize ran before our Harmony patches.
        // We hook the panel ROOT (not the field) so the right-click is caught at the
        // top of the trickle-down path, independent of field event quirks.
        // -------------------------------------------------------------------------
        public static void EnsureAttached()
        {
            UIChat chat;
            try { chat = MonoBehaviourSingleton<UIManager>.Instance?.Chat; }
            catch { return; }
            if (chat == null) return;

            TextField tf;
            try { tf = AccessTools.Field(typeof(UIChat), "textField")?.GetValue(chat) as TextField; }
            catch { return; }
            if (tf == null) return;

            _chat = chat;
            _field = tf;
            try { _chatRoot = AccessTools.Field(typeof(UIChat), "chat")?.GetValue(chat) as VisualElement; } catch { }

            // Hide the popup only when focus actually leaves the picker (Enter/Esc/clicked elsewhere).
            // Clicking a picker item moves focus to that item (a descendant of the chat element), so
            // we keep the popup open in that case.
            if (!ReferenceEquals(_focusHookedField, tf))
            {
                tf.RegisterCallback<FocusOutEvent>(OnFieldFocusOut);
                _focusHookedField = tf;
            }

            var root = tf.panel?.visualTree;
            if (root != null && !ReferenceEquals(_hookedRoot, root))
            {
                root.RegisterCallback<PointerDownEvent>(OnRootPointerDown, TrickleDown.TrickleDown);
                _hookedRoot = root;
                Debug.Log("[EmojiPicker] Right-click handler attached to chat panel root.");
            }
        }

        private static void OnRootPointerDown(PointerDownEvent evt)
        {
            if (evt.button != 1) return; // right mouse only

            if (!_loggedRightClick)
            {
                _loggedRightClick = true;
                Debug.Log($"[EmojiPicker] Right-click reached UI panel. chatFocused={_chat?.IsFocused}, " +
                          $"pos={evt.position}, fieldBounds={_field?.worldBound}");
            }

            if (!IsEnabled()) return;
            if (_field == null || _chat == null || !_chat.IsFocused) return; // only while chat input is open

            // Only act if the click is on the chat input box.
            if (!_field.worldBound.Contains((Vector2)evt.position)) return;

            evt.StopPropagation();
            evt.StopImmediatePropagation();

            Toggle();
        }

        // -------------------------------------------------------------------------
        // Show / hide / toggle
        // -------------------------------------------------------------------------
        private static void Toggle()
        {
            if (_popup != null && _popup.style.display == DisplayStyle.Flex) { Hide(); return; }
            Show();
        }

        private static void Show()
        {
            if (_field == null) return;

            // Parent UNDER the chat element so focus moving into the picker counts as
            // "still inside chat" - UIChat's FocusOut handler then won't StopInput() (which
            // would close the chat and wipe the typed text). Fall back to the panel root.
            var parent = _chatRoot ?? _field.panel?.visualTree;
            if (parent == null) return;

            EnsureBuilt();

            if (_popup.parent != parent)
            {
                _popup.RemoveFromHierarchy();
                parent.Add(_popup);
            }

            _popup.style.display = DisplayStyle.Flex;
            _popup.BringToFront();
            SelectTab(_activeTab);
            Reposition();
        }

        private static void Hide()
        {
            if (_popup != null) _popup.style.display = DisplayStyle.None;
        }

        private static void OnFieldFocusOut(FocusOutEvent evt)
        {
            // Keep the picker open when focus moved into it (e.g. clicking an emoji item).
            var rt = evt.relatedTarget as VisualElement;
            if (rt != null && _popup != null && (rt == _popup || _popup.Contains(rt)))
                return;
            Hide();
        }

        private static void Reposition()
        {
            if (_field == null || _popup == null) return;
            var parent = _popup.parent;
            if (parent == null) return;

            // Place the popup BELOW the chat input box, left-aligned with it, clamped fully
            // on screen (above-the-box placement got clipped off the top).
            Rect pw = parent.worldBound;
            Rect fw = _field.worldBound;

            // style.left/top are in the parent's LOCAL units; worldBound is post-scale
            // (UIChat.SetScale scales the chat element), so convert through the scale factor.
            float localH = parent.resolvedStyle.height;
            float scale = (localH > 1f && pw.height > 1f) ? pw.height / localH : 1f;
            if (scale <= 0.01f) scale = 1f;

            float wLocal = _popup.resolvedStyle.width;  if (wLocal <= 1f) wLocal = PopupWidth;
            float hLocal = _popup.resolvedStyle.height; if (hLocal <= 1f) hLocal = PopupHeight;
            float wWorld = wLocal * scale, hWorld = hLocal * scale;

            // Screen-space bounds to clamp against.
            var rootVe = parent.panel?.visualTree;
            Rect rw = rootVe != null ? rootVe.worldBound : new Rect(0f, 0f, Screen.width, Screen.height);

            float leftWorld = fw.xMin; // align with the text box's left edge
            if (leftWorld + wWorld > rw.xMax - 8f)
                leftWorld = Mathf.Max(rw.xMin + 8f, rw.xMax - 8f - wWorld);

            float topWorld = fw.yMax + 6f; // just under the text box
            if (topWorld + hWorld > rw.yMax - 8f)
                topWorld = Mathf.Max(rw.yMin + 8f, rw.yMax - 8f - hWorld); // not enough room below: shift up to fit
            if (topWorld < rw.yMin + 8f) topWorld = rw.yMin + 8f;

            _popup.style.left = (leftWorld - pw.xMin) / scale;
            _popup.style.top = (topWorld - pw.yMin) / scale;
            _popup.style.bottom = StyleKeyword.Auto;
        }

        // -------------------------------------------------------------------------
        // Build the popup once.
        // -------------------------------------------------------------------------
        private static void EnsureBuilt()
        {
            if (_popup != null) return;

            _popup = new VisualElement { name = "ponce_emoji_picker" };
            _popup.style.position = Position.Absolute;
            _popup.style.width = PopupWidth;
            _popup.style.maxHeight = PopupHeight;
            _popup.style.backgroundColor = PanelBg;
            SetBorder(_popup, 1f, BorderCol);
            SetRadius(_popup, 8f);
            _popup.style.paddingLeft = _popup.style.paddingRight = 6f;
            _popup.style.paddingTop = _popup.style.paddingBottom = 6f;
            _popup.style.flexDirection = FlexDirection.Column;

            // Header: tab buttons + close.
            var header = new VisualElement();
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            header.style.marginBottom = 6f;

            _tabEmoji = MakeTab("Emoji", () => SelectTab(0));
            _tabKaomoji = MakeTab("Kaomoji", () => SelectTab(1));
            _tabCustom = MakeTab("Custom", () => SelectTab(2));
            header.Add(_tabEmoji);
            header.Add(_tabKaomoji);
            header.Add(_tabCustom);

            var spacer = new VisualElement { style = { flexGrow = 1f } };
            header.Add(spacer);

            var close = MakeTab("✕", Hide);
            close.style.minWidth = 26f;
            header.Add(close);

            _popup.Add(header);

            // Panes.
            _emojiScroll = MakeGrid(KaomojiSystem.GetEmojiPickerItems(), true);
            _kaomojiScroll = MakeGrid(KaomojiSystem.GetKaomojiPickerItems(), false);
            _customScroll = MakeCustomGrid();
            _popup.Add(_emojiScroll);
            _popup.Add(_kaomojiScroll);
            _popup.Add(_customScroll);

            // Keep clicks inside the popup from bubbling out to the game; keep layout responsive.
            _popup.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());
            _popup.RegisterCallback<GeometryChangedEvent>(_ => Reposition());

            _popup.style.display = DisplayStyle.None;
        }

        private static void SelectTab(int tab)
        {
            _activeTab = tab;
            if (_emojiScroll != null) _emojiScroll.style.display = tab == 0 ? DisplayStyle.Flex : DisplayStyle.None;
            if (_kaomojiScroll != null) _kaomojiScroll.style.display = tab == 1 ? DisplayStyle.Flex : DisplayStyle.None;
            if (_customScroll != null) _customScroll.style.display = tab == 2 ? DisplayStyle.Flex : DisplayStyle.None;
            if (_tabEmoji != null) _tabEmoji.style.backgroundColor = tab == 0 ? TabActive : TabIdle;
            if (_tabKaomoji != null) _tabKaomoji.style.backgroundColor = tab == 1 ? TabActive : TabIdle;
            if (_tabCustom != null) _tabCustom.style.backgroundColor = tab == 2 ? TabActive : TabIdle;
        }

        private static ScrollView MakeGrid(List<KeyValuePair<string, string>> items, bool isEmoji)
        {
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1f;
            scroll.style.maxHeight = PopupHeight - 48f;

            var grid = scroll.contentContainer;
            grid.style.flexDirection = FlexDirection.Row;
            grid.style.flexWrap = Wrap.Wrap;
            grid.style.alignItems = Align.Center;

            foreach (var item in items)
                grid.Add(MakeItem(item.Key, item.Value, isEmoji));

            return scroll;
        }

        // Custom tab: image emoji loaded from Plugins/PlayerQoL/Emojis (PNG/JPG/GIF).
        private static ScrollView MakeCustomGrid()
        {
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1f;
            scroll.style.maxHeight = PopupHeight - 48f;

            var grid = scroll.contentContainer;
            grid.style.flexDirection = FlexDirection.Row;
            grid.style.flexWrap = Wrap.Wrap;
            grid.style.alignItems = Align.Center;

            var items = CustomEmojiPack.GetPickerItems();
            if (items.Count == 0)
            {
                var hint = new Label("No custom emojis found.\nDrop PNG/JPG/GIF files into Plugins\\PlayerQoL\\Emojis.");
                hint.style.color = TextCol;
                hint.style.fontSize = 13;
                hint.style.whiteSpace = WhiteSpace.Normal;
                hint.style.paddingLeft = hint.style.paddingTop = 8f;
                grid.Add(hint);
                return scroll;
            }

            foreach (var item in items)
            {
                var tex = item.Value;
                var cell = new VisualElement();
                cell.focusable = true; // same focus rule as MakeItem - keeps chat open on click
                cell.tooltip = item.Key;
                cell.style.flexDirection = FlexDirection.Row;
                cell.style.alignItems = Align.Center;
                cell.style.justifyContent = Justify.Center;
                cell.style.height = 34f;
                cell.style.minWidth = 34f;
                cell.style.marginLeft = cell.style.marginRight = 2f;
                cell.style.marginTop = cell.style.marginBottom = 2f;
                cell.style.paddingLeft = cell.style.paddingRight = 3f;
                SetRadius(cell, 4f);

                const float drawH = 28f;
                var img = new Image { image = tex, scaleMode = ScaleMode.ScaleToFit };
                img.pickingMode = PickingMode.Ignore;
                img.style.height = drawH;
                img.style.width = Mathf.Max(drawH, drawH * ((float)tex.width / Mathf.Max(1, tex.height)));
                cell.Add(img);

                string token = item.Key;
                cell.RegisterCallback<PointerEnterEvent>(_ => cell.style.backgroundColor = ItemHover);
                cell.RegisterCallback<PointerLeaveEvent>(_ => cell.style.backgroundColor = Color.clear);
                cell.RegisterCallback<PointerDownEvent>(evt =>
                {
                    evt.StopPropagation();
                    Insert(token);
                });

                grid.Add(cell);
            }

            return scroll;
        }

        private static VisualElement MakeItem(string token, string glyph, bool isEmoji)
        {
            const float cellH = 30f;

            // Focusable so a click lands focus on this element (a chat descendant) instead of
            // blurring to null -> UIChat keeps the chat open. See OnFieldFocusOut / Show().
            var cell = new VisualElement { name = "ponce_emoji_cell" };
            cell.focusable = true;
            cell.tooltip = token;
            cell.style.flexDirection = FlexDirection.Row;
            cell.style.alignItems = Align.Center;
            cell.style.justifyContent = Justify.Center;
            cell.style.height = cellH;
            cell.style.minWidth = isEmoji ? 30f : 36f;
            cell.style.marginLeft = cell.style.marginRight = 2f;
            cell.style.marginTop = cell.style.marginBottom = 2f;
            cell.style.paddingLeft = cell.style.paddingRight = 4f;
            SetRadius(cell, 4f);

            if (isEmoji)
            {
                // Emoji render natively (in colour) via the game's panel emoji fallback - a plain
                // Label looks far better than a flat monochrome texture.
                var lbl = new Label(glyph) { pickingMode = PickingMode.Ignore };
                lbl.style.color = TextCol;
                lbl.style.fontSize = 20;
                lbl.style.unityTextAlign = TextAnchor.MiddleCenter;
                cell.Add(lbl);
            }
            else
            {
                // Kaomoji have no glyphs in the game font, so render them to a texture.
                var tex = GlyphRenderer.Get(glyph, false, 40);
                if (tex != null)
                {
                    const float drawH = 22f;
                    var img = new Image { image = tex, scaleMode = ScaleMode.ScaleToFit };
                    img.pickingMode = PickingMode.Ignore;
                    img.style.height = drawH;
                    img.style.width = Mathf.Max(drawH, drawH * ((float)tex.width / Mathf.Max(1, tex.height)));
                    cell.Add(img);
                }
                else
                {
                    var lbl = new Label(glyph) { pickingMode = PickingMode.Ignore };
                    lbl.style.color = TextCol;
                    lbl.style.fontSize = 14;
                    lbl.style.unityTextAlign = TextAnchor.MiddleCenter;
                    cell.Add(lbl);
                }
            }

            cell.RegisterCallback<PointerEnterEvent>(_ => cell.style.backgroundColor = ItemHover);
            cell.RegisterCallback<PointerLeaveEvent>(_ => cell.style.backgroundColor = Color.clear);
            cell.RegisterCallback<PointerDownEvent>(evt =>
            {
                evt.StopPropagation();
                Insert(token);
            });

            return cell;
        }

        private static Button MakeTab(string text, Action onClick)
        {
            var b = new Button(onClick) { text = text };
            b.style.color = TextCol;
            b.style.backgroundColor = TabIdle;
            b.style.marginLeft = b.style.marginRight = 2f;
            b.style.marginTop = b.style.marginBottom = 0f;
            b.style.paddingLeft = b.style.paddingRight = 10f;
            b.style.paddingTop = b.style.paddingBottom = 3f;
            SetBorder(b, 0f, BorderCol);
            SetRadius(b, 4f);
            return b;
        }

        // -------------------------------------------------------------------------
        // Insert a shortcode at the caret (falls back to append) and keep chat focused.
        // -------------------------------------------------------------------------
        private static void Insert(string token)
        {
            if (_field == null || string.IsNullOrEmpty(token)) return;

            string cur = _field.value ?? string.Empty;
            int caret = cur.Length;

            try
            {
                var ci = typeof(TextField).GetProperty("cursorIndex",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (ci != null && ci.GetValue(_field) is int idx)
                    caret = Mathf.Clamp(idx, 0, cur.Length);
            }
            catch { }

            string next = cur.Substring(0, caret) + token + cur.Substring(caret);
            _field.value = next;

            // Return focus to the input box so Enter sends and the user can keep typing.
            try { _field.Focus(); } catch { }

            int newCaret = Mathf.Min(caret + token.Length, next.Length);
            try
            {
                var t = typeof(TextField);
                t.GetProperty("cursorIndex", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.SetValue(_field, newCaret);
                t.GetProperty("selectIndex", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.SetValue(_field, newCaret);
            }
            catch { }

            Debug.Log($"[EmojiPicker] inserted '{token}', value now: '{_field.value}'");
        }

        // -------------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------------
        private static bool IsEnabled()
        {
            try
            {
                var runner = UnityEngine.Object.FindFirstObjectByType<PoncePuck.Keybinds.KeybindRunner>();
                if (runner == null) return true;
                var cmdField = typeof(PoncePuck.Keybinds.KeybindRunner).GetField("_cmd",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var cmd = cmdField?.GetValue(runner) as PoncePuck.Keybinds.CommandKeybindConfig;
                return cmd == null || cmd.enableEmojiPicker;
            }
            catch { return true; }
        }

        private static void SetBorder(VisualElement ve, float w, Color c)
        {
            ve.style.borderTopWidth = ve.style.borderBottomWidth = w;
            ve.style.borderLeftWidth = ve.style.borderRightWidth = w;
            ve.style.borderTopColor = ve.style.borderBottomColor = c;
            ve.style.borderLeftColor = ve.style.borderRightColor = c;
        }

        private static void SetRadius(VisualElement ve, float r)
        {
            ve.style.borderTopLeftRadius = ve.style.borderTopRightRadius = r;
            ve.style.borderBottomLeftRadius = ve.style.borderBottomRightRadius = r;
        }
    }
}
