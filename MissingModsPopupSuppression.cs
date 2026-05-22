// Per-server suppression of the vanilla "MODS REQUIRED" popup.
//
// When the user ticks "Don't show this popup again for this server" inside the
// popup, we store a fingerprint of the server's required-mod-ID set under the
// server's "ip:port" key. Next time the same server asks for the same set of
// mods, the popup is auto-confirmed (we emulate the OK click that kicks off
// the mod download / enable state machine) so the player joins seamlessly.
//
// Per-server is the right granularity: if the server adds, removes, or
// reorders any required mod, the fingerprint differs, the trust does NOT
// apply, and the popup reappears so the user can review the new mod set.
//
// Vanilla flow (decompiled from UIPopupManagerController and the OK-click
// handler in ConnectionManagerController):
//   * Event_OnReconnectionStateChanged sees Phase=AwaitingMods +
//     ClientRequiredModIds changed + PendingModIds.Length == 0, and calls
//     UIPopupManager.ShowPopup("missingMods", "MODS REQUIRED", ...).
//   * Clicking OK on the popup runs the load-bearing step:
//         missing-readiness = ClientRequiredModIds \ ReadyMods
//         missing-enabling  = ClientRequiredModIds \ missing-readiness \ EnabledMods
//         GlobalStateManager.SetReconnectionState({ pendingReadinessModIds,
//                                                   pendingEnablingModIds })
//     This kicks off the mod download/enable state machine. Without OK,
//     the connection is stuck — which is why a naive "skip ShowPopup"
//     breaks joins.

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UIElements;

namespace PoncePuck.Keybinds
{
    internal static class MissingModsPopupSuppression
    {
        private const string PopupName = "missingMods";
        private const string ToggleName = "PPKB_MissingModsDontShow";

        private static Dictionary<string, string> Store =>
            KeybindRunner.Instance?.CommandConfig?.trustedServerMods;

        // ─────────────────────── management UI surface ────────────────────────
        // Exposed for a future "CLEAR TRUSTED SERVERS" admin button in settings.

