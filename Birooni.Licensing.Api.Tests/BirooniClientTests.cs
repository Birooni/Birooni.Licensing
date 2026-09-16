using Birooni.Client.Hardware;
using Birooni.Client.Models;
using Birooni.Client.Storage;
using Birooni.Client.Validation;
using Birooni.Licensing.Api.DTOs.Responses;
using Birooni.Licensing.Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Birooni.Licensing.Api.Tests;

public class BirooniClientTests
{
    [Fact]
    public void HardwareFingerprintCollector_ReturnsDeterministicHardwareHash()
    {
        var fp1 = HardwareFingerprintCollector.GetDeviceFingerprint();
        var fp2 = HardwareFingerprintCollector.GetDeviceFingerprint();

        Assert.NotNull(fp1);
        Assert.NotEmpty(fp1);
        Assert.StartsWith("HW-", fp1);
        Assert.Equal(fp1, fp2);
    }

    [Fact]
    public void LicenseCacheManager_SavesAndLoadsTokenCorrectly()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"birooni_test_license_{Guid.NewGuid():N}.lic");
        try
        {
            var cacheManager = new LicenseCacheManager(tempFile);
            var token = new CachedToken(
                Token: "TOKEN_123.SIG_456",
                PayloadJson: "{\"licenseKey\":\"TEST-KEY\"}",
                Signature: "SIG_456",
                ValidUntil: DateTimeOffset.UtcNow.AddDays(14),
                ExpiresAt: DateTimeOffset.UtcNow.AddDays(30)
            );

            cacheManager.SaveToken(token);
            Assert.True(File.Exists(tempFile));

            var loaded = cacheManager.LoadToken();
            Assert.NotNull(loaded);
            Assert.Equal(token.Token, loaded.Token);
            Assert.Equal(token.PayloadJson, loaded.PayloadJson);
            Assert.Equal(token.Signature, loaded.Signature);

            cacheManager.ClearToken();
            Assert.False(File.Exists(tempFile));
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public void OfflineTokenValidator_ValidatesServerSignedTokenSuccessfully()
    {
        var signer = new TokenSigner(new ConfigurationBuilder().Build(), NullLogger<TokenSigner>.Instance);
        var publicKeyPem = signer.GetPublicKeyPem();

        var deviceId = "HW-TEST-MACHINE-01";
        var serverToken = signer.Sign("BIROONI-PRO-2026", deviceId, DateTimeOffset.UtcNow.AddMonths(1), "Birooni", "Standard");

        var cachedToken = new CachedToken(
            serverToken.Token,
            serverToken.PayloadJson,
            serverToken.Signature,
            serverToken.ValidUntil,
            serverToken.ExpiresAt
        );

        var validator = new OfflineTokenValidator(publicKeyPem);
        var result = validator.Validate(cachedToken, deviceId);

        Assert.True(result.IsGranted);
        Assert.Equal(LicenseStatus.ValidOffline, result.Status);
        Assert.NotNull(result.Payload);
        Assert.Equal("BIROONI-PRO-2026", result.Payload.LicenseKey);
        Assert.Equal(deviceId, result.Payload.DeviceId);
    }

    [Fact]
    public void OfflineTokenValidator_RejectsTamperedPayload()
    {
        var signer = new TokenSigner(new ConfigurationBuilder().Build(), NullLogger<TokenSigner>.Instance);
        var publicKeyPem = signer.GetPublicKeyPem();

        var deviceId = "HW-TEST-MACHINE-01";
        var serverToken = signer.Sign("BIROONI-PRO-2026", deviceId, DateTimeOffset.UtcNow.AddMonths(1));

        // Tamper with the JSON payload
        var tamperedPayloadJson = serverToken.PayloadJson.Replace("BIROONI-PRO-2026", "HACKED-KEY");

        var cachedToken = new CachedToken(
            serverToken.Token,
            tamperedPayloadJson,
            serverToken.Signature,
            serverToken.ValidUntil,
            serverToken.ExpiresAt
        );

        var validator = new OfflineTokenValidator(publicKeyPem);
        var result = validator.Validate(cachedToken, deviceId);

        Assert.False(result.IsGranted);
        Assert.Equal(LicenseStatus.SignatureInvalid, result.Status);
    }

    [Fact]
    public void OfflineTokenValidator_RejectsDeviceMismatch()
    {
        var signer = new TokenSigner(new ConfigurationBuilder().Build(), NullLogger<TokenSigner>.Instance);
        var publicKeyPem = signer.GetPublicKeyPem();

        var authorizedDeviceId = "HW-ORIGINAL-PC";
        var copiedDeviceId = "HW-STOLEN-LAPTOP";

        var serverToken = signer.Sign("BIROONI-PRO-2026", authorizedDeviceId, DateTimeOffset.UtcNow.AddMonths(1));

        var cachedToken = new CachedToken(
            serverToken.Token,
            serverToken.PayloadJson,
            serverToken.Signature,
            serverToken.ValidUntil,
            serverToken.ExpiresAt
        );

        var validator = new OfflineTokenValidator(publicKeyPem);
        var result = validator.Validate(cachedToken, copiedDeviceId);

        Assert.False(result.IsGranted);
        Assert.Equal(LicenseStatus.DeviceMismatch, result.Status);
        Assert.Contains(authorizedDeviceId, result.Message);
        Assert.Contains(copiedDeviceId, result.Message);
    }

    [Fact]
    public void OfflineTokenValidator_RejectsExpiredToken()
    {
        var signer = new TokenSigner(new ConfigurationBuilder().Build(), NullLogger<TokenSigner>.Instance);
        var publicKeyPem = signer.GetPublicKeyPem();

        var deviceId = "HW-TEST-MACHINE-01";
        var expiredDate = DateTimeOffset.UtcNow.AddDays(-1);

        var payload = new LicenseTokenPayload(
            LicenseKey: "EXPIRED-KEY",
            DeviceId: deviceId,
            ExpiresAt: expiredDate,
            ValidUntil: expiredDate,
            IssuedAt: DateTimeOffset.UtcNow.AddDays(-15)
        );

        var serverToken = signer.SignPayload(payload);

        var cachedToken = new CachedToken(
            serverToken.Token,
            serverToken.PayloadJson,
            serverToken.Signature,
            expiredDate,
            expiredDate
        );

        var validator = new OfflineTokenValidator(publicKeyPem);
        var result = validator.Validate(cachedToken, deviceId);

        Assert.False(result.IsGranted);
        Assert.Equal(LicenseStatus.Expired, result.Status);
    }
}
