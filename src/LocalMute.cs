// Unity 6000.0.44f1 / .NET 4.8 / C# 7.3
// Refs: 0Harmony.dll, Unity.Netcode.Runtime.dll, UnityEngine*.dll, UnityEngine.UIElementsModule.dll, Newtonsoft.Json.dll
//
// Local (client-only) text/voice mute with per-player VOIP volume slider.
// - LEFT-CLICK a scoreboard row => opens our overlay menu (drawn ON TOP of the scoreboard).
// - One menu at a time; opening another closes the previous. Click outside or hide the scoreboard -> closes.
// - Slider drags properly (we mask scoreboard clicks while a menu is open so UI Toolkit events reach the slider).
// - “View profile” tries UIScoreboard internal method, then Steam overlay, then web.
// - Name strike only (no emojis, no number changes).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UIElements;
using PoncePuck.LocalMute;
using System.Collections;
using UnityEngine.Rendering;
using UnityEngine.UI;
using System.Drawing.Printing;
using Steamworks;

#region Config / Store
namespace PoncePuck.LocalMute
{
    // ------------------------------- Config / Store -------------------------------
    [Serializable]
    public class PlayerInfo
    {
    public string steamId = "";
    public string playerName = "";
    public string playerNumber = ""; // Jersey number
    public string profileUrl = "";
    public string notes = "";
    public DateTime dateAdded = DateTime.Now;
    public DateTime lastSeen = DateTime.Now; // Last time the player was encountered
    public string lastServerSeen = ""; // Last server the player was seen on
    }

    [Serializable]
    public class LocalMuteConfig
    {
        public HashSet<ulong> Text = new HashSet<ulong>();
        public HashSet<ulong> Voice = new HashSet<ulong>();
        public Dictionary<ulong, int> VoiceVolumePercent = new Dictionary<ulong, int>(); // 0..200
        public List<PlayerInfo> SavedPlayers = new List<PlayerInfo>(); // Renamed from SavedPlayers
        public List<PlayerInfo> BlockedPlayers = new List<PlayerInfo>();
        public List<PlayerInfo> RecentPlayers = new List<PlayerInfo>(); // Last 50 players encountered
        public int Version = 9; // Increment version for new fields
    }

    public static class LocalMuteStore
    {
        private static string _configPath;
        private static DateTime _lastWrite;
        public static LocalMuteConfig Config { get; private set; } = new LocalMuteConfig();

