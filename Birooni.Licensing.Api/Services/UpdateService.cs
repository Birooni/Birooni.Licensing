using Birooni.Licensing.Api.Data;
using Birooni.Licensing.Api.DTOs.Requests;
using Birooni.Licensing.Api.DTOs.Responses;
using Birooni.Licensing.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Birooni.Licensing.Api.Services;

public class UpdateService : IUpdateService
{
    private readonly LicensingDbContext _db;
    private readonly ILogger<UpdateService> _logger;

    public UpdateService(LicensingDbContext db, ILogger<UpdateService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<UpdateCheckResponse> CheckForUpdateAsync(
        string product,
        string currentVersion,
        string? revitVersion = null,
        CancellationToken cancellationToken = default)
    {
        var prod = product.Trim();
        var cleanRev = string.IsNullOrWhiteSpace(revitVersion) ? "All" : revitVersion.Trim();

        // Fetch candidates for this product matching the Revit version or "All"
        var candidates = await _db.ProductReleases
            .Where(r => r.Product.ToLower() == prod.ToLower() &&
                       (r.RevitVersion == "All" || r.RevitVersion == cleanRev))
            .OrderByDescending(r => r.ReleasedAt)
            .ToListAsync(cancellationToken);

        if (!candidates.Any())
        {
            return new UpdateCheckResponse
            {
                UpdateAvailable = false,
                Product = prod,
                CurrentVersion = currentVersion,
                LatestVersion = currentVersion
            };
        }

        // Find the release with the highest semantic version
        ProductRelease? latestRelease = null;
        Version? highestParsedVersion = null;

        foreach (var release in candidates)
        {
            var parsed = ParseVersion(release.Version);
            if (parsed == null) continue;

            if (highestParsedVersion == null || parsed > highestParsedVersion)
            {
                highestParsedVersion = parsed;
                latestRelease = release;
            }
        }

        latestRelease ??= candidates.First();
        var currentParsed = ParseVersion(currentVersion);

        bool isNewer = false;
        if (currentParsed != null && highestParsedVersion != null)
        {
            isNewer = highestParsedVersion > currentParsed;
        }
        else
        {
            isNewer = !string.Equals(latestRelease.Version.TrimStart('v'), currentVersion.TrimStart('v'), StringComparison.OrdinalIgnoreCase);
        }

        return new UpdateCheckResponse
        {
            UpdateAvailable = isNewer,
            Product = prod,
            CurrentVersion = currentVersion,
            LatestVersion = latestRelease.Version,
            DownloadUrl = isNewer ? latestRelease.DownloadUrl : null,
            ReleaseNotes = isNewer ? latestRelease.ReleaseNotes : null,
            IsMandatory = isNewer && latestRelease.IsMandatory,
            ChecksumSha256 = isNewer ? latestRelease.ChecksumSha256 : null,
            ReleasedAt = isNewer ? latestRelease.ReleasedAt : null
        };
    }

    public async Task<ProductRelease> CreateReleaseAsync(CreateReleaseRequest request, CancellationToken cancellationToken = default)
    {
        var release = new ProductRelease
        {
            Product = request.Product.Trim(),
            Version = request.Version.Trim().TrimStart('v'),
            RevitVersion = string.IsNullOrWhiteSpace(request.RevitVersion) ? "All" : request.RevitVersion.Trim(),
            DownloadUrl = request.DownloadUrl.Trim(),
            ReleaseNotes = request.ReleaseNotes?.Trim() ?? string.Empty,
            IsMandatory = request.IsMandatory,
            ChecksumSha256 = request.ChecksumSha256?.Trim(),
            ReleasedAt = DateTimeOffset.UtcNow
        };

        _db.ProductReleases.Add(release);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("New release published: {Product} v{Version} for Revit {Revit}",
            release.Product, release.Version, release.RevitVersion);

        return release;
    }

    public async Task<List<ProductRelease>> GetReleasesAsync(string? product = null, CancellationToken cancellationToken = default)
    {
        var query = _db.ProductReleases.AsQueryable();
        if (!string.IsNullOrWhiteSpace(product))
        {
            query = query.Where(r => r.Product.ToLower() == product.Trim().ToLower());
        }

        return await query.OrderByDescending(r => r.ReleasedAt).ToListAsync(cancellationToken);
    }

    private static Version? ParseVersion(string versionStr)
    {
        if (string.IsNullOrWhiteSpace(versionStr)) return null;
        var clean = versionStr.Trim().TrimStart('v', 'V');
        
        // Handle e.g. "1.0" -> "1.0.0"
        var parts = clean.Split('.');
        if (parts.Length == 1 && int.TryParse(parts[0], out _))
        {
            clean += ".0.0";
        }
        else if (parts.Length == 2)
        {
            clean += ".0";
        }

        return Version.TryParse(clean, out var v) ? v : null;
    }
}
