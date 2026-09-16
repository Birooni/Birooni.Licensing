using System.Security.Cryptography;
using Birooni.Licensing.Api.Data;
using Birooni.Licensing.Api.DTOs.Requests;
using Birooni.Licensing.Api.DTOs.Responses;
using Birooni.Licensing.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Birooni.Licensing.Api.Services;

public class LicensingService : ILicensingService
{
    private readonly LicensingDbContext _context;
    private readonly ITokenSigner _tokenSigner;
    private readonly ILogger<LicensingService> _logger;

    public LicensingService(
        LicensingDbContext context,
        ITokenSigner tokenSigner,
        ILogger<LicensingService> logger)
    {
        _context = context;
        _tokenSigner = tokenSigner;
        _logger = logger;
    }

    public async Task<LicenseResult> ActivateAsync(ActivateLicenseRequest request, string? ipAddress, CancellationToken cancellationToken = default)
    {
        var licenseKey = request.LicenseKey.Trim();
        var deviceId = request.DeviceId.Trim();

        var license = await _context.Licenses
            .FirstOrDefaultAsync(l => l.LicenseKey == licenseKey, cancellationToken);

        if (license == null)
        {
            await LogValidationAsync(null, deviceId, "LicenseNotFound", ipAddress, cancellationToken);
            return LicenseResult.Fail("License key not found.", licenseKey);
        }

        if (!license.IsActive)
        {
            await LogValidationAsync(license.Id, deviceId, "LicenseInactive", ipAddress, cancellationToken);
            return LicenseResult.Fail("License is inactive or suspended.", licenseKey);
        }

        if (license.ExpiresAt.HasValue && license.ExpiresAt.Value < DateTimeOffset.UtcNow)
        {
            await LogValidationAsync(license.Id, deviceId, "LicenseExpired", ipAddress, cancellationToken);
            return LicenseResult.Fail($"License expired on {license.ExpiresAt.Value:yyyy-MM-dd HH:mm:ss} UTC.", licenseKey);
        }

        var existingActivation = await _context.Activations
            .FirstOrDefaultAsync(a => a.LicenseId == license.Id && a.DeviceId == deviceId, cancellationToken);

        if (existingActivation != null)
        {
            if (existingActivation.IsActive)
            {
                // Device already activated, refresh timestamp
                existingActivation.LastValidatedAt = DateTimeOffset.UtcNow;
                if (!string.IsNullOrWhiteSpace(request.DeviceName)) existingActivation.DeviceName = request.DeviceName;
                if (!string.IsNullOrWhiteSpace(request.PluginVersion)) existingActivation.PluginVersion = request.PluginVersion;

                await LogValidationAsync(license.Id, deviceId, "Reactivated", ipAddress, cancellationToken);
                await _context.SaveChangesAsync(cancellationToken);

                var activeCount = await _context.Activations.CountAsync(a => a.LicenseId == license.Id && a.IsActive, cancellationToken);
                var token = _tokenSigner.Sign(license.LicenseKey, deviceId, license.ExpiresAt, license.Product, license.LicenseType);

                return LicenseResult.Ok(
                    "Device activation renewed successfully.",
                    license.LicenseKey,
                    license.Product,
                    license.CustomerName,
                    license.LicenseType,
                    license.ExpiresAt,
                    activeCount,
                    license.MaxActivations,
                    token);
            }
            else
            {
                // Reactivate existing dormant activation if seat available
                var activeCount = await _context.Activations.CountAsync(a => a.LicenseId == license.Id && a.IsActive, cancellationToken);
                if (activeCount >= license.MaxActivations)
                {
                    await LogValidationAsync(license.Id, deviceId, "MaxActivationsExceeded", ipAddress, cancellationToken);
                    return LicenseResult.Fail($"Maximum activation limit ({license.MaxActivations}) reached for this license.", licenseKey);
                }

                existingActivation.IsActive = true;
                existingActivation.ActivatedAt = DateTimeOffset.UtcNow;
                existingActivation.LastValidatedAt = DateTimeOffset.UtcNow;
                if (!string.IsNullOrWhiteSpace(request.DeviceName)) existingActivation.DeviceName = request.DeviceName;
                if (!string.IsNullOrWhiteSpace(request.PluginVersion)) existingActivation.PluginVersion = request.PluginVersion;

                await LogValidationAsync(license.Id, deviceId, "Reactivated", ipAddress, cancellationToken);
                await _context.SaveChangesAsync(cancellationToken);

                var updatedCount = activeCount + 1;
                var token = _tokenSigner.Sign(license.LicenseKey, deviceId, license.ExpiresAt, license.Product, license.LicenseType);

                return LicenseResult.Ok(
                    "Device reactivated successfully.",
                    license.LicenseKey,
                    license.Product,
                    license.CustomerName,
                    license.LicenseType,
                    license.ExpiresAt,
                    updatedCount,
                    license.MaxActivations,
                    token);
            }
        }

        // New activation
        var currentActiveCount = await _context.Activations.CountAsync(a => a.LicenseId == license.Id && a.IsActive, cancellationToken);
        if (currentActiveCount >= license.MaxActivations)
        {
            await LogValidationAsync(license.Id, deviceId, "MaxActivationsExceeded", ipAddress, cancellationToken);
            return LicenseResult.Fail($"Maximum activation limit ({license.MaxActivations}) reached for this license.", licenseKey);
        }

        var newActivation = new Activation
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            DeviceId = deviceId,
            DeviceName = request.DeviceName ?? string.Empty,
            PluginVersion = request.PluginVersion ?? string.Empty,
            IsActive = true,
            ActivatedAt = DateTimeOffset.UtcNow,
            LastValidatedAt = DateTimeOffset.UtcNow
        };

