using System.Net.Http.Json;
using System.Text.Json;
using Birooni.Client.Hardware;
using Birooni.Client.Models;
using Birooni.Client.Storage;
using Birooni.Client.Validation;

namespace Birooni.Client;

public class BirooniLicenseClient
{
    private readonly HttpClient _httpClient;
    private readonly LicenseCacheManager _cacheManager;
    private readonly OfflineTokenValidator _validator;
    private readonly string _pluginVersion;

    public string DeviceFingerprint { get; }
    public string ApiBaseUrl { get; }

    /// <summary>
    /// Raised when asynchronous background validation ping finishes.
    /// Allows the Revit UI to receive notification if license status changed.
    /// </summary>
    public event EventHandler<LicenseValidationResult>? BackgroundValidationCompleted;

    public BirooniLicenseClient(
        string apiBaseUrl,
        string publicKeyPem,
        string pluginVersion = "2026.1.0",
        HttpClient? httpClient = null,
        LicenseCacheManager? cacheManager = null)
    {
        ApiBaseUrl = apiBaseUrl.TrimEnd('/');
        _pluginVersion = pluginVersion;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _cacheManager = cacheManager ?? new LicenseCacheManager();
        _validator = new OfflineTokenValidator(publicKeyPem);

        DeviceFingerprint = HardwareFingerprintCollector.GetDeviceFingerprint();
    }

    /// <summary>
    /// Non-blocking entry point designed for Autodesk Revit addins (IExternalApplication.OnStartup).
    /// Immediately inspects the local %ProgramData% cache to grant UI access without network latency.
    /// Concurrently dispatches a background task to refresh validation with the server.
    /// </summary>
    public async Task<LicenseValidationResult> InitializeLicenseAsync()
    {
        // 1. Inspect local cache immediately (offline fast-path)
        var cachedToken = _cacheManager.LoadToken();
        var offlineResult = _validator.Validate(cachedToken, DeviceFingerprint);

        if (offlineResult.IsGranted)
        {
            // 2. Dispatch background server validation without awaiting (non-blocking)
            _ = Task.Run(async () =>
            {
                try
                {
                    await RefreshValidationServerPingAsync(offlineResult.Payload?.LicenseKey);
                }
                catch
                {
                    // Failures in background ping do NOT revoke valid offline session
                }
            });

            return offlineResult;
        }

        // If offline validation failed or no cache exists, attempt one synchronous online check
        // if license key is available from previously cached payload
        if (cachedToken != null)
        {
            var serverResult = await RefreshValidationServerPingAsync(offlineResult.Payload?.LicenseKey);
            if (serverResult.IsGranted)
            {
                return serverResult;
            }
        }

        return offlineResult;
    }

