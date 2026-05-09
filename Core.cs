using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Unity.Netcode;
using UnityEngine;
using UITK = UnityEngine.UIElements;

namespace PoncePuck.Keybinds
{
    public sealed partial class KeybindRunner : MonoBehaviour
    {
        // --- paths (new ModHub/PlayerQoL structure)
        private static string GameDir  => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        private static string ConfigDir => Path.Combine(GameDir, "config");
        private static string ModHubDir => Path.Combine(ConfigDir, "ModHub");
        private static string PlayerQoLDir => Path.Combine(ModHubDir, "PlayerQoL");
        
        // Current config paths (ModHub/PlayerQoL)
        private static string CmdPath    => Path.Combine(PlayerQoLDir, "PonceKeybinds.Commands.json");
        private static string LocalMutePath => Path.Combine(PlayerQoLDir, "PonceLocalMute.json");
        private static string SoundsConfigPath => Path.Combine(GameDir, "oomtm450_sounds_clientconfig.json");
        
        // Legacy paths (config/playerinput) for migration
        private static string LegacyPlayerInputDir => Path.Combine(ConfigDir, "playerinput");
        private static string LegacyCmdPath    => Path.Combine(LegacyPlayerInputDir, "PonceKeybinds.Commands.json");
        private static string LegacyLocalMutePath => Path.Combine(LegacyPlayerInputDir, "PonceLocalMute.json");
        
        // Old paths for very old migration (direct in config/)
        private static string OldCmdPath    => Path.Combine(ConfigDir, "PonceKeybinds.Commands.json");
        private static string OldLocalMutePath => Path.Combine(ConfigDir, "PonceLocalMute.json");
        private static string OldPositionSettingsPath => Path.Combine(ConfigDir, "PoncePosition.Settings.json");


        // configs
        private CommandKeybindConfig _cmd   = new CommandKeybindConfig();
        
        // Public accessors
        public CommandKeybindConfig CommandConfig => _cmd;

        // Static helper for conditional debug logging
        public static void DebugLog(string message)
        {
            var runner = UnityEngine.Object.FindFirstObjectByType<KeybindRunner>();
            if (runner?._cmd?.enableDebugLogging == true)
            {
                Debug.Log(message);
            }
        }

        public static void DebugLogWarning(string message)
        {
            var runner = UnityEngine.Object.FindFirstObjectByType<KeybindRunner>();
            if (runner?._cmd?.enableDebugLogging == true)
            {
                Debug.LogWarning(message);
            }
        }

        public static void DebugLogError(string message)
        {
            var runner = UnityEngine.Object.FindFirstObjectByType<KeybindRunner>();
            if (runner?._cmd?.enableDebugLogging == true)
            {
                Debug.LogError(message);
            }
        }

        public static void EnsureConfigs()
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);
                Directory.CreateDirectory(ModHubDir);
                Directory.CreateDirectory(PlayerQoLDir);
                
                // Migrate old configs to new ModHub/PlayerQoL location
                MigrateConfigsToModHub();

