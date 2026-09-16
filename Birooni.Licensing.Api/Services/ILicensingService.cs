using Birooni.Licensing.Api.DTOs.Requests;
using Birooni.Licensing.Api.DTOs.Responses;

namespace Birooni.Licensing.Api.Services;

public interface ILicensingService
{
    Task<LicenseResult> ActivateAsync(ActivateLicenseRequest request, string? ipAddress, CancellationToken cancellationToken = default);

    Task<LicenseResult> ValidateAsync(ValidateLicenseRequest request, string? ipAddress, CancellationToken cancellationToken = default);

    Task<DeactivateResult> DeactivateAsync(DeactivateLicenseRequest request, string? ipAddress, CancellationToken cancellationToken = default);

    Task<LicenseResult> TrialAsync(TrialLicenseRequest request, string? ipAddress, CancellationToken cancellationToken = default);
}
