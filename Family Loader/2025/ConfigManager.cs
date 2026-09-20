using System;
using System.IO;
using System.Text.Json;

namespace FamilyLoader
{
    public static class ConfigManager
    {
        public static string RevitVersion { get; set; } = "Unknown";

        private static string GetDefaultConfigPath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string baseDir = Path.Combine(appData, "Autodesk", "Revit", "Addins", "Family Loader");
            string versionDir = Path.Combine(baseDir, RevitVersion);
            
            if (!Directory.Exists(versionDir))
            {
                Directory.CreateDirectory(versionDir);
            }
            
            string path = Path.Combine(versionDir, "config.json");
            
            // Migrate from old global path if version-specific file doesn't exist
            if (!File.Exists(path))
            {
                string oldPath = Path.Combine(baseDir, "config.json");
                if (File.Exists(oldPath))
                {
                    try
                    {
                        File.Copy(oldPath, path);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error migrating config: {ex.Message}");
                    }
                }
            }

            return path;
        }

        public static Configuration Load()
        {
            string path = GetDefaultConfigPath();
            if (!File.Exists(path))
            {
                return new Configuration();
            }

            try
            {
                string json = File.ReadAllText(path);
                var config = JsonSerializer.Deserialize<Configuration>(json) ?? new Configuration();
                
                // Migrate legacy Sessions to Tabs
                if (config.Tabs.Count == 0 && config.Sessions.Count > 0)
                {
                    config.Tabs.Add(new TabConfig
                    {
                        Name = "Family Loader",
                        Panels = config.Sessions
                    });
                    config.Sessions = new System.Collections.Generic.List<PanelConfig>(); // Clear after migration
                    Save(config); // Save the migrated config
                }

                return config;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading config: {ex.Message}");
                return new Configuration();
            }
        }

        public static void Save(Configuration config)
        {
            string path = GetDefaultConfigPath();
            SaveToFile(config, path);
        }

        public static void Export(Configuration config, string filePath)
        {
            SaveToFile(config, filePath);
        }

        public static Configuration Import(string filePath)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException("Import file not found", filePath);
            }

            string json = File.ReadAllText(filePath);
            var config = JsonSerializer.Deserialize<Configuration>(json) ?? new Configuration();
            
            // Migrate legacy Sessions to Tabs
            if (config.Tabs.Count == 0 && config.Sessions.Count > 0)
            {
                config.Tabs.Add(new TabConfig
                {
                    Name = "Family Loader",
                    Panels = config.Sessions
                });
                config.Sessions = new System.Collections.Generic.List<PanelConfig>();
            }
            
            return config;
        }

        private static void SaveToFile(Configuration config, string filePath)
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(config, options);
                File.WriteAllText(filePath, json);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to save configuration: {ex.Message}", ex);
            }
        }
    }
}
