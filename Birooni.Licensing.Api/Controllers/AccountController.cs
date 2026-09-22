using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Birooni.Licensing.Api.DTOs.Requests;
using Birooni.Licensing.Api.DTOs.Responses;
using Birooni.Licensing.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Birooni.Licensing.Api.Controllers;

[ApiController]
[Route("api/account")]
[Produces("application/json")]
public class AccountController : ControllerBase
{
    private readonly IAccountService _accounts;
    private readonly IEmailSender _email;

    public AccountController(IAccountService accounts, IEmailSender email)
    {
        _accounts = accounts;
        _email = email;
    }

    [HttpGet("mail-status")]
    [AllowAnonymous]
    public IActionResult MailStatus() => Ok(new { smtpConfigured = _email.IsConfigured });

    [HttpPost("signup")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Signup([FromBody] SignupRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(AuthResponse.Fail("Please provide your name, email, and a password of at least 8 characters."));
        }

        var result = await _accounts.SignupAsync(request, cancellationToken);
        if (!result.Success)
        {
            if (result.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                return Conflict(result);
            }

            return BadRequest(result);
        }

        return Ok(result);
    }

    [HttpPost("signin")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Signin([FromBody] SigninRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return Unauthorized(AuthResponse.Fail("Email and password are required."));
        }

        var result = await _accounts.SigninAsync(request, cancellationToken);
        if (!result.Success)
        {
            return Unauthorized(result);
        }

        return Ok(result);
    }

    [HttpPost("verify")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Verify([FromBody] VerifyEmailRequest request, CancellationToken cancellationToken)
    {
        var result = await _accounts.VerifyEmailAsync(request.Token, cancellationToken);
        if (!result.Success) return BadRequest(result);
        return Ok(result);
    }

    [HttpGet("verify")]
    [AllowAnonymous]
    public async Task<IActionResult> VerifyGet([FromQuery] string? token, CancellationToken cancellationToken)
    {
        var web = (HttpContext.RequestServices.GetRequiredService<IConfiguration>()["App:PublicWebUrl"]
                   ?? "https://ibrooni.com").TrimEnd('/');
        if (string.IsNullOrWhiteSpace(token))
        {
            return Redirect($"{web}/account.html?verifyError=1");
        }

        var result = await _accounts.VerifyEmailAsync(token, cancellationToken);
        if (!result.Success)
        {
            return Redirect($"{web}/account.html?verifyError=1");
        }

        return Redirect($"{web}/account.html?verified=1");
    }

    [HttpPost("resend-verification")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Resend([FromBody] ResendVerificationRequest request, CancellationToken cancellationToken)
    {
        var result = await _accounts.ResendVerificationAsync(request.Email, cancellationToken);
        return Ok(result);
    }

    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType(typeof(AccountProfileDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Me(CancellationToken cancellationToken)
    {
        var accountId = GetAccountId();
        if (accountId == null)
        {
            return Unauthorized(new { error = "Invalid session." });
        }

        var profile = await _accounts.GetProfileAsync(accountId.Value, cancellationToken);
        if (profile == null)
        {
            return Unauthorized(new { error = "Account was not found." });
        }

        return Ok(profile);
    }

    [HttpGet("purchases")]
    [Authorize]
    [ProducesResponseType(typeof(List<PurchaseDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Purchases(CancellationToken cancellationToken)
    {
        var accountId = GetAccountId();
        if (accountId == null)
        {
            return Unauthorized(new { error = "Invalid session." });
        }

        var purchases = await _accounts.GetPurchasesAsync(accountId.Value, cancellationToken);
        return Ok(purchases);
    }

    private Guid? GetAccountId()
    {
        var sub = User.FindFirstValue(JwtRegisteredClaimNames.Sub)
                  ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}
