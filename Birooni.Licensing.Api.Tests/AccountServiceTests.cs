using System.Text.RegularExpressions;
using Birooni.Licensing.Api.Data;
using Birooni.Licensing.Api.DTOs.Requests;
using Birooni.Licensing.Api.Models;
using Birooni.Licensing.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Birooni.Licensing.Api.Tests;

public class AccountServiceTests
{
    private sealed class FakeEmailSender : IEmailSender
    {
        public bool IsConfigured => true;
        public string? LastHtml { get; private set; }

        public string? LastToken
        {
            get
            {
                if (string.IsNullOrWhiteSpace(LastHtml)) return null;
                var match = Regex.Match(LastHtml, @"verify=([^""&\s]+)");
                return match.Success ? Uri.UnescapeDataString(match.Groups[1].Value) : null;
            }
        }

        public Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken cancellationToken = default)
        {
            LastHtml = htmlBody;
            return Task.CompletedTask;
        }
    }

    private static (LicensingDbContext db, AccountService service, FakeEmailSender email) Create()
    {
        var options = new DbContextOptionsBuilder<LicensingDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new LicensingDbContext(options);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Secret"] = "test-jwt-secret-at-least-32-chars!!",
                ["App:PublicWebUrl"] = "https://ibrooni.com"
            })
            .Build();
        var jwt = new JwtTokenService(config, NullLogger<JwtTokenService>.Instance);
        var email = new FakeEmailSender();
        return (db, new AccountService(db, jwt, email, config), email);
    }

    [Fact]
    public async Task Signup_Then_Signin_ReturnsToken()
    {
        var (db, service, email) = Create();
        var signup = await service.SignupAsync(new SignupRequest
        {
            FullName = "Ada Khan",
            Email = "ada@studio.com",
            Password = "secret123",
            Company = "Studio"
        });

        Assert.True(signup.Success);
        Assert.True(signup.RequiresVerification);
        Assert.True(string.IsNullOrWhiteSpace(signup.Token));
        Assert.Equal("ada@studio.com", signup.Account!.Email);
        Assert.False(db.Licenses.Any());

        var blocked = await service.SigninAsync(new SigninRequest
        {
            Email = "ADA@studio.com",
            Password = "secret123"
        });
        Assert.False(blocked.Success);
        Assert.True(blocked.RequiresVerification);

        var verified = await service.VerifyEmailAsync(email.LastToken!);
        Assert.True(verified.Success);
        Assert.False(string.IsNullOrWhiteSpace(verified.Token));
        Assert.StartsWith("FAMILY-", verified.FamilyLoaderKey);

        var signin = await service.SigninAsync(new SigninRequest
        {
            Email = "ADA@studio.com",
            Password = "secret123"
        });
        Assert.True(signin.Success);
        Assert.False(string.IsNullOrWhiteSpace(signin.Token));
    }

    [Fact]
    public async Task Signup_DuplicateEmail_Fails()
    {
        var (_, service, _) = Create();
        var first = await service.SignupAsync(new SignupRequest
        {
            FullName = "Ada",
            Email = "ada@studio.com",
            Password = "secret123"
        });
        Assert.True(first.Success);

        var second = await service.SignupAsync(new SignupRequest
        {
            FullName = "Ada 2",
            Email = "ada@studio.com",
            Password = "secret123"
        });
        Assert.False(second.Success);
        Assert.Contains("already exists", second.Message);
    }

    [Fact]
    public async Task Signup_GrantsPerpetualFamilyLoaderLicense()
    {
        var (db, service, mail) = Create();
        var signup = await service.SignupAsync(new SignupRequest
        {
            FullName = "Ada Khan",
            Email = "ada@studio.com",
            Password = "secret123"
        });
        Assert.True(signup.Success);
        Assert.Empty(db.Licenses);

        var verified = await service.VerifyEmailAsync(mail.LastToken!);
        var license = Assert.Single(db.Licenses);
        Assert.Equal("FamilyLoader", license.Product);
        Assert.Equal("Registered", license.LicenseType);
        Assert.Null(license.ExpiresAt);
        Assert.True(license.IsActive);
        Assert.Equal(verified.FamilyLoaderKey, license.LicenseKey);

        var again = await service.SigninAsync(new SigninRequest
        {
            Email = "ada@studio.com",
            Password = "secret123"
        });
        Assert.Equal(verified.FamilyLoaderKey, again.FamilyLoaderKey);
        Assert.Equal(1, db.Licenses.Count());
    }

    [Fact]
    public async Task Signin_UpgradesExistingFamilyLoaderTrial()
    {
        var (db, service, mail) = Create();
        db.Licenses.Add(new License
        {
            LicenseKey = "TRIAL-OLD-KEY",
            Product = "FamilyLoader",
            CustomerName = "Sara",
            CustomerEmail = "sara@studio.com",
            LicenseType = "Trial",
            LicenseMode = "Individual",
            MaxActivations = 1,
            IsActive = true,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(3)
        });
        await db.SaveChangesAsync();

        await service.SignupAsync(new SignupRequest
        {
            FullName = "Sara",
            Email = "sara@studio.com",
            Password = "secret123"
        });
        await service.VerifyEmailAsync(mail.LastToken!);

        var license = Assert.Single(db.Licenses);
        Assert.Equal("TRIAL-OLD-KEY", license.LicenseKey);
        Assert.Equal("Registered", license.LicenseType);
        Assert.Null(license.ExpiresAt);
        Assert.True(license.IsActive);
    }

    [Fact]
    public async Task Purchases_IncludePersonalAndCompanyLicenses()
    {
        var (db, service, mail) = Create();
        var signup = await service.SignupAsync(new SignupRequest
        {
            FullName = "Sara",
            Email = "sara@gensler.com",
            Password = "secret123",
            Company = "Gensler"
        });
        Assert.True(signup.Success);
        var verified = await service.VerifyEmailAsync(mail.LastToken!);
        Assert.True(verified.Success);

        db.Licenses.Add(new License
        {
            LicenseKey = "PERS-1",
            Product = "FamilyLoader",
            CustomerName = "Sara",
            CustomerEmail = "sara@gensler.com",
            LicenseType = "Commercial",
            LicenseMode = "Individual",
            MaxActivations = 1,
            IsActive = true
        });
        db.Licenses.Add(new License
        {
            LicenseKey = "SITE-1",
            Product = "Biruscan",
            CustomerName = "Gensler BIM",
            CustomerEmail = "bim-lead@gensler.com",
            Company = "Gensler",
            LicenseType = "Commercial",
            LicenseMode = "SiteLicense",
            AllowedDomain = "@gensler.com",
            MaxActivations = 50,
            IsActive = true
        });
        db.Licenses.Add(new License
        {
            LicenseKey = "OTHER-1",
            Product = "FamilyLoader",
            CustomerName = "Other",
            CustomerEmail = "other@example.com",
            LicenseType = "Commercial",
            LicenseMode = "Individual",
            IsActive = true
        });
        await db.SaveChangesAsync();

        var purchases = await service.GetPurchasesAsync(signup.Account!.Id);
        Assert.Contains(purchases, p => p.LicenseKey == "PERS-1" && p.Ownership == "personal");
        Assert.Contains(purchases, p => p.LicenseKey == "SITE-1" && p.Ownership == "company");
        Assert.Contains(purchases, p => p.Product == "FamilyLoader" && p.LicenseType == "Registered");
        Assert.DoesNotContain(purchases, p => p.LicenseKey == "OTHER-1");
    }

    [Fact]
    public void MatchesPurchase_SiteLicense_RequiresDomain()
    {
        var site = new License
        {
            CustomerEmail = "lead@gensler.com",
            LicenseMode = "SiteLicense",
            AllowedDomain = "@gensler.com"
        };
        Assert.True(AccountService.MatchesPurchase(site, "sara@gensler.com", "@gensler.com", null));
        Assert.False(AccountService.MatchesPurchase(site, "sara@example.com", "@example.com", null));
    }
}
