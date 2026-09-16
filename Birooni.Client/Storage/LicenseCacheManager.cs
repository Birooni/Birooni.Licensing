using System.Text.Json;
using Birooni.Client.Models;

namespace Birooni.Client.Storage;

public class LicenseCacheManager
{
    public string LicenseFilePath { get; }

    public LicenseCacheManager(string? customFilePath = null)
    {
        if (!string.IsNullOrWhiteSpace(customFilePath))
        {
            LicenseFilePath = customFilePath;
        }
        else
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            LicenseFilePath = Path.Combine(programData, "Birooni", "license.lic");
        }
    }

    public CachedToken? LoadToken()
    {
        try
        {
            if (!File.Exists(LicenseFilePath))
            {
                return null;
            }

            var json = File.ReadAllText(LicenseFilePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            return JsonSerializer.Deserialize<CachedToken>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch
        {
            return null;
        }
    }

    public void SaveToken(CachedToken token)
    {
        try
        {
            var directory = Path.GetDirectoryName(LicenseFilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(token, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(LicenseFilePath, json);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to write license cache to {LicenseFilePath}", ex);
        }
    }

    public void ClearToken()
    {
        try
        {
            if (File.Exists(LicenseFilePath))
            {
                File.Delete(LicenseFilePath);
            }
        }
        catch
        {
            // Ignore failure on cleanup
        }
    }
}
