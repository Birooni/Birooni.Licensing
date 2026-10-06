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
    public string? AvoidMepClashKey { get; set; }
    public bool RequiresVerification { get; set; }
    public bool EmailSent { get; set; } = true;
    public string? VerificationLink { get; set; }

    public static AuthResponse Ok(string message, string? token, AccountProfileDto? account, string? familyLoaderKey = null, bool requiresVerification = false, bool emailSent = true, string? verificationLink = null, string? avoidMepClashKey = null) =>
        new()
        {
            Success = true,
            Message = message,
            Token = token,
            Account = account,
            FamilyLoaderKey = familyLoaderKey,
            AvoidMepClashKey = avoidMepClashKey,
            RequiresVerification = requiresVerification,
            EmailSent = emailSent,
            VerificationLink = verificationLink
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

public class RobotOfferDto
{
    public int Cap { get; set; }
    public int Claimed { get; set; }
    public int Remaining { get; set; }
    public bool ClaimedByYou { get; set; }
    public string? LicenseKey { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset FreeUntil { get; set; }
    public DateTimeOffset FounderStarts { get; set; }
    public DateTimeOffset FounderExpires { get; set; }
    public int FamilyLoaderGranted { get; set; }
}

public class FamilyLoaderStatsDto
{
    public int Granted { get; set; }
}

public class ClaimRobotOfferResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? LicenseKey { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public int Remaining { get; set; }
    public int Claimed { get; set; }
    public int Cap { get; set; }

    public static ClaimRobotOfferResponse Fail(string message, int remaining = 0, int claimed = 0, int cap = 100) =>
        new()
        {
            Success = false,
            Message = message,
            Remaining = remaining,
            Claimed = claimed,
            Cap = cap
        };
}
