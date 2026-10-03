using System;
using System.IO;
using System.Text.Json;

namespace Biruscan.Models
{
    public class MepSettings
    {
        private static MepSettings _instance;
        public static MepSettings Instance => _instance ??= Load();

        public double PipeExtensionLengthFeet { get; set; } = 1.0;
        public double DuctExtensionLengthFeet { get; set; } = 1.5;
        public double ConduitExtensionLengthFeet { get; set; } = 1.0;
        public double ElbowExtensionLengthFeet { get; set; } = 1.5;
        public bool AutoConnectStubs { get; set; } = true;
        public string PreferredPipeTypeName { get; set; } = "";
        public string PreferredPipingSystemTypeName { get; set; } = "";
        public string PreferredRectDuctTypeName { get; set; } = "";
        public string PreferredRoundDuctTypeName { get; set; } = "";
        public string PreferredDuctSystemTypeName { get; set; } = "";

        private static string SettingsFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Autodesk", "Revit", "Addins", "2023", "Biruscan", "mep_settings.json"
        );

        public static MepSettings Load()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    string json = File.ReadAllText(SettingsFilePath);
                    return JsonSerializer.Deserialize<MepSettings>(json) ?? new MepSettings();
                }
            }
            catch { }
            return new MepSettings();
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
            PipeExtensionLengthFeet = 1.0;
            DuctExtensionLengthFeet = 1.5;
            ConduitExtensionLengthFeet = 1.0;
            ElbowExtensionLengthFeet = 1.5;
            AutoConnectStubs = true;
            PreferredPipeTypeName = "";
            PreferredPipingSystemTypeName = "";
            PreferredRectDuctTypeName = "";
            PreferredRoundDuctTypeName = "";
            PreferredDuctSystemTypeName = "";
            Save();
        }
    }
}
