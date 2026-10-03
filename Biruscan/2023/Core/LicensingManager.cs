#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Autodesk.Revit.UI;
using Birooni.Client;
using Birooni.Client.Models;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace Biruscan.Core
{
    public static class LicensingManager
    {
        public const string ApiBaseUrl = "https://api.ibrooni.com";
        public const string ProductCode = "Biruscan";
        public static readonly DateTimeOffset FreeUntilUtc = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);

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
        public static BirooniLicenseClient Client =>
            _client ??= new BirooniLicenseClient(ApiBaseUrl, PublicKeyPem, "1.0.7", productName: ProductCode);

        private static Birooni.Client.Updates.UpdateChecker? _updateChecker;
        public static Birooni.Client.Updates.UpdateChecker UpdateChecker =>
            _updateChecker ??= new Birooni.Client.Updates.UpdateChecker(ApiBaseUrl);

        private static Birooni.Client.Updates.AutoUpdateManager? _autoUpdateManager;
        public static Birooni.Client.Updates.AutoUpdateManager AutoUpdateManager =>
            _autoUpdateManager ??= new Birooni.Client.Updates.AutoUpdateManager();

        public static LicenseValidationResult? CurrentStatus { get; private set; }

        public static bool IsFreePeriod => DateTimeOffset.UtcNow < FreeUntilUtc;

        public static Task InitializeAsync()
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    if (!IsFreePeriod)
                    {
                        try
                        {
                            CurrentStatus = await Client.InitializeLicenseAsync();
                        }
                        catch
                        {
                            // Offline cache is optional; tools still lock without a granted key.
                        }
                    }

                    var currentVersion = typeof(LicensingManager).Assembly.GetName().Version?.ToString(3) ?? "1.0.7";
                    var revitYear = GuessRevitYear();
                    var fallback = UseNet48Payload()
                        ? "https://ibrooni.com/downloads/Biruscan-latest-net48.json"
                        : "https://ibrooni.com/downloads/Biruscan-latest.json";
                    var update = await UpdateChecker.CheckAsync(ProductCode, currentVersion, revitYear, fallback);
                    if (update?.DownloadUrl != null &&
                        update.DownloadUrl.IndexOf("Biruscan-update.zip", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        update.DownloadUrl.IndexOf("net48", StringComparison.OrdinalIgnoreCase) < 0 &&
                        UseNet48Payload())
                    {
                        update = null;
                    }

                    if (update?.UpdateAvailable == true && !string.IsNullOrWhiteSpace(update.DownloadUrl))
                    {
                        var pluginDir = Path.GetDirectoryName(typeof(LicensingManager).Assembly.Location);
                        if (string.IsNullOrWhiteSpace(pluginDir)) return;
                        var stagingDir = Path.Combine(pluginDir, ".staging");
                        var staged = await AutoUpdateManager.DownloadAndStageUpdateAsync(
                            update.DownloadUrl, stagingDir, update.ChecksumSha256);
                        if (staged)
                        {
                            AutoUpdateManager.ScheduleApplyOnExit(Process.GetCurrentProcess().Id, stagingDir, pluginDir);
                        }
                    }
                }
                catch
                {
                    // Never block Revit
                }
            });

            return Task.CompletedTask;
        }

        public static bool EnsureLicense()
        {
            if (IsFreePeriod) return true;
            if (CurrentStatus != null && CurrentStatus.IsGranted) return true;

            try
            {
                var cached = Client.InitializeLicenseAsync().ConfigureAwait(false).GetAwaiter().GetResult();
                CurrentStatus = cached;
                if (cached != null && cached.IsGranted) return true;
            }
            catch
            {
                // Fall through to the key prompt.
            }

            if (!PromptLicense(out var key, out var mailId))
            {
                TaskDialog.Show(
                    "Biruscan",
                    "Biruscan locked on 1 January 2027. A license key must be assigned in Ibrooni Admin and activated on this PC. Until a key is assigned, Biruscan stays locked.");
                return false;
            }

            try
            {
                var activated = Client.ActivateLicenseAsync(key.Trim(), userEmail: mailId).ConfigureAwait(false).GetAwaiter().GetResult();
                CurrentStatus = activated;
                if (activated != null && activated.IsGranted) return true;
                TaskDialog.Show("Biruscan", activated?.Message ?? "That license key could not be activated.");
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Biruscan", "License activation failed: " + ex.Message);
            }

            return false;
        }

        private static bool PromptLicense(out string key, out string mailId)
        {
            key = string.Empty;
            mailId = string.Empty;
            using var form = new Form();
            form.Text = "Biruscan license required";
            form.FormBorderStyle = FormBorderStyle.FixedDialog;
            form.StartPosition = FormStartPosition.CenterScreen;
            form.MinimizeBox = false;
            form.MaximizeBox = false;
            form.Width = 480;
            form.Height = 300;
            form.TopMost = true;

            var label = new Label
            {
                Left = 16,
                Top = 12,
                Width = 430,
                Height = 56,
                Text = "The free period ended on 1 January 2027. Paste the Biruscan key from Ibrooni Admin (SCAN-xxxx-xxxx-xxxx). Mail ID is required for a company site license."
            };
            var mailLabel = new Label { Left = 16, Top = 72, Width = 430, Height = 18, Text = "Mail ID" };
            var mailBox = new System.Windows.Forms.TextBox { Left = 16, Top = 92, Width = 430 };
            var saved = Birooni.Client.Storage.LicenseCacheManager.LoadMailId();
            if (!string.IsNullOrWhiteSpace(saved)) mailBox.Text = saved;
            var keyLabel = new Label { Left = 16, Top = 122, Width = 430, Height = 18, Text = "License key" };
            var box = new System.Windows.Forms.TextBox { Left = 16, Top = 142, Width = 430 };
            var ok = new Button { Text = "Activate", Left = 256, Width = 90, Top = 188, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", Left = 356, Width = 90, Top = 188, DialogResult = DialogResult.Cancel };
            form.Controls.Add(label);
            form.Controls.Add(mailLabel);
            form.Controls.Add(mailBox);
            form.Controls.Add(keyLabel);
            form.Controls.Add(box);
            form.Controls.Add(ok);
            form.Controls.Add(cancel);
            form.AcceptButton = ok;
            form.CancelButton = cancel;
            if (form.ShowDialog() != DialogResult.OK) return false;
            key = (box.Text ?? string.Empty).Trim();
            mailId = (mailBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(key)) return false;
            if (string.IsNullOrWhiteSpace(mailId) || mailId.IndexOf('@') < 1)
            {
                TaskDialog.Show(
                    "Biruscan",
                    "Enter your Mail ID (office email). Site licenses require it so the key can match your company domain.");
                return false;
            }
            return true;
        }

        private static bool UseNet48Payload()
        {
#if NETFRAMEWORK
            return true;
#else
            return false;
#endif
        }

        private static string? GuessRevitYear()
        {
            try
            {
                var dir = Path.GetDirectoryName(typeof(LicensingManager).Assembly.Location);
                while (!string.IsNullOrEmpty(dir))
                {
                    var name = Path.GetFileName(dir);
                    if (name.Length == 4 && int.TryParse(name, out var year) && year >= 2020 && year <= 2030)
                    {
                        return name;
                    }

                    dir = Path.GetDirectoryName(dir);
                }
            }
            catch
            {
                // ignore
            }

            return null;
        }
    }
}
