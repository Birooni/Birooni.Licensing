using System.Text.Json;
using Birooni.Client.Models;

namespace Birooni.Client.Storage;

public class LicenseCacheManager
{
    public string LicenseFilePath { get; }

    public LicenseCacheManager(string? customFilePath = null, string? productName = null)
    {
        if (!string.IsNullOrWhiteSpace(customFilePath))
        {
            LicenseFilePath = customFilePath;
        }
        else
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var fileName = string.IsNullOrWhiteSpace(productName) || productName.Equals("BiruBox", StringComparison.OrdinalIgnoreCase)
                ? "license.lic"
                : $"{productName.Trim().ToLowerInvariant()}.lic";
            LicenseFilePath = Path.Combine(programData, "Birooni", fileName);
        }
    }

    public static LicenseCacheManager ForProduct(string productName) => new(productName: productName);

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

    public static string MailIdFilePath
    {
        get
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(programData, "Birooni", "mail-id.txt");
        }
    }

    public static string? LoadMailId()
    {
        try
        {
            if (!File.Exists(MailIdFilePath))
            {
                return null;
            }

            var value = File.ReadAllText(MailIdFilePath).Trim();
            return value.IndexOf('@') > 0 ? value : null;
        }
        catch
        {
            return null;
        }
    }

    public static void SaveMailId(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.IndexOf('@') < 1)
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(MailIdFilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(MailIdFilePath, email.Trim());
        }
        catch
        {
            // Identity cache is optional
        }
    }
}
