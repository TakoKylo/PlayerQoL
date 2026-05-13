// Base-game UX fixes:
//   1. Allow All-Chat / Team-Chat in LockerRoom phase and with PlayerRole.None.
//   2. ESC closes secondary menus (Settings, Mods, ServerBrowser, etc.) when they
//      are open in either Playing or LockerRoom phase.
//   3. Right-click a chat message to copy its plain text to clipboard.
//   4. Chat messages are made drag-selectable by swapping the underlying Label
//      for a read-only TextField (the only UI Toolkit control with native
//      text selection in this Unity version).

using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UIElements;

namespace PoncePuck.Keybinds
{
    internal static class BaseMenuPatches
    {
        // ---- ESC handling for base game secondary menus ---------------------
        //
        // The vanilla pause-action handler only operates on the pause menu and
        // only while phase == Playing. When a secondary menu is visible we
        // route ESC to that menu's own "close" event so its controller can
        // restore the previous view correctly (e.g. Settings -> MainMenu in
        // lobby, Settings -> PauseMenu in game).
        //
        // Returns true if a secondary menu was closed (ESC was consumed).
        public static bool TryCloseTopmostSecondaryMenu()
        {
            try
            {
                var ui = MonoBehaviourSingleton<UIManager>.Instance;
                if (ui == null) return false;

                // Order matters: nested popups before their parents.
                if (ui.Identity != null && ui.Identity.IsVisible) { EventManager.TriggerEvent("Event_OnIdentityClickClose"); return true; }
                if (ui.Appearance != null && ui.Appearance.IsVisible) { EventManager.TriggerEvent("Event_OnAppearanceClickClose"); return true; }
                if (ui.Friends != null && ui.Friends.IsVisible) { EventManager.TriggerEvent("Event_OnFriendsClickClose"); return true; }
                if (ui.NewServer != null && ui.NewServer.IsVisible) { EventManager.TriggerEvent("Event_OnNewServerClickClose"); return true; }
                if (ui.ServerBrowser != null && ui.ServerBrowser.IsVisible) { EventManager.TriggerEvent("Event_OnServerBrowserClickClose"); return true; }
                if (ui.Settings != null && ui.Settings.IsVisible) { EventManager.TriggerEvent("Event_OnSettingsClickClose"); return true; }
                if (ui.Mods != null && ui.Mods.IsVisible) { EventManager.TriggerEvent("Event_OnModsClickClose"); return true; }
                if (ui.PlayerMenu != null && ui.PlayerMenu.IsVisible) { EventManager.TriggerEvent("Event_OnPlayerMenuClickBack"); return true; }
                if (ui.Play != null && ui.Play.IsVisible) { EventManager.TriggerEvent("Event_OnPlayClickClose"); return true; }
            }
            catch (Exception e) { Debug.LogWarning("[PPKB] ESC menu close failed: " + e.Message); }
            return false;
        }

        // ---- Chat-open relaxations -----------------------------------------
        //
        // Vanilla `OnAllChatActionPerformed` / `OnTeamChatActionPerformed` gate
        // on `Phase == Playing`. That means clients in the lobby (LockerRoom)
        // can't open chat. We replace the guard with one that just blocks when
        // another interactive view (menu, popup) is up.

        // Block chat opening only when a modal text-input menu is up. Exclude
        // background views like MainMenu/Play that stay visible in LockerRoom
        // phase, and transient gameplay views like Team/Position select.
        private static bool ChatShouldBeBlocked()
        {
            try
            {
                // Dev console is a focusable text input — chat keys typed inside it
                // are user input, not chat-open requests.
                if (DevConsole.Instance != null && DevConsole.Instance.IsOpen) return true;

                var ui = MonoBehaviourSingleton<UIManager>.Instance;
                if (ui == null) return false;
                if (ui.Settings != null && ui.Settings.IsVisible) return true;
                if (ui.Mods != null && ui.Mods.IsVisible) return true;
                if (ui.PauseMenu != null && ui.PauseMenu.IsVisible) return true;
                if (ui.ServerBrowser != null && ui.ServerBrowser.IsVisible) return true;
                if (ui.NewServer != null && ui.NewServer.IsVisible) return true;
                if (ui.Identity != null && ui.Identity.IsVisible) return true;
                if (ui.Appearance != null && ui.Appearance.IsVisible) return true;
                if (ui.PlayerMenu != null && ui.PlayerMenu.IsVisible) return true;
                if (ui.Friends != null && ui.Friends.IsVisible) return true;
            }
            catch { }
            return false;
        }

