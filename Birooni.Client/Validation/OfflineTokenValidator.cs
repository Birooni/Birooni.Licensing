using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Birooni.Client.Models;

namespace Birooni.Client.Validation;

public class OfflineTokenValidator
{
    private readonly RSA _rsa;

    public OfflineTokenValidator(string publicKeyPem)
    {
        if (string.IsNullOrWhiteSpace(publicKeyPem))
        {
            throw new ArgumentException("RSA Public Key PEM must be provided for offline token validation.", nameof(publicKeyPem));
        }

        _rsa = RSA.Create();
        _rsa.ImportFromPem(publicKeyPem.Trim());
    }

    public LicenseValidationResult Validate(CachedToken? token, string currentDeviceId)
    {
        if (token == null)
        {
            return LicenseValidationResult.Denied(LicenseStatus.Unlicensed, "No cached license found.");
        }

        // 1. Verify RSA-SHA256 PKCS#1 digital signature
        try
        {
            var payloadBytes = Encoding.UTF8.GetBytes(token.PayloadJson);
            var signatureBytes = Convert.FromBase64String(token.Signature);

            var isValidSignature = _rsa.VerifyData(payloadBytes, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            if (!isValidSignature)
            {
                return LicenseValidationResult.Denied(LicenseStatus.SignatureInvalid, "Cryptographic signature validation failed. The license token may be corrupted or tampered with.");
            }
        }
        catch (Exception ex)
        {
            return LicenseValidationResult.Denied(LicenseStatus.SignatureInvalid, $"Cryptographic verification error: {ex.Message}");
        }

        // 2. Deserialize and inspect payload
        LicensePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<LicensePayload>(token.PayloadJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (payload == null)
            {
                return LicenseValidationResult.Denied(LicenseStatus.SignatureInvalid, "Invalid license payload structure.");
            }
        }
        catch (Exception ex)
        {
            return LicenseValidationResult.Denied(LicenseStatus.SignatureInvalid, $"Failed to parse license payload: {ex.Message}");
        }

        // 3. Verify hardware binding (Device ID)
        if (!string.Equals(payload.DeviceId, currentDeviceId, StringComparison.OrdinalIgnoreCase))
        {
            return LicenseValidationResult.Denied(LicenseStatus.DeviceMismatch,
                $"License is tied to device '{payload.DeviceId}', but current machine is '{currentDeviceId}'.");
        }

        // 4. Verify overall license expiration
        if (payload.ExpiresAt.HasValue && payload.ExpiresAt.Value < DateTimeOffset.UtcNow)
        {
            return LicenseValidationResult.Denied(LicenseStatus.Expired,
                $"License expired on {payload.ExpiresAt.Value:yyyy-MM-dd HH:mm:ss} UTC.");
        }

        // 5. Verify offline validation lease (ValidUntil)
        if (token.ValidUntil < DateTimeOffset.UtcNow || payload.ValidUntil < DateTimeOffset.UtcNow)
        {
            return LicenseValidationResult.Denied(LicenseStatus.Expired,
                $"Offline validation window expired on {token.ValidUntil:yyyy-MM-dd HH:mm:ss} UTC. An internet connection is required to refresh validation.");
        }

        return LicenseValidationResult.Granted(
            LicenseStatus.ValidOffline,
            $"License valid offline until {token.ValidUntil:yyyy-MM-dd HH:mm:ss} UTC.",
            payload,
            token);
    }
}
