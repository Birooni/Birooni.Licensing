using Birooni.Licensing.Api.Data;
using Birooni.Licensing.Api.DTOs.Requests;
using Birooni.Licensing.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Birooni.Licensing.Api.Tests;

public class UpdateServiceTests
{
    private LicensingDbContext CreateInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<LicensingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new LicensingDbContext(options);
    }

    [Fact]
    public async Task CheckForUpdate_ReturnsUpdateAvailable_WhenNewerVersionExists()
    {
        using var db = CreateInMemoryDbContext();
        var service = new UpdateService(db, NullLogger<UpdateService>.Instance);

        await service.CreateReleaseAsync(new CreateReleaseRequest
        {
            Product = "BiruBox",
            Version = "1.1.0",
            RevitVersion = "All",
            DownloadUrl = "https://download.birooni.com/BiruBox-Setup-v1.1.0.msi",
            ReleaseNotes = "Auto section buffer feature",
            IsMandatory = true
        });

        var response = await service.CheckForUpdateAsync("BiruBox", "1.0.0", "2025");

        Assert.True(response.UpdateAvailable);
        Assert.Equal("1.1.0", response.LatestVersion);
        Assert.Equal("https://download.birooni.com/BiruBox-Setup-v1.1.0.msi", response.DownloadUrl);
        Assert.True(response.IsMandatory);
    }

    [Fact]
    public async Task CheckForUpdate_ReturnsFalse_WhenCurrentVersionIsUpToDate()
    {
        using var db = CreateInMemoryDbContext();
        var service = new UpdateService(db, NullLogger<UpdateService>.Instance);

        await service.CreateReleaseAsync(new CreateReleaseRequest
        {
            Product = "BiruBox",
            Version = "1.0.0",
            RevitVersion = "All",
            DownloadUrl = "https://download.birooni.com/BiruBox-Setup.msi"
        });

        var response = await service.CheckForUpdateAsync("BiruBox", "1.0.0", "2025");

        Assert.False(response.UpdateAvailable);
    }

    [Fact]
    public async Task CheckForUpdate_RespectsRevitVersionFilter()
    {
        using var db = CreateInMemoryDbContext();
        var service = new UpdateService(db, NullLogger<UpdateService>.Instance);

        // Release specifically for Revit 2026 only
        await service.CreateReleaseAsync(new CreateReleaseRequest
        {
            Product = "BiruBox",
            Version = "2.0.0",
            RevitVersion = "2026",
            DownloadUrl = "https://download.birooni.com/BiruBox-2026.msi"
        });

        // Check for Revit 2025 -> should NOT see 2.0.0
        var response2025 = await service.CheckForUpdateAsync("BiruBox", "1.0.0", "2025");
        Assert.False(response2025.UpdateAvailable);

        // Check for Revit 2026 -> SHOULD see 2.0.0
        var response2026 = await service.CheckForUpdateAsync("BiruBox", "1.0.0", "2026");
        Assert.True(response2026.UpdateAvailable);
        Assert.Equal("2.0.0", response2026.LatestVersion);
    }
}
