using System;
using System.Collections.Generic;

namespace PoncePuck.Keybinds
{
    // Position enum for the 6 hockey positions
    public enum HockeyPosition
    {
        None = 0,
        LeftWing = 1,      // LW
        Center = 2,        // C
        RightWing = 3,     // RW
        LeftDefense = 4,   // LD
        RightDefense = 5,  // RD
        Goalie = 6         // G
    }

    // Setting types that can be overridden per position
    public enum PositionSettingType
    {
        FOV = 0,
        CameraAngle = 1,
        Handedness = 2,
        StickSensitivity = 3,
        LookSensitivity = 4,
        ChatOpacity = 5,
        ChatScale = 6,
        MinimapOpacity = 7,
        MinimapBackgroundOpacity = 8,
        MinimapHorizontalPosition = 9,
        MinimapVerticalPosition = 10,
        MinimapScale = 11
    }

    // A single position setting override entry
    [Serializable]
    public class PositionSettingEntry
    {
        public HockeyPosition position = HockeyPosition.None;
        public PositionSettingType settingType = PositionSettingType.FOV;
        public string value = "";  // String value that gets parsed based on settingType
    }

    // Config for all position-specific settings (kept for migration, use CommandKeybindConfig.positionSettings instead)
    [Serializable]
    public class PositionSettingsConfig
    {
        public List<PositionSettingEntry> entries = new List<PositionSettingEntry>();
    }

    [Serializable]
    public class CommandKeybindConfig
    {
        public List<string> extraCommands = new List<string>(); // "/vs:J"
        public List<string> prefills = new List<string>();      // "/w |U"

        // Position-specific settings stored as strings: "POSITION:SETTINGTYPE:VALUE"
        // e.g., "RW:FOV:90", "G:Handedness:Left"
        public List<string> positionSettingsRaw = new List<string>();
        
        // Runtime-only parsed list (not serialized)
        [NonSerialized]
        public List<PositionSettingEntry> positionSettings = new List<PositionSettingEntry>();
        
        // Call this after loading to parse raw strings into entries
        public void ParsePositionSettings()
        {
            positionSettings = new List<PositionSettingEntry>();
            foreach (var raw in positionSettingsRaw)
            {
                var parts = raw.Split(':');
                if (parts.Length >= 3)
                {
                    var entry = new PositionSettingEntry
                    {
                        position = ParsePosition(parts[0]),
                        settingType = ParseSettingType(parts[1]),
                        value = parts[2]
                    };
                    if (entry.position != HockeyPosition.None)
                        positionSettings.Add(entry);
                }
            }
        }
        
        // Call this before saving to convert entries back to raw strings
        public void SerializePositionSettings()
        {
            positionSettingsRaw = new List<string>();
            foreach (var entry in positionSettings)
            {
                if (entry.position != HockeyPosition.None && !string.IsNullOrEmpty(entry.value))
                {
                    var posStr = PositionToShortString(entry.position);
                    var typeStr = SettingTypeToString(entry.settingType);
                    positionSettingsRaw.Add($"{posStr}:{typeStr}:{entry.value}");
                }
            }
        }
        
        private static HockeyPosition ParsePosition(string s)
        {
            switch (s.ToUpper())
            {
                case "LW": return HockeyPosition.LeftWing;
                case "C": return HockeyPosition.Center;
                case "RW": return HockeyPosition.RightWing;
                case "LD": return HockeyPosition.LeftDefense;
                case "RD": return HockeyPosition.RightDefense;
                case "G": return HockeyPosition.Goalie;
                default: return HockeyPosition.None;
            }
        }
        
