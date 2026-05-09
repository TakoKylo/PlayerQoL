using System;
using System.Reflection;
using System.Linq;
using HarmonyLib;
using Unity.Collections;
using UnityEngine;

public sealed class PoncePuck_Keybinds_ClientMod : global::IPuckMod
{
    private Harmony _harmony;
    private GameObject _host;
    
    // Track if mod is enabled to prevent UI access when disabled
    public static bool IsModEnabled { get; private set; } = false;

    public bool OnEnable()
    {
        try
        {
            _harmony = new Harmony("net.poncepuck.keybinds");
            _harmony.PatchAll(Assembly.GetExecutingAssembly());

            // Initialize LocalMute config first
            PoncePuck.LocalMute.LocalMuteStore.EnsureLoaded();
            Debug.Log("[PPKB] LocalMute config initialized.");

            // Initialize kaomoji system
            PoncePuck.LocalMute.KaomojiSystem.Initialize();
            Debug.Log("[PPKB] Kaomoji system initialized.");

            // Preload ping sound to avoid fallback on first mention
            PoncePuck.Keybinds.PingSoundRuntime.PreloadSound();
            Debug.Log("[PPKB] Ping sound preloading started.");

            // Headless or no TMP? Skip UI.
            var hasTMP = HarmonyLib.AccessTools.TypeByName("TMPro.TMP_Text") != null;
            var isHeadless = Application.isBatchMode || !hasTMP;

            if (!isHeadless)
            {
                _host = new GameObject("[PPKB Host]");
                UnityEngine.Object.DontDestroyOnLoad(_host);
                _host.AddComponent<PoncePuck.Keybinds.KeybindRunner>();

                // Add LocalMute runner to the same host GameObject
                _host.AddComponent<PoncePuck.LocalMute.LocalMuteRunner>();
                Debug.Log("[PPKB] LocalMute runner added.");

                // Start live-gradient animation runner
                PoncePuck.Keybinds.LiveGradientSystem.Initialize(_host);
                Debug.Log("[PPKB] Live gradient system initialized.");

                // Initialize LocalMute patches
                InitializeLocalMutePatches(_harmony);
            }

            IsModEnabled = true;
            Debug.Log("[PPKB] Enabled.");
            return true;
        }
        catch (Exception e)
        {
            Debug.LogError("[PPKB] Failed to enable: " + e);
            try { _harmony?.UnpatchSelf(); } catch { }
            try { if (_host != null) UnityEngine.Object.Destroy(_host); } catch { }
            _harmony = null; _host = null;
            return false;
        }
    }

    public bool OnDisable()
    {
        IsModEnabled = false;

        // Shut down live gradient animation runner
        try { PoncePuck.Keybinds.LiveGradientSystem.Shutdown(); } catch { }

        try
        {
            // Track all current players before leaving
            PoncePuck.LocalMute.LocalMuteStore.TrackAllCurrentPlayers();
        }
        catch (Exception e)
        {
            Debug.LogError("[PPKB] Failed to track players on disable: " + e); 
        }
        
        try { _harmony?.UnpatchSelf(); } catch { }
        try { if (_host != null) UnityEngine.Object.Destroy(_host); } catch { }
        _harmony = null; _host = null;
        Debug.Log("[PPKB] Disabled.");
        return true;
    }

    private void InitializeLocalMutePatches(Harmony harmony)
    {
        try
        {
            // Initialize LocalMute patches using the existing harmony instance
            PoncePuck.LocalMute.LocalMuteClientMod.Patch_Chat_Receive(harmony);
            PoncePuck.LocalMute.LocalMuteClientMod.Patch_Voice_Receive(harmony);
            PoncePuck.LocalMute.LocalMuteClientMod.Patch_Chat_LocalCommands(harmony);
            PoncePuck.LocalMute.LocalMuteClientMod.Patch_Chat_AddMessage(harmony);
            PoncePuck.LocalMute.LocalMuteClientMod.Patch_Scoreboard_UI(harmony);
            Debug.Log("[PPKB] LocalMute patches initialized.");
        }
        catch (Exception e)
        {
            Debug.LogError("[PPKB] LocalMute patch initialization failed: " + e);
        }
    }