    /// <summary>
    /// Activates a purchased license key on this machine.
    /// </summary>
    public async Task<LicenseValidationResult> ActivateLicenseAsync(string licenseKey, string? deviceName = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new
            {
                LicenseKey = licenseKey.Trim(),
                DeviceId = DeviceFingerprint,
                DeviceName = deviceName ?? Environment.MachineName,
                PluginVersion = _pluginVersion
            };

            var response = await _httpClient.PostAsJsonAsync($"{ApiBaseUrl}/api/license/activate", request, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            var apiResult = JsonSerializer.Deserialize<ServerLicenseResponse>(content, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (response.IsSuccessStatusCode && apiResult?.Success == true && apiResult.Token != null)
            {
                var cachedToken = new CachedToken(
                    apiResult.Token.Token,
                    apiResult.Token.PayloadJson,
                    apiResult.Token.Signature,
                    apiResult.Token.ValidUntil,
                    apiResult.Token.ExpiresAt
                );

                _cacheManager.SaveToken(cachedToken);
                return _validator.Validate(cachedToken, DeviceFingerprint);
            }

            return LicenseValidationResult.Denied(
                LicenseStatus.Unlicensed,
                apiResult?.Message ?? $"Activation failed with status code {response.StatusCode}");
        }
        catch (Exception ex)
        {
            return LicenseValidationResult.Denied(LicenseStatus.NetworkError, $"Network error during activation: {ex.Message}");
        }
    }

    /// <summary>
    /// Issues a new 14-day trial for this device and activates it locally.
    /// </summary>
    public async Task<LicenseValidationResult> ClaimTrialAsync(
        string customerName,
        string customerEmail,
        string? deviceName = null,
        string product = "Birooni",
        CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new
            {
                CustomerName = customerName.Trim(),
                CustomerEmail = customerEmail.Trim(),
                DeviceId = DeviceFingerprint,
                DeviceName = deviceName ?? Environment.MachineName,
                Product = product,
                PluginVersion = _pluginVersion
            };

            var response = await _httpClient.PostAsJsonAsync($"{ApiBaseUrl}/api/license/trial", request, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            var apiResult = JsonSerializer.Deserialize<ServerLicenseResponse>(content, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (response.IsSuccessStatusCode && apiResult?.Success == true && apiResult.Token != null)
            {
                var cachedToken = new CachedToken(
                    apiResult.Token.Token,
                    apiResult.Token.PayloadJson,
                    apiResult.Token.Signature,
                    apiResult.Token.ValidUntil,
                    apiResult.Token.ExpiresAt
                );

                _cacheManager.SaveToken(cachedToken);
                return _validator.Validate(cachedToken, DeviceFingerprint);
            }

            return LicenseValidationResult.Denied(
                LicenseStatus.Unlicensed,
                apiResult?.Message ?? $"Trial claim failed with status code {response.StatusCode}");
        }
        catch (Exception ex)
        {
            return LicenseValidationResult.Denied(LicenseStatus.NetworkError, $"Network error claiming trial: {ex.Message}");
        }
    }

    /// <summary>
    /// Deactivates this machine's activation and releases the seat.
    /// </summary>
    public async Task<bool> DeactivateLicenseAsync(string licenseKey, CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new
            {
                LicenseKey = licenseKey.Trim(),
                DeviceId = DeviceFingerprint
            };

            var response = await _httpClient.PostAsJsonAsync($"{ApiBaseUrl}/api/license/deactivate", request, cancellationToken);
            _cacheManager.ClearToken();

            return response.IsSuccessStatusCode;
        }
        catch
        {
            _cacheManager.ClearToken();
            return false;
        }
    }

    private async Task<LicenseValidationResult> RefreshValidationServerPingAsync(string? licenseKey)
    {
        if (string.IsNullOrWhiteSpace(licenseKey))
        {
            return LicenseValidationResult.Denied(LicenseStatus.Unlicensed, "No license key available to validate.");
        }

        try
        {
            var request = new
            {
                LicenseKey = licenseKey,
                DeviceId = DeviceFingerprint,
                PluginVersion = _pluginVersion
            };

            var response = await _httpClient.PostAsJsonAsync($"{ApiBaseUrl}/api/license/validate", request);
            var content = await response.Content.ReadAsStringAsync();

            var apiResult = JsonSerializer.Deserialize<ServerLicenseResponse>(content, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (response.IsSuccessStatusCode && apiResult?.Success == true && apiResult.Token != null)
            {
                var updatedToken = new CachedToken(
                    apiResult.Token.Token,
                    apiResult.Token.PayloadJson,
                    apiResult.Token.Signature,
                    apiResult.Token.ValidUntil,
                    apiResult.Token.ExpiresAt
                );

                _cacheManager.SaveToken(updatedToken);
                var validationResult = _validator.Validate(updatedToken, DeviceFingerprint);
                BackgroundValidationCompleted?.Invoke(this, validationResult);
                return validationResult;
            }

            var failedResult = LicenseValidationResult.Denied(LicenseStatus.Unlicensed, apiResult?.Message ?? "Server validation rejected license.");
            BackgroundValidationCompleted?.Invoke(this, failedResult);
            return failedResult;
        }
        catch (Exception ex)
        {
            var errResult = LicenseValidationResult.Denied(LicenseStatus.NetworkError, $"Background validation network failure: {ex.Message}");
            BackgroundValidationCompleted?.Invoke(this, errResult);
            return errResult;
        }
    }

    private class ServerLicenseResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public string? LicenseKey { get; set; }
        public ServerToken? Token { get; set; }
    }

    private class ServerToken
    {
        public string Token { get; set; } = string.Empty;
        public string PayloadJson { get; set; } = string.Empty;
        public string Signature { get; set; } = string.Empty;
        public DateTimeOffset ValidUntil { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
    }
}
