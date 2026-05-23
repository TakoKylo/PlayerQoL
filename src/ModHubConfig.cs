using System;
using System.IO;
using System.Linq;
using UnityEngine;

namespace PonceMods.Shared
{
    /// <summary>
    /// Centralized configuration management for Puck game mods.
    /// All client-side mod configs are stored in config/ModHub/ folder.
    /// </summary>
    public static class ModHubConfig
    {
        private const string ModHubFolderName = "ModHub";
        private static string _cachedModHubPath;
        private static bool _initialized;

        /// <summary>
        /// Old config locations that need migration to ModHub folder.
        /// DashFall keybinds go to DashFall folder, PlayerInput/QoL keybinds go to PlayerQoL folder.
        /// </summary>
        private static readonly (string OldPath, string NewPath, bool IsFolder)[] MigrationMappings = new[]
        {
            // Standalone config files
            ("config/arms_config.json", "config/ModHub/arms_config.json", false),
            ("config/dodgepuck_client_config.json", "config/ModHub/dodgepuck_client_config.json", false),
            ("config/shootout_client_config.json", "config/ModHub/shootout_client_config.json", false),
            
            // DashFall keybinds - go to DashFall folder
            ("config/playerinput/PonceKeybinds.Goalie.json", "config/ModHub/DashFall/PonceKeybinds.Goalie.json", false),
            ("config/playerinput/PonceKeybinds.Skater.json", "config/ModHub/DashFall/PonceKeybinds.Skater.json", false),
            ("config/playerinput/DashFall.Client.json", "config/ModHub/DashFall/DashFall.Client.json", false),
            
            // PlayerInput QoL keybinds - go to PlayerQoL folder
            ("config/playerinput/PonceLocalMute.json", "config/ModHub/PlayerQoL/PonceLocalMute.json", false),
            ("config/playerinput/PonceKeybinds.Commands.json", "config/ModHub/PlayerQoL/PonceKeybinds.Commands.json", false),
            
            // Asset folders
            ("config/Sprays", "config/ModHub/Sprays", true),
            ("config/Scoreboard", "config/ModHub/Scoreboard", true)
        };

        /// <summary>
        /// Gets the base game directory path.
        /// </summary>
        public static string GameDirectory
        {
            get
            {
                // Application.dataPath returns "GameFolder/Puck_Data"
                // We need to go up one level to get the game root
                return Path.GetDirectoryName(Application.dataPath);
            }
        }

        /// <summary>
        /// Checks if the current instance is a dedicated server.
        /// ModHub config operations should only run on clients.
        /// </summary>
        public static bool IsDedicatedServer()
        {
            // Check for headless/batch mode which indicates dedicated server
            return Application.isBatchMode || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null;
        }

        /// <summary>
        /// Gets the ModHub folder path (config/ModHub/).
        /// Creates the folder if it doesn't exist.
        /// </summary>
        /// <returns>Full path to the ModHub folder, or null if on dedicated server.</returns>
        public static string GetModHubPath()
        {
            if (IsDedicatedServer())
            {
                return null;
            }

            if (!_initialized)
            {
                Initialize();
            }

            return _cachedModHubPath;
        }

        /// <summary>
        /// Gets the full path to a config file within the ModHub folder.
        /// </summary>
        /// <param name="configFileName">The config file name (e.g., "arms_config.json")</param>
        /// <returns>Full path to the config file, or null if on dedicated server.</returns>
        public static string GetConfigPath(string configFileName)
        {
            if (IsDedicatedServer())
            {
                return null;
            }

            string modHubPath = GetModHubPath();
            if (string.IsNullOrEmpty(modHubPath))
            {
                return null;
            }

            return Path.Combine(modHubPath, configFileName);
        }