        public static string ConfigPath
        {
            get
            {
                if (string.IsNullOrEmpty(_configPath))
                {
                    // Use ModHub/PlayerQoL config directory
                    string gameDir = Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath, ".."));
                    string configDir = Path.Combine(gameDir, "config");
                    string modHubDir = Path.Combine(configDir, "ModHub");
                    string playerQoLDir = Path.Combine(modHubDir, "PlayerQoL");
                    Directory.CreateDirectory(modHubDir);
                    Directory.CreateDirectory(playerQoLDir);
                    _configPath = Path.Combine(playerQoLDir, "PonceLocalMute.json");
                }
                return _configPath;
            }
        }

        public static void EnsureLoaded()
        {
            try
            {
                // Migrate from old locations if needed
                string gameDir = Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath, ".."));
                string configDir = Path.Combine(gameDir, "config");
                
                // Old location (direct in config/)
                string oldPath = Path.Combine(configDir, "PonceLocalMute.json");
                // Legacy location (config/playerinput/)
                string legacyPath = Path.Combine(configDir, "playerinput", "PonceLocalMute.json");
                
                if (File.Exists(legacyPath) && !File.Exists(ConfigPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
                    File.Copy(legacyPath, ConfigPath);
                    Debug.Log($"[LocalMute] Migrated config to ModHub/PlayerQoL");
                }
                else if (File.Exists(oldPath) && !File.Exists(ConfigPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
                    File.Copy(oldPath, ConfigPath);
                    Debug.Log($"[LocalMute] Migrated config from old location to ModHub/PlayerQoL");
                }
                
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
                if (!File.Exists(ConfigPath)) SaveAtomic();
                Load();
            }
            catch (Exception e) { Debug.LogError("[LocalMute] EnsureLoaded failed: " + e); }
        }

        public static void Load()
        {
            try
            {
                var json = File.ReadAllText(ConfigPath);
                Config = JsonConvert.DeserializeObject<LocalMuteConfig>(json) ?? new LocalMuteConfig();
                _lastWrite = File.GetLastWriteTimeUtc(ConfigPath);
                
                // Migration: Set lastSeen to dateAdded for existing players that don't have lastSeen
                bool needsMigration = false;
                foreach (var player in Config.SavedPlayers)
                {
                    if (player.lastSeen == DateTime.MinValue || player.lastSeen.Year < 2020)
                    {
                        player.lastSeen = player.dateAdded;
                        needsMigration = true;
                    }
                }
                foreach (var player in Config.BlockedPlayers)
                {
                    if (player.lastSeen == DateTime.MinValue || player.lastSeen.Year < 2020)
                    {
                        player.lastSeen = player.dateAdded;
                        needsMigration = true;
                    }
                }
                foreach (var player in Config.RecentPlayers)
                {
                    if (player.lastSeen == DateTime.MinValue || player.lastSeen.Year < 2020)
                    {
                        player.lastSeen = player.dateAdded;
                        needsMigration = true;
                    }
                }
                
                if (needsMigration)
                {
                    LogHelper.Log("[LocalMute] Migrated existing players: set lastSeen to dateAdded");
                    SaveAtomic(); // Save migrated data
                }
                
                LogHelper.Log($"[LocalMute] Loaded: text={Config.Text.Count}, voice={Config.Voice.Count}, vols={Config.VoiceVolumePercent.Count}");
            }
            catch (Exception e)
            {
                Debug.LogError("[LocalMute] Load failed: " + e);
                Config = new LocalMuteConfig();
            }
        }

        public static void SaveAtomic()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
                var tmp = ConfigPath + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(Config, Formatting.Indented));
                if (File.Exists(ConfigPath)) File.Replace(tmp, ConfigPath, null);
                else File.Move(tmp, ConfigPath);
                _lastWrite = File.GetLastWriteTimeUtc(ConfigPath);
            }
            catch (Exception e) { Debug.LogError("[LocalMute] SaveAtomic failed: " + e); }
        }

        public static void HotReloadIfChanged()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return;
                var wt = File.GetLastWriteTimeUtc(ConfigPath);
                if (wt > _lastWrite) Load();
            }
            catch (Exception e) { Debug.LogError("[LocalMute] HotReloadIfChanged failed: " + e); }
        }

        public static bool IsTextMuted(ulong sid) => sid != 0 && Config.Text.Contains(sid);
        public static bool IsVoiceMuted(ulong sid) => sid != 0 && Config.Voice.Contains(sid);

        public static int GetVoiceVolume(ulong sid)
        {
            if (sid == 0) return 100;
            return Config.VoiceVolumePercent.TryGetValue(sid, out var v) ? Mathf.Clamp(v, 0, 200) : 100;
        }

        public static void SetVoiceVolume(ulong sid, int pct)
        {
            if (sid == 0) return;
            Config.VoiceVolumePercent[sid] = Mathf.Clamp(pct, 0, 200);
            SaveAtomic();
        }

        public static void ToggleFullMute(ulong sid, bool muted)
        {
            if (sid == 0) return;
            if (muted) { Config.Text.Add(sid); Config.Voice.Add(sid); }
            else { Config.Text.Remove(sid); Config.Voice.Remove(sid); }
            SaveAtomic();
        }

        public static void MuteText(ulong sid, bool on)
        {
            if (sid == 0) return;
            if (on) Config.Text.Add(sid); else Config.Text.Remove(sid);
            SaveAtomic();
        }

        public static void MuteVoice(ulong sid, bool on)
        {
            if (sid == 0) return;
            if (on) Config.Voice.Add(sid); else Config.Voice.Remove(sid);
            SaveAtomic();
        }

        // Social management methods
        public static void AddToSaved(string steamId, string playerName, string profileUrl, string playerNumber = "")
        {
            if (string.IsNullOrEmpty(steamId)) return;
            
            // Remove from blocked if present
            Config.BlockedPlayers.RemoveAll(p => p.steamId == steamId);
            
            // Check if already in Saved
            if (Config.SavedPlayers.Any(p => p.steamId == steamId)) return;
            
            // Get current server name
            string serverName = "";
            try { serverName = GetCurrentServerName(); } catch { }
            
            // Add to Saved
            Config.SavedPlayers.Add(new PlayerInfo
            {
                steamId = steamId,
                playerName = playerName,
                playerNumber = playerNumber,
                profileUrl = profileUrl,
                dateAdded = DateTime.Now,
                lastSeen = DateTime.Now,
                lastServerSeen = serverName,
            });
            
            SaveAtomic();
            RefreshKeybindRunnerUI();
            
            // Refresh scoreboard underline
            if (ulong.TryParse(steamId, out ulong sid))
            {
                ScoreboardUtil.RefreshRowForSteamId(sid);
            }
        }

        public static void RemoveFromSaved(string steamId)
        {
            if (string.IsNullOrEmpty(steamId)) return;
            Config.SavedPlayers.RemoveAll(p => p.steamId == steamId);
            SaveAtomic();
            RefreshKeybindRunnerUI();
            
            // Refresh scoreboard underline
            if (ulong.TryParse(steamId, out ulong sid))
            {
                ScoreboardUtil.RefreshRowForSteamId(sid);
            }
        }

        public static void AddToBlocked(string steamId, string playerName, string profileUrl)
        {
            if (string.IsNullOrEmpty(steamId))
            {
                Debug.LogWarning("[PPKB] AddToBlocked: steamId is null or empty");
                return;
            }

            // Get player number and server name if available
            string playerNum = "?";
            string serverName = "";
            try
            {
                var playerManager = MonoBehaviourSingleton<PlayerManager>.Instance;
                if (playerManager != null && ulong.TryParse(steamId, out ulong steamIdUlong))
                {
                    var player = playerManager.GetPlayers(false)?.FirstOrDefault(p => RosterSnapshot.GetSteamId(p) == steamIdUlong);
                    if (player != null && player.Number != null)
                    {
                        playerNum = player.Number.Value.ToString();
                    }
                }
                serverName = GetCurrentServerName();
            }
            catch { }

            Debug.Log($"[PPKB] AddToBlocked called for: #{playerNum} {playerName} (SteamID: {steamId})");

            // Remove from saved if present
            Config.SavedPlayers.RemoveAll(p => p.steamId == steamId);

            // Check if already blocked
            if (Config.BlockedPlayers.Any(p => p.steamId == steamId))
            {
                Debug.Log($"[PPKB] Player #{playerNum} {playerName} is already in BlockedPlayers list");
                return;
            }
            
            // Add to blocked
            Config.BlockedPlayers.Add(new PlayerInfo
            {
                steamId = steamId,
                playerName = playerName,
                playerNumber = playerNum,
                profileUrl = profileUrl,
                dateAdded = DateTime.Now,
                lastSeen = DateTime.Now,
                lastServerSeen = serverName,
            });
            
            Debug.Log($"[PPKB] Added #{playerNum} {playerName} to BlockedPlayers. Total blocked: {Config.BlockedPlayers.Count}");
            
            SaveAtomic();
            RefreshKeybindRunnerUI();
            
            // Refresh scoreboard styling
            if (ulong.TryParse(steamId, out ulong sid))
            {
                ScoreboardUtil.RefreshRowForSteamId(sid);
            }
        }

        public static void RemoveFromBlocked(string steamId)
        {
            if (string.IsNullOrEmpty(steamId)) return;
            Config.BlockedPlayers.RemoveAll(p => p.steamId == steamId);
            SaveAtomic();
            RefreshKeybindRunnerUI();
            
            // Refresh scoreboard styling
            if (ulong.TryParse(steamId, out ulong sid))
            {
                ScoreboardUtil.RefreshRowForSteamId(sid);
            }
        }

        public static bool IsSaved(string steamId)
        {
            if (string.IsNullOrEmpty(steamId)) return false;
            return Config.SavedPlayers.Any(p => p.steamId == steamId);
        }

        public static bool IsBlocked(string steamId)
        {
            if (string.IsNullOrEmpty(steamId)) return false;
            return Config.BlockedPlayers.Any(p => p.steamId == steamId);
        }

        public static bool IsAdminModeEnabled()
        {
            try
            {
                // Only check for config toggle
                return PoncePuck.Keybinds.KeybindRunner.AdminModeEnabled;
            }
            catch (Exception e)
            {
                Debug.LogError($"[LocalMute] IsAdminModeEnabled error: {e}");
                return false;
            }
        }

        public static void TrackAllCurrentPlayers()
        {
            try
            {
                // Get all players from PlayerManager
                var playerManager = MonoBehaviourSingleton<PlayerManager>.Instance;
                if (playerManager == null)
                {
                    LogHelper.Log("[LocalMute] PlayerManager not found - cannot track players");
                    return;
                }

                var allPlayers = playerManager.GetPlayers(includeReplay: false);
                if (allPlayers == null || allPlayers.Count == 0)
                {
                    LogHelper.Log("[LocalMute] No players to track");
                    return;
                }

                int tracked = 0;
                foreach (var player in allPlayers)
                {
                    if (player == null || player.Username == null) continue;
                    
                    var sid = RosterSnapshot.GetSteamId(player);
                    if (sid == 0) continue;

                    var playerName = player.Username.Value.ToString();
                    if (string.IsNullOrEmpty(playerName)) continue;

                    string playerNum = player.Number?.Value.ToString() ?? "?";
                    LogHelper.Log($"[LocalMute] Tracking player #{playerNum} {playerName} (SteamID: {sid})");

                    TrackRecentPlayer(sid.ToString(), playerName, $"https://steamcommunity.com/profiles/{sid}");
                    tracked++;
                }

                LogHelper.Log($"[LocalMute] Tracked {tracked} players (will save when leaving server)");
                // Note: Don't save here - save happens when leaving server
            }
            catch (Exception e)
            {
                Debug.LogError("[LocalMute] TrackAllCurrentPlayers error: " + e);
            }
        }

        public static void TrackRecentPlayer(string steamId, string playerName, string profileUrl)
        {
            if (string.IsNullOrEmpty(steamId) || string.IsNullOrEmpty(playerName)) return;

            // Get player number and server name if available
            string playerNum = "";
            string serverName = "";
            try
            {
                var playerManager = MonoBehaviourSingleton<PlayerManager>.Instance;
                if (playerManager != null && ulong.TryParse(steamId, out ulong steamIdUlong))
                {
                    var player = playerManager.GetPlayers(false)?.FirstOrDefault(p => RosterSnapshot.GetSteamId(p) == steamIdUlong);
                    if (player != null && player.Number != null)
                    {
                        playerNum = player.Number.Value.ToString();
                    }
                }
                
                // Get current server name
                serverName = GetCurrentServerName();
            }
            catch { }

            // Update player name in Saved list if they're there
            var savedPlayer = Config.SavedPlayers.FirstOrDefault(p => p.steamId == steamId);
            if (savedPlayer != null)
            {
                if (savedPlayer.playerName != playerName)
                {
                    LogHelper.Log($"[LocalMute] Updating saved player name: #{playerNum} {savedPlayer.playerName} -> {playerName}");
                    savedPlayer.playerName = playerName;
                }
                // Update player number, server, and last seen time
                savedPlayer.playerNumber = playerNum;
                savedPlayer.lastServerSeen = serverName;
                savedPlayer.lastSeen = DateTime.Now;
                return; // Don't add to recent if in saved
            }

            // Update player name in Blocked list if they're there
            var blockedPlayer = Config.BlockedPlayers.FirstOrDefault(p => p.steamId == steamId);
            if (blockedPlayer != null)
            {
                if (blockedPlayer.playerName != playerName)
                {
                    LogHelper.Log($"[LocalMute] Updating blocked player name: #{playerNum} {blockedPlayer.playerName} -> {playerName}");
                    blockedPlayer.playerName = playerName;
                }
                // Update player number, server, and last seen time
                blockedPlayer.playerNumber = playerNum;
                blockedPlayer.lastServerSeen = serverName;
                blockedPlayer.lastSeen = DateTime.Now;
                return; // Don't add to recent if blocked
            }

            // Remove existing entry from recent if present (to update timestamp and move to front)
            var existingCount = Config.RecentPlayers.RemoveAll(p => p.steamId == steamId);
            
            // Add to front of list (silently - no per-player logs)
            Config.RecentPlayers.Insert(0, new PlayerInfo
            {
                steamId = steamId,
                playerName = playerName,
                playerNumber = playerNum,
                profileUrl = profileUrl,
                dateAdded = DateTime.Now,
                lastSeen = DateTime.Now,
                lastServerSeen = serverName,
            });

            // Keep only last 50 players
            while (Config.RecentPlayers.Count > 50)
            {
                Config.RecentPlayers.RemoveAt(Config.RecentPlayers.Count - 1);
            }

            // Note: Save is handled when leaving server, not on every player track
        }

        public static void RemoveFromRecent(string steamId)
        {
            if (string.IsNullOrEmpty(steamId)) return;
            Config.RecentPlayers.RemoveAll(p => p.steamId == steamId);
            SaveAtomic();
            RefreshKeybindRunnerUI();
        }

        public static void UpdatePlayerNotes(string steamId, string notes)
        {
            if (string.IsNullOrEmpty(steamId)) return;

            // Update notes in both saved and blocked lists
            var savedPlayer = Config.SavedPlayers.FirstOrDefault(p => p.steamId == steamId);
            if (savedPlayer != null)
            {
                savedPlayer.notes = notes ?? "";
            }

            var blockedPlayer = Config.BlockedPlayers.FirstOrDefault(p => p.steamId == steamId);
            if (blockedPlayer != null)
            {
                blockedPlayer.notes = notes ?? "";
            }

            SaveAtomic();
            RefreshKeybindRunnerUI();
        }

        public static string GetPlayerNotes(string steamId)
        {
            if (string.IsNullOrEmpty(steamId)) return "";

            var savedPlayer = Config.SavedPlayers.FirstOrDefault(p => p.steamId == steamId);
            if (savedPlayer != null) return savedPlayer.notes ?? "";

            var blockedPlayer = Config.BlockedPlayers.FirstOrDefault(p => p.steamId == steamId);
            if (blockedPlayer != null) return blockedPlayer.notes ?? "";

            return "";
        }

        public static void RefreshKeybindRunnerUI()
        {
            try
            {
                var keybindRunner = UnityEngine.Object.FindFirstObjectByType<PoncePuck.Keybinds.KeybindRunner>();
                if (keybindRunner != null)
                {
                    keybindRunner.RefreshUI();
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[LocalMute] Failed to refresh KeybindRunner UI: {e.Message}");
            }
        }

        // Returns the current server name or address, or empty string if not available
        public static string GetCurrentServerName()
        {
            try
            {
                // FIRST: Try to get server name from scoreboard (most reliable, shows custom server names)
                string scoreboardName = ScoreboardUtil.GetServerNameFromScoreboard();
                if (!string.IsNullOrEmpty(scoreboardName))
                {
                    return scoreboardName;
                }

                // FALLBACK: Try to get server address from Unity Transport
                var nm = Unity.Netcode.NetworkManager.Singleton;
                if (nm != null && nm.IsClient)
                {
                    var transport = nm.NetworkConfig.NetworkTransport;
                    var transportType = transport?.GetType();
                    if (transportType != null)
                    {
                        var addressProp = transportType.GetProperty("ConnectionAddress", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (addressProp != null)
                        {
                            var addr = addressProp.GetValue(transport) as string;
                            if (!string.IsNullOrEmpty(addr))
                                return addr;
                        }
                        var addressField = transportType.GetField("ConnectionAddress", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (addressField != null)
                        {
                            var addr = addressField.GetValue(transport) as string;
                            if (!string.IsNullOrEmpty(addr))
                                return addr;
                        }
                    }
                    // Last fallback: use NetworkManager name or status
                    return nm.name ?? "UnknownServer";
                }
            }
            catch { }
            return "";
        }
    }

    // Helper class for conditional logging
    public static class LogHelper
    {
        public static void Log(string message)
        {
            if (IsLiveLoggingEnabled())
            {
                Debug.Log(message);
            }
        }

        public static void LogWarning(string message)
        {
            if (IsLiveLoggingEnabled())
            {
                Debug.LogWarning(message);
            }
        }

        private static bool IsLiveLoggingEnabled()
        {
            try
            {
                var kbRunner = UnityEngine.Object.FindFirstObjectByType<PoncePuck.Keybinds.KeybindRunner>();
                return kbRunner != null && kbRunner.CommandConfig.enableLiveLogging;
            }
            catch
            {
                return true; // Default to enabled if we can't check
            }
        }
    }
    #endregion
    #region Mod Entry
    // ------------------------------- Mod Entry -------------------------------
    // NOTE: This class is now integrated into the main keybinds mod entry point
    // The IPuckMod interface is handled by PoncePuck_Keybinds_ClientMod in Client.Entry.cs
    public sealed class LocalMuteClientMod
    {
        // Reply tracking for @mentions
        private static string _lastMentioner = null;  // Username of last person who @mentioned you
        private static string _lastMentionMessage = null;  // The message that mentioned you
        private static DateTime _lastMentionTime = DateTime.MinValue; // When the mention happened
        
        // Ping sound debouncing to prevent multiple sounds
        private static DateTime _lastPingSoundTime = DateTime.MinValue;
        private const double PING_SOUND_COOLDOWN = 1.0; // 1 second cooldown between sounds
        
        // Message deduplication - cache of recent message hashes to prevent duplicate pings
        private static HashSet<int> _recentMessageHashes = new HashSet<int>();
        private static Queue<int> _messageHashQueue = new Queue<int>();
        private const int MAX_CACHED_MESSAGES = 10; // Keep track of last 10 messages
        

        // ------------------------------- Patches -------------------------------
        public static void Patch_Chat_Receive(Harmony h)
        {
            try
            {
                // B310: chat receive is ChatManager.Server_SendChatMessageRpc(ChatMessage, RpcParams)
                // (private). Suppress muted senders here so the message never reaches AddChatMessage.
                var target = AccessTools.Method(typeof(ChatManager), "Server_SendChatMessageRpc");
                if (target == null) { Debug.LogError("[LocalMute] Could not find ChatManager.Server_SendChatMessageRpc"); return; }
                h.Patch(target, prefix: new HarmonyMethod(typeof(LocalMuteClientMod), nameof(Chat_Receive_Prefix)));
                LogHelper.Log("[LocalMute] Patched ChatManager.Server_SendChatMessageRpc");
            }
            catch (Exception e) { Debug.LogError("[LocalMute] Patch_Chat_Receive failed: " + e); }
        }

        public static void Patch_Voice_Receive(Harmony h)
        {
            try
            {
                var t = typeof(PlayerVoiceRecorder);
                var target = AccessTools.Method(t, "Server_VoiceDataRpc", new[] { typeof(byte[]) });
                if (target == null) { Debug.LogError("[LocalMute] Could not find PlayerVoiceRecorder.Server_VoiceDataRpc"); return; }
                h.Patch(target, prefix: new HarmonyMethod(typeof(LocalMuteClientMod), nameof(Voice_Receive_Prefix)));
                LogHelper.Log("[LocalMute] Patched PlayerVoiceRecorder.Server_VoiceDataRpc");
            }
            catch (Exception e) { Debug.LogError("[LocalMute] Patch_Voice_Receive failed: " + e); }
        }

        public static void Patch_Chat_LocalCommands(Harmony h)
        {
            try
            {
                // B310: Client_SendChatMessage moved from UIChat to ChatManager
                var tChatMgr = typeof(ChatManager);
                var m = AccessTools.Method(tChatMgr, "Client_SendChatMessage", new[] { typeof(string), typeof(bool), typeof(bool) });
                if (m != null)
                {
                    h.Patch(m, prefix: new HarmonyMethod(typeof(LocalMuteClientMod), nameof(Chat_LocalCommand_Prefix)));
                    Debug.Log("[LocalMute] Patched ChatManager.Client_SendChatMessage (local commands)");
                }
            }
            catch (Exception e) { Debug.LogError("[LocalMute] Patch_Chat_LocalCommands failed: " + e); }
        }

        public static void Patch_Chat_AddMessage(Harmony h)
        {
            try
            {
                var tUI = typeof(UIChat);
                Debug.Log($"[LocalMute] Looking for UIChat.AddChatMessage...");
                // B310: AddChatMessage(ChatMessage, Units, bool)
                var m = AccessTools.Method(tUI, "AddChatMessage", new[] { typeof(ChatMessage), typeof(Units), typeof(bool) });
                if (m == null)
                {
                    Debug.LogError("[LocalMute] Could not find UIChat.AddChatMessage(ChatMessage, Units, bool)");
                    
                    // Try to find it without parameter matching
                    var allMethods = AccessTools.GetDeclaredMethods(tUI);
                    Debug.Log($"[LocalMute] All UIChat methods:");
                    foreach (var method in allMethods)
                    {
                        if (method.Name.Contains("Chat") || method.Name.Contains("Message"))
                        {
                            var parms = string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name));
                            Debug.Log($"[LocalMute]   {method.Name}({parms})");
                        }
                    }
                    return;
                }
                
                Debug.Log($"[LocalMute] Found AddChatMessage, applying postfix patch...");
                h.Patch(m, postfix: new HarmonyMethod(typeof(LocalMuteClientMod), nameof(Chat_AddMessage_Postfix)));
                Debug.Log("[LocalMute] Successfully patched UIChat.AddChatMessage (for @mention highlighting)");
            }
            catch (Exception e) { Debug.LogError("[LocalMute] Patch_Chat_AddMessage failed: " + e); }
        }

        public static void Patch_Scoreboard_UI(Harmony h)
        {
            try
            {
                var t = typeof(UIScoreboard);
                // B310: AddPlayer(Player) calls StylePlayer(Player). Both add new rows and refresh existing
                // rows route through StylePlayer, so postfixing StylePlayer is the single point for restyling.
                var add = AccessTools.Method(t, "AddPlayer", new[] { typeof(Player) });
                var style = AccessTools.Method(t, "StylePlayer", new[] { typeof(Player) });
                if (add != null) h.Patch(add, postfix: new HarmonyMethod(typeof(LocalMuteClientMod), nameof(Scoreboard_AddPlayer_Postfix)));
                if (style != null) h.Patch(style, postfix: new HarmonyMethod(typeof(LocalMuteClientMod), nameof(Scoreboard_UpdatePlayer_Postfix)));

                // B310: Hide() is virtual on UIView; UIScoreboard inherits it. Patch the inherited method
                // and filter by __instance type so we only react to scoreboard hides.
                var hide = AccessTools.Method(typeof(UIView), "Hide");
                if (hide != null)
                    h.Patch(hide, postfix: new HarmonyMethod(typeof(LocalMuteClientMod), nameof(Scoreboard_Hide_Postfix)));
            }
            catch (Exception e) { Debug.LogError("[LocalMute] Patch_Scoreboard_UI failed: " + e); }
        }

        // ------------------------------- RPC Prefixes -------------------------------
        // B310: ChatManager.Server_SendChatMessageRpc(ChatMessage chatMessage, RpcParams rpcParams)
        // The ChatMessage carries SteamID/Username/IsSystem directly, so we filter without parsing strings.
        public static bool Chat_Receive_Prefix(ChatMessage chatMessage)
        {
            try
            {
                // System messages: still apply the safe-echo filter for other server mods that
                // emit command echos. Real system messages from this game's ChatManager pass through.
                if (chatMessage.IsSystem)
                {
                    var content = chatMessage.Content.ToString();
                    if (IsSafeEchoFromOtherMod(content)) return false;
                    return true;
                }

                // Player message: drop it if the sender is text-muted.
                if (chatMessage.SteamID.HasValue)
                {
                    var sidStr = chatMessage.SteamID.Value.ToString();
                    if (ulong.TryParse(sidStr, out var sid) && LocalMuteStore.IsTextMuted(sid))
                        return false;
                }
                return true;
            }
            catch (Exception e) { Debug.LogError("[LocalMute] Chat_Receive_Prefix error: " + e); return true; }
        }

        
        // Postfix patch for UIChat.AddChatMessage - processes @mentions, kaomoji, and enables rich text
        // B310: AddChatMessage(ChatMessage, Units, bool) 
        public static void Chat_AddMessage_Postfix(UIChat __instance, ChatMessage chatMessage)
        {
            try
            {
                string message = chatMessage.Content.ToString();
                // B310 exposes the logical chat row container as "messages".
                // Fall back to the ScrollView content container if needed.
                var messagesRoot = AccessTools.Field(typeof(UIChat), "messages")?.GetValue(__instance) as UnityEngine.UIElements.VisualElement;
                if (messagesRoot == null)
                {
                    var chatScrollView = AccessTools.Field(typeof(UIChat), "scrollView")?.GetValue(__instance) as UnityEngine.UIElements.ScrollView;
                    messagesRoot = chatScrollView?.contentContainer;
                }

                if (messagesRoot == null || messagesRoot.childCount == 0)
                    return;

                UnityEngine.UIElements.Label lastLabel = null;
                for (int index = messagesRoot.childCount - 1; index >= 0 && lastLabel == null; index--)
                {
                    var child = messagesRoot[index];
                    lastLabel = child as UnityEngine.UIElements.Label ?? child?.Q<UnityEngine.UIElements.Label>();
                }
                if (lastLabel == null)
                    return;

                string originalText = lastLabel.text;

                // Respect the rich text toggle: only strip <noparse> and unlock rich text when enabled.
                // The game wraps player names/content in <noparse> specifically to prevent user-typed
                // HTML from rendering - if the toggle is OFF we leave noparse in place.
                bool richTextOn = IsRichTextEnabled();

                string workingText = richTextOn
                    ? originalText.Replace("<noparse>", "").Replace("</noparse>", "")
                    : originalText;

                // Markdown only makes sense when rich text will render.
                string processedText = richTextOn ? ProcessMarkdown(workingText) : workingText;

                // Kaomoji/emoji shortcodes are plain-text replacements - always safe.
                processedText = KaomojiSystem.ProcessKaomoji(processedText);
                
                // Then process @mentions if the message contains @
                bool wasMentioned = false;
                if (processedText.Contains("@"))
                {
                    var result = ProcessMentions(processedText);
                    processedText = result.Item1;
                    wasMentioned = result.Item2;
                    
                    // If we were mentioned, track the sender for /r command and play ping sound
                    if (wasMentioned)
                    {
                        string senderName = ExtractSenderName(processedText);
                        if (!string.IsNullOrEmpty(senderName))
                        {
                            _lastMentioner = senderName;
                            _lastMentionMessage = processedText;
                            _lastMentionTime = DateTime.Now;
                            Debug.Log($"[LocalMute] Mentioned by: {senderName}");
                        }
                        
                        // Create a unique key based on sender and time (rounded to nearest 100ms)
                        // This detects when the SAME message is processed multiple times (Prefix + Postfix)
                        // but allows different messages from same sender to play sound
                        long timeTicks = DateTime.Now.Ticks / 1000000; // Round to nearest 100ms
                        string messageKey = $"{senderName}_{timeTicks}";
                        int messageHash = messageKey.GetHashCode();
                        
                        // Only play sound if we haven't seen this exact message recently
                        if (!_recentMessageHashes.Contains(messageHash))
                        {
                            // Add to cache
                            _recentMessageHashes.Add(messageHash);
                            _messageHashQueue.Enqueue(messageHash);
                            
                            // Remove old hashes if cache is full
                            if (_messageHashQueue.Count > MAX_CACHED_MESSAGES)
                            {
                                int oldHash = _messageHashQueue.Dequeue();
                                _recentMessageHashes.Remove(oldHash);
                            }
                            
                            // Play ping sound with cooldown to prevent spam
                            try
                            {
                                DateTime now = DateTime.Now;
                                if ((now - _lastPingSoundTime).TotalSeconds >= PING_SOUND_COOLDOWN)
                                {
                                    PoncePuck.Keybinds.PingSoundRuntime.TryPlay();
                                    _lastPingSoundTime = now;
                                    Debug.Log($"[LocalMute] Played ping sound (cooldown: {PING_SOUND_COOLDOWN}s)");
                                }
                                else
                                {
                                    Debug.Log($"[LocalMute] Ping sound on cooldown ({(now - _lastPingSoundTime).TotalSeconds:F1}s ago)");
                                }
                            }
                            catch (Exception e)
                            {
                                Debug.LogError($"[LocalMute] Failed to play ping sound: {e}");
                            }
                        }
                        else
                        {
                            Debug.Log($"[LocalMute] Duplicate message detected, skipping ping sound");
                        }
                        
                        // Mention highlight only visible when rich text is on.
                        if (richTextOn)
                            processedText = $"<mark=#FFFF0019>{processedText}</mark>";
                    }
                }

                // Write processed text back; only enable rich text when the toggle permits it.
                if (processedText != originalText)
                {
                    lastLabel.text = processedText;
                    if (richTextOn)
                        lastLabel.enableRichText = true;
                }

                // Apply external custom emojis (images/GIFs) after text processing.
                // Shortcodes travel as ASCII, each client renders them as images locally.
                if (richTextOn)
                    CustomEmojiPack.TryApplyInlineEmojis(lastLabel, processedText);
            }
            catch (Exception e)
            {
                Debug.LogError($"[LocalMute] Chat_AddMessage_Postfix error: {e}");
            }
        }
        
        private static bool IsRichTextEnabled()
        {
            try
            {
                var runner = UnityEngine.Object.FindFirstObjectByType<PoncePuck.Keybinds.KeybindRunner>();
                if (runner == null) return true;
                var cmdField = typeof(PoncePuck.Keybinds.KeybindRunner).GetField("_cmd",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (cmdField == null) return true;
                var cmd = cmdField.GetValue(runner) as PoncePuck.Keybinds.CommandKeybindConfig;
                return cmd == null || cmd.enableChatRichText;
            }
            catch { return true; }
        }

        // Helper to detect safe echo messages from other server mods
        private static bool IsSafeEchoFromOtherMod(string message)
        {
            if (string.IsNullOrEmpty(message)) return false;
            
            // Common safe echo patterns from server mods:
            // "✅ <command>" - success echo
            // "❌ <command>" - error echo
            // "⚠️ <command>" - warning echo
            // "<SERVER>" prefix
            // "Adjusted client..." - server adjustment messages
            // "Disabled/Enabled..." - server state messages
            // "Command /... not recognized" - command not found messages
            // Messages that echo back commands without player names
            
            // Check for emoji indicators
            if (message.StartsWith("✅") || message.StartsWith("❌") || message.StartsWith("⚠️"))
            {
                return true;
            }
            
            // Check for <SERVER> prefix
            if (message.StartsWith("<SERVER>") || message.StartsWith("[SERVER]"))
            {
                return true;
            }
            
            // Check for "Command /... not recognized" messages (case insensitive)
            string lower = message.ToLower();
            if (lower.StartsWith("command /") && lower.Contains("not recognized"))
            {
                return true;
            }
            
            // Check for server adjustment messages
            if (lower.StartsWith("adjusted client") || 
                lower.StartsWith("disabled ") ||
                lower.StartsWith("enabled ") ||
                lower.StartsWith("set ") ||
                lower.Contains("wrote client config"))
            {
                return true;
            }
            
            // Check for common server command echoes without player context
            // Usually these don't have a colon (no "PlayerName: message" format)
            if (!message.Contains(":"))
            {
                // Check if it looks like a command echo (starts with /)
                string trimmed = message.Trim();
                if (trimmed.StartsWith("/") || trimmed.StartsWith("Command:"))
                {
                    return true;
                }
            }
            
            return false;
        }

        public static bool Voice_Receive_Prefix(PlayerVoiceRecorder __instance, byte[] voiceData)
        {
            try
            {
                var player = __instance ? __instance.GetComponent<Player>() : null;
                ulong steam = RosterSnapshot.GetSteamId(player);
                
                // Track voice activity for indicators
                if (player != null && steam != 0)
                {
                    string playerName = player.Username.Value.ToString();
                    PlayerTypingDetector.UpdateVoiceActivity(steam, playerName);
                }
                
                if (LocalMuteStore.IsVoiceMuted(steam)) return false;
                VoiceUtil.ApplyVolumeForPlayer(player, steam);
            }
            catch (Exception e) { Debug.LogError("[LocalMute] Voice_Receive_Prefix error: " + e); }
            return true;
        }

        /// <summary>
        /// Count visible characters that will be sent in chat message.
        /// Strips rich text tags like <color>, <b>, <i>, <size>, etc. but keeps the text content.
        /// This prevents anti-cheat bans from messages that appear short but have hidden rich text.
        /// </summary>
        private static int CountVisibleCharacters(string message)
        {
            if (string.IsNullOrEmpty(message)) return 0;
            
            var sb = new System.Text.StringBuilder();
            bool inTag = false;
            
            foreach (char c in message)
            {
                if (c == '<')
                {
                    inTag = true;
                    continue;
                }
                if (c == '>')
                {
                    inTag = false;
                    continue;
                }
                if (!inTag)
                {
                    sb.Append(c);
                }
            }
            
            return sb.Length;
        }

        // B310: AddChatMessage helper - uses ChatManager to display local messages
        private static void AddLocalChatMessage(ChatManager chatMgr, string text)
        {
            var msg = new ChatMessage();
            msg.Content = new Unity.Collections.FixedString512Bytes(text);
            msg.IsSystem = true;
            chatMgr.AddChatMessage(msg);
        }

        // B310: ChatManager.Client_SendChatMessage(string content, bool isQuickChat, bool isTeamChat)
        public static bool Chat_LocalCommand_Prefix(ChatManager __instance, ref string content, bool isQuickChat, bool isTeamChat)
        {
            // Keep 'message' as local alias for body compatibility
            ref string message = ref content;
            try
            {
                if (string.IsNullOrEmpty(message)) return true;

                // Normalize long-form whisper alias to the short form B310 actually handles reliably.
                if (message.StartsWith("/whisper ", StringComparison.OrdinalIgnoreCase))
                {
                    message = "/w " + message.Substring("/whisper ".Length);
                }
                
                // Process /r command FIRST to expand it to @mention format
                if (message.TrimStart().StartsWith("/"))
                {
                    var messageParts = message.Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
                    var command = messageParts[0].ToLowerInvariant();
                    
                    // Handle /r (reply to last mention) - expand it to @mention
                    if (command == "/r" || command == "/reply")
                    {
                        if (string.IsNullOrEmpty(_lastMentioner))
                        {
                            AddLocalChatMessage(__instance, "<b><color=#CCCCCC>[Reply]</color></b> No recent mentions to reply to.");
                            return false;
                        }
                        
                        // Check if mention is recent (within last 5 minutes)
                        if ((DateTime.Now - _lastMentionTime).TotalMinutes > 5)
                        {
                            AddLocalChatMessage(__instance, $"<b><color=#CCCCCC>[Reply]</color></b> Last mention from <b>{_lastMentioner}</b> was over 5 minutes ago.");
                            return false;
                        }
                        
                        // Build the reply message
                        string replyText = "";
                        if (messageParts.Length >= 2)
                        {
                            // Get the message part (everything after /r)
                            replyText = messageParts.Length == 2 ? messageParts[1] : messageParts[1] + " " + messageParts[2];
                        }
                        else
                        {
                            AddLocalChatMessage(__instance, "<b><color=#CCCCCC>[Reply]</color></b> Usage: /r <message>");
                            return false;
                        }
                        
                        // Replace the message with @mention format
                        message = $"@{_lastMentioner} {replyText}";
                        
                        // Show context preview (local only)
                        if (!string.IsNullOrEmpty(_lastMentionMessage))
                        {
                            int colonIndex = _lastMentionMessage.IndexOf(':');
                            if (colonIndex > 0 && colonIndex < _lastMentionMessage.Length - 1)
                            {
                                string contextMsg = _lastMentionMessage.Substring(colonIndex + 1).Trim();
                                AddLocalChatMessage(__instance, $"<color=#888888><size=16>Replying to: {_lastMentioner}: {contextMsg}</size></color>");
                            }
                        }
                        
                        // Continue to validation below (don't return yet)
                    }
                }
                
                // CRITICAL: Check character limit AFTER /r expansion to prevent anti-cheat bans
                // Count actual characters that will be sent (strip rich text tags but keep visible text)
                int actualLength = CountVisibleCharacters(message);
                if (actualLength > 128)
                {
                    AddLocalChatMessage(__instance, $"<b><color=#FF0000>[Error]</color></b> Message too long ({actualLength}/128 characters). Anti-cheat will ban you if sent!");
                    return false; // Block the message
                }
                
                // Regular chat (non-command) — shortcodes stay as plain ASCII in transit;
                // the display-side patch (Chat_AddMessage_Postfix) converts them to emoji
                // on every client, avoiding server-side stripping of raw Unicode.
                if (message[0] != '/')
                {
                    return true;
                }
                
                // It's a command - parse it
                var parts = message.Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
                var cmd = parts[0].ToLowerInvariant();
                
                // /r command was already handled above before validation
                // Skip the duplicate handling here
                
                // Whisper messages — shortcodes also stay as-is in transit.
                if (cmd == "/whisper" || cmd == "/w")
                {
                    return true; // Let it continue to normal whisper handling
                }
                
                // Handle our local mute commands
                if (cmd != "/lmute" && cmd != "/lunmute" && cmd != "/lmuted" && cmd != "/lm" && cmd != "/lum") 
                    return true;

                var roster = RosterSnapshot.Build();

                if (cmd == "/lmuted")
                {
                    string fmt(ulong id) => id.ToString();
                    var textList = string.Join(", ", LocalMuteStore.Config.Text.Select(fmt));
                    var voiceList = string.Join(", ", LocalMuteStore.Config.Voice.Select(fmt));
                    AddLocalChatMessage(__instance, $"<b><color=#FF9500>[LocalMute]</color></b> Text: {textList}  |  Voice: {voiceList}");
                    return false;
                }

                if (parts.Length < 2)
                {
                    AddLocalChatMessage(__instance, "<b><color=#FF9500>[LocalMute]</color></b> Usage: /lmute <name|#num|steamId>  |  /lunmute <name|#num|steamId>");
                    return false;
                }

                var token = parts[1];
                if (!roster.ResolveToken(token, out var pinfo))
                {
                    if (ulong.TryParse(token, out var rawSid) && rawSid != 0)
                        pinfo = new RosterSnapshot.PlayerInfo { SteamId = rawSid, Name = "(offline)" };
                    else { AddLocalChatMessage(__instance, "<b><color=#FF9500>[LocalMute]</color></b> Player not found."); return false; }
                }

                if (cmd == "/lm" || cmd == "/lmute")
                {
                    LocalMuteStore.ToggleFullMute(pinfo.SteamId, true);
                    AddLocalChatMessage(__instance, $"<b><color=#FF9500>[LocalMute]</color></b> Muted <b>{pinfo.DisplayHeader}</b> locally (text+voice).");
                }
                else
                {
                    LocalMuteStore.ToggleFullMute(pinfo.SteamId, false);
                    AddLocalChatMessage(__instance, $"<b><color=#FF9500>[LocalMute]</color></b> Unmuted <b>{pinfo.DisplayHeader}</b> locally.");
                }

                ScoreboardUtil.RefreshRowForSteamId(pinfo.SteamId);
                return false;
            }
            catch (Exception e) { Debug.LogError("[LocalMute] Chat_LocalCommand_Prefix error: " + e); return true; }
        }

        // ------------------------------- Scoreboard hooks -------------------------------
        public static void Scoreboard_AddPlayer_Postfix(UIScoreboard __instance, Player player)
        {
            try 
            { 
                var playerName = player?.Username.Value.ToString() ?? "null";
                Debug.Log($"[LocalMute] AddPlayer called for: {playerName}");
                Scoreboard_UpdatePlayerUI(__instance, player); 
            } 
            catch (Exception e) { Debug.LogError("[LocalMute] AddPlayer postfix error: " + e); }
        }

        public static void Scoreboard_UpdatePlayer_Postfix(UIScoreboard __instance, Player player)
        {
            try 
            { 
                // Removed debug log to reduce spam
                Scoreboard_UpdatePlayerUI(__instance, player); 
            } 
            catch (Exception e) { Debug.LogError("[LocalMute] UpdatePlayer postfix error: " + e); }
        }

        // Patched onto UIView.Hide(); fires for every UIView. Only react when the scoreboard hides.
        public static void Scoreboard_Hide_Postfix(UIView __instance)
        {
            if (__instance is UIScoreboard) ScoreboardUtil.CloseAllMenus();
        }

        internal static void Scoreboard_UpdatePlayerUI(UIScoreboard ui, Player player)
        {
            if (!player) return;
            var sid = RosterSnapshot.GetSteamId(player);
            var row = ScoreboardUtil.GetPlayerRow(ui, player);
            if (row == null) return;
            
            // Track recent players silently (update config only, no UI refresh)
            if (sid != 0 && player.Username != null)
            {
                var playerName = player.Username.Value.ToString();
                if (!string.IsNullOrEmpty(playerName))
                {
                    // Track player in config (no UI refresh - user clicks Update button to see changes)
                    LocalMuteStore.TrackRecentPlayer(sid.ToString(), playerName, $"https://steamcommunity.com/profiles/{sid}");
                }
            }
            ScoreboardUtil.EnsureVisibilityCloseHook(ui);
            // LEFT click opens menu (and prevents default profile-open on left click)
            ScoreboardUtil.BindLeftClickOpensMenu(ui, row);
            // RIGHT click opens admin menu (if enabled)
            ScoreboardUtil.BindRightClickOpensAdminMenu(ui, row);

            // Name-only strike while muted and underline while saved
            bool isMuted = LocalMuteStore.IsTextMuted(sid) || LocalMuteStore.IsVoiceMuted(sid);
            bool isSaved = LocalMuteStore.IsSaved(sid.ToString());
            ScoreboardUtil.ApplyPlayerStyling_NameOnly(row, player, isMuted, isSaved);
        }

        // ------------------------------- Helpers -------------------------------
        private static string StripRichText(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new System.Text.StringBuilder(s.Length);
            bool tag = false;
            foreach (var ch in s)
            {
                if (ch == '<') { tag = true; continue; }
                if (ch == '>') { tag = false; continue; }
                if (!tag) sb.Append(ch);
            }
            return sb.ToString();
        }

        private static string ExtractHeaderName(string s)
        {
            var colon = s.IndexOf(':');
            if (colon <= 0) return StripRichText(s).Trim();
            return StripRichText(s.Substring(0, colon)).Trim();
        }

        /// <summary>
        /// Process Discord-style markdown formatting in chat messages
        /// ***bold+italic*** -> bold and italic
        /// **bold** -> bold text
        /// *italic* -> italic text
        /// __underline__ -> underline text
        /// ~~strikethrough~~ -> strikethrough text
        /// ||spoiler|| -> hidden text (opaque gray background)
        /// `code` -> monospace code
        /// </summary>
        private static string ProcessMarkdown(string message)
        {
            if (string.IsNullOrEmpty(message))
                return message;

            try
            {
                string result = message;
                
                // Process in order of specificity (most specific first to avoid conflicts)
                
                // 1. ***bold italic*** (3 asterisks) - must be done before ** and *
                result = ProcessPairedMarkdown(result, "***", "<b><i>", "</i></b>");
                
                // 2. **bold** (2 asterisks)
                result = ProcessPairedMarkdown(result, "**", "<b>", "</b>");
                
                // 3. *italic* (1 asterisk)
                result = ProcessPairedMarkdown(result, "*", "<i>", "</i>");
                
                // 4. __underline__ (2 underscores)
                result = ProcessPairedMarkdown(result, "__", "<u>", "</u>");
                
                // 5. ~~strikethrough~~ (2 tildes)
                result = ProcessPairedMarkdown(result, "~~", "<s>", "</s>");
                
                // 6. ||spoiler|| (2 pipes - hidden text with opaque gray background)
                result = ProcessPairedMarkdown(result, "||", "<mark=#888888FF><color=#888888FF>", "</color></mark>");
                
                // 7. `code` (1 backtick - monospace)
                result = ProcessPairedMarkdown(result, "`", "<color=#E8E8E8><b>", "</b></color>");
                
                return result;
            }
            catch (Exception e)
            {
                Debug.LogError($"[LocalMute] ProcessMarkdown error: {e}");
                return message;
            }
        }

        private static string ProcessPairedMarkdown(string text, string marker, string openTag, string closeTag)
        {
            if (string.IsNullOrEmpty(text) || !text.Contains(marker))
                return text;

            var sb = new System.Text.StringBuilder();
            int lastIndex = 0;
            int count = 0;
            
            while (lastIndex < text.Length)
            {
                int index = text.IndexOf(marker, lastIndex);
                if (index == -1)
                {
                    // No more markers, append the rest
                    sb.Append(text.Substring(lastIndex));
                    break;
                }
                
                // Append text before marker
                sb.Append(text.Substring(lastIndex, index - lastIndex));
                
                // Append open or close tag
                if (count % 2 == 0)
                    sb.Append(openTag);
                else
                    sb.Append(closeTag);
                
                count++;
                lastIndex = index + marker.Length;
            }
            
            return sb.ToString();
        }

        /// <summary>
        /// Process @mentions in chat messages
        /// - Shows @username in purple for EVERYONE
        /// - Returns wasMentioned=true if local player was mentioned
        /// - Caller wraps full message in light yellow if wasMentioned=true
        /// Supports: @playername, @everyone, @here, @red, @blue, @spec, @admin, @donor
        /// Returns: (processedMessage, wasMentioned)
        /// </summary>
        private static Tuple<string, bool> ProcessMentions(string message)
        {
            if (string.IsNullOrEmpty(message) || !message.Contains("@"))
                return Tuple.Create(message, false);

            try
            {
                // Get local player using PlayerManager
                var localPlayer = MonoBehaviourSingleton<PlayerManager>.Instance?.GetLocalPlayer();
                if (localPlayer == null)
                {
                    try
                    {
                        var localPlayerObject = NetworkManager.Singleton?.LocalClient?.PlayerObject;
                        localPlayer = localPlayerObject != null ? localPlayerObject.GetComponent<Player>() : null;
                    }
                    catch { }
                }
                if (localPlayer == null)
                    return Tuple.Create(message, false);

                var localSteamId = RosterSnapshot.GetSteamId(localPlayer);
                
                // Get player's team and role info
                var roster = RosterSnapshot.Build();
                var localPlayerInfo = roster.Players.FirstOrDefault(p => p.SteamId == localSteamId);
                
                // Get clean username without tags (consistent with ExtractUsername)
                string localUsername = "";
                if (localPlayerInfo != null && !string.IsNullOrEmpty(localPlayerInfo.Name))
                {
                    localUsername = RosterSnapshot.ExtractUsername(localPlayerInfo.Name).ToLower();
                }
                else
                {
                    localUsername = StripRichText(localPlayer.Username?.Value.ToString() ?? "").ToLower();
                }
                
                PlayerTeam playerTeam = localPlayer.Team; // B310: Team is now a direct PlayerTeam property
                bool isSpectator = playerTeam == PlayerTeam.Spectator;
                string localTeam = playerTeam.ToString();
                bool isAdmin = LocalMuteStore.IsAdminModeEnabled();
                bool isDonor = localPlayerInfo != null && (localPlayerInfo.DisplayHeader.Contains("[Donor]") || localPlayerInfo.DisplayHeader.Contains("[D]"));
                
                var sb = new System.Text.StringBuilder();
                int lastPos = 0;
                bool wasMentioned = false;
                
                // Find all @ symbols and check if they're mentions
                for (int i = 0; i < message.Length; i++)
                {
                    if (message[i] == '@')
                    {
                        // Extract the mention - skip brackets and special chars, get the actual username
                        int mentionStart = i;
                        int mentionEnd = i + 1;
                        
                        // Skip any leading brackets like [A], [D], [Admin], etc after the @
                        while (mentionEnd < message.Length && message[mentionEnd] == '[')
                        {
                            // Skip to the closing bracket
                            while (mentionEnd < message.Length && message[mentionEnd] != ']')
                            {
                                mentionEnd++;
                            }
                            if (mentionEnd < message.Length && message[mentionEnd] == ']')
                            {
                                mentionEnd++; // Skip the ]
                            }
                            // Skip any spaces after the bracket
                            while (mentionEnd < message.Length && message[mentionEnd] == ' ')
                            {
                                mentionEnd++;
                            }
                        }
                        
                        // Now extract the alphanumeric username
                        int usernameStart = mentionEnd;
                        while (mentionEnd < message.Length && 
                               (char.IsLetterOrDigit(message[mentionEnd]) || message[mentionEnd] == '_'))
                        {
                            mentionEnd++;
                        }
                        
                        if (mentionEnd > usernameStart) // Has at least one character in the username
                        {
                            // Extract just the username part (without brackets)
                            string mention = message.Substring(usernameStart, mentionEnd - usernameStart).ToLower();
                            bool isLocalPlayerMentioned = false;
                            bool isValidMention = false; // Only highlight if mention is valid
                            
                            // Check special mentions
                            if (mention == "everyone")
                            {
                                isLocalPlayerMentioned = true;
                                isValidMention = true;
                            }
                            else if (mention == "here")
                            {
                                // Mention for players not spectating
                                isLocalPlayerMentioned = !isSpectator;
                                isValidMention = true;
                            }
                            else if (mention == "red")
                            {
                                isLocalPlayerMentioned = localTeam == "Red";
                                isValidMention = true;
                            }
                            else if (mention == "blue")
                            {
                                isLocalPlayerMentioned = localTeam == "Blue";
                                isValidMention = true;
                            }
                            else if (mention == "spec")
                            {
                                isLocalPlayerMentioned = isSpectator;
                                isValidMention = true;
                            }
                            else if (mention == "admin")
                            {
                                isLocalPlayerMentioned = isAdmin;
                                isValidMention = true;
                            }
                            else if (mention == "donor")
                            {
                                isLocalPlayerMentioned = isDonor;
                                isValidMention = true;
                            }
                            else if (mention == localUsername)
                            {
                                // Direct username mention
                                isLocalPlayerMentioned = true;
                                isValidMention = true;
                            }
                            else
                            {
                                // Try fuzzy matching with roster - find closest player name
                                if (roster.ResolveToken(mention, out var matchedPlayer))
                                {
                                    isValidMention = true; // Player exists in roster
                                    // Check if matched player is local player
                                    if (matchedPlayer.SteamId == localSteamId)
                                    {
                                        isLocalPlayerMentioned = true;
                                    }
                                }
                            }
                            
                            // Track if we were mentioned
                            if (isLocalPlayerMentioned)
                            {
                                wasMentioned = true;
                            }
                            
                            // Add text before mention
                            sb.Append(message.Substring(lastPos, mentionStart - lastPos));
                            
                            // Only show purple highlight if it's a valid mention (player exists)
                            if (isValidMention)
                            {
                                sb.Append("<mark=#BB88FF80>@"); // Purple background highlight for @mentions (semi-transparent)
                                sb.Append(message.Substring(i + 1, mentionEnd - i - 1));
                                sb.Append("</mark>");
                            }
                            else
                            {
                                // Not a valid mention, show as plain text
                                sb.Append("@");
                                sb.Append(message.Substring(i + 1, mentionEnd - i - 1));
                            }
                            
                            lastPos = mentionEnd;
                            i = mentionEnd - 1; // -1 because loop will increment
                        }
                    }
                }
                
                // Add remaining text
                if (lastPos < message.Length)
                {
                    sb.Append(message.Substring(lastPos));
                }
                
                return Tuple.Create(sb.ToString(), wasMentioned);
            }
            catch (Exception e)
            {
                Debug.LogError($"[LocalMute] ProcessMentions error: {e}");
                return Tuple.Create(message, false);
            }
        }
        
        /// <summary>
        /// Extract the sender name from a chat message (format: "Sender: message" or "<bold>Sender</bold>: message")
        /// Returns clean username without prefixes like [Admin], [Donor], or #number
        /// </summary>
        private static string ExtractSenderName(string message)
        {
            if (string.IsNullOrEmpty(message)) return null;
            
            try
            {
                int colonIndex = message.IndexOf(':');
                if (colonIndex <= 0) return null;
                
                string beforeColon = message.Substring(0, colonIndex);
                // Strip any rich text tags
                string withoutRichText = StripRichText(beforeColon).Trim();
                
                // For format: "[Admin] #62 [A] Amikiir:" 
                // We want: "[A] Amikiir" (with position tags like [A], [D], but not [M], [Mod])
                // Find the first # followed by a number, then take everything AFTER the space following that number
                
                int hashIndex = withoutRichText.IndexOf('#');
                if (hashIndex >= 0)
                {
                    // Find the space after the number
                    int spaceAfterNumber = withoutRichText.IndexOf(' ', hashIndex);
                    if (spaceAfterNumber > 0 && spaceAfterNumber < withoutRichText.Length - 1)
                    {
                        // Take everything after that space (includes tags like [A], [D], etc.)
                        string nameWithTags = withoutRichText.Substring(spaceAfterNumber + 1).Trim();
                        
                        // Remove [M] and [Mod] tags specifically (but keep [A], [D], [G], etc.)
                        nameWithTags = nameWithTags.Replace("[M] ", "").Replace("[Mod] ", "");
                        nameWithTags = nameWithTags.Replace("[M]", "").Replace("[Mod]", "");
                        
                        // Remove unicode emojis from the result
                        var cleanSb = new System.Text.StringBuilder();
                        foreach (var ch in nameWithTags)
                        {
                            // Keep ASCII, brackets, and letters
                            if (ch <= 127 || ch == '[' || ch == ']' || char.IsLetter(ch) || char.IsWhiteSpace(ch))
                            {
                                cleanSb.Append(ch);
                            }
                        }
                        
                        return cleanSb.ToString().Trim();
                    }
                }
                
                // Fallback: if no #number format found, remove leading bracket tags
                string result = withoutRichText;
                while (result.StartsWith("["))
                {
                    int closeBracket = result.IndexOf(']');
                    if (closeBracket > 0 && closeBracket < result.Length - 1)
                    {
                        result = result.Substring(closeBracket + 1).Trim();
                    }
                    else
                    {
                        break; // Malformed bracket, stop
                    }
                }
                
                return result;
            }
            catch
            {
                return null;
            }
        }

        // Open menu entry point for util
        internal static void OpenMenuFor(UIScoreboard ui, VisualElement row, Player player) =>
            ScoreboardUtil.OpenOverlayMenu(ui, row, player);
        
        // Open admin menu entry point
        internal static void OpenAdminMenuFor(UIScoreboard ui, VisualElement row, Player player) =>
            ScoreboardUtil.OpenAdminOverlayMenu(ui, row, player);
    }
    #endregion
    #region RosterSnapshot
    // ------------------------------- Roster Snapshot -------------------------------
    internal class RosterSnapshot
    {
        public class PlayerInfo
        {
            public ulong SteamId;
            public string Name;
            public int Number;
            public PlayerTeam Team;
            public string DisplayHeader => $"#{Number:D2} {Name}";
        }

        public readonly List<PlayerInfo> Players = new List<PlayerInfo>();

        public static RosterSnapshot Build()
        {
            var snap = new RosterSnapshot();
            try
            {
                var nm = NetworkManager.Singleton;
                if (nm != null)
                {
                    foreach (var cc in nm.ConnectedClientsList)
                    {
                        var p = cc.PlayerObject ? cc.PlayerObject.GetComponent<Player>() : null;
                        if (!p) continue;
                        snap.Players.Add(new PlayerInfo
                        {
                            SteamId = GetSteamId(p),
                            Name = p.Username != null ? p.Username.Value.ToString() : "(?)",
                            Number = (p.Number != null) ? p.Number.Value : 0,
                            Team = p.Team // B310: Team is now a direct PlayerTeam property
                        });
                    }
                }
            }
            catch (Exception e) { Debug.LogError("[LocalMute] Roster build error: " + e); }
            return snap;
        }

        public static ulong GetSteamId(Player p)
        {
            try
            {
                if (p?.SteamId != null && ulong.TryParse(p.SteamId.Value.ToString(), out var val)) return val;
            }
            catch { }
            return 0UL;
        }

        public bool TryFindByDisplayHeader(string header, out PlayerInfo info)
        {
            header = header?.Trim() ?? "";
            foreach (var p in Players)
            {
                if (header.Equals($"#{p.Number:D2} {p.Name}", StringComparison.Ordinal) ||
                    header.Equals(p.Name, StringComparison.Ordinal) ||
                    header.EndsWith(" " + p.Name, StringComparison.Ordinal))
                { info = p; return true; }
            }
            info = null; return false;
        }

        public bool ResolveToken(string token, out PlayerInfo info)
        {
            info = null; if (string.IsNullOrEmpty(token)) return false;
            var t = token.Trim(); if (t.StartsWith("#")) t = t.Substring(1);
            if (t.Length <= 3 && int.TryParse(t, out var num)) { info = Players.FirstOrDefault(p => p.Number == num); if (info != null) return true; }
            if (ulong.TryParse(token, out var sid)) { info = Players.FirstOrDefault(p => p.SteamId == sid); if (info != null) return true; }
            var want = t.ToLowerInvariant();
            // Extract username (strip rich text AND prefixes like [D], [Donor], [A], [Admin])
            info = Players.FirstOrDefault(p => {
                if (p.Name == null) return false;
                var plainName = ExtractUsername(p.Name);
                return plainName.Equals(t, StringComparison.OrdinalIgnoreCase) || 
                       plainName.ToLowerInvariant().StartsWith(want);
            });
            return info != null;
        }
        
        // Helper to extract username from display name (removes rich text tags and prefixes)
        public static string ExtractUsername(string displayName)
        {
            if (string.IsNullOrEmpty(displayName)) return displayName;
            
            // First strip rich text tags like <color=#FF0000>, <i>, <b>, etc.
            var sb = new System.Text.StringBuilder();
            bool inTag = false;
            foreach (var ch in displayName)
            {
                if (ch == '<') { inTag = true; continue; }
                if (ch == '>') { inTag = false; continue; }
                if (!inTag) sb.Append(ch);
            }
            string withoutRichText = sb.ToString();
            
            // Then remove ALL bracket tags: [TEAM], [Admin], [A], [D], [M], [Mod], etc (loop until no more brackets)
            string result = withoutRichText.Trim();
            while (result.StartsWith("["))
            {
                int closeBracket = result.IndexOf(']');
                if (closeBracket > 0 && closeBracket < result.Length - 1)
                {
                    result = result.Substring(closeBracket + 1).Trim();
                }
                else
                {
                    break; // Malformed bracket, stop
                }
            }
            
            // Remove #number prefix (like "#62 Amikiir" -> "Amikiir")
            if (result.StartsWith("#"))
            {
                int spaceIndex = result.IndexOf(' ');
                if (spaceIndex > 0 && spaceIndex < result.Length - 1)
                {
                    result = result.Substring(spaceIndex + 1).Trim();
                }
            }
            
            // Remove unicode emojis and special symbols (♠, ✧, ◈, ♥, etc.)
            // Filter out characters > U+007F (extended ASCII) that are not letters
            var cleanSb = new System.Text.StringBuilder();
            foreach (var ch in result)
            {
                // Keep ASCII printable characters and extended letters (like accented characters)
                if (ch <= 127 || char.IsLetter(ch) || char.IsWhiteSpace(ch))
                {
                    cleanSb.Append(ch);
                }
            }
            result = cleanSb.ToString().Trim();
            
            return result;
        }
    }

    // ------------------------------- Runner -------------------------------

    public class LocalMuteRunner : MonoBehaviour
    {
        public static LocalMuteRunner Instance { get; private set; }
        private bool _wasInGame = false;
        private bool _clientEventsHooked = false;
        private float _nextPoll;
        
        // === PERFORMANCE: Cache UIScoreboard lookup ===
        private static UIScoreboard _cachedUIScoreboard;
        private static float _nextScoreboardCacheRefresh;
        private const float SCOREBOARD_CACHE_INTERVAL = 5.0f;

        void Awake()
        { 
            Instance = this;
        }
        
        void OnDestroy()
        {
            UnhookClientEvents();
        }
        
        public static void Run(IEnumerator co) { if (Instance) Instance.StartCoroutine(co); }
        
        private static UIScoreboard GetCachedUIScoreboard()
        {
            float now = Time.unscaledTime;
            if (_cachedUIScoreboard == null || now >= _nextScoreboardCacheRefresh)
            {
                _nextScoreboardCacheRefresh = now + SCOREBOARD_CACHE_INTERVAL;
                _cachedUIScoreboard = UnityEngine.Object.FindFirstObjectByType<UIScoreboard>(UnityEngine.FindObjectsInactive.Include);
            }
            return _cachedUIScoreboard;
        }

        void Update()
        {
            // Hook events once EventManager is available
            if (!_clientEventsHooked)
            {
                HookClientEvents();
            }
            var ui = GetCachedUIScoreboard();
            if (ScoreboardUtil.HasOpenMenu() && !ScoreboardUtil.IsScoreboardVisible(ui))
                ScoreboardUtil.CloseAllMenus();
            
            try
            {
                // Reduce poll frequency to 2 seconds (was 0.5s) - only for voice/config/scoreboard
                if (Time.unscaledTime >= _nextPoll)
                {
                    _nextPoll = Time.unscaledTime + 2.0f;
                    LocalMuteStore.HotReloadIfChanged();
                    VoiceUtil.ApplyVolumeToAll();
                    PlayerTypingDetector.Poll();

                    // If scoreboard not visible/active, close menus automatically
                    // Use cached lookup to avoid expensive FindFirstObjectByType every 2 seconds
                    if (!ScoreboardUtil.IsScoreboardVisible(ui))
                        ScoreboardUtil.CloseAllMenus();
                }
            }
            catch (System.OverflowException) { /* Suppress Unity networking buffer overflow errors */ }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[LocalMute] Error in Update: {ex.Message}");
            }
        }
        
        private void HookClientEvents()
        {
            try
            {
                // EventManager is static in B310
                EventManager.AddEventListener("Event_OnClientConnected", new Action<Dictionary<string, object>>(OnClientConnected));
                EventManager.AddEventListener("Event_OnDisconnected", new Action<Dictionary<string, object>>(OnClientDisconnected));
                _clientEventsHooked = true;
            }
            catch { }
        }
        
        private void UnhookClientEvents()
        {
            if (!_clientEventsHooked) return;
            try
            {
                // EventManager is static in B310
                EventManager.RemoveEventListener("Event_OnClientConnected", new Action<Dictionary<string, object>>(OnClientConnected));
                EventManager.RemoveEventListener("Event_OnDisconnected", new Action<Dictionary<string, object>>(OnClientDisconnected));
                _clientEventsHooked = false;
            }
            catch { }
        }
        
        private void OnClientConnected(Dictionary<string, object> message)
        {
            if (!_wasInGame)
            {
                Debug.Log("[LocalMute] Joined server - tracking all players");
                StartCoroutine(TrackAllPlayersDelayed());
                StartCoroutine(AutoClosePanelDelayed());
                _wasInGame = true;
            }
        }
        
        private void OnClientDisconnected(Dictionary<string, object> message)
        {
            if (_wasInGame)
            {
                Debug.Log("[LocalMute] Left server - saving recent players");
                LocalMuteStore.SaveAtomic();
                _wasInGame = false;
            }
        }

        private IEnumerator TrackAllPlayersDelayed()
        {
            // Wait a bit for all players to spawn
            yield return new WaitForSeconds(2f);
            LocalMuteStore.TrackAllCurrentPlayers();
        }
        
        private IEnumerator AutoClosePanelDelayed()
        {
            // Wait 5 seconds after joining
            yield return new WaitForSeconds(5f);
            
            try
            {
                // Find the KeybindRunner instance
                var kbRunner = UnityEngine.Object.FindFirstObjectByType<PoncePuck.Keybinds.KeybindRunner>();
                if (kbRunner != null)
                {
                    var type = kbRunner.GetType();
                    
                    // First trigger the send settings coroutine
                    var sendMethod = type.GetMethod("SendAudioCommandsAfterClose", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (sendMethod != null)
                    {
                        var coroutine = sendMethod.Invoke(kbRunner, null) as System.Collections.IEnumerator;
                        if (coroutine != null)
                        {
                            StartCoroutine(coroutine);
                            Debug.Log("[LocalMute] Auto-sent settings to server");
                        }
                    }
                    else
                    {
                        Debug.LogWarning("[LocalMute] SendAudioCommandsAfterClose method not found");
                    }
                    
                    // Then close the panel (if it was open)
                    var closeMethod = type.GetMethod("ClosePPKBPanel", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    if (closeMethod != null)
                    {
                        closeMethod.Invoke(kbRunner, null);
                    }
                }
                else
                {
                    Debug.LogWarning("[LocalMute] KeybindRunner not found for auto-send");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[LocalMute] Failed to auto-send settings: {e.Message}");
            }
        }
    }

    #endregion
    #region Voice helpers
    // ------------------------------- Voice helpers -------------------------------
    // ------------------------------- LocalMuteGainFilter -------------------------------
    internal sealed class LocalMuteGainFilter : MonoBehaviour
    {
        // thread-safe enough for UI changes
        public volatile float Gain = 1f; // 1 = no change

        void OnAudioFilterRead(float[] data, int channels)
        {
            var g = Gain;
            if (g <= 1f || data == null) return; // <=100% uses normal volume path
            for (int i = 0; i < data.Length; i++)
            {
                float s = data[i] * g;
                // Soft limiting using tanh curve - allows amplification while preventing harsh clipping
                if (s > 1f) s = (float)System.Math.Tanh(s);
                else if (s < -1f) s = -(float)System.Math.Tanh(-s);
                data[i] = s;
            }
        }
    }

    internal static class VoiceUtil
    {
        private static readonly Dictionary<int, float> BaseVolumes = new Dictionary<int, float>();

        private static UnityEngine.Component GetVoiceAudioSourceForPlayer(Player player)
        {
            try
            {
                var body = player ? player.PlayerBody : null; // PlayerBodyV2
                if (body == null) return null;

                var prop = body.GetType().GetProperty("VoiceAudioSource", BindingFlags.Instance | BindingFlags.Public);
                if (prop != null)
                {
                    var a = prop.GetValue(body, null) as UnityEngine.Component;
                    if (a != null) return a;
                }
                var fld = body.GetType().GetField("voiceAudioSource", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (fld != null)
                {
                    var a = fld.GetValue(body) as UnityEngine.Component;
                    if (a != null) return a;
                }

                var audioType = typeof(UnityEngine.Component).Assembly.GetType("UnityEngine.AudioSource");
                if (audioType != null)
                    return (UnityEngine.Component)body.GetComponent(audioType) ?? (UnityEngine.Component)body.GetComponentInChildren(audioType, true);
            }
            catch { }
            return null;
        }

        private static float GetBaseVolume(UnityEngine.Component audioSrc)
        {
            if (audioSrc == null) return 1f;
            int id = audioSrc.GetInstanceID();
            if (BaseVolumes.TryGetValue(id, out var v) && v > 0f) return v;

            float current = 1f;
            try
            {
                var t = audioSrc.GetType();
                var prop = t.GetProperty("volume", BindingFlags.Instance | BindingFlags.Public);
                if (prop != null && prop.CanRead) current = Convert.ToSingle(prop.GetValue(audioSrc, null));
                else
                {
                    var field = t.GetField("volume", BindingFlags.Instance | BindingFlags.Public);
                    if (field != null) current = Convert.ToSingle(field.GetValue(audioSrc));
                }
            }
            catch { current = 1f; }

            current = Mathf.Clamp01(current);
            BaseVolumes[id] = current;
            return current;
        }

        private static void SetSourceVolume(UnityEngine.Component audioSrc, float vol01)
        {
            if (audioSrc == null) return;
            try
            {
                var t = audioSrc.GetType();
                var prop = t.GetProperty("volume", BindingFlags.Instance | BindingFlags.Public);
                if (prop != null && prop.CanWrite) { prop.SetValue(audioSrc, Mathf.Clamp01(vol01), null); return; }
                var field = t.GetField("volume", BindingFlags.Instance | BindingFlags.Public);
                if (field != null) field.SetValue(audioSrc, Mathf.Clamp01(vol01));
            }
            catch (Exception e) { Debug.LogError("[LocalMute] SetSourceVolume failed: " + e); }
        }

        private static LocalMuteGainFilter EnsureGainFilter(UnityEngine.Component audioSrc)
        {
            if (audioSrc == null) return null;
            var go = audioSrc.gameObject;
            var f = go.GetComponent<LocalMuteGainFilter>();
            if (f == null) f = go.AddComponent<LocalMuteGainFilter>();
            return f;
        }

        public static void ApplyVolumeForPlayer(Player player, ulong sid)
        {
            var audio = GetVoiceAudioSourceForPlayer(player);
            if (audio == null) return;

            float baseVol = GetBaseVolume(audio);               // saved 0..1
            float pct = Mathf.Clamp(LocalMuteStore.GetVoiceVolume(sid), 0, 200);
            float gainRaw = pct / 100f;

            // Apply 2.5x multiplier for amplification above 100%
            if (gainRaw > 1f)
            {
                gainRaw = 1f + (gainRaw - 1f) * 2.5f;
            }

            // <=100%: use normal AudioSource volume (no filter gain)
            // >100% : keep source at baseVol, add post gain via filter
            float srcVol = baseVol * Mathf.Min(1f, gainRaw);
            SetSourceVolume(audio, srcVol);

            var filter = EnsureGainFilter(audio);
            if (filter != null) filter.Gain = Mathf.Max(1f, gainRaw);
        }

        private static UnityEngine.Component FindAudioSourceFromRecorder(PlayerVoiceRecorder rec)
        {
            if (!rec) return null;
            var audioType = typeof(UnityEngine.Component).Assembly.GetType("UnityEngine.AudioSource");
            if (audioType == null) return null;

            // Prefer AudioSource on the recorder GameObject
            UnityEngine.Component src =
                  rec.GetComponent(audioType)
               ?? rec.GetComponentInChildren(audioType, true);

            // Fallback to player's body if needed
            if (src == null)
            {
                var p = rec.GetComponent<Player>();
                var body = p ? p.PlayerBody : null;
                if (body)
                    src = body.GetComponent(audioType) ?? body.GetComponentInChildren(audioType, true);
            }

            // Make sure effects aren’t bypassed (so our filter runs)
            TryDisableBypassEffects(src);
            return src;
        }

        private static void TryDisableBypassEffects(UnityEngine.Component src)
        {
            if (src == null) return;
            try
            {
                var t = src.GetType();
                var bypass = t.GetProperty("bypassEffects", BindingFlags.Instance | BindingFlags.Public);
                if (bypass != null && bypass.CanWrite) bypass.SetValue(src, false, null);
            }
            catch { }
        }

        // >>> NEW main entry used by the prefix
        public static void ApplyVolumeForRecorder(PlayerVoiceRecorder rec, ulong sid)
        {
            var audio = FindAudioSourceFromRecorder(rec);
            if (audio == null) return;

            float baseVol = GetBaseVolume(audio);                  // your cached 0..1
            float pct = Mathf.Clamp(LocalMuteStore.GetVoiceVolume(sid), 0, 200);
            float gain = pct / 100f;

            // Apply 2.5x multiplier for amplification above 100%
            if (gain > 1f)
            {
                gain = 1f + (gain - 1f) * 2.5f;
            }

            // <=100%: just use AudioSource volume; >100%: keep source at base, add gain post-mix
            float srcVol = baseVol * Mathf.Min(1f, gain);
            SetSourceVolume(audio, srcVol);

            var filter = EnsureGainFilter(audio);
            if (filter != null) filter.Gain = Mathf.Max(1f, gain);
        }
        
        // === PERFORMANCE: Cache PlayerVoiceRecorder lookup ===
        private static PlayerVoiceRecorder[] _cachedRecorders;
        private static float _nextRecorderCacheRefresh;
        private const float RECORDER_CACHE_INTERVAL = 2.0f;
        
        private static PlayerVoiceRecorder[] GetCachedRecorders()
        {
            float now = Time.unscaledTime;
            if (_cachedRecorders == null || now >= _nextRecorderCacheRefresh)
            {
                _nextRecorderCacheRefresh = now + RECORDER_CACHE_INTERVAL;
                _cachedRecorders = UnityEngine.Object.FindObjectsByType<PlayerVoiceRecorder>(FindObjectsSortMode.None);
            }
            return _cachedRecorders ?? new PlayerVoiceRecorder[0];
        }

        // Replace your “apply to all” methods with recorder-based passes
        public static void ApplyVolumeToAll()
        {
            try
            {
                var recs = GetCachedRecorders();
                foreach (var r in recs)
                {
                    if (r == null) continue;
                    var p = r.GetComponent<Player>();
                    var sid = RosterSnapshot.GetSteamId(p);
                    ApplyVolumeForRecorder(r, sid);
                }
            }
            catch (Exception e) { Debug.LogError("[LocalMute] ApplyVolumeToAll error: " + e); }
        }

        public static void ApplyVolumeToAllForSteamId(ulong sid)
        {
            try
            {
                var recs = GetCachedRecorders();
                foreach (var r in recs)
                {
                    if (r == null) continue;
                    var p = r.GetComponent<Player>();
                    if (RosterSnapshot.GetSteamId(p) == sid)
                        ApplyVolumeForRecorder(r, sid);
                }
            }
            catch (Exception e) { Debug.LogError("[LocalMute] ApplyVolumeToAllForSteamId error: " + e); }
        }
    }
}
#endregion
#region Message helpers
// ------------------------------- Chat helpers (whisper prefill) -------------------------------internal static class ChatWhisperUtil

internal static class ChatWhisperUtil
{

    // Right Arrow -> jump caret to end (and clear any selection)
    public static void InstallRightArrowJumpToEnd(UnityEngine.UIElements.TextField tf)
    {
        if (tf == null) return;
        if (tf.userData as string == "LM_RightHooked") return;

        tf.userData = "LM_RightHooked";

        tf.RegisterCallback<KeyDownEvent>(e =>
        {
            if (e.keyCode == KeyCode.RightArrow)
            {
                int len = tf.value?.Length ?? 0;
                tf.cursorIndex = len;
                tf.selectIndex = len; // no highlight
                e.StopPropagation();
            }
        }, TrickleDown.TrickleDown);
    }
        public static void InstallTabSwallow(VisualElement ve)
    {
        if (ve == null) return;
        if (ve.userData as string == "LM_TabHooked") return;

        ve.userData = "LM_TabHooked";
        ve.focusable = true;                  // needs focusable to receive KeyDownEvent
        ve.pickingMode = PickingMode.Position;

        ve.RegisterCallback<KeyDownEvent>(e =>
        {
            if (e.keyCode == KeyCode.Tab)
            {
                e.StopImmediatePropagation(); // prevent scoreboard/chat default Tab behavior
            }
        }, TrickleDown.TrickleDown);
    }
    public static bool TryUseKeybindsBridge(string text)
    {
        try
        {
            Debug.Log($"[LocalMute] Trying keybinds bridge with text: '{text}'");
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType("PoncePuck.Keybinds.KeybindRunner");
                if (t == null) continue;
                Debug.Log($"[LocalMute] Found KeybindRunner type in assembly: {asm.GetName().Name}");
                var m = t.GetMethod("OpenChatWithPrefill", BindingFlags.Public | BindingFlags.Static) ??
                        t.GetMethod("PrefillAndOpenChat", BindingFlags.Public | BindingFlags.Static);
                if (m != null) 
                { 
                    Debug.Log($"[LocalMute] Found method: {m.Name}, invoking...");
                    m.Invoke(null, new object[] { text }); 
                    return true; 
                }
                else
                {
                    Debug.Log("[LocalMute] No prefill method found on KeybindRunner");
                }
            }
            Debug.Log("[LocalMute] KeybindRunner type not found in any assembly");
        } 
        catch (Exception e) 
        { 
            Debug.LogError($"[LocalMute] Keybinds bridge error: {e}");
        }
        return false;
    }

    public static bool TryOpenChatAndSet(string text)
    {
        try
        {
            Debug.Log($"[LocalMute] Trying direct chat manipulation with text: '{text}'");
            var chat = UnityEngine.Object.FindFirstObjectByType<UIChat>(UnityEngine.FindObjectsInactive.Include);
            if (chat == null) 
            {
                Debug.Log("[LocalMute] UIChat not found");
                return false;
            }
            Debug.Log("[LocalMute] UIChat found, attempting to set text...");

            // open chat
            foreach (var m in chat.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var n = m.Name.ToLowerInvariant();
                if ((n.Contains("open") || n.Contains("show")) && m.GetParameters().Length == 0)
                { try { m.Invoke(chat, null); } catch { } }
            }

            // try official setters first...
            foreach (var m in chat.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var n = m.Name.ToLowerInvariant();
                if ((n.Contains("set") || n.Contains("prefill")) && (n.Contains("input") || n.Contains("text")))
                {
                    var ps = m.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType == typeof(string))
                    { m.Invoke(chat, new object[] { text }); return true; }
                }
            }
            foreach (var p in chat.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                if (p.CanWrite && p.PropertyType == typeof(string) && p.Name.ToLowerInvariant().Contains("input"))
                { p.SetValue(chat, text, null); return true; }

            // ---- Fallback: write directly to the TextField ----
            var doc = chat.GetComponent<UIDocument>();
            var root = doc ? doc.rootVisualElement : null;
            var tf = root?.Q<UnityEngine.UIElements.TextField>() ?? root?.Query<UnityEngine.UIElements.TextField>().First();
            if (tf != null)
            {
                void Apply()
                {
                    tf.value = text;
                    FocusEndNoSelect(tf);           // ⬅️ caret at end, no selection
                    InstallRightArrowJumpToEnd(tf);
                    InstallTabSwallow(tf);        // swallow Tab while typing
                }
                
                void InstallTabSwallow(UnityEngine.UIElements.TextField textField)
                {
                    if (textField == null) return;
                    if (textField.userData as string == "LM_TabSwallow") return;
                    textField.userData = "LM_TabSwallow";
                    textField.RegisterCallback<KeyDownEvent>(e =>
                    {
                        if (e.keyCode == KeyCode.Tab)
                        {
                            e.StopPropagation();
                        }
                    }, TrickleDown.TrickleDown);
                }
            
                // do it now and next tick in case chat UI overwrites once on focus
                Apply();
                tf.schedule.Execute(Apply).ExecuteLater(0);
                return true;
            }
        }
        catch { }
        return false;
    }
public static void FocusEndNoSelect(UnityEngine.UIElements.TextField tf)
{
    if (tf == null) return;

    // 1) Don’t auto-select all on focus
    try { tf.selectAllOnFocus = false; } catch {} // (exists on TextInputBaseField in Unity 6)

    void MoveEnd()
    {
        int len = tf.value?.Length ?? 0;
        tf.cursorIndex = len;
        tf.selectIndex = len; // no highlight
    }

    // 2) Do it now, and again after focus settles
    tf.Focus();
    MoveEnd();
    tf.schedule.Execute(MoveEnd).ExecuteLater(0);
    tf.schedule.Execute(MoveEnd).ExecuteLater(50);    // one more frame for stubborn UIs

    // 3) If the game re-focuses the field later, keep forcing end
    tf.RegisterCallback<FocusInEvent>(_ => MoveEnd(), TrickleDown.TrickleDown);
}

    public static System.Collections.IEnumerator DelayedPrefill(string text)
    {
        // Wait for Tab to be released before proceeding (using Input System)
        Debug.Log("[LocalMute] Waiting for Tab to be released...");
        
        // Check if Input System is available
        UnityEngine.InputSystem.Keyboard keyboard = null;
        bool useInputSystem = false;
        
        try
        {
            keyboard = UnityEngine.InputSystem.Keyboard.current;
            useInputSystem = keyboard != null;
        }
        catch (System.Exception e)
        {
            Debug.Log($"[LocalMute] Input System not available: {e.Message}");
            useInputSystem = false;
        }
        
        if (useInputSystem && keyboard != null)
        {
            // Wait for Tab release using Input System
            while (keyboard.tabKey.isPressed)
            {
                yield return null; // Wait one frame
            }
            Debug.Log("[LocalMute] Tab released (Input System)");
            yield return new WaitForSeconds(0.1f); // Small additional delay
        }
        else
        {
            // Fallback to timed delay if Input System fails
            Debug.Log("[LocalMute] Using fallback delay");
            yield return new WaitForSeconds(0.5f); // Longer delay as fallback
        }
        
        Debug.Log($"[LocalMute] Attempting delayed prefill: '{text}'");
        
        bool success = TryUseKeybindsBridge(text) || TryOpenChatAndSet(text);
        if (success)
        {
            Debug.Log("[LocalMute] Delayed prefill successful");
        }
        else
        {
            Debug.LogWarning("[LocalMute] Delayed prefill failed");
        }
    }

    // Tab swallowing system
    private static bool _swallowingTab = false;
    private static System.Collections.IEnumerator _tabSwallowCoroutine;

    public static void StartTabSwallowing()
    {
        if (_swallowingTab) return;
        _swallowingTab = true;
        Debug.Log("[LocalMute] Started Tab swallowing");
        
        if (_tabSwallowCoroutine != null)
            LocalMuteRunner.Instance?.StopCoroutine(_tabSwallowCoroutine);
        
        _tabSwallowCoroutine = TabSwallowRoutine();
        LocalMuteRunner.Run(_tabSwallowCoroutine);
    }

    private static System.Collections.IEnumerator TabSwallowRoutine()
    {
        while (_swallowingTab)
        {
            // Check if Tab key is still held down
            if (UnityEngine.Input.GetKey(KeyCode.Tab))
            {
                // Tab is held, keep swallowing
                yield return null;
            }
            else
            {
                // Tab released, stop swallowing
                Debug.Log("[LocalMute] Tab released, stopping swallowing");
                _swallowingTab = false;
                break;
            }
        }
    }

    public static bool IsSwallowingTab()
    {
        return _swallowingTab;
    }

    // ===== Caret helpers (UITK-friendly) =====
    public static void InstallChatTweaks(UnityEngine.UIElements.TextField tf)
    {
        if (tf == null) return;

        // sentinel to avoid double-registration
        if (tf.userData as string == "LM_Tweaked") return;
        tf.userData = "LM_Tweaked";

        // Optional: Right Arrow forces caret to end (and clears selection)
        tf.RegisterCallback<KeyDownEvent>(e =>
        {
            if (e.keyCode == KeyCode.RightArrow)
            {
                int len = tf.value?.Length ?? 0;
                tf.cursorIndex = len;
                tf.selectIndex = len;
                e.StopPropagation();
            }
        }, TrickleDown.TrickleDown);
}
    }
#endregion
#region UI helpers / ScoreboardUtil
// ------------------------------- UI helpers (overlay & scoreboard) -------------------------------
internal static class ScoreboardUtil
{
    // ===== mini “MakeReadable” & font helpers (pattern from your Keybinds panel) =====
    private static Font _uiTextFont;
    private static Font GetUIFont()
    {
        if (_uiTextFont != null) return _uiTextFont;
        
        // Try to get the font from the game's PanelSettings (matches base game font exactly)
        try
        {
            var uiManager = MonoBehaviourSingleton<UIManager>.Instance;
            if (uiManager != null && uiManager.PanelSettings != null)
            {
                var textSettings = uiManager.PanelSettings.textSettings;
                if (textSettings != null && textSettings.defaultFontAsset != null)
                {
                    _uiTextFont = textSettings.defaultFontAsset.sourceFontFile;
                    if (_uiTextFont != null) return _uiTextFont;
                }
            }
        }
        catch { }
        
        // Fallback to Arial
        try { _uiTextFont = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { }
        if (_uiTextFont == null)
        {
            try
            {
                _uiTextFont = Font.CreateDynamicFontFromOSFont(
                new[] { "Arial", "Helvetica Neue", "Segoe UI", "Liberation Sans", "Noto Sans" }, 16);
            }
            catch { }
        }
        return _uiTextFont;
    }
    private static void MakeReadable(VisualElement ve)
    {
        ve.style.color = Color.white;
        ve.style.unityFont = GetUIFont();
    }
    // (Reference: same approach as your Keybinds panel to normalize text & foreground UI.)  // :contentReference[oaicite:2]{index=2}

    // ===== Strip [D] and [A] tags from player names for admin commands =====
    private static string StripAdminTags(string playerName)
    {
        if (string.IsNullOrEmpty(playerName)) return playerName;
        
        // Remove [D] and [A] tags (case-insensitive, with or without spaces)
        playerName = System.Text.RegularExpressions.Regex.Replace(playerName, @"\[D\]\s*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        playerName = System.Text.RegularExpressions.Regex.Replace(playerName, @"\[A\]\s*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        return playerName.Trim();
    }

    // ===== Extract server name from scoreboard UI =====
    public static string GetServerNameFromScoreboard()
    {
        try
        {
            var scoreboard = UnityEngine.Object.FindFirstObjectByType<UIScoreboard>();
            if (scoreboard != null)
            {
                var doc = scoreboard.GetComponent<UIDocument>();
                if (doc != null && doc.rootVisualElement != null)
                {
                    var root = doc.rootVisualElement;
                    
                    // Try the known ServerContainer/NameLabel structure
                    var serverContainer = root.Q<VisualElement>("ServerContainer");
                    if (serverContainer != null)
                    {
                        var nameLabel = serverContainer.Q<Label>("NameLabel");
                        if (nameLabel != null && !string.IsNullOrEmpty(nameLabel.text))
                        {
                            // Strip rich text tags from server name
                            return StripRichTextTags(nameLabel.text);
                        }
                    }
                }
            }
        }
        catch (Exception e) 
        { 
            Debug.LogWarning($"[LocalMute] GetServerNameFromScoreboard failed: {e.Message}");
        }
        return "";
    }

    private static string StripRichTextTags(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new System.Text.StringBuilder(s.Length);
        bool tag = false;
        foreach (var ch in s)
        {
            if (ch == '<') { tag = true; continue; }
            if (ch == '>') { tag = false; continue; }
            if (!tag) sb.Append(ch);
        }
        return sb.ToString();
    }

    // ===== menu tracking / z-order =====
    internal static VisualElement _openMenu;          // the active menu VE
    private static VisualElement _clickCatcher;      // backdrop that closes on outside click
    private static VisualElement _attachRoot;        // top-level attach root
    private static readonly List<VisualElement> _masked = new List<VisualElement>(); // pickables we masked out
    public static bool HasOpenMenu() => _openMenu != null || _clickCatcher != null;
    public static void CloseAllMenus()
    {
        try
        {
            if (_openMenu != null) _openMenu.RemoveFromHierarchy();
            if (_clickCatcher != null) _clickCatcher.RemoveFromHierarchy();
            _openMenu = null; _clickCatcher = null;

            // restore masked pickables
            for (int i = 0; i < _masked.Count; i++)
            {
                var ve = _masked[i];
                if (ve != null) ve.pickingMode = PickingMode.Position;
            }
            _masked.Clear();
        }
        catch { }
    }

    public static VisualElement GetPlayerRow(UIScoreboard ui, Player player)
    {
        try
        {
            var f = AccessTools.Field(typeof(UIScoreboard), "playerVisualElementMap");
            if (f != null)
            {
                var dict = f.GetValue(ui) as System.Collections.IDictionary;
                if (dict != null && dict.Contains(player)) return dict[player] as VisualElement;
            }
        }
        catch (Exception e) { Debug.LogError("[LocalMute] GetPlayerRow failed: " + e); }
        return null;
    }

    private static VisualElement GetScoreboardRoot(UIScoreboard ui)
    {
        if (ui == null) return null;
        try
        {
            var fields = typeof(UIScoreboard).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(f => typeof(VisualElement).IsAssignableFrom(f.FieldType)).ToArray();
            var preferred = fields.FirstOrDefault(f => f.Name.ToLowerInvariant().Contains("root")) ?? fields.FirstOrDefault();
            var ve = preferred != null ? (preferred.GetValue(ui) as VisualElement) : null;
            if (ve != null) return ve;

            var doc = ui.GetComponent<UIDocument>();
            if (doc != null) return doc.rootVisualElement;
        }
        catch { }
        return null;
    }

    public static bool IsScoreboardVisible(UIScoreboard ui)
    {
        if (ui == null || !ui.isActiveAndEnabled || !ui.gameObject.activeInHierarchy)
            return false;

        var root = GetScoreboardRoot(ui);
        if (root == null || root.panel == null)
            return false;

        var rs = root.resolvedStyle;
        if (rs.display == DisplayStyle.None || rs.visibility == Visibility.Hidden)
            return false;

        var wb = root.worldBound;
        return wb.width > 2f && wb.height > 2f;
    }

    private static VisualElement GetTopLevelRoot()
    {
        try
        {
            var uiMgr = UnityEngine.Object.FindFirstObjectByType<UIManager>(UnityEngine.FindObjectsInactive.Include);
            var doc = uiMgr != null ? uiMgr.UIDocument : UnityEngine.Object.FindFirstObjectByType<UIDocument>(UnityEngine.FindObjectsInactive.Include);
            return doc != null ? doc.rootVisualElement : null;
        }
        catch { return null; }
    }

    private static VisualElement ResolveAttachRoot(UIScoreboard ui, VisualElement row = null)
    {
        // Prefer panel visualTree so overlay coordinates are stable even if scoreboard root changes.
        var sbRoot = GetScoreboardRoot(ui);
        if (sbRoot != null && sbRoot.panel != null && sbRoot.panel.visualTree != null)
            return sbRoot.panel.visualTree;

        if (row != null && row.panel != null && row.panel.visualTree != null)
            return row.panel.visualTree;

        return sbRoot ?? GetTopLevelRoot() ?? row;
    }

    // LEFT click opens menu; swallow default left-click that would open profile
    public static void BindLeftClickOpensMenu(UIScoreboard ui, VisualElement row)
    {
        if (row == null) return;
        if (row.ClassListContains("LM_LeftClickBound")) return;

        void Swallow(VisualElement ve)
        {
            ve.RegisterCallback<PointerDownEvent>((PointerDownEvent e) =>
            {
                if (e.button == (int)MouseButton.LeftMouse) { e.StopImmediatePropagation(); e.StopPropagation(); }
            }, TrickleDown.TrickleDown);

            ve.RegisterCallback<ClickEvent>((ClickEvent e) =>
            {
                e.StopImmediatePropagation(); e.StopPropagation();
            }, TrickleDown.TrickleDown);
        }
        Swallow(row);
        foreach (var c in row.Children()) Swallow(c);

        row.RegisterCallback<PointerUpEvent>((PointerUpEvent ev) =>
        {
            if (ev.button != (int)MouseButton.LeftMouse) return;

            try
            {
                var f = AccessTools.Field(typeof(UIScoreboard), "playerVisualElementMap");
                if (f != null)
                {
                    var dict = f.GetValue(ui) as System.Collections.IDictionary;
                    if (dict != null)
                    {
                        foreach (System.Collections.DictionaryEntry kv in dict)
                        {
                            if (ReferenceEquals(kv.Value, row))
                            {
                                var player = kv.Key as Player;
                                LocalMuteClientMod.OpenMenuFor(ui, row, player);
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception e) { Debug.LogError("[LocalMute] OpenMenuFor row failed: " + e); }

            ev.StopPropagation();
        });

        row.AddToClassList("LM_LeftClickBound");
    }

    public static void BindRightClickOpensAdminMenu(UIScoreboard ui, VisualElement row)
    {
        if (row == null) return;
        if (row.ClassListContains("LM_RightClickBound")) return;

        row.RegisterCallback<PointerUpEvent>((PointerUpEvent ev) =>
        {
            if (ev.button != (int)MouseButton.RightMouse) return;

            // Check if admin settings are enabled
            bool isAdminMode = LocalMuteStore.IsAdminModeEnabled();
            Debug.Log($"[LocalMute] Right-click detected, admin mode enabled: {isAdminMode}");
            
            if (!isAdminMode) return;

            try
            {
                var f = AccessTools.Field(typeof(UIScoreboard), "playerVisualElementMap");
                if (f != null)
                {
                    var dict = f.GetValue(ui) as System.Collections.IDictionary;
                    if (dict != null)
                    {
                        bool found = false;
                        foreach (System.Collections.DictionaryEntry kv in dict)
                        {
                            if (ReferenceEquals(kv.Value, row))
                            {
                                var player = kv.Key as Player;
                                Debug.Log($"[LocalMute] Opening admin menu for player: {player?.Username?.Value}");
                                LocalMuteClientMod.OpenAdminMenuFor(ui, row, player);
                                found = true;
                                break;
                            }
                        }
                        if (!found) Debug.LogWarning("[LocalMute] Player not found in dictionary for admin menu");
                    }
                }
            }
            catch (Exception e) { Debug.LogError("[LocalMute] OpenAdminMenuFor row failed: " + e); }

            ev.StopPropagation();
        });

        row.AddToClassList("LM_RightClickBound");
    }
    internal static readonly Color32 TextFieldBg = new Color32(57, 57, 57, 255);
    internal static readonly Color32 RowBg = new Color32(61, 61, 61, 255);
    internal static readonly Color32 ButtonBg = new Color32(57, 57, 57, 255);
    internal static readonly Color P_White = new Color(0.93f, 0.93f, 0.93f, 1f);
    internal static readonly Color BtnBrightGray = (Color)RowBg;
    private static void Flashable(UnityEngine.UIElements.Button b, Color baseBg, int flashMs = 140)
    {
        b.focusable = true;
        void SetBase() { b.style.backgroundColor = new UnityEngine.UIElements.StyleColor(baseBg); b.style.color = Color.white; }

        SetBase();
        bool hover = false, flashing = false;

        b.RegisterCallback<PointerEnterEvent>(_ => { hover = true; b.style.backgroundColor = P_White; b.style.color = Color.black; });
        b.RegisterCallback<PointerLeaveEvent>(_ => { hover = false; if (!flashing) SetBase(); });
        b.RegisterCallback<GeometryChangedEvent>(_ => SetBase());

        b.RegisterCallback<PointerUpEvent>(_ =>
        {
            flashing = true;
            b.style.backgroundColor = P_White; b.style.color = Color.black;
            b.schedule.Execute(() => { flashing = false; if (!hover) SetBase(); }).StartingIn(flashMs);
        });
    }
    public static void AddButtonFlash(UnityEngine.UIElements.Button b, int flashMs = 140)
    {
        var baseBg = b.style.backgroundColor.keyword != UnityEngine.UIElements.StyleKeyword.Null ? b.style.backgroundColor.value : b.resolvedStyle.backgroundColor;
        Flashable(b, baseBg, flashMs);
    }
    private static void AddButtonFlashWhiteHover(UnityEngine.UIElements.Button b, Color baseBg, int flashMs = 140) => Flashable(b, baseBg, flashMs);

    private static void SetTabVisual(UnityEngine.UIElements.Button b, bool active)
    {
        b.style.backgroundColor = active ? P_White : new UnityEngine.UIElements.StyleColor(RowBg);
        b.style.color = active ? Color.black : Color.white;
    }
    private static void AddTabHover(UnityEngine.UIElements.Button b, Func<bool> isActive)
    {
        b.focusable = false;
        void Apply() => SetTabVisual(b, isActive());
        Apply();

        b.RegisterCallback<PointerEnterEvent>(_ => { if (!isActive()) { b.style.backgroundColor = P_White; b.style.color = Color.black; } });
        b.RegisterCallback<PointerLeaveEvent>(_ => Apply());
        b.RegisterCallback<AttachToPanelEvent>(_ => Apply());
        b.RegisterCallback<GeometryChangedEvent>(_ => Apply());
    }

    private static void StyleSliderLikeBase(UnityEngine.UIElements.Slider slider)
    {
        if (slider == null) return;
        
        // Match button styling for uniformity
        slider.style.height = 30;
        slider.style.backgroundColor = new UnityEngine.UIElements.StyleColor(ButtonBg);
        slider.style.borderTopLeftRadius = 0;
        slider.style.borderTopRightRadius = 0;
        slider.style.borderBottomLeftRadius = 0;
        slider.style.borderBottomRightRadius = 0;
        
        // Style the track and handle if accessible
        try
        {
            var track = slider.Q<UnityEngine.UIElements.VisualElement>("unity-tracker");
            if (track != null)
            {
                track.style.backgroundColor = new UnityEngine.UIElements.StyleColor(ButtonBg);
            }
            
            var draggerBorder = slider.Q<UnityEngine.UIElements.VisualElement>("unity-dragger-border");
            if (draggerBorder != null)
            {
                draggerBorder.style.backgroundColor = new UnityEngine.UIElements.StyleColor(P_White);
                draggerBorder.style.borderTopWidth = 0;
                draggerBorder.style.borderBottomWidth = 0;
                draggerBorder.style.borderLeftWidth = 0;
                draggerBorder.style.borderRightWidth = 0;
            }
            
            var dragger = slider.Q<UnityEngine.UIElements.VisualElement>("unity-dragger");
            if (dragger != null)
            {
                dragger.style.backgroundColor = new UnityEngine.UIElements.StyleColor(P_White);
            }
            
            var handle = slider.Q<UnityEngine.UIElements.VisualElement>("unity-drag-container");
            if (handle != null)
            {
                handle.style.backgroundColor = new UnityEngine.UIElements.StyleColor(ButtonBg);
            }
        }
        catch
        {
            // Fallback if internal structure is different
        }
    }

    // ===== overlay menu open =====
    public static void OpenOverlayMenu(UIScoreboard ui, VisualElement row, Player player)
    {
        if (row == null || player == null) return;

        CloseAllMenus(); // one-at-a-time

        _attachRoot = ResolveAttachRoot(ui, row);
        if (_attachRoot == null) return;

        // click-catcher background (closes on left-click)
        _clickCatcher = new VisualElement { name = "LM_MenuBackdrop" };
        _clickCatcher.style.position = Position.Absolute;
        _clickCatcher.style.left = 0; _clickCatcher.style.top = 0;
        _clickCatcher.style.right = 0; _clickCatcher.style.bottom = 0;
        _clickCatcher.style.backgroundColor = new Color(); // invisible, but intercepts clicks
        _clickCatcher.pickingMode = PickingMode.Position;
        _clickCatcher.RegisterCallback<PointerUpEvent>(_ => CloseAllMenus());
        _attachRoot.Add(_clickCatcher);

        // the menu
        var sid = RosterSnapshot.GetSteamId(player);
        bool tMuted = LocalMuteStore.IsTextMuted(sid);
        bool vMuted = LocalMuteStore.IsVoiceMuted(sid);
        int vol = LocalMuteStore.GetVoiceVolume(sid);

        var menu = new VisualElement { name = "LocalMuteMenu" };
        menu.style.position = Position.Absolute;
        
        // Use the same background color as the main panel
        var keybindRunner = UnityEngine.Object.FindFirstObjectByType<PoncePuck.Keybinds.KeybindRunner>();
        Color menuBg = TextFieldBg; // fallback
        if (keybindRunner != null)
        {
            try
            {
                var panelBg = keybindRunner.GetMainPanelBackgroundColor();
                if (panelBg.HasValue) menuBg = panelBg.Value;
            }
            catch { }
        }
        
        menu.style.backgroundColor = new UnityEngine.UIElements.StyleColor(menuBg);
        menu.style.paddingLeft = 4; menu.style.paddingRight = 4;
        menu.style.paddingTop = 6; menu.style.paddingBottom = 4;
        menu.style.flexDirection = FlexDirection.Column;
        menu.style.minWidth = 240;
        menu.pickingMode = PickingMode.Position;

        // Dynamic Save/Unsaved button
        string steamId = player.SteamId?.Value.ToString() ?? "0";
        string profileUrl = $"https://steamcommunity.com/profiles/{steamId}";
        bool isSaved = LocalMuteStore.IsSaved(steamId);
        bool isBlocked = LocalMuteStore.IsBlocked(steamId);
        
        var saveBtn = new UnityEngine.UIElements.Button(() => { 
                string playerNameForBtn = player.Username?.Value.ToString() ?? "Unknown";
                string playerNum = "";
                try
                {
                    if (player.Number != null)
                    {
                        int num = player.Number.Value;
                        if (num > 0 && num < 100)
                        {
                            playerNum = num.ToString();
                        }
                    }
                }
                catch { }
                
                if (LocalMuteStore.IsSaved(steamId))
            {
                Debug.Log($"[LocalMute] Remove Save button clicked for player: {playerNameForBtn} (SteamID: {steamId})");
                LocalMuteStore.RemoveFromSaved(steamId);
            }
            else
            {
                Debug.Log($"[LocalMute] Add Save button clicked for player: {playerNameForBtn} (SteamID: {steamId})");
                LocalMuteStore.AddToSaved(steamId, playerNameForBtn, profileUrl, playerNum);
            }
            
            // Force additional UI refresh for social panel
            try
            {
                var kbRunner = UnityEngine.Object.FindFirstObjectByType<PoncePuck.Keybinds.KeybindRunner>();
                kbRunner?.RefreshUI();
            }
            catch { }
            
            CloseAllMenus();
        });
        
        saveBtn.text = isSaved ? "UNSAVE" : "SAVE";
        MakeReadable(saveBtn);
        saveBtn.style.marginTop = 4; saveBtn.style.marginBottom = 4;
        saveBtn.style.marginLeft = 4; saveBtn.style.marginRight = 4;
        saveBtn.style.paddingLeft = 6; saveBtn.style.paddingTop = 6;
        saveBtn.style.paddingBottom = 4; saveBtn.style.height = 40;
        saveBtn.style.backgroundColor = new UnityEngine.UIElements.StyleColor(isSaved ? 
            new Color(0.8f, 0.4f, 0f, 1f) : new Color(0f, 0.6f, 0f, 1f)); // Orange for remove, Green for add
        AddButtonFlash(saveBtn);
        AddTabHover(saveBtn, () => false);

        // Only show save button if not blocked and not muted
        if (!isBlocked && !(tMuted && vMuted))
        {
            menu.Add(saveBtn);
        }

        var profileBtn = new UnityEngine.UIElements.Button(() => 
        { 
            // Use Steam overlay to open profile
            try
            {
                ulong steamId64 = 0;
                if (ulong.TryParse(steamId, out steamId64))
                {
                    Steamworks.SteamFriends.ActivateGameOverlayToUser("steamid", new Steamworks.CSteamID(steamId64));
                }
                else
                {
                    Debug.LogWarning($"[LocalMute] Invalid Steam ID for profile: {steamId}");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[LocalMute] Failed to open Steam profile overlay: {e.Message}");
            }
            CloseAllMenus(); 
        });

        profileBtn.text = "VIEW PROFILE";
        MakeReadable(profileBtn);
        profileBtn.style.marginTop = 4; profileBtn.style.marginBottom = 4;
        profileBtn.style.marginLeft = 4; profileBtn.style.marginRight = 4;
        profileBtn.style.paddingLeft = 6; profileBtn.style.paddingTop = 6;
        profileBtn.style.paddingBottom = 4; profileBtn.style.height = 40;
        profileBtn.style.backgroundColor = new UnityEngine.UIElements.StyleColor(BtnBrightGray);
        AddButtonFlash(profileBtn);
        AddTabHover(profileBtn, () => false);
        menu.Add(profileBtn);

        // INFO button - shows highlightable dialog with player info
        var infoBtn = new UnityEngine.UIElements.Button(() => { 
            // Open the KeybindRunner panel using reflection
            try
            {
                var kbRunner = UnityEngine.Object.FindFirstObjectByType<PoncePuck.Keybinds.KeybindRunner>();
                if (kbRunner != null)
                {
                    var openPanelMethod = kbRunner.GetType().GetMethod("OpenPPKBMenuFromCode", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    if (openPanelMethod != null)
                    {
                        openPanelMethod.Invoke(kbRunner, null);
                    }
                }
            }
            catch { }
            ShowPlayerInfoDialog(ui, player, steamId, profileUrl);
            CloseAllMenus(); 
        }) { text = "INFO" };
        MakeReadable(infoBtn);
        infoBtn.style.marginTop = 4; infoBtn.style.marginBottom = 4;
        infoBtn.style.marginLeft = 4; infoBtn.style.marginRight = 4;
        infoBtn.style.paddingLeft = 6; infoBtn.style.paddingTop = 6;
        infoBtn.style.paddingBottom = 4;
        infoBtn.style.height = 40;
        infoBtn.style.backgroundColor = new UnityEngine.UIElements.StyleColor(BtnBrightGray);
        AddButtonFlash(infoBtn);
        AddTabHover(infoBtn, () => false);
        menu.Add(infoBtn);

        // Vote Kick button
        var kickBtn = new UnityEngine.UIElements.Button(() => 
        { 
            string playerNameForKick = player.Username?.Value.ToString() ?? "Unknown";
            string displayName = playerNameForKick;
            playerNameForKick = StripAdminTags(playerNameForKick);
            string vkPlayerNumber = player.Number?.Value.ToString() ?? "?";
            
            ShowVoteKickConfirmationDialog("VOTE KICK PLAYER", vkPlayerNumber, displayName, playerNameForKick, steamId);
        }) { text = "VOTE KICK" };
        MakeReadable(kickBtn);
        kickBtn.style.marginTop = 4; kickBtn.style.marginBottom = 4;
        kickBtn.style.marginLeft = 4; kickBtn.style.marginRight = 4;
        kickBtn.style.paddingLeft = 6; kickBtn.style.paddingTop = 6;
        kickBtn.style.paddingBottom = 4;
        kickBtn.style.height = 40;
        kickBtn.style.backgroundColor = new UnityEngine.UIElements.StyleColor(BtnBrightGray);
        AddButtonFlash(kickBtn);
        AddTabHover(kickBtn, () => false);
        menu.Add(kickBtn);

        // Consolidated Block/Unblock button (handles both muting and social list)
        UnityEngine.UIElements.Button mBtn = new UnityEngine.UIElements.Button();
        MakeReadable(mBtn);
        bool isFullyMuted = tMuted && vMuted;
        mBtn.text = (isFullyMuted || isBlocked) ? "UNBLOCK" : "BLOCK";
        mBtn.clicked += () =>
        {
            string playerNameForBtn = player.Username?.Value.ToString() ?? "Unknown";
            
            if (isFullyMuted || isBlocked)
            {
                // Unblock: remove from mute AND social blocked list
                LocalMuteStore.ToggleFullMute(sid, false);
                LocalMuteStore.RemoveFromBlocked(steamId);
                Debug.Log($"[LocalMute] Unblocked player: {playerNameForBtn} (SteamID: {steamId})");
            }
            else
            {
                // Block: add to mute AND social blocked list
                LocalMuteStore.ToggleFullMute(sid, true);
                LocalMuteStore.AddToBlocked(steamId, playerNameForBtn, profileUrl);
                Debug.Log($"[LocalMute] Blocked player: {playerNameForBtn} (SteamID: {steamId})");
            }
            
            RefreshRowForSteamId(sid);
            
            // Force additional UI refresh for social panel
            try
            {
                var kbRunner = UnityEngine.Object.FindFirstObjectByType<PoncePuck.Keybinds.KeybindRunner>();
                kbRunner?.RefreshUI();
            }
            catch { }
            
            CloseAllMenus();
        };
        mBtn.style.marginTop = 4; mBtn.style.marginBottom = 4;
        mBtn.style.marginLeft = 4; mBtn.style.marginRight = 4;
        mBtn.style.paddingLeft = 6; mBtn.style.paddingTop = 6;
        mBtn.style.paddingBottom = 10;
        mBtn.style.height = 40;
        mBtn.style.backgroundColor = new UnityEngine.UIElements.StyleColor((isFullyMuted || isBlocked) ? 
            new Color(0.8f, 0.4f, 0f, 1f) : new Color(0.8f, 0f, 0f, 1f)); // Orange for unblock, Red for block
        AddButtonFlash(mBtn);
        AddTabHover(mBtn, () => false);

        // Only show block button if not saved
        if (!isSaved)
        {
            menu.Add(mBtn);
        }

        // PLAYER VOLUME title (styled like a button space but just text on background)
        var volTitle = new VisualElement();
        volTitle.style.marginTop = 4; 
        volTitle.style.marginBottom = 4;
        volTitle.style.marginLeft = 4; 
        volTitle.style.marginRight = 4;
        volTitle.style.paddingLeft = 6; 
        volTitle.style.paddingTop = 6;
        volTitle.style.paddingBottom = 10;
        volTitle.style.height = 40;
        
        var volLabel = new Label("PLAYER VOLUME");
        MakeReadable(volLabel);
        volLabel.style.unityTextAlign = TextAnchor.MiddleLeft;
        volTitle.Add(volLabel);
        menu.Add(volTitle);

        // Volume controls row (styled like a button but no hover/click)
        var volumeRow = new VisualElement();
        volumeRow.style.flexDirection = FlexDirection.Row;
        volumeRow.style.justifyContent = Justify.SpaceBetween;
        volumeRow.style.alignItems = Align.Center;
        volumeRow.style.marginTop = 4; 
        volumeRow.style.marginBottom = 4;
        volumeRow.style.marginLeft = 4; 
        volumeRow.style.marginRight = 4;
        volumeRow.style.paddingLeft = 6; 
        volumeRow.style.paddingRight = 6;
        volumeRow.style.paddingTop = 6;
        volumeRow.style.paddingBottom = 6;
        volumeRow.style.height = 40;
        volumeRow.style.backgroundColor = new UnityEngine.UIElements.StyleColor(TextFieldBg);

        // Volume slider (settings style)
        if (ulong.TryParse(steamId, out ulong steamIdUlong))
        {
            var volume = PoncePuck.LocalMute.LocalMuteStore.GetVoiceVolume(steamIdUlong);

            // Editable volume value field (left side)
            var volumeValueField = new TextField();
            volumeValueField.value = $"{volume}";
            MakeReadable(volumeValueField);
            volumeValueField.style.backgroundColor = new UnityEngine.UIElements.StyleColor(TextFieldBg);
            volumeValueField.style.minWidth = 60;
            volumeValueField.style.maxWidth = 60;
            volumeValueField.style.maxHeight = 30;
            volumeValueField.style.unityTextAlign = TextAnchor.MiddleRight;
            volumeValueField.style.marginLeft = 8;
            volumeValueField.style.marginRight = 8;

            // Volume slider (middle, grows to fill)
            var volumeSlider = new UnityEngine.UIElements.Slider(0f, 200f) { value = volume };
            volumeSlider.style.backgroundColor = new UnityEngine.UIElements.StyleColor(TextFieldBg);
            volumeSlider.style.flexGrow = 1; 
            volumeSlider.style.flexBasis = 0;  
            volumeSlider.style.marginRight = 8;
            
            // Apply settings slider styling
            StyleSliderLikeBase(volumeSlider);

            // Two-way binding: slider updates field, field updates slider
            volumeSlider.RegisterValueChangedCallback(evt =>
            {
                int intValue = Mathf.RoundToInt(evt.newValue);
                PoncePuck.LocalMute.LocalMuteStore.SetVoiceVolume(steamIdUlong, intValue);
                volumeValueField.SetValueWithoutNotify(intValue.ToString());
            });

            volumeValueField.RegisterValueChangedCallback(evt =>
            {
                if (int.TryParse(evt.newValue, out int newValue))
                {
                    newValue = Mathf.Clamp(newValue, 0, 200);
                    PoncePuck.LocalMute.LocalMuteStore.SetVoiceVolume(steamIdUlong, newValue);
                    volumeSlider.SetValueWithoutNotify(newValue);
                }
            });

            volumeRow.Add(volumeValueField);
            volumeRow.Add(volumeSlider);
        }

        menu.Add(volumeRow);


        // Send Message button (whisper prefill)
        var msgBtn = new UnityEngine.UIElements.Button(() => { 
            string playerName = player.Username?.Value.ToString() ?? "Unknown";
            string whisperText = $"/w {playerName} ";
            Debug.Log($"[LocalMute] Send Message button clicked for player: {playerName}");
            
            // Close menu first, then wait for Tab release and prefill
            CloseAllMenus();
            
            LocalMuteRunner.Run(ChatWhisperUtil.DelayedPrefill(whisperText));
        }) { text = "SEND MESSAGE" };
        MakeReadable(msgBtn);
        msgBtn.style.marginTop = 4; msgBtn.style.marginBottom = 4;
        msgBtn.style.marginLeft = 4; msgBtn.style.marginRight = 4;
        msgBtn.style.paddingLeft = 6; msgBtn.style.paddingTop = 6;
        msgBtn.style.paddingBottom = 4;
        msgBtn.style.height = 40;
        msgBtn.style.backgroundColor = new UnityEngine.UIElements.StyleColor(BtnBrightGray);
        AddButtonFlash(msgBtn);
        AddTabHover(msgBtn, () => false);
        menu.Add(msgBtn);

        var cBtn = new UnityEngine.UIElements.Button(() => { CloseAllMenus(); }) { text = "CLOSE" };
        MakeReadable(cBtn);
        cBtn.style.marginTop = 4; cBtn.style.marginBottom = 4;
        cBtn.style.marginLeft = 4; cBtn.style.marginRight = 4;
        cBtn.style.paddingLeft = 6; cBtn.style.paddingTop = 6;
        cBtn.style.paddingBottom = 4;
        cBtn.style.height = 40;
        cBtn.style.backgroundColor = new UnityEngine.UIElements.StyleColor(BtnBrightGray);
        AddButtonFlash(cBtn);
        AddTabHover(cBtn, () => false);
        menu.Add(cBtn);

        _attachRoot.Add(menu);
        ChatWhisperUtil.InstallTabSwallow(_clickCatcher);
        ChatWhisperUtil.InstallTabSwallow(menu);
        // one of them should hold focus so the key events come to us:
        menu.focusable = true;
        menu.Focus();
        _openMenu = menu;

        // place menu near the row (on top of the scoreboard)
        PlaceMenuNearRow(row, menu, 8f);

        // bring overlay to the very top and mask scoreboard so slider drags work
        _clickCatcher.BringToFront(); menu.BringToFront();
        MaskScoreboardForClicks(ui, true);
    }

    // ===== player info dialog (highlightable) =====
    private static void ShowPlayerInfoDialog(UIScoreboard ui, Player player, string steamId, string profileUrl)
    {
        Debug.Log($"[LocalMute] ShowPlayerInfoDialog called for steamId: {steamId}");
        
        // Find the player in our records
        PlayerInfo foundPlayer = null;

        try
        {
            var savedPlayer = LocalMuteStore.Config.SavedPlayers.FirstOrDefault(p => p.steamId == steamId);
            if (savedPlayer != null)
            {
                foundPlayer = savedPlayer;
                Debug.Log($"[LocalMute] Found player in saved list: {savedPlayer.playerName}");
            }
            else
            {
                var blockedPlayer = LocalMuteStore.Config.BlockedPlayers.FirstOrDefault(p => p.steamId == steamId);
                if (blockedPlayer != null)
                {
                    foundPlayer = blockedPlayer;
                    Debug.Log($"[LocalMute] Found player in blocked list: {blockedPlayer.playerName}");
                }
                else
                {
                    var recentPlayer = LocalMuteStore.Config.RecentPlayers.FirstOrDefault(p => p.steamId == steamId);
                    if (recentPlayer != null)
                    {
                        foundPlayer = recentPlayer;
                        Debug.Log($"[LocalMute] Found player in recent list: {recentPlayer.playerName}");
                    }
                    else
                    {
                        Debug.Log($"[LocalMute] Player not found in any list, creating basic info");
                        // Create basic player info if not found
                        foundPlayer = new PlayerInfo
                        {
                            steamId = steamId,
                            playerName = "Player " + steamId.Substring(Math.Max(0, steamId.Length - 4)),
                            playerNumber = "",
                            profileUrl = profileUrl,
                            notes = "",
                            dateAdded = DateTime.Now,
                            lastServerSeen = ""
                        };
                    }
                }
            }
        }
        catch (Exception e) 
        { 
            Debug.LogError($"[LocalMute] Error looking up player info: {e}");
        }

        // Show standalone info dialog
        ShowStandaloneInfoDialog(ui, foundPlayer);
    }

    // Standalone info dialog that doesn't require KeybindRunner
    private static void ShowStandaloneInfoDialog(UIScoreboard ui, PlayerInfo player)
    {
        Debug.Log("[LocalMute] Creating standalone info dialog");
        
        // Get the UIDocument root
        var uiDoc = ui.GetComponentInParent<UIDocument>();
        if (uiDoc == null || uiDoc.rootVisualElement == null)
        {
            Debug.LogError("[LocalMute] Could not find UIDocument root");
            return;
        }
        
        var root = uiDoc.rootVisualElement;
        
        // Create overlay
        var overlay = new VisualElement();
        overlay.name = "StandalonePlayerInfoOverlay";
        overlay.style.position = Position.Absolute;
        overlay.style.left = 0;
        overlay.style.top = 0;
        overlay.style.right = 0;
        overlay.style.bottom = 0;
        overlay.style.backgroundColor = new Color(0, 0, 0, 0.7f);
        overlay.style.alignItems = Align.Center;
        overlay.style.justifyContent = Justify.Center;
        
        // Get custom panel background color from KeybindRunner if available
        Color panelBg = new Color32(50, 50, 50, 255);
        try
        {
            var runner = UnityEngine.Object.FindFirstObjectByType<PoncePuck.Keybinds.KeybindRunner>();
            if (runner != null)
            {
                var method = typeof(PoncePuck.Keybinds.KeybindRunner).GetMethod("GetMainPanelBackgroundColor",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                if (method != null)
                {
                    var result = method.Invoke(runner, null) as Color?;
                    if (result.HasValue)
                    {
                        panelBg = result.Value;
                    }
                }
            }
        }
        catch { }
        
        var infoPanel = new VisualElement();
        infoPanel.style.width = 1000;
        infoPanel.style.height = 605;
        infoPanel.style.backgroundColor = panelBg;
        infoPanel.style.paddingLeft = 8;
        infoPanel.style.paddingRight = 8;
        infoPanel.style.paddingTop = 8;
        infoPanel.style.paddingBottom = 8;
        
        // Title
        var titleLabel = new Label("PLAYER INFORMATION");
        MakeReadableLabel(titleLabel);
        titleLabel.style.fontSize = 24;
        titleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
        titleLabel.style.marginBottom = 8;
        titleLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
        infoPanel.Add(titleLabel);
        
        // Info section
        var infoSection = new VisualElement();
        infoSection.style.backgroundColor = new StyleColor(new Color32(61, 61, 61, 255));
        infoSection.style.paddingLeft = 8;
        infoSection.style.paddingRight = 8;
        infoSection.style.paddingTop = 8;
        infoSection.style.paddingBottom = 8;
        infoSection.style.marginBottom = 8;
        infoSection.style.marginLeft = 8;
        infoSection.style.marginRight = 8;
        infoSection.style.marginTop = 8;
        
        // Row 1: Player name/number on left, Date on right
        string displayNumber = string.IsNullOrEmpty(player.playerNumber) ? "?" : player.playerNumber;
        
        var row1 = new VisualElement();
        row1.style.flexDirection = FlexDirection.Row;
        row1.style.justifyContent = Justify.SpaceBetween;
        row1.style.marginBottom = 8;
        
        var nameLabel = new Label($"#{displayNumber} {player.playerName.ToUpper()}");
        MakeReadableLabel(nameLabel);
        nameLabel.style.fontSize = 18;
        nameLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
        row1.Add(nameLabel);
        
        var dateLabel = new Label($"Added: {player.dateAdded:MM/dd/yy HH:mm:ss} | Last Seen: {player.lastSeen:MM/dd/yy HH:mm:ss}");
        MakeReadableLabel(dateLabel);
        dateLabel.style.fontSize = 18;
        dateLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
        row1.Add(dateLabel);
        
        infoSection.Add(row1);
        
        // Row 2: Last server on left, Steam ID on right
        var row2 = new VisualElement();
        row2.style.flexDirection = FlexDirection.Row;
        row2.style.justifyContent = Justify.SpaceBetween;
        
        if (!string.IsNullOrEmpty(player.lastServerSeen))
        {
            var serverLabel = new Label($"LAST SERVER: {player.lastServerSeen}");
            MakeReadableLabel(serverLabel);
            serverLabel.style.fontSize = 18;
            serverLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            row2.Add(serverLabel);
        }
        else
        {
            row2.Add(new VisualElement()); // Spacer
        }
        
        var steamIdLabel = new Label($"STEAM ID: {player.steamId}");
        MakeReadableLabel(steamIdLabel);
        steamIdLabel.style.fontSize = 18;
        steamIdLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
        row2.Add(steamIdLabel);
        
        infoSection.Add(row2);
        infoPanel.Add(infoSection);
        
        // Notes header
        var notesHeader = new Label("NOTES:");
        MakeReadableLabel(notesHeader);
        notesHeader.style.fontSize = 18;
        notesHeader.style.unityFontStyleAndWeight = FontStyle.Bold;
        notesHeader.style.marginBottom = 8;
        notesHeader.style.marginLeft = 8;
        notesHeader.style.marginRight = 8;
        infoPanel.Add(notesHeader);
        
        // Notes container
        var notesContainer = new VisualElement();
        notesContainer.style.flexGrow = 1;
        notesContainer.style.marginBottom = 8;
        notesContainer.style.marginLeft = 8;
        notesContainer.style.marginRight = 8;
        notesContainer.style.backgroundColor = new Color(0.1f, 0.1f, 0.1f, 0.8f);
        notesContainer.style.paddingLeft = 8;
        notesContainer.style.paddingRight = 8;
        notesContainer.style.paddingTop = 8;
        notesContainer.style.paddingBottom = 8;
        
                        // Create both TextField and Label (switch between them)
                var notesField = new UnityEngine.UIElements.TextField();
                notesField.multiline = true;
                notesField.style.height = new UnityEngine.UIElements.StyleLength(new UnityEngine.UIElements.Length(100, UnityEngine.UIElements.LengthUnit.Percent));
                notesField.style.whiteSpace = UnityEngine.UIElements.WhiteSpace.Normal;
                notesField.style.unityTextAlign = TextAnchor.UpperLeft;
                notesField.style.backgroundColor = UnityEngine.UIElements.StyleKeyword.Null; // Transparent, container has bg
                MakeReadable(notesField);

                var notesLabel = new UnityEngine.UIElements.Label();
                notesLabel.enableRichText = true;
                notesLabel.style.height = new UnityEngine.UIElements.StyleLength(new UnityEngine.UIElements.Length(100, UnityEngine.UIElements.LengthUnit.Percent));
                notesLabel.style.whiteSpace = UnityEngine.UIElements.WhiteSpace.Normal;
                notesLabel.style.unityTextAlign = TextAnchor.UpperLeft;
                MakeReadable(notesLabel);

                bool isEditing = false;
                UnityEngine.UIElements.Button saveEditBtn = null;

                // Function to switch modes
                System.Action SwitchMode = () =>
                {
                    if (isEditing)
                    {
                        // Switch to view mode (Label with rich text)
                        notesContainer.Clear();
                        notesLabel.text = notesField.value;
                        notesContainer.Add(notesLabel);
                        if (saveEditBtn != null) saveEditBtn.text = "EDIT";
                        isEditing = false;
                    }
                    else
                    {
                        // Switch to edit mode (TextField)
                        notesContainer.Clear();
                        notesField.value = player.notes ?? "";
                        notesContainer.Add(notesField);
                        if (saveEditBtn != null) saveEditBtn.text = "SAVE";
                        isEditing = true;
                        
                        // Force alignment after adding
                        notesField.schedule.Execute(() =>
                        {
                            var input = notesField.Q(className: "unity-text-element");
                            if (input != null)
                            {
                                input.style.unityTextAlign = TextAnchor.UpperLeft;
                                input.style.whiteSpace = UnityEngine.UIElements.WhiteSpace.Normal;
                            }
                        }).ExecuteLater(0);
                    }
                };

                // Start in view mode
                notesLabel.text = player.notes ?? "";
                notesContainer.Add(notesLabel);
                infoPanel.Add(notesContainer);

        // Button row
        var buttonRow = new VisualElement();
        buttonRow.style.flexDirection = FlexDirection.Row;
        buttonRow.style.justifyContent = Justify.Center;
        buttonRow.style.marginBottom = 8;
        buttonRow.style.marginLeft = 8;
        buttonRow.style.marginRight = 8;

        // Profile button
        var profileBtn = new UnityEngine.UIElements.Button(() =>
        {
            try
            {
                // Use Steam overlay to open profile
                if (!string.IsNullOrEmpty(player.steamId))
                {
                    ulong steamId64 = 0;
                    if (ulong.TryParse(player.steamId, out steamId64))
                    {
                        Steamworks.SteamFriends.ActivateGameOverlayToUser("steamid", new Steamworks.CSteamID(steamId64));
                    }
                    else
                    {
                        Debug.LogWarning($"[LocalMute] Invalid Steam ID for profile: {player.steamId}");
                    }
                }
                else
                {
                    Debug.LogWarning($"[LocalMute] No Steam ID available for player");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[LocalMute] Failed to open Steam profile overlay: {e.Message}");
            }
        });
        profileBtn.text = "PROFILE";
        MakeReadableLabel(profileBtn);
        profileBtn.style.unityTextAlign = TextAnchor.MiddleCenter;
        profileBtn.style.height = 50;
        profileBtn.style.width = 150;
        profileBtn.style.paddingLeft = 18;
        profileBtn.style.paddingRight = 18;
        profileBtn.style.marginRight = 8;
        profileBtn.style.backgroundColor = new StyleColor(ButtonBg);
        AddButtonFlash(profileBtn);
        buttonRow.Add(profileBtn);

        // Copy Steam ID button
        var copySteamIdBtn = new UnityEngine.UIElements.Button(() =>
        {
            try
            {
                GUIUtility.systemCopyBuffer = player.steamId;
                LogHelper.Log($"[LocalMute] Copied Steam ID to clipboard: {player.steamId}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[LocalMute] Failed to copy Steam ID: {e.Message}");
            }
        });
        copySteamIdBtn.text = "COPY ID";
        MakeReadableLabel(copySteamIdBtn);
        copySteamIdBtn.style.unityTextAlign = TextAnchor.MiddleCenter;
        copySteamIdBtn.style.height = 50;
        copySteamIdBtn.style.width = 150;
        copySteamIdBtn.style.paddingLeft = 18;
        copySteamIdBtn.style.paddingRight = 18;
        copySteamIdBtn.style.marginRight = 8;
        copySteamIdBtn.style.backgroundColor = new StyleColor(ButtonBg);
        copySteamIdBtn.style.whiteSpace = WhiteSpace.NoWrap;
        AddButtonFlash(copySteamIdBtn);
        buttonRow.Add(copySteamIdBtn);

        // Save/Edit toggle button
        saveEditBtn = new UnityEngine.UIElements.Button(() =>
        {
            if (isEditing)
            {
                // Save the notes
                PoncePuck.LocalMute.LocalMuteStore.UpdatePlayerNotes(player.steamId, notesField.value);
                player.notes = notesField.value; // Update local copy
            }
            // Toggle mode
            SwitchMode();
        });

        saveEditBtn.text = "EDIT"; // Start with EDIT since we begin in view mode
        saveEditBtn.style.unityTextAlign = TextAnchor.MiddleCenter;
        saveEditBtn.style.height = 50;
        saveEditBtn.style.width = 120;
        saveEditBtn.style.paddingLeft = 18;
        saveEditBtn.style.paddingRight = 18;
        saveEditBtn.style.marginRight = 8;
        saveEditBtn.style.backgroundColor = new StyleColor(ButtonBg);
        MakeReadableLabel(saveEditBtn);
                AddButtonFlash(saveEditBtn);
                buttonRow.Add(saveEditBtn);

        // Close button
        var closeBtn = new UnityEngine.UIElements.Button(() =>
        {
            overlay.RemoveFromHierarchy();
            Debug.Log("[LocalMute] Closed standalone info dialog");
        });
        closeBtn.text = "CLOSE";
        MakeReadableLabel(closeBtn);
        closeBtn.style.unityTextAlign = TextAnchor.MiddleCenter;
        closeBtn.style.height = 50;
        closeBtn.style.width = 150;
        closeBtn.style.paddingLeft = 18;
        closeBtn.style.paddingRight = 18;
        closeBtn.style.backgroundColor = new StyleColor(ButtonBg);
        AddButtonFlash(closeBtn);
        buttonRow.Add(closeBtn);

        infoPanel.Add(buttonRow);
        overlay.Add(infoPanel);
        root.Add(overlay);

        // Keep cursor active while dialog is open
        LocalMuteRunner.Run(KeepCursorActiveWhileDialogOpen(overlay));

        Debug.Log("[LocalMute] Standalone info dialog created and added to root");
    }

    private static System.Collections.IEnumerator KeepCursorActiveWhileDialogOpen(VisualElement overlay)
    {
        while (overlay != null && overlay.parent != null)
        {
            // Keep cursor unlocked and visible
            UnityEngine.Cursor.lockState = CursorLockMode.None;
            UnityEngine.Cursor.visible = true;
            
            // B310: isMouseActive replaced with GlobalStateManager.UIState.IsMouseRequired
            try
            {
                var uiState = GlobalStateManager.UIState;
                uiState.IsMouseRequired = true;
                GlobalStateManager.UIState = uiState;
            }
            catch { }
            
            yield return null; // Wait one frame
        }
        
        Debug.Log("[LocalMute] Dialog closed, stopped keeping cursor active");
    }
    
    public static void MakeReadableLabel(VisualElement ve)
    {
        ve.style.color = Color.white;
        ve.style.unityFont = GetUIFont();
    }

    private static void ShowBasicPlayerInfoDialog(UIScoreboard ui, Player player, string steamId, string profileUrl, PlayerInfo foundPlayer)
    {
        var dialog = new VisualElement { name = "PlayerInfoDialog" };
        dialog.style.position = Position.Absolute;
        dialog.style.left = 0; dialog.style.top = 0;
        dialog.style.right = 0; dialog.style.bottom = 0;
        dialog.style.backgroundColor = new Color(0, 0, 0, 0.8f);
        dialog.style.alignItems = Align.Center;
        dialog.style.justifyContent = Justify.Center;
        dialog.pickingMode = PickingMode.Position;

        var panel = new VisualElement();
        panel.style.backgroundColor = new Color(0.15f, 0.15f, 0.15f, 1f);
        panel.style.paddingLeft = 20; panel.style.paddingRight = 20;
        panel.style.paddingTop = 20; panel.style.paddingBottom = 20;
        panel.style.minWidth = 400;
        panel.style.maxWidth = 600;

        // Get player info from passed parameter or try to find it
        string playerName = "Unknown";
        string playerNumber = "?";

        if (foundPlayer != null)
        {
            playerName = foundPlayer.playerName;
            playerNumber = foundPlayer.playerNumber;
        }
        else
        {
            try
            {
                var savedPlayer = LocalMuteStore.Config.SavedPlayers.FirstOrDefault(p => p.steamId == steamId);
                if (savedPlayer != null)
                {
                    playerName = savedPlayer.playerName;
                    playerNumber = savedPlayer.playerNumber;
                    foundPlayer = savedPlayer;
                }
                else
                {
                    var blockedPlayer = LocalMuteStore.Config.BlockedPlayers.FirstOrDefault(p => p.steamId == steamId);
                    if (blockedPlayer != null)
                    {
                        playerName = blockedPlayer.playerName;
                        playerNumber = blockedPlayer.playerNumber;
                        foundPlayer = blockedPlayer;
                    }
                    else
                    {
                        var recentPlayer = LocalMuteStore.Config.RecentPlayers.FirstOrDefault(p => p.steamId == steamId);
                        if (recentPlayer != null)
                        {
                            playerName = recentPlayer.playerName;
                            playerNumber = recentPlayer.playerNumber;
                            foundPlayer = recentPlayer;
                        }
                    }
                }
            }
            catch (Exception e) 
            { 
                Debug.LogError($"[LocalMute] Error looking up player info: {e}");
            }
        }

        try
        {
            var savedPlayer = LocalMuteStore.Config.SavedPlayers.FirstOrDefault(p => p.steamId == steamId);
            if (savedPlayer != null)
            {
                playerName = savedPlayer.playerName;
                playerNumber = savedPlayer.playerNumber;
                foundPlayer = savedPlayer;
            }
            else
            {
                var blockedPlayer = LocalMuteStore.Config.BlockedPlayers.FirstOrDefault(p => p.steamId == steamId);
                if (blockedPlayer != null)
                {
                    playerName = blockedPlayer.playerName;
                    playerNumber = blockedPlayer.playerNumber;
                    foundPlayer = blockedPlayer;
                }
                else
                {
                    var recentPlayer = LocalMuteStore.Config.RecentPlayers.FirstOrDefault(p => p.steamId == steamId);
                    if (recentPlayer != null)
                    {
                        playerName = recentPlayer.playerName;
                        playerNumber = recentPlayer.playerNumber;
                        foundPlayer = recentPlayer;
                    }
                }
            }
        }
        catch (Exception e) 
        { 
            Debug.LogError($"[LocalMute] Error looking up player info: {e}");
        }
        
        // Only show number if it's valid (not null, not empty, not '?', and not whitespace)
        string trimmedNumber = playerNumber?.Trim();
        bool validNumber = !string.IsNullOrEmpty(trimmedNumber) && trimmedNumber != "?";
        string titleText = validNumber ? $"PLAYER INFO - #{trimmedNumber} {playerName}" : $"PLAYER INFO - {playerName}";

        var title = new Label(titleText);
        MakeReadable(title);
        title.style.fontSize = 20;
        title.style.unityFontStyleAndWeight = FontStyle.Bold;
        title.style.marginBottom = 15;
        panel.Add(title);

        // Show last seen server if available
        
        // Steam ID (highlightable)
        var steamIdField = new TextField("Steam ID") { value = steamId, isReadOnly = true };
        MakeReadable(steamIdField);
        steamIdField.style.marginBottom = 10;
        panel.Add(steamIdField);
        
        // Profile URL (highlightable)
        var profileField = new TextField("Profile URL") { value = profileUrl, isReadOnly = true };
        MakeReadable(profileField);
        profileField.style.marginBottom = 10;
        panel.Add(profileField);
        
        // VIEW PROFILE button
        var profileBtn = new UnityEngine.UIElements.Button(() => { 
            OpenProfile(ui, player);
        }) { text = "OPEN STEAM PROFILE" };
        MakeReadable(profileBtn);
        profileBtn.style.marginTop = 10;
        profileBtn.style.marginBottom = 10;
        profileBtn.style.height = 40;
        profileBtn.style.backgroundColor = new Color(0.2f, 0.4f, 0.6f, 1f);
        AddButtonFlash(profileBtn);
        panel.Add(profileBtn);
        
        // CLOSE button
        var closeBtn = new UnityEngine.UIElements.Button(() => {
            var root = GetScoreboardRoot(ui) ?? GetTopLevelRoot();
            if (root != null && dialog.parent == root)
            {
                root.Remove(dialog);
            }
        }) { text = "CLOSE" };
        MakeReadable(closeBtn);
        closeBtn.style.marginTop = 10;
        closeBtn.style.height = 40;
        closeBtn.style.backgroundColor = new Color(0.5f, 0.5f, 0.5f, 1f);
        AddButtonFlash(closeBtn);
        panel.Add(closeBtn);
        
        dialog.Add(panel);
        
        // Click background to close
        dialog.RegisterCallback<ClickEvent>(evt => {
            if (evt.target == dialog)
            {
                var root = GetScoreboardRoot(ui) ?? GetTopLevelRoot();
                if (root != null && dialog.parent == root)
                {
                    root.Remove(dialog);
                }
            }
        });
        
        var root2 = GetScoreboardRoot(ui) ?? GetTopLevelRoot();
        if (root2 != null)
        {
            root2.Add(dialog);
            dialog.BringToFront();
        }
    }

    // ===== admin overlay menu =====
    public static void OpenAdminOverlayMenu(UIScoreboard ui, VisualElement row, Player player)
    {
        if (row == null || player == null) return;

        CloseAllMenus(); // one-at-a-time

        _attachRoot = ResolveAttachRoot(ui, row);
        if (_attachRoot == null) return;

        // click-catcher background (closes on left-click)
        _clickCatcher = new VisualElement { name = "LM_AdminMenuBackdrop" };
        _clickCatcher.style.position = Position.Absolute;
        _clickCatcher.style.left = 0; _clickCatcher.style.top = 0;
        _clickCatcher.style.right = 0; _clickCatcher.style.bottom = 0;
        _clickCatcher.style.backgroundColor = new Color(); // invisible
        _clickCatcher.pickingMode = PickingMode.Position;
        _clickCatcher.RegisterCallback<PointerUpEvent>(_ => CloseAllMenus());
        _attachRoot.Add(_clickCatcher);

        // the admin menu
        var menu = new VisualElement { name = "LocalMuteAdminMenu" };
        menu.style.position = Position.Absolute;
        
        // Use the same background color as the main panel
        var keybindRunner = UnityEngine.Object.FindFirstObjectByType<PoncePuck.Keybinds.KeybindRunner>();
        Color menuBg = TextFieldBg; // fallback
        if (keybindRunner != null)
        {
            try
            {
                var panelBg = keybindRunner.GetMainPanelBackgroundColor();
                if (panelBg.HasValue) menuBg = panelBg.Value;
            }
            catch { }
        }
        
        menu.style.backgroundColor = new UnityEngine.UIElements.StyleColor(menuBg);
        menu.style.paddingLeft = 4; menu.style.paddingRight = 4;
        menu.style.paddingTop = 6; menu.style.paddingBottom = 4;
        menu.style.flexDirection = FlexDirection.Column;
        menu.style.minWidth = 240;
        menu.pickingMode = PickingMode.Position;

        string playerName = player.Username?.Value.ToString() ?? "Unknown";
        string cleanPlayerName = StripAdminTags(playerName); // For admin commands
        string steamId = player.SteamId?.Value.ToString() ?? "0";
        string playerNumber = player.Number?.Value.ToString() ?? "?";

        // Admin menu header
        var header = new UnityEngine.UIElements.Label($"ACTIONS TO {playerName}");
        MakeReadable(header);
        header.style.fontSize = 16;
        header.style.unityFontStyleAndWeight = FontStyle.Bold;
        header.style.marginTop = 4;
        header.style.marginBottom = 8;
        header.style.marginLeft = 4;
        header.style.marginRight = 4;
        header.style.color = new UnityEngine.UIElements.StyleColor(Color.yellow);
        menu.Add(header);

        // BAN STEAM ID button
        var banBtn = new UnityEngine.UIElements.Button(() => {
            ShowBanConfirmationDialog("BAN PLAYER", playerNumber, playerName, cleanPlayerName, steamId);
        }) { text = "BAN" };
        StyleAdminButton(banBtn, ButtonBg);
        menu.Add(banBtn);

        // MUTE duration slider (1m to 4w)
        // Duration values: 1m, 5m, 10m, 30m, 1h, 2h, 6h, 12h, 1d, 3d, 1w, 2w, 4w
        var muteDurations = new[] { "1m", "5m", "10m", "30m", "1h", "2h", "6h", "12h", "1d", "3d", "1w", "2w", "4w" };
        int currentMuteDurationIndex = 2; // Default to 10m

        // MUTE button - uses slider value
        var muteBtn = new UnityEngine.UIElements.Button(() => {
            string duration = muteDurations[currentMuteDurationIndex];
            SendChatCommand($"/mute {cleanPlayerName} {duration}");
            Debug.Log($"[LocalMute] Admin MUTE executed for {cleanPlayerName} ({duration})");
            CloseAllMenus();
        }) { text = "MUTE" };
        StyleAdminButton(muteBtn, ButtonBg);
        menu.Add(muteBtn);

        var muteDurationRow = new VisualElement();
        muteDurationRow.style.flexDirection = FlexDirection.Row;
        muteDurationRow.style.justifyContent = Justify.SpaceBetween;
        muteDurationRow.style.alignItems = Align.Center;
        muteDurationRow.style.marginBottom = 4;
        muteDurationRow.style.marginLeft = 4;
        muteDurationRow.style.marginRight = 4;
        muteDurationRow.style.paddingLeft = 6;
        muteDurationRow.style.paddingRight = 6;
        muteDurationRow.style.paddingTop = 6;
        muteDurationRow.style.paddingBottom = 6;
        muteDurationRow.style.height = 36;
        muteDurationRow.style.backgroundColor = new UnityEngine.UIElements.StyleColor(ButtonBg);

        var muteDurationValue = new Label(muteDurations[currentMuteDurationIndex]);
        MakeReadable(muteDurationValue);
        muteDurationValue.style.unityTextAlign = TextAnchor.MiddleRight;
        muteDurationValue.style.minWidth = 50;
        muteDurationRow.Add(muteDurationValue);

        var muteDurationSlider = new UnityEngine.UIElements.Slider(0, muteDurations.Length - 1) { value = currentMuteDurationIndex };
        muteDurationSlider.style.flexGrow = 1;
        muteDurationSlider.style.marginLeft = 10;
        muteDurationSlider.style.marginRight = 10;
        StyleSliderLikeBase(muteDurationSlider);

        muteDurationSlider.RegisterValueChangedCallback(evt =>
        {
            int index = Mathf.RoundToInt(evt.newValue);
            currentMuteDurationIndex = index;
            muteDurationValue.text = muteDurations[index];
        });

        muteDurationRow.Add(muteDurationSlider);
        menu.Add(muteDurationRow);

        // UNMUTE button
        var unmuteBtn = new UnityEngine.UIElements.Button(() => {
            SendChatCommand($"/unmute {cleanPlayerName}");
            Debug.Log($"[LocalMute] Admin UNMUTE executed for {cleanPlayerName}");
            CloseAllMenus();
        }) { text = "UNMUTE" };
        StyleAdminButton(unmuteBtn, BtnBrightGray);
        menu.Add(unmuteBtn);

        // INFO button - sends /whoami command
        var infoBtn = new UnityEngine.UIElements.Button(() => {
            SendChatCommand($"/whoami {cleanPlayerName}");
            Debug.Log($"[LocalMute] Admin INFO executed for {cleanPlayerName}");
            CloseAllMenus();
        }) { text = "INFO" };
        StyleAdminButton(infoBtn, BtnBrightGray);
        menu.Add(infoBtn);

        // KICK button
        var kickBtn = new UnityEngine.UIElements.Button(() => {
            ShowKickConfirmationDialog("KICK PLAYER", playerNumber, playerName, cleanPlayerName, steamId);
        }) { text = "KICK" };
        StyleAdminButton(kickBtn, BtnBrightGray);
        menu.Add(kickBtn);

        // FREEZE button
        var freezeBtn = new UnityEngine.UIElements.Button(() => {
            SendChatCommand($"/freeze {cleanPlayerName}");
            Debug.Log($"[LocalMute] Admin FREEZE executed for {cleanPlayerName}");
            CloseAllMenus();
        }) { text = "FREEZE" };
        StyleAdminButton(freezeBtn, BtnBrightGray);
        menu.Add(freezeBtn);

        // UNFREEZE button
        var unfreezeBtn = new UnityEngine.UIElements.Button(() => {
            SendChatCommand($"/unfreeze {cleanPlayerName}");
            Debug.Log($"[LocalMute] Admin UNFREEZE executed for {cleanPlayerName}");
            CloseAllMenus();
        }) { text = "UNFREEZE" };
        StyleAdminButton(unfreezeBtn, BtnBrightGray);
        menu.Add(unfreezeBtn);

        // SWAP button - prefills command
        var swapBtn = new UnityEngine.UIElements.Button(() => {
            string prefillText = $"/swap {cleanPlayerName} ";
            Debug.Log($"[LocalMute] Admin SWAP prefill for {cleanPlayerName}");
            CloseAllMenus();
            LocalMuteRunner.Run(ChatWhisperUtil.DelayedPrefill(prefillText));
        }) { text = "SWAP" };
        StyleAdminButton(swapBtn, BtnBrightGray);
        menu.Add(swapBtn);

        // CHANGE TEAM button - prefills command
        var ctBtn = new UnityEngine.UIElements.Button(() => {
            string prefillText = $"/ct {cleanPlayerName} ";
            Debug.Log($"[LocalMute] Admin CHANGE TEAM prefill for {cleanPlayerName}");
            CloseAllMenus();
            LocalMuteRunner.Run(ChatWhisperUtil.DelayedPrefill(prefillText));
        }) { text = "CHANGE TEAM" };
        StyleAdminButton(ctBtn, BtnBrightGray);
        menu.Add(ctBtn);

        // CLOSE button
        var closeBtn = new UnityEngine.UIElements.Button(() => { CloseAllMenus(); }) { text = "CLOSE" };
        StyleAdminButton(closeBtn, BtnBrightGray);
        menu.Add(closeBtn);

        _attachRoot.Add(menu);
        ChatWhisperUtil.InstallTabSwallow(_clickCatcher);
        ChatWhisperUtil.InstallTabSwallow(menu);
        menu.focusable = true;
        menu.Focus();
        _openMenu = menu;

        // place menu near the row
        PlaceMenuNearRow(row, menu, 8f);

        // bring overlay to the top
        _clickCatcher.BringToFront(); menu.BringToFront();
        MaskScoreboardForClicks(ui, true);
    }

    private static void StyleAdminButton(UnityEngine.UIElements.Button btn, Color bgColor)
    {
        MakeReadable(btn);
        btn.style.marginBottom = 4;
        btn.style.marginLeft = 4; btn.style.marginRight = 4;
        btn.style.paddingLeft = 6; btn.style.paddingTop = 6;
        btn.style.paddingBottom = 4;
        btn.style.height = 36;
        btn.style.backgroundColor = new UnityEngine.UIElements.StyleColor(bgColor);
        AddButtonFlash(btn);
        AddTabHover(btn, () => false);
    }

    private static void SendChatCommand(string command)
    {
        try
        {
            var keybindRunner = UnityEngine.Object.FindFirstObjectByType<PoncePuck.Keybinds.KeybindRunner>();
            if (keybindRunner != null)
            {
                keybindRunner.SendChatMessage(command);
                Debug.Log($"[LocalMute] Sent command via KeybindRunner: {command}");
            }
            else
            {
                Debug.LogWarning("[LocalMute] KeybindRunner not found, cannot send command");
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[LocalMute] Failed to send command '{command}': {e}");
        }
    }

    private static void ShowBanConfirmationDialog(string title, string playerNumber, string playerName, string cleanPlayerName, string steamId)
     {
        CloseAllMenus();

        var ui = UnityEngine.Object.FindFirstObjectByType<UIScoreboard>(UnityEngine.FindObjectsInactive.Include);
        _attachRoot = ResolveAttachRoot(ui, null);
        if (_attachRoot == null || _attachRoot.panel == null) return;

        // Create overlay
        if (_clickCatcher == null)
        {
            _clickCatcher = new VisualElement();
            _clickCatcher.style.position = UnityEngine.UIElements.Position.Absolute;
            _clickCatcher.style.left = 0; _clickCatcher.style.right = 0;
            _clickCatcher.style.top = 0; _clickCatcher.style.bottom = 0;
            _clickCatcher.style.backgroundColor = new UnityEngine.UIElements.StyleColor(new Color(0, 0, 0, 0.75f));
            _clickCatcher.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());
            _clickCatcher.focusable = true;
        }
        _attachRoot.Add(_clickCatcher);

        // Create confirmation dialog
        var dialog = new VisualElement();
        dialog.style.position = UnityEngine.UIElements.Position.Absolute;
        dialog.style.width = 420;
        dialog.style.minHeight = 320;
        dialog.style.backgroundColor = new UnityEngine.UIElements.StyleColor(new Color(0.15f, 0.15f, 0.15f, 1f));
        dialog.style.paddingTop = 8;
        dialog.style.paddingBottom = 8;
        dialog.style.paddingLeft = 8;
        dialog.style.paddingRight = 8;

        // Title
        var titleLabel = new Label(title);
        MakeReadable(titleLabel);
        titleLabel.style.fontSize = 20;
        titleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
        titleLabel.style.color = new UnityEngine.UIElements.StyleColor(Color.yellow);
        titleLabel.style.marginBottom = 12;
        titleLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
        dialog.Add(titleLabel);

        // Message
        var messageLabel = new Label($"CONFIRMATION\n\n{playerNumber} - {playerName}\nSteam ID: {steamId}");
        MakeReadable(messageLabel);
        messageLabel.style.fontSize = 16;
        messageLabel.style.color = new UnityEngine.UIElements.StyleColor(P_White);
        messageLabel.style.marginBottom = 20;
        messageLabel.style.whiteSpace = WhiteSpace.Normal;
        messageLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
        dialog.Add(messageLabel);

        // Button rows
        var buttonRow1 = new VisualElement();
        buttonRow1.style.flexDirection = FlexDirection.Row;
        buttonRow1.style.justifyContent = Justify.Center;
        buttonRow1.style.marginBottom = 8;
        dialog.Add(buttonRow1);

        var buttonRow2 = new VisualElement();
        buttonRow2.style.flexDirection = FlexDirection.Row;
        buttonRow2.style.justifyContent = Justify.Center;
        buttonRow2.style.marginBottom = 8;
        dialog.Add(buttonRow2);

        var buttonRow3 = new VisualElement();
        buttonRow3.style.flexDirection = FlexDirection.Row;
        buttonRow3.style.justifyContent = Justify.Center;
        dialog.Add(buttonRow3);

        // Ban by Number button
        var BanNumberBtn = new UnityEngine.UIElements.Button(() => {
            SendChatCommand($"/ban {playerNumber}");
            LogHelper.Log($"[LocalMute] Admin BAN by number executed for {playerNumber}");
            CloseAllMenus();
        }) { text = $"BAN NUMBER" };
        MakeReadable(BanNumberBtn);
        BanNumberBtn.style.width = 200;
        BanNumberBtn.style.height = 50;
        BanNumberBtn.style.marginRight = 8;
        BanNumberBtn.style.paddingLeft = 18;
        BanNumberBtn.style.paddingRight = 18;
        BanNumberBtn.style.paddingTop = 12;
        BanNumberBtn.style.paddingBottom = 12;
        BanNumberBtn.style.unityTextAlign = TextAnchor.MiddleCenter;
        BanNumberBtn.style.backgroundColor = new UnityEngine.UIElements.StyleColor(ButtonBg);
        BanNumberBtn.style.whiteSpace = WhiteSpace.NoWrap;
        AddButtonFlash(BanNumberBtn);
        buttonRow1.Add(BanNumberBtn);

        // Ban by Name button
        var BanNameBtn = new UnityEngine.UIElements.Button(() => {
            SendChatCommand($"/ban {cleanPlayerName}");
            LogHelper.Log($"[LocalMute] Admin BAN by name executed for {cleanPlayerName}");
            CloseAllMenus();
        }) { text = $"BAN NAME" };
        MakeReadable(BanNameBtn);
        BanNameBtn.style.width = 200;
        BanNameBtn.style.height = 50;
        BanNameBtn.style.paddingLeft = 18;
        BanNameBtn.style.paddingRight = 18;
        BanNameBtn.style.paddingTop = 12;
        BanNameBtn.style.paddingBottom = 12;
        BanNameBtn.style.unityTextAlign = TextAnchor.MiddleCenter;
        BanNameBtn.style.backgroundColor = new UnityEngine.UIElements.StyleColor(ButtonBg);
        BanNameBtn.style.whiteSpace = WhiteSpace.NoWrap;
        AddButtonFlash(BanNameBtn);
        buttonRow1.Add(BanNameBtn);

                // ban by id button
        var BanSteamBtn = new UnityEngine.UIElements.Button(() => {
            SendChatCommand($"/bansteamid {steamId}");
            LogHelper.Log($"[LocalMute] Admin BAN by Steam ID executed for {steamId}");
            CloseAllMenus();
        }) { text = $"BAN ID" };
        MakeReadable(BanSteamBtn);
        BanSteamBtn.style.width = 200;
        BanSteamBtn.style.height = 50;
        BanSteamBtn.style.paddingLeft = 18;
        BanSteamBtn.style.paddingRight = 18;
        BanSteamBtn.style.paddingTop = 12;
        BanSteamBtn.style.paddingBottom = 12;
        BanSteamBtn.style.marginRight = 8;
        BanSteamBtn.style.unityTextAlign = TextAnchor.MiddleCenter;
        BanSteamBtn.style.backgroundColor = new UnityEngine.UIElements.StyleColor(ButtonBg);
        BanSteamBtn.style.whiteSpace = WhiteSpace.NoWrap;
        AddButtonFlash(BanSteamBtn);
        buttonRow2.Add(BanSteamBtn);

        // Copy Steam ID button
        var copySteamIdBtn = new UnityEngine.UIElements.Button(() => {
            GUIUtility.systemCopyBuffer = steamId;
            LogHelper.Log($"[LocalMute] Copied Steam ID to clipboard: {steamId}");
        }) { text = "COPY ID" };
        MakeReadable(copySteamIdBtn);
        copySteamIdBtn.style.width = 200;
        copySteamIdBtn.style.height = 50;
        copySteamIdBtn.style.paddingLeft = 18;
        copySteamIdBtn.style.paddingRight = 18;
        copySteamIdBtn.style.paddingTop = 12;
        copySteamIdBtn.style.paddingBottom = 12;
        copySteamIdBtn.style.unityTextAlign = TextAnchor.MiddleCenter;
        copySteamIdBtn.style.backgroundColor = new UnityEngine.UIElements.StyleColor(ButtonBg);
        copySteamIdBtn.style.whiteSpace = WhiteSpace.NoWrap;
        AddButtonFlash(copySteamIdBtn);
        buttonRow2.Add(copySteamIdBtn);

        // Cancel button (full width on third row)
        var cancelBtn = new UnityEngine.UIElements.Button(() => {
            CloseAllMenus();
        }) { text = "CANCEL" };
        MakeReadable(cancelBtn);
        cancelBtn.style.width = 410;
        cancelBtn.style.height = 50;
        cancelBtn.style.paddingLeft = 18;
        cancelBtn.style.paddingRight = 18;
        cancelBtn.style.paddingTop = 12;
        cancelBtn.style.paddingBottom = 12;
        cancelBtn.style.unityTextAlign = TextAnchor.MiddleCenter;
        cancelBtn.style.backgroundColor = new UnityEngine.UIElements.StyleColor(ButtonBg);
        cancelBtn.style.whiteSpace = WhiteSpace.NoWrap;
        AddButtonFlash(cancelBtn);
        buttonRow3.Add(cancelBtn);

        _attachRoot.Add(dialog);
        ChatWhisperUtil.InstallTabSwallow(_clickCatcher);
        ChatWhisperUtil.InstallTabSwallow(dialog);
        dialog.focusable = true;
        dialog.Focus();
        _openMenu = dialog;

        // Center the dialog
        dialog.schedule.Execute(() =>
        {
            if (_attachRoot == null || _attachRoot.panel == null)
            {
                CloseAllMenus();
                return;
            }
            Rect pr = _attachRoot.contentRect;
            Rect dr = dialog.contentRect;
            dialog.style.left = (pr.width - dr.width) / 2;
            dialog.style.top = (pr.height - dr.height) / 2;
        });

        dialog.schedule.Execute(() =>
        {
            if (_attachRoot == null || _attachRoot.panel == null || !IsScoreboardVisible(ui))
                CloseAllMenus();
        }).Every(100);

        // Bring overlay and dialog to the top
        _clickCatcher.BringToFront();
        dialog.BringToFront();
    }

    private static void ShowKickConfirmationDialog(string title, string playerNumber, string playerName, string cleanPlayerName, string steamId)
    {
        CloseAllMenus();

        var ui = UnityEngine.Object.FindFirstObjectByType<UIScoreboard>(UnityEngine.FindObjectsInactive.Include);
        _attachRoot = ResolveAttachRoot(ui, null);
        if (_attachRoot == null || _attachRoot.panel == null) return;

        // Create overlay
        if (_clickCatcher == null)
        {
            _clickCatcher = new VisualElement();
            _clickCatcher.style.position = UnityEngine.UIElements.Position.Absolute;
            _clickCatcher.style.left = 0; _clickCatcher.style.right = 0;
            _clickCatcher.style.top = 0; _clickCatcher.style.bottom = 0;
            _clickCatcher.style.backgroundColor = new UnityEngine.UIElements.StyleColor(new Color(0, 0, 0, 0.75f));
            _clickCatcher.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());
            _clickCatcher.focusable = true;
        }
        _attachRoot.Add(_clickCatcher);

                // Create confirmation dialog
        var dialog = new VisualElement();
        dialog.style.position = UnityEngine.UIElements.Position.Absolute;
        dialog.style.width = 420;
        dialog.style.height = 320;
        dialog.style.backgroundColor = new UnityEngine.UIElements.StyleColor(new Color(0.15f, 0.15f, 0.15f, 1f));
        dialog.style.paddingTop = 8;
        dialog.style.paddingBottom = 8;
        dialog.style.paddingLeft = 8;
        dialog.style.paddingRight = 8;

        // Title
        var titleLabel = new Label(title);
        MakeReadable(titleLabel);
        titleLabel.style.fontSize = 20;
        titleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
        titleLabel.style.color = new UnityEngine.UIElements.StyleColor(Color.yellow);
        titleLabel.style.marginBottom = 12;
        titleLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
        dialog.Add(titleLabel);

        // Message
        var messageLabel = new Label($"CONFIRMATION\n\n{playerNumber} - {playerName}\nSteam ID: {steamId}");
        MakeReadable(messageLabel);
        messageLabel.style.fontSize = 16;
        messageLabel.style.color = new UnityEngine.UIElements.StyleColor(P_White);
        messageLabel.style.marginBottom = 20;
        messageLabel.style.whiteSpace = WhiteSpace.Normal;
        messageLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
        dialog.Add(messageLabel);

        // Button rows
        var buttonRow1 = new VisualElement();
        buttonRow1.style.flexDirection = FlexDirection.Row;
        buttonRow1.style.justifyContent = Justify.Center;
        buttonRow1.style.marginBottom = 8;
        dialog.Add(buttonRow1);

        var buttonRow2 = new VisualElement();
        buttonRow2.style.flexDirection = FlexDirection.Row;
        buttonRow2.style.justifyContent = Justify.Center;
        buttonRow2.style.marginBottom = 8;
        dialog.Add(buttonRow2);

        var buttonRow3 = new VisualElement();
        buttonRow3.style.flexDirection = FlexDirection.Row;
        buttonRow3.style.justifyContent = Justify.Center;
        dialog.Add(buttonRow3);

        // Kick by Number button
        var kickNumberBtn = new UnityEngine.UIElements.Button(() => {
            SendChatCommand($"/kick {playerNumber}");
            LogHelper.Log($"[LocalMute] Admin KICK by number executed for {playerNumber}");
            CloseAllMenus();
        }) { text = $"KICK NUMBER" };
        MakeReadable(kickNumberBtn);
        kickNumberBtn.style.width = 200;
        kickNumberBtn.style.height = 50;
        kickNumberBtn.style.marginRight = 8;
        kickNumberBtn.style.paddingLeft = 18;
        kickNumberBtn.style.paddingRight = 18;
        kickNumberBtn.style.paddingTop = 12;
        kickNumberBtn.style.paddingBottom = 12;
        kickNumberBtn.style.unityTextAlign = TextAnchor.MiddleCenter;
        kickNumberBtn.style.backgroundColor = new UnityEngine.UIElements.StyleColor(ButtonBg);
        kickNumberBtn.style.whiteSpace = WhiteSpace.NoWrap;
        AddButtonFlash(kickNumberBtn);
        buttonRow1.Add(kickNumberBtn);

        // Kick by Name button
        var kickNameBtn = new UnityEngine.UIElements.Button(() => {
            SendChatCommand($"/kick {cleanPlayerName}");
            LogHelper.Log($"[LocalMute] Admin KICK by name executed for {cleanPlayerName}");
            CloseAllMenus();
        }) { text = $"KICK NAME" };
        MakeReadable(kickNameBtn);
        kickNameBtn.style.width = 200;
        kickNameBtn.style.height = 50;
        kickNameBtn.style.paddingLeft = 18;
        kickNameBtn.style.paddingRight = 18;
        kickNameBtn.style.paddingTop = 12;
        kickNameBtn.style.paddingBottom = 12;
        kickNameBtn.style.unityTextAlign = TextAnchor.MiddleCenter;
        kickNameBtn.style.backgroundColor = new UnityEngine.UIElements.StyleColor(ButtonBg);
        kickNameBtn.style.whiteSpace = WhiteSpace.NoWrap;
        AddButtonFlash(kickNameBtn);
        buttonRow1.Add(kickNameBtn);

                        // Kick by Name button
        var kickSteamBtn = new UnityEngine.UIElements.Button(() => {
            SendChatCommand($"/kicksteamid {steamId}");
            LogHelper.Log($"[LocalMute] Admin KICK by Steam ID executed for {steamId}");
            CloseAllMenus();
        }) { text = $"KICK ID" };
        MakeReadable(kickSteamBtn);
        kickSteamBtn.style.width = 200;
        kickSteamBtn.style.height = 50;
        kickSteamBtn.style.paddingLeft = 18;
        kickSteamBtn.style.paddingRight = 18;
        kickSteamBtn.style.paddingTop = 12;
        kickSteamBtn.style.paddingBottom = 12;
        kickSteamBtn.style.marginRight = 8;
        kickSteamBtn.style.unityTextAlign = TextAnchor.MiddleCenter;
        kickSteamBtn.style.backgroundColor = new UnityEngine.UIElements.StyleColor(ButtonBg);
        kickSteamBtn.style.whiteSpace = WhiteSpace.NoWrap;
        AddButtonFlash(kickSteamBtn);
        buttonRow2.Add(kickSteamBtn);

        // Copy Steam ID button
        var copySteamIdBtn = new UnityEngine.UIElements.Button(() => {
            GUIUtility.systemCopyBuffer = steamId;
            LogHelper.Log($"[LocalMute] Copied Steam ID to clipboard: {steamId}");
        }) { text = "COPY ID" };
        MakeReadable(copySteamIdBtn);
        copySteamIdBtn.style.width = 200;
        copySteamIdBtn.style.height = 50;
        copySteamIdBtn.style.paddingLeft = 18;
        copySteamIdBtn.style.paddingRight = 18;
        copySteamIdBtn.style.paddingTop = 12;
        copySteamIdBtn.style.paddingBottom = 12;
        copySteamIdBtn.style.unityTextAlign = TextAnchor.MiddleCenter;
        copySteamIdBtn.style.backgroundColor = new UnityEngine.UIElements.StyleColor(ButtonBg);
        copySteamIdBtn.style.whiteSpace = WhiteSpace.NoWrap;
        AddButtonFlash(copySteamIdBtn);
        buttonRow2.Add(copySteamIdBtn);

        // Cancel button (full width on third row)
        var cancelBtn = new UnityEngine.UIElements.Button(() => {
            CloseAllMenus();
        }) { text = "CANCEL" };
        MakeReadable(cancelBtn);
        cancelBtn.style.width = 410;
        cancelBtn.style.height = 50;
        cancelBtn.style.paddingLeft = 18;
        cancelBtn.style.paddingRight = 18;
        cancelBtn.style.paddingTop = 12;
        cancelBtn.style.paddingBottom = 12;
        cancelBtn.style.unityTextAlign = TextAnchor.MiddleCenter;
        cancelBtn.style.backgroundColor = new UnityEngine.UIElements.StyleColor(ButtonBg);
        cancelBtn.style.whiteSpace = WhiteSpace.NoWrap;
        AddButtonFlash(cancelBtn);
        buttonRow3.Add(cancelBtn);

        _attachRoot.Add(dialog);
        ChatWhisperUtil.InstallTabSwallow(_clickCatcher);
        ChatWhisperUtil.InstallTabSwallow(dialog);
        dialog.focusable = true;
        dialog.Focus();
        _openMenu = dialog;

        // Center the dialog
        dialog.schedule.Execute(() =>
        {
            if (_attachRoot == null || _attachRoot.panel == null)
            {
                CloseAllMenus();
                return;
            }
            Rect pr = _attachRoot.contentRect;
            Rect dr = dialog.contentRect;
            dialog.style.left = (pr.width - dr.width) / 2;
            dialog.style.top = (pr.height - dr.height) / 2;
        });

        dialog.schedule.Execute(() =>
        {
            if (_attachRoot == null || _attachRoot.panel == null || !IsScoreboardVisible(ui))
                CloseAllMenus();
        }).Every(100);

        // Bring overlay and dialog to the top
        _clickCatcher.BringToFront();
        dialog.BringToFront();
    }
    private static void ShowVoteKickConfirmationDialog(string title, string playerNumber, string playerName, string cleanPlayerName, string steamId)
    {
        CloseAllMenus();

        var ui = UnityEngine.Object.FindFirstObjectByType<UIScoreboard>(UnityEngine.FindObjectsInactive.Include);
        _attachRoot = ResolveAttachRoot(ui, null);
        if (_attachRoot == null || _attachRoot.panel == null) return;

        // Create overlay
        if (_clickCatcher == null)
        {
            _clickCatcher = new VisualElement();
            _clickCatcher.style.position = UnityEngine.UIElements.Position.Absolute;
            _clickCatcher.style.left = 0; _clickCatcher.style.right = 0;
            _clickCatcher.style.top = 0; _clickCatcher.style.bottom = 0;
            _clickCatcher.style.backgroundColor = new UnityEngine.UIElements.StyleColor(new Color(0, 0, 0, 0.75f));
            _clickCatcher.RegisterCallback<PointerDownEvent>(e => e.StopPropagation());
            _clickCatcher.focusable = true;
        }
        _attachRoot.Add(_clickCatcher);

        // Create confirmation dialog
        var dialog = new VisualElement();
        dialog.style.position = UnityEngine.UIElements.Position.Absolute;
        dialog.style.width = 420;
        dialog.style.height = 320;
        dialog.style.backgroundColor = new UnityEngine.UIElements.StyleColor(new Color(0.15f, 0.15f, 0.15f, 1f));
        dialog.style.paddingTop = 8;
        dialog.style.paddingBottom = 8;
        dialog.style.paddingLeft = 8;
        dialog.style.paddingRight = 8;

        // Title
        var titleLabel = new Label(title);
        MakeReadable(titleLabel);
        titleLabel.style.fontSize = 20;
        titleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
        titleLabel.style.color = new UnityEngine.UIElements.StyleColor(Color.yellow);
        titleLabel.style.marginBottom = 12;
        titleLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
        dialog.Add(titleLabel);

        // Message
        var messageLabel = new Label($"CONFIRMATION\n\n{playerNumber} - {playerName}\nSteam ID: {steamId}");
        MakeReadable(messageLabel);
        messageLabel.style.fontSize = 16;
        messageLabel.style.color = new UnityEngine.UIElements.StyleColor(P_White);
        messageLabel.style.marginBottom = 20;
        messageLabel.style.whiteSpace = WhiteSpace.Normal;
        messageLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
        dialog.Add(messageLabel);

        // Button rows
        var buttonRow1 = new VisualElement();
        buttonRow1.style.flexDirection = FlexDirection.Row;
        buttonRow1.style.justifyContent = Justify.Center;
        buttonRow1.style.marginBottom = 8;
        dialog.Add(buttonRow1);

        var buttonRow2 = new VisualElement();
        buttonRow2.style.flexDirection = FlexDirection.Row;
        buttonRow2.style.justifyContent = Justify.Center;
        buttonRow2.style.marginBottom = 8;
        dialog.Add(buttonRow2);

        var buttonRow3 = new VisualElement();
        buttonRow3.style.flexDirection = FlexDirection.Row;
        buttonRow3.style.justifyContent = Justify.Center;
        dialog.Add(buttonRow3);

        // Vote Kick by Number button
        var vkNumberBtn = new UnityEngine.UIElements.Button(() => {
            try
            {
                var kbRunner = UnityEngine.Object.FindFirstObjectByType<PoncePuck.Keybinds.KeybindRunner>();
                if (kbRunner != null)
                {
                    kbRunner.SendChatMessage($"/vk {playerNumber}");
                    LogHelper.Log($"[LocalMute] Vote kick by number initiated for {playerNumber}");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[LocalMute] Failed to send vote kick: {e}");
            }
            CloseAllMenus();
        }) { text = $"KICK NUMBER" };
        MakeReadable(vkNumberBtn);
        vkNumberBtn.style.width = 200;
        vkNumberBtn.style.height = 50;
        vkNumberBtn.style.marginRight = 8;
        vkNumberBtn.style.paddingLeft = 18;
        vkNumberBtn.style.paddingRight = 18;
        vkNumberBtn.style.paddingTop = 12;
        vkNumberBtn.style.paddingBottom = 12;
        vkNumberBtn.style.unityTextAlign = TextAnchor.MiddleCenter;
        vkNumberBtn.style.backgroundColor = new UnityEngine.UIElements.StyleColor(ButtonBg);
        vkNumberBtn.style.whiteSpace = WhiteSpace.NoWrap;
        AddButtonFlash(vkNumberBtn);
        buttonRow1.Add(vkNumberBtn);

        // Vote Kick by Name button
        var vkNameBtn = new UnityEngine.UIElements.Button(() => {
            try
            {
                var kbRunner = UnityEngine.Object.FindFirstObjectByType<PoncePuck.Keybinds.KeybindRunner>();
                if (kbRunner != null)
                {
                    kbRunner.SendChatMessage($"/vk {cleanPlayerName}");
                    LogHelper.Log($"[LocalMute] Vote kick by name initiated for {cleanPlayerName}");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[LocalMute] Failed to send vote kick: {e}");
            }
            CloseAllMenus();
        }) { text = $"KICK NAME" };
        MakeReadable(vkNameBtn);
        vkNameBtn.style.width = 200;
        vkNameBtn.style.height = 50;
        vkNameBtn.style.paddingLeft = 18;
        vkNameBtn.style.paddingRight = 18;
        vkNameBtn.style.paddingTop = 12;
        vkNameBtn.style.paddingBottom = 12;
        vkNameBtn.style.unityTextAlign = TextAnchor.MiddleCenter;
        vkNameBtn.style.backgroundColor = new UnityEngine.UIElements.StyleColor(ButtonBg);
        vkNameBtn.style.whiteSpace = WhiteSpace.NoWrap;
        AddButtonFlash(vkNameBtn);
        buttonRow1.Add(vkNameBtn);

                        // Kick by Name button
        var kickSteamBtn = new UnityEngine.UIElements.Button(() => {
            SendChatCommand($"/kicksteamid {steamId}");
            LogHelper.Log($"[LocalMute] Admin KICK by Steam ID executed for {steamId}");
            CloseAllMenus();
        }) { text = $"KICK ID" };
        MakeReadable(kickSteamBtn);
        kickSteamBtn.style.width = 200;
        kickSteamBtn.style.height = 50;
        kickSteamBtn.style.paddingLeft = 18;
        kickSteamBtn.style.paddingRight = 18;
        kickSteamBtn.style.paddingTop = 12;
        kickSteamBtn.style.paddingBottom = 12;
        kickSteamBtn.style.marginRight = 8;
        kickSteamBtn.style.unityTextAlign = TextAnchor.MiddleCenter;
        kickSteamBtn.style.backgroundColor = new UnityEngine.UIElements.StyleColor(ButtonBg);
        kickSteamBtn.style.whiteSpace = WhiteSpace.NoWrap;
        AddButtonFlash(kickSteamBtn);
        buttonRow2.Add(kickSteamBtn);

        // Copy Steam ID button
        var copySteamIdBtn = new UnityEngine.UIElements.Button(() => {
            GUIUtility.systemCopyBuffer = steamId;
            LogHelper.Log($"[LocalMute] Copied Steam ID to clipboard: {steamId}");
        }) { text = "COPY ID" };
        MakeReadable(copySteamIdBtn);
        copySteamIdBtn.style.width = 200;
        copySteamIdBtn.style.height = 50;
        copySteamIdBtn.style.paddingLeft = 18;
        copySteamIdBtn.style.paddingRight = 18;
        copySteamIdBtn.style.paddingTop = 12;
        copySteamIdBtn.style.paddingBottom = 12;
        copySteamIdBtn.style.unityTextAlign = TextAnchor.MiddleCenter;
        copySteamIdBtn.style.backgroundColor = new UnityEngine.UIElements.StyleColor(ButtonBg);
        copySteamIdBtn.style.whiteSpace = WhiteSpace.NoWrap;
        AddButtonFlash(copySteamIdBtn);
        buttonRow2.Add(copySteamIdBtn);

        // Cancel button (full width on third row)
        var cancelBtn = new UnityEngine.UIElements.Button(() => {
            CloseAllMenus();
        }) { text = "CANCEL" };
        MakeReadable(cancelBtn);
        cancelBtn.style.width = 410;
        cancelBtn.style.height = 50;
        cancelBtn.style.paddingLeft = 18;
        cancelBtn.style.paddingRight = 18;
        cancelBtn.style.paddingTop = 12;
        cancelBtn.style.paddingBottom = 12;
        cancelBtn.style.unityTextAlign = TextAnchor.MiddleCenter;
        cancelBtn.style.backgroundColor = new UnityEngine.UIElements.StyleColor(ButtonBg);
        cancelBtn.style.whiteSpace = WhiteSpace.NoWrap;
        AddButtonFlash(cancelBtn);
        buttonRow3.Add(cancelBtn);

        _attachRoot.Add(dialog);
        ChatWhisperUtil.InstallTabSwallow(_clickCatcher);
        ChatWhisperUtil.InstallTabSwallow(dialog);
        dialog.focusable = true;
        dialog.Focus();
        _openMenu = dialog;

        // Center the dialog
        dialog.schedule.Execute(() =>
        {
            if (_attachRoot == null || _attachRoot.panel == null)
            {
                CloseAllMenus();
                return;
            }
            Rect pr = _attachRoot.contentRect;
            Rect dr = dialog.contentRect;
            dialog.style.left = (pr.width - dr.width) / 2;
            dialog.style.top = (pr.height - dr.height) / 2;
        });

        dialog.schedule.Execute(() =>
        {
            if (_attachRoot == null || _attachRoot.panel == null || !IsScoreboardVisible(ui))
                CloseAllMenus();
        }).Every(100);

        // Bring overlay and dialog to the top
        _clickCatcher.BringToFront();
        dialog.BringToFront();
    }

    private static void PlaceMenuNearRow(VisualElement row, VisualElement menu, float marginY)
    {
        if (row == null || menu == null || _attachRoot == null) return;
        menu.schedule.Execute(() =>
        {
            if (_openMenu != menu)
                return;

            if (row.panel == null || _attachRoot.panel == null)
            {
                CloseAllMenus();
                return;
            }

            Rect rowWB = row.worldBound;
            if (rowWB.width <= 1f || rowWB.height <= 1f)
            {
                CloseAllMenus();
                return;
            }

            // place menu aligned to left edge of NAME column (same Y)
            Vector2 rootPos = _attachRoot.WorldToLocal(new Vector2(rowWB.xMin, rowWB.yMin));
            Rect pr = _attachRoot.contentRect;
            Rect m = menu.contentRect;

            float left = Mathf.Clamp(rootPos.x + 6f, pr.xMin, pr.xMax - m.width);
            float desiredTop = rootPos.y + marginY;
            float bottomMargin = 10f; // Minimum margin from bottom of screen
            
            // Check if menu would be cut off at the bottom
            float maxTop = pr.yMax - m.height - bottomMargin;
            float top = Mathf.Min(desiredTop, maxTop);
            
            // Also clamp to not go above the top
            top = Mathf.Max(top, pr.yMin);

            menu.style.left = left;
            menu.style.top = top;
        }).Every(50);
    }

    // Mask scoreboard VEs so pointer events go to our overlay (like your panel does).  // :contentReference[oaicite:4]{index=4}
    private static void MaskScoreboardForClicks(UIScoreboard ui, bool mask)
    {
        var root = GetScoreboardRoot(ui);
        if (root == null) return;

        if (mask)
        {
            _masked.Clear();
            foreach (var c in root.Children())
            {
                if (c == null) continue;
                if (_openMenu != null && IsUnder(c, _openMenu)) continue;
                if (_clickCatcher != null && IsUnder(c, _clickCatcher)) continue;
                if (c.pickingMode != PickingMode.Ignore)
                {
                    c.pickingMode = PickingMode.Ignore;
                    _masked.Add(c);
                }
            }
        }
        else
        {
            for (int i = 0; i < _masked.Count; i++)
            {
                var ve = _masked[i];
                if (ve != null) ve.pickingMode = PickingMode.Position;
            }
            _masked.Clear();
        }
    }

    private static bool IsUnder(VisualElement child, VisualElement ancestor)
    {
        for (var p = child; p != null; p = p.parent) if (p == ancestor) return true;
        return false;
    }

    public static void RefreshRowForSteamId(ulong sid)
    {
        try
        {
            var ui = UnityEngine.Object.FindFirstObjectByType<UIScoreboard>(UnityEngine.FindObjectsInactive.Include);
            if (ui == null) return;
            var nm = NetworkManager.Singleton; if (nm == null) return;
            foreach (var cc in nm.ConnectedClientsList)
            {
                var p = cc.PlayerObject ? cc.PlayerObject.GetComponent<Player>() : null; if (!p) continue;
                if (RosterSnapshot.GetSteamId(p) == sid)
                {
                    LocalMuteClientMod.Scoreboard_UpdatePlayerUI(ui, p);
                    break;
                }
            }
        }
        catch (Exception e) { Debug.LogError("[LocalMute] RefreshRowForSteamId error: " + e); }
    }
    public static void ApplyPlayerStyling_NameOnly(VisualElement row, Player player, bool muted, bool saved)
    {
        try
        {
            if (row == null || !player) return;

            var labels = row.Query<Label>().ToList();
            if (labels == null || labels.Count == 0) return;

            var nameLabel = labels.OrderByDescending(l => (l.text ?? "").Length).FirstOrDefault();
            if (nameLabel == null) return;

            // Clean previous styling (remove strikethrough, underline, color tags)
            string cleanText = nameLabel.text
                .Replace("<s>", "").Replace("</s>", "")
                .Replace("<u>", "").Replace("</u>", "")
                .Replace("<color=#808080>", "").Replace("</color>", "");

            // Apply styling based on status
            // Saved players always appear normal (no styling)
            if (saved)
            {
                // Saved players: no special styling, always normal
                nameLabel.text = cleanText;
                nameLabel.style.opacity = 1f;
            }
            else if (muted)
            {
                // Muted/blocked players: strikethrough + grey
                nameLabel.text = $"<color=#808080><s>{cleanText}</s></color>";
                nameLabel.style.opacity = 1f;
            }
            else
            {
                // Normal player
                nameLabel.text = cleanText;
                nameLabel.style.opacity = 1f;
            }
        }
        catch (Exception e) { Debug.LogError("[LocalMute] ApplyPlayerStyling_NameOnly error: " + e); }
    }

    // Public profile open with fallbacks (scoreboard -> Steam overlay -> web)
    public static void OpenProfile(UIScoreboard ui, Player player)
    {
        if (TryOpenProfileViaScoreboard(ui, player)) return;
        var sid = RosterSnapshot.GetSteamId(player);
        if (TryOpenSteamOverlayProfile(sid)) return;
        // Last fallback: open in Steam overlay directly (no web browser)
        if (sid != 0) 
        {
            try
            {
                Steamworks.SteamFriends.ActivateGameOverlayToUser("steamid", new Steamworks.CSteamID(sid));
            }
            catch (Exception e)
            {
                Debug.LogError($"[LocalMute] Failed to open Steam profile overlay: {e.Message}");
            }
        }
    }

    private static bool TryOpenProfileViaScoreboard(UIScoreboard ui, Player player)
    {
        try
        {
            var methods = typeof(UIScoreboard).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo best = null;
            foreach (var m in methods)
            {
                var n = m.Name.ToLowerInvariant();
                if (n.Contains("profile") || n.Contains("playercard") || n.Contains("inspect") || n.Contains("openplayer"))
                {
                    var ps = m.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType == typeof(Player)) { best = m; break; }
                    if (ps.Length == 0) best = m;
                }
            }
            if (best != null)
            {
                if (best.GetParameters().Length == 1) best.Invoke(ui, new object[] { player });
                else best.Invoke(ui, null);
                return true;
            }
        }
        catch (Exception e) { Debug.LogError("[LocalMute] TryOpenProfileViaScoreboard failed: " + e); }
        return false;
    }

    private static bool TryOpenSteamOverlayProfile(ulong steamId)
    {
        if (steamId == 0) return false;
        try
        {
            Type friendsType = null, csteamType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t1 = asm.GetType("Steamworks.SteamFriends");
                var t2 = asm.GetType("Steamworks.CSteamID");
                if (t1 != null && t2 != null) { friendsType = t1; csteamType = t2; break; }
            }
            if (friendsType == null || csteamType == null) return false;

            var ctor = csteamType.GetConstructor(new[] { typeof(ulong) });
            if (ctor == null) return false;
            var sidObj = ctor.Invoke(new object[] { steamId });

            var overlayToUser = friendsType.GetMethod("ActivateGameOverlayToUser", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), csteamType }, null);
            if (overlayToUser != null) { overlayToUser.Invoke(null, new object[] { "steamid", sidObj }); return true; }

            var overlayToWeb = friendsType.GetMethod("ActivateGameOverlayToWebPage", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
            if (overlayToWeb != null) { overlayToWeb.Invoke(null, new object[] { "https://steamcommunity.com/profiles/" + steamId }); return true; }
        }
        catch (Exception e) { Debug.LogError("[LocalMute] Steam overlay profile open failed: " + e); }
        return false;
    }
    private static bool _visibilityHooked;
    public static void EnsureVisibilityCloseHook(UIScoreboard ui)
    {
        if (ui == null || _visibilityHooked) return;
        try
        {
            // B310: UIView exposes a public Action<UIView> OnVisibility delegate (not an event).
            // Combine our handler in so we close menus whenever the scoreboard visibility flips.
            ui.OnVisibility = (Action<UIView>)Delegate.Combine(ui.OnVisibility, new Action<UIView>(_ => CloseAllMenus()));
            _visibilityHooked = true;
        }
        catch { }
    }

}
public static class PlayerTypingDetector
{
    // Track which players are currently typing or talking
    private static Dictionary<ulong, PlayerActivity> _playerActivities = new Dictionary<ulong, PlayerActivity>();
    private static Dictionary<ulong, float> _lastVoiceActivity = new Dictionary<ulong, float>();
    
    public class PlayerActivity
    {
        public string PlayerName;
        public bool IsTyping;
        public bool IsTalking;
        public float LastTypingTime;
        public float LastTalkingTime;
    }
    
    public static Dictionary<ulong, PlayerActivity> GetActiveActivities()
    {
        return _playerActivities;
    }
    
    public static void UpdateVoiceActivity(ulong steamId, string playerName)
    {
        _lastVoiceActivity[steamId] = Time.time;
        
        if (!_playerActivities.ContainsKey(steamId))
        {
            _playerActivities[steamId] = new PlayerActivity { PlayerName = playerName };
        }
        else if (string.IsNullOrEmpty(_playerActivities[steamId].PlayerName))
        {
            _playerActivities[steamId].PlayerName = playerName;
        }
        
        _playerActivities[steamId].IsTalking = true;
        _playerActivities[steamId].LastTalkingTime = Time.time;
    }
    
    private static Player[] _cachedPlayers;
    private static float _lastPlayerCacheTime = 0f;
    private const float PLAYER_CACHE_INTERVAL = 2.0f; // Cache players for 2 seconds
    
    public static void Poll()
    {
        try
        {
            float currentTime = Time.time;
            var playersToRemove = new List<ulong>();
            
            // Cache player list to avoid expensive FindObjectsByType calls every 0.5s
            if (_cachedPlayers == null || currentTime - _lastPlayerCacheTime > PLAYER_CACHE_INTERVAL)
            {
                _cachedPlayers = UnityEngine.Object.FindObjectsByType<Player>(FindObjectsSortMode.None);
                _lastPlayerCacheTime = currentTime;
            }
            
            var activePlayers = new HashSet<ulong>();
            
            // One-time diagnostic logging - DISABLED to reduce console spam
            //if (!_hasLoggedPlayerStructure && _cachedPlayers != null && _cachedPlayers.Length > 0)
            //{
            //    LogPlayerStructure(_cachedPlayers[0]);
            //    _hasLoggedPlayerStructure = true;
            //}
            
            if (_cachedPlayers != null)
            {
                foreach (var p in _cachedPlayers)
                {
                    if (p == null) continue; // Player might have disconnected
                    
                    ulong sid = RosterSnapshot.GetSteamId(p);
                    if (sid == 0) continue;
                    
                    activePlayers.Add(sid);
                    bool isTyping = IsTyping(p);
                    bool isTalking = IsTalking(p); // Check actual PlayerVoiceRecorder.IsRecording
                    string playerName = p.Username.Value.ToString();
                    
                    if (!_playerActivities.ContainsKey(sid))
                    {
                        _playerActivities[sid] = new PlayerActivity { PlayerName = playerName };
                    }
                    else if (string.IsNullOrEmpty(_playerActivities[sid].PlayerName))
                    {
                        _playerActivities[sid].PlayerName = playerName;
                    }
                    
                    // Update typing status
                    if (isTyping)
                    {
                        _playerActivities[sid].IsTyping = true;
                        _playerActivities[sid].LastTypingTime = currentTime;
                    }
                    else if (currentTime - _playerActivities[sid].LastTypingTime > 1.0f)
                    {
                        _playerActivities[sid].IsTyping = false;
                    }
                    
                    // Update talking status from direct check
                    if (isTalking)
                    {
                        _playerActivities[sid].IsTalking = true;
                        _playerActivities[sid].LastTalkingTime = currentTime;
                    }
                    else if (currentTime - _playerActivities[sid].LastTalkingTime > 0.5f)
                    {
                        _playerActivities[sid].IsTalking = false;
                    }
                }
            }
            
            // Update voice/talking status based on recent activity
            foreach (var kvp in _playerActivities)
            {
                ulong sid = kvp.Key;
                var activity = kvp.Value;
                
                // Check if voice activity is recent (within last 0.5 seconds)
                if (_lastVoiceActivity.ContainsKey(sid))
                {
                    if (currentTime - _lastVoiceActivity[sid] > 0.5f)
                    {
                        activity.IsTalking = false;
                    }
                }
                else
                {
                    activity.IsTalking = false;
                }
                
                // Remove inactive players
                if (!activity.IsTyping && !activity.IsTalking)
                {
                    if (!activePlayers.Contains(sid) || 
                        (currentTime - activity.LastTypingTime > 2.0f && currentTime - activity.LastTalkingTime > 2.0f))
                    {
                        playersToRemove.Add(sid);
                    }
                }
            }
            
            // Clean up inactive players
            foreach (var sid in playersToRemove)
            {
                _playerActivities.Remove(sid);
                _lastVoiceActivity.Remove(sid);
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[LocalMute] PlayerTypingDetector.Poll error: {e}");
        }
    }
    
    private static bool IsTyping(Player p)
    {
        // NOTE: The game doesn't network-sync typing status across players.
        // We can only detect LOCAL player typing by checking UIChat focus.
        // Remote player typing detection would require the game to add network sync.
        
        // For now, we'll return false since there's no per-player typing indicator
        // The voice detection works great though via PlayerVoiceRecorder.IsRecording!
        return false;
        
        // Alternative approach: Check if this is the local player and UIChat is focused
        // But that would only show YOUR typing, not others' typing
    }
    
    // Check if a player is talking by examining their PlayerVoiceRecorder component
    private static bool IsTalking(Player p)
    {
        try
        {
            var recorder = p.GetComponent<PlayerVoiceRecorder>();
            if (recorder != null)
            {
                // Use the actual field name from the game's source code
                return recorder.IsRecording;
            }
        }
        catch { }
        return false;
    }

    
    // Method 2: Check UIChat for active typing (alternative approach)
    private static HashSet<ulong> CheckUIChatForTyping()
    {
        var typingPlayers = new HashSet<ulong>();
        try
        {
            var chat = UnityEngine.Object.FindFirstObjectByType<UIChat>(UnityEngine.FindObjectsInactive.Include);
            if (chat == null) return typingPlayers;
            
            var chatType = chat.GetType();
            
            // Look for input field or text field that might indicate typing
            var inputField = chatType.GetField("_inputField", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? chatType.GetField("inputField", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? chatType.GetField("m_inputField", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            
            if (inputField != null)
            {
                var input = inputField.GetValue(chat);
                if (input != null)
                {
                    // Check if the chat is focused/active
                    var isFocused = false;
                    
                    // Try UIToolkit TextField
                    if (input is UnityEngine.UIElements.TextField textField)
                    {
                        isFocused = textField.focusController?.focusedElement == textField;
                    }
                    // Try legacy InputField
                    else
                    {
                        var focusedProp = input.GetType().GetProperty("isFocused", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (focusedProp != null && focusedProp.PropertyType == typeof(bool))
                            isFocused = (bool)focusedProp.GetValue(input);
                    }
                    
                    if (isFocused)
                    {
                        // The local player is typing - we'd need to get local player's Steam ID
                        // This is less useful for remote player detection but good for local
                    }
                }
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[LocalMute] CheckUIChatForTyping error: {e}");
        }
        
        return typingPlayers;
    }
    
    // Diagnostic: Log Player class structure once to help find typing-related fields
    private static void LogPlayerStructure(Player player)
    {
        try
        {
            var playerType = player.GetType();
            Debug.Log($"[PlayerTypingDetector] === Player Class Structure Analysis ===");
            Debug.Log($"[PlayerTypingDetector] Full Type Name: {playerType.FullName}");
            
            // Log all fields
            var fields = playerType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Debug.Log($"[PlayerTypingDetector] === Fields ({fields.Length}) ===");
            foreach (var field in fields)
            {
                var typeName = field.FieldType.Name;
                if (field.Name.ToLower().Contains("typ") || 
                    field.Name.ToLower().Contains("chat") || 
                    field.Name.ToLower().Contains("input") ||
                    field.Name.ToLower().Contains("voice") ||
                    field.Name.ToLower().Contains("talk"))
                {
                    Debug.Log($"[PlayerTypingDetector] *** {field.Name} : {typeName} (potential match!)");
                }
                else if (field.FieldType == typeof(bool) || typeName.Contains("NetworkVariable"))
                {
                    Debug.Log($"[PlayerTypingDetector] {field.Name} : {typeName}");
                }
            }
            
            // Log all properties
            var properties = playerType.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Debug.Log($"[PlayerTypingDetector] === Properties ({properties.Length}) ===");
            foreach (var prop in properties)
            {
                var typeName = prop.PropertyType.Name;
                if (prop.Name.ToLower().Contains("typ") || 
                    prop.Name.ToLower().Contains("chat") || 
                    prop.Name.ToLower().Contains("input") ||
                    prop.Name.ToLower().Contains("voice") ||
                    prop.Name.ToLower().Contains("talk"))
                {
                    Debug.Log($"[PlayerTypingDetector] *** {prop.Name} : {typeName} (potential match!)");
                }
                else if (prop.PropertyType == typeof(bool) || typeName.Contains("NetworkVariable"))
                {
                    Debug.Log($"[PlayerTypingDetector] {prop.Name} : {typeName}");
                }
            }
            
            Debug.Log($"[PlayerTypingDetector] === End of Player Structure ===");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[PlayerTypingDetector] LogPlayerStructure error: {e}");
        }
    }
}
#endregion




