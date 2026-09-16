using Birooni.Licensing.Api.DTOs.Responses;

namespace Birooni.Licensing.Api.Services;

public interface ITokenSigner
{
    /// <summary>
    /// Signs a license payload containing LicenseKey, DeviceId, ExpiresAt, and ValidUntil (DateTimeOffset.UtcNow + 14 days).
    /// </summary>
    SignedLicenseToken Sign(string licenseKey, string deviceId, DateTimeOffset? expiresAt, string? product = null, string? licenseType = null);

    /// <summary>
    /// Signs an explicit LicenseTokenPayload.
    /// </summary>
    SignedLicenseToken SignPayload(LicenseTokenPayload payload);

    /// <summary>
    /// Verifies that the given payload JSON matches the RSA-SHA256 PKCS#1 signature.
    /// </summary>
    bool Verify(string payloadJson, string signatureBase64);

    /// <summary>
    /// Exports the public key in SubjectPublicKeyInfo PEM format for client-side offline verification.
    /// </summary>
    string GetPublicKeyPem();
}
