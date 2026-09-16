using System.Windows;
using Autodesk.Revit.UI;
using Birooni.Client;
using Birooni.Client.Models;
using BiruBox.UI;

namespace BiruBox.Core;

public static class LicensingManager
{
    public const string ApiBaseUrl = "https://birooni-licensing.onrender.com";

    public const string PublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEA0SlDylVCS5rtXZLToBOn
        0FuWUHdUHI95nykc0ZbQHs92ElQLujBKtZ5cbD3htdhZAUJvuATtLGdLHsTYUKIL
        XW+gtA5lV05cGjVrGDvg/d9VD5zfusKYZxz9FEAwmAnfWe7x/BXCU519rjCsfcNn
        fAdK2TtRyP5T7qi+G8vzrD0s92vcNNsGAi0BNmDlYZp4RAKZ/YWmLgmLhWXgjOyw
        obIs4mOnLIZD5yxr81f3GEZyFrQ9XAxO/eTecXXELOyC7lrjHNAUblxd9QY6e6AA
        8zj8+Ajz9QmqZj+S5eXTEZ1cHuphr7PsU6UkuV48/Fn1rj5v0mq610E9dmUtqCei
        nQIDAQAB
        -----END PUBLIC KEY-----
        """;

    private static BirooniLicenseClient? _client;
    public static BirooniLicenseClient Client => _client ??= new BirooniLicenseClient(ApiBaseUrl, PublicKeyPem, "2026.1.0");

    public static LicenseValidationResult? CurrentStatus { get; private set; }

    public static bool IsLicenseValid => CurrentStatus?.IsGranted == true;

    /// <summary>
    /// Non-blocking initialization called during Revit IExternalApplication.OnStartup.
    /// Immediately checks the local offline token cache without blocking Revit's UI thread.
    /// </summary>
    public static async Task<LicenseValidationResult> InitializeAsync()
    {
        Client.BackgroundValidationCompleted += (sender, result) =>
        {
            CurrentStatus = result;
        };

        CurrentStatus = await Client.InitializeLicenseAsync();
        return CurrentStatus;
    }

    /// <summary>
    /// Checks if a valid license/trial is active. If not, prompts the user with the LicenseDialog.
    /// Returns true if licensed; false if the user cancels or remains unlicensed.
    /// </summary>
    public static bool EnsureLicense(Window? owner = null)
    {
        if (IsLicenseValid)
        {
            return true;
        }

        // Show dialog modally
        var dialog = new LicenseDialog();
        if (owner is not null)
        {
            dialog.Owner = owner;
        }

        var result = dialog.ShowDialog();
        return IsLicenseValid;
    }

    public static string GetStatusText()
    {
        if (CurrentStatus == null || !CurrentStatus.IsGranted)
        {
            return "Unlicensed";
        }

        var payload = CurrentStatus.Payload;
        if (payload == null)
        {
            return "Active";
        }

        if (string.Equals(payload.LicenseType, "Trial", StringComparison.OrdinalIgnoreCase))
        {
            var remaining = payload.ValidUntil - DateTimeOffset.UtcNow;
            var days = Math.Max(0, (int)Math.Ceiling(remaining.TotalDays));
            return $"Trial ({days} day{(days == 1 ? "" : "s")} left)";
        }

        if (payload.ExpiresAt.HasValue)
        {
            var remaining = payload.ExpiresAt.Value - DateTimeOffset.UtcNow;
            var days = Math.Max(0, (int)Math.Ceiling(remaining.TotalDays));
            return $"Subscription ({days} days left)";
        }

        return "Licensed";
    }

    public static void UpdateStatus(LicenseValidationResult result)
    {
        CurrentStatus = result;
    }
}
