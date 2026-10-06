using System.Security.Cryptography;
using Birooni.Licensing.Api.Data;
using Birooni.Licensing.Api.DTOs.Requests;
using Birooni.Licensing.Api.DTOs.Responses;
using Birooni.Licensing.Api.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Birooni.Licensing.Api.Services;

public class AccountService : IAccountService
{
    private static readonly SemaphoreSlim RobotClaimGate = new(1, 1);

    private readonly LicensingDbContext _db;
    private readonly IJwtTokenService _jwt;
    private readonly IEmailSender _email;
    private readonly IConfiguration _configuration;
    private readonly PasswordHasher<CustomerAccount> _hasher = new();

    public AccountService(LicensingDbContext db, IJwtTokenService jwt, IEmailSender email, IConfiguration configuration)
    {
        _db = db;
        _jwt = jwt;
        _email = email;
        _configuration = configuration;
    }

    public async Task<AuthResponse> SignupAsync(SignupRequest request, CancellationToken cancellationToken = default)
    {
        var email = NormalizeEmail(request.Email);
        if (string.IsNullOrWhiteSpace(email))
        {
            return AuthResponse.Fail("A valid email address is required.");
        }

        var name = request.FullName.Trim();
        if (name.Length < 2)
        {
            return AuthResponse.Fail("Please enter your name.");
        }

        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 8)
        {
            return AuthResponse.Fail("Password must be at least 8 characters.");
        }

        if (!string.Equals(request.Password, request.ConfirmPassword, StringComparison.Ordinal))
        {
            return AuthResponse.Fail("Password and confirm password must match.");
        }

        var exists = await _db.CustomerAccounts.AnyAsync(a => a.Email == email, cancellationToken);
        if (exists)
        {
            return AuthResponse.Fail("An account with this email already exists. Sign in instead.");
        }

        var account = new CustomerAccount
        {
            Email = email,
            FullName = name,
            Company = string.IsNullOrWhiteSpace(request.Company) ? null : request.Company.Trim(),
            CreatedAt = DateTimeOffset.UtcNow
        };
        account.PasswordHash = _hasher.HashPassword(account, request.Password);
        account.EmailVerified = false;

        _db.CustomerAccounts.Add(account);
        var (sent, _) = await IssueVerificationAsync(account, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);

        var message = sent
            ? $"Check {email} (and spam) for a message from info@ibrooni.com. Click Verify email in that message. You can sign in only after that."
            : "Verification mail could not be sent. Wait a minute and use Resend verification email.";