        internal static List<string> SnapshotKeys()
        {
            var s = Store;
            if (s == null) return new List<string>();
            var list = new List<string>(s.Keys);
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        internal static int CountModsFor(string key)
        {
            var s = Store;
            if (s == null || string.IsNullOrEmpty(key)) return 0;
            if (!s.TryGetValue(key, out string v) || string.IsNullOrEmpty(v)) return 0;
            return v.Split(',').Length;
        }

        internal static int TrustedServerCount() => Store?.Count ?? 0;

        // ─────────── ip:port -> friendly name cache ───────────────────────
        // The server browser fetches a ServerPreviewData (incl. name) for
        // every visible row; the patch below records each one so the trusted-
        // server list in settings can show the friendly name instead of the
        // bare ip:port. The cache is in-memory only — once the user exits
        // the game, we fall back to "ip:port" until they reopen the browser.
        private static readonly Dictionary<string, string> _endpointToName =
            new Dictionary<string, string>();

        internal static string GetCachedServerName(string ipPortKey)
        {
            if (string.IsNullOrEmpty(ipPortKey)) return null;
            return _endpointToName.TryGetValue(ipPortKey, out var name) ? name : null;
        }

        // Capture every ServerPreviewData the browser stores so trusted-list
        // rendering can substitute a real name for ip:port. Hot path is tiny
        // (one dict write per server-row update) so the patch is unconditional.
        [HarmonyPatch(typeof(UIServerBrowser), "SetServerPreviewData")]
        private static class UIServerBrowser_SetServerPreviewData_Postfix
        {
            private static void Postfix(EndPoint endPoint, ServerPreviewData previewData)
            {
                try
                {
                    if (endPoint == null || previewData == null) return;
                    if (string.IsNullOrEmpty(previewData.name)) return;
                    string key = endPoint.ipAddress + ":" + endPoint.port;
                    _endpointToName[key] = previewData.name;
                }
                catch (Exception e) { Debug.LogWarning("[PPKB] server-name cache failed: " + e.Message); }
            }
        }

        internal static void Remove(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            var runner = KeybindRunner.Instance;
            var s = runner?.CommandConfig?.trustedServerMods;
            if (s == null) return;
            if (s.Remove(key)) runner.SaveConfigsAndRefresh();
        }

        internal static void RemoveAll()
        {
            var runner = KeybindRunner.Instance;
            var s = runner?.CommandConfig?.trustedServerMods;
            if (s == null || s.Count == 0) return;
            s.Clear();
            runner.SaveConfigsAndRefresh();
        }

        // ─────────────────────────── helpers ──────────────────────────────────

        private static string CurrentEndpointKey()
        {
            try
            {
                var ep = GlobalStateManager.ConnectionState.LastConnection?.EndPoint;
                return ep == null ? null : ep.ipAddress + ":" + ep.port;
            }
            catch { return null; }
        }

        // Sorted CSV of mod IDs. Order-insensitive comparison so a server
        // shuffling its required list doesn't break the trust.
        private static string FingerprintModIds(string[] modIds)
        {
            if (modIds == null || modIds.Length == 0) return "";
            var copy = (string[])modIds.Clone();
            Array.Sort(copy, StringComparer.Ordinal);
            return string.Join(",", copy);
        }

        private static bool IsTrustedForCurrentServer(string[] requiredModIds)
        {
            var store = Store;
            if (store == null) return false;
            string key = CurrentEndpointKey();
            if (string.IsNullOrEmpty(key)) return false;
            if (!store.TryGetValue(key, out string saved)) return false;
            return saved == FingerprintModIds(requiredModIds);
        }

        private static void RememberTrust(string[] requiredModIds)
        {
            var runner = KeybindRunner.Instance;
            var store = runner?.CommandConfig?.trustedServerMods;
            if (store == null) return;
            string key = CurrentEndpointKey();
            if (string.IsNullOrEmpty(key)) return;
            store[key] = FingerprintModIds(requiredModIds);
            runner.SaveConfigsAndRefresh();
        }

        private static void ForgetTrust()
        {
            var runner = KeybindRunner.Instance;
            var store = runner?.CommandConfig?.trustedServerMods;
            if (store == null) return;
            string key = CurrentEndpointKey();
            if (string.IsNullOrEmpty(key)) return;
            if (store.Remove(key)) runner.SaveConfigsAndRefresh();
        }

        // Re-implements the load-bearing side of the OK-click handler in
        // ConnectionManagerController. Without this the connection sits stuck
        // in AwaitingMods because nothing else kicks off the download/enable
        // pipeline.
        private static void EmulateOkClick(string[] requiredModIds)
        {
            try
            {
                if (GlobalStateManager.ReconnectionState.Phase != ReconnectionPhase.AwaitingMods) return;
                var enabled = ModManager.EnabledMods.Select(m => m.Id).ToArray();
                var ready   = ModManager.ReadyMods.Select(m => m.Id).ToArray();
                var pendingReadiness = requiredModIds.Except(ready).ToArray();
                var pendingEnabling  = requiredModIds.Except(pendingReadiness).Except(enabled).ToArray();
                GlobalStateManager.SetReconnectionState(new Dictionary<string, object>
                {
                    { "pendingReadinessModIds", pendingReadiness },
                    { "pendingEnablingModIds", pendingEnabling },
                });
            }
            catch (Exception e) { Debug.LogWarning("[PPKB] MissingMods OK-emulation failed: " + e.Message); }
        }

        // ─────────────────────────── patches ──────────────────────────────────

        // Prefix on the ShowPopup call. If the current server is trusted and
        // its required mod set still matches what we recorded, swallow the
        // popup and run the OK-click emulation directly.
        [HarmonyPatch(typeof(UIPopupManager), nameof(UIPopupManager.ShowPopup))]
        private static class ShowPopup_AutoConfirm_Prefix
        {
            private static bool Prefix(string name)
            {
                if (name != PopupName) return true;
                try
                {
                    var required = GlobalStateManager.ReconnectionState.ClientRequiredModIds;
                    var key = CurrentEndpointKey();
                    var store = Store;
                    string saved = (store != null && key != null && store.TryGetValue(key, out var s)) ? s : null;
                    var fingerprint = FingerprintModIds(required);
                    bool trusted = saved != null && saved == fingerprint;
                    // Diagnostic log gated on enableDebugLogging — popups are
                    // rare so log volume isn't bad either way, but matching the
                    // rest of PPI's logging convention.
                    if (KeybindRunner.Instance?.CommandConfig?.enableDebugLogging ?? false)
                        Debug.Log($"[PPKB] missingMods popup: key=\"{key ?? "<null>"}\" " +
                                  $"storeHas={(store != null && key != null && store.ContainsKey(key))} " +
                                  $"requiredCount={required?.Length ?? 0} " +
                                  $"fingerprintMatch={(saved != null ? (saved == fingerprint ? "yes" : "DIFFERS") : "no-entry")}");
                    if (!trusted) return true;
                    EmulateOkClick(required);
                    return false; // skip vanilla ShowPopup
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[PPKB] MissingMods auto-confirm failed: " + e.Message);
                    return true;
                }
            }
        }

        // Postfix on the popup's Initialize. Injects the "Don't show again for
        // this server" toggle into the popup body. Initial state mirrors whether
        // the server is currently trusted; toggling on/off writes/erases the
        // trust entry immediately so the next visit takes effect even if the
        // user dismisses the popup with the close button instead of OK.
        [HarmonyPatch(typeof(PopupMissingModsPopupContent), nameof(PopupMissingModsPopupContent.Initialize))]
        private static class Popup_Initialize_Postfix
        {
            private static void Postfix(PopupMissingModsPopupContent __instance)
            {
                try
                {
                    var root = __instance?.VisualElement;
                    if (root == null) return;
                    if (root.Q<Toggle>(ToggleName) != null) return; // double-init guard

                    var itemsField = AccessTools.Field(typeof(PopupMissingModsPopupContent), "steamWorkshopItems");
                    var items = itemsField?.GetValue(__instance) as SteamWorkshopItem[];
                    string[] modIds = items?.Select(i => i.Id).ToArray() ?? Array.Empty<string>();

                    bool initiallyTrusted = IsTrustedForCurrentServer(modIds);

                    var toggle = new Toggle("Don't show this popup again for this server")
                    {
                        name = ToggleName,
                        value = initiallyTrusted,
                    };
                    toggle.style.marginTop = 10;
                    toggle.style.fontSize = 13;
                    PoncePuck.Keybinds.KeybindRunner.StyleConfigCheckbox(toggle);
                    toggle.RegisterCallback<AttachToPanelEvent>(_ =>
                    {
                        var lbl = toggle.Q<Label>(className: "unity-toggle__label");
                        if (lbl != null) lbl.style.fontSize = 13;
                    });
                    toggle.RegisterCallback<ChangeEvent<bool>>(evt =>
                    {
                        if (evt.newValue) RememberTrust(modIds);
                        else              ForgetTrust();
                    });

                    var hint = new Label(
                        "Skipping is per-server. If this server changes its required mods, "
                        + "the popup will return so you can review the new set.");
                    hint.style.fontSize = 11;
                    hint.style.color = new Color(0.7f, 0.7f, 0.7f);
                    hint.style.marginTop = 2;
                    hint.style.whiteSpace = WhiteSpace.Normal;

                    root.Add(toggle);
                    root.Add(hint);
                }
                catch (Exception e) { Debug.LogWarning("[PPKB] MissingModsPopup inject failed: " + e.Message); }
            }
        }
    }
}
