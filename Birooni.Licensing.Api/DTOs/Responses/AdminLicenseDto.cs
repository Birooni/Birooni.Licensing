namespace Birooni.Licensing.Api.DTOs.Responses;

public class AdminLicenseDto
{
    public Guid Id { get; set; }
    public string LicenseKey { get; set; } = string.Empty;
    public string Product { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string CustomerEmail { get; set; } = string.Empty;
    public string? Company { get; set; }
    public string LicenseType { get; set; } = string.Empty;
    public string LicenseMode { get; set; } = string.Empty;
    public string? AllowedDomain { get; set; }
    public int MaxActivations { get; set; }
    public int? ConcurrentSeats { get; set; }
    public int ActiveActivationsCount { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class AdminDeviceDto
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public string LicenseKey { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string CustomerEmail { get; set; } = string.Empty;
    public string? Company { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public string? DeviceName { get; set; }
    public string? PluginVersion { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset ActivatedAt { get; set; }
    public DateTimeOffset LastValidatedAt { get; set; }
}
