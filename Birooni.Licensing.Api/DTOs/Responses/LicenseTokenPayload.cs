namespace Birooni.Licensing.Api.DTOs.Responses;

public record LicenseTokenPayload(
    string LicenseKey,
    string DeviceId,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset ValidUntil,
    DateTimeOffset IssuedAt,
    string? Product = null,
    string? LicenseType = null
);
