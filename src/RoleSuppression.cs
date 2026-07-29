// File: PoncePuck.Keybinds/KeybindRunner.RoleSuppression.cs
// Target: .NET Framework 4.8, C# 7.3

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UIElements;

namespace PoncePuck.Keybinds
{
    internal enum PressPhase { Down, Held, Up }
    public sealed partial class KeybindRunner : MonoBehaviour
    {
        // ---------- SettingsManager reflection cache ----------
        // In B310, SettingsManager is a static class (no instance needed)
        private static Type _cachedSettingsManagerType;

        // ---------- role probe cache ----------
        private bool _cachedIsGoalie;
        private HockeyPosition _cachedPosition = HockeyPosition.None;
        private UnityEngine.Object _cachedPlayerObj;
        
        // Position settings tracking
        private HockeyPosition _lastAppliedPosition = HockeyPosition.None;
        
        // Base settings - stored once at startup from your actual game settings
        private bool _hasBaseSettings;
        private float _baseFOV = 90f, _baseCameraAngle = 30f, _baseStickSens = 0.2f, _baseLookSens = 0.2f;
        private float _baseChatOpacity = 1f, _baseChatScale = 1f;
        private float _baseMinimapOpacity = 1f, _baseMinimapBgOpacity = 0.5f;
        // H position default 100 = right edge, V default 0 = top; values are percent of screen, not 0-1.
        private float _baseMinimapHPos = 100f, _baseMinimapVPos = 0f, _baseMinimapScale = 1f;
        private string _baseHandedness = "Right";
        
        // Track which settings are currently overridden
        private bool _appliedFOV, _appliedCameraAngle, _appliedHandedness, _appliedStickSens, _appliedLookSens;
        private bool _appliedChatOpacity, _appliedChatScale;
        private bool _appliedMinimapOpacity, _appliedMinimapBgOpacity, _appliedMinimapHPos, _appliedMinimapVPos, _appliedMinimapScale;

        // ---------- keyboard hook state ----------
        private bool _suppressionHooked;
        private bool _posSelHooked;
        private bool _eventHooked;

        // ---------- UI hooks for suppression refresh ----------
        private void EnsurePositionSelectHook()
        {
            try
            {
                if (!_posSelHooked)
                {
                    InputManager.PositionSelectAction.performed += _ => { RefreshSuppression(); };
                    _posSelHooked = true;
                }
            }
            catch (Exception ex) { DebugLog($"[PPKB] EnsurePositionSelectHook error: {ex.Message}"); }
            
            // Also hook into EventManager for more reliable position change detection
            HookPositionChangeEvents();
        }
        
        private void HookPositionChangeEvents()
        {
            if (_eventHooked) return;
            
            try
            {
                // The event is "Event_OnRoleChanged" - NOT "Event_OnPlayerRoleChanged", and there is
                // no "Event_OnPlayerPositionChanged" at all. Those two names were carried over from
                // an older build and do not exist in b1117 (verified against the string table in
                // libs/Puck.dll). AddEventListener happily registers an unknown name, so this hook
                // reported success and then never fired once - which is why per-position FOV /
                // sensitivity / minimap overrides silently stopped being applied.
                EventManager.AddEventListener("Event_OnRoleChanged", new Action<Dictionary<string, object>>(OnPlayerRoleChanged));
                EventManager.AddEventListener("Event_OnPlayerStateChanged", new Action<Dictionary<string, object>>(OnPlayerPositionChanged));
                _eventHooked = true;
                Debug.Log("[PPKB] Hooked position change events");
            }
            catch (Exception ex) 
            { 
                Debug.LogWarning($"[PPKB] Failed to hook position events: {ex.Message}"); 
            }
        }
        
        private void OnPlayerRoleChanged(Dictionary<string, object> message)
        {
            try
            {
                // Clear cache so we re-read position
                _cachedPlayerObj = null;
                RefreshSuppression();
            }
            catch (Exception ex) { DebugLog($"[PPKB] OnPlayerRoleChanged error: {ex.Message}"); }
        }
        
        private void OnPlayerPositionChanged(Dictionary<string, object> message)
        {
            try
            {
                // Clear cache so we re-read position
                _cachedPlayerObj = null;
                RefreshSuppression();
            }
            catch (Exception ex) { DebugLog($"[PPKB] OnPlayerPositionChanged error: {ex.Message}"); }
        }
        
        // NOTE: do NOT add a periodic GetCurrentPosition() poll here as a "safety net" for the
        // events. It was tried and it thrashes: GetCurrentPosition returns HockeyPosition.None both
        // when the player genuinely has no position AND when its throttled player scan transiently
        // finds nothing, so polling it turns every scan miss into a fake position change. The result
        // is a ~1Hz Center -> None -> Center loop that restores base settings and re-applies the
        // override forever. Position changes are event-driven on purpose.

        private void UnhookPositionChangeEvents()
        {
            if (!_eventHooked) return;
            
            try
            {
                // EventManager is static in B310
                EventManager.RemoveEventListener("Event_OnPlayerRoleChanged", new Action<Dictionary<string, object>>(OnPlayerRoleChanged));
                EventManager.RemoveEventListener("Event_OnPlayerPositionChanged", new Action<Dictionary<string, object>>(OnPlayerPositionChanged));
                _eventHooked = false;
            }
            catch { }
        }


        // ---------- reflection cache for Player ----------
        private static Type _tPlayer;
        private static PropertyInfo _piIsOwner, _piIsLocal, _piRole, _piRoleValue;

        private void ResolvePlayerReflection()
        {
            if (_tPlayer != null) return;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    _tPlayer = asm.GetType("Player", false) ?? asm.GetTypes().FirstOrDefault(x => x.Name == "Player");
                    if (_tPlayer != null) break;
                }
                catch { }
            }
            if (_tPlayer == null) return;

