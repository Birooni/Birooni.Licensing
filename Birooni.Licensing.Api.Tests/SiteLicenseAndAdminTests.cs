using Birooni.Licensing.Api.Data;
using Birooni.Licensing.Api.DTOs.Requests;
using Birooni.Licensing.Api.Models;
using Birooni.Licensing.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Birooni.Licensing.Api.Tests;

public class SiteLicenseAndAdminTests
{
    private (LicensingDbContext db, LicensingService service) CreateTestContext()
    {
        var options = new DbContextOptionsBuilder<LicensingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var db = new LicensingDbContext(options);
        var config = new ConfigurationBuilder().Build();
        var signer = new TokenSigner(config, NullLogger<TokenSigner>.Instance);
        var service = new LicensingService(db, signer, NullLogger<LicensingService>.Instance);

        return (db, service);
    }

    [Fact]
    public async Task SiteLicense_AllowsActivation_WhenUserEmailMatchesAuthorizedDomain()
    {
        var (db, service) = CreateTestContext();

        // Create Site License for @gensler.com
        var license = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "SITE-GENSLER-2026",
            Product = "BiruBox",
            CustomerName = "Gensler BIM Team",
            CustomerEmail = "bim-lead@gensler.com",
            Company = "Gensler",
            LicenseType = "Commercial",
            LicenseMode = "SiteLicense",
            AllowedDomain = "@gensler.com",
            MaxActivations = 100,
            IsActive = true
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();

        // Architect from @gensler.com activates
        var result = await service.ActivateAsync(new ActivateLicenseRequest
        {
            LicenseKey = "SITE-GENSLER-2026",
            DeviceId = "DEV-GENSLER-PC-01",
            DeviceName = "Design-Rig-1",
            UserEmail = "sarah.architect@gensler.com"
        }, null);

        Assert.True(result.Success);
        Assert.NotNull(result.Token);
    }

    [Fact]
    public async Task SiteLicense_RejectsActivation_WhenUserEmailDoesNotMatchDomain()
    {
        var (db, service) = CreateTestContext();

        var license = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "SITE-GENSLER-2026",
            Product = "BiruBox",
            CustomerName = "Gensler BIM Team",
            CustomerEmail = "bim-lead@gensler.com",
            LicenseType = "Commercial",
            LicenseMode = "SiteLicense",
            AllowedDomain = "@gensler.com",
            MaxActivations = 100,
            IsActive = true
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();

        // User with external email tries to activate
        var result = await service.ActivateAsync(new ActivateLicenseRequest
        {
            LicenseKey = "SITE-GENSLER-2026",
            DeviceId = "DEV-ROGUE-PC",
            UserEmail = "freelancer@gmail.com"
        }, null);

        Assert.False(result.Success);
        Assert.Contains("corporate domain", result.Message);
    }

    [Fact]
    public async Task Admin_CreateCustomLicense_GeneratesKeyAndSavesRecord()
    {
        var (db, service) = CreateTestContext();

        var res = await service.CreateCustomLicenseAsync(new CreateCustomLicenseRequest
        {
            Product = "BiruBox",
            CustomerName = "Foster + Partners",
            CustomerEmail = "cad@foster.com",
            Company = "Foster + Partners",
            LicenseMode = "Individual",
            LicenseType = "Commercial",
            MaxActivations = 5,
            DurationDays = 365
        });

        Assert.True(res.Success);
        Assert.StartsWith("BIRU-", res.LicenseKey);

        var saved = await db.Licenses.FirstOrDefaultAsync(l => l.LicenseKey == res.LicenseKey);
        Assert.NotNull(saved);
        Assert.Equal("Foster + Partners", saved.Company);
        Assert.Equal(5, saved.MaxActivations);
    }

    [Fact]
    public async Task Admin_RevokeLicense_DeactivatesLicenseAndAllDeviceSeats()
    {
        var (db, service) = CreateTestContext();

        var createRes = await service.CreateCustomLicenseAsync(new CreateCustomLicenseRequest
        {
            Product = "BiruBox",
            CustomerName = "Test Client",
            CustomerEmail = "client@firm.com",
            MaxActivations = 2
        });

        // Activate device
        await service.ActivateAsync(new ActivateLicenseRequest
        {
            LicenseKey = createRes.LicenseKey!,
            DeviceId = "DEV-PC-01"
        }, null);

        var lic = await db.Licenses.FirstAsync(l => l.LicenseKey == createRes.LicenseKey);
        var act = await db.Activations.FirstAsync(a => a.LicenseId == lic.Id);
        Assert.True(act.IsActive);

        // Revoke
        var revoked = await service.RevokeLicenseAsync(lic.Id, "Refunded");
        Assert.True(revoked);

        var updatedLic = await db.Licenses.FindAsync(lic.Id);
        var updatedAct = await db.Activations.FindAsync(act.Id);

        Assert.False(updatedLic!.IsActive);
        Assert.False(updatedAct!.IsActive);
    }
}
