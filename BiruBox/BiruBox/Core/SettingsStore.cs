using System;
using System.IO;
using System.Text.Json;

namespace BiruBox.Core;

public static class SettingsStore
{
    private static readonly string SettingsFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BiruBox");

    private static readonly string SettingsFile = Path.Combine(SettingsFolder, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static AutoSectionSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsFile))
            {
                return new AutoSectionSettings();
            }

            string json = File.ReadAllText(SettingsFile);
            return JsonSerializer.Deserialize<AutoSectionSettings>(json, JsonOptions) ?? new AutoSectionSettings();
        }
        catch
        {
            return new AutoSectionSettings();
        }
    }

    public static void Save(AutoSectionSettings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsFolder);
            string json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(SettingsFile, json);
        }
        catch
        {
            // Silent fail: plugin remains usable if persistence is blocked
        }
    }
}