        private static PositionSettingType ParseSettingType(string s)
        {
            switch (s.ToUpper())
            {
                case "FOV": return PositionSettingType.FOV;
                case "CAMERAANGLE": return PositionSettingType.CameraAngle;
                case "HANDEDNESS": return PositionSettingType.Handedness;
                case "STICKSENSITIVITY": return PositionSettingType.StickSensitivity;
                case "LOOKSENSITIVITY": return PositionSettingType.LookSensitivity;
                case "CHATOPACITY": return PositionSettingType.ChatOpacity;
                case "CHATSCALE": return PositionSettingType.ChatScale;
                case "MINIMAPOPACITY": return PositionSettingType.MinimapOpacity;
                case "MINIMAPBACKGROUNDOPACITY": return PositionSettingType.MinimapBackgroundOpacity;
                case "MINIMAPHORIZONTALPOSITION": return PositionSettingType.MinimapHorizontalPosition;
                case "MINIMAPVERTICALPOSITION": return PositionSettingType.MinimapVerticalPosition;
                case "MINIMAPSCALE": return PositionSettingType.MinimapScale;
                default: return PositionSettingType.FOV;
            }
        }
        
        private static string PositionToShortString(HockeyPosition pos)
        {
            switch (pos)
            {
                case HockeyPosition.LeftWing: return "LW";
                case HockeyPosition.Center: return "C";
                case HockeyPosition.RightWing: return "RW";
                case HockeyPosition.LeftDefense: return "LD";
                case HockeyPosition.RightDefense: return "RD";
                case HockeyPosition.Goalie: return "G";
                default: return "NONE";
            }
        }
        
        private static string SettingTypeToString(PositionSettingType type)
        {
            switch (type)
            {
                case PositionSettingType.FOV: return "FOV";
                case PositionSettingType.CameraAngle: return "CameraAngle";
                case PositionSettingType.Handedness: return "Handedness";
                case PositionSettingType.StickSensitivity: return "StickSensitivity";
                case PositionSettingType.LookSensitivity: return "LookSensitivity";
                case PositionSettingType.ChatOpacity: return "ChatOpacity";
                case PositionSettingType.ChatScale: return "ChatScale";
                case PositionSettingType.MinimapOpacity: return "MinimapOpacity";
                case PositionSettingType.MinimapBackgroundOpacity: return "MinimapBackgroundOpacity";
                case PositionSettingType.MinimapHorizontalPosition: return "MinimapHorizontalPosition";
                case PositionSettingType.MinimapVerticalPosition: return "MinimapVerticalPosition";
                case PositionSettingType.MinimapScale: return "MinimapScale";
                default: return "FOV";
            }
        }

        // NEW persisted audio/mention settings
        public bool enableTagCustomization = false;  // Enable tag/color customization (default disabled)
        public bool autoApplyTagOnJoin = true;  // Auto-apply tag/color when joining server (default on)
        public string customTag = "";  // Custom tag text (e.g., "Pro", "Rookie", etc.)
        public bool enablePanelCustomization = true;  // Enable panel customization
        public bool enableSounds = false;  // Enable sound settings
        public bool enableAdminSettings = false;  // Enable admin menu (default disabled)
        public float musicvol = 0.5f;
        public bool warmupmusic = true;
        public bool enableColor = false;
        public bool enableRichText = true;  // Enable rich text in name tags
        public string colorHex = "#FFFFFF";
        
        // Legacy compatibility (can be removed in future)
        public bool enableDonor { get => enableTagCustomization; set => enableTagCustomization = value; }
        public float panelAlpha = 1f;
        public float _panelHueSlider = 0f;
        public float _panelSaturationSlider = 0f;
        public float _panelBrightnessSlider = 0.20f;

        public bool enableChatRichText = true;  // Enable rich text in chat messages (default on)
        public bool enableLiveGradients = true; // Animate rolling gradient tags from TagMod (default on)
        public bool disableArenaVisuals = false;  // Disable arena/stadium visuals completely (default off)
        public bool disableArenaProps = false;    // Disable 3D props/meshes (default off - enabled)
        public bool disableArenaLights = false;   // Disable arena lights (default off - enabled)
        public bool disableArenaSkybox = false;   // Disable custom skybox (default off - enabled)
        public bool disableArenaParticles = false; // Disable particle effects (default off - enabled)
        public float arenaAudioVolume = 0.9f;     // Arena ambient audio volume (0-1, default 0.9)
        
        // Scoreboard/Scorebug customization settings
        public bool enableRecentPlayers = true;  // Show Recent 50 players section (default on)
        public bool enableLiveLogging = true;  // Enable live logging functionality (default on)
        