        _context.Activations.Add(newActivation);
        await LogValidationAsync(license.Id, deviceId, "Activated", ipAddress, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        var finalActiveCount = currentActiveCount + 1;
        var signedToken = _tokenSigner.Sign(license.LicenseKey, deviceId, license.ExpiresAt, license.Product, license.LicenseType);

        return LicenseResult.Ok(
            "Device activated successfully.",
            license.LicenseKey,
            license.Product,
            license.CustomerName,
            license.LicenseType,
            license.ExpiresAt,
            finalActiveCount,
            license.MaxActivations,
            signedToken);
    }

    public async Task<LicenseResult> ValidateAsync(ValidateLicenseRequest request, string? ipAddress, CancellationToken cancellationToken = default)
    {
        var licenseKey = request.LicenseKey.Trim();
        var deviceId = request.DeviceId.Trim();

        var license = await _context.Licenses
            .FirstOrDefaultAsync(l => l.LicenseKey == licenseKey, cancellationToken);

        if (license == null)
        {
            await LogValidationAsync(null, deviceId, "LicenseNotFound", ipAddress, cancellationToken);
            return LicenseResult.Fail("License key not found.", licenseKey);
        }

        if (!license.IsActive)
        {
            await LogValidationAsync(license.Id, deviceId, "LicenseInactive", ipAddress, cancellationToken);
            return LicenseResult.Fail("License is inactive or suspended.", licenseKey);
        }

        if (license.ExpiresAt.HasValue && license.ExpiresAt.Value < DateTimeOffset.UtcNow)
        {
            await LogValidationAsync(license.Id, deviceId, "LicenseExpired", ipAddress, cancellationToken);
            return LicenseResult.Fail($"License expired on {license.ExpiresAt.Value:yyyy-MM-dd HH:mm:ss} UTC.", licenseKey);
        }

        var activation = await _context.Activations
            .FirstOrDefaultAsync(a => a.LicenseId == license.Id && a.DeviceId == deviceId, cancellationToken);

        if (activation == null || !activation.IsActive)
        {
            await LogValidationAsync(license.Id, deviceId, "DeviceNotActivated", ipAddress, cancellationToken);
            return LicenseResult.Fail("This device is not activated for the specified license.", licenseKey);
        }

        activation.LastValidatedAt = DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(request.PluginVersion))
        {
            activation.PluginVersion = request.PluginVersion;
        }

        await LogValidationAsync(license.Id, deviceId, "Validated", ipAddress, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        var activeCount = await _context.Activations.CountAsync(a => a.LicenseId == license.Id && a.IsActive, cancellationToken);
        var token = _tokenSigner.Sign(license.LicenseKey, deviceId, license.ExpiresAt, license.Product, license.LicenseType);

        return LicenseResult.Ok(
            "License is valid.",
            license.LicenseKey,
            license.Product,
            license.CustomerName,
            license.LicenseType,
            license.ExpiresAt,
            activeCount,
            license.MaxActivations,
            token);
    }

