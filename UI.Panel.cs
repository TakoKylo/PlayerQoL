// Panel build + menu buttons + styling + tabs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem.UI;
using UITK = UnityEngine.UIElements;
using UnityEngine.UIElements; // This brings in the 'Q' extension method for VisualElement
using UnityEngine.InputSystem;
using System.Drawing.Printing;
using Steamworks;

namespace PoncePuck.Keybinds
{
    public sealed partial class KeybindRunner
    {
        public static bool AdminModeEnabled => _instance?._cmd?.enableAdminSettings ?? false;
        public static KeybindRunner Instance => _instance;
        private static KeybindRunner _instance;
        
        public KeybindRunner()
        {
            _instance = this;
        }
        // UI Toolkit bits
    private UITK.VisualElement _ppkbPanel;
        private UITK.VisualElement _ppkbBackdrop;
        // Menu buttons now handled by ModMenuHub
        private UITK.VisualElement _lastRoot;
        private UITK.VisualElement _cmdRowsRoot;
        private UITK.VisualElement _prefRowsRoot;
        private UITK.VisualElement _tabBar;
        private UITK.Button _tabActions, _tabCommands, _tabSocial, _tabSettings;
        private UITK.VisualElement _actionsSectionVE, _commandsSectionVE, _socialSectionVE, _settingsSectionVE;

        private UITK.UIDocument _doc;
        private UITK.Button _ppkbCloseButton;

        private bool _buttonsWiredForThisRoot;
        private bool _clientEventsHooked;
        private float _nextPositionCheck;
        private DateTime _lastSoundsConfigWriteTime;

        // cursor state for panel
        private bool _savedCursorState = false;
        private bool _prevMouseActive = false;
        private CursorLockMode _prevLockState = CursorLockMode.None;
        private bool _prevCursorVisible = false;

        // tabs
        private enum PpkbTab { Positions, Commands, Social, Settings }
        private PpkbTab _activeTab = PpkbTab.Positions;

        // Add a field to track button click
        private bool _ppkbCloseButtonClicked = false;

        // rows/controls collections (commands UI declares models)
        private readonly List<UITK.VisualElement> _maskedPickables = new List<UITK.VisualElement>();
        private UITK.VisualElement _blockScope;
        private readonly List<UITK.VisualElement> _hiddenMenuButtons = new List<UITK.VisualElement>();

        // Root we attach to (so we know which siblings to mask)

        // ===== UI palette =====
        private static readonly Color32 TextFieldBg = new Color32(57, 57, 57, 255);
        private static readonly Color32 RowBg = new Color32(61, 61, 61, 255);
        private static readonly Color32 ButtonBg = new Color32(57, 57, 57, 255);
        private static readonly Color32 PanelBg = new Color32(48, 48, 47, 255);
        private static readonly Color32 TabActiveBg = new Color32(80, 80, 80, 255);
        private static readonly Color32 TabInactiveBg = new Color32(66, 66, 66, 255);
        private static readonly Color P_White = new Color(0.93f, 0.93f, 0.93f, 1f);
        private static readonly Color BtnBrightGray = (Color)RowBg;
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
        private static void ForceUIFont(UITK.VisualElement ve)
        {
            var f = GetUIFont(); if (f == null) return;
            ve.style.unityFont = f;
        }
        private static void MakeReadable(UITK.TextField tf)
        {
            tf.style.color = Color.white;
            tf.style.unityFont = GetUIFont();

            // make the field itself the swatch #3 color
            tf.style.backgroundColor = new UITK.StyleColor(TextFieldBg);

            var input = tf.childCount > 0 ? tf.ElementAt(0) : null;
            if (input != null)
            {
                input.style.color = Color.white;
                input.style.unityFont = GetUIFont();
                input.style.backgroundColor = new UITK.StyleColor(TextFieldBg);
            }
        }
        private static void MakeReadable(UITK.Label l)
        {
            l.style.color = Color.white;
            l.style.unityFont = GetUIFont();
        }
        private static void MakeReadable(UITK.Button b)
        {
            b.style.color = Color.white;
            b.style.unityFont = GetUIFont();
        }

        private static void StyleSlider(UITK.Slider slider)
        {
            // Style the slider track to be more visible
            var tracker = slider.Q(className: "unity-slider__tracker");
            if (tracker != null)
            {
                tracker.style.backgroundColor = new UITK.StyleColor(new Color(0.3f, 0.3f, 0.3f, 1f));
                tracker.style.height = 6;
                tracker.style.borderTopLeftRadius = 3;
                tracker.style.borderTopRightRadius = 3;
                tracker.style.borderBottomLeftRadius = 3;
                tracker.style.borderBottomRightRadius = 3;
            }

            // Style the dragger (thumb)
            var dragger = slider.Q(className: "unity-slider__dragger");
            if (dragger != null)
            {
                dragger.style.width = 16;
                dragger.style.height = 16;
                dragger.style.backgroundColor = new UITK.StyleColor(new Color(0.8f, 0.8f, 0.8f, 1f));
                dragger.style.borderTopLeftRadius = 8;
                dragger.style.borderTopRightRadius = 8;
                dragger.style.borderBottomLeftRadius = 8;
                dragger.style.borderBottomRightRadius = 8;
            }

            // Style the fill/progress
            var draggerBorder = slider.Q(className: "unity-slider__dragger-border");
            if (draggerBorder != null)
            {
                draggerBorder.style.backgroundColor = new UITK.StyleColor(new Color(0.4f, 0.6f, 0.9f, 1f));
            }
        }

    private void OnDestroy() 
    { 
        // Restore any overridden settings before destroying (in case exiting game with position-specific settings)
        try
        {
            RestoreOverriddenSettings();
        }
        catch { }
        
        // Clean up UI elements before destroying
        try
        {
            if (_ppkbPanel != null)
            {
                _ppkbPanel.RemoveFromHierarchy();
                _ppkbPanel = null;
            }
            if (_ppkbBackdrop != null)
            {
                _ppkbBackdrop.RemoveFromHierarchy();
                _ppkbBackdrop = null;
            }
            if (_captureOverlay != null)
            {
                _captureOverlay.RemoveFromHierarchy();
                _captureOverlay = null;
            }
            // Unregister from ModMenuHub and cleanup if we're the owner
            try
            {
                PonceMods.Shared.ModMenuHub.UnregisterMod("PlayerInput");
                PonceMods.Shared.ModMenuHub.Cleanup("PlayerInput");
            }
            catch { }
            
            // Reset flags so it can be re-initialized
            _buttonsWiredForThisRoot = false;
            _ppkbCloseButton = null;
            _ppkbCloseButtonClicked = false;
        }
        catch { }
        
        // Clean up event listeners
        try
        {
            UnhookPositionChangeEvents();
            UnhookClientEvents();
        }
        catch { }
        
        ClearInputActions(); 
        HookKeyboardEvent(false); 
        HideBackgroundMenuButtons(false); 
    }

        private void Update()
        {
            // Hook events once EventManager is available
            if (!_clientEventsHooked)
            {
                HookClientEvents();
            }
            
            // Handle ESC key to fully close panel
            // Check visibility using resolved style for more accurate detection
            bool isPanelVisible = _ppkbPanel != null && 
                (_ppkbPanel.style.display == UITK.DisplayStyle.Flex || 
                 _ppkbPanel.resolvedStyle.display == UITK.DisplayStyle.Flex);
            
            if (isPanelVisible && !_isCapturing)
            {
                var kb = Keyboard.current;
                if (kb != null && kb.escapeKey.wasPressedThisFrame)
                {
                    Debug.Log("[PPKB] ESC pressed while panel visible - closing");
                    FullClosePPKBPanel();
                    return;
                }
            }
            // Close panel via our close button flag
            if (_ppkbCloseButtonClicked)
            {
                _ppkbCloseButtonClicked = false;
                if (_isCapturing) 
                { 
                    CancelChordCapture(); 
                }
                else 
                { 
                    ClosePPKBPanel(); 
                }
            }
            
            // Fallback position check every 0.5 seconds (events may not always fire)
            // Reduced from 3s to 0.5s for more reliable position detection
            if (Time.unscaledTime >= _nextPositionCheck)
            {
                _nextPositionCheck = Time.unscaledTime + 0.5f;
                RefreshSuppression();
            }
        }
        
