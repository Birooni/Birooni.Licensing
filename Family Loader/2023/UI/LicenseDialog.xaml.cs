using System;
using System.Windows;
using System.Windows.Media;
using Birooni.Client.Models;
using FamilyLoader.Core;

namespace FamilyLoader.UI
{
    public partial class LicenseDialog : Window
    {
        public LicenseDialog()
        {
            InitializeComponent();
            Loaded += LicenseDialog_Loaded;
        }

        private void LicenseDialog_Loaded(object sender, RoutedEventArgs e)
        {
            TxtHardwareId.Text = LicensingManager.Client.DeviceFingerprint;
            RefreshUiState();
        }

        private void RefreshUiState()
        {
            var status = LicensingManager.CurrentStatus;

            if (status?.IsGranted == true)
            {
                var summary = LicensingManager.GetStatusText();
                StatusBadgeText.Text = summary;
                StatusBadgeText.Foreground = new SolidColorBrush(Color.FromRgb(34, 197, 94)); // Green
                StatusBadgeBorder.Background = new SolidColorBrush(Color.FromArgb(40, 34, 197, 94));

                var payload = status.Payload;
                if (payload != null)
                {
                    TxtDetailType.Text = payload.LicenseType ?? "Standard";
                    TxtDetailCustomer.Text = payload.LicenseKey;
                    TxtDetailExpiry.Text = payload.ExpiresAt.HasValue
                        ? payload.ExpiresAt.Value.ToString("yyyy-MM-dd")
                        : "Perpetual (Valid offline until " + payload.ValidUntil.ToString("yyyy-MM-dd") + ")";

                    TxtLicenseKey.Text = payload.LicenseKey;
                }

                if (LicensingManager.IsUpdateStaged)
                {
                    TxtStatusMessage.Text = $"License is active. v{LicensingManager.AvailableUpdate?.LatestVersion} update downloaded — will apply on Revit close.";
                    TxtStatusMessage.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                }
                else
                {
                    TxtStatusMessage.Text = "License is active and valid.";
                    TxtStatusMessage.Foreground = new SolidColorBrush(Color.FromRgb(22, 101, 52));
                }
                BtnDeactivate.IsEnabled = true;
            }
            else
            {
                StatusBadgeText.Text = "Unlicensed";
                StatusBadgeText.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Red
                StatusBadgeBorder.Background = new SolidColorBrush(Color.FromArgb(40, 239, 68, 68));

                TxtDetailType.Text = "None";
                TxtDetailCustomer.Text = "None";
                TxtDetailExpiry.Text = "Expired / None";

                TxtStatusMessage.Text = status?.Message ?? "No active license found.";
                TxtStatusMessage.Foreground = new SolidColorBrush(Color.FromRgb(185, 28, 28));
                BtnDeactivate.IsEnabled = false;
            }
        }

        private void BtnCopyHwId_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(TxtHardwareId.Text);
                BtnCopyHwId.Content = "Copied!";
            }
            catch
            {
                // Ignore clipboard lock
            }
        }

        private async void BtnActivate_Click(object sender, RoutedEventArgs e)
        {
            var key = TxtLicenseKey.Text.Trim();
            if (string.IsNullOrWhiteSpace(key) || key == "BIROONI-")
            {
                MessageBox.Show("Please enter a valid license key.", "Family Loader Licensing", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BtnActivate.IsEnabled = false;
            BtnActivate.Content = "Activating...";
            TxtStatusMessage.Text = "Connecting to licensing server (waking server if needed, ~20s)...";

            try
            {
                var result = await LicensingManager.Client.ActivateLicenseAsync(key);
                LicensingManager.UpdateStatus(result);
                RefreshUiState();

                if (result.IsGranted)
                {
                    MessageBox.Show("License activated successfully! You may now use all Family Loader features.", "Family Loader Licensing", MessageBoxButton.OK, MessageBoxImage.Information);
                    DialogResult = true;
                    Close();
                }
                else
                {
                    MessageBox.Show(result.Message, "Activation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Activation failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnActivate.IsEnabled = true;
                BtnActivate.Content = "Activate License";
            }
        }

        private async void BtnClaimTrial_Click(object sender, RoutedEventArgs e)
        {
            var name = TxtTrialName.Text.Trim();
            var email = TxtTrialEmail.Text.Trim();

            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show("Please enter your name.", "Family Loader Licensing", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
            {
                MessageBox.Show("Please enter a valid email address.", "Family Loader Licensing", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BtnClaimTrial.IsEnabled = false;
            BtnClaimTrial.Content = "Connecting...";
            TxtStatusMessage.Text = "Connecting to licensing server (waking server if needed, ~20s)...";

            try
            {
                var result = await LicensingManager.Client.ClaimTrialAsync(name, email, product: "FamilyLoader");
                LicensingManager.UpdateStatus(result);
                RefreshUiState();

                if (result.IsGranted)
                {
                    MessageBox.Show("Your 14-day trial has started! All Family Loader features are now unlocked.", "Trial Activated", MessageBoxButton.OK, MessageBoxImage.Information);
                    DialogResult = true;
                    Close();
                }
                else
                {
                    MessageBox.Show(result.Message, "Trial Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Trial claim failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnClaimTrial.IsEnabled = true;
                BtnClaimTrial.Content = "Claim 14-Day Free Trial";
            }
        }

        private async void BtnDeactivate_Click(object sender, RoutedEventArgs e)
        {
            var key = LicensingManager.CurrentStatus?.Payload?.LicenseKey;
            if (string.IsNullOrEmpty(key))
            {
                return;
            }

            var confirm = MessageBox.Show(
                "Are you sure you want to deactivate Family Loader on this machine?\nThis will release the license seat so you can activate it on another computer.",
                "Confirm Deactivation",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            BtnDeactivate.IsEnabled = false;
            BtnDeactivate.Content = "Deactivating...";

            try
            {
                var success = await LicensingManager.Client.DeactivateLicenseAsync(key);
                LicensingManager.UpdateStatus(LicenseValidationResult.Denied(LicenseStatus.Unlicensed, "License deactivated."));
                RefreshUiState();

                if (success)
                {
                    MessageBox.Show("Seat released successfully. You can now activate this key on another machine.", "Deactivated", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show("License was cleared locally, but server deactivation could not be confirmed.", "Notice", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            finally
            {
                BtnDeactivate.IsEnabled = true;
                BtnDeactivate.Content = "Deactivate on this PC (Transfer Seat)";
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