        return AuthResponse.Ok(
            message,
            token: null,
            account: ToProfile(account),
            requiresVerification: true,
            emailSent: sent,
            verificationLink: null);
    }

    public async Task<AuthResponse> SigninAsync(SigninRequest request, CancellationToken cancellationToken = default)
    {
        var email = NormalizeEmail(request.Email);
        var account = await _db.CustomerAccounts.FirstOrDefaultAsync(a => a.Email == email, cancellationToken);
        if (account == null)
        {
            return AuthResponse.Fail("Email or password is incorrect.");
        }

        var result = _hasher.VerifyHashedPassword(account, account.PasswordHash, request.Password);
        if (result == PasswordVerificationResult.Failed)
        {
            return AuthResponse.Fail("Email or password is incorrect.");
        }

        if (!account.EmailVerified)
        {
            var (sent, _) = await IssueVerificationAsync(account, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);

            return new AuthResponse
            {
                Success = false,
                RequiresVerification = true,
                EmailSent = sent,
                VerificationLink = null,
                Message = sent
                    ? "Confirm this email first. Open the Ibrooni message from info@ibrooni.com and click Verify email."
                    : "Confirm this email first. Wait a minute, then use Resend verification email."
            };
        }

        account.LastLoginAt = DateTimeOffset.UtcNow;
        var familyKey = await EnsureFamilyLoaderLicenseAsync(account, cancellationToken);
        var clashKey = await EnsureAvoidMepClashLicenseAsync(account, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);

        return AuthResponse.Ok("Signed in.", _jwt.CreateToken(account), ToProfile(account), familyKey, avoidMepClashKey: clashKey);
    }

    public async Task<AuthResponse> VerifyEmailAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return AuthResponse.Fail("Missing verification token.");
        }

        var hash = HashToken(token.Trim());
        var account = await _db.CustomerAccounts.FirstOrDefaultAsync(
            a => a.VerificationTokenHash == hash, cancellationToken);

        if (account == null)
        {
            return AuthResponse.Fail("This verification link is invalid. Request a new one from the sign-in page.");
        }

        if (account.EmailVerified)
        {
            var existingKey = await EnsureFamilyLoaderLicenseAsync(account, cancellationToken);
            var existingClash = await EnsureAvoidMepClashLicenseAsync(account, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            return AuthResponse.Ok("Email is already verified. You can sign in.", _jwt.CreateToken(account), ToProfile(account), existingKey, avoidMepClashKey: existingClash);
        }

        if (account.VerificationExpiresAt is null || account.VerificationExpiresAt < DateTimeOffset.UtcNow)
        {
            return AuthResponse.Fail("This verification link has expired. Sign in to receive a new one.");
        }

        account.EmailVerified = true;
        account.VerificationTokenHash = null;
        account.VerificationExpiresAt = null;
        account.LastLoginAt = DateTimeOffset.UtcNow;

        var familyKey = await EnsureFamilyLoaderLicenseAsync(account, cancellationToken);
        var clashKey = await EnsureAvoidMepClashLicenseAsync(account, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);

        return AuthResponse.Ok(
            "Email verified. Family Loader and Avoid MEP Clash keys are on this account.",
            _jwt.CreateToken(account),
            ToProfile(account),
            familyKey,
            avoidMepClashKey: clashKey);
    }

    public async Task<AuthResponse> ResendVerificationAsync(string email, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeEmail(email);
        var account = await _db.CustomerAccounts.FirstOrDefaultAsync(a => a.Email == normalized, cancellationToken);

        // Always look successful so attackers cannot probe which addresses exist.
        if (account == null || account.EmailVerified)
        {
            return AuthResponse.Ok("If that email is registered and still unverified, a new link is on its way.", null, null, requiresVerification: true);
        }

        if (account.VerificationSentAt is not null && account.VerificationSentAt > DateTimeOffset.UtcNow.AddSeconds(-45))
        {
            return AuthResponse.Ok("If that email is registered and still unverified, a new link is on its way.", null, null, requiresVerification: true);
        }

        var (sent, _) = await IssueVerificationAsync(account, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return AuthResponse.Ok(
            sent
                ? "If that email is registered and still unverified, a new Verify email link is on its way. Check inbox and spam."
                : "Verification mail could not be sent. Wait a minute and try Resend verification email again.",
            null,
            null,
            requiresVerification: true,
            emailSent: sent,
            verificationLink: null);
    }

    public async Task<AuthResponse> ForgotPasswordAsync(string email, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeEmail(email);
        var account = await _db.CustomerAccounts.FirstOrDefaultAsync(a => a.Email == normalized, cancellationToken);
        const string generic = "If that inbox has an Ibrooni account, a Reset password link is on its way. Check inbox and spam.";

        if (account == null)
        {
            return AuthResponse.Ok(generic, null, null, emailSent: true);
        }

        if (account.PasswordResetSentAt is not null && account.PasswordResetSentAt > DateTimeOffset.UtcNow.AddSeconds(-45))
        {
            return AuthResponse.Ok(generic, null, null, emailSent: true);
        }

        var (sent, _) = await IssuePasswordResetAsync(account, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return AuthResponse.Ok(
            sent ? generic : "Reset mail could not be sent. Wait a minute and try again.",
            null,
            null,
            emailSent: sent);
    }

    public async Task<AuthResponse> ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Token))
        {
            return AuthResponse.Fail("Missing reset token.");
        }

        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 8)
        {
            return AuthResponse.Fail("Password must be at least 8 characters.");
        }

        if (!string.Equals(request.Password, request.ConfirmPassword, StringComparison.Ordinal))
        {
            return AuthResponse.Fail("Password and confirm password must match.");
        }

        var hash = HashToken(request.Token.Trim());
        var account = await _db.CustomerAccounts.FirstOrDefaultAsync(
            a => a.PasswordResetTokenHash == hash, cancellationToken);

        if (account == null)
        {
            return AuthResponse.Fail("This reset link is invalid. Request a new one from Forgot password.");
        }

        if (account.PasswordResetExpiresAt is null || account.PasswordResetExpiresAt < DateTimeOffset.UtcNow)
        {
            return AuthResponse.Fail("This reset link has expired. Request a new one from Forgot password.");
        }

        account.PasswordHash = _hasher.HashPassword(account, request.Password);
        account.PasswordResetTokenHash = null;
        account.PasswordResetExpiresAt = null;
        account.PasswordResetSentAt = null;
        await _db.SaveChangesAsync(cancellationToken);

        if (!account.EmailVerified)
        {
            return AuthResponse.Ok(
                "Password updated. Confirm your email from the Verify email message before signing in.",
                null,
                ToProfile(account),
                requiresVerification: true);
        }

        return AuthResponse.Ok("Password updated. Sign in with your new password.", null, ToProfile(account));
    }

    public async Task<AccountProfileDto?> GetProfileAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var account = await _db.CustomerAccounts.FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken);
        return account == null ? null : ToProfile(account);
    }

    public async Task<List<PurchaseDto>> GetPurchasesAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var account = await _db.CustomerAccounts.FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken);
        if (account == null)
        {
            return new List<PurchaseDto>();
        }

        var email = account.Email;
        var domain = GetEmailDomain(email);

        var licenses = await _db.Licenses
            .Include(l => l.Activations)
            .AsNoTracking()
            .Where(l =>
                l.CustomerEmail.ToLower() == email.ToLower()
                || l.LicenseMode == "SiteLicense")
            .ToListAsync(cancellationToken);

        return licenses
            .Where(l => MatchesPurchase(l, email, domain))
            .OrderByDescending(l => l.CreatedAt)
            .Select(l => ToPurchase(l, email))
            .ToList();
    }

    public static bool MatchesPurchase(License license, string email, string domain)
    {
        if (string.Equals(license.CustomerEmail, email, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.Equals(license.LicenseMode, "SiteLicense", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(license.AllowedDomain)
            || string.IsNullOrWhiteSpace(domain))
        {
            return false;
        }

        var allowed = license.AllowedDomain.Trim();
        if (!allowed.StartsWith('@'))
        {
            allowed = "@" + allowed;
        }

        return domain.Equals(allowed, StringComparison.OrdinalIgnoreCase);
    }

    private static PurchaseDto ToPurchase(License license, string email)
    {
        var personal = string.Equals(license.CustomerEmail, email, StringComparison.OrdinalIgnoreCase);
        return new PurchaseDto
        {
            Id = license.Id,
            LicenseKey = license.LicenseKey,
            Product = license.Product,
            LicenseType = license.LicenseType,
            LicenseMode = license.LicenseMode,
            Ownership = personal ? "personal" : "company",
            Company = license.Company,
            AllowedDomain = license.AllowedDomain,
            MaxActivations = license.MaxActivations,
            ActiveActivationsCount = license.Activations.Count(a => a.IsActive),
            IsActive = license.IsActive,
            ExpiresAt = license.ExpiresAt,
            CreatedAt = license.CreatedAt,
            Activations = license.Activations
                .OrderByDescending(a => a.LastValidatedAt)
                .Select(a => new PurchaseActivationDto
                {
                    DeviceName = a.DeviceName,
                    DeviceId = a.DeviceId,
                    PluginVersion = a.PluginVersion,
                    IsActive = a.IsActive,
                    ActivatedAt = a.ActivatedAt,
                    LastValidatedAt = a.LastValidatedAt
                })
                .ToList()
        };
    }

    public const string FamilyLoaderProduct = "FamilyLoader";
    public const string AvoidMepClashProduct = "AvoidMepClash";
    public const string ViewReferenceRobotProduct = "ViewReferenceRobot";
    public const int ViewReferenceRobotFounderCap = 100;

    /// <summary>
    /// Registered Ibrooni accounts receive a perpetual Family Loader license.
    /// Existing trials on the same email are upgraded; commercial keys are left in place.
    /// </summary>
    public async Task<string> EnsureFamilyLoaderLicenseAsync(CustomerAccount account, CancellationToken cancellationToken = default)
    {
        var email = account.Email;
        var existing = await _db.Licenses
            .Where(l => l.Product.ToLower() == FamilyLoaderProduct.ToLower() && l.CustomerEmail.ToLower() == email)
            .OrderByDescending(l => l.IsActive)
            .ThenByDescending(l => l.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing != null)
        {
            var isTrial = string.Equals(existing.LicenseType, "Trial", StringComparison.OrdinalIgnoreCase);
            if (isTrial || existing.ExpiresAt.HasValue)
            {
                existing.IsActive = true;
                existing.LicenseType = "Registered";
                existing.LicenseMode = "Individual";
                existing.ExpiresAt = null;
                existing.CustomerName = account.FullName;
                existing.Company = account.Company;
                if (existing.MaxActivations < 2) existing.MaxActivations = 2;
            }

            return existing.LicenseKey;
        }

        var license = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = GenerateFamilyLoaderKey(),
            Product = FamilyLoaderProduct,
            CustomerName = account.FullName,
            CustomerEmail = email,
            Company = account.Company,
            LicenseType = "Registered",
            LicenseMode = "Individual",
            MaxActivations = 2,
            IsActive = true,
            ExpiresAt = null,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.Licenses.Add(license);
        await _db.SaveChangesAsync(cancellationToken);
        return license.LicenseKey;
    }

    private static string GenerateFamilyLoaderKey()
    {
        Span<byte> randomBytes = stackalloc byte[6];
        RandomNumberGenerator.Fill(randomBytes);
        var hex = Convert.ToHexString(randomBytes);
        return $"FAMILY-{hex[..4]}-{hex[4..8]}-{hex[8..12]}";
    }

    /// <summary>
    /// Registered Ibrooni accounts receive a perpetual Avoid MEP Clash license after the 30-day trial.
    /// Existing trials on the same email are upgraded; commercial keys are left in place.
    /// </summary>
    public async Task<string> EnsureAvoidMepClashLicenseAsync(CustomerAccount account, CancellationToken cancellationToken = default)
    {
        var email = account.Email;
        var existing = await _db.Licenses
            .Where(l => l.Product.ToLower() == AvoidMepClashProduct.ToLower() && l.CustomerEmail.ToLower() == email)
            .OrderByDescending(l => l.IsActive)
            .ThenByDescending(l => l.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing != null)
        {
            var isTrial = string.Equals(existing.LicenseType, "Trial", StringComparison.OrdinalIgnoreCase);
            if (isTrial || existing.ExpiresAt.HasValue)
            {
                existing.IsActive = true;
                existing.LicenseType = "Registered";
                existing.LicenseMode = "Individual";
                existing.ExpiresAt = null;
                existing.CustomerName = account.FullName;
                existing.Company = account.Company;
                if (existing.MaxActivations < 2) existing.MaxActivations = 2;
            }

            return existing.LicenseKey;
        }

        var license = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = GenerateAvoidMepClashKey(),
            Product = AvoidMepClashProduct,
            CustomerName = account.FullName,
            CustomerEmail = email,
            Company = account.Company,
            LicenseType = "Registered",
            LicenseMode = "Individual",
            MaxActivations = 2,
            IsActive = true,
            ExpiresAt = null,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.Licenses.Add(license);
        await _db.SaveChangesAsync(cancellationToken);
        return license.LicenseKey;
    }

    private static string GenerateAvoidMepClashKey()
    {
        Span<byte> randomBytes = stackalloc byte[6];
        RandomNumberGenerator.Fill(randomBytes);
        var hex = Convert.ToHexString(randomBytes);
        return $"CLASH-{hex[..4]}-{hex[4..8]}-{hex[8..12]}";
    }

    public static readonly DateTimeOffset ViewReferenceRobotFreeUntil = new(2027, 1, 31, 23, 59, 59, TimeSpan.Zero);
    public static readonly DateTimeOffset ViewReferenceRobotFounderStarts = new(2027, 2, 1, 0, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset ViewReferenceRobotFounderExpires = ViewReferenceRobotFounderStarts.AddMonths(6);

    public async Task<RobotOfferDto> GetRobotOfferAsync(Guid? accountId, CancellationToken cancellationToken = default)
    {
        var (claimed, remaining) = await RobotFounderStatsAsync(cancellationToken);
        var dto = new RobotOfferDto
        {
            Cap = ViewReferenceRobotFounderCap,
            Claimed = claimed,
            Remaining = remaining,
            FreeUntil = ViewReferenceRobotFreeUntil,
            FounderStarts = ViewReferenceRobotFounderStarts,
            FounderExpires = ViewReferenceRobotFounderExpires,
            FamilyLoaderGranted = await CountFamilyLoaderEmailsAsync(cancellationToken)
        };

        if (accountId is Guid id)
        {
            var account = await _db.CustomerAccounts.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
            if (account != null)
            {
                var existing = await FindViewReferenceRobotLicenseAsync(account.Email, cancellationToken);
                if (existing != null)
                {
                    dto.ClaimedByYou = true;
                    dto.LicenseKey = existing.LicenseKey;
                    dto.ExpiresAt = existing.ExpiresAt;
                }
            }
        }

        return dto;
    }

    public async Task<FamilyLoaderStatsDto> GetFamilyLoaderStatsAsync(CancellationToken cancellationToken = default)
    {
        return new FamilyLoaderStatsDto
        {
            Granted = await CountFamilyLoaderEmailsAsync(cancellationToken)
        };
    }

    public async Task<ClaimRobotOfferResponse> ClaimRobotOfferAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var account = await _db.CustomerAccounts.FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken);
        if (account == null)
        {
            return ClaimRobotOfferResponse.Fail("Account was not found.");
        }

        if (!account.EmailVerified)
        {
            var (claimedOpen, remainingOpen) = await RobotFounderStatsAsync(cancellationToken);
            return ClaimRobotOfferResponse.Fail(
                "Confirm your Mail ID before claiming this offer.",
                remainingOpen,
                claimedOpen,
                ViewReferenceRobotFounderCap);
        }

        await RobotClaimGate.WaitAsync(cancellationToken);
        try
        {
            var existing = await FindViewReferenceRobotLicenseAsync(account.Email, cancellationToken);
            var already = existing != null;
            var key = await EnsureViewReferenceRobotLicenseAsync(account, cancellationToken);
            var (claimed, remaining) = await RobotFounderStatsAsync(cancellationToken);

            if (key == null)
            {
                return ClaimRobotOfferResponse.Fail(
                    "All 100 founder licenses have been claimed.",
                    remaining,
                    claimed,
                    ViewReferenceRobotFounderCap);
            }

            var license = already ? existing : await FindViewReferenceRobotLicenseAsync(account.Email, cancellationToken);
            return new ClaimRobotOfferResponse
            {
                Success = true,
                Message = already
                    ? "This account already has a View Reference Robot founder license. The six months start on 1 February 2027."
                    : "Founder license claimed. The six months start on 1 February 2027.",
                LicenseKey = key,
                ExpiresAt = license?.ExpiresAt,
                Remaining = remaining,
                Claimed = claimed,
                Cap = ViewReferenceRobotFounderCap
            };
        }
        finally
        {
            RobotClaimGate.Release();
        }
    }

    /// <summary>
    /// Everyone may use View Reference Robot until 31 January 2027 without a key.
    /// The first 100 verified accounts may claim a six-month Founder license that
    /// starts on 1 February 2027. Grants happen only through ClaimRobotOfferAsync.
    /// </summary>
    public async Task<string?> EnsureViewReferenceRobotLicenseAsync(CustomerAccount account, CancellationToken cancellationToken = default)
    {
        var email = account.Email;
        var existing = await FindViewReferenceRobotLicenseAsync(email, cancellationToken);
        if (existing != null)
        {
            AlignFounderExpiry(existing);
            await _db.SaveChangesAsync(cancellationToken);
            return existing.LicenseKey;
        }

        var grantedCount = await CountViewReferenceRobotEmailsAsync(cancellationToken);
        if (grantedCount >= ViewReferenceRobotFounderCap)
        {
            return null;
        }

        var license = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = GenerateViewReferenceRobotKey(),
            Product = ViewReferenceRobotProduct,
            CustomerName = account.FullName,
            CustomerEmail = email,
            Company = account.Company,
            LicenseType = "Founder",
            LicenseMode = "Individual",
            MaxActivations = 2,
            IsActive = true,
            ExpiresAt = ViewReferenceRobotFounderExpires,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.Licenses.Add(license);
        await _db.SaveChangesAsync(cancellationToken);
        return license.LicenseKey;
    }

    private static void AlignFounderExpiry(License license)
    {
        if (!string.Equals(license.LicenseType, "Founder", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (license.ExpiresAt is null || license.ExpiresAt < ViewReferenceRobotFounderExpires)
        {
            license.ExpiresAt = ViewReferenceRobotFounderExpires;
        }
    }

    private async Task<License?> FindViewReferenceRobotLicenseAsync(string email, CancellationToken cancellationToken)
    {
        return await _db.Licenses
            .Where(l => l.Product.ToLower() == ViewReferenceRobotProduct.ToLower() && l.CustomerEmail.ToLower() == email)
            .OrderByDescending(l => l.IsActive)
            .ThenByDescending(l => l.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<int> CountViewReferenceRobotEmailsAsync(CancellationToken cancellationToken)
    {
        return await _db.Licenses
            .Where(l => l.Product.ToLower() == ViewReferenceRobotProduct.ToLower())
            .Select(l => l.CustomerEmail.ToLower())
            .Distinct()
            .CountAsync(cancellationToken);
    }

    private async Task<int> CountFamilyLoaderEmailsAsync(CancellationToken cancellationToken)
    {
        return await _db.Licenses
            .Where(l => l.Product.ToLower() == FamilyLoaderProduct.ToLower())
            .Select(l => l.CustomerEmail.ToLower())
            .Distinct()
            .CountAsync(cancellationToken);
    }

    private async Task<(int Claimed, int Remaining)> RobotFounderStatsAsync(CancellationToken cancellationToken)
    {
        var claimed = await CountViewReferenceRobotEmailsAsync(cancellationToken);
        var remaining = Math.Max(0, ViewReferenceRobotFounderCap - claimed);
        return (claimed, remaining);
    }

    private static string GenerateViewReferenceRobotKey()
    {
        Span<byte> randomBytes = stackalloc byte[6];
        RandomNumberGenerator.Fill(randomBytes);
        var hex = Convert.ToHexString(randomBytes);
        return $"ROBOT-{hex[..4]}-{hex[4..8]}-{hex[8..12]}";
    }

    private async Task<(bool Sent, string Link)> IssueVerificationAsync(CustomerAccount account, CancellationToken cancellationToken)
    {
        var rawToken = CreateToken();
        account.VerificationTokenHash = HashToken(rawToken);
        account.VerificationExpiresAt = DateTimeOffset.UtcNow.AddHours(24);
        account.VerificationSentAt = DateTimeOffset.UtcNow;

        var link = $"{PublicWebUrl()}/account.html?verify={Uri.EscapeDataString(rawToken)}";

        var html = BuildActionEmail(
            account.FullName,
            "Confirm this email for your Ibrooni account. You can sign in only after you click the button below.",
            "Verify email",
            link);

        try
        {
            await _email.SendAsync(account.Email, "Verify your Ibrooni email", html, cancellationToken);
            return (true, link);
        }
        catch (Exception)
        {
            return (false, link);
        }
    }

    private async Task<(bool Sent, string Link)> IssuePasswordResetAsync(CustomerAccount account, CancellationToken cancellationToken)
    {
        var rawToken = CreateToken();
        account.PasswordResetTokenHash = HashToken(rawToken);
        account.PasswordResetExpiresAt = DateTimeOffset.UtcNow.AddHours(2);
        account.PasswordResetSentAt = DateTimeOffset.UtcNow;

        var web = PublicWebUrl();
        var link = $"{web}/account.html?reset={Uri.EscapeDataString(rawToken)}";
        var html = BuildActionEmail(
            account.FullName,
            "Use the button below to set a new Ibrooni password. If you did not ask for this, ignore the message.",
            "Reset password",
            link,
            "This link expires in 2 hours.");

        try
        {
            await _email.SendAsync(account.Email, "Reset your Ibrooni password", html, cancellationToken);
            return (true, link);
        }
        catch (Exception)
        {
            return (false, link);
        }
    }

    private string PublicWebUrl() =>
        (_configuration["App:PublicWebUrl"]
         ?? Environment.GetEnvironmentVariable("PUBLIC_WEB_URL")
         ?? "https://ibrooni.com").TrimEnd('/');

    private static string BuildActionEmail(string fullName, string intro, string buttonLabel, string link, string expiry = "This link expires in 24 hours.")
    {
        var name = System.Net.WebUtility.HtmlEncode(fullName);
        var label = System.Net.WebUtility.HtmlEncode(buttonLabel);
        return $"""
            <!DOCTYPE html>
            <html>
            <body style="margin:0;padding:0;background:#070b12;font-family:Arial,Helvetica,sans-serif;">
              <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="background:#070b12;padding:32px 16px;">
                <tr><td align="center">
                  <table role="presentation" width="560" cellspacing="0" cellpadding="0" style="background:#111827;border-radius:12px;padding:32px;">
                    <tr><td style="color:#e8eef5;font-size:16px;line-height:1.55;">
                      <p style="margin:0 0 16px;">Hello {name},</p>
                      <p style="margin:0 0 16px;">{System.Net.WebUtility.HtmlEncode(intro)}</p>
                      <p style="margin:28px 0;">
                        <a href="{link}" style="display:inline-block;background:#2F8A96;color:#ffffff;text-decoration:none;font-weight:700;padding:14px 28px;border-radius:8px;">{label}</a>
                      </p>
                      <p style="margin:0 0 8px;color:#9aa7b5;font-size:14px;">If the button does not work, copy and paste this link into your browser:</p>
                      <p style="margin:0 0 24px;word-break:break-all;font-size:14px;"><a href="{link}" style="color:#2F8A96;">{link}</a></p>
                      <p style="margin:0;color:#9aa7b5;font-size:13px;">{System.Net.WebUtility.HtmlEncode(expiry)} If you did not create an Ibrooni account, ignore this message.</p>
                    </td></tr>
                  </table>
                </td></tr>
              </table>
            </body>
            </html>
            """;
    }

    private static string CreateToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    internal static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

    private static AccountProfileDto ToProfile(CustomerAccount account) => new()
    {
        Id = account.Id,
        Email = account.Email,
        FullName = account.FullName,
        Company = account.Company,
        CreatedAt = account.CreatedAt,
        EmailVerified = account.EmailVerified
    };

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    internal static string GetEmailDomain(string email)
    {
        var at = email.LastIndexOf('@');
        return at < 0 || at == email.Length - 1 ? string.Empty : "@" + email[(at + 1)..];
    }
}