                if (!File.Exists(CmdPath))
                {
                    var c = new CommandKeybindConfig();
                    c.extraCommands.Add("/vs:J");
                    c.extraCommands.Add("/vw:K");
                    c.extraCommands.Add("/s:G");
                    c.prefills.Add("/w |U");
                    c.prefills.Add("/r |R");
                    // new fields already have sane defaults
                    AtomicWrite(CmdPath, JsonUtility.ToJson(c, true));
                    Debug.Log("[PPKB] Created default command config: " + CmdPath);
                }

            }
            catch (Exception e) { Debug.LogException(e); }
        }
        
        private static void MigrateConfigsToModHub()
        {
            try
            {
                // Migrate from config/playerinput (legacy) to ModHub/PlayerQoL
                if (File.Exists(LegacyCmdPath) && !File.Exists(CmdPath))
                {
                    File.Copy(LegacyCmdPath, CmdPath);
                    Debug.Log($"[PPKB] Migrated commands config to {PlayerQoLDir}");
                }
                
                if (File.Exists(LegacyLocalMutePath) && !File.Exists(LocalMutePath))
                {
                    File.Copy(LegacyLocalMutePath, LocalMutePath);
                    Debug.Log($"[PPKB] Migrated local mute config to {PlayerQoLDir}");
                }
                
                // Also check very old paths (direct in config/)
                if (File.Exists(OldCmdPath) && !File.Exists(CmdPath))
                {
                    File.Copy(OldCmdPath, CmdPath);
                    Debug.Log($"[PPKB] Migrated commands config from old location to {PlayerQoLDir}");
                }
                
                if (File.Exists(OldLocalMutePath) && !File.Exists(LocalMutePath))
                {
                    File.Copy(OldLocalMutePath, LocalMutePath);
                    Debug.Log($"[PPKB] Migrated local mute config from old location to {PlayerQoLDir}");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PPKB] Failed to migrate configs: {e.Message}");
            }
        }

        private static void AtomicWrite(string path, string content)
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, content);
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        private void Awake()
        {
            Debug.Log($"[PPKB] Using config: {CmdPath}");
            
            // Run centralized ModHub config migration
            try
            {
                PonceMods.Shared.ModHubConfig.MigrateOldConfigs();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PPKB] ModHub config migration failed: {e.Message}");
            }
            
            EnsureConfigs(); // Create default configs if they don't exist
            LoadConfigs();   // Then load the configs
            
            // Reload ping sound after config is loaded to use saved selection
            PoncePuck.Keybinds.PingSoundRuntime.ReloadSound(_cmd);
            
            RebuildLookups();
            ResetInputActions();
            ApplyGameInputRebindings();
            SendHelloRoleAware();
            RefreshSuppression();
            EnsurePositionSelectHook();
            TryHookNavMenus();
            RepairUINavigateIfBroken();
        }

        private void LoadConfigs()
        {
            try
            {
                if (File.Exists(CmdPath))     _cmd    = JsonUtility.FromJson<CommandKeybindConfig>(File.ReadAllText(CmdPath)) ?? new CommandKeybindConfig();
                if (_cmd == null)    _cmd    = new CommandKeybindConfig();

                if (_cmd.prefills == null)
                {
                    _cmd.prefills = new List<string>();
                }
                else
                {
                    for (int i = 0; i < _cmd.prefills.Count; i++)
                    {
                        var prefill = _cmd.prefills[i];
                        if (!string.IsNullOrEmpty(prefill) && prefill.StartsWith("/whisper ", StringComparison.OrdinalIgnoreCase))
                        {
                            _cmd.prefills[i] = "/w " + prefill.Substring("/whisper ".Length);
                        }
                    }
                }
                
                // Parse position settings from raw strings
                _cmd.ParsePositionSettings();
                Debug.Log($"[PPKB] Loaded {_cmd.positionSettings.Count} position settings from {_cmd.positionSettingsRaw?.Count ?? 0} raw entries");

                // Migrate old position settings file if it exists
                if (File.Exists(OldPositionSettingsPath))
                {
                    try
                    {
                        var oldSettings = JsonUtility.FromJson<PositionSettingsConfig>(File.ReadAllText(OldPositionSettingsPath));
                        if (oldSettings?.entries != null && oldSettings.entries.Count > 0 && _cmd.positionSettings.Count == 0)
                        {
                            _cmd.positionSettings.AddRange(oldSettings.entries);
                            Debug.Log($"[PPKB] Migrated {oldSettings.entries.Count} position settings from old file");
                        }
                        File.Delete(OldPositionSettingsPath);
                        Debug.Log("[PPKB] Deleted old position settings file");
                    }
                    catch { }
                }
            }
            catch (Exception e) { Debug.LogException(e); }
        }

        private void SaveConfigsAndRefresh()
        {
            try
            {
                Directory.CreateDirectory(ModHubDir);
                Directory.CreateDirectory(PlayerQoLDir);

                // Ensure positionSettings list is not null before serialization
                if (_cmd.positionSettings == null)
                    _cmd.positionSettings = new List<PositionSettingEntry>();
                
                // Serialize position settings to raw strings for JSON
                _cmd.SerializePositionSettings();
                
                // Debug: log position settings before save
                Debug.Log($"[PPKB] Saving {_cmd.positionSettings.Count} position settings as {_cmd.positionSettingsRaw.Count} raw strings");
                foreach (var raw in _cmd.positionSettingsRaw)
                {
                    Debug.Log($"[PPKB]   - {raw}");
                }
                
                string cmdJson = JsonUtility.ToJson(_cmd, true);
                Debug.Log($"[PPKB] Commands JSON to write ({cmdJson.Length} chars)");
                AtomicWrite(CmdPath, cmdJson);
                
                if (_cmd.enableDebugLogging)
                {
                    Debug.Log($"[PPKB] Saved configs - Selected sound: {_cmd.selectedMentionSound}, Enabled: {_cmd.mentionSoundEnabled}, Volume: {_cmd.mentionSoundVolume}");
                }
                RebuildLookups();
                ResetInputActions();
                ApplyGameInputRebindings();
                SendHelloRoleAware();
                RefreshSuppression();
                EnsurePositionSelectHook();
                
                // Re-apply position settings if currently playing (in case settings were modified)
                ReapplyCurrentPositionSettings();
            }
            catch (Exception e) { Debug.LogException(e); }
        }

        // Public API for external mods
        public static void AddPlayerToFavorites(string steamId, string playerName, string profileUrl)
        {
            PoncePuck.LocalMute.LocalMuteStore.AddToSaved(steamId, playerName, profileUrl);
            Debug.Log($"[PPKB] Added {playerName} to favorites (SteamID: {steamId})");
        }

        public static void AddPlayerToBlocked(string steamId, string playerName, string profileUrl)
        {
            PoncePuck.LocalMute.LocalMuteStore.AddToBlocked(steamId, playerName, profileUrl);
            Debug.Log($"[PPKB] Added {playerName} to blocked list (SteamID: {steamId})");
        }

        public static bool IsPlayerFavorited(string steamId)
        {
            return PoncePuck.LocalMute.LocalMuteStore.IsSaved(steamId);
        }

        public static bool IsPlayerBlocked(string steamId)
        {
            return PoncePuck.LocalMute.LocalMuteStore.IsBlocked(steamId);
        }

        // Public method to check if admin mode is enabled
        public bool IsAdminEnabled()
        {
            return _cmd.enableAdminSettings;
        }

        // Public method to send chat messages (used by admin menu)
        public void SendChatMessage(string message)
        {
            TrySendChat(message);
        }


        // Apply arena disable state (destroy arena objects and update config)
        public void ApplyArenaDisableState(bool disable)
        {
            try
            {
                Debug.Log($"[PPKB] ApplyArenaDisableState called: {(disable ? "DISABLE" : "ENABLE")} arena");

                // Update the SceneryLoaderConfig.local.json file
                string sceneryConfigPath = Path.Combine(ConfigDir, "SceneryLoader", "SceneryLoaderConfig.local.json");
                if (File.Exists(sceneryConfigPath))
                {
                    try
                    {
                        string jsonContent = File.ReadAllText(sceneryConfigPath);
                        
                        // Use string replacement to preserve all fields in the JSON
                        // Find and replace the useSceneLocally value
                        if (jsonContent.Contains("\"useSceneLocally\""))
                        {
                            // Replace true/false with the new value
                            string pattern = "\"useSceneLocally\"\\s*:\\s*(true|false)";
                            string replacement = $"\"useSceneLocally\": {(!disable).ToString().ToLower()}";
                            jsonContent = System.Text.RegularExpressions.Regex.Replace(jsonContent, pattern, replacement);
                            
                            File.WriteAllText(sceneryConfigPath, jsonContent);
                            Debug.Log($"[PPKB] Updated SceneryLoaderConfig.json: useSceneLocally = {!disable}");
                        }
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"[PPKB] Failed to update SceneryLoaderConfig.json: {e}");
                    }
                }

                // Find and destroy/restore all arena objects immediately (mid-game support)
                if (disable)
                {
                    // Destroy arena objects - search all GameObjects for arena-related objects
                    var allObjects = UnityEngine.Object.FindObjectsByType<GameObject>(UnityEngine.FindObjectsSortMode.None);
                    
                    var arenaObjects = allObjects
                        .Where(go => {
                            // Must have visual components to be worth destroying
                            if (go.GetComponentsInChildren<Renderer>(true).Length == 0 && 
                                go.GetComponentsInChildren<MeshFilter>(true).Length == 0)
                                return false;
                                
                            // Check for arena-related names
                            string name = go.name.ToLower();
                            return name.Contains("hockeyarena") || 
                                   name.Contains("outdoorhockey") ||
                                   name.Contains("scenery") ||
                                   name.Contains("arena") && (name.Contains("root") || name.Contains("(clone)"));
                        })
                        .ToArray();

                    int destroyedCount = 0;
                    foreach (var arenaObj in arenaObjects)
                    {
                        Debug.Log($"[PPKB] Destroying arena object: {arenaObj.name} (scene: {arenaObj.scene.name})");
                        UnityEngine.Object.Destroy(arenaObj);
                        destroyedCount++;
                    }
                    
                    if (destroyedCount > 0)
                    {
                        Debug.Log($"[PPKB] Arena disabled: destroyed {destroyedCount} objects");
                    }
                    else
                    {
                        Debug.Log("[PPKB] Arena disabled: no arena objects found (may not be loaded yet)");
                    }
                    
                    // Also restore base game skybox when disabling full arena
                    if (_originalSkybox == null && RenderSettings.skybox != null)
                    {
                        _originalSkybox = RenderSettings.skybox;
                    }
                    
                    // Try to find and restore base game skybox
                    if (_baseGameSkybox == null)
                    {
                        var allMaterials = Resources.FindObjectsOfTypeAll<Material>();
                        foreach (var mat in allMaterials)
                        {
                            if (mat.name.Contains("Skybox") || mat.shader?.name == "Skybox/Procedural" || mat.shader?.name == "Skybox/6 Sided")
                            {
                                if (mat != _originalSkybox)
                                {
                                    _baseGameSkybox = mat;
                                    break;
                                }
                            }
                        }
                    }
                    
                    RenderSettings.skybox = _baseGameSkybox;
                    DynamicGI.UpdateEnvironment();
                    Debug.Log($"[PPKB] Restored base game skybox{(_baseGameSkybox != null ? "" : " (not found - cleared skybox)")}");
                }
                else
                {
                    // Re-enabling: user needs to rejoin server or manually reload
                    Debug.Log("[PPKB] Arena enabled - will load on next server join");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[PPKB] ApplyArenaDisableState failed: {e}");
            }
        }

        // Apply partial arena disable (specific parts only)
        public void ApplyArenaPartialDisable()
        {
            try
            {
                // Find the arena root - check multiple possible names
                var arenaRoot = UnityEngine.Object.FindObjectsByType<GameObject>(UnityEngine.FindObjectsSortMode.None)
                    .FirstOrDefault(go => go.name == "HockeyArenaRoot" ||  // Indoor arena (exact match first)
                                          go.name.Contains("OutdoorHockey") || 
                                          go.name.Contains("HockeyArena"));

                if (arenaRoot == null)
                {
                    Debug.Log("[PPKB] Arena root not found - arena may not be loaded yet");
                    return;
                }
                
                Debug.Log($"[PPKB] Arena found: '{arenaRoot.name}', applying settings now");

                int affectedCount = 0;

                // Disable/Enable Props/3D Assets (Meshes/Renderers)
                var renderers = arenaRoot.GetComponentsInChildren<Renderer>(true);
                foreach (var renderer in renderers)
                {
                    renderer.enabled = !_cmd.disableArenaProps;
                }
                if (renderers.Length > 0)
                {
                    Debug.Log($"[PPKB] {(_cmd.disableArenaProps ? "Disabled" : "Enabled")} {renderers.Length} props/renderers");
                    affectedCount += renderers.Length;
                }

                // Disable/Enable Lights
                var lights = arenaRoot.GetComponentsInChildren<Light>(true);
                foreach (var light in lights)
                {
                    light.enabled = !_cmd.disableArenaLights;
                }
                if (lights.Length > 0)
                {
                    Debug.Log($"[PPKB] {(_cmd.disableArenaLights ? "Disabled" : "Enabled")} {lights.Length} lights");
                    affectedCount += lights.Length;
                }

                // Disable/Enable Skybox
                if (_cmd.disableArenaSkybox)
                {
                    // Store arena skybox before disabling
                    if (_originalSkybox == null && RenderSettings.skybox != null)
                    {
                        _originalSkybox = RenderSettings.skybox;
                    }
                    
                    // Try to find and restore base game skybox
                    if (_baseGameSkybox == null)
                    {
                        // Look for base game skybox by searching for common Unity default skybox names
                        var allMaterials = Resources.FindObjectsOfTypeAll<Material>();
                        foreach (var mat in allMaterials)
                        {
                            if (mat.name.Contains("Skybox") || mat.shader?.name == "Skybox/Procedural" || mat.shader?.name == "Skybox/6 Sided")
                            {
                                if (mat != _originalSkybox) // Make sure it's not the arena skybox
                                {
                                    _baseGameSkybox = mat;
                                    break;
                                }
                            }
                        }
                    }
                    
                    // Restore base game skybox or set to null if not found
                    RenderSettings.skybox = _baseGameSkybox;
                    DynamicGI.UpdateEnvironment();
                    Debug.Log($"[PPKB] Disabled custom skybox{(_baseGameSkybox != null ? " and restored base game skybox" : "")}");
                    affectedCount++;
                }
                else
                {
                    // Restore arena custom skybox
                    if (_originalSkybox != null)
                    {
                        RenderSettings.skybox = _originalSkybox;
                        DynamicGI.UpdateEnvironment();
                        Debug.Log("[PPKB] Restored custom skybox");
                    }
                }

                // Disable/Enable Particles
                var particleSystems = arenaRoot.GetComponentsInChildren<ParticleSystem>(true);
                foreach (var ps in particleSystems)
                {
                    if (_cmd.disableArenaParticles)
                    {
                        if (ps.isPlaying)
                        {
                            ps.Stop();
                            ps.Clear();
                        }
                        var emission = ps.emission;
                        emission.enabled = false;
                    }
                    else
                    {
                        var emission = ps.emission;
                        emission.enabled = true;
                        if (!ps.isPlaying)
                        {
                            ps.Play();
                        }
                    }
                }
                if (particleSystems.Length > 0)
                {
                    Debug.Log($"[PPKB] {(_cmd.disableArenaParticles ? "Disabled" : "Enabled")} {particleSystems.Length} particle systems");
                    affectedCount += particleSystems.Length;
                }

                // Adjust Audio Volume - find ALL scene audio sources (excluding player/UI)
                int audioAffected = 0;
                
                // Find all AudioSources in scene, filter out player/voice/UI related
                var allAudioSources = UnityEngine.Object.FindObjectsByType<AudioSource>(UnityEngine.FindObjectsSortMode.None)
                    .Where(a => {
                        var name = a.gameObject.name.ToLower();
                        var parentName = a.transform.parent?.name.ToLower() ?? "";
                        // Exclude player-related audio (voice chat, footsteps, etc)
                        return !name.Contains("player") && 
                               !name.Contains("voice") &&
                               !name.Contains("puck") &&  // Puck hit sounds
                               !parentName.Contains("player") &&
                               !parentName.Contains("voice");
                    })
                    .ToArray();
                
                foreach (var audioSource in allAudioSources)
                {
                    // Store original volume in a tracker component if not already stored
                    if (audioSource.gameObject.GetComponent<AudioVolumeTracker>() == null)
                    {
                        var tracker = audioSource.gameObject.AddComponent<AudioVolumeTracker>();
                        tracker.originalVolume = audioSource.volume;
                    }
                    
                    var volTracker = audioSource.gameObject.GetComponent<AudioVolumeTracker>();
                    audioSource.volume = volTracker.originalVolume * _cmd.arenaAudioVolume;
                    audioAffected++;
                }
                
                if (audioAffected > 0)
                {
                    Debug.Log($"[PPKB] Set volume for {audioAffected} audio sources to {(int)(_cmd.arenaAudioVolume * 100)}%");
                    affectedCount += audioAffected;
                }
                else
                {
                    Debug.Log("[PPKB] No arena audio sources found to adjust");
                }

                if (affectedCount > 0)
                {
                    Debug.Log($"[PPKB] Arena partial disable complete: affected {affectedCount} components");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[PPKB] ApplyArenaPartialDisable failed: {e}");
            }
        }

        // Store original skybox materials
        private static Material _originalSkybox;  // Arena custom skybox
        private static Material _baseGameSkybox;  // Base game default skybox

        // Helper component to track original audio volumes
        private class AudioVolumeTracker : MonoBehaviour
        {
            public float originalVolume = 1.0f;
        }

        // Wrapper classes for SceneryLoaderConfig.json
        [Serializable]
        private class SceneryLoaderConfigWrapper
        { 
            public SceneInformation sceneInformation = new SceneInformation();
        }

        [Serializable]
        private class SceneInformation
        {
            public string bundleName = string.Empty;
            public string prefabName = string.Empty;
            public string skyboxName = string.Empty;
            public bool useSceneLocally = false;
            public string contentKey64 = string.Empty;
        }

        // Sounds mod config structure
        [Serializable]
        public class SoundsModConfig
        {
            public bool LogInfo = true;
            public bool Music = true;
            public float MusicVolume = 0.5f;
            public bool CustomGoalHorns = true;
            public bool WarmupMusic = false;
        }

        // Read sounds config from the mod's JSON file
        public static SoundsModConfig LoadSoundsConfig()
        {
            try
            {
                if (File.Exists(SoundsConfigPath))
                {
                    string json = File.ReadAllText(SoundsConfigPath);
                    return JsonUtility.FromJson<SoundsModConfig>(json) ?? new SoundsModConfig();
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[PPKB] Failed to load sounds config: {e}");
            }
            return new SoundsModConfig();
        }

        // Write sounds config to the mod's JSON file and trigger reload
        public static void SaveSoundsConfig(SoundsModConfig config)
        {
            try
            {
                string json = JsonUtility.ToJson(config, true);
                File.WriteAllText(SoundsConfigPath, json);
                Debug.Log("[PPKB] Saved sounds config");
                
                // Try to reload the sounds mod config via reflection
                TryReloadSoundsModConfig();
            }
            catch (Exception e)
            {
                Debug.LogError($"[PPKB] Failed to save sounds config: {e}");
            }
        }
        
        // Try to reload the sounds mod's config at runtime via reflection
        private static void TryReloadSoundsModConfig()
        {
            try
            {
                // Find the sounds mod assembly
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        // Find the Sounds class (not Main - the class is called Sounds)
                        var soundsType = asm.GetType("oomtm450PuckMod_Sounds.Sounds");
                        if (soundsType != null)
                        {
                            // Get the ClientConfig static field
                            var clientConfigField = soundsType.GetField("ClientConfig", 
                                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                            
                            if (clientConfigField != null)
                            {
                                // Get old config values before updating
                                var oldConfig = clientConfigField.GetValue(null);
                                var clientConfigType = asm.GetType("oomtm450PuckMod_Sounds.Configs.ClientConfig");
                                bool oldWarmupMusic = true;
                                if (oldConfig != null && clientConfigType != null)
                                {
                                    var warmupProp = clientConfigType.GetProperty("WarmupMusic");
                                    if (warmupProp != null)
                                        oldWarmupMusic = (bool)warmupProp.GetValue(oldConfig);
                                }
                                
                                // Read new config from file
                                var readConfigMethod = clientConfigType?.GetMethod("ReadConfig", 
                                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                                
                                if (readConfigMethod != null)
                                {
                                    var newConfig = readConfigMethod.Invoke(null, null);
                                    
                                    // Update the static ClientConfig field
                                    clientConfigField.SetValue(null, newConfig);
                                    Debug.Log($"[PPKB] Updated Sounds.ClientConfig: {newConfig}");
                                    
                                    // Get values from new config
                                    var musicVolProp = clientConfigType.GetProperty("MusicVolume");
                                    var warmupMusicProp = clientConfigType.GetProperty("WarmupMusic");
                                    float musicVol = musicVolProp != null ? (float)musicVolProp.GetValue(newConfig) : 0.8f;
                                    bool newWarmupMusic = warmupMusicProp != null ? (bool)warmupMusicProp.GetValue(newConfig) : true;
                                    
                                    // Find _soundsSystem field
                                    var soundsSystemField = soundsType.GetField("_soundsSystem", 
                                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                                    
                                    // Find _currentMusicPlaying field
                                    var currentMusicField = soundsType.GetField("_currentMusicPlaying",
                                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                                    
                                    if (soundsSystemField != null)
                                    {
                                        var soundsSystem = soundsSystemField.GetValue(null);
                                        if (soundsSystem != null)
                                        {
                                            var soundsSystemType = soundsSystem.GetType();
                                            
                                            // Apply volume change
                                            var changeMusicVolMethod = soundsSystemType.GetMethod("ChangeMusicVolume",
                                                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                                            
                                            if (changeMusicVolMethod != null)
                                            {
                                                changeMusicVolMethod.Invoke(soundsSystem, new object[] { musicVol });
                                                Debug.Log($"[PPKB] Applied music volume: {musicVol}");
                                            }
                                            
                                            // Handle warmup toggle change
                                            if (oldWarmupMusic != newWarmupMusic && currentMusicField != null)
                                            {
                                                string currentMusic = (string)currentMusicField.GetValue(null);
                                                
                                                // Check if currently playing warmup music
                                                var warmupListProp = soundsSystemType.GetProperty("WarmupMusicList",
                                                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                                                
                                                if (warmupListProp != null)
                                                {
                                                    var warmupList = warmupListProp.GetValue(soundsSystem);
                                                    var containsMethod = warmupList?.GetType().GetMethod("Contains");
                                                    bool isWarmupMusic = containsMethod != null && 
                                                        !string.IsNullOrEmpty(currentMusic) && 
                                                        (bool)containsMethod.Invoke(warmupList, new object[] { currentMusic });
                                                    
                                                    if (isWarmupMusic)
                                                    {
                                                        if (oldWarmupMusic && !newWarmupMusic)
                                                        {
                                                            // Warmup was on, now off - stop the music
                                                            var stopAllMethod = soundsSystemType.GetMethod("StopAll",
                                                                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                                                            stopAllMethod?.Invoke(soundsSystem, null);
                                                            Debug.Log("[PPKB] Stopped warmup music (toggle turned off)");
                                                        }
                                                        else if (!oldWarmupMusic && newWarmupMusic)
                                                        {
                                                            // Warmup was off, now on - start the music
                                                            var playMethod = soundsSystemType.GetMethod("Play",
                                                                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public,
                                                                null,
                                                                new Type[] { typeof(string), typeof(string), typeof(float), typeof(bool) },
                                                                null);
                                                            playMethod?.Invoke(soundsSystem, new object[] { currentMusic, "Music", 0f, true });
                                                            Debug.Log("[PPKB] Started warmup music (toggle turned on)");
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                        else
                                        {
                                            Debug.Log("[PPKB] _soundsSystem is null (no music playing?)");
                                        }
                                    }
                                }
                                return;
                            }
                        }
                    }
                    catch { /* ignore assembly errors */ }
                }
                Debug.Log("[PPKB] Sounds mod not found for config reload - changes will apply on next game restart");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PPKB] Could not reload sounds mod config: {e.Message}");
            }
        }
    }
}