        /// <summary>
        /// Gets the full path to a subfolder within the ModHub folder.
        /// Creates the folder if it doesn't exist.
        /// </summary>
        /// <param name="folderName">The folder name (e.g., "Sprays", "Scoreboard", "playerinput")</param>
        /// <returns>Full path to the folder, or null if on dedicated server.</returns>
        public static string GetFolderPath(string folderName)
        {
            if (IsDedicatedServer())
            {
                return null;
            }

            string modHubPath = GetModHubPath();
            if (string.IsNullOrEmpty(modHubPath))
            {
                return null;
            }

            string folderPath = Path.Combine(modHubPath, folderName);

            try
            {
                if (!Directory.Exists(folderPath))
                {
                    Directory.CreateDirectory(folderPath);
                    Debug.Log($"[ModHubConfig] Created folder: {folderPath}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ModHubConfig] Failed to create folder '{folderName}': {ex.Message}");
                return null;
            }

            return folderPath;
        }

        /// <summary>
        /// Initializes the ModHub folder structure.
        /// </summary>
        private static void Initialize()
        {
            if (_initialized || IsDedicatedServer())
            {
                return;
            }

            try
            {
                _cachedModHubPath = Path.Combine(GameDirectory, "config", ModHubFolderName);

                if (!Directory.Exists(_cachedModHubPath))
                {
                    Directory.CreateDirectory(_cachedModHubPath);
                    Debug.Log($"[ModHubConfig] Created ModHub folder: {_cachedModHubPath}");
                }

                _initialized = true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ModHubConfig] Failed to initialize ModHub folder: {ex.Message}");
                _cachedModHubPath = null;
            }
        }

