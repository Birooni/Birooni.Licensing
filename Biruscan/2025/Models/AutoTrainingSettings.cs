using System;
using System.IO;
using System.Text.Json;

namespace Biruscan.Models
{
    /// <summary>
    /// Configuration and preferences for Auto-Training & Active Learning.
    /// Persisted across Revit sessions in the user's Addin folder.
    /// </summary>
    public class AutoTrainingSettings
    {
        private static AutoTrainingSettings _instance;
        public static AutoTrainingSettings Instance => _instance ??= Load();

        /// <summary>Master switch for auto-training observation during Fitter and MEP tools.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Observe elements created by Pipe, Duct, Cable Tray, Wall, and Steel fitters.</summary>
        public bool ObserveFitterTools { get; set; } = true;

        /// <summary>Observe elements created or modified by MEP operations (Elbow, Tee, Union, Reducer, Unify, Cross).</summary>
        public bool ObserveMepOperations { get; set; } = true;

        /// <summary>Learn from accepted & kept elements as positive ground truth.</summary>
        public bool LearnAcceptedFits { get; set; } = true;

        /// <summary>Learn from user manual adjustments / edits on fitted elements (Mistake & Correction learning).</summary>
        public bool LearnCorrections { get; set; } = true;

        /// <summary>Learn from deleted / undone elements as negative samples (rejection learning).</summary>
        public bool LearnRejections { get; set; } = true;

        /// <summary>Number of active learning events before triggering a background auto-retrain (e.g. 5, 10, 20).</summary>
        public int AutoRetrainThreshold { get; set; } = 10;

        /// <summary>Automatically save adapted neural weights to BiruscanMepWeights.dat on retrain.</summary>
        public bool AutoSaveWeights { get; set; } = true;

        private static string SettingsFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Autodesk", "Revit", "Addins", "2025", "Biruscan", "auto_train_settings.json"
        );

        public static AutoTrainingSettings Load()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    string json = File.ReadAllText(SettingsFilePath);
                    return JsonSerializer.Deserialize<AutoTrainingSettings>(json) ?? new AutoTrainingSettings();
                }
            }
            catch { }
            return new AutoTrainingSettings();
        }

        public void Save()
        {
            try
            {
                string dir = Path.GetDirectoryName(SettingsFilePath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsFilePath, json);
            }
            catch { }
        }

        public void ResetDefaults()
        {
            Enabled = true;
            ObserveFitterTools = true;
            ObserveMepOperations = true;
            LearnAcceptedFits = true;
            LearnCorrections = true;
            LearnRejections = true;
            AutoRetrainThreshold = 10;
            AutoSaveWeights = true;
            Save();
        }
    }
}
