using Birooni.Licensing.Api.DTOs.Requests;
using Birooni.Licensing.Api.DTOs.Responses;

namespace Birooni.Licensing.Api.Services;

public interface ILicensingService
{
    Task<LicenseResult> ActivateAsync(ActivateLicenseRequest request, string? ipAddress, CancellationToken cancellationToken = default);

    Task<LicenseResult> ValidateAsync(ValidateLicenseRequest request, string? ipAddress, CancellationToken cancellationToken = default);

    Task<DeactivateResult> DeactivateAsync(DeactivateLicenseRequest request, string? ipAddress, CancellationToken cancellationToken = default);

    Task<LicenseResult> TrialAsync(TrialLicenseRequest request, string? ipAddress, CancellationToken cancellationToken = default);

    // Admin & Management operations
    Task<AdminStatsResponse> GetAdminStatsAsync(CancellationToken cancellationToken = default);

    Task<List<AdminLicenseDto>> GetAdminLicensesAsync(string? search = null, string? licenseMode = null, bool? isActive = null, CancellationToken cancellationToken = default);

    Task<LicenseResult> CreateCustomLicenseAsync(CreateCustomLicenseRequest request, CancellationToken cancellationToken = default);

    Task<bool> RevokeLicenseAsync(Guid licenseId, string? reason = null, CancellationToken cancellationToken = default);

    Task<List<AdminDeviceDto>> GetAdminDevicesAsync(string? search = null, CancellationToken cancellationToken = default);

    Task<bool> ReleaseDeviceAsync(Guid activationId, CancellationToken cancellationToken = default);
}