        /// <summary>
        /// Migrates old config files and folders to the new ModHub location.
        /// Should be called once during mod initialization.
        /// </summary>
        public static void MigrateOldConfigs()
        {
            if (IsDedicatedServer())
            {
                Debug.Log("[ModHubConfig] Skipping migration on dedicated server.");
                return;
            }

            // Ensure ModHub folder exists
            string modHubPath = GetModHubPath();
            if (string.IsNullOrEmpty(modHubPath))
            {
                Debug.LogError("[ModHubConfig] Cannot migrate configs - ModHub path not available.");
                return;
            }

            Debug.Log("[ModHubConfig] Starting config migration check...");
            int migratedCount = 0;

            foreach (var mapping in MigrationMappings)
            {
                string oldFullPath = Path.Combine(GameDirectory, mapping.OldPath);
                string newFullPath = Path.Combine(GameDirectory, mapping.NewPath);

                try
                {
                    if (mapping.IsFolder)
                    {
                        if (Directory.Exists(oldFullPath) && !Directory.Exists(newFullPath))
                        {
                            MigrateFolder(oldFullPath, newFullPath);
                            migratedCount++;
                        }
                    }
                    else
                    {
                        // Check if old file exists
                        if (File.Exists(oldFullPath))
                        {
                            if (!File.Exists(newFullPath))
                            {
                                // Copy to new location
                                MigrateFile(oldFullPath, newFullPath);
                                migratedCount++;
                            }
                            else
                            {
                                // New file exists, but old file wasn't marked - mark it now
                                MarkFileAsMigrated(oldFullPath);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[ModHubConfig] Failed to migrate '{mapping.OldPath}': {ex.Message}");
                }
            }

            if (migratedCount > 0)
            {
                Debug.Log($"[ModHubConfig] Migration complete. Migrated {migratedCount} item(s).");
            }
            else
            {
                Debug.Log("[ModHubConfig] No configs needed migration.");
            }
            
            // Clean up the old playerinput folder if it only has .migrated files left
            CleanupLegacyPlayerInputFolder();
        }
        
        /// <summary>
        /// Checks the old playerinput folder and renames it to _migrated if all active files have been migrated.
        /// </summary>
        private static void CleanupLegacyPlayerInputFolder()
        {
            try
            {
                string legacyDir = Path.Combine(GameDirectory, "config", "playerinput");
                string migratedDir = legacyDir + "_migrated";
                
                if (!Directory.Exists(legacyDir) || Directory.Exists(migratedDir))
                    return;
                
                // Check if there are any non-.migrated files left
                var activeFiles = Directory.GetFiles(legacyDir, "*", SearchOption.AllDirectories)
                    .Where(f => !f.EndsWith(".migrated", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                
                if (activeFiles.Length == 0)
                {
                    // All files are migrated, rename the folder
                    Directory.Move(legacyDir, migratedDir);
                    Debug.Log($"[ModHubConfig] Renamed old playerinput folder to: playerinput_migrated");
                }
                else
                {
                    Debug.Log($"[ModHubConfig] playerinput folder still has {activeFiles.Length} active file(s), not renaming yet.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ModHubConfig] Could not cleanup playerinput folder: {ex.Message}");
            }
        }

        /// <summary>
        /// Migrates a single file from old location to new location.
        /// </summary>
        private static void MigrateFile(string oldPath, string newPath)
        {
            // Ensure the destination directory exists
            string destDir = Path.GetDirectoryName(newPath);
            if (!Directory.Exists(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            // Copy the file to new location
            File.Copy(oldPath, newPath);
            Debug.Log($"[ModHubConfig] Migrated file: {Path.GetFileName(oldPath)}");

            // Optionally delete the old file (or rename it to .bak)
            try
            {
                string backupPath = oldPath + ".migrated";
                if (File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                }
                File.Move(oldPath, backupPath);
                Debug.Log($"[ModHubConfig] Old file renamed to: {Path.GetFileName(backupPath)}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ModHubConfig] Could not rename old file '{Path.GetFileName(oldPath)}': {ex.Message}");
            }
        }
        
        /// <summary>
        /// Marks an old file as migrated (when new file already exists).
        /// </summary>
        private static void MarkFileAsMigrated(string oldPath)
        {
            try
            {
                string backupPath = oldPath + ".migrated";
                if (File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                }
                File.Move(oldPath, backupPath);
                Debug.Log($"[ModHubConfig] Marked old file as migrated: {Path.GetFileName(backupPath)}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ModHubConfig] Could not mark old file '{Path.GetFileName(oldPath)}' as migrated: {ex.Message}");
            }
        }

        /// <summary>
        /// Migrates an entire folder from old location to new location.
        /// </summary>
        private static void MigrateFolder(string oldPath, string newPath)
        {
            // Create the destination folder
            Directory.CreateDirectory(newPath);

            // Copy all files
            foreach (string filePath in Directory.GetFiles(oldPath, "*", SearchOption.AllDirectories))
            {
                string relativePath = filePath.Substring(oldPath.Length + 1);
                string destFilePath = Path.Combine(newPath, relativePath);

                string destFileDir = Path.GetDirectoryName(destFilePath);
                if (!Directory.Exists(destFileDir))
                {
                    Directory.CreateDirectory(destFileDir);
                }

                File.Copy(filePath, destFilePath);
            }

            Debug.Log($"[ModHubConfig] Migrated folder: {Path.GetFileName(oldPath)}");

            // Rename the old folder to indicate it was migrated
            try
            {
                string backupPath = oldPath + "_migrated";
                if (Directory.Exists(backupPath))
                {
                    Directory.Delete(backupPath, true);
                }
                Directory.Move(oldPath, backupPath);
                Debug.Log($"[ModHubConfig] Old folder renamed to: {Path.GetFileName(backupPath)}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ModHubConfig] Could not rename old folder '{Path.GetFileName(oldPath)}': {ex.Message}");
            }
        }

        /// <summary>
        /// Checks if a config file exists in the ModHub folder.
        /// </summary>
        /// <param name="configFileName">The config file name</param>
        /// <returns>True if the config exists</returns>
        public static bool ConfigExists(string configFileName)
        {
            string configPath = GetConfigPath(configFileName);
            return !string.IsNullOrEmpty(configPath) && File.Exists(configPath);
        }

        /// <summary>
        /// Checks if a folder exists in the ModHub folder.
        /// </summary>
        /// <param name="folderName">The folder name</param>
        /// <returns>True if the folder exists</returns>
        public static bool FolderExists(string folderName)
        {
            string modHubPath = GetModHubPath();
            if (string.IsNullOrEmpty(modHubPath))
            {
                return false;
            }

            return Directory.Exists(Path.Combine(modHubPath, folderName));
        }

        /// <summary>
        /// Reads all text from a config file in the ModHub folder.
        /// </summary>
        /// <param name="configFileName">The config file name</param>
        /// <returns>The file contents, or null if file doesn't exist or on error</returns>
        public static string ReadConfig(string configFileName)
        {
            string configPath = GetConfigPath(configFileName);
            if (string.IsNullOrEmpty(configPath) || !File.Exists(configPath))
            {
                return null;
            }

            try
            {
                return File.ReadAllText(configPath);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ModHubConfig] Failed to read config '{configFileName}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Writes text to a config file in the ModHub folder.
        /// </summary>
        /// <param name="configFileName">The config file name</param>
        /// <param name="content">The content to write</param>
        /// <returns>True if successful</returns>
        public static bool WriteConfig(string configFileName, string content)
        {
            string configPath = GetConfigPath(configFileName);
            if (string.IsNullOrEmpty(configPath))
            {
                return false;
            }

            try
            {
                File.WriteAllText(configPath, content);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ModHubConfig] Failed to write config '{configFileName}': {ex.Message}");
                return false;
            }
        }
    }
}
