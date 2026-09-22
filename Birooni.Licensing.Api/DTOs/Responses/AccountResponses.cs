namespace Birooni.Licensing.Api.DTOs.Responses;

public class AccountProfileDto
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string? Company { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public bool EmailVerified { get; set; }
}

public class AuthResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? Token { get; set; }
    public AccountProfileDto? Account { get; set; }
    public string? FamilyLoaderKey { get; set; }
    public bool RequiresVerification { get; set; }
    public bool EmailSent { get; set; } = true;

    public static AuthResponse Ok(string message, string? token, AccountProfileDto? account, string? familyLoaderKey = null, bool requiresVerification = false, bool emailSent = true) =>
        new()
        {
            Success = true,
            Message = message,
            Token = token,
            Account = account,
            FamilyLoaderKey = familyLoaderKey,
            RequiresVerification = requiresVerification,
            EmailSent = emailSent
        };

    public static AuthResponse Fail(string message, bool requiresVerification = false) =>
        new() { Success = false, Message = message, RequiresVerification = requiresVerification };
}

public class PurchaseActivationDto
{
    public string DeviceName { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string? PluginVersion { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset ActivatedAt { get; set; }
    public DateTimeOffset LastValidatedAt { get; set; }
}

public class PurchaseDto
{
    public Guid Id { get; set; }
    public string LicenseKey { get; set; } = string.Empty;
    public string Product { get; set; } = string.Empty;
    public string LicenseType { get; set; } = string.Empty;
    public string LicenseMode { get; set; } = string.Empty;
    public string Ownership { get; set; } = "personal";
    public string? Company { get; set; }
    public string? AllowedDomain { get; set; }
    public int MaxActivations { get; set; }
    public int ActiveActivationsCount { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<PurchaseActivationDto> Activations { get; set; } = new();
}
