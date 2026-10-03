using System;
using System.IO;
using System.Text.Json;

namespace Biruscan.Models
{
    /// <summary>
    /// Configuration settings for the User Modeling Watcher (Learning from Manual Revit Modeling).
    /// </summary>
    public class UserModelingWatcherSettings
    {
        private static readonly string SettingsFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Autodesk", "Revit", "Addins", "2025", "Biruscan", "user_modeling_watcher_settings.json");

        private static UserModelingWatcherSettings _instance;
        private static readonly object _lock = new object();

        public static UserModelingWatcherSettings Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = Load();
                        }
                    }
                }
                return _instance;
            }
        }

        /// <summary>Master switch to watch and learn from user's manual modeling in Revit.</summary>
        public bool Enabled { get; set; } = true;

        public bool WatchPipes { get; set; } = true;
        public bool WatchDucts { get; set; } = true;
        public bool WatchConduits { get; set; } = true;
        public bool WatchCableTrays { get; set; } = true;
        public bool WatchFittings { get; set; } = true;
        public bool WatchWallsAndStructures { get; set; } = true;

        /// <summary>Radius around manual elements to query point cloud data (in feet, default 1.5ft / ~450mm).</summary>
        public double RoiSearchRadiusFeet { get; set; } = 1.5;

        /// <summary>Number of manual modeling actions before triggering background neural fine-tuning.</summary>
        public int AutoRetrainThreshold { get; set; } = 10;

        /// <summary>Automatically save fine-tuned neural weights to BiruscanMepWeights.dat.</summary>
        public bool AutoSaveWeights { get; set; } = true;

        public static UserModelingWatcherSettings Load()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    string json = File.ReadAllText(SettingsFilePath);
                    return JsonSerializer.Deserialize<UserModelingWatcherSettings>(json) ?? new UserModelingWatcherSettings();
                }
            }
            catch { }
            return new UserModelingWatcherSettings();
        }

        public void Save()
        {
            try
            {
                string dir = Path.GetDirectoryName(SettingsFilePath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsFilePath, json);
            }
            catch { }
        }
    }
}
