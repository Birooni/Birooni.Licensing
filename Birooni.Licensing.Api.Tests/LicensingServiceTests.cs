using Birooni.Licensing.Api.Data;
using Birooni.Licensing.Api.DTOs.Requests;
using Birooni.Licensing.Api.Models;
using Birooni.Licensing.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Birooni.Licensing.Api.Tests;

public class LicensingServiceTests
{
    private LicensingDbContext CreateInMemoryDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<LicensingDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;

        return new LicensingDbContext(options);
    }

    private TokenSigner CreateTokenSigner()
    {
        var config = new ConfigurationBuilder().Build();
        return new TokenSigner(config, NullLogger<TokenSigner>.Instance);
    }

    [Fact]
    public async Task Trial_Issues14DayTrial_AndActivatesDevice()
    {
        using var context = CreateInMemoryDbContext(nameof(Trial_Issues14DayTrial_AndActivatesDevice));
        using var signer = CreateTokenSigner();
        var service = new LicensingService(context, signer, NullLogger<LicensingService>.Instance);

        var request = new TrialLicenseRequest
        {
            CustomerName = "Dr. Birooni",
            CustomerEmail = "birooni@example.com",
            DeviceId = "HW-DEVICE-100",
            DeviceName = "Workstation 1",
            Product = "Birooni CAD Plugin",
            PluginVersion = "1.0.0"
        };

        var result = await service.TrialAsync(request, "127.0.0.1");

        Assert.True(result.Success);
        Assert.NotNull(result.LicenseKey);
        Assert.StartsWith("TRIAL-", result.LicenseKey);
        Assert.NotNull(result.ExpiresAt);
        Assert.NotNull(result.Token);

        // Verify validity is ~14 days
        var diff = result.ExpiresAt.Value - DateTimeOffset.UtcNow;
        Assert.InRange(diff.TotalDays, 13.9, 14.1);

        // Verify database records
        var license = await context.Licenses.FirstOrDefaultAsync(l => l.LicenseKey == result.LicenseKey);
        Assert.NotNull(license);
        Assert.Equal("Trial", license.LicenseType);
        Assert.Equal(1, license.MaxActivations);

        var activation = await context.Activations.FirstOrDefaultAsync(a => a.LicenseId == license.Id && a.DeviceId == "HW-DEVICE-100");
        Assert.NotNull(activation);
        Assert.True(activation.IsActive);

        var log = await context.ValidationLogs.FirstOrDefaultAsync(v => v.LicenseId == license.Id && v.Status == "TrialIssued");
        Assert.NotNull(log);
    }

    [Fact]
    public async Task Trial_RejectsSubsequentTrial_ForSameDeviceId()
    {
        using var context = CreateInMemoryDbContext(nameof(Trial_RejectsSubsequentTrial_ForSameDeviceId));
        using var signer = CreateTokenSigner();
        var service = new LicensingService(context, signer, NullLogger<LicensingService>.Instance);

        var request1 = new TrialLicenseRequest
        {
            CustomerName = "First User",
            CustomerEmail = "user1@example.com",
            DeviceId = "SHARED-DEVICE-999",
            Product = "Birooni CAD",
        };

        var result1 = await service.TrialAsync(request1, "127.0.0.1");
        Assert.True(result1.Success);

        // Try requesting a second trial with same DeviceId
        var request2 = new TrialLicenseRequest
        {
            CustomerName = "Second User",
            CustomerEmail = "user2@example.com",
            DeviceId = "SHARED-DEVICE-999",
            Product = "Birooni CAD",
        };

        var result2 = await service.TrialAsync(request2, "127.0.0.1");
        Assert.False(result2.Success);
        Assert.Contains("already been issued", result2.Message);
    }

    [Fact]
    public async Task Activate_EnforcesMaxActivationsLimit()
    {
        using var context = CreateInMemoryDbContext(nameof(Activate_EnforcesMaxActivationsLimit));
        using var signer = CreateTokenSigner();
        var service = new LicensingService(context, signer, NullLogger<LicensingService>.Instance);

        var license = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "SINGLE-SEAT-KEY-1234",
            Product = "Birooni CAD",
            CustomerName = "Engineering Dept",
            CustomerEmail = "eng@example.com",
            LicenseType = "Standard",
            MaxActivations = 1,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        };
        context.Licenses.Add(license);
        await context.SaveChangesAsync();

        // 1. Activate on first device -> should succeed
        var res1 = await service.ActivateAsync(new ActivateLicenseRequest
        {
            LicenseKey = license.LicenseKey,
            DeviceId = "PC-1",
            DeviceName = "Laptop 1"
        }, "10.0.0.1");
        Assert.True(res1.Success);

        // 2. Activate on second device -> should fail because MaxActivations is 1
        var res2 = await service.ActivateAsync(new ActivateLicenseRequest
        {
            LicenseKey = license.LicenseKey,
            DeviceId = "PC-2",
            DeviceName = "Desktop 2"
        }, "10.0.0.2");
        Assert.False(res2.Success);
        Assert.Contains("limit (1) reached", res2.Message);
    }

    [Fact]
    public async Task Deactivate_FreesActivationSlot_AllowingNewDevice()
    {
        using var context = CreateInMemoryDbContext(nameof(Deactivate_FreesActivationSlot_AllowingNewDevice));
        using var signer = CreateTokenSigner();
        var service = new LicensingService(context, signer, NullLogger<LicensingService>.Instance);

        var license = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "MOVE-KEY-999",
            Product = "Birooni CAD",
            CustomerName = "Design Team",
            CustomerEmail = "design@example.com",
            MaxActivations = 1,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        };
        context.Licenses.Add(license);
        await context.SaveChangesAsync();

        // Activate PC-1
        await service.ActivateAsync(new ActivateLicenseRequest
        {
            LicenseKey = license.LicenseKey,
            DeviceId = "PC-1"
        }, "10.0.0.1");

        // Deactivate PC-1
        var deactResult = await service.DeactivateAsync(new DeactivateLicenseRequest
        {
            LicenseKey = license.LicenseKey,
            DeviceId = "PC-1"
        }, "10.0.0.1");
        Assert.True(deactResult.Success);
        Assert.Equal(0, deactResult.ActiveActivationsCount);

        // Now activate PC-2 -> should succeed!
        var res2 = await service.ActivateAsync(new ActivateLicenseRequest
        {
            LicenseKey = license.LicenseKey,
            DeviceId = "PC-2"
        }, "10.0.0.2");
        Assert.True(res2.Success);
    }

    [Fact]
    public async Task Validate_ReturnsValid_ForActivatedDevice()
    {
        using var context = CreateInMemoryDbContext(nameof(Validate_ReturnsValid_ForActivatedDevice));
        using var signer = CreateTokenSigner();
        var service = new LicensingService(context, signer, NullLogger<LicensingService>.Instance);

        var license = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "VALID-TEST-KEY",
            Product = "Birooni CAD",
            CustomerName = "Test",
            CustomerEmail = "test@example.com",
            MaxActivations = 5,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        };
        context.Licenses.Add(license);
        await context.SaveChangesAsync();

        await service.ActivateAsync(new ActivateLicenseRequest
        {
            LicenseKey = license.LicenseKey,
            DeviceId = "DEV-TEST-1"
        }, "10.0.0.1");

        var validateRes = await service.ValidateAsync(new ValidateLicenseRequest
        {
            LicenseKey = license.LicenseKey,
            DeviceId = "DEV-TEST-1"
        }, "10.0.0.1");

        Assert.True(validateRes.Success);
        Assert.NotNull(validateRes.Token);
        Assert.True(signer.Verify(validateRes.Token.PayloadJson, validateRes.Token.Signature));
    }
}
