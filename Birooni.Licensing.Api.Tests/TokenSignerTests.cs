using System.Security.Cryptography;
using System.Text.Json;
using Birooni.Licensing.Api.DTOs.Responses;
using Birooni.Licensing.Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Birooni.Licensing.Api.Tests;

public class TokenSignerTests
{
    [Fact]
    public void Constructor_GeneratesDevKeypair_WhenNoConfigSupplied()
    {
        var config = new ConfigurationBuilder().Build();
        var signer = new TokenSigner(config, NullLogger<TokenSigner>.Instance);

        var publicKeyPem = signer.GetPublicKeyPem();

        Assert.NotNull(publicKeyPem);
        Assert.StartsWith("-----BEGIN PUBLIC KEY-----", publicKeyPem.Trim());
        Assert.EndsWith("-----END PUBLIC KEY-----", publicKeyPem.Trim());
    }

    [Fact]
    public void Sign_CreatesValidSignedToken_WithFourteenDayValidity()
    {
        var config = new ConfigurationBuilder().Build();
        var signer = new TokenSigner(config, NullLogger<TokenSigner>.Instance);

        var licenseKey = "BIROONI-PRO-1234-5678";
        var deviceId = "DEV-MAC-AA-BB-CC-DD";
        var expiresAt = DateTimeOffset.UtcNow.AddMonths(1);

        var token = signer.Sign(licenseKey, deviceId, expiresAt, "Birooni Designer", "Standard");

        Assert.NotNull(token);
        Assert.NotNull(token.Token);
        Assert.NotEmpty(token.Signature);
        Assert.NotEmpty(token.PayloadJson);

        // Verify ValidUntil is ~14 days from now
        var diff = token.ValidUntil - DateTimeOffset.UtcNow;
        Assert.InRange(diff.TotalDays, 13.9, 14.1);

        // Verify JSON payload contents
        var payload = JsonSerializer.Deserialize<LicenseTokenPayload>(token.PayloadJson, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        Assert.NotNull(payload);
        Assert.Equal(licenseKey, payload.LicenseKey);
        Assert.Equal(deviceId, payload.DeviceId);
        Assert.Equal("Birooni Designer", payload.Product);
    }

    [Fact]
    public void Verify_ValidatesPayloadAndSignatureSuccessfully()
    {
        var config = new ConfigurationBuilder().Build();
        var signer = new TokenSigner(config, NullLogger<TokenSigner>.Instance);

        var token = signer.Sign("TEST-KEY", "DEVICE-01", null);

        var isValid = signer.Verify(token.PayloadJson, token.Signature);
        Assert.True(isValid);
    }

    [Fact]
    public void Verify_RejectsTamperedPayload()
    {
        var config = new ConfigurationBuilder().Build();
        var signer = new TokenSigner(config, NullLogger<TokenSigner>.Instance);

        var token = signer.Sign("TEST-KEY", "DEVICE-01", null);
        var tamperedPayload = token.PayloadJson.Replace("TEST-KEY", "HACKED-KEY");

        var isValid = signer.Verify(tamperedPayload, token.Signature);
        Assert.False(isValid);
    }
}
