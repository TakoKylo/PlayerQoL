// Integrated from "IFeelLeftOut" by gubby (com.gubby.ifeelleftout, v2.0.0).
// Wide-view secondary camera for goalies. Toggle key reads from CommandKeybindConfig.
//
// Hot-path note: Harmony patches PlayerCamera.OnTick, which fires per-camera per-tick
// for EVERY player in the scene. The Tick entry point MUST early-exit for non-local
// cameras before doing any FindFirstObjectByType work, or it will tank FPS as the
// player count grows.

using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace PoncePuck.Keybinds
{
    internal static class LeftOutCamera
    {
        private const float CAM_HEIGHT = 12f;
        private const float CAM_ANGLE = 60f;
        private const float CAM_FOV = 90f;

        private static bool initialized;
        private static bool toggleActive;
        private static Player localPlayer;
        private static Camera playerCam;
        private static Camera leftOutCamera;
        private static GameObject leftOutCameraGO;
        private static bool keyWasPressed;
        private static bool reloadKeyWasPressed;
        private static Vector3 lastKnownGoodPosition = Vector3.zero;
        private static bool hasValidPosition;
        private static PlayerTeam lastSeenTeam = PlayerTeam.None;

        private static CommandKeybindConfig Cfg => KeybindRunner.Instance?.CommandConfig;
        private static bool DebugEnabled => Cfg?.enableDebugLogging ?? false;

        private static void Log(string msg) { if (DebugEnabled) Debug.Log("[PPKB/LeftOut] " + msg); }
        private static void LogError(string msg) { Debug.LogError("[PPKB/LeftOut] " + msg); }

        private static bool IsDedicatedServer()
        {
            return SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null;
        }

        public static void Reset()
        {
            try
            {
                if (leftOutCamera != null) leftOutCamera.enabled = false;
                if (leftOutCameraGO != null) UnityEngine.Object.DestroyImmediate(leftOutCameraGO);
                leftOutCamera = null;
                leftOutCameraGO = null;
                playerCam = null;
                localPlayer = null;
                toggleActive = false;
                initialized = false;
                keyWasPressed = false;
                reloadKeyWasPressed = false;
                hasValidPosition = false;
                lastKnownGoodPosition = Vector3.zero;
                lastSeenTeam = PlayerTeam.None;
            }
            catch (Exception e) { LogError("Reset failed: " + e.Message); }
        }

        private static bool IsValidCameraPosition(Vector3 p)
        {
            if (p == Vector3.zero) return false;
            if (float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z)) return false;
            if (Mathf.Abs(p.x) > 100f || Mathf.Abs(p.z) > 100f || p.y < 0f || p.y > 50f) return false;
            return true;
        }

        private static Vector3 GetSafeCameraPosition(PlayerTeam team)
        {
            float z = (team == PlayerTeam.Blue) ? -15f : 15f;
            var candidate = new Vector3(0f, CAM_HEIGHT, z);
            if (IsValidCameraPosition(candidate))
            {
                lastKnownGoodPosition = candidate;
                hasValidPosition = true;
                return candidate;
            }
            if (hasValidPosition && IsValidCameraPosition(lastKnownGoodPosition))
                return lastKnownGoodPosition;

            var fallback = new Vector3(0f, 20f, 0f);
            lastKnownGoodPosition = fallback;
            hasValidPosition = true;
            return fallback;
        }

        private static void EnsureLeftOutCameraExists()
        {
            if (leftOutCamera != null && leftOutCameraGO != null) return;

            if (leftOutCameraGO != null) UnityEngine.Object.DestroyImmediate(leftOutCameraGO);
            leftOutCameraGO = new GameObject("PPKB_LeftOutCamera");
            UnityEngine.Object.DontDestroyOnLoad(leftOutCameraGO);
            leftOutCamera = leftOutCameraGO.AddComponent<Camera>();
            leftOutCamera.enabled = false;
            if (playerCam != null)
            {
                leftOutCamera.clearFlags = playerCam.clearFlags;
                leftOutCamera.backgroundColor = playerCam.backgroundColor;
                leftOutCamera.cullingMask = playerCam.cullingMask;
                leftOutCamera.depth = playerCam.depth + 1f;
            }
        }

        private static void EnsurePlayerCameraExists(PlayerCamera instance)
        {
            if (playerCam != null) return;
            // Prefer the patched instance (guaranteed by IsOwner check) over
            // localPlayer.PlayerCamera, which can lag behind during spawn.
            var pc = instance != null ? instance : (localPlayer != null ? localPlayer.PlayerCamera : null);
            if (pc != null && ((BaseCamera)pc).UnityCamera != null)
                playerCam = ((BaseCamera)pc).UnityCamera;
        }

        private static void InitializeLeftOutCamera(PlayerTeam team)
        {
            try
            {
                EnsureLeftOutCameraExists();
                if (leftOutCamera == null) { LogError("Failed to create left-out camera"); return; }

                var pos = GetSafeCameraPosition(team);
                float yaw = (team == PlayerTeam.Blue) ? -180f : 0f;
                leftOutCamera.transform.position = pos;
                leftOutCamera.transform.rotation = Quaternion.Euler(CAM_ANGLE, yaw, 0f);
                leftOutCamera.fieldOfView = CAM_FOV;

                if (!IsValidCameraPosition(leftOutCamera.transform.position))
                {
                    leftOutCamera.transform.position = pos;
                    if (!IsValidCameraPosition(leftOutCamera.transform.position))
                    {
                        initialized = false;
                        return;
                    }
                }
                initialized = true;
                Log("Initialized at " + leftOutCamera.transform.position);
            }
            catch (Exception e) { LogError("Initialize failed: " + e.Message); Reset(); }
        }

        private static Key ParseKey(string name, Key fallback)
        {
            if (string.IsNullOrEmpty(name)) return fallback;
            Key result;
            if (Enum.TryParse<Key>(name, true, out result)) return result;
            return fallback;
        }

        private static void HandleInput(CommandKeybindConfig cfg)
        {
            var kb = Keyboard.current;
            if (kb == null) return;

            var toggleKey = ParseKey(cfg?.leftOutCameraToggleKey, Key.F1);
            var reloadKey = ParseKey(cfg?.leftOutCameraReloadKey, Key.F2);

            bool reloadPressed = ((ButtonControl)kb[reloadKey]).wasPressedThisFrame;
            if (reloadPressed && !reloadKeyWasPressed && localPlayer != null && localPlayer.Team != PlayerTeam.None && localPlayer.Team != PlayerTeam.Spectator)
            {
                initialized = false;
                InitializeLeftOutCamera(localPlayer.Team);
            }
            reloadKeyWasPressed = reloadPressed;

            bool pressed = ((ButtonControl)kb[toggleKey]).isPressed;
            if (pressed && !keyWasPressed)
            {
                if (leftOutCamera == null || playerCam == null) { keyWasPressed = pressed; return; }
                if (!toggleActive)
                {
                    var pos = leftOutCamera.transform.position;
                    if (!IsValidCameraPosition(pos) && localPlayer != null && localPlayer.Team != PlayerTeam.None)
                    {
                        InitializeLeftOutCamera(localPlayer.Team);
                        if (!IsValidCameraPosition(leftOutCamera.transform.position)) { keyWasPressed = pressed; return; }
                    }
                }
                toggleActive = !toggleActive;
                Log("Toggle: " + (toggleActive ? "ENABLED" : "DISABLED"));
            }
            keyWasPressed = pressed;
        }

        private static void UpdateCameraStates()
        {
            if (leftOutCamera == null || playerCam == null) return;
            if (leftOutCamera.enabled == toggleActive) return;

            if (toggleActive)
            {
                var pos = leftOutCamera.transform.position;
                if (!IsValidCameraPosition(pos) && localPlayer != null && localPlayer.Team != PlayerTeam.None && localPlayer.Team != PlayerTeam.Spectator)
                {
                    leftOutCamera.transform.position = GetSafeCameraPosition(localPlayer.Team);
                }
            }
            leftOutCamera.enabled = toggleActive;
            playerCam.enabled = !toggleActive;
        }

        private static void DetectTeamChange()
        {
            if (localPlayer == null) return;
            var team = localPlayer.Team;
            if (lastSeenTeam != PlayerTeam.None && team != lastSeenTeam) Reset();
            lastSeenTeam = team;
        }

        public static void Tick(PlayerCamera instance)
        {
            // === HOT PATH: this runs per-PlayerCamera per-tick (i.e. N players × tick rate).
            // Everything before the local-player check must be O(1) — no scene scans.
            try
            {
                if (instance == null) return;

                // Fast filter: PlayerCamera is spawned with SpawnWithOwnership(OwnerClientId),
                // so IsOwner is true exactly for the local client's camera. Cheaper and more
                // reliable than chasing Player.IsLocalPlayer (which needs IsPlayerObject set).
                if (!instance.IsOwner) return;

                if (IsDedicatedServer()) return;

                var camPlayer = instance.Player;
                if (camPlayer == null) return;

                // Cached static — no FindFirstObjectByType. If the runner isn't up yet
                // (mod still initializing), fall back to defaults rather than bailing.
                var cfg = Cfg;
                if (cfg != null && !cfg.enableLeftOutCamera) return;

                // Cache the local player reference. If it changed (e.g. respawn),
                // wipe transient state.
                if (localPlayer != camPlayer)
                {
                    localPlayer = camPlayer;
                    playerCam = null;
                    initialized = false;
                    toggleActive = false;
                    lastSeenTeam = PlayerTeam.None;
                }

                DetectTeamChange();
                if (localPlayer == null) return; // DetectTeamChange may have called Reset()

                if (localPlayer.Role != PlayerRole.Goalie) return;

                EnsurePlayerCameraExists(instance);
                EnsureLeftOutCameraExists();
                HandleInput(cfg);

                if (!initialized && localPlayer.Team != PlayerTeam.None && localPlayer.Team != PlayerTeam.Spectator)
                    InitializeLeftOutCamera(localPlayer.Team);

                UpdateCameraStates();
            }
            catch (Exception e)
            {
                LogError("Tick failed: " + e.Message);
                Reset();
            }
        }

        public static void OnDespawn(PlayerCamera instance)
        {
            try
            {
                if (IsDedicatedServer() || instance == null) return;
                if (!instance.IsOwner) return; // only the local camera's despawn matters
                Reset();
            }
            catch (Exception e) { LogError("OnDespawn failed: " + e.Message); }
        }
    }

    [HarmonyPatch(typeof(PlayerCamera), "OnTick")]
    internal static class LeftOutCamera_OnTick_Patch
    {
        private static void Postfix(PlayerCamera __instance, float deltaTime)
        {
            LeftOutCamera.Tick(__instance);
        }
    }

    [HarmonyPatch(typeof(PlayerCamera), "OnNetworkDespawn")]
    internal static class LeftOutCamera_OnNetworkDespawn_Patch
    {
        private static void Prefix(PlayerCamera __instance)
        {
            LeftOutCamera.OnDespawn(__instance);
        }
    }
}
