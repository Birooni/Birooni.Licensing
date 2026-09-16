namespace Birooni.Licensing.Api.DTOs.Responses;

public record SignedLicenseToken(
    string Token,
    string PayloadJson,
    string Signature,
    DateTimeOffset ValidUntil,
    DateTimeOffset? ExpiresAt
);
