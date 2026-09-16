namespace Birooni.Licensing.Api.DTOs.Responses;

public class LicenseResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? LicenseKey { get; set; }
    public string? Product { get; set; }
    public string? CustomerName { get; set; }
    public string? LicenseType { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public int? ActivationsUsed { get; set; }
    public int? MaxActivations { get; set; }
    public SignedLicenseToken? Token { get; set; }

    public static LicenseResult Ok(string message, string licenseKey, string product, string customerName, string licenseType, DateTimeOffset? expiresAt, int activationsUsed, int maxActivations, SignedLicenseToken token)
    {
        return new LicenseResult
        {
            Success = true,
            Message = message,
            LicenseKey = licenseKey,
            Product = product,
            CustomerName = customerName,
            LicenseType = licenseType,
            ExpiresAt = expiresAt,
            ActivationsUsed = activationsUsed,
            MaxActivations = maxActivations,
            Token = token
        };
    }

    public static LicenseResult Fail(string message, string? licenseKey = null)
    {
        return new LicenseResult
        {
            Success = false,
            Message = message,
            LicenseKey = licenseKey
        };
    }
}