        private static void ForceOpenChat(UIChat chat, bool teamChat)
        {
            if (chat == null) return;
            try
            {
                // Chat already capturing input — don't re-StartInput, that would
                // wipe whatever the user is typing.
                if (chat.IsFocused) return;

                if (!chat.IsVisible) chat.Show();

                // TeamSelect / PositionSelect / other LockerRoom overlays sit on
                // top of chat, so even after Show() the textfield is visually
                // behind them. Lift the chat view to the top of its siblings.
                var view = AccessTools.Field(typeof(UIView), "view")?.GetValue(chat) as VisualElement;
                view?.BringToFront();

                chat.StartInput(isTeamChat: teamChat);
            }
            catch (Exception e) { Debug.LogWarning("[PPKB] ForceOpenChat failed: " + e.Message); }
        }

        [HarmonyPatch(typeof(UIManager), "OnAllChatActionPerformed")]
        private static class AllChat_AllowAnyPhase
        {
            private static bool Prefix(UIManager __instance)
            {
                try
                {
                    if (ChatShouldBeBlocked()) return false;
                    ForceOpenChat(__instance?.Chat, teamChat: false);
                    return false;
                }
                catch { }
                return true;
            }
        }

        [HarmonyPatch(typeof(UIManager), "OnTeamChatActionPerformed")]
        private static class TeamChat_AllowAnyPhase
        {
            private static bool Prefix(UIManager __instance)
            {
                try
                {
                    if (ChatShouldBeBlocked()) return false;
                    ForceOpenChat(__instance?.Chat, teamChat: true);
                    return false;
                }
                catch { }
                return true;
            }
        }

        // ---- Selectable chat text + right-click copy -----------------------
        //
        // UI Toolkit's TextElement (Label's base) supports selection via the
        // `selection` interface (Unity 2023+). Enabling `isSelectable` lets the
        // user drag-highlight chat text. We also keep a right-click handler
        // that copies the line's plain text to the system clipboard.

        // Enable click-to-copy AND drag-select on chat lines.
        //
        // The chat panel sits as a non-interactive HUD overlay: its parent
        // chain has pickingMode=Ignore so pointer events fall through to the
        // game. To make text interactive we walk the WHOLE ancestor chain from
        // the chat view up to its panel root and flip every Ignore to Position.
        //
        // Selection is enabled best-effort via TextElement.selection — if it
        // doesn't take, the user can still left-click a line to copy the whole
        // message (or right-click for the same).

        private static bool _chatPickingOpened;
        private static void OpenChatPickingPath(UIChat chat)
        {
            if (_chatPickingOpened || chat == null) return;
            try
            {
                var view = AccessTools.Field(typeof(UIView), "view")?.GetValue(chat) as VisualElement;
                if (view == null) return;
                // Walk all the way up to the panel root and unblock pointer events.
                var cur = view;
                while (cur != null)
                {
                    if (cur.pickingMode == PickingMode.Ignore) cur.pickingMode = PickingMode.Position;
                    cur = cur.parent;
                }
                // Also flip any known internal containers explicitly.
                foreach (var name in new[] { "chat", "scrollView", "messages", "padding" })
                {
                    var ve = AccessTools.Field(typeof(UIChat), name)?.GetValue(chat) as VisualElement;
                    if (ve != null && ve.pickingMode == PickingMode.Ignore)
                        ve.pickingMode = PickingMode.Position;
                }
                _chatPickingOpened = true;
            }
            catch (Exception e) { Debug.LogWarning("[PPKB] OpenChatPickingPath failed: " + e.Message); }
        }

