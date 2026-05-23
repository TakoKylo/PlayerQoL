using UnityEngine;
using UnityEngine.Networking;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace PoncePuck.Keybinds
{
    internal static class PingSoundRuntime
    {
        static GameObject _host;
        static AudioSource _src;
        static AudioClip _clip;
        static float _nextOkAt;
        static bool _isLoading = false;
        static bool _customSoundLoaded = false;
        static string _lastLoadedSound = "";

        const float CooldownSeconds = 0.25f;

        static void Init()
        {
            if (_host != null) return;
            _host = new GameObject("PPKB_PingHost");
            Object.DontDestroyOnLoad(_host);
            _src = _host.AddComponent<AudioSource>();
            _src.playOnAwake = false;
            _src.loop = false;
            _src.spatialBlend = 0f; // 2D sound (not 3D spatial)
            _src.volume = 1f;
            
            // Create procedural sound immediately as fallback
            CreateProceduralSound();
            
            // Load initial custom sound (will replace procedural if successful)
            ReloadSound();
            
            Debug.Log("[PingSoundRuntime] Initialized ping sound system");
        }

        // Public method to preload sound during mod initialization
        public static void PreloadSound()
        {
            Init();
        }

        static void CreateProceduralSound()
        {
            _clip = AudioClip.Create("ppkb_ping", 4000, 1, 8000, false);
            // simple click-ish tone
            var data = new float[4000];
            for (int i = 0; i < data.Length; i++) data[i] = (i < 120) ? (1f - i / 120f) : 0f;
            _clip.SetData(data, 0);
            _customSoundLoaded = false;
            _lastLoadedSound = "";
        }

        // Reload sound based on config
        public static void ReloadSound()
        {
            var runner = UnityEngine.Object.FindFirstObjectByType<KeybindRunner>();
            var cmd = runner?.CommandConfig;
            
            // Use default sound if config not available yet
            string selectedSound = cmd?.selectedMentionSound ?? "MentionPingDefault.mp3";
            
            ReloadSoundInternal(selectedSound);
        }
        
        // Reload sound with specific config (used during initialization)
        public static void ReloadSound(CommandKeybindConfig config)
        {
            if (config == null)
            {
                Debug.Log("[PingSoundRuntime] ReloadSound called with null config, using default");
                ReloadSound();
                return;
            }
            
            string selectedSound = config.selectedMentionSound ?? "MentionPingDefault.mp3";
            Debug.Log($"[PingSoundRuntime] ReloadSound called with config - Selected: {selectedSound}, LastLoaded: {_lastLoadedSound}, CustomLoaded: {_customSoundLoaded}");
            ReloadSoundInternal(selectedSound);
        }
        
        private static void ReloadSoundInternal(string selectedSound)
        {
            // Check if we need to reload (different sound selected)
            if (_customSoundLoaded && _lastLoadedSound == selectedSound && _clip != null)
            {
                // Already loaded the correct sound
                Debug.Log($"[PingSoundRuntime] Sound '{selectedSound}' already loaded, skipping reload");
                return;
            }
            
            Debug.Log($"[PingSoundRuntime] Starting reload for sound: {selectedSound}");
            
            // Ensure we have a fallback sound while loading
            if (_clip == null)
            {
                CreateProceduralSound();
            }
            
            // Reset loading flag to allow new load (cancel any in-progress load)
            _isLoading = false;
            
            // Try to load custom sound from PingSounds folder (will replace procedural sound when complete)
            if (_host != null)
            {
                var loader = _host.GetComponent<PingSoundLoader>();
                if (loader == null) loader = _host.AddComponent<PingSoundLoader>();
                loader.StartCoroutine(LoadCustomSound(selectedSound));
            }
        }

        static IEnumerator LoadCustomSound(string filename)
        {
            // Prevent concurrent loads
            if (_isLoading)
            {
                Debug.Log("[PingSoundRuntime] Load already in progress, skipping");
                yield break;
            }
            _isLoading = true;
            
            // Get the Plugins/PoncePlayerInput directory (where the DLL is located)
            string dllPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
            string modDir = Path.GetDirectoryName(dllPath);
            string soundsDir = Path.Combine(modDir, "PingSounds");
            string soundPath = Path.Combine(soundsDir, filename);
            
            Debug.Log($"[PingSoundRuntime] Loading ping sound: {soundPath}");
            
            if (!File.Exists(soundPath))
            {
                Debug.LogWarning($"[PingSoundRuntime] Sound file not found: {soundPath}, using procedural sound");
                CreateProceduralSound();
                _isLoading = false;
                yield break;
            }
            
            // Determine audio type from extension
            AudioType audioType = AudioType.MPEG;
            string ext = Path.GetExtension(soundPath).ToLower();
            if (ext == ".wav") audioType = AudioType.WAV;
            else if (ext == ".ogg") audioType = AudioType.OGGVORBIS;
            
            // Load the audio file using UnityWebRequest
            string fileUri = "file:///" + soundPath.Replace("\\", "/");
            using (UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip(fileUri, audioType))
            {
                yield return www.SendWebRequest();
                
                if (www.result == UnityWebRequest.Result.Success)
                {
                    _clip = DownloadHandlerAudioClip.GetContent(www);
                    if (_clip != null)
                    {
                        _clip.name = "custom_ping_" + filename;
                        _customSoundLoaded = true;
                        _lastLoadedSound = filename;
                        Debug.Log($"[PingSoundRuntime] Successfully loaded: {filename}");
                    }
                    else
                    {
                        Debug.LogWarning($"[PingSoundRuntime] Failed to extract audio from: {soundPath}");
                        CreateProceduralSound();
                    }
                }
                else
                {
                    Debug.LogWarning($"[PingSoundRuntime] Failed to load sound: {www.error}");
                    CreateProceduralSound();
                }
            }
            
            _isLoading = false;
        }

        // Get list of available sound files in PingSounds folder
        public static List<string> GetAvailableSounds()
        {
            var sounds = new List<string>();
            
            try
            {
                string dllPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                string modDir = Path.GetDirectoryName(dllPath);
                string soundsDir = Path.Combine(modDir, "PingSounds");
                
                if (Directory.Exists(soundsDir))
                {
                    var files = Directory.GetFiles(soundsDir, "*.*")
                        .Where(f => f.EndsWith(".mp3", System.StringComparison.OrdinalIgnoreCase) ||
                                   f.EndsWith(".wav", System.StringComparison.OrdinalIgnoreCase) ||
                                   f.EndsWith(".ogg", System.StringComparison.OrdinalIgnoreCase))
                        .Select(f => Path.GetFileName(f))
                        .OrderBy(f => f)
                        .ToList();
                    
                    sounds.AddRange(files);
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[PingSoundRuntime] Error scanning sounds folder: {e}");
            }
            
            // Always have at least one option
            if (sounds.Count == 0)
            {
                sounds.Add("MentionPingDefault.mp3");
            }
            
            return sounds;
        }

        public static void TryPlay()
        {
            Init();
            
            var runner = UnityEngine.Object.FindFirstObjectByType<KeybindRunner>();
            var cmd = runner?.CommandConfig;
            if (cmd == null) return;
            
            // Check if sound is enabled in config
            if (!cmd.mentionSoundEnabled) return;
            
            // Reinitialize if objects were destroyed
            if (_host == null || _src == null)
            {
                _host = null;
                _src = null;
                Init();
            }
            
            if (_src == null || _clip == null) return;
            if (Time.unscaledTime < _nextOkAt) return;
            _nextOkAt = Time.unscaledTime + CooldownSeconds;
            
            // Apply volume from config
            _src.volume = Mathf.Clamp01(cmd.mentionSoundVolume);
            
            try 
            { 
                _src.PlayOneShot(_clip);
                Debug.Log($"[PingSoundRuntime] Played mention sound (volume: {_src.volume})");
            } 
            catch (System.Exception e)
            {
                Debug.LogError($"[PingSoundRuntime] Failed to play sound: {e.Message}");
            }
        }
        
        // Helper MonoBehaviour to run coroutines
        private class PingSoundLoader : MonoBehaviour { }
    }
}
