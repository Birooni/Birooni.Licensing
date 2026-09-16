using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Birooni.Licensing.Api.DTOs.Responses;

namespace Birooni.Licensing.Api.Services;

public class TokenSigner : ITokenSigner, IDisposable
{
    private readonly RSA _rsa;
    private readonly ILogger<TokenSigner> _logger;
    private readonly JsonSerializerOptions _jsonOptions;
    private bool _disposed;

    public TokenSigner(IConfiguration configuration, ILogger<TokenSigner> logger)
    {
        _logger = logger;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        _rsa = InitializeRsa(configuration);
    }

    private RSA InitializeRsa(IConfiguration configuration)
    {
        var rsa = RSA.Create(2048);

        // 1. Try environment variable
        var privateKeyEnv = Environment.GetEnvironmentVariable("RSA_PRIVATE_KEY");
        if (!string.IsNullOrWhiteSpace(privateKeyEnv))
        {
            try
            {
                rsa.ImportFromPem(privateKeyEnv.Trim());
                _logger.LogInformation("RSA private key successfully loaded from environment variable RSA_PRIVATE_KEY.");
                return rsa;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse RSA_PRIVATE_KEY environment variable. Falling back to configuration.");
            }
        }

        // 2. Try configuration key or file path
        var privateKeyConfig = configuration["Licensing:RsaPrivateKeyPem"] ?? configuration["Licensing:PrivateKey"];
        if (!string.IsNullOrWhiteSpace(privateKeyConfig))
        {
            try
            {
                if (File.Exists(privateKeyConfig))
                {
                    var filePem = File.ReadAllText(privateKeyConfig);
                    rsa.ImportFromPem(filePem.Trim());
                    _logger.LogInformation("RSA private key loaded from file: {Path}", privateKeyConfig);
                    return rsa;
                }

                rsa.ImportFromPem(privateKeyConfig.Trim());
                _logger.LogInformation("RSA private key loaded from configuration.");
                return rsa;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse RSA private key from configuration. Generating development keypair.");
            }
        }

        // 3. Generate development keypair if none supplied
        _logger.LogWarning("No RSA private key provided in configuration or environment. Generated a transient 2048-bit RSA keypair for development.");
        return rsa;
    }

    public SignedLicenseToken Sign(string licenseKey, string deviceId, DateTimeOffset? expiresAt, string? product = null, string? licenseType = null)
    {
        var validUntil = DateTimeOffset.UtcNow.AddDays(14);
        var payload = new LicenseTokenPayload(
            LicenseKey: licenseKey,
            DeviceId: deviceId,
            ExpiresAt: expiresAt,
            ValidUntil: validUntil,
            IssuedAt: DateTimeOffset.UtcNow,
            Product: product,
            LicenseType: licenseType
        );

        return SignPayload(payload);
    }

    public SignedLicenseToken SignPayload(LicenseTokenPayload payload)
    {
        var payloadJson = JsonSerializer.Serialize(payload, _jsonOptions);
        var payloadBytes = Encoding.UTF8.GetBytes(payloadJson);

        var signatureBytes = _rsa.SignData(payloadBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var signatureBase64 = Convert.ToBase64String(signatureBytes);

        // Compact token format: Base64Url(payload).Base64Url(signature)
        var token = $"{Base64UrlEncode(payloadBytes)}.{Base64UrlEncode(signatureBytes)}";

        return new SignedLicenseToken(
            Token: token,
            PayloadJson: payloadJson,
            Signature: signatureBase64,
            ValidUntil: payload.ValidUntil,
            ExpiresAt: payload.ExpiresAt
        );
    }

    public bool Verify(string payloadJson, string signatureBase64)
    {
        try
        {
            var payloadBytes = Encoding.UTF8.GetBytes(payloadJson);
            var signatureBytes = Convert.FromBase64String(signatureBase64);
            return _rsa.VerifyData(payloadBytes, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error verifying license token signature.");
            return false;
        }
    }

    public string GetPublicKeyPem()
    {
        return _rsa.ExportSubjectPublicKeyInfoPem();
    }

    private static string Base64UrlEncode(byte[] input)
    {
        return Convert.ToBase64String(input)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _rsa.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