        [HarmonyPatch(typeof(UIChat), "AddChatMessage")]
        private static class Chat_MakeSelectable_Postfix
        {
            private static void Postfix(UIChat __instance, ChatMessage chatMessage)
            {
                try
                {
                    OpenChatPickingPath(__instance);

                    var messages = AccessTools.Field(typeof(UIChat), "messages")?.GetValue(__instance) as VisualElement;
                    if (messages == null || messages.childCount == 0) return;
                    var child = messages[messages.childCount - 1];
                    if (child == null) return;

                    child.pickingMode = PickingMode.Position;

                    var labels = child.Query<Label>().ToList();
                    foreach (var lbl in labels)
                    {
                        lbl.focusable = true;
                        lbl.pickingMode = PickingMode.Position;
                        try
                        {
                            lbl.selection.isSelectable = true;
                            lbl.selection.doubleClickSelectsWord = true;
                            lbl.selection.tripleClickSelectsLine = true;
                        }
                        catch { }

                        // Left- OR right-click copies the whole message line.
                        var copyTarget = lbl;
                        lbl.RegisterCallback<PointerDownEvent>(evt =>
                        {
                            try
                            {
                                if (evt.button != 0 && evt.button != 1) return;
                                GUIUtility.systemCopyBuffer = StripRichText(copyTarget.text ?? "");
                                evt.StopPropagation();
                            }
                            catch { }
                        });
                    }
                }
                catch (Exception e) { Debug.LogWarning("[PPKB] selection-enable failed: " + e.Message); }
            }
        }

