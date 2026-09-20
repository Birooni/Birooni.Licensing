using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Autodesk.Revit.UI;
using Birooni.Client;
using Birooni.Client.Models;
using FamilyLoader.UI;

namespace FamilyLoader.Core
{
    public static class LicensingManager
    {
        public const string ApiBaseUrl = "https://api.ibrooni.com";
        public const string ProductCode = "FamilyLoader";

        public const string PublicKeyPem = @"-----BEGIN PUBLIC KEY-----
MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEA0Hf8aMkNF3xvChyRR6Zl
eScGi9JzYqc0Ap0IkuNB194E1xUDzaZrFcILFkVycnu6mpsrFF2D4gi1hFwUnUIX
6NXeEBpFqma9Rd5tLHf08zAccKStBtOh0phsnkoP0Nz5FiqrB/63/chCxM3qWrHH
MihZ0HQ27qkJYzKmNsci3vMTzNBTZVBYJDjzOR9Bqc24o4d63BibriDOxUdlkkN0
+s25UwQka+bBUMBTx1bKLgeyd7FgmMFXtG+JYc6rBBrbLFvYLmfNoPgpMjBbfoM2
LFrFZ/qqCKnA6ZpWR0OBdH/VdkXXT574zwlDNNQfb6xillUj2egldQvTrpPIG51z
HQIDAQAB
-----END PUBLIC KEY-----";

        private static BirooniLicenseClient? _client;
        public static BirooniLicenseClient Client => _client ??= new BirooniLicenseClient(ApiBaseUrl, PublicKeyPem, "1.1.0", productName: ProductCode);

        private static Birooni.Client.Updates.UpdateChecker? _updateChecker;
        public static Birooni.Client.Updates.UpdateChecker UpdateChecker => _updateChecker ??= new Birooni.Client.Updates.UpdateChecker(ApiBaseUrl);

        private static Birooni.Client.Updates.AutoUpdateManager? _autoUpdateManager;
        public static Birooni.Client.Updates.AutoUpdateManager AutoUpdateManager => _autoUpdateManager ??= new Birooni.Client.Updates.AutoUpdateManager();

        public static LicenseValidationResult? CurrentStatus { get; private set; }
        public static Birooni.Client.Updates.AppUpdateInfo? AvailableUpdate { get; private set; }
        public static bool IsUpdateStaged { get; private set; }

        public static event EventHandler<Birooni.Client.Updates.AppUpdateInfo>? UpdateDetected;
        public static event EventHandler<Birooni.Client.Updates.AppUpdateInfo>? UpdateStaged;

        public static bool IsLicenseValid => CurrentStatus?.IsGranted == true;

        public static async Task<LicenseValidationResult> InitializeAsync()
        {
            Client.BackgroundValidationCompleted += (sender, result) =>
            {
                CurrentStatus = result;
            };

            CurrentStatus = await Client.InitializeLicenseAsync();

            // Background update check (non-blocking)
            _ = Task.Run(async () =>
            {
                try
                {
                    var currentVersion = typeof(LicensingManager).Assembly.GetName().Version?.ToString(3) ?? "1.1.0";
                    var update = await UpdateChecker.CheckAsync(ProductCode, currentVersion, ConfigManager.RevitVersion);
                    if (update?.UpdateAvailable == true && !string.IsNullOrWhiteSpace(update.DownloadUrl))
                    {
                        AvailableUpdate = update;
                        UpdateDetected?.Invoke(null, update);

                        var pluginDir = Path.GetDirectoryName(typeof(LicensingManager).Assembly.Location);
                        if (!string.IsNullOrWhiteSpace(pluginDir))
                        {
                            var stagingDir = Path.Combine(pluginDir, ".staging");
                            var staged = await AutoUpdateManager.DownloadAndStageUpdateAsync(
                                update.DownloadUrl,
                                stagingDir,
                                update.ChecksumSha256);

                            if (staged)
                            {
                                IsUpdateStaged = true;
                                int hostPid = Process.GetCurrentProcess().Id;
                                AutoUpdateManager.ScheduleApplyOnExit(hostPid, stagingDir, pluginDir);
                                UpdateStaged?.Invoke(null, update);
                            }
                        }
                    }
                }
                catch
                {
                    // Silent
                }
            });

            return CurrentStatus;
        }

        public static bool EnsureLicense(Window? owner = null)
        {
            if (IsLicenseValid) return true;

            var dialog = new LicenseDialog();
            if (owner != null) dialog.Owner = owner;

            var res = dialog.ShowDialog();
            return IsLicenseValid;
        }

        public static string GetStatusText()
        {
            if (CurrentStatus == null || !CurrentStatus.IsGranted)
                return "Unlicensed";

            var payload = CurrentStatus.Payload;
            if (payload == null) return "Active";

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
}