            _piIsOwner = _tPlayer.GetProperty("IsOwner", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _piIsLocal = _tPlayer.GetProperty("IsLocalPlayer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            var fiRole = _tPlayer.GetField("Role", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _piRole = _tPlayer.GetProperty("Role", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (fiRole != null)
            {
                var roleType = fiRole.FieldType;
                _piRoleValue = roleType?.GetProperty("Value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }
        }

        private bool IsLocalOrOwner(object p)
        {
            try
            {
                if (_piIsOwner != null && (bool)(_piIsOwner.GetValue(p, null) ?? false)) return true;
                if (_piIsLocal != null && (bool)(_piIsLocal.GetValue(p, null) ?? false)) return true;
            }
            catch { }
            return false;
        }

        private bool TryReadRole(object p, out bool isGoalie)
        {
            isGoalie = false;
            try
            {
                object role = null;
                if (_piRole != null) role = _piRole.GetValue(p, null);
                if (role == null)
                {
                    var fiRole = _tPlayer.GetField("Role", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (fiRole != null) role = fiRole.GetValue(p);
                    if (role != null && _piRoleValue != null) role = _piRoleValue.GetValue(role, null);
                }
                var name = role?.ToString() ?? "";
                if (name.IndexOf("Goalie", StringComparison.OrdinalIgnoreCase) >= 0) { isGoalie = true; return true; }
                if (name.IndexOf("Attack", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Skater", StringComparison.OrdinalIgnoreCase) >= 0) { isGoalie = false; return true; }
            }
            catch { }
            return false;
        }

        private bool TryReadPosition(object p, out HockeyPosition position)
        {
            position = HockeyPosition.None;
            try
            {
                // Team-first check: PlayerTeam.Spectator means the user is spectating,
                // even if they previously had a PlayerPosition assigned.
                var piTeam = _tPlayer.GetProperty("Team", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (piTeam != null)
                {
                    var teamVal = piTeam.GetValue(p, null);
                    if (teamVal != null && string.Equals(teamVal.ToString(), "Spectator", StringComparison.OrdinalIgnoreCase))
                    {
                        position = HockeyPosition.Spectator;
                        return true;
                    }
                }

                // First, try to read PlayerPosition.Name for specific position (LW, C, RW, LD, RD, G)
                var fiPlayerPosition = _tPlayer.GetField("PlayerPosition", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (fiPlayerPosition != null)
                {
                    var playerPosition = fiPlayerPosition.GetValue(p);
                    if (playerPosition != null)
                    {
                        var tPlayerPosition = playerPosition.GetType();
                        var fiName = tPlayerPosition.GetField("Name", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (fiName != null)
                        {
                            var posName = fiName.GetValue(playerPosition)?.ToString() ?? "";
                            if (!string.IsNullOrEmpty(posName))
                            {
                                // Check for specific positions by name
                                if (posName.Equals("G", StringComparison.OrdinalIgnoreCase) || posName.IndexOf("Goalie", StringComparison.OrdinalIgnoreCase) >= 0) 
                                { position = HockeyPosition.Goalie; return true; }
                                if (posName.Equals("LW", StringComparison.OrdinalIgnoreCase) || posName.IndexOf("LeftWing", StringComparison.OrdinalIgnoreCase) >= 0 || posName.IndexOf("Left Wing", StringComparison.OrdinalIgnoreCase) >= 0) 
                                { position = HockeyPosition.LeftWing; return true; }
                                if (posName.Equals("RW", StringComparison.OrdinalIgnoreCase) || posName.IndexOf("RightWing", StringComparison.OrdinalIgnoreCase) >= 0 || posName.IndexOf("Right Wing", StringComparison.OrdinalIgnoreCase) >= 0) 
                                { position = HockeyPosition.RightWing; return true; }
                                if (posName.Equals("C", StringComparison.OrdinalIgnoreCase) || posName.IndexOf("Center", StringComparison.OrdinalIgnoreCase) >= 0) 
                                { position = HockeyPosition.Center; return true; }
                                if (posName.Equals("LD", StringComparison.OrdinalIgnoreCase) || posName.IndexOf("LeftDefense", StringComparison.OrdinalIgnoreCase) >= 0 || posName.IndexOf("Left Defense", StringComparison.OrdinalIgnoreCase) >= 0) 
                                { position = HockeyPosition.LeftDefense; return true; }
                                if (posName.Equals("RD", StringComparison.OrdinalIgnoreCase) || posName.IndexOf("RightDefense", StringComparison.OrdinalIgnoreCase) >= 0 || posName.IndexOf("Right Defense", StringComparison.OrdinalIgnoreCase) >= 0) 
                                { position = HockeyPosition.RightDefense; return true; }
                            }
                        }
                    }
                }
                
                // Fallback: read Role enum (only gives Attacker/Goalie)
                object role = null;
                if (_piRole != null) role = _piRole.GetValue(p, null);
                if (role == null)
                {
                    var fiRole = _tPlayer.GetField("Role", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (fiRole != null) role = fiRole.GetValue(p);
                    if (role != null && _piRoleValue != null) role = _piRoleValue.GetValue(role, null);
                }
                var roleName = role?.ToString() ?? "";
                
                // Role enum only has Goalie/Attacker, so we can only detect goalie here
                if (roleName.IndexOf("Goalie", StringComparison.OrdinalIgnoreCase) >= 0) 
                { position = HockeyPosition.Goalie; return true; }
            }
            catch { }
            return false;
        }

        // PERFORMANCE: Cache player scan results to avoid expensive FindObjectsOfTypeAll
        private static float _nextPlayerScanTime;
        private const float PLAYER_SCAN_INTERVAL = 0.5f; // Scan every 0.5 seconds for faster position detection
        
        private HockeyPosition GetCurrentPosition()
        {
            try
            {
                ResolvePlayerReflection();
                if (_tPlayer == null) { return HockeyPosition.None; }

                // Try cached player first (fast path)
                if (_cachedPlayerObj != null)
                {
                    if (TryReadPosition(_cachedPlayerObj, out HockeyPosition pos))
                    {
                        _cachedPosition = pos; 
                        _cachedIsGoalie = (pos == HockeyPosition.Goalie);
                        return pos;
                    }
                    _cachedPlayerObj = null; // fell through, re-scan below
                }

                // PERFORMANCE: Throttle expensive player scan
                float now = Time.unscaledTime;
                if (now < _nextPlayerScanTime)
                {
                    return _cachedPosition; // Return last known position
                }
                _nextPlayerScanTime = now + PLAYER_SCAN_INTERVAL;

                // Scan for local player using FindObjectsByType (faster than Resources.FindObjectsOfTypeAll)
                var players = UnityEngine.Object.FindObjectsByType(_tPlayer, FindObjectsInactive.Exclude, FindObjectsSortMode.None);
                foreach (var p in players)
                {
                    if (!IsLocalOrOwner(p)) continue;
                    if (TryReadPosition(p, out HockeyPosition pos))
                    {
                        _cachedPlayerObj = p;
                        _cachedPosition = pos;
                        _cachedIsGoalie = (pos == HockeyPosition.Goalie);
                        return pos;
                    }
                }
            }
            catch (Exception ex) { DebugLog($"[PPKB] GetCurrentPosition error: {ex.Message}"); }

            return HockeyPosition.None;
        }

        private bool IsGoalieNow()
        {
            var pos = GetCurrentPosition();
            return pos == HockeyPosition.Goalie;
        }

        // Force re-apply position settings for the current position (called when settings are modified)
        public void ReapplyCurrentPositionSettings()
        {
            try
            {
                var position = GetCurrentPosition();
                if (position == HockeyPosition.None) return;
                
                bool hasOverrides = _cmd != null && _cmd.positionSettings != null && 
                    _cmd.positionSettings.Exists(e => e.position == position);
                
                if (hasOverrides)
                {
                    ApplyPositionSettings(position);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PPKB] Failed to reapply position settings: {ex.Message}");
            }
        }

        // ---------- suppression computation + application ----------
        private void RefreshSuppression()
        {
            // Get current position
            var position = GetCurrentPosition();
            bool goalie = (position == HockeyPosition.Goalie);

            // Hook keyboard event for ESC key blocking
            HookKeyboardEvent(true);
            
            // Handle position changes (including changing TO None when disconnecting)
            if (_lastAppliedPosition != position)
            {
                HockeyPosition previousPosition = _lastAppliedPosition;
                Debug.Log($"[PPKB] Position changed: {previousPosition} -> {position}");
                
                // Load base settings once (from PlayerPrefs - your actual saved game settings)
                LoadBaseSettingsFromConfig();
                
                // Check if PREVIOUS position had overrides (we need to restore when LEAVING)
                bool previousHadOverrides = previousPosition != HockeyPosition.None && 
                    _cmd != null && _cmd.positionSettings != null && 
                    _cmd.positionSettings.Exists(e => e.position == previousPosition);
                
                // Check if NEW position has overrides
                bool newHasOverrides = position != HockeyPosition.None &&
                    _cmd != null && _cmd.positionSettings != null && 
                    _cmd.positionSettings.Exists(e => e.position == position);
                
                // Restore base settings when LEAVING a position that had overrides
                bool anyApplied = _appliedFOV || _appliedCameraAngle || _appliedHandedness || _appliedStickSens || _appliedLookSens ||
                                  _appliedChatOpacity || _appliedChatScale || 
                                  _appliedMinimapOpacity || _appliedMinimapBgOpacity || _appliedMinimapHPos || _appliedMinimapVPos || _appliedMinimapScale;
                if (previousHadOverrides && anyApplied)
                {
                    Debug.Log($"[PPKB] Restoring base settings (previous position {previousPosition} had overrides, now at {position})");
                    RestoreOverriddenSettings();
                }
                
                // Update last applied position BEFORE applying new settings
                _lastAppliedPosition = position;
                
                // Apply new position's settings if it has any (with a small delay for game to stabilize)
                if (newHasOverrides)
                {
                    StartCoroutine(ApplyPositionSettingsDelayed(position, 0.5f));
                }
            }
        }
        
        private System.Collections.IEnumerator ApplyPositionSettingsDelayed(HockeyPosition position, float delay)
        {
            yield return new WaitForSeconds(delay);
            
            // Re-check that we're still in this position
            var currentPos = GetCurrentPosition();
            if (currentPos == position)
            {
                ApplyPositionSettings(position);
            }
        }

        // ---------- low-level Q/E dive suppression + ESC blocking ----------
        private void HookKeyboardEvent(bool on)
        {
            if (on == _suppressionHooked) return;
            try
            {
                if (on) InputSystem.onEvent += OnAnyInputEvent;
                else InputSystem.onEvent -= OnAnyInputEvent;
                _suppressionHooked = on;
            }
            catch { }
        }

        private void OnAnyInputEvent(InputEventPtr eventPtr, InputDevice device)
        {
            // Only react to keyboard state events for THIS keyboard.
            var kb = device as Keyboard;
            if (kb == null) return;
            if (!eventPtr.valid) return;
            if (!(eventPtr.IsA<StateEvent>() || eventPtr.IsA<DeltaStateEvent>())) return;
            if (eventPtr.deviceId != kb.deviceId) return;

            // Block ESC key when panel is open (unless capturing keybinds)
            var escKey = kb.escapeKey;
            if (escKey != null)
            {
                float escValue = 0f;
                bool gotEsc = false;
                try { gotEsc = escKey.ReadValueFromEvent(eventPtr, out escValue); } catch { gotEsc = false; }
                
                if (gotEsc && escValue > 0.5f) // ESC key pressed
                {
                    // Don't block ESC - let Update() handle closing the panel
                    // This allows the panel's ESC handler to work properly
                    // Also block ESC if standalone info dialog is open
                    try
                    {
                        var uiDoc = UnityEngine.Object.FindFirstObjectByType<UnityEngine.UIElements.UIDocument>(UnityEngine.FindObjectsInactive.Include);
                        if (uiDoc != null && uiDoc.rootVisualElement != null)
                        {
                            var overlay = uiDoc.rootVisualElement.Q("StandalonePlayerInfoOverlay");
                            // Check if overlay exists and is in the hierarchy (parent != null means it's attached)
                            if (overlay != null && overlay.parent != null)
                            {
                                eventPtr.handled = true;
                                return;
                            }
                        }
                    }
                    catch { }
                }
            }
        }

        // ---------- net: hello (stub, actions removed) ----------
        private void SendHelloRoleAware()
        {
            // Action bindings removed - this is now a no-op stub
        }

        private Type GetSettingsManagerType()
        {
            if (_cachedSettingsManagerType != null) return _cachedSettingsManagerType;
            
            _cachedSettingsManagerType = Type.GetType("SettingsManager, Assembly-CSharp") 
                ?? AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch (Exception ex) { DebugLog($"[PPKB] Error loading types from assembly: {ex.Message}"); return new Type[0]; } })
                    .FirstOrDefault(t => t.Name == "SettingsManager");
            return _cachedSettingsManagerType;
        }
        
        // B310: SettingsManager is now a static class, no instance needed.
        // This helper returns a non-null sentinel so existing null-checks pass,
        // but all reflection lookups use BindingFlags.Static and pass null for instance.
        private object GetSettingsManagerInstance(Type tSettingsManager)
        {
            if (tSettingsManager == null) return null;
            return tSettingsManager; // return the type itself as a non-null sentinel
        }
        
        // ---------- Position-specific settings application ----------
        // Capture current game settings as base values to restore later
        private void LoadBaseSettingsFromConfig()
        {
            if (_hasBaseSettings) return;
            
            try
            {
                // Capture current game settings as base values
                _baseFOV = GetCurrentFOV();
                _baseCameraAngle = GetCurrentCameraAngle();
                _baseHandedness = GetCurrentHandedness();
                _baseStickSens = GetCurrentStickSensitivity();
                _baseLookSens = GetCurrentLookSensitivity();
                
                // Load base values for chat/minimap settings from game
                _baseChatOpacity = GetCurrentChatOpacity();
                _baseChatScale = GetCurrentChatScale();
                _baseMinimapOpacity = GetCurrentMinimapOpacity();
                _baseMinimapBgOpacity = GetCurrentMinimapBackgroundOpacity();
                _baseMinimapHPos = GetCurrentMinimapHorizontalPosition();
                _baseMinimapVPos = GetCurrentMinimapVerticalPosition();
                _baseMinimapScale = GetCurrentMinimapScale();
                
                _hasBaseSettings = true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PPKB] Failed to load base settings: {ex.Message}");
            }
        }
        
        // Apply settings configured for this position
        private void ApplyPositionSettings(HockeyPosition position)
        {
            try
            {
                if (_cmd == null || _cmd.positionSettings == null || _cmd.positionSettings.Count == 0)
                {
                    return;
                }
                
                // Reset applied flags
                _appliedFOV = _appliedCameraAngle = _appliedHandedness = _appliedStickSens = _appliedLookSens = false;
                _appliedChatOpacity = _appliedChatScale = false;
                _appliedMinimapOpacity = _appliedMinimapBgOpacity = _appliedMinimapHPos = _appliedMinimapVPos = _appliedMinimapScale = false;
                
                // Apply FOV only if set for this position
                var fov = _cmd.GetFOVForPosition(position);
                if (fov.HasValue)
                {
                    ApplyFOV(fov.Value, position);
                    _appliedFOV = true;
                }
                
                // Apply Camera Angle only if set for this position
                var angle = _cmd.GetCameraAngleForPosition(position);
                if (angle.HasValue)
                {
                    ApplyCameraAngle(angle.Value, position);
                    _appliedCameraAngle = true;
                }
                
                // Apply Handedness only if set for this position
                var hand = _cmd.GetHandednessForPosition(position);
                if (!string.IsNullOrEmpty(hand))
                {
                    ApplyHandedness(hand, position);
                    _appliedHandedness = true;
                }

                // Apply Stick Sensitivity only if set for this position
                var stickSens = _cmd.GetStickSensitivityForPosition(position);
                if (stickSens.HasValue)
                {
                    ApplyStickSensitivity(stickSens.Value, position);
                    _appliedStickSens = true;
                }

                // Apply Look Sensitivity only if set for this position
                var lookSens = _cmd.GetLookSensitivityForPosition(position);
                if (lookSens.HasValue)
                {
                    ApplyLookSensitivity(lookSens.Value, position);
                    _appliedLookSens = true;
                }
                
                // Apply Chat Opacity only if set for this position
                var chatOpacity = _cmd.GetChatOpacityForPosition(position);
                if (chatOpacity.HasValue)
                {
                    ApplyChatOpacity(chatOpacity.Value, position);
                    _appliedChatOpacity = true;
                }

                // Apply Chat Scale only if set for this position
                var chatScale = _cmd.GetChatScaleForPosition(position);
                if (chatScale.HasValue)
                {
                    ApplyChatScale(chatScale.Value, position);
                    _appliedChatScale = true;
                }

                // Apply Minimap Opacity only if set for this position
                var minimapOpacity = _cmd.GetMinimapOpacityForPosition(position);
                if (minimapOpacity.HasValue)
                {
                    ApplyMinimapOpacity(minimapOpacity.Value, position);
                    _appliedMinimapOpacity = true;
                }

                // Apply Minimap Background Opacity only if set for this position
                var minimapBgOpacity = _cmd.GetMinimapBackgroundOpacityForPosition(position);
                if (minimapBgOpacity.HasValue)
                {
                    ApplyMinimapBackgroundOpacity(minimapBgOpacity.Value, position);
                    _appliedMinimapBgOpacity = true;
                }

                // Apply Minimap Horizontal Position only if set for this position
                var minimapHPos = _cmd.GetMinimapHorizontalPositionForPosition(position);
                if (minimapHPos.HasValue)
                {
                    ApplyMinimapHorizontalPosition(minimapHPos.Value, position);
                    _appliedMinimapHPos = true;
                }

                // Apply Minimap Vertical Position only if set for this position
                var minimapVPos = _cmd.GetMinimapVerticalPositionForPosition(position);
                if (minimapVPos.HasValue)
                {
                    ApplyMinimapVerticalPosition(minimapVPos.Value, position);
                    _appliedMinimapVPos = true;
                }

                // Apply Minimap Scale only if set for this position
                var minimapScale = _cmd.GetMinimapScaleForPosition(position);
                if (minimapScale.HasValue)
                {
                    ApplyMinimapScale(minimapScale.Value, position);
                    _appliedMinimapScale = true;
                }
                
                Debug.Log($"[PPKB] Applied settings for {position}: FOV={_appliedFOV}, Angle={_appliedCameraAngle}, Hand={_appliedHandedness}, Stick={_appliedStickSens}, Look={_appliedLookSens}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PPKB] Failed to apply position settings: {ex.Message}");
            }
        }
        
        // Restore any settings that were overridden back to base (your preferred game settings)
        private void RestoreOverriddenSettings()
        {
            try
            {
                if (_appliedFOV)
                {
                    ApplyFOV(_baseFOV, HockeyPosition.None);
                    Debug.Log($"[PPKB] Restored FOV to base: {_baseFOV}");
                }
                if (_appliedCameraAngle)
                {
                    ApplyCameraAngle(_baseCameraAngle, HockeyPosition.None);
                    Debug.Log($"[PPKB] Restored Camera Angle to base: {_baseCameraAngle}");
                }
                if (_appliedHandedness && !string.IsNullOrEmpty(_baseHandedness))
                {
                    ApplyHandedness(_baseHandedness, HockeyPosition.None);
                    Debug.Log($"[PPKB] Restored Handedness to base: {_baseHandedness}");
                }
                if (_appliedStickSens)
                {
                    ApplyStickSensitivity(_baseStickSens, HockeyPosition.None);
                    Debug.Log($"[PPKB] Restored Stick Sensitivity to base: {_baseStickSens}");
                }
                if (_appliedLookSens)
                {
                    ApplyLookSensitivity(_baseLookSens, HockeyPosition.None);
                    Debug.Log($"[PPKB] Restored Look Sensitivity to base: {_baseLookSens}");
                }
                if (_appliedChatOpacity)
                {
                    ApplyChatOpacity(_baseChatOpacity, HockeyPosition.None);
                    Debug.Log($"[PPKB] Restored Chat Opacity to base: {_baseChatOpacity}");
                }
                if (_appliedChatScale)
                {
                    ApplyChatScale(_baseChatScale, HockeyPosition.None);
                    Debug.Log($"[PPKB] Restored Chat Scale to base: {_baseChatScale}");
                }
                if (_appliedMinimapOpacity)
                {
                    ApplyMinimapOpacity(_baseMinimapOpacity, HockeyPosition.None);
                    Debug.Log($"[PPKB] Restored Minimap Opacity to base: {_baseMinimapOpacity}");
                }
                if (_appliedMinimapBgOpacity)
                {
                    ApplyMinimapBackgroundOpacity(_baseMinimapBgOpacity, HockeyPosition.None);
                    Debug.Log($"[PPKB] Restored Minimap BG Opacity to base: {_baseMinimapBgOpacity}");
                }
                if (_appliedMinimapHPos)
                {
                    ApplyMinimapHorizontalPosition(_baseMinimapHPos, HockeyPosition.None);
                    Debug.Log($"[PPKB] Restored Minimap Horizontal to base: {_baseMinimapHPos}");
                }
                if (_appliedMinimapVPos)
                {
                    ApplyMinimapVerticalPosition(_baseMinimapVPos, HockeyPosition.None);
                    Debug.Log($"[PPKB] Restored Minimap Vertical to base: {_baseMinimapVPos}");
                }
                if (_appliedMinimapScale)
                {
                    ApplyMinimapScale(_baseMinimapScale, HockeyPosition.None);
                    Debug.Log($"[PPKB] Restored Minimap Scale to base: {_baseMinimapScale}");
                }
                
                // Reset flags
                _appliedFOV = _appliedCameraAngle = _appliedHandedness = _appliedStickSens = _appliedLookSens = false;
                _appliedChatOpacity = _appliedChatScale = false;
                _appliedMinimapOpacity = _appliedMinimapBgOpacity = _appliedMinimapHPos = _appliedMinimapVPos = _appliedMinimapScale = false;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PPKB] Failed to restore settings: {ex.Message}");
            }
        }
        
        // Restore a specific setting to base value when deleted from UI while playing that position
        public void RestoreSettingToBase(HockeyPosition position, PositionSettingType settingType)
        {
            try
            {
                // Only restore if we're currently playing this position
                var currentPos = GetCurrentPosition();
                if (currentPos != position)
                {
                    Debug.Log($"[PPKB] Not restoring {settingType} - not currently playing {position} (current: {currentPos})");
                    return;
                }
                
                Debug.Log($"[PPKB] Restoring {settingType} to base for {position}");
                
                switch (settingType)
                {
                    case PositionSettingType.FOV:
                        if (_appliedFOV) { ApplyFOV(_baseFOV, HockeyPosition.None); _appliedFOV = false; }
                        break;
                    case PositionSettingType.CameraAngle:
                        if (_appliedCameraAngle) { ApplyCameraAngle(_baseCameraAngle, HockeyPosition.None); _appliedCameraAngle = false; }
                        break;
                    case PositionSettingType.Handedness:
                        if (_appliedHandedness) { ApplyHandedness(_baseHandedness, HockeyPosition.None); _appliedHandedness = false; }
                        break;
                    case PositionSettingType.StickSensitivity:
                        if (_appliedStickSens) { ApplyStickSensitivity(_baseStickSens, HockeyPosition.None); _appliedStickSens = false; }
                        break;
                    case PositionSettingType.LookSensitivity:
                        if (_appliedLookSens) { ApplyLookSensitivity(_baseLookSens, HockeyPosition.None); _appliedLookSens = false; }
                        break;
                    case PositionSettingType.ChatOpacity:
                        if (_appliedChatOpacity) { ApplyChatOpacity(_baseChatOpacity, HockeyPosition.None); _appliedChatOpacity = false; }
                        break;
                    case PositionSettingType.ChatScale:
                        if (_appliedChatScale) { ApplyChatScale(_baseChatScale, HockeyPosition.None); _appliedChatScale = false; }
                        break;
                    case PositionSettingType.MinimapOpacity:
                        if (_appliedMinimapOpacity) { ApplyMinimapOpacity(_baseMinimapOpacity, HockeyPosition.None); _appliedMinimapOpacity = false; }
                        break;
                    case PositionSettingType.MinimapBackgroundOpacity:
                        if (_appliedMinimapBgOpacity) { ApplyMinimapBackgroundOpacity(_baseMinimapBgOpacity, HockeyPosition.None); _appliedMinimapBgOpacity = false; }
                        break;
                    case PositionSettingType.MinimapHorizontalPosition:
                        if (_appliedMinimapHPos) { ApplyMinimapHorizontalPosition(_baseMinimapHPos, HockeyPosition.None); _appliedMinimapHPos = false; }
                        break;
                    case PositionSettingType.MinimapVerticalPosition:
                        if (_appliedMinimapVPos) { ApplyMinimapVerticalPosition(_baseMinimapVPos, HockeyPosition.None); _appliedMinimapVPos = false; }
                        break;
                    case PositionSettingType.MinimapScale:
                        if (_appliedMinimapScale) { ApplyMinimapScale(_baseMinimapScale, HockeyPosition.None); _appliedMinimapScale = false; }
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PPKB] Failed to restore {settingType} to base: {ex.Message}");
            }
        }
        
        // Getters for current game settings - try PlayerPrefs first, then SettingsManager
        private float GetCurrentFOV()
        {
            try
            {
                // Try PlayerPrefs first (this has the user's saved preference)
                if (PlayerPrefs.HasKey("fov"))
                {
                    float prefsFov = PlayerPrefs.GetFloat("fov");
                    Debug.Log($"[PPKB] Read FOV from PlayerPrefs: {prefsFov}");
                    if (prefsFov > 0) return prefsFov;
                }
                
                // Try SettingsManager
                var tSettingsManager = GetSettingsManagerType();
                var instance = GetSettingsManagerInstance(tSettingsManager);
                if (instance != null && tSettingsManager != null)
                {
                    var prop = tSettingsManager.GetProperty("Fov", BindingFlags.Static | BindingFlags.Public);
                    if (prop != null)
                    {
                        var val = prop.GetValue(null);
                        if (val != null) 
                        {
                            float fov = Convert.ToSingle(val);
                            Debug.Log($"[PPKB] Read FOV from SettingsManager: {fov}");
                            return fov;
                        }
                    }
                }
            }
            catch (Exception ex) { Debug.LogWarning($"[PPKB] GetCurrentFOV error: {ex.Message}"); }
            return 90f;
        }
        
        private float GetCurrentCameraAngle()
        {
            try
            {
                // Try PlayerPrefs first
                if (PlayerPrefs.HasKey("cameraAngle"))
                {
                    float prefsAngle = PlayerPrefs.GetFloat("cameraAngle");
                    Debug.Log($"[PPKB] Read CameraAngle from PlayerPrefs: {prefsAngle}");
                    if (prefsAngle >= 0) return prefsAngle;
                }
                
                var tSettingsManager = GetSettingsManagerType();
                var instance = GetSettingsManagerInstance(tSettingsManager);
                if (instance != null && tSettingsManager != null)
                {
                    var prop = tSettingsManager.GetProperty("CameraAngle", BindingFlags.Static | BindingFlags.Public);
                    if (prop != null)
                    {
                        var val = prop.GetValue(null);
                        if (val != null) 
                        {
                            float angle = Convert.ToSingle(val);
                            Debug.Log($"[PPKB] Read CameraAngle from SettingsManager: {angle}");
                            return angle;
                        }
                    }
                }
            }
            catch (Exception ex) { DebugLog($"[PPKB] GetCurrentCameraAngle error: {ex.Message}"); }
            return 30f;
        }
        
        private string GetCurrentHandedness()
        {
            try
            {
                // Read SettingsManager.Handedness (PlayerHandedness enum: None/Left/Right).
                // The game persists this via SaveManager.SetEnum -> PlayerPrefs.SetInt,
                // so reading PlayerPrefs.GetString would always return "".
                var tSettingsManager = GetSettingsManagerType();
                var fi = tSettingsManager?.GetField("Handedness", BindingFlags.Static | BindingFlags.Public);
                if (fi != null)
                {
                    var val = fi.GetValue(null);
                    if (val != null)
                    {
                        string hand = val.ToString();
                        Debug.Log($"[PPKB] Read Handedness from SettingsManager: {hand}");
                        return hand;
                    }
                }
            }
            catch (Exception ex) { DebugLog($"[PPKB] GetCurrentHandedness error: {ex.Message}"); }
            return "Right";
        }

        // Resolve PlayerHandedness enum type via the UpdateHandedness method signature.
        private static Type _cachedHandednessEnumType;
        private Type GetPlayerHandednessType()
        {
            if (_cachedHandednessEnumType != null) return _cachedHandednessEnumType;
            try
            {
                var tSettingsManager = GetSettingsManagerType();
                var mi = tSettingsManager?.GetMethod("UpdateHandedness", BindingFlags.Static | BindingFlags.Public);
                var ps = mi?.GetParameters();
                if (ps != null && ps.Length == 1 && ps[0].ParameterType.IsEnum)
                    _cachedHandednessEnumType = ps[0].ParameterType;
            }
            catch { }
            return _cachedHandednessEnumType;
        }
        
        private float GetCurrentStickSensitivity()
        {
            try
            {
                // Try PlayerPrefs first
                if (PlayerPrefs.HasKey("globalStickSensitivity"))
                {
                    float prefsSens = PlayerPrefs.GetFloat("globalStickSensitivity");
                    Debug.Log($"[PPKB] Read StickSensitivity from PlayerPrefs: {prefsSens}");
                    if (prefsSens >= 0) return prefsSens;
                }
                
                var tSettingsManager = GetSettingsManagerType();
                var instance = GetSettingsManagerInstance(tSettingsManager);
                if (instance != null && tSettingsManager != null)
                {
                    var prop = tSettingsManager.GetProperty("GlobalStickSensitivity", BindingFlags.Static | BindingFlags.Public);
                    if (prop != null)
                    {
                        var val = prop.GetValue(null);
                        if (val != null) 
                        {
                            float sens = Convert.ToSingle(val);
                            Debug.Log($"[PPKB] Read StickSensitivity from SettingsManager: {sens}");
                            return sens;
                        }
                    }
                }
            }
            catch (Exception ex) { DebugLog($"[PPKB] GetCurrentStickSensitivity error: {ex.Message}"); }
            return 0.2f;
        }
        
        private float GetCurrentLookSensitivity()
        {
            try
            {
                // Try PlayerPrefs first
                if (PlayerPrefs.HasKey("lookSensitivity"))
                {
                    float prefsSens = PlayerPrefs.GetFloat("lookSensitivity");
                    Debug.Log($"[PPKB] Read LookSensitivity from PlayerPrefs: {prefsSens}");
                    if (prefsSens >= 0) return prefsSens;
                }
                
                var tSettingsManager = GetSettingsManagerType();
                var instance = GetSettingsManagerInstance(tSettingsManager);
                if (instance != null && tSettingsManager != null)
                {
                    var prop = tSettingsManager.GetProperty("LookSensitivity", BindingFlags.Static | BindingFlags.Public);
                    if (prop != null)
                    {
                        var val = prop.GetValue(null);
                        if (val != null) 
                        {
                            float sens = Convert.ToSingle(val);
                            Debug.Log($"[PPKB] Read LookSensitivity from SettingsManager: {sens}");
                            return sens;
                        }
                    }
                }
            }
            catch (Exception ex) { DebugLog($"[PPKB] GetCurrentLookSensitivity error: {ex.Message}"); }
            return 0.2f;
        }
        
        private float GetCurrentChatOpacity()
        {
            try
            {
                if (PlayerPrefs.HasKey("chatOpacity"))
                    return PlayerPrefs.GetFloat("chatOpacity");
                    
                var tSettingsManager = GetSettingsManagerType();
                var instance = GetSettingsManagerInstance(tSettingsManager);
                if (instance != null && tSettingsManager != null)
                {
                    var prop = tSettingsManager.GetProperty("ChatOpacity", BindingFlags.Static | BindingFlags.Public);
                    if (prop != null)
                    {
                        var val = prop.GetValue(null);
                        if (val != null) return Convert.ToSingle(val);
                    }
                }
            }
            catch (Exception ex) { DebugLog($"[PPKB] GetCurrentChatOpacity error: {ex.Message}"); }
            return 1f;
        }
        
        private float GetCurrentChatScale()
        {
            try
            {
                if (PlayerPrefs.HasKey("chatScale"))
                    return PlayerPrefs.GetFloat("chatScale");
                    
                var tSettingsManager = GetSettingsManagerType();
                var instance = GetSettingsManagerInstance(tSettingsManager);
                if (instance != null && tSettingsManager != null)
                {
                    var prop = tSettingsManager.GetProperty("ChatScale", BindingFlags.Static | BindingFlags.Public);
                    if (prop != null)
                    {
                        var val = prop.GetValue(null);
                        if (val != null) return Convert.ToSingle(val);
                    }
                }
            }
            catch (Exception ex) { DebugLog($"[PPKB] GetCurrentChatScale error: {ex.Message}"); }
            return 1f;
        }
        
        private float GetCurrentMinimapOpacity()
        {
            try
            {
                if (PlayerPrefs.HasKey("minimapOpacity"))
                    return PlayerPrefs.GetFloat("minimapOpacity");
                    
                var tSettingsManager = GetSettingsManagerType();
                var instance = GetSettingsManagerInstance(tSettingsManager);
                if (instance != null && tSettingsManager != null)
                {
                    var prop = tSettingsManager.GetProperty("MinimapOpacity", BindingFlags.Static | BindingFlags.Public);
                    if (prop != null)
                    {
                        var val = prop.GetValue(null);
                        if (val != null) return Convert.ToSingle(val);
                    }
                }
            }
            catch (Exception ex) { DebugLog($"[PPKB] GetCurrentMinimapOpacity error: {ex.Message}"); }
            return 1f;
        }
        
        private float GetCurrentMinimapBackgroundOpacity()
        {
            try
            {
                if (PlayerPrefs.HasKey("minimapBackgroundOpacity"))
                    return PlayerPrefs.GetFloat("minimapBackgroundOpacity");
                    
                var tSettingsManager = GetSettingsManagerType();
                var instance = GetSettingsManagerInstance(tSettingsManager);
                if (instance != null && tSettingsManager != null)
                {
                    var prop = tSettingsManager.GetProperty("MinimapBackgroundOpacity", BindingFlags.Static | BindingFlags.Public);
                    if (prop != null)
                    {
                        var val = prop.GetValue(null);
                        if (val != null) return Convert.ToSingle(val);
                    }
                }
            }
            catch (Exception ex) { DebugLog($"[PPKB] GetCurrentMinimapBackgroundOpacity error: {ex.Message}"); }
            return 0.5f;
        }
        
        private float GetCurrentMinimapHorizontalPosition()
        {
            try
            {
                if (PlayerPrefs.HasKey("minimapHorizontalPosition"))
                    return PlayerPrefs.GetFloat("minimapHorizontalPosition");
                    
                var tSettingsManager = GetSettingsManagerType();
                var instance = GetSettingsManagerInstance(tSettingsManager);
                if (instance != null && tSettingsManager != null)
                {
                    var prop = tSettingsManager.GetProperty("MinimapHorizontalPosition", BindingFlags.Static | BindingFlags.Public);
                    if (prop != null)
                    {
                        var val = prop.GetValue(null);
                        if (val != null) return Convert.ToSingle(val);
                    }
                }
            }
            catch (Exception ex) { DebugLog($"[PPKB] GetCurrentMinimapHorizontalPosition error: {ex.Message}"); }
            return 100f;
        }

        private float GetCurrentMinimapVerticalPosition()
        {
            try
            {
                if (PlayerPrefs.HasKey("minimapVerticalPosition"))
                    return PlayerPrefs.GetFloat("minimapVerticalPosition");
                    
                var tSettingsManager = GetSettingsManagerType();
                var instance = GetSettingsManagerInstance(tSettingsManager);
                if (instance != null && tSettingsManager != null)
                {
                    var prop = tSettingsManager.GetProperty("MinimapVerticalPosition", BindingFlags.Static | BindingFlags.Public);
                    if (prop != null)
                    {
                        var val = prop.GetValue(null);
                        if (val != null) return Convert.ToSingle(val);
                    }
                }
            }
            catch (Exception ex) { DebugLog($"[PPKB] GetCurrentMinimapVerticalPosition error: {ex.Message}"); }
            return 0f;
        }
        
        private float GetCurrentMinimapScale()
        {
            try
            {
                if (PlayerPrefs.HasKey("minimapScale"))
                    return PlayerPrefs.GetFloat("minimapScale");
                    
                var tSettingsManager = GetSettingsManagerType();
                var instance = GetSettingsManagerInstance(tSettingsManager);
                if (instance != null && tSettingsManager != null)
                {
                    var prop = tSettingsManager.GetProperty("MinimapScale", BindingFlags.Static | BindingFlags.Public);
                    if (prop != null)
                    {
                        var val = prop.GetValue(null);
                        if (val != null) return Convert.ToSingle(val);
                    }
                }
            }
            catch (Exception ex) { DebugLog($"[PPKB] GetCurrentMinimapScale error: {ex.Message}"); }
            return 1f;
        }
        
        private void ApplyFOV(float targetFOV, HockeyPosition position)
        {
            try
            {
                var tSettingsManager = GetSettingsManagerType();
                var instance = GetSettingsManagerInstance(tSettingsManager);
                
                if (instance != null && tSettingsManager != null)
                {
                    var miUpdate = tSettingsManager.GetMethod("UpdateFov", BindingFlags.Static | BindingFlags.Public);
                    miUpdate?.Invoke(null, new object[] { targetFOV });
                    Debug.Log($"[PPKB] Applied FOV {targetFOV} for {position}");
                    return;
                }

                // Fallback: directly set camera FOV
                var mainCam = Camera.main;
                if (mainCam == null)
                {
                    var camGO = GameObject.FindGameObjectWithTag("MainCamera");
                    if (camGO != null) mainCam = camGO.GetComponent<Camera>();
                }
                
                if (mainCam != null && mainCam.fieldOfView != targetFOV)
                {
                    mainCam.fieldOfView = targetFOV;
                    Debug.Log($"[PPKB] Applied FOV {targetFOV} for {position} (direct camera)");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PPKB] Failed to apply FOV: {ex.Message}");
            }
        }
        
        private void ApplyCameraAngle(float angle, HockeyPosition position)
        {
            try
            {
                var tSettingsManager = GetSettingsManagerType();
                var instance = GetSettingsManagerInstance(tSettingsManager);
                
                if (instance != null && tSettingsManager != null)
                {
                    var miUpdate = tSettingsManager.GetMethod("UpdateCameraAngle", BindingFlags.Static | BindingFlags.Public);
                    miUpdate?.Invoke(null, new object[] { angle });
                    Debug.Log($"[PPKB] Applied camera angle {angle} for {position}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PPKB] Failed to apply camera angle: {ex.Message}");
            }
        }
        
        private void ApplyHandedness(string handedness, HockeyPosition position)
        {
            try
            {
                // The game's UpdateHandedness takes a PlayerHandedness enum, not a string —
                // boxing a string into the object[] caused a silent reflection failure.
                var tSettingsManager = GetSettingsManagerType();
                if (tSettingsManager == null) return;

                var tHand = GetPlayerHandednessType();
                if (tHand == null)
                {
                    Debug.LogWarning("[PPKB] PlayerHandedness enum not found");
                    return;
                }

                string normalized = handedness?.Trim() ?? "";
                bool wantLeft = normalized.StartsWith("L", StringComparison.OrdinalIgnoreCase);
                string enumName = wantLeft ? "Left" : "Right";

                object enumValue;
                try { enumValue = Enum.Parse(tHand, enumName, true); }
                catch { enumValue = Enum.Parse(tHand, "Right", true); }

                var miUpdate = tSettingsManager.GetMethod("UpdateHandedness", BindingFlags.Static | BindingFlags.Public);
                miUpdate?.Invoke(null, new object[] { enumValue });
                Debug.Log($"[PPKB] Applied handedness {enumName} for {position}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PPKB] Failed to apply handedness: {ex.Message}");
            }
        }

        private void ApplyStickSensitivity(float sensitivity, HockeyPosition position)
        {
            try
            {
                var tSettingsManager = GetSettingsManagerType();
                var instance = GetSettingsManagerInstance(tSettingsManager);
                
                if (instance != null && tSettingsManager != null)
                {
                    var miUpdate = tSettingsManager.GetMethod("UpdateGlobalStickSensitivity", BindingFlags.Static | BindingFlags.Public);
                    miUpdate?.Invoke(null, new object[] { sensitivity });
                    Debug.Log($"[PPKB] Applied stick sensitivity {sensitivity} for {position}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PPKB] Failed to apply stick sensitivity: {ex.Message}");
            }
        }

        private void ApplyLookSensitivity(float sensitivity, HockeyPosition position)
        {
            try
            {
                var tSettingsManager = GetSettingsManagerType();
                var instance = GetSettingsManagerInstance(tSettingsManager);
                
                if (instance != null && tSettingsManager != null)
                {
                    var miUpdate = tSettingsManager.GetMethod("UpdateLookSensitivity", BindingFlags.Static | BindingFlags.Public);
                    miUpdate?.Invoke(null, new object[] { sensitivity });
                    Debug.Log($"[PPKB] Applied look sensitivity {sensitivity} for {position}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PPKB] Failed to apply look sensitivity: {ex.Message}");
            }
        }

        private void ApplyChatOpacity(float opacity, HockeyPosition position)
        {
            try
            {
                var tSettingsManager = GetSettingsManagerType();
                var instance = GetSettingsManagerInstance(tSettingsManager);
                
                if (instance != null && tSettingsManager != null)
                {
                    var miUpdate = tSettingsManager.GetMethod("UpdateChatOpacity", BindingFlags.Static | BindingFlags.Public);
                    miUpdate?.Invoke(null, new object[] { opacity });
                    Debug.Log($"[PPKB] Applied chat opacity {opacity} for {position}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PPKB] Failed to apply chat opacity: {ex.Message}");
            }
        }

        private void ApplyChatScale(float scale, HockeyPosition position)
        {
            try
            {
                var tSettingsManager = GetSettingsManagerType();
                var instance = GetSettingsManagerInstance(tSettingsManager);
                
                if (instance != null && tSettingsManager != null)
                {
                    var miUpdate = tSettingsManager.GetMethod("UpdateChatScale", BindingFlags.Static | BindingFlags.Public);
                    miUpdate?.Invoke(null, new object[] { scale });
                    Debug.Log($"[PPKB] Applied chat scale {scale} for {position}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PPKB] Failed to apply chat scale: {ex.Message}");
            }
        }

        private void ApplyMinimapOpacity(float opacity, HockeyPosition position)
        {
            try
            {
                var tSettingsManager = GetSettingsManagerType();
                var instance = GetSettingsManagerInstance(tSettingsManager);
                
                if (instance != null && tSettingsManager != null)
                {
                    var miUpdate = tSettingsManager.GetMethod("UpdateMinimapOpacity", BindingFlags.Static | BindingFlags.Public);
                    miUpdate?.Invoke(null, new object[] { opacity });
                    Debug.Log($"[PPKB] Applied minimap opacity {opacity} for {position}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PPKB] Failed to apply minimap opacity: {ex.Message}");
            }
        }

        private void ApplyMinimapBackgroundOpacity(float opacity, HockeyPosition position)
        {
            try
            {
                var tSettingsManager = GetSettingsManagerType();
                var instance = GetSettingsManagerInstance(tSettingsManager);
                
                if (instance != null && tSettingsManager != null)
                {
                    var miUpdate = tSettingsManager.GetMethod("UpdateMinimapBackgroundOpacity", BindingFlags.Static | BindingFlags.Public);
                    miUpdate?.Invoke(null, new object[] { opacity });
                    Debug.Log($"[PPKB] Applied minimap background opacity {opacity} for {position}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PPKB] Failed to apply minimap background opacity: {ex.Message}");
            }
        }

        private void ApplyMinimapHorizontalPosition(float position, HockeyPosition hockeyPosition)
        {
            try
            {
                var tSettingsManager = GetSettingsManagerType();
                var instance = GetSettingsManagerInstance(tSettingsManager);
                
                if (instance != null && tSettingsManager != null)
                {
                    var miUpdate = tSettingsManager.GetMethod("UpdateMinimapHorizontalPosition", BindingFlags.Static | BindingFlags.Public);
                    miUpdate?.Invoke(null, new object[] { position });
                    Debug.Log($"[PPKB] Applied minimap horizontal position {position} for {hockeyPosition}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PPKB] Failed to apply minimap horizontal position: {ex.Message}");
            }
        }

        private void ApplyMinimapVerticalPosition(float position, HockeyPosition hockeyPosition)
        {
            try
            {
                var tSettingsManager = GetSettingsManagerType();
                var instance = GetSettingsManagerInstance(tSettingsManager);
                
                if (instance != null && tSettingsManager != null)
                {
                    var miUpdate = tSettingsManager.GetMethod("UpdateMinimapVerticalPosition", BindingFlags.Static | BindingFlags.Public);
                    miUpdate?.Invoke(null, new object[] { position });
                    Debug.Log($"[PPKB] Applied minimap vertical position {position} for {hockeyPosition}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PPKB] Failed to apply minimap vertical position: {ex.Message}");
            }
        }

        private void ApplyMinimapScale(float scale, HockeyPosition position)
        {
            try
            {
                var tSettingsManager = GetSettingsManagerType();
                var instance = GetSettingsManagerInstance(tSettingsManager);
                
                if (instance != null && tSettingsManager != null)
                {
                    var miUpdate = tSettingsManager.GetMethod("UpdateMinimapScale", BindingFlags.Static | BindingFlags.Public);
                    miUpdate?.Invoke(null, new object[] { scale });
                    Debug.Log($"[PPKB] Applied minimap scale {scale} for {position}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PPKB] Failed to apply minimap scale: {ex.Message}");
            }
        }
    }
}
