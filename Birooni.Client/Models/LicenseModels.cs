namespace Birooni.Client.Models;

public enum LicenseStatus
{
    ValidOffline,
    ValidOnline,
    Expired,
    SignatureInvalid,
    DeviceMismatch,
    Unlicensed,
    NetworkError
}

public record CachedToken(
    string Token,
    string PayloadJson,
    string Signature,
    DateTimeOffset ValidUntil,
    DateTimeOffset? ExpiresAt
);

public record LicensePayload(
    string LicenseKey,
    string DeviceId,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset ValidUntil,
    DateTimeOffset IssuedAt,
    string? Product = null,
    string? LicenseType = null
);

public class LicenseValidationResult
{
    public LicenseStatus Status { get; set; }
    public bool IsGranted => Status is LicenseStatus.ValidOffline or LicenseStatus.ValidOnline;
    public string Message { get; set; } = string.Empty;
    public LicensePayload? Payload { get; set; }
    public CachedToken? CachedToken { get; set; }

    public static LicenseValidationResult Granted(LicenseStatus status, string message, LicensePayload payload, CachedToken token) =>
        new()
        {
            Status = status,
            Message = message,
            Payload = payload,
            CachedToken = token
        };

    public static LicenseValidationResult Denied(LicenseStatus status, string message) =>
        new()
        {
            Status = status,
            Message = message
        };
}