    public async Task<DeactivateResult> DeactivateAsync(DeactivateLicenseRequest request, string? ipAddress, CancellationToken cancellationToken = default)
    {
        var licenseKey = request.LicenseKey.Trim();
        var deviceId = request.DeviceId.Trim();

        var license = await _context.Licenses
            .FirstOrDefaultAsync(l => l.LicenseKey == licenseKey, cancellationToken);

        if (license == null)
        {
            await LogValidationAsync(null, deviceId, "DeactivateLicenseNotFound", ipAddress, cancellationToken);
            return new DeactivateResult
            {
                Success = false,
                Message = "License key not found.",
                LicenseKey = licenseKey,
                DeviceId = deviceId
            };
        }

        var activation = await _context.Activations
            .FirstOrDefaultAsync(a => a.LicenseId == license.Id && a.DeviceId == deviceId && a.IsActive, cancellationToken);

        if (activation == null)
        {
            await LogValidationAsync(license.Id, deviceId, "DeactivateDeviceNotFound", ipAddress, cancellationToken);
            return new DeactivateResult
            {
                Success = false,
                Message = "No active activation found for this device and license.",
                LicenseKey = licenseKey,
                DeviceId = deviceId
            };
        }

        activation.IsActive = false;
        await LogValidationAsync(license.Id, deviceId, "Deactivated", ipAddress, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        var remainingActive = await _context.Activations.CountAsync(a => a.LicenseId == license.Id && a.IsActive, cancellationToken);

        return new DeactivateResult
        {
            Success = true,
            Message = "Device successfully deactivated. License seat released.",
            LicenseKey = licenseKey,
            DeviceId = deviceId,
            ActiveActivationsCount = remainingActive
        };
    }

    public async Task<LicenseResult> TrialAsync(TrialLicenseRequest request, string? ipAddress, CancellationToken cancellationToken = default)
    {
        var deviceId = request.DeviceId.Trim();

        // Ensure a device cannot claim multiple trials
        var existingTrialActivation = await _context.Activations
            .Include(a => a.License)
            .Where(a => a.DeviceId == deviceId && a.License != null && a.License.LicenseType == "Trial")
            .FirstOrDefaultAsync(cancellationToken);

        if (existingTrialActivation != null)
        {
            await LogValidationAsync(existingTrialActivation.LicenseId, deviceId, "TrialAlreadyClaimed", ipAddress, cancellationToken);
            return LicenseResult.Fail($"A trial license has already been issued for device '{deviceId}'.", existingTrialActivation.License?.LicenseKey);
        }

        // Generate 14-day trial license
        var trialKey = GenerateTrialKey();
        var trialExpiry = DateTimeOffset.UtcNow.AddDays(14);

        var license = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = trialKey,
            Product = request.Product.Trim(),
            CustomerName = request.CustomerName.Trim(),
            CustomerEmail = request.CustomerEmail.Trim(),
            LicenseType = "Trial",
            MaxActivations = 1,
            IsActive = true,
            ExpiresAt = trialExpiry,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var activation = new Activation
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            DeviceId = deviceId,
            DeviceName = request.DeviceName ?? string.Empty,
            PluginVersion = request.PluginVersion ?? string.Empty,
            IsActive = true,
            ActivatedAt = DateTimeOffset.UtcNow,
            LastValidatedAt = DateTimeOffset.UtcNow
        };

        _context.Licenses.Add(license);
        _context.Activations.Add(activation);

        await LogValidationAsync(license.Id, deviceId, "TrialIssued", ipAddress, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);

        var token = _tokenSigner.Sign(license.LicenseKey, deviceId, license.ExpiresAt, license.Product, license.LicenseType);

        return LicenseResult.Ok(
            "14-day trial license generated and activated successfully.",
            license.LicenseKey,
            license.Product,
            license.CustomerName,
            license.LicenseType,
            license.ExpiresAt,
            activationsUsed: 1,
            maxActivations: 1,
            token: token);
    }

    private async Task LogValidationAsync(Guid? licenseId, string deviceId, string status, string? ipAddress, CancellationToken cancellationToken)
    {
        try
        {
            var log = new ValidationLog
            {
                LicenseId = licenseId,
                DeviceId = deviceId,
                Status = status,
                IpAddress = ipAddress,
                CreatedAt = DateTimeOffset.UtcNow
            };

            await _context.ValidationLogs.AddAsync(log, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record validation log for device {DeviceId}, status: {Status}", deviceId, status);
        }
    }

    private static string GenerateTrialKey()
    {
        // Generates formatted trial key like TRIAL-A1B2-C3D4-E5F6
        Span<byte> randomBytes = stackalloc byte[6];
        RandomNumberGenerator.Fill(randomBytes);
        var hex = Convert.ToHexString(randomBytes);
        return $"TRIAL-{hex[..4]}-{hex[4..8]}-{hex[8..12]}";
    }
}
