using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Birooni.Licensing.Api.Models;
using Microsoft.IdentityModel.Tokens;

namespace Birooni.Licensing.Api.Services;

public class JwtTokenService : IJwtTokenService
{
    public const string Issuer = "https://api.ibrooni.com";
    public const string Audience = "https://ibrooni.com";

    private readonly byte[] _key;

    public JwtTokenService(IConfiguration configuration, ILogger<JwtTokenService> logger)
    {
        _key = ResolveKey(configuration, logger);
    }

    public byte[] GetSigningKeyBytes() => _key;

    public string CreateToken(CustomerAccount account)
    {
        var credentials = new SigningCredentials(new SymmetricSecurityKey(_key), SecurityAlgorithms.HmacSha256);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, account.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, account.Email),
            new Claim(JwtRegisteredClaimNames.Name, account.FullName),
            new Claim("company", account.Company ?? string.Empty)
        };

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddDays(30),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static byte[] ResolveKey(IConfiguration configuration, ILogger logger)
    {
        var secret = Environment.GetEnvironmentVariable("JWT_SECRET")
                     ?? configuration["Jwt:Secret"];

        if (!string.IsNullOrWhiteSpace(secret) && secret.Length >= 32)
        {
            return SHA256.HashData(Encoding.UTF8.GetBytes(secret.Trim()));
        }

        var rsaPem = configuration["Licensing:RsaPrivateKeyPem"];
        if (!string.IsNullOrWhiteSpace(rsaPem))
        {
            logger.LogWarning("JWT_SECRET is not set. Deriving a signing key from the RSA private key configuration.");
            return SHA256.HashData(Encoding.UTF8.GetBytes(rsaPem.Trim()));
        }

        logger.LogWarning("JWT_SECRET is not set. Using a development signing key. Set JWT_SECRET in production.");
        return SHA256.HashData("birooni-dev-jwt-secret-change-me-2026"u8.ToArray());
    }
}