        // Mention sound settings
        public bool mentionSoundEnabled = true;  // Enable @mention ping sound (default on)
        public float mentionSoundVolume = 0.5f;  // Mention sound volume (0-1, default 0.8)
        public string selectedMentionSound = "MentionPingDefault.mp3";  // Selected ping sound file
        
        // Debug settings
        public bool enableDebugLogging = false;  // Enable debug logging (default off)

        // Helper methods for position settings
        public float? GetFOVForPosition(HockeyPosition pos)
        {
            foreach (var e in positionSettings)
            {
                if (e.position == pos && e.settingType == PositionSettingType.FOV)
                {
                    if (float.TryParse(e.value, out float fov)) return fov;
                }
            }
            return null;
        }

        public float? GetCameraAngleForPosition(HockeyPosition pos)
        {
            foreach (var e in positionSettings)
            {
                if (e.position == pos && e.settingType == PositionSettingType.CameraAngle)
                {
                    if (float.TryParse(e.value, out float angle)) return angle;
                }
            }
            return null;
        }

        public string GetHandednessForPosition(HockeyPosition pos)
        {
            foreach (var e in positionSettings)
            {
                if (e.position == pos && e.settingType == PositionSettingType.Handedness)
                {
                    return e.value;
                }
            }
            return null;
        }

        public float? GetStickSensitivityForPosition(HockeyPosition pos)
        {
            foreach (var e in positionSettings)
            {
                if (e.position == pos && e.settingType == PositionSettingType.StickSensitivity)
                {
                    if (float.TryParse(e.value, out float sens)) return sens;
                }
            }
            return null;
        }

        public float? GetLookSensitivityForPosition(HockeyPosition pos)
        {
            foreach (var e in positionSettings)
            {
                if (e.position == pos && e.settingType == PositionSettingType.LookSensitivity)
                {
                    if (float.TryParse(e.value, out float sens)) return sens;
                }
            }
            return null;
        }

        public float? GetChatOpacityForPosition(HockeyPosition pos)
        {
            foreach (var e in positionSettings)
            {
                if (e.position == pos && e.settingType == PositionSettingType.ChatOpacity)
                {
                    if (float.TryParse(e.value, out float val)) return val;
                }
            }
            return null;
        }

        public float? GetChatScaleForPosition(HockeyPosition pos)
        {
            foreach (var e in positionSettings)
            {
                if (e.position == pos && e.settingType == PositionSettingType.ChatScale)
                {
                    if (float.TryParse(e.value, out float val)) return val;
                }
            }
            return null;
        }

        public float? GetMinimapOpacityForPosition(HockeyPosition pos)
        {
            foreach (var e in positionSettings)
            {
                if (e.position == pos && e.settingType == PositionSettingType.MinimapOpacity)
                {
                    if (float.TryParse(e.value, out float val)) return val;
                }
            }
            return null;
        }

        public float? GetMinimapBackgroundOpacityForPosition(HockeyPosition pos)
        {
            foreach (var e in positionSettings)
            {
                if (e.position == pos && e.settingType == PositionSettingType.MinimapBackgroundOpacity)
                {
                    if (float.TryParse(e.value, out float val)) return val;
                }
            }
            return null;
        }

        public float? GetMinimapHorizontalPositionForPosition(HockeyPosition pos)
        {
            foreach (var e in positionSettings)
            {
                if (e.position == pos && e.settingType == PositionSettingType.MinimapHorizontalPosition)
                {
                    if (float.TryParse(e.value, out float val)) return val;
                }
            }
            return null;
        }

        public float? GetMinimapVerticalPositionForPosition(HockeyPosition pos)
        {
            foreach (var e in positionSettings)
            {
                if (e.position == pos && e.settingType == PositionSettingType.MinimapVerticalPosition)
                {
                    if (float.TryParse(e.value, out float val)) return val;
                }
            }
            return null;
        }

        public float? GetMinimapScaleForPosition(HockeyPosition pos)
        {
            foreach (var e in positionSettings)
            {
                if (e.position == pos && e.settingType == PositionSettingType.MinimapScale)
                {
                    if (float.TryParse(e.value, out float val)) return val;
                }
            }
            return null;
        }
    }


}