    // ---------------------------------------------------------------------
    // Chat receive patch (client) – swallow/retouch + forcibly rebuild the TMP line
    // ---------------------------------------------------------------------
    private void TryPatchChatReceive()
    {
        try
        {
            var uiChat = FindTypeByNameLoose("UIChat") ?? FindTypeByNameLoose("NS7.Puck.UIChat");
            if (uiChat == null) { Debug.LogWarning("[PPKB] UIChat type not found for receive."); return; }

            var targets = uiChat.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(m =>
                {
                    var n = m.Name;
                    if (n != "AddChatMessage" && n != "Client_AddChatMessage" && n != "Client_OnChatMessage") return false;
                    var ps = m.GetParameters();
                    // B310: AddChatMessage(ChatMessage, Units, bool) - first param is ChatMessage
                    return ps.Length >= 1 && (ps[0].ParameterType == typeof(string) || ps[0].ParameterType == typeof(ChatMessage));
                })
                .ToArray();

            if (targets.Length == 0) { Debug.LogWarning("[PPKB] Chat receive method not found."); return; }

            var harmonyType = Type.GetType("HarmonyLib.Harmony, 0Harmony");
            var hmType = Type.GetType("HarmonyLib.HarmonyMethod, 0Harmony");
            if (harmonyType == null || hmType == null) { Debug.LogWarning("[PPKB] Harmony not found; receive decoration disabled."); return; }

            if (_harmony == null) _harmony = (HarmonyLib.Harmony)Activator.CreateInstance(harmonyType, new object[] { "net.poncepuck.keybinds.client.recv2" });

            var pfx = typeof(PoncePuck_Keybinds_ClientMod).GetMethod(nameof(ChatReceive_Prefix), BindingFlags.Static | BindingFlags.NonPublic);
            var pox = typeof(PoncePuck_Keybinds_ClientMod).GetMethod(nameof(ChatReceive_Postfix), BindingFlags.Static | BindingFlags.NonPublic);
            if (pfx == null || pox == null) { Debug.LogWarning("[PPKB] ChatReceive_* methods missing."); return; }

            var hmCtor = hmType.GetConstructor(new[] { typeof(MethodInfo) });
            var patch = harmonyType.GetMethod("Patch", new[] { typeof(MethodBase), hmType, hmType, hmType, hmType });

            int patched = 0;
            foreach (var m in targets)
            {
                var hmPfx = hmCtor.Invoke(new object[] { pfx });
                var hmPox = hmCtor.Invoke(new object[] { pox });
                // Prefix priority high (run before any sanitization) and always allow original
                hmType.GetProperty("priority")?.SetValue(hmPfx, 400);
                patch.Invoke(_harmony, new object[] { m, hmPfx, hmPox, null, null });
                patched++;
            }
            Debug.Log($"[PPKB] Chat receive patched on {patched} method(s).");
        }
        catch (Exception e) { Debug.LogWarning("[PPKB] Chat receive patch failed: " + e); }
    }

    // Prefix: normalize + decorate the raw text
    // B310: first param is ChatMessage (not string), use __0 as object for compatibility
    private static void ChatReceive_Prefix(object[] __args)
    {
        try
        {
            if (__args == null || __args.Length == 0) return;
            if (__args[0] is ChatMessage cm)
            {
                var raw = cm.Content.ToString();
                var decorated = PoncePuck_Keybinds_ClientMod.DecorateChatBody(raw);
                cm.Content = new Unity.Collections.FixedString512Bytes(decorated);
                __args[0] = cm;
            }
            else if (__args[0] is string s)
            {
                __args[0] = PoncePuck_Keybinds_ClientMod.DecorateChatBody(s ?? "");
            }
        }
        catch { }
    }

    // Postfix: find the LAST label and overwrite it with rich-text decorated text.
    private static void ChatReceive_Postfix(object __instance, object[] __args)
    {
        try
        {
            string original = "";
            if (__args != null && __args.Length > 0)
            {
                if (__args[0] is ChatMessage cm) original = cm.Content.ToString();
                else if (__args[0] is string s) original = s ?? "";
            }
            string decorated = PoncePuck_Keybinds_ClientMod.DecorateChatBody(original);
            PoncePuck_Keybinds_ClientMod.ReplaceLastChatLabel(__instance, decorated);
        }
        catch { }
    }

    // ---------------------------------------------------------------------
    // Small utility (only keep ONE copy in your project)
    // ---------------------------------------------------------------------
    internal static Type FindTypeByNameLoose(string name)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var t = asm.GetType(name, false);
                if (t != null) return t;
                t = asm.GetTypes().FirstOrDefault(x => x.Name == name || x.FullName?.EndsWith("." + name) == true);
                if (t != null) return t;
            }
            catch { }
        }
        return null;
    }

    public static void ReplaceLastChatLabel(object uiChatInstance, string decoratedText)
        {
            // Dummy implementation: add your logic to replace the last chat label in the UI
            // For now, this method does nothing
        }

    public static string DecorateChatBody(string input)
    {
        // Example implementation: return input unchanged or decorate as needed
        return input;
    }
}