        private static string StripRichText(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            // Quick and good-enough: drop everything inside <...> tags.
            var sb = new System.Text.StringBuilder(s.Length);
            bool inTag = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '<') { inTag = true; continue; }
                if (c == '>') { inTag = false; continue; }
                if (!inTag) sb.Append(c);
            }
            return sb.ToString();
        }

        // ---- Server browser: inline filters at the bottom -------------------
        //
        // Vanilla shows a separate full-screen Filters popup behind a [FILTERS]
        // button. Reparenting that popup as-is keeps the screen-overlay USS so
        // it ends up positioned off-screen. Instead, harvest its child controls
        // (search field, max-ping field, toggles) into a fresh compact row
        // appended at the bottom of the server browser. The original popup is
        // hidden entirely. Controls retain their wired-up callbacks because we
        // only reparent the elements themselves.

        private static UIServerBrowser _inlinedFor;

        // Match the server-list row style: dark uniform row, bold uppercase
        // label on the left, control right-aligned, comfortable padding.
        private static readonly Color BrowserRowBg = new Color(61f / 255f, 61f / 255f, 61f / 255f, 1f);

        private static void StyleInputField(VisualElement field, float width)
        {
            field.style.width = width;
            field.style.height = 32;
            // Same color as the row so text fields blend like base-game UI.
            field.style.backgroundColor = new StyleColor(BrowserRowBg);
            field.style.borderTopWidth = 0; field.style.borderBottomWidth = 0;
            field.style.borderLeftWidth = 0; field.style.borderRightWidth = 0;

            var input = field.Q(className: "unity-base-text-field__input")
                     ?? field.Q(className: "unity-text-field__input")
                     ?? field.Q(className: "unity-base-field__input");
            if (input != null)
            {
                input.style.backgroundColor = new StyleColor(BrowserRowBg);
                input.style.color = Color.white;
                input.style.unityTextAlign = TextAnchor.MiddleRight;
                input.style.fontSize = 24;
                input.style.unityFontStyleAndWeight = FontStyle.Normal;
                input.style.paddingLeft = 6;
                input.style.paddingRight = 6;
                input.style.borderTopWidth = 0; input.style.borderBottomWidth = 0;
                input.style.borderLeftWidth = 0; input.style.borderRightWidth = 0;
            }
        }

        private static void StyleToggleBox(Toggle toggle)
        {
            if (toggle == null) return;
            var checkmark = toggle.Q(className: "unity-toggle__checkmark");
            if (checkmark != null)
            {
                checkmark.style.width = 24;
                checkmark.style.height = 24;
                checkmark.style.borderTopWidth = 2; checkmark.style.borderBottomWidth = 2;
                checkmark.style.borderLeftWidth = 2; checkmark.style.borderRightWidth = 2;
                var col = new StyleColor(new Color(1f, 1f, 1f, 0.85f));
                checkmark.style.borderTopColor = col; checkmark.style.borderBottomColor = col;
                checkmark.style.borderLeftColor = col; checkmark.style.borderRightColor = col;
            }
        }

        private static VisualElement BuildCell(string label, VisualElement control)
        {
            var labelProp = control.GetType().GetProperty("label",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            try { labelProp?.SetValue(control, ""); } catch { }

            var labelChild = control.Q<Label>(className: "unity-base-field__label")
                          ?? control.Q<Label>(className: "unity-toggle__label")
                          ?? control.Q<Label>(className: "unity-text-field__label");
            if (labelChild != null) labelChild.style.display = DisplayStyle.None;

            control.style.marginLeft = 0;
            control.style.marginRight = 0;
            control.style.marginTop = 0;
            control.style.marginBottom = 0;
            control.style.flexGrow = 0;
            control.style.flexShrink = 0;

            var cell = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    alignItems = Align.Center,
                    justifyContent = Justify.SpaceBetween,
                    height = 50,
                    minHeight = 50,
                    flexShrink = 0,
                    marginBottom = 8,
                    paddingLeft = 24,
                    paddingRight = 24,
                    backgroundColor = new StyleColor(BrowserRowBg),
                }
            };
            var lab = new Label(label)
            {
                style =
                {
                    color = Color.white,
                    fontSize = 24,
                    unityFontStyleAndWeight = FontStyle.Normal,
                    unityTextAlign = TextAnchor.MiddleLeft,
                }
            };
            cell.Add(lab);
            cell.Add(control);
            return cell;
        }

        private static VisualElement BuildColumn()
        {
            return new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Column,
                    flexGrow = 1,
                    flexShrink = 0,
                    flexBasis = new StyleLength(new Length(50, LengthUnit.Percent)),
                    width = new StyleLength(new Length(50, LengthUnit.Percent)),
                }
            };
        }

        private static void InlineFilters(UIServerBrowser browser)
        {
            if (browser == null || browser == _inlinedFor) return;
            try
            {
                var serverBrowser = AccessTools.Field(typeof(UIServerBrowser), "serverBrowser")?.GetValue(browser) as VisualElement;
                var filters = AccessTools.Field(typeof(UIServerBrowser), "filters")?.GetValue(browser) as VisualElement;
                var filtersButton = AccessTools.Field(typeof(UIServerBrowser), "filtersButton")?.GetValue(browser) as VisualElement;
                var refreshBtn = AccessTools.Field(typeof(UIServerBrowser), "refreshButton")?.GetValue(browser) as VisualElement;
                var newServerBtn = AccessTools.Field(typeof(UIServerBrowser), "newServerButton")?.GetValue(browser) as VisualElement;
                if (serverBrowser == null || filters == null) return;

                if (filtersButton != null) filtersButton.style.display = DisplayStyle.None;
                filters.style.display = DisplayStyle.None;

                var search = filters.Q<VisualElement>("SearchTextField")?.Q<TextField>();
                var maxPing = filters.Q<VisualElement>("MaxPingIntegerField")?.Q<IntegerField>();
                var showFull = filters.Q<VisualElement>("ShowFullToggle")?.Q<Toggle>();
                var showEmpty = filters.Q<VisualElement>("ShowEmptyToggle")?.Q<Toggle>();
                var showPwd = filters.Q<VisualElement>("ShowPasswordProtectedToggle")?.Q<Toggle>();
                var showModded = filters.Q<VisualElement>("ShowModdedToggle")?.Q<Toggle>();
                var showUnreach = filters.Q<VisualElement>("ShowUnreachableToggle")?.Q<Toggle>();

                // ---- Filter strip: two columns side-by-side ----
                var strip = new VisualElement
                {
                    name = "PPKB_InlineFilters",
                    style =
                    {
                        flexDirection = FlexDirection.Row,
                        alignItems = Align.FlexStart,
                        flexShrink = 0,
                        marginTop = 8,
                        marginBottom = 8,
                    }
                };

                var col1 = BuildColumn();
                var col2 = BuildColumn();

                if (search != null)
                {
                    search.RemoveFromHierarchy();
                    StyleInputField(search, 200);
                    col1.Add(BuildCell("SEARCH", search));
                }
                if (maxPing != null)
                {
                    maxPing.RemoveFromHierarchy();
                    StyleInputField(maxPing, 80);
                    col1.Add(BuildCell("MAX PING", maxPing));
                }
                if (showUnreach != null)
                {
                    showUnreach.RemoveFromHierarchy();
                    StyleToggleBox(showUnreach);
                    col1.Add(BuildCell("UNREACHABLE", showUnreach));
                }
                if (showModded != null) { showModded.RemoveFromHierarchy(); StyleToggleBox(showModded); col1.Add(BuildCell("MODDED", showModded)); }

                if (showFull != null)   { showFull.RemoveFromHierarchy();   StyleToggleBox(showFull);   col2.Add(BuildCell("FULL", showFull));     }
                if (showEmpty != null)  { showEmpty.RemoveFromHierarchy();  StyleToggleBox(showEmpty);  col2.Add(BuildCell("EMPTY", showEmpty));   }
                if (showPwd != null)    { showPwd.RemoveFromHierarchy();    StyleToggleBox(showPwd);    col2.Add(BuildCell("LOCKED", showPwd));    }

                // Fill the empty 4th slot in col2 with the REFRESH + NEW SERVER buttons.
                if (refreshBtn != null && newServerBtn != null)
                {
                    refreshBtn.RemoveFromHierarchy();
                    newServerBtn.RemoveFromHierarchy();
                    foreach (var b in new[] { refreshBtn, newServerBtn })
                    {
                        b.style.height = 50;
                        b.style.flexGrow = 1;
                        b.style.flexBasis = 0;
                        b.style.flexShrink = 1;
                        b.style.minWidth = 0;
                        b.style.marginLeft = 0;
                        b.style.marginRight = 0;
                        b.style.marginTop = 0;
                        b.style.marginBottom = 0;
                        b.style.fontSize = 24;
                        b.style.unityFontStyleAndWeight = FontStyle.Normal;
                        b.style.color = Color.white;
                        b.style.backgroundColor = new StyleColor(BrowserRowBg);
                        b.style.borderTopWidth = 0; b.style.borderBottomWidth = 0;
                        b.style.borderLeftWidth = 0; b.style.borderRightWidth = 0;
                        b.style.paddingLeft = 24; b.style.paddingRight = 24;
                        b.style.unityTextAlign = TextAnchor.MiddleLeft;
                    }
                    refreshBtn.style.marginRight = 4;
                    newServerBtn.style.marginLeft = 4;

                    var buttonsRow = new VisualElement
                    {
                        style =
                        {
                            flexDirection = FlexDirection.Row,
                            alignItems = Align.Center,
                            justifyContent = Justify.SpaceBetween,
                            height = 50,
                            minHeight = 50,
                            flexShrink = 0,
                            marginBottom = 8,
                        }
                    };
                    buttonsRow.Add(refreshBtn);
                    buttonsRow.Add(newServerBtn);
                    col2.Add(buttonsRow);
                }

                strip.Add(col1);
                strip.Add(col2);
                // 50/50 columns flush with parent edges (matching server rows
                // above), with a small visual gutter between them.
                col1.style.marginLeft = 0;
                col1.style.marginRight = 4;
                col2.style.marginLeft = 4;
                col2.style.marginRight = 0;

                serverBrowser.Add(strip);

                _inlinedFor = browser;
            }
            catch (Exception e) { Debug.LogWarning("[PPKB] ServerBrowser inline-filters failed: " + e.Message); }
        }

        [HarmonyPatch(typeof(UIServerBrowser), "Show")]
        private static class ServerBrowser_Show_InlineFilters
        {
            private static void Postfix(UIServerBrowser __instance) => InlineFilters(__instance);
        }

        // Keep ShowFilters/HideFilters from re-displaying the now-hidden popup.
        [HarmonyPatch(typeof(UIServerBrowser), "ShowFilters")]
        private static class ServerBrowser_ShowFilters_NoOp { private static bool Prefix() => false; }
        [HarmonyPatch(typeof(UIServerBrowser), "HideFilters")]
        private static class ServerBrowser_HideFilters_NoOp { private static bool Prefix() => false; }
    }
}
