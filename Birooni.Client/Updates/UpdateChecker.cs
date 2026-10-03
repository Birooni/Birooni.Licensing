using System.Net.Http.Json;
using System.Text.Json;

namespace Birooni.Client.Updates;

public class UpdateChecker
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient _httpClient;
    private readonly string _apiBaseUrl;

    public UpdateChecker(string apiBaseUrl, HttpClient? httpClient = null)
    {
        _apiBaseUrl = apiBaseUrl.TrimEnd('/');
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
    }

    /// <summary>
    /// Checks the licensing API, then a static manifest on the website if the API is cold or unreachable.
    /// </summary>
    public async Task<AppUpdateInfo?> CheckAsync(
        string product,
        string currentVersion,
        string? revitVersion = null,
        string? fallbackManifestUrl = null,
        CancellationToken cancellationToken = default)
    {
        AppUpdateInfo? info = null;
        for (var attempt = 0; attempt < 3 && info == null; attempt++)
        {
            if (attempt > 0)
            {
                try { await Task.Delay(2000 * attempt, cancellationToken); } catch { return info; }
            }

            info = await TryGetAsync(BuildApiUrl(product, currentVersion, revitVersion), cancellationToken);
        }

        if (info == null && !string.IsNullOrWhiteSpace(fallbackManifestUrl))
        {
            info = await TryGetAsync(fallbackManifestUrl!, cancellationToken);
            if (info != null)
            {
                info.CurrentVersion = currentVersion;
                info.UpdateAvailable = IsNewer(info.LatestVersion, currentVersion);
                if (!info.UpdateAvailable)
                {
                    info.DownloadUrl = null;
                }
            }
        }

        return info;
    }

    private string BuildApiUrl(string product, string currentVersion, string? revitVersion)
    {
        var url = $"{_apiBaseUrl}/api/updates/check?product={Uri.EscapeDataString(product)}&version={Uri.EscapeDataString(currentVersion)}";
        if (!string.IsNullOrWhiteSpace(revitVersion))
        {
            url += $"&revitVersion={Uri.EscapeDataString(revitVersion)}";
        }

        try
        {
            var deviceId = Hardware.HardwareFingerprintCollector.GetDeviceFingerprint();
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                url += "&deviceId=" + Uri.EscapeDataString(deviceId);
            }

            var deviceName = Environment.MachineName;
            if (!string.IsNullOrWhiteSpace(deviceName))
            {
                url += "&deviceName=" + Uri.EscapeDataString(deviceName);
            }
        }
        catch
        {
            // Install reporting must never break update checks.
        }

        return url;
    }

    private async Task<AppUpdateInfo?> TryGetAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<AppUpdateInfo>(url, JsonOptions, cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    internal static bool IsNewer(string? latest, string current)
    {
        if (string.IsNullOrWhiteSpace(latest)) return false;
        if (Version.TryParse(Normalize(latest), out var l) && Version.TryParse(Normalize(current), out var c))
        {
            return l > c;
        }

        return !string.Equals(Normalize(latest), Normalize(current), StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string version)
    {
        var clean = version.Trim().TrimStart('v', 'V');
        var parts = clean.Split('.');
        if (parts.Length == 1) return clean + ".0.0";
        if (parts.Length == 2) return clean + ".0";
        return clean;
    }
}
