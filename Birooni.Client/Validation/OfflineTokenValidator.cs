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
#if NET8_0_OR_GREATER
        _rsa.ImportFromPem(publicKeyPem.Trim());
#else
        ImportPemPublicKeyNet48(_rsa, publicKeyPem.Trim());
#endif
    }

#if !NET8_0_OR_GREATER
    private static void ImportPemPublicKeyNet48(RSA rsa, string pem)
    {
        var base64 = pem
            .Replace("-----BEGIN PUBLIC KEY-----", "")
            .Replace("-----END PUBLIC KEY-----", "")
            .Replace("\r", "")
            .Replace("\n", "")
            .Trim();
        byte[] der = Convert.FromBase64String(base64);

        using var ms = new MemoryStream(der);
        using var reader = new BinaryReader(ms);

        if (reader.ReadByte() != 0x30) throw new CryptographicException("Invalid PEM DER: Expected Sequence");
        ReadLength(reader);

        if (reader.ReadByte() != 0x30) throw new CryptographicException("Invalid PEM DER: Expected Algorithm Sequence");
        int algLen = ReadLength(reader);
        reader.ReadBytes(algLen);

        if (reader.ReadByte() != 0x03) throw new CryptographicException("Invalid PEM DER: Expected Bit String");
        ReadLength(reader);
        reader.ReadByte();

        if (reader.ReadByte() != 0x30) throw new CryptographicException("Invalid PEM DER: Expected RSAPublicKey Sequence");
        ReadLength(reader);

        if (reader.ReadByte() != 0x02) throw new CryptographicException("Invalid PEM DER: Expected Modulus Integer");
        int modLen = ReadLength(reader);
        byte[] modulus = reader.ReadBytes(modLen);
        if (modulus.Length > 0 && modulus[0] == 0x00)
        {
            byte[] trimmed = new byte[modulus.Length - 1];
            Buffer.BlockCopy(modulus, 1, trimmed, 0, trimmed.Length);
            modulus = trimmed;
        }

        if (reader.ReadByte() != 0x02) throw new CryptographicException("Invalid PEM DER: Expected Exponent Integer");
        int expLen = ReadLength(reader);
        byte[] exponent = reader.ReadBytes(expLen);

        var rsaParams = new RSAParameters
        {
            Modulus = modulus,
            Exponent = exponent
        };
        rsa.ImportParameters(rsaParams);
    }

    private static int ReadLength(BinaryReader reader)
    {
        byte b = reader.ReadByte();
        if ((b & 0x80) == 0) return b;
        int count = b & 0x7F;
        int len = 0;
        for (int i = 0; i < count; i++)
        {
            len = (len << 8) | reader.ReadByte();
        }
        return len;
    }
#endif

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
