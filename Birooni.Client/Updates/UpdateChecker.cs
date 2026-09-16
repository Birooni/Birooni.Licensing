using System.Net.Http.Json;

namespace Birooni.Client.Updates;

public class UpdateChecker
{
    private readonly HttpClient _httpClient;
    private readonly string _apiBaseUrl;

    public UpdateChecker(string apiBaseUrl, HttpClient? httpClient = null)
    {
        _apiBaseUrl = apiBaseUrl.TrimEnd('/');
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    /// <summary>
    /// Checks the server for a newer release. Designed to run as a non-blocking background task.
    /// Returns null if network or server is unreachable.
    /// </summary>
    public async Task<AppUpdateInfo?> CheckAsync(string product, string currentVersion, string? revitVersion = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"{_apiBaseUrl}/api/updates/check?product={Uri.EscapeDataString(product)}&version={Uri.EscapeDataString(currentVersion)}";
            if (!string.IsNullOrWhiteSpace(revitVersion))
            {
                url += $"&revitVersion={Uri.EscapeDataString(revitVersion)}";
            }

            return await _httpClient.GetFromJsonAsync<AppUpdateInfo>(url, cancellationToken);
        }
        catch
        {
            // Silent error handling: update check must never block or crash Revit
            return null;
        }
    }
}