        // UpdateButtonVisibility is no longer needed since ModMenuHub handles the menu button
        
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
            try
            {
                // Initialize on connect
                SendHelloRoleAware();
                RefreshSuppression();
                EnsurePositionSelectHook();
                TryHookNavMenus();
                ForceCleanupUI();
                
                // Apply arena disable state on server join (with delay for arena to load)
                StartCoroutine(ApplyArenaVisualsDelayed());
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PPKB] OnClientConnected error: {ex.Message}");
            }
        }
        
        private void OnClientDisconnected(Dictionary<string, object> message)
        {
            try
            {
                // Restore any overridden settings when disconnecting from server
                Debug.Log("[PPKB] Client disconnected - restoring base settings");
                RestoreOverriddenSettings();
                _lastAppliedPosition = HockeyPosition.None;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PPKB] OnClientDisconnected cleanup error: {ex.Message}");
            }
        }

        private System.Collections.IEnumerator ApplyArenaVisualsDelayed()
        {
            // Wait for arena to load with retry logic
            int maxRetries = 3;
            int retryCount = 0;
            float delayBetweenRetries = 3f;
            
            while (retryCount < maxRetries)
            {
                yield return new WaitForSeconds(delayBetweenRetries);
                
                // Check if arena exists before applying
                var arenaRoot = UnityEngine.Object.FindObjectsByType<GameObject>(UnityEngine.FindObjectsSortMode.None)
                    .FirstOrDefault(go => go.name.Contains("OutdoorHockey") || go.name.Contains("HockeyArena"));
                
                if (arenaRoot != null)
                {
                    Debug.Log("[PPKB] Arena found, applying settings now");
                    
                    // Apply the current disable state
                    if (_cmd.disableArenaVisuals)
                    {
                        ApplyArenaDisableState(true);
                    }
                    else
                    {
                        // Only apply partial settings when NOT fully disabling
                        // (because full disable destroys the objects)
                        ApplyArenaPartialDisable();
                    }
                    break;
                }
                else
                {
                    retryCount++;
                    Debug.Log($"[PPKB] Arena not found yet, retry {retryCount}/{maxRetries}");
                }
            }
            
            if (retryCount >= maxRetries)
            {
                Debug.LogWarning("[PPKB] Arena never loaded after all retries");
            }
        }

        private void DestroyArenaObjectsIfDisabled()
        {
            try
            {
                // Find all potential arena objects with more specific criteria
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
                    UnityEngine.Object.Destroy(arenaObj);
                    destroyedCount++;
                }
                
                if (destroyedCount > 0)
                {
                    Debug.Log($"[PPKB] Arena check: destroyed {destroyedCount} arena objects");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[PPKB] DestroyArenaObjectsIfDisabled failed: {e}");
            }
        }

        private void CheckAndReloadSoundsConfig()
        {
            try
            {
                string soundsConfigPath = Path.Combine(Path.GetFullPath(Path.Combine(Application.dataPath, "..")), "config", "oomtm450_sounds_clientconfig.json");
                
                if (!System.IO.File.Exists(soundsConfigPath))
                    return;

                // Get the last write time of the config file
                var currentWriteTime = System.IO.File.GetLastWriteTime(soundsConfigPath);

                // If this is the first check, just store the time and return
                if (_lastSoundsConfigWriteTime == default(DateTime))
                {
                    _lastSoundsConfigWriteTime = currentWriteTime;
                    return;
                }

                // If the file has been modified externally, reload the UI
                if (currentWriteTime != _lastSoundsConfigWriteTime)
                {
                    _lastSoundsConfigWriteTime = currentWriteTime;
                    
                    // Reload the config
                    var soundsConfig = LoadSoundsConfig();
                    
                    // Update our local config cache
                    _cmd.musicvol = soundsConfig.MusicVolume;
                    _cmd.warmupmusic = soundsConfig.WarmupMusic;
                    
                    // Save the updated local config using AtomicWrite
                    string gameDir = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
                    string configDir = Path.Combine(gameDir, "config");
                    string modHubDir = Path.Combine(configDir, "ModHub");
                    string playerQoLDir = Path.Combine(modHubDir, "PlayerQoL");
                    string cmdPath = Path.Combine(playerQoLDir, "PonceKeybinds.Commands.json");
                    Directory.CreateDirectory(modHubDir);
                    Directory.CreateDirectory(playerQoLDir);
                    AtomicWrite(cmdPath, JsonUtility.ToJson(_cmd, true));
                    
                    // Update the UI sliders if the panel is open
                    if (_ppkbPanel != null && _ppkbPanel.style.display == DisplayStyle.Flex)
                    {
                        var settingsPanel = _ppkbPanel.Q("settings-panel");
                        if (settingsPanel != null)
                        {
                            // Update SOUNDS VOLUME slider
                            var soundsVolumeSlider = settingsPanel.Q<Slider>("sounds-volume-slider");
                            if (soundsVolumeSlider != null)
                            {
                                soundsVolumeSlider.value = soundsConfig.MusicVolume;
                            }

                            // Update WARMUP MUSIC toggle
                            var warmupMusicToggle = settingsPanel.Q<Toggle>("warmup-music-toggle");
                            if (warmupMusicToggle != null)
                            {
                                warmupMusicToggle.value = soundsConfig.WarmupMusic;
                            }
                        }
                    }

                    Debug.Log($"[PPKB] Sounds config externally modified, updated local config and UI");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[PPKB] CheckAndReloadSoundsConfig failed: {e}");
            }
        }

        private void TryHookNavMenus()
        {
            try
            {
                var uiMgr = UnityEngine.Object.FindFirstObjectByType<UIManager>(UnityEngine.FindObjectsInactive.Include);
                _doc = uiMgr != null ? uiMgr.UIDocument : UnityEngine.Object.FindFirstObjectByType<UITK.UIDocument>(UnityEngine.FindObjectsInactive.Include);
                var root = _doc != null ? _doc.rootVisualElement : null;
                if (root == null) return;

                if (_lastRoot != root)
                {
                    _lastRoot = root;
                    _buttonsWiredForThisRoot = false;

                    // rebuild overlay on this root
                    _ppkbBackdrop?.RemoveFromHierarchy();
                    _ppkbPanel?.RemoveFromHierarchy();
                    _ppkbBackdrop = null;
                    _ppkbPanel = null;
                }

                if (!_buttonsWiredForThisRoot) TryWireButtonsOnce(root);
                if (_ppkbPanel == null) BuildPPKBPanel(root);
            }
            catch (Exception e) { Debug.LogException(e); }
        }

        private static readonly FieldInfo _fiMainSettings =
            typeof(UIMainMenu).GetField("settingsButton", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo _fiPauseSettings =
            typeof(UIPauseMenu).GetField("settingsButton", BindingFlags.Instance | BindingFlags.NonPublic);

        private static void CopyClasses(UITK.VisualElement from, UITK.VisualElement to)
        {
            if (from == null || to == null) return;
            foreach (var cls in from.GetClasses()) to.AddToClassList(cls);
        }
        private static UITK.Button MakeSiblingButton(UITK.Button reference, string text, Action onClick)
        {
            var b = new UITK.Button(onClick) { text = text };
            CopyClasses(reference, b);
            b.name = text.Replace(" ", "_") + "_PPKB";
            b.pickingMode = UITK.PickingMode.Position;

            b.style.backgroundColor = new UITK.StyleColor(ButtonBg);
            b.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleLeft);
            b.style.width = reference.style.width;
            b.style.minWidth = reference.style.minWidth;
            b.style.maxWidth = reference.style.maxWidth;
            b.style.height = reference.style.height;
            b.style.minHeight = reference.style.minHeight;
            b.style.maxHeight = reference.style.maxHeight;
            b.style.marginTop = 8f;
            b.style.paddingTop = 8f;
            b.style.paddingBottom = 8f;
            b.style.paddingLeft = 15f;
            AddButtonFlash(b);
            return b;
        }

        private void TryWireButtonsOnce(UITK.VisualElement root)
        {
            if (root == null || _buttonsWiredForThisRoot) return;

            // Register with ModMenuHub instead of creating our own buttons
            PonceMods.Shared.ModMenuHub.RegisterMod(
                "PlayerInput",
                "PLAYER QOL",
                OpenPPKBPanel,
                10 // Priority - lower = higher in list
            );
            
            // Initialize ModMenuHub (first mod to call this becomes owner)
            PonceMods.Shared.ModMenuHub.Initialize("PlayerInput");
            if (_ppkbPanel == null) 
            {
                BuildPPKBPanel(root);
            }
            else
            {
                // Panel already exists, just ensure it's attached to the correct root
                var attachRoot = GetAttachRoot(root);
                if (_ppkbBackdrop != null && _ppkbBackdrop.parent != attachRoot)
                {
                    _ppkbBackdrop.RemoveFromHierarchy();
                    attachRoot.Add(_ppkbBackdrop);
                }
                if (_ppkbPanel.parent != attachRoot)
                {
                    _ppkbPanel.RemoveFromHierarchy();
                    attachRoot.Add(_ppkbPanel);
                }
            }

            RefreshPanelCommandFields();

            // Mark as wired once we've registered with ModMenuHub
            _buttonsWiredForThisRoot = true;
            Debug.Log("[PPKB] Registered with ModMenuHub.");
        }



        private UITK.VisualElement GetAttachRoot(UITK.VisualElement fallbackRoot)
        {
            try
            {
                var main = MonoBehaviourSingleton<UIManager>.Instance?.MainMenu;
                if (main != null && _fiMainSettings != null)
                {
                    var sb = _fiMainSettings.GetValue(main) as UITK.Button;
                    var cont = sb?.parent?.parent;
                    if (cont != null) return cont;
                }
                var pause = MonoBehaviourSingleton<UIManager>.Instance?.PauseMenu;
                if (pause != null && _fiPauseSettings != null)
                {
                    var sb = _fiPauseSettings.GetValue(pause) as UITK.Button;
                    var cont = sb?.parent?.parent;
                    if (cont != null) return cont;
                }
            }
            catch { }
            return (fallbackRoot as UITK.VisualElement) ?? _doc?.rootVisualElement ?? (_lastRoot as UITK.VisualElement);
        }

        private UITK.VisualElement ChooseAttachRoot()
        {
            var docRoot = _doc?.rootVisualElement ?? _lastRoot;
            var pause = MonoBehaviourSingleton<UIManager>.Instance?.PauseMenu;
            if (pause != null && pause.isActiveAndEnabled) return docRoot; // attach to document root while paused
            return GetAttachRoot(docRoot);
        }

        public void OpenPPKBPanel()
        {
            // Prevent opening panel if mod is disabled
            if (!PoncePuck_Keybinds_ClientMod.IsModEnabled)
            {
                Debug.Log("[PPKB] Cannot open panel - mod is disabled");
                return;
            }
            
            if (_ppkbPanel == null) { TryHookNavMenus(); if (_ppkbPanel == null) return; }

            var ui = MonoBehaviourSingleton<UIManager>.Instance;
            if (ui != null)
            {
                _prevMouseActive = GlobalStateManager.UIState.IsMouseRequired;
                _prevLockState = UnityEngine.Cursor.lockState;
                _prevCursorVisible = UnityEngine.Cursor.visible;
                _savedCursorState = true;
                var uiState = GlobalStateManager.UIState;
                uiState.IsMouseRequired = true;
                GlobalStateManager.UIState = uiState;
            }
            UnityEngine.Cursor.lockState = CursorLockMode.None;
            UnityEngine.Cursor.visible = true;

            var attachRoot = ChooseAttachRoot();
            _blockScope = attachRoot;

            if (_ppkbBackdrop.parent != attachRoot) attachRoot.Add(_ppkbBackdrop);
            if (_ppkbPanel.parent != attachRoot) attachRoot.Add(_ppkbPanel);
            _ppkbBackdrop.RemoveFromHierarchy(); attachRoot.Add(_ppkbBackdrop);
            _ppkbPanel.RemoveFromHierarchy(); attachRoot.Add(_ppkbPanel);

            EnsureCaptureOverlay(attachRoot);

            _ppkbBackdrop.style.display = UITK.DisplayStyle.Flex;
            _ppkbPanel.style.display = UITK.DisplayStyle.Flex;

            MaskSiblingsForClicks(attachRoot, true);
            _ppkbCloseButton.style.display = UITK.DisplayStyle.Flex;

            // Menu buttons are already hidden by hub, no need to hide them again

            // Refresh the social panel when opening to show latest data
            RefreshPanelFields();
        }

        public void ClosePPKBPanel()
        {
            if (_ppkbPanel == null) return;
            
            // Check if panel is already closed - don't re-open hub if so
            bool wasVisible = _ppkbPanel.style.display == UITK.DisplayStyle.Flex;

            _ppkbBackdrop.style.display = UITK.DisplayStyle.None;
            _ppkbPanel.style.display = UITK.DisplayStyle.None;

            var root = _blockScope ?? GetAttachRoot(_doc?.rootVisualElement ?? _lastRoot);
            MaskSiblingsForClicks(root, false);
            
            // Only return to hub if the panel was actually visible
            if (!wasVisible)
            {
                _blockScope = null;
                return;
            }
            
            // Don't restore menu buttons or cursor state - hub will manage them
            Debug.Log("[PPKB] Panel closed, returning to hub");
            
            // Return to ModMenuHub
            try
            {
                PonceMods.Shared.ModMenuHub.OpenPanel();
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[PPKB] Failed to open hub: {e}");
                // Fallback: restore state manually
                HideBackgroundMenuButtons(false);
                if (_savedCursorState)
                {
                    var uiState = GlobalStateManager.UIState;
                    uiState.IsMouseRequired = _prevMouseActive;
                    GlobalStateManager.UIState = uiState;
                    UnityEngine.Cursor.lockState = _prevLockState;
                    UnityEngine.Cursor.visible = _prevCursorVisible;
                    _savedCursorState = false;
                    ForceCleanupUI();
                }
            }

            _blockScope = null;
        }
        
        /// <summary>
        /// Fully close panel without returning to hub (ESC behavior).
        /// </summary>
        public void FullClosePPKBPanel()
        {
            if (_ppkbPanel == null) return;
            
            // If capturing, cancel capture first
            if (_isCapturing)
            {
                CancelChordCapture();
                return;
            }

            _ppkbBackdrop.style.display = UITK.DisplayStyle.None;
            _ppkbPanel.style.display = UITK.DisplayStyle.None;

            var root = _blockScope ?? GetAttachRoot(_doc?.rootVisualElement ?? _lastRoot);
            MaskSiblingsForClicks(root, false);
            _blockScope = null;
            
            Debug.Log("[PPKB] Panel fully closed via ESC");
            
            // Use ModMenuHub's FullClose to handle cursor and menu buttons properly
            try
            {
                PonceMods.Shared.ModMenuHub.FullClose();
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[PPKB] Failed to full close: {e}");
                // Fallback: restore state manually
                HideBackgroundMenuButtons(false);
                if (_savedCursorState)
                {
                    var uiState2 = GlobalStateManager.UIState;
                    uiState2.IsMouseRequired = _prevMouseActive;
                    GlobalStateManager.UIState = uiState2;
                    UnityEngine.Cursor.lockState = _prevLockState;
                    UnityEngine.Cursor.visible = _prevCursorVisible;
                    _savedCursorState = false;
                    ForceCleanupUI();
                }
            }
        }

        private void MaskSiblingsForClicks(UITK.VisualElement root, bool mask)
        {
            if (root == null) return;

            if (mask)
            {
                _maskedPickables.Clear();
                foreach (var ve in root.Children())
                {
                    if (ve == null) continue;
                    if ((_ppkbPanel != null && IsUnder(ve, _ppkbPanel)) ||
                        (_ppkbBackdrop != null && IsUnder(ve, _ppkbBackdrop)) ||
                        (_captureOverlay != null && IsUnder(ve, _captureOverlay)))
                        continue;

                    if (ve.pickingMode != UITK.PickingMode.Ignore)
                    {
                        ve.pickingMode = UITK.PickingMode.Ignore;
                        _maskedPickables.Add(ve);
                    }
                }
            }
            else
            {
                for (int i = 0; i < _maskedPickables.Count; i++)
                {
                    var ve = _maskedPickables[i];
                    if (ve != null) ve.pickingMode = UITK.PickingMode.Position;
                }
                _maskedPickables.Clear();
            }
        }

        /// <summary>
        /// Check if UISettings or other overlay menus are open that should hide our menu buttons
        /// </summary>
        private bool IsOtherMenuOpen()
        {
            try
            {
                // Check if the game's Settings panel is visible
                var uiSettings = MonoBehaviourSingleton<UISettings>.Instance;
                if (uiSettings != null && uiSettings.IsVisible) return true;
                
                // Check for settings container (fast - single element lookup)
                var root = _doc?.rootVisualElement ?? _lastRoot;
                if (root != null)
                {
                    var settingsContainer = root.Q("SettingsContainer");
                    if (settingsContainer != null && settingsContainer.style.display == UITK.DisplayStyle.Flex)
                        return true;
                        
                    // Check for DashFall mod menu specifically (fast - single element lookup)
                    var dashFallPanel = root.Q("DashFallPanel");
                    if (dashFallPanel != null && dashFallPanel.style.display == UITK.DisplayStyle.Flex)
                        return true;
                }
            }
            catch { }
            return false;
        }
        
        private static bool IsUnder(UITK.VisualElement child, UITK.VisualElement ancestor)
        {
            for (var p = child; p != null; p = p.parent) if (p == ancestor) return true;
            return false;
        }

        private void HideBackgroundMenuButtons(bool hide)
        {
            var root = _blockScope ?? GetAttachRoot(_doc?.rootVisualElement ?? _lastRoot);
            if (root == null) return;

            if (hide)
            {
                _hiddenMenuButtons.Clear();
                foreach (var b in root.Query<UITK.Button>().ToList())
                {
                    if (b == null) continue;
                    // Don't hide buttons that are part of our panel UI (but DO hide when capture overlay is showing)
                    if ((_ppkbPanel != null && IsUnder(b, _ppkbPanel)) ||
                        (_ppkbBackdrop != null && IsUnder(b, _ppkbBackdrop)))
                        continue;

                    // Hide all other buttons, including our "PLAYER QOL" menu buttons
                    if (b.resolvedStyle.display != UITK.DisplayStyle.None)
                    {
                        _hiddenMenuButtons.Add(b);
                        b.style.display = UITK.DisplayStyle.None;
                    }
                }
            }
            else
            {
                for (int i = 0; i < _hiddenMenuButtons.Count; i++)
                {
                    var b = _hiddenMenuButtons[i];
                    if (b != null) b.style.display = UITK.DisplayStyle.Flex;
                }
                _hiddenMenuButtons.Clear();
            }
        }

        private void BuildPPKBPanel(UITK.VisualElement root)
        {
            try
            {
                // Clean up any existing panel first to avoid duplicate event handlers
                if (_ppkbPanel != null)
                {
                    _ppkbPanel.RemoveFromHierarchy();
                    _ppkbPanel = null;
                }
                if (_ppkbBackdrop != null)
                {
                    _ppkbBackdrop.RemoveFromHierarchy();
                    _ppkbBackdrop = null;
                }
                if (_ppkbCloseButton != null)
                {
                    _ppkbCloseButton = null;
                }
                
                // Create full-screen backdrop for click-to-close
                _ppkbBackdrop = new UITK.VisualElement { name = "PPKB_Backdrop" };
                _ppkbBackdrop.style.position = UITK.Position.Absolute;
                _ppkbBackdrop.style.left = 0;
                _ppkbBackdrop.style.top = 0;
                _ppkbBackdrop.style.right = 0;
                _ppkbBackdrop.style.bottom = 0;
                _ppkbBackdrop.style.backgroundColor = new UITK.StyleColor(new Color(0, 0, 0, 0f)); // Transparent
                _ppkbBackdrop.style.display = UITK.DisplayStyle.None;
                _ppkbBackdrop.pickingMode = UITK.PickingMode.Position;
                _ppkbBackdrop.RegisterCallback<UITK.PointerUpEvent>(_ => ClosePPKBPanel());

                _ppkbPanel = new UITK.VisualElement { name = "PPKB_Panel" };
                _ppkbPanel.style.position = UITK.Position.Absolute;
                _ppkbPanel.style.left = new UITK.Length(50, UITK.LengthUnit.Percent);
                _ppkbPanel.style.top = new UITK.Length(50, UITK.LengthUnit.Percent);
                _ppkbPanel.style.translate = new UITK.Translate(
                    new UITK.Length(-50, UITK.LengthUnit.Percent),
                    new UITK.Length(-50, UITK.LengthUnit.Percent), 0f);
                int targetW = Mathf.Clamp(Mathf.RoundToInt(Screen.width * 0.58f), 680, 980);
                _ppkbPanel.style.width = targetW;
                _ppkbPanel.style.height = new UITK.Length(84, UITK.LengthUnit.Percent);
                _ppkbPanel.style.minHeight = new UITK.Length(56, UITK.LengthUnit.Percent);
                _ppkbPanel.style.maxHeight = new UITK.Length(56, UITK.LengthUnit.Percent);
                _ppkbPanel.style.overflow = UITK.Overflow.Hidden;
                _ppkbPanel.style.flexDirection = UITK.FlexDirection.Column;
                
                // Apply saved panel color from config (HSV values)
                Color panelColor = Color.HSVToRGB(_cmd._panelHueSlider, _cmd._panelSaturationSlider, _cmd._panelBrightnessSlider);
                panelColor.a = Mathf.Clamp01(_cmd.panelAlpha);
                _ppkbPanel.style.backgroundColor = new UITK.StyleColor(panelColor);
                
                _ppkbPanel.style.paddingLeft = 8; _ppkbPanel.style.paddingRight = 8;
                _ppkbPanel.style.paddingTop = 8; _ppkbPanel.style.paddingBottom = 8;
                _ppkbPanel.style.display = UITK.DisplayStyle.None;

                var bigTitle = new UITK.Label("PLAYER QOL");
                bigTitle.style.unityFontStyleAndWeight = FontStyle.Normal;
                bigTitle.style.fontSize = 50;
                bigTitle.style.color = Color.white;
                bigTitle.style.marginBottom = 8;
                _ppkbPanel.Add(bigTitle);

                _tabBar = new UITK.VisualElement();
                _tabBar.style.flexDirection = UITK.FlexDirection.Row; _tabBar.style.marginBottom = 8;
                _ppkbPanel.Add(_tabBar);
                ForceUIFont(_ppkbPanel);

                UITK.Button MakeTab(string text, Action onClick)
                {
                    var b = new UITK.Button(onClick) { text = text.ToUpperInvariant() };
                    b.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
                    b.style.height = 50;
                    b.style.flexGrow = 1;
                    b.style.paddingLeft = 8;
                    b.style.paddingRight = 8;
                    b.style.marginRight = 8;
                    b.style.marginBottom = 26;
                    b.style.fontSize = 24;
                    b.style.borderTopLeftRadius = 6;
                    b.style.borderTopRightRadius = 6;
                    b.style.borderBottomLeftRadius = 0;
                    b.style.borderBottomRightRadius = 0;
                    b.style.borderBottomWidth = 0;
                    b.style.borderBottomColor = new UITK.StyleColor(Color.white);
                    b.style.backgroundColor = new UITK.StyleColor(TabInactiveBg);
                    b.style.color = new Color(0.7f, 0.7f, 0.7f);
                    ForceUIFont(b);
                    return b;
                }
                _tabActions = MakeTab("POSITIONS", () => ShowTab(PpkbTab.Positions));
                _tabSocial = MakeTab("PLAYERS", () => ShowTab(PpkbTab.Social));
                _tabCommands = MakeTab("COMMANDS", () => ShowTab(PpkbTab.Commands));
                _tabSettings = MakeTab("SETTINGS", () => ShowTab(PpkbTab.Settings));
                _tabBar.Add(_tabActions); _tabBar.Add(_tabCommands); _tabBar.Add(_tabSocial); _tabBar.Add(_tabSettings);
                AddTabHover(_tabActions, () => _activeTab == PpkbTab.Positions);
                AddTabHover(_tabSocial, () => _activeTab == PpkbTab.Social);
                AddTabHover(_tabCommands, () => _activeTab == PpkbTab.Commands);
                AddTabHover(_tabSettings, () => _activeTab == PpkbTab.Settings);

                var scroll = new UITK.ScrollView
                {
                    verticalScrollerVisibility = UITK.ScrollerVisibility.Auto,
                    horizontalScrollerVisibility = UITK.ScrollerVisibility.Hidden
                };
                scroll.style.flexGrow = 1;
                _ppkbPanel.Add(scroll);

                _actionsSectionVE = new UITK.VisualElement();
                _socialSectionVE = new UITK.VisualElement();
                _commandsSectionVE = new UITK.VisualElement();
                _settingsSectionVE = new UITK.VisualElement();
                scroll.Add(_actionsSectionVE);
                scroll.Add(_socialSectionVE);
                scroll.Add(_commandsSectionVE);
                scroll.Add(_settingsSectionVE);

                var row = new UITK.VisualElement();
                row.style.flexDirection = UITK.FlexDirection.Row;
                row.style.justifyContent = UITK.Justify.SpaceBetween;
                row.style.marginTop = 8;

                UITK.Button MakeDonateButton(string t, Action onClick)
                {
                    var b = new UITK.Button(onClick) { text = t.ToUpperInvariant() };
                    b.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
                    b.style.height = 50;
                    b.style.marginBottom = 8;
                    b.style.paddingLeft = 18; b.style.paddingRight = 18;
                    b.style.backgroundColor = new UITK.StyleColor(BtnBrightGray);
                    AddButtonFlash(b);
                    return b;
                }
                UITK.Button MakeResetButton(string t, Action onClick)
                {
                    var b = new UITK.Button(onClick) { text = t.ToUpperInvariant() };
                    b.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
                    b.style.height = 50;
                    b.style.marginBottom = 22;
                    b.style.paddingLeft = 18; b.style.paddingRight = 18;
                    b.style.marginLeft = 8;
                    b.style.backgroundColor = BtnBrightGray;
                    AddButtonFlash(b);
                    return b;
                }
                UITK.Button MakeCloseButton(string t, Action onClick)
                {
                    var b = new UITK.Button(onClick) { text = t.ToUpperInvariant() };
                    b.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
                    b.style.height = 50;
                    b.style.marginBottom = 22;
                    b.style.paddingLeft = 18; b.style.paddingRight = 182;
                    b.style.marginLeft = 8;
                    b.style.backgroundColor = BtnBrightGray;
                    AddButtonFlash(b);
                    return b;
                }

                var donate = MakeDonateButton("COFFEE?", () =>
                {
                    Application.OpenURL("https://buymeacoffee.com/amikiir");
                });

                var reset = MakeResetButton("RESET TO DEFAULT", () =>
                {
                    ResetToDefaultsUI();
                    SaveConfigsAndRefresh();
                    RefreshPanelFields();
                });

                var close = MakeCloseButton("CLOSE", () =>
                {
                    // Prevent double-clicks and ensure we're not already closing
                    if (_ppkbCloseButtonClicked || _ppkbPanel == null || _ppkbPanel.style.display == UITK.DisplayStyle.None)
                        return;
                        
                    _ppkbCloseButtonClicked = true;
                    
                    // Try to apply settings
                    bool applied = TryApplyFromPanel();
                    if (applied)
                    {
                        SaveConfigsAndRefresh();
                    }
                    
                    // Always close the panel regardless of apply result
                    ClosePPKBPanel();
                });
                _ppkbCloseButton = close;

                // Donate button on left, Reset and Close on right
                row.Add(donate);
                var rightButtons = new UITK.VisualElement();
                rightButtons.style.flexDirection = UITK.FlexDirection.Row;
                rightButtons.Add(reset);
                rightButtons.Add(close);
                row.Add(rightButtons);
                
                _ppkbPanel.Add(row);
                
                // Prevent clicks on panel from closing via backdrop
                _ppkbPanel.RegisterCallback<UITK.PointerUpEvent>(e => e.StopPropagation());

                var attachRoot = GetAttachRoot(root);
                attachRoot.Add(_ppkbBackdrop);
                attachRoot.Add(_ppkbPanel);
                EnsureCaptureOverlay(attachRoot);
                _ppkbBackdrop.BringToFront();
                _ppkbPanel.BringToFront();

                RefreshPanelFields();
                ShowTab(PpkbTab.Settings);
            }
            catch (Exception ex)
            {
                Debug.LogError("[PPKB] BuildPPKBPanel failed: " + ex);
            }
        }

        private void ShowTab(PpkbTab t)
        {
            if (t == PpkbTab.Settings) ShowSettingsTab();
            _activeTab = t;

            _actionsSectionVE.style.display = (t == PpkbTab.Positions) ? UITK.DisplayStyle.Flex : UITK.DisplayStyle.None;
            _socialSectionVE.style.display = (t == PpkbTab.Social) ? UITK.DisplayStyle.Flex : UITK.DisplayStyle.None;
            _commandsSectionVE.style.display = (t == PpkbTab.Commands) ? UITK.DisplayStyle.Flex : UITK.DisplayStyle.None;
            _settingsSectionVE.style.display = (t == PpkbTab.Settings) ? UITK.DisplayStyle.Flex : UITK.DisplayStyle.None;

            if (_tabActions != null) SetTabVisual(_tabActions, _activeTab == PpkbTab.Positions);
            if (_tabSocial != null) SetTabVisual(_tabSocial, _activeTab == PpkbTab.Social);
            if (_tabCommands != null) SetTabVisual(_tabCommands, _activeTab == PpkbTab.Commands);
            if (_tabSettings != null) SetTabVisual(_tabSettings, _activeTab == PpkbTab.Settings);
        }

        // ---------- Position Settings UI (mini-tabs per position) ----------
        private class PosSettingRow 
        { 
            public HockeyPosition position;
            public UITK.DropdownField settingDropdown; 
            public UITK.VisualElement valueContainer;  // Container for dynamic value control
            public UITK.TextField valueField;          // Text field for numeric values
            public UITK.DropdownField valueDropdown;   // Dropdown for enum values like handedness
            public UITK.Slider valueSlider;            // Slider for numeric ranges
            public UITK.VisualElement row; 
        }
        private readonly List<PosSettingRow> _posSettingRows = new List<PosSettingRow>();
        private readonly Dictionary<HockeyPosition, UITK.VisualElement> _positionContents = new Dictionary<HockeyPosition, UITK.VisualElement>();
        private readonly Dictionary<HockeyPosition, UITK.Button> _positionTabs = new Dictionary<HockeyPosition, UITK.Button>();
        private HockeyPosition _activePositionTab = HockeyPosition.LeftWing;
        private UITK.VisualElement _posSettingRowsRoot;

        private static readonly List<string> PositionChoices = new List<string>
        {
            "Left Wing", "Center", "Right Wing", "Left Defense", "Right Defense", "Goalie", "Spectator"
        };
        private static readonly List<string> SettingChoices = new List<string> 
        { 
            "Field of View", "Camera Angle", "Handedness", "Stick Sensitivity", "Look Sensitivity",
            "Chat Opacity", "Chat Scale", "Minimap Opacity", "Minimap Background Opacity",
            "Minimap Horizontal", "Minimap Vertical", "Minimap Scale"
        };
        private static readonly List<string> HandednessChoices = new List<string> { "Left", "Right" };

        private UITK.VisualElement MakePositionSettingsPanel()
        {
            var wrap = new UITK.VisualElement();
            wrap.style.paddingLeft = 10;
            wrap.style.paddingRight = 10;

            var head = new UITK.Label("POSITION SETTINGS");
            head.style.unityFontStyleAndWeight = FontStyle.Normal;
            head.style.fontSize = 30;
            head.style.marginBottom = 10;
            MakeReadable(head);
            wrap.Add(head);

            // Position mini-tabs bar
            var posTabBar = new UITK.VisualElement();
            posTabBar.style.flexDirection = UITK.FlexDirection.Row;
            posTabBar.style.marginBottom = 12;
            posTabBar.style.flexWrap = UITK.Wrap.Wrap;
            wrap.Add(posTabBar);

            var desc = new UITK.Label("<size=12>Override settings for each position. Settings apply automatically when you play that position.</size>");
            desc.style.marginBottom = 12;
            desc.style.whiteSpace = UITK.WhiteSpace.Normal;
            MakeReadable(desc);
            wrap.Add(desc);

            _positionTabs.Clear();
            _positionContents.Clear();

            var positionOrder = new[] {
                HockeyPosition.LeftWing, HockeyPosition.Center, HockeyPosition.RightWing,
                HockeyPosition.LeftDefense, HockeyPosition.RightDefense, HockeyPosition.Goalie,
                HockeyPosition.Spectator
            };

            foreach (var pos in positionOrder)
            {
                var tab = MakePositionTab(pos);
                _positionTabs[pos] = tab;
                posTabBar.Add(tab);
            }

            _posSettingRowsRoot = new UITK.VisualElement();
            wrap.Add(_posSettingRowsRoot);

            // Create content containers for each position
            foreach (var pos in positionOrder)
            {
                var content = CreatePositionContent(pos);
                _positionContents[pos] = content;
                _posSettingRowsRoot.Add(content);
            }

            // Load existing entries and show first tab
            RefreshPositionSettingRows();
            ShowPositionTab(HockeyPosition.LeftWing);

            return wrap;
        }

        private UITK.Button MakePositionTab(HockeyPosition pos)
        {
            var shortName = GetPositionShortName(pos);
            var tab = new UITK.Button(() => ShowPositionTab(pos)) { text = shortName };
            tab.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
            tab.style.height = 36;
            tab.style.minWidth = 50;
            tab.style.paddingLeft = 16;
            tab.style.paddingRight = 16;
            tab.style.marginRight = 4;
            tab.style.marginBottom = 4;
            tab.style.backgroundColor = new UITK.StyleColor(RowBg);
            tab.style.color = Color.white;
            AddButtonFlash(tab);
            return tab;
        }

        private string GetPositionShortName(HockeyPosition pos)
        {
            switch (pos)
            {
                case HockeyPosition.LeftWing: return "Left Wing";
                case HockeyPosition.Center: return "Center";
                case HockeyPosition.RightWing: return "Right Wing";
                case HockeyPosition.LeftDefense: return "Left Defense";
                case HockeyPosition.RightDefense: return "Right Defense";
                case HockeyPosition.Goalie: return "Goalie";
                case HockeyPosition.Spectator: return "Spectator";
                default: return "?";
            }
        }

        private void ShowPositionTab(HockeyPosition pos)
        {
            _activePositionTab = pos;

            // Update tab visuals
            foreach (var kvp in _positionTabs)
            {
                bool active = kvp.Key == pos;
                kvp.Value.style.backgroundColor = active 
                    ? new UITK.StyleColor(GetPositionColor(kvp.Key)) 
                    : new UITK.StyleColor(RowBg);
                kvp.Value.style.color = active ? Color.black : Color.white;
            }

            // Show/hide content
            foreach (var kvp in _positionContents)
            {
                kvp.Value.style.display = (kvp.Key == pos) ? UITK.DisplayStyle.Flex : UITK.DisplayStyle.None;
            }
        }

        private UITK.VisualElement CreatePositionContent(HockeyPosition pos)
        {
            var content = new UITK.VisualElement();
            content.style.display = UITK.DisplayStyle.None;

            // Section header with position name and add button
            var headerRow = new UITK.VisualElement();
            headerRow.style.flexDirection = UITK.FlexDirection.Row;
            headerRow.style.alignItems = UITK.Align.Center;
            headerRow.style.marginBottom = 10;

            var header = new UITK.Label(PositionToString(pos).ToUpper());
            header.style.unityFontStyleAndWeight = FontStyle.Normal;
            header.style.fontSize = 20;
            header.style.color = GetPositionColor(pos);
            header.style.flexGrow = 1;
            MakeReadable(header);
            headerRow.Add(header);

            content.Add(headerRow);

            // Container for setting rows
            var rowsContainer = new UITK.VisualElement();
            rowsContainer.name = "rows_" + pos.ToString();
            content.Add(rowsContainer);

            // Add button at bottom
            var addBtn = new UITK.Button(() => {
                // Find first available setting type that doesn't already exist for this position
                var availableType = GetFirstAvailableSettingType(pos);
                if (availableType.HasValue)
                {
                    AddPositionSettingRow(pos, availableType.Value, GetDefaultValueForSetting(availableType.Value));
                }
                else
                {
                    Debug.Log($"[PPKB] All setting types already added for {pos}");
                }
            });
            addBtn.text = "ADD NEW SETTING";
            addBtn.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
            addBtn.style.height = 50;
            addBtn.style.marginTop = 10;
            addBtn.style.marginBottom = 10;
            addBtn.style.backgroundColor = BtnBrightGray;
            AddButtonFlash(addBtn);
            content.Add(addBtn);

            return content;
        }

        private Color GetPositionColor(HockeyPosition pos)
        {
            switch (pos)
            {
                case HockeyPosition.LeftWing: return new Color(0.3f, 0.65f, 1f);      // Blue
                case HockeyPosition.Center: return new Color(0.3f, 0.85f, 0.5f);      // Green
                case HockeyPosition.RightWing: return new Color(1f, 0.65f, 0.3f);     // Orange
                case HockeyPosition.LeftDefense: return new Color(0.85f, 0.5f, 0.85f); // Purple
                case HockeyPosition.RightDefense: return new Color(0.95f, 0.4f, 0.4f); // Red
                case HockeyPosition.Goalie: return new Color(1f, 0.88f, 0.3f);        // Gold
                case HockeyPosition.Spectator: return new Color(0.7f, 0.7f, 0.75f);   // Light grey
                default: return Color.white;
            }
        }

        // Handedness / Stick Sensitivity / Camera Angle don't meaningfully apply
        // while spectating, so hide them from the Spectator tab.
        private static bool IsSettingAllowedFor(HockeyPosition pos, PositionSettingType st)
        {
            if (pos == HockeyPosition.Spectator)
            {
                return st != PositionSettingType.Handedness
                    && st != PositionSettingType.StickSensitivity
                    && st != PositionSettingType.CameraAngle;
            }
            return true;
        }

        private static List<string> GetSettingChoicesFor(HockeyPosition pos)
        {
            if (pos != HockeyPosition.Spectator) return SettingChoices;
            return SettingChoices
                .Where(name => IsSettingAllowedFor(pos, StringToSettingType(name)))
                .ToList();
        }

        private PositionSettingType? GetFirstAvailableSettingType(HockeyPosition pos)
        {
            // All possible setting types in order
            var allTypes = new[] {
                PositionSettingType.FOV,
                PositionSettingType.CameraAngle,
                PositionSettingType.Handedness,
                PositionSettingType.StickSensitivity,
                PositionSettingType.LookSensitivity,
                PositionSettingType.ChatOpacity,
                PositionSettingType.ChatScale,
                PositionSettingType.MinimapOpacity,
                PositionSettingType.MinimapBackgroundOpacity,
                PositionSettingType.MinimapHorizontalPosition,
                PositionSettingType.MinimapVerticalPosition,
                PositionSettingType.MinimapScale
            };

            foreach (var settingType in allTypes)
            {
                if (!IsSettingAllowedFor(pos, settingType)) continue;
                bool alreadyExists = _posSettingRows.Any(r => r.position == pos &&
                    StringToSettingType(r.settingDropdown?.value ?? "") == settingType);
                if (!alreadyExists)
                    return settingType;
            }

            return null; // All types already used
        }

        private string GetDefaultValueForSetting(PositionSettingType st)
        {
            switch (st)
            {
                case PositionSettingType.FOV: return "90";
                case PositionSettingType.CameraAngle: return "30";
                case PositionSettingType.Handedness: return "Right";
                case PositionSettingType.StickSensitivity: return "1";
                case PositionSettingType.LookSensitivity: return "1";
                case PositionSettingType.ChatOpacity: return "1";
                case PositionSettingType.ChatScale: return "1";
                case PositionSettingType.MinimapOpacity: return "1";
                case PositionSettingType.MinimapBackgroundOpacity: return "0.5";
                case PositionSettingType.MinimapHorizontalPosition: return "100";
                case PositionSettingType.MinimapVerticalPosition: return "0";
                case PositionSettingType.MinimapScale: return "1";
                default: return "90";
            }
        }

        private void RefreshPositionSettingRows()
        {
            _posSettingRows.Clear();
            
            // Clear all row containers
            foreach (var kvp in _positionContents)
            {
                var container = kvp.Value.Q("rows_" + kvp.Key.ToString());
                container?.Clear();
            }

            // Re-add entries from config
            foreach (var entry in _cmd.positionSettings)
            {
                AddPositionSettingRow(entry.position, entry.settingType, entry.value);
            }
        }

        private void AddPositionSettingRow(HockeyPosition position, PositionSettingType settingType, string value)
        {
            if (!_positionContents.TryGetValue(position, out var content)) return;
            var rowsContainer = content.Q("rows_" + position.ToString());
            if (rowsContainer == null) return;

            // Check if this position already has this setting type
            bool alreadyExists = _posSettingRows.Any(r => r.position == position && 
                StringToSettingType(r.settingDropdown?.value ?? "") == settingType);
            if (alreadyExists)
            {
                Debug.Log($"[PPKB] Position {position} already has a {settingType} setting - skipping duplicate");
                return;
            }

            var model = new PosSettingRow { position = position };

            var row = new UITK.VisualElement();
            row.style.flexDirection = UITK.FlexDirection.Row;
            row.style.alignItems = UITK.Align.Center;
            row.style.height = 50;
            row.style.marginBottom = 10;
            row.style.backgroundColor = new UITK.StyleColor(RowBg);
            row.style.paddingLeft = 12; row.style.paddingRight = 12;
            row.style.paddingTop = 8; row.style.paddingBottom = 8;

            // Setting type dropdown
            var settingDropdown = new UITK.DropdownField();
            settingDropdown.choices = GetSettingChoicesFor(position);
            settingDropdown.value = SettingTypeToString(settingType);
            settingDropdown.style.minWidth = 300;
            settingDropdown.style.marginRight = GAP;
                        // Add dropdown indicator
            var arrow = new UITK.Label(" ▼");
            arrow.style.position = Position.Absolute;
            arrow.style.right = 8;
            arrow.style.unityTextAlign = TextAnchor.MiddleRight;
            settingDropdown.Add(arrow);
            
            row.Add(settingDropdown);
            model.settingDropdown = settingDropdown;

            // Value container (will hold either slider+text, dropdown, or just text)
            var valueContainer = new UITK.VisualElement();
            valueContainer.style.flexDirection = UITK.FlexDirection.Row;
            valueContainer.style.alignItems = UITK.Align.Center;
            valueContainer.style.flexGrow = 1;
            valueContainer.style.marginRight = GAP;
            row.Add(valueContainer);
            model.valueContainer = valueContainer;

            // Create the appropriate value control based on setting type
            CreateValueControl(model, settingType, value);

            // Update value control when setting type changes
            settingDropdown.RegisterValueChangedCallback(evt => 
            {
                var newType = StringToSettingType(evt.newValue);
                var oldType = StringToSettingType(evt.previousValue);
                
                // Check if another row already has this setting type for this position
                bool duplicateExists = _posSettingRows.Any(r => r != model && 
                    r.position == position && 
                    StringToSettingType(r.settingDropdown?.value ?? "") == newType);
                
                if (duplicateExists)
                {
                    // Revert to previous value
                    settingDropdown.SetValueWithoutNotify(evt.previousValue);
                    Debug.Log($"[PPKB] Cannot change to {newType} - {position} already has that setting");
                    return;
                }
                
                CreateValueControl(model, newType, GetDefaultValueForSetting(newType));
            });

            // Remove button (styled like commands section)
            var remove = new UITK.Button(() => 
            { 
                // Get the setting type before removing
                var removedType = StringToSettingType(model.settingDropdown?.value ?? "");
                var removedPosition = model.position;
                
                row.RemoveFromHierarchy(); 
                _posSettingRows.Remove(model);
                
                // If we're currently playing this position, restore that setting to base
                RestoreSettingToBase(removedPosition, removedType);
            });

            StyleRowButton(remove, BTN_W_RM, "REMOVE");
            remove.style.backgroundColor = new UITK.StyleColor(ButtonBg);
            row.Add(remove);

            model.row = row;
            _posSettingRows.Add(model);
            Debug.Log($"[PPKB] Added position setting row: pos={position}, type={settingType}, value={value}, total rows: {_posSettingRows.Count}");
            rowsContainer.Add(row);
        }

        private void CreateValueControl(PosSettingRow model, PositionSettingType settingType, string value)
        {
            model.valueContainer.Clear();
            model.valueField = null;
            model.valueDropdown = null;
            model.valueSlider = null;

            switch (settingType)
            {
                case PositionSettingType.FOV:
                    // FOV: Slider (60-150) + TextField
                    float fovVal = 90f;
                    float.TryParse(value, out fovVal);
                    fovVal = Mathf.Clamp(fovVal, 60f, 150f);

                    var fovSlider = new UITK.Slider(60f, 150f) { value = fovVal };
                    fovSlider.style.flexGrow = 1;
                    fovSlider.style.minWidth = 150;
                    fovSlider.style.height = 20;
                    StyleSlider(fovSlider);
                    model.valueContainer.Add(fovSlider);
                    model.valueSlider = fovSlider;

                    var fovField = new UITK.TextField { value = fovVal.ToString("0") };
                    fovField.style.width = 60;
                    fovField.style.marginLeft = 8;
                    MakeReadable(fovField);
                    model.valueContainer.Add(fovField);
                    model.valueField = fovField;

                    fovSlider.RegisterValueChangedCallback(evt => fovField.SetValueWithoutNotify(evt.newValue.ToString("0")));
                    fovField.RegisterValueChangedCallback(evt => {
                        if (float.TryParse(evt.newValue, out float v))
                            fovSlider.SetValueWithoutNotify(Mathf.Clamp(v, 60f, 150f));
                    });
                    break;

                case PositionSettingType.CameraAngle:
                    // Camera Angle: Slider (0-90) + TextField
                    float angleVal = 30f;
                    float.TryParse(value, out angleVal);
                    angleVal = Mathf.Clamp(angleVal, 0f, 90f);

                    var angleSlider = new UITK.Slider(0f, 90f) { value = angleVal };
                    angleSlider.style.flexGrow = 1;
                    angleSlider.style.minWidth = 150;
                    angleSlider.style.height = 20;
                    StyleSlider(angleSlider);
                    model.valueContainer.Add(angleSlider);
                    model.valueSlider = angleSlider;

                    var angleField = new UITK.TextField { value = angleVal.ToString("0") };
                    angleField.style.width = 60;
                    angleField.style.marginLeft = 8;
                    MakeReadable(angleField);
                    model.valueContainer.Add(angleField);
                    model.valueField = angleField;

                    angleSlider.RegisterValueChangedCallback(evt => angleField.SetValueWithoutNotify(evt.newValue.ToString("0")));
                    angleField.RegisterValueChangedCallback(evt => {
                        if (float.TryParse(evt.newValue, out float v))
                            angleSlider.SetValueWithoutNotify(Mathf.Clamp(v, 0f, 90f));
                    });
                    break;

                case PositionSettingType.Handedness:
                    // Handedness: Dropdown (Left/Right)
                    var handDropdown = new UITK.DropdownField();
                    handDropdown.choices = HandednessChoices;
                    handDropdown.value = (value == "Left" || value == "LEFT") ? "Left" : "Right";
                    handDropdown.style.minWidth = 100;
                    model.valueContainer.Add(handDropdown);
                    model.valueDropdown = handDropdown;
                    break;

                case PositionSettingType.StickSensitivity:
                    // Stick Sensitivity: Slider (0-1) + TextField
                    float stickVal = 0.2f;
                    float.TryParse(value, out stickVal);
                    stickVal = Mathf.Clamp(stickVal, 0f, 1f);

                    var stickSlider = new UITK.Slider(0f, 1f) { value = stickVal };
                    stickSlider.style.flexGrow = 1;
                    stickSlider.style.minWidth = 150;
                    stickSlider.style.height = 20;
                    StyleSlider(stickSlider);
                    model.valueContainer.Add(stickSlider);
                    model.valueSlider = stickSlider;

                    var stickField = new UITK.TextField { value = stickVal.ToString("0.00") };
                    stickField.style.minWidth = 50;
                    stickField.style.maxWidth = 50;
                    stickField.style.marginLeft = 8;
                    stickField.style.whiteSpace = UITK.WhiteSpace.NoWrap;
                    MakeReadable(stickField);
                    model.valueContainer.Add(stickField);
                    model.valueField = stickField;

                    stickSlider.RegisterValueChangedCallback(evt => stickField.SetValueWithoutNotify(evt.newValue.ToString("0.00")));
                    stickField.RegisterValueChangedCallback(evt => {
                        if (float.TryParse(evt.newValue, out float v))
                            stickSlider.SetValueWithoutNotify(Mathf.Clamp(v, 0f, 1f));
                    });
                    break;

                case PositionSettingType.LookSensitivity:
                    // Look Sensitivity: Slider (0-1) + TextField
                    float lookVal = 0.2f;
                    float.TryParse(value, out lookVal);
                    lookVal = Mathf.Clamp(lookVal, 0f, 1f);

                    var lookSlider = new UITK.Slider(0f, 1f) { value = lookVal };
                    lookSlider.style.flexGrow = 1;
                    lookSlider.style.minWidth = 150;
                    lookSlider.style.height = 20;
                    StyleSlider(lookSlider);
                    model.valueContainer.Add(lookSlider);
                    model.valueSlider = lookSlider;

                    var lookField = new UITK.TextField { value = lookVal.ToString("0.00") };
                    lookField.style.minWidth = 50;
                    lookField.style.maxWidth = 50;
                    lookField.style.marginLeft = 8;
                    lookField.style.whiteSpace = UITK.WhiteSpace.NoWrap;
                    MakeReadable(lookField);
                    model.valueContainer.Add(lookField);
                    model.valueField = lookField;

                    lookSlider.RegisterValueChangedCallback(evt => lookField.SetValueWithoutNotify(evt.newValue.ToString("0.00")));
                    lookField.RegisterValueChangedCallback(evt => {
                        if (float.TryParse(evt.newValue, out float v))
                            lookSlider.SetValueWithoutNotify(Mathf.Clamp(v, 0f, 1f));
                    });
                    break;

                case PositionSettingType.ChatOpacity:
                case PositionSettingType.ChatScale:
                case PositionSettingType.MinimapOpacity:
                case PositionSettingType.MinimapBackgroundOpacity:
                case PositionSettingType.MinimapScale:
                    // All these are 0-1 sliders
                    float opacityVal = 1f;
                    float.TryParse(value, out opacityVal);
                    opacityVal = Mathf.Clamp(opacityVal, 0f, 1f);

                    var opacitySlider = new UITK.Slider(0f, 1f) { value = opacityVal };
                    opacitySlider.style.flexGrow = 1;
                    opacitySlider.style.minWidth = 150;
                    opacitySlider.style.height = 20;
                    StyleSlider(opacitySlider);
                    model.valueContainer.Add(opacitySlider);
                    model.valueSlider = opacitySlider;

                    var opacityField = new UITK.TextField { value = opacityVal.ToString("0.00") };
                    opacityField.style.minWidth = 50;
                    opacityField.style.maxWidth = 50;
                    opacityField.style.marginLeft = 8;
                    opacityField.style.whiteSpace = UITK.WhiteSpace.NoWrap;
                    MakeReadable(opacityField);
                    model.valueContainer.Add(opacityField);
                    model.valueField = opacityField;

                    opacitySlider.RegisterValueChangedCallback(evt => opacityField.SetValueWithoutNotify(evt.newValue.ToString("0.00")));
                    opacityField.RegisterValueChangedCallback(evt => {
                        if (float.TryParse(evt.newValue, out float v))
                            opacitySlider.SetValueWithoutNotify(Mathf.Clamp(v, 0f, 1f));
                    });
                    break;

                case PositionSettingType.MinimapHorizontalPosition:
                case PositionSettingType.MinimapVerticalPosition:
                    // Position sliders: game uses 0-100 (percent of screen);
                    // defaults are 100 (right edge) for H and 0 (top) for V.
                    float posDefault = (settingType == PositionSettingType.MinimapHorizontalPosition) ? 100f : 0f;
                    float posVal = posDefault;
                    float.TryParse(value, out posVal);
                    posVal = Mathf.Clamp(posVal, 0f, 100f);

                    var posSlider = new UITK.Slider(0f, 100f) { value = posVal };
                    posSlider.style.flexGrow = 1;
                    posSlider.style.minWidth = 150;
                    posSlider.style.height = 20;
                    StyleSlider(posSlider);
                    model.valueContainer.Add(posSlider);
                    model.valueSlider = posSlider;

                    var posField = new UITK.TextField { value = posVal.ToString("0") };
                    posField.style.minWidth = 50;
                    posField.style.maxWidth = 50;
                    posField.style.marginLeft = 8;
                    posField.style.whiteSpace = UITK.WhiteSpace.NoWrap;
                    MakeReadable(posField);
                    model.valueContainer.Add(posField);
                    model.valueField = posField;

                    posSlider.RegisterValueChangedCallback(evt => posField.SetValueWithoutNotify(evt.newValue.ToString("0")));
                    posField.RegisterValueChangedCallback(evt => {
                        if (float.TryParse(evt.newValue, out float v))
                            posSlider.SetValueWithoutNotify(Mathf.Clamp(v, 0f, 100f));
                    });
                    break;
            }
        }

        private string GetValueFromModel(PosSettingRow model)
        {
            if (model.valueDropdown != null) return model.valueDropdown.value;
            if (model.valueSlider != null) return model.valueSlider.value.ToString();
            if (model.valueField != null) return model.valueField.value;
            return "";
        }

        private static string PositionToString(HockeyPosition pos)
        {
            switch (pos)
            {
                case HockeyPosition.LeftWing: return "Left Wing";
                case HockeyPosition.Center: return "Center";
                case HockeyPosition.RightWing: return "Right Wing";
                case HockeyPosition.LeftDefense: return "Left Defense";
                case HockeyPosition.RightDefense: return "Right Defense";
                case HockeyPosition.Goalie: return "Goalie";
                case HockeyPosition.Spectator: return "Spectator";
                default: return "Goalie";
            }
        }

        private static HockeyPosition StringToPosition(string s)
        {
            switch (s)
            {
                case "Left Wing": return HockeyPosition.LeftWing;
                case "Center": return HockeyPosition.Center;
                case "Right Wing": return HockeyPosition.RightWing;
                case "Left Defense": return HockeyPosition.LeftDefense;
                case "Right Defense": return HockeyPosition.RightDefense;
                case "Goalie": return HockeyPosition.Goalie;
                case "Spectator": return HockeyPosition.Spectator;
                default: return HockeyPosition.None;
            }
        }

        private static string SettingTypeToString(PositionSettingType st)
        {
            switch (st)
            {
                case PositionSettingType.FOV: return "Field of View";
                case PositionSettingType.CameraAngle: return "Camera Angle";
                case PositionSettingType.Handedness: return "Handedness";
                case PositionSettingType.StickSensitivity: return "Stick Sensitivity";
                case PositionSettingType.LookSensitivity: return "Look Sensitivity";
                case PositionSettingType.ChatOpacity: return "Chat Opacity";
                case PositionSettingType.ChatScale: return "Chat Scale";
                case PositionSettingType.MinimapOpacity: return "Minimap Opacity";
                case PositionSettingType.MinimapBackgroundOpacity: return "Minimap Background Opacity";
                case PositionSettingType.MinimapHorizontalPosition: return "Minimap Horizontal";
                case PositionSettingType.MinimapVerticalPosition: return "Minimap Vertical";
                case PositionSettingType.MinimapScale: return "Minimap Scale";
                default: return "Field of View";
            }
        }

        private static PositionSettingType StringToSettingType(string s)
        {
            switch (s)
            {
                case "Field of View": return PositionSettingType.FOV;
                case "Camera Angle": return PositionSettingType.CameraAngle;
                case "Handedness": return PositionSettingType.Handedness;
                case "Stick Sensitivity": return PositionSettingType.StickSensitivity;
                case "Look Sensitivity": return PositionSettingType.LookSensitivity;
                case "Chat Opacity": return PositionSettingType.ChatOpacity;
                case "Chat Scale": return PositionSettingType.ChatScale;
                case "Minimap Opacity": return PositionSettingType.MinimapOpacity;
                case "Minimap Background Opacity": return PositionSettingType.MinimapBackgroundOpacity;
                case "Minimap Horizontal": return PositionSettingType.MinimapHorizontalPosition;
                case "Minimap Vertical": return PositionSettingType.MinimapVerticalPosition;
                case "Minimap Scale": return PositionSettingType.MinimapScale;
                default: return PositionSettingType.FOV;
            }
        }

        private static readonly Color32 ChipBg = new Color32(80, 80, 80, 255);
        private static readonly Color32 ChipXBg = new Color32(100, 100, 100, 255);

        private UITK.VisualElement MakeChip(string text, Action onRemove)
        {
            var chip = new UITK.VisualElement();
            chip.style.flexDirection = UITK.FlexDirection.Row;
            chip.style.alignItems = UITK.Align.Center;
            chip.style.backgroundColor = new UITK.StyleColor(ChipBg);
            chip.style.paddingLeft = 8; chip.style.paddingRight = 4;
            chip.style.paddingTop = 4; chip.style.paddingBottom = 4;
            chip.style.marginRight = 4;
            chip.style.borderTopLeftRadius = 4; chip.style.borderTopRightRadius = 4;
            chip.style.borderBottomLeftRadius = 4; chip.style.borderBottomRightRadius = 4;

            var label = new UITK.Label(text);
            label.style.fontSize = 14;
            label.style.color = Color.white;
            MakeReadable(label);
            chip.Add(label);

            var xBtn = new UITK.Button(() => onRemove?.Invoke()) { text = "×" };
            xBtn.style.width = 20; xBtn.style.height = 20;
            xBtn.style.marginLeft = 4;
            xBtn.style.backgroundColor = new UITK.StyleColor(ChipXBg);
            xBtn.style.fontSize = 14;
            xBtn.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
            xBtn.style.paddingLeft = 0; xBtn.style.paddingRight = 0;
            xBtn.style.paddingTop = 0; xBtn.style.paddingBottom = 0;
            xBtn.style.color = Color.white;
            MakeReadable(xBtn);
            AddChipButtonFlash(xBtn);
            chip.Add(xBtn);

            return chip;
        }

        private static void AddChipButtonFlash(UITK.Button btn)
        {
            btn.RegisterCallback<UITK.PointerEnterEvent>(_ =>
            {
                btn.style.backgroundColor = new UITK.StyleColor(new Color32(180, 80, 80, 255));
                btn.style.color = Color.white;
            });
            btn.RegisterCallback<UITK.PointerLeaveEvent>(_ =>
            {
                btn.style.backgroundColor = new UITK.StyleColor(ChipXBg);
                btn.style.color = Color.white;
            });
        }

        // --- Unified UI sizing ---
        const float BTN_H = 36f;
        const float BTN_W = 120f;     // BIND / CLEAR
        const float BTN_W_RM = 150f;  // REMOVE
        const float GAP = 8f;

        private void StyleTextField(UITK.TextField tf)
        {
            MakeReadable(tf);
            tf.style.height = BTN_H;
            tf.style.marginRight = GAP;

            tf.style.flexGrow = 0;
            tf.style.flexShrink = 1;
            tf.style.flexBasis = 0;
            tf.style.minWidth = 160;

            var input = tf.childCount > 0 ? tf.ElementAt(0) : null;
            if (input != null)
            {
                input.style.height = BTN_H;
                input.style.flexGrow = 0;
                input.style.flexShrink = 1;
                input.style.flexBasis = 0;
                input.style.minWidth = 160;
                input.style.whiteSpace = UITK.WhiteSpace.NoWrap;
                input.style.overflow = UITK.Overflow.Hidden;
                input.style.textOverflow = UITK.TextOverflow.Clip;
            }
        }
        private void StyleRowButton(UITK.Button b, float w, string label = null)
        {
            if (!string.IsNullOrEmpty(label)) b.text = label;

            b.style.height = BTN_H;
            b.style.minWidth = w;
            b.style.maxWidth = w;
            b.style.whiteSpace = UITK.WhiteSpace.NoWrap;
            b.style.flexShrink = 0;

            b.style.marginLeft = GAP;
            b.style.paddingLeft = 12;
            b.style.paddingRight = 12;
            b.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);

            b.style.backgroundColor = new UITK.StyleColor(ButtonBg);
            AddButtonFlashWhiteHover(b, (Color)ButtonBg);
        }

        private static void Flashable(UITK.Button b, Color baseBg, int flashMs = 140)
        {
            b.focusable = true;
            void SetBase() { b.style.backgroundColor = new UITK.StyleColor(baseBg); b.style.color = Color.white; }

            SetBase();
            bool hover = false, flashing = false;

            b.RegisterCallback<PointerEnterEvent>(_ => { hover = true; b.style.backgroundColor = Color.white; b.style.color = Color.black; });
            b.RegisterCallback<PointerLeaveEvent>(_ => { hover = false; if (!flashing) SetBase(); });
            b.RegisterCallback<GeometryChangedEvent>(_ => SetBase());

            b.RegisterCallback<PointerUpEvent>(_ =>
            {
                flashing = true;
                b.style.backgroundColor = Color.white; b.style.color = Color.black;
                b.schedule.Execute(() => { flashing = false; if (!hover) SetBase(); }).StartingIn(flashMs);
            });
        }
        private static void AddButtonFlash(UITK.Button b, int flashMs = 140)
        {
            var baseBg = b.style.backgroundColor.keyword != UITK.StyleKeyword.Null ? b.style.backgroundColor.value : b.resolvedStyle.backgroundColor;
            Flashable(b, baseBg, flashMs);
        }
        private static void AddButtonFlashWhiteHover(UITK.Button b, Color baseBg, int flashMs = 140) => Flashable(b, baseBg, flashMs);

        private void SetTabVisual(UITK.Button b, bool active)
        {
            b.style.backgroundColor = new UITK.StyleColor(active ? TabActiveBg : TabInactiveBg);
            b.style.color = active ? Color.white : new Color(0.7f, 0.7f, 0.7f);
            b.style.borderBottomWidth = active ? 3 : 0;
        }
        private void AddTabHover(UITK.Button b, Func<bool> isActive)
        {
            b.focusable = false;
            void Apply() => SetTabVisual(b, isActive());
            Apply();

            b.RegisterCallback<PointerEnterEvent>(_ => { 
                if (!isActive()) { 
                    b.style.backgroundColor = new UITK.StyleColor(Color.white); 
                    b.style.color = Color.black; 
                } 
            });
            b.RegisterCallback<PointerLeaveEvent>(_ => Apply());
            b.RegisterCallback<AttachToPanelEvent>(_ => Apply());
            b.RegisterCallback<GeometryChangedEvent>(_ => Apply());
        }

        partial void RefreshPanelFields()
        {
            if (_actionsSectionVE != null)
            {
                _actionsSectionVE.Clear();
                // Show position settings panel instead of role tables
                _actionsSectionVE.Add(MakePositionSettingsPanel());
            }
            if (_commandsSectionVE != null)
            {
                _commandsSectionVE.Clear();
                _commandsSectionVE.Add(MakeCommandsTable());
                RefreshPanelCommandFields();
            }
            if (_socialSectionVE != null)
            {
                _socialSectionVE.Clear();
                _socialSectionVE.Add(MakeSocialTable());
            }
            if (_settingsSectionVE != null)
            {
                // Rebuild settings tab to reflect current config values
                ShowSettingsTab();
            }
            
            // Re-apply tab visibility to ensure correct tab is shown after rebuild
            _actionsSectionVE.style.display = (_activeTab == PpkbTab.Positions) ? UITK.DisplayStyle.Flex : UITK.DisplayStyle.None;
            _socialSectionVE.style.display = (_activeTab == PpkbTab.Social) ? UITK.DisplayStyle.Flex : UITK.DisplayStyle.None;
            _commandsSectionVE.style.display = (_activeTab == PpkbTab.Commands) ? UITK.DisplayStyle.Flex : UITK.DisplayStyle.None;
            _settingsSectionVE.style.display = (_activeTab == PpkbTab.Settings) ? UITK.DisplayStyle.Flex : UITK.DisplayStyle.None;
        }

        public void RefreshUI()
        {
            RefreshPanelFields();
        }

        public Color? GetMainPanelBackgroundColor()
        {
            if (_ppkbPanel != null)
            {
                return _ppkbPanel.style.backgroundColor.value;
            }
            return null;
        }
        private void RepairUINavigateIfBroken()
        {
            try
            {
                var ui = UnityEngine.Object.FindFirstObjectByType<InputSystemUIInputModule>(UnityEngine.FindObjectsInactive.Include);
                var asset = ui?.actionsAsset;
                if (asset == null) return;

                var nav = asset.FindAction("UI/Navigate", false) ?? asset.FindAction("Navigate", false);
                if (nav == null) return;

                bool hasBareKeyBinding = nav.bindings.Any(b => !b.isComposite && !b.isPartOfComposite);
                if (hasBareKeyBinding)
                {
                    for (int i = 0; i < nav.bindings.Count; i++)
                    {
                        nav.RemoveAllBindingOverrides();
                    }
                    Debug.Log("[PPKB] Repaired UI/Navigate binding overrides.");
                }
            }
            catch (System.Exception e)
            {
                Debug.Log("[PPKB] RepairUINavigateIfBroken failed: " + e.Message);
            }
        }
        private static void AttachLast(UITK.VisualElement parent, UITK.VisualElement child)
        {
            if (parent == null || child == null) return;
            child.RemoveFromHierarchy();
            parent.Add(child);
        }

        private UITK.VisualElement MakeSocialTable()
        {
            var container = new UITK.VisualElement();
            container.style.flexDirection = UITK.FlexDirection.Column;
            container.style.paddingTop = 8;
            container.style.paddingBottom = 8;
            var playerHeader = new UITK.Label("PLAYER MANAGEMENT");
            playerHeader.style.fontSize = 30;
            playerHeader.style.color = new UITK.StyleColor(P_White);
            playerHeader.style.unityFontStyleAndWeight = FontStyle.Normal;
            playerHeader.style.marginBottom = 4;
            container.Add(playerHeader);

            // Global search field (searches all sections)
            var globalSearchContainer = new UITK.VisualElement();
            globalSearchContainer.style.flexDirection = UITK.FlexDirection.Row;
            globalSearchContainer.style.alignItems = UITK.Align.Center;
            globalSearchContainer.style.marginBottom = 16;

            var globalSearchLabel = new UITK.Label("Search All Players:");
            globalSearchLabel.style.color = new UITK.StyleColor(P_White);
            globalSearchLabel.style.fontSize = 18;
            globalSearchLabel.style.marginRight = 8;
            globalSearchLabel.style.minWidth = 120;
            MakeReadable(globalSearchLabel);
            globalSearchContainer.Add(globalSearchLabel);

            var globalSearchField = new UITK.TextField();
            globalSearchField.style.flexGrow = 1;
            globalSearchField.style.height = 30;
            globalSearchField.style.backgroundColor = new UITK.StyleColor(RowBg);
            globalSearchField.style.borderTopColor = new UITK.StyleColor(Color.gray);
            globalSearchField.style.borderBottomColor = new UITK.StyleColor(Color.gray);
            globalSearchField.style.borderLeftColor = new UITK.StyleColor(Color.gray);
            globalSearchField.style.borderRightColor = new UITK.StyleColor(Color.gray);
            globalSearchField.style.borderTopWidth = 1;
            globalSearchField.style.borderBottomWidth = 1;
            globalSearchField.style.borderLeftWidth = 1;
            globalSearchField.style.borderRightWidth = 1;
            globalSearchField.style.color = new UITK.StyleColor(P_White);
            globalSearchContainer.Add(globalSearchField);

            container.Add(globalSearchContainer);

            // Create containers for each section
            var savHeader = new UITK.Label("SAVED PLAYERS");
            savHeader.style.fontSize = 24;
            savHeader.style.color = new UITK.StyleColor(P_White);
            savHeader.style.unityFontStyleAndWeight = FontStyle.Normal;
            savHeader.style.marginBottom = 4;
            container.Add(savHeader);

            var savContainer = new UITK.VisualElement();
            savContainer.style.flexDirection = UITK.FlexDirection.Column;
            container.Add(savContainer);

            // Blocked Players Section
            var blockedHeader = new UITK.Label("BLOCKED PLAYERS");
            blockedHeader.style.fontSize = 24;
            blockedHeader.style.color = new UITK.StyleColor(P_White);
            blockedHeader.style.unityFontStyleAndWeight = FontStyle.Normal;
            blockedHeader.style.marginBottom = 4;
            container.Add(blockedHeader);

            var blockedContainer = new UITK.VisualElement();
            blockedContainer.style.flexDirection = UITK.FlexDirection.Column;
            blockedContainer.style.marginBottom = 16;
            container.Add(blockedContainer);

            // Recent Players Section (conditionally shown based on settings)
            var recentHeaderRow = new UITK.VisualElement();
            recentHeaderRow.style.flexDirection = UITK.FlexDirection.Row;
            recentHeaderRow.style.alignItems = UITK.Align.Center;
            recentHeaderRow.style.marginTop = 16;
            recentHeaderRow.style.marginBottom = 4;
            // Hide if setting is disabled
            recentHeaderRow.style.display = _cmd.enableRecentPlayers ? UITK.DisplayStyle.Flex : UITK.DisplayStyle.None;

            var recentHeader = new UITK.Label("RECENT PLAYERS (LAST 50)");
            recentHeader.style.fontSize = 24;
            recentHeader.style.color = new UITK.StyleColor(P_White);
            recentHeader.style.unityFontStyleAndWeight = FontStyle.Normal;
            recentHeader.style.flexGrow = 1;
            recentHeaderRow.Add(recentHeader);

            container.Add(recentHeaderRow);

            var recentContainer = new UITK.VisualElement();
            recentContainer.style.flexDirection = UITK.FlexDirection.Column;
            // Hide if setting is disabled
            recentContainer.style.display = _cmd.enableRecentPlayers ? UITK.DisplayStyle.Flex : UITK.DisplayStyle.None;
            container.Add(recentContainer);

            // Global search function that searches all sections
            System.Action<string> refreshAllSections = (searchText) =>
            {
                // Clear all containers
                savContainer.Clear();
                blockedContainer.Clear();
                recentContainer.Clear();

                var searchLower = searchText.ToLower();
                bool hasSearch = !string.IsNullOrEmpty(searchText);

                // Filter and display saved players
                var SavedPlayers = PoncePuck.LocalMute.LocalMuteStore.Config.SavedPlayers;
                Debug.Log($"[PPKB] Refreshing social panel - SavedPlayers count: {SavedPlayers.Count}");
                var filteredSaved = hasSearch ?
                    SavedPlayers.Where(p => p.playerName.ToLower().Contains(searchLower)).ToList() :
                    SavedPlayers.ToList();

                if (filteredSaved.Count == 0 && !hasSearch)
                {
                    var noSaved = new UITK.Label("No saved players");
                    noSaved.style.color = new UITK.StyleColor(Color.gray);
                    savContainer.Add(noSaved);
                }
                else
                {
                    foreach (var player in filteredSaved)
                    {
                        var playerRow = CreatePlayerRow(player, isBlocked: false);
                        savContainer.Add(playerRow);
                    }
                }

                // Filter and display blocked players
                var blockedPlayers = PoncePuck.LocalMute.LocalMuteStore.Config.BlockedPlayers;
                Debug.Log($"[PPKB] Refreshing social panel - BlockedPlayers count: {blockedPlayers.Count}");
                var filteredBlocked = hasSearch ?
                    blockedPlayers.Where(p => p.playerName.ToLower().Contains(searchLower)).ToList() :
                    blockedPlayers.ToList();

                if (filteredBlocked.Count == 0 && !hasSearch)
                {
                    var noBlocked = new UITK.Label("No blocked players");
                    noBlocked.style.color = new UITK.StyleColor(Color.gray);
                    blockedContainer.Add(noBlocked);
                }
                else
                {
                    foreach (var player in filteredBlocked)
                    {
                        var playerRow = CreatePlayerRow(player, isBlocked: true);
                        blockedContainer.Add(playerRow);
                    }
                }

                // Filter and display recent players
                var recentPlayers = PoncePuck.LocalMute.LocalMuteStore.Config.RecentPlayers;
                var filteredRecent = hasSearch ?
                    recentPlayers.Where(p => p.playerName.ToLower().Contains(searchLower)).ToList() :
                    recentPlayers.ToList();

                if (filteredRecent.Count == 0 && !hasSearch)
                {
                    var noRecent = new UITK.Label("No recent players");
                    noRecent.style.color = new UITK.StyleColor(Color.gray);
                    recentContainer.Add(noRecent);
                }
                else
                {
                    foreach (var player in filteredRecent.Take(50))
                    {
                        var playerRow = CreateRecentPlayerRow(player);
                        recentContainer.Add(playerRow);
                    }
                }
            };

            // Initial load
            refreshAllSections("");

            // Global search functionality
            globalSearchField.RegisterValueChangedCallback(evt => refreshAllSections(evt.newValue));

            return container;
        }

        private UITK.VisualElement CreatePlayerRow(PoncePuck.LocalMute.PlayerInfo player, bool isBlocked)
        {
            // Use the same styling pattern as action/command rows (clean single line)
            var row = new UITK.VisualElement();
            row.style.flexDirection = UITK.FlexDirection.Row;
            row.style.alignItems = UITK.Align.Center;
            row.style.height = 50;
            row.style.marginBottom = 10;
            row.style.backgroundColor = new UITK.StyleColor(RowBg);
            row.style.paddingLeft = 12;
            row.style.paddingRight = 10;
            row.style.paddingTop = 8;
            row.style.paddingBottom = 8;

            // Player name + volume info (left side, fixed width)
            var nameContainer = new UITK.VisualElement();
            nameContainer.style.flexDirection = UITK.FlexDirection.Column;
            nameContainer.style.minWidth = 200;
            nameContainer.style.maxWidth = 200;
            nameContainer.style.justifyContent = UITK.Justify.Center;

            var nameLabel = new UITK.Label(player.playerName);
            nameLabel.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleLeft);
            nameLabel.style.color = isBlocked ? new UITK.StyleColor(Color.red) : new UITK.StyleColor(Color.green);
            nameLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            nameLabel.style.whiteSpace = UITK.WhiteSpace.NoWrap;
            nameLabel.style.overflow = UITK.Overflow.Hidden;
            nameLabel.style.marginBottom = 2;
            MakeReadable(nameLabel);
            nameContainer.Add(nameLabel);



            row.Add(nameContainer);

            // Middle section: Volume slider (like chips area, grows and pushes buttons right)
            var middleSection = new UITK.VisualElement();
            middleSection.style.flexDirection = UITK.FlexDirection.Row;
            middleSection.style.justifyContent = UITK.Justify.FlexEnd;
            middleSection.style.alignItems = UITK.Align.Center;
            middleSection.style.flexGrow = 1;
            middleSection.style.flexShrink = 1;
            middleSection.style.minWidth = 0;
            middleSection.style.marginLeft = 4;
            middleSection.style.marginTop = 4;

            // Volume slider (settings style)
            if (ulong.TryParse(player.steamId, out ulong steamIdUlong))
            {
                var volume = PoncePuck.LocalMute.LocalMuteStore.GetVoiceVolume(steamIdUlong);

                // Volume slider (mimicking settings slider style)
                var volumeSlider = new UITK.Slider(0f, 200f) { value = volume };
                volumeSlider.style.flexGrow = 1;
                volumeSlider.style.flexBasis = 0;
                volumeSlider.style.marginLeft = 6;
                volumeSlider.style.maxWidth = 300; // Limit maximum width

                // Apply settings slider styling
                StyleSliderLikeBase(volumeSlider);

                // Editable volume value field
                var volumeValueField = new UITK.TextField();
                volumeValueField.value = $"{volume}";
                MakeReadable(volumeValueField);
                volumeValueField.style.minWidth = 60;
                volumeValueField.style.maxWidth = 60;
                volumeValueField.style.unityTextAlign = TextAnchor.MiddleRight;

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

                middleSection.Add(volumeValueField);
                middleSection.Add(volumeSlider);
            }

            row.Add(middleSection);

            // Right section: Buttons (fixed width like other tabs)
            var rightSection = new UITK.VisualElement();
            rightSection.style.flexDirection = UITK.FlexDirection.Row;
            rightSection.style.alignItems = UITK.Align.Center;
            rightSection.style.flexShrink = 0;

            // Information button (shows info with editable notes)
            var infoBtn = new UITK.Button(() =>
            {
                ShowInformationDialog(player);
            });
            StyleRowButton(infoBtn, BTN_W, "INFO");
            infoBtn.style.backgroundColor = new UITK.StyleColor(ButtonBg);
            infoBtn.style.width = 80;
            rightSection.Add(infoBtn);

            // Block/Save toggle button
            if (!isBlocked)
            {
                // Player is saved - show BLOCK button
                var blockBtn = new UITK.Button(() =>
                {
                    // Block: mute and move from saved to blocked
                    if (ulong.TryParse(player.steamId, out ulong sid))
                    {
                        PoncePuck.LocalMute.LocalMuteStore.ToggleFullMute(sid, true);
                    }
                    PoncePuck.LocalMute.LocalMuteStore.AddToBlocked(player.steamId, player.playerName, player.profileUrl);
                    PoncePuck.LocalMute.LocalMuteStore.RemoveFromSaved(player.steamId);
                    RefreshPanelFields();

                    // Force immediate scoreboard refresh
                    if (ulong.TryParse(player.steamId, out ulong sid2))
                    {
                        ScoreboardUtil.RefreshRowForSteamId(sid2);
                    }
                });
                StyleRowButton(blockBtn, BTN_W, "BLOCK");
                blockBtn.style.backgroundColor = new UITK.StyleColor(ButtonBg);
                blockBtn.style.width = 140;
                rightSection.Add(blockBtn);
            }
            else
            {
                // Player is blocked - show SAVE button
                var saveBtn = new UITK.Button(() =>
                {
                    // Save: unmute and move from blocked to saved
                    if (ulong.TryParse(player.steamId, out ulong sid))
                    {
                        PoncePuck.LocalMute.LocalMuteStore.ToggleFullMute(sid, false);
                    }
                    PoncePuck.LocalMute.LocalMuteStore.AddToSaved(player.steamId, player.playerName, player.profileUrl);
                    PoncePuck.LocalMute.LocalMuteStore.RemoveFromBlocked(player.steamId);
                    RefreshPanelFields();

                    // Force immediate scoreboard refresh
                    if (ulong.TryParse(player.steamId, out ulong sid2))
                    {
                        ScoreboardUtil.RefreshRowForSteamId(sid2);
                    }
                });
                StyleRowButton(saveBtn, BTN_W, "SAVE");
                saveBtn.style.backgroundColor = new UITK.StyleColor(ButtonBg);
                rightSection.Add(saveBtn);
            }

            // Remove button
            var removeBtn = new UITK.Button(() =>
        {
            if (isBlocked)
            {
                // Unblock: unmute the player AND remove from social blocked list
                if (ulong.TryParse(player.steamId, out ulong sid))
                {
                    PoncePuck.LocalMute.LocalMuteStore.ToggleFullMute(sid, false);
                }
                PoncePuck.LocalMute.LocalMuteStore.RemoveFromBlocked(player.steamId);
            }
            else
            {
                PoncePuck.LocalMute.LocalMuteStore.RemoveFromSaved(player.steamId);
            }
            RefreshPanelFields();

            // Force immediate scoreboard refresh
            if (ulong.TryParse(player.steamId, out ulong sid2))
            {
                ScoreboardUtil.RefreshRowForSteamId(sid2);
            }
        });

            StyleRowButton(removeBtn, BTN_W, "REMOVE");
            removeBtn.style.backgroundColor = new UITK.StyleColor(ButtonBg);
            rightSection.Add(removeBtn);

            row.Add(rightSection);

            return row;
        }

        private UITK.VisualElement CreateRecentPlayerRow(PoncePuck.LocalMute.PlayerInfo player)
        {
            // Same styling as regular player rows
            var row = new UITK.VisualElement();
            row.style.flexDirection = UITK.FlexDirection.Row;
            row.style.alignItems = UITK.Align.Center;
            row.style.height = 50;
            row.style.marginBottom = 10;
            row.style.backgroundColor = new UITK.StyleColor(RowBg);
            row.style.paddingLeft = 12;
            row.style.paddingRight = 10;
            row.style.paddingTop = 8;
            row.style.paddingBottom = 8;

            // Player name + date info (left side, fixed width)
            var nameContainer = new UITK.VisualElement();
            nameContainer.style.flexDirection = UITK.FlexDirection.Column;
            nameContainer.style.minWidth = 200;
            nameContainer.style.maxWidth = 200;
            nameContainer.style.justifyContent = UITK.Justify.Center;

            var nameLabel = new UITK.Label(player.playerName);
            nameLabel.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleLeft);
            nameLabel.style.color = new UITK.StyleColor(P_White);
            nameLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            nameLabel.style.whiteSpace = UITK.WhiteSpace.NoWrap;
            nameLabel.style.overflow = UITK.Overflow.Hidden;
            nameLabel.style.marginBottom = 2;
            MakeReadable(nameLabel);

            var dateLabel = new UITK.Label($"Last Seen: {player.lastSeen:MM/dd/yy HH:mm:ss}");
            dateLabel.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleLeft);
            dateLabel.style.color = new UITK.StyleColor(Color.gray);
            dateLabel.style.fontSize = 12;
            dateLabel.style.whiteSpace = UITK.WhiteSpace.NoWrap;
            MakeReadable(dateLabel);

            nameContainer.Add(nameLabel);
            nameContainer.Add(dateLabel);
            row.Add(nameContainer);

            // Middle section: Volume slider (same as regular rows)
            var middleSection = new UITK.VisualElement();
            middleSection.style.flexDirection = UITK.FlexDirection.Row;
            middleSection.style.alignItems = UITK.Align.Center;
            middleSection.style.flexGrow = 1;

            if (ulong.TryParse(player.steamId, out var steamIdUlong))
            {
                var volume = PoncePuck.LocalMute.LocalMuteStore.GetVoiceVolume(steamIdUlong);

                var volumeSlider = new UITK.Slider(0f, 200f) { value = volume };
                volumeSlider.style.flexGrow = 1;
                volumeSlider.style.flexBasis = 0;
                volumeSlider.style.marginLeft = 6;
                volumeSlider.style.maxWidth = 300;

                StyleSliderLikeBase(volumeSlider);

                var volumeValueField = new UITK.TextField();
                volumeValueField.value = $"{volume}";
                MakeReadable(volumeValueField);
                volumeValueField.style.minWidth = 60;
                volumeValueField.style.maxWidth = 60;
                volumeValueField.style.unityTextAlign = TextAnchor.MiddleRight;

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

                middleSection.Add(volumeValueField);
                middleSection.Add(volumeSlider);
            }

            row.Add(middleSection);

            // Right section: Different buttons for recent players (Block/Saved instead of Remove/Notes)
            var rightSection = new UITK.VisualElement();
            rightSection.style.flexDirection = UITK.FlexDirection.Row;
            rightSection.style.alignItems = UITK.Align.Center;
            rightSection.style.flexShrink = 0;

            // Info button
            var infoBtn = new UITK.Button(() => ShowInformationDialog(player));
            infoBtn.text = "INFO";
            StyleRowButton(infoBtn, BTN_W, "INFO");
            infoBtn.style.backgroundColor = new UITK.StyleColor(ButtonBg);
            infoBtn.style.width = 80;
            rightSection.Add(infoBtn);

            // Block button
            var blockBtn = new UITK.Button(() =>
            {
                // Block: mute the player AND add to social blocked list
                if (ulong.TryParse(player.steamId, out ulong sid))
                {
                    PoncePuck.LocalMute.LocalMuteStore.ToggleFullMute(sid, true);
                }
                PoncePuck.LocalMute.LocalMuteStore.AddToBlocked(player.steamId, player.playerName, player.profileUrl);
                PoncePuck.LocalMute.LocalMuteStore.RemoveFromRecent(player.steamId);
                RefreshPanelFields();

                // Force immediate scoreboard refresh
                if (ulong.TryParse(player.steamId, out ulong sid2))
                {
                    ScoreboardUtil.RefreshRowForSteamId(sid2);
                }
            });
            StyleRowButton(blockBtn, BTN_W, "BLOCK");
            blockBtn.style.backgroundColor = new UITK.StyleColor(ButtonBg);
            rightSection.Add(blockBtn);

            // Save button
            var saveBtn = new UITK.Button(() =>
            {
                PoncePuck.LocalMute.LocalMuteStore.AddToSaved(player.steamId, player.playerName, player.profileUrl);
                PoncePuck.LocalMute.LocalMuteStore.RemoveFromRecent(player.steamId);
                RefreshPanelFields();

                // Force immediate scoreboard refresh
                if (ulong.TryParse(player.steamId, out ulong sid))
                {
                    ScoreboardUtil.RefreshRowForSteamId(sid);
                }
            });
            StyleRowButton(saveBtn, BTN_W, "SAVE");
            saveBtn.style.backgroundColor = new UITK.StyleColor(ButtonBg);
            saveBtn.style.width = 140;
            rightSection.Add(saveBtn);

            row.Add(rightSection);

            return row;
        }

        private void ShowNotesDialog(PoncePuck.LocalMute.PlayerInfo player)
        {
            // Create a simple inline notes editor
            if (_activeNotesPlayer != null) return; // Only one at a time

            _activeNotesPlayer = player;
            _notesOverlay = new UITK.VisualElement();
            _notesOverlay.style.position = UITK.Position.Absolute;
            _notesOverlay.style.left = 0;
            _notesOverlay.style.top = 0;
            _notesOverlay.style.right = 0;
            _notesOverlay.style.bottom = 0;
            _notesOverlay.style.backgroundColor = new UITK.StyleColor(new Color(0, 0, 0, 0.7f));
            _notesOverlay.style.alignItems = UITK.Align.Center;
            _notesOverlay.style.justifyContent = UITK.Justify.FlexStart;

            var notesPanel = new UITK.VisualElement();
            notesPanel.style.width = 600;
            notesPanel.style.minHeight = 300;
            // Use dynamic panel background color
            var panelBg = GetMainPanelBackgroundColor() ?? new Color32(50, 50, 50, 255); // Fully opaque fallback
            notesPanel.style.backgroundColor = new UITK.StyleColor(panelBg);
            notesPanel.style.paddingLeft = 8;
            notesPanel.style.paddingRight = 8;
            notesPanel.style.paddingTop = 8;
            notesPanel.style.paddingBottom = 8;

            var titleLabel = new UITK.Label($"NOTES FOR {player.playerName.ToUpper()}");
            MakeReadable(titleLabel);
            titleLabel.style.fontSize = 18;
            titleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            titleLabel.style.marginBottom = 16;
            titleLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            notesPanel.Add(titleLabel);

            var notesField = new UITK.TextField();
            notesField.value = player.notes ?? "";
            notesField.multiline = true;
            notesField.style.height = 300;
            notesField.style.marginLeft = 8;
            notesField.style.marginRight = 8;
            notesField.style.marginTop = 8;
            notesField.style.marginBottom = 8;
            MakeReadable(notesField);
            notesField.style.backgroundColor = new UITK.StyleColor(new Color(0.1f, 0.1f, 0.1f, 0.8f));
            notesField.style.paddingLeft = 8;
            notesField.style.paddingRight = 8;
            notesField.style.paddingTop = 8;
            notesField.style.paddingBottom = 8;
            notesPanel.Add(notesField);

            var buttonRow = new UITK.VisualElement();
            buttonRow.style.flexDirection = UITK.FlexDirection.Row;
            buttonRow.style.justifyContent = UITK.Justify.FlexEnd;
            buttonRow.style.marginTop = 8;

            var cancelBtn = new UITK.Button(() =>
            {
                CloseNotesDialog();
            });
            cancelBtn.text = "CANCEL";
            cancelBtn.style.color = new UITK.StyleColor(Color.white);
            cancelBtn.style.backgroundColor = new UITK.StyleColor(ButtonBg);
            cancelBtn.style.marginRight = 8;
            cancelBtn.style.width = BTN_W;
            cancelBtn.style.height = BTN_H;
            cancelBtn.style.paddingLeft = 4;
            cancelBtn.style.paddingRight = 4;
            cancelBtn.style.paddingTop = 4;
            cancelBtn.style.paddingBottom = 4;
            AddButtonFlash(cancelBtn);
            buttonRow.Add(cancelBtn);

            var saveBtn = new UITK.Button(() =>
            {
                PoncePuck.LocalMute.LocalMuteStore.UpdatePlayerNotes(player.steamId, notesField.value);
                player.notes = notesField.value; // Update local copy
                CloseNotesDialog();
                RefreshPanelFields();
            });
            saveBtn.text = "SAVE";
            saveBtn.style.color = new UITK.StyleColor(Color.white);
            saveBtn.style.backgroundColor = new UITK.StyleColor(ButtonBg);
            saveBtn.style.width = BTN_W;
            saveBtn.style.height = BTN_H;
            saveBtn.style.paddingLeft = 4;
            saveBtn.style.paddingRight = 4;
            saveBtn.style.paddingTop = 4;
            saveBtn.style.paddingBottom = 4;
            AddButtonFlash(saveBtn);
            buttonRow.Add(saveBtn);

            notesPanel.Add(buttonRow);
            _notesOverlay.Add(notesPanel);

            // Add to the root panel
            if (_ppkbPanel != null)
            {
                _ppkbPanel.Add(_notesOverlay);
            }

            // Focus the text field
            notesField.Focus();
        }

        private PoncePuck.LocalMute.PlayerInfo _activeNotesPlayer;
        private UITK.VisualElement _notesOverlay;
        private PoncePuck.LocalMute.PlayerInfo _activeInfoPlayer;
        private UITK.VisualElement _infoOverlay;

        private void CloseNotesDialog()
        {
            if (_notesOverlay != null)
            {
                _notesOverlay.RemoveFromHierarchy();
                _notesOverlay = null;
            }
            _activeNotesPlayer = null;
        }

        private void ShowInformationDialog(PoncePuck.LocalMute.PlayerInfo player)
        {
            // Only one information dialog at a time
            if (_activeInfoPlayer != null) return;

            _activeInfoPlayer = player;
            _infoOverlay = new UITK.VisualElement();
            _infoOverlay.style.position = UITK.Position.Absolute;
            _infoOverlay.style.left = 0;
            _infoOverlay.style.top = 0;
            _infoOverlay.style.right = 0;
            _infoOverlay.style.bottom = 0;
            _infoOverlay.style.backgroundColor = new UITK.StyleColor(new Color(0, 0, 0, 0.7f));
            _infoOverlay.style.alignItems = UITK.Align.Center;
            _infoOverlay.style.justifyContent = UITK.Justify.Center;

            var infoPanel = new UITK.VisualElement();
            infoPanel.style.width = 1000;
            infoPanel.style.height = 605;
            // Use dynamic panel background color
            var panelBg = GetMainPanelBackgroundColor() ?? new Color32(50, 50, 50, 255); // Fully opaque fallback
            infoPanel.style.backgroundColor = new UITK.StyleColor(panelBg);
            infoPanel.style.paddingLeft = 8;
            infoPanel.style.paddingRight = 8;
            infoPanel.style.paddingTop = 8;
            infoPanel.style.paddingBottom = 8;

            // Title
            var titleLabel = new UITK.Label($"PLAYER INFORMATION");
            MakeReadable(titleLabel);
            titleLabel.style.fontSize = 24;
            titleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            titleLabel.style.marginBottom = 8;
            titleLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            infoPanel.Add(titleLabel);

            // Create info section with consistent row styling
            var infoSection = new UITK.VisualElement();
            infoSection.style.backgroundColor = new UITK.StyleColor(RowBg);
            infoSection.style.paddingLeft = 8;
            infoSection.style.paddingRight = 8;
            infoSection.style.paddingTop = 8;
            infoSection.style.paddingBottom = 8;
            infoSection.style.marginBottom = 8;
            infoSection.style.marginLeft = 8;
            infoSection.style.marginRight = 8;
            infoSection.style.marginTop = 8;

            // Player name with number next to it
            string displayNumber = string.IsNullOrEmpty(player.playerNumber) ? "?" : player.playerNumber;
            
            // Row 1: Player name/number on left, Date on right
            var row1 = new UITK.VisualElement();
            row1.style.flexDirection = UITK.FlexDirection.Row;
            row1.style.justifyContent = UITK.Justify.SpaceBetween;
            row1.style.marginBottom = 8;
            
            var nameNumLabel = MakeHighlightable($"#{displayNumber} {player.playerName.ToUpper()}", 18, true);
            MakeReadable(nameNumLabel);
            row1.Add(nameNumLabel);

            var dateLabel = MakeHighlightable($"Added: {player.dateAdded:MM/dd/yy HH:mm:ss} | Last Seen: {player.lastSeen:MM/dd/yy HH:mm:ss}", 18, true);
            MakeReadable(dateLabel);
            row1.Add(dateLabel);
            
            infoSection.Add(row1);

            // Row 2: Last server on left, Steam ID on right
            var row2 = new UITK.VisualElement();
            row2.style.flexDirection = UITK.FlexDirection.Row;
            row2.style.justifyContent = UITK.Justify.SpaceBetween;
            
            if (!string.IsNullOrEmpty(player.lastServerSeen))
            {
                var serverLabel = MakeHighlightable($"LAST SERVER: {player.lastServerSeen}", 18, true);
                MakeReadable(serverLabel);
                row2.Add(serverLabel);
            }
            else
            {
                // Add empty element to maintain layout
                var spacer = new UITK.VisualElement();
                row2.Add(spacer);
            }

            var steamIdLabel = MakeHighlightable($"STEAM ID: {player.steamId}", 18, true);
            MakeReadable(steamIdLabel);
            row2.Add(steamIdLabel);
            
            infoSection.Add(row2);
            infoPanel.Add(infoSection);

                // Notes section with toggle between edit/view mode
                var notesHeaderLabel = new UITK.Label("NOTES:");
                MakeReadable(notesHeaderLabel);
                notesHeaderLabel.style.fontSize = 18;
                notesHeaderLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
                notesHeaderLabel.style.marginBottom = 8;
                notesHeaderLabel.style.marginLeft = 8;
                notesHeaderLabel.style.marginRight = 8;
                infoPanel.Add(notesHeaderLabel);

                // Container for notes (will hold either TextField or Label)
                var notesContainer = new UITK.VisualElement();
                notesContainer.style.flexGrow = 1; // Take up remaining space
                notesContainer.style.marginBottom = 8;
                notesContainer.style.marginLeft = 8;
                notesContainer.style.marginRight = 8;
                notesContainer.style.backgroundColor = new UITK.StyleColor(new Color(0.1f, 0.1f, 0.1f, 0.8f));
                notesContainer.style.paddingLeft = 8;
                notesContainer.style.paddingRight = 8;
                notesContainer.style.paddingTop = 8;
                notesContainer.style.paddingBottom = 8;

                // Create both TextField and Label (switch between them)
                var notesField = new UITK.TextField();
                notesField.multiline = true;
                notesField.style.height = new UITK.StyleLength(new UITK.Length(100, UITK.LengthUnit.Percent));
                notesField.style.whiteSpace = UITK.WhiteSpace.Normal;
                notesField.style.unityTextAlign = TextAnchor.UpperLeft;
                notesField.style.backgroundColor = UITK.StyleKeyword.Null; // Transparent, container has bg
                MakeReadable(notesField);

                var notesLabel = new UITK.Label();
                notesLabel.enableRichText = true;
                notesLabel.style.height = new UITK.StyleLength(new UITK.Length(100, UITK.LengthUnit.Percent));
                notesLabel.style.whiteSpace = UITK.WhiteSpace.Normal;
                notesLabel.style.unityTextAlign = TextAnchor.UpperLeft;
                MakeReadable(notesLabel);

                bool isEditing = false;
                UITK.Button saveEditBtn = null;

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
                                input.style.whiteSpace = UITK.WhiteSpace.Normal;
                            }
                        }).ExecuteLater(0);
                    }
                };

                // Start in view mode
                notesLabel.text = player.notes ?? "";
                notesContainer.Add(notesLabel);
                infoPanel.Add(notesContainer);

                // Button row (Profile, Save/Edit, and Close)
                var buttonRow = new UITK.VisualElement();
                buttonRow.style.flexDirection = UITK.FlexDirection.Row;
                buttonRow.style.justifyContent = UITK.Justify.Center;
                buttonRow.style.marginLeft = 8;
                buttonRow.style.marginRight = 8;

                var profileBtn = new UITK.Button(() =>
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
                                Debug.LogWarning($"[PPKB] Invalid Steam ID for profile: {player.steamId}");
                            }
                        }
                        else
                        {
                            Debug.LogWarning($"[PPKB] No Steam ID available for player");
                        }
                    }
                    catch (System.Exception e)
                    {
                        Debug.LogError($"[PPKB] Failed to open Steam profile overlay: {e.Message}");
                    }
                });
                StyleRowButton(profileBtn, BTN_W, "PROFILE");
                profileBtn.text = "PROFILE";
                profileBtn.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
                profileBtn.style.height = 50;
                profileBtn.style.paddingLeft = 18; profileBtn.style.paddingRight = 18;
                profileBtn.style.backgroundColor = new UITK.StyleColor(ButtonBg);
                AddButtonFlash(profileBtn);
                buttonRow.Add(profileBtn);

                // Save/Edit toggle button
                saveEditBtn = new UITK.Button(() =>
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
                StyleRowButton(saveEditBtn, BTN_W, "EDIT");
                saveEditBtn.text = "EDIT"; // Start with EDIT since we begin in view mode
                saveEditBtn.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
                saveEditBtn.style.height = 50;
                saveEditBtn.style.paddingLeft = 18; saveEditBtn.style.paddingRight = 18;
                saveEditBtn.style.backgroundColor = new UITK.StyleColor(ButtonBg);
                AddButtonFlash(saveEditBtn);
                buttonRow.Add(saveEditBtn);

                var closeBtn = new UITK.Button(() =>
                {
                    CloseInformationDialog();
                });
                StyleRowButton(closeBtn, BTN_W, "CLOSE");
                closeBtn.text = "CLOSE";
                closeBtn.style.unityTextAlign = new UITK.StyleEnum<TextAnchor>(TextAnchor.MiddleCenter);
                closeBtn.style.height = 50;
                closeBtn.style.paddingLeft = 18; closeBtn.style.paddingRight = 18;
                closeBtn.style.marginRight = 8;
                closeBtn.style.backgroundColor = new UITK.StyleColor(ButtonBg);
                AddButtonFlash(closeBtn);
                buttonRow.Add(closeBtn);

                infoPanel.Add(buttonRow);

                _infoOverlay.Add(infoPanel);

                // Add to the root panel
                if (_ppkbPanel != null)
                {
                    _ppkbPanel.Add(_infoOverlay);
                }
            }

        private void CloseInformationDialog()
        {
            if (_infoOverlay != null)
            {
                _infoOverlay.RemoveFromHierarchy();
                _infoOverlay = null;
            }
            _activeInfoPlayer = null;
        }

        // Make a TextField behave like a highlightable/copyable label
        private UITK.TextField MakeHighlightable(string text, int fontSize = 16, bool bold = true)
        {
            var field = new UITK.TextField();
            field.value = text;
            field.isReadOnly = true; // Make it read-only so it acts like a label
            field.style.fontSize = fontSize;
            field.style.marginBottom = 8;
            
            if (bold)
            {
                field.style.unityFontStyleAndWeight = FontStyle.Bold;
            }
            
            MakeReadable(field);
            
            return field;
        }
    }
}




