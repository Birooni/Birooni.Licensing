using Birooni.Licensing.Api.DTOs.Requests;
using Birooni.Licensing.Api.DTOs.Responses;
using Birooni.Licensing.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Birooni.Licensing.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class LicenseController : ControllerBase
{
    private readonly ILicensingService _licensingService;
    private readonly ITokenSigner _tokenSigner;
    private readonly ILogger<LicenseController> _logger;

    public LicenseController(
        ILicensingService licensingService,
        ITokenSigner tokenSigner,
        ILogger<LicenseController> logger)
    {
        _licensingService = licensingService;
        _tokenSigner = tokenSigner;
        _logger = logger;
    }

    /// <summary>
    /// Activates a license on a specific device.
    /// </summary>
    [HttpPost("activate")]
    [ProducesResponseType(typeof(LicenseResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(LicenseResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(LicenseResult), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(LicenseResult), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Activate([FromBody] ActivateLicenseRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(LicenseResult.Fail("Invalid request payload."));
        }

        var ipAddress = GetClientIpAddress();
        var result = await _licensingService.ActivateAsync(request, ipAddress, cancellationToken);

        if (!result.Success)
        {
            if (result.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                return NotFound(result);
            }
            if (result.Message.Contains("limit", StringComparison.OrdinalIgnoreCase))
            {
                return Conflict(result);
            }
            return BadRequest(result);
        }

        return Ok(result);
    }

    /// <summary>
    /// Validates an active device activation and issues an updated 14-day cryptographic token.
    /// </summary>
    [HttpPost("validate")]
    [ProducesResponseType(typeof(LicenseResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(LicenseResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(LicenseResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Validate([FromBody] ValidateLicenseRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(LicenseResult.Fail("Invalid request payload."));
        }

        var ipAddress = GetClientIpAddress();
        var result = await _licensingService.ValidateAsync(request, ipAddress, cancellationToken);

        if (!result.Success)
        {
            if (result.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                return NotFound(result);
            }
            return BadRequest(result);
        }

        return Ok(result);
    }

    /// <summary>
    /// Deactivates a license on a specific device, releasing an activation slot.
    /// </summary>
    [HttpPost("deactivate")]
    [ProducesResponseType(typeof(DeactivateResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(DeactivateResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(DeactivateResult), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Deactivate([FromBody] DeactivateLicenseRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(new DeactivateResult
            {
                Success = false,
                Message = "Invalid request payload.",
                LicenseKey = request.LicenseKey,
                DeviceId = request.DeviceId
            });
        }

        var ipAddress = GetClientIpAddress();
        var result = await _licensingService.DeactivateAsync(request, ipAddress, cancellationToken);

        if (!result.Success)
        {
            if (result.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                return NotFound(result);
            }
            return BadRequest(result);
        }

        return Ok(result);
    }

    /// <summary>
    /// Issues a new 14-day trial license and activates it for the device (one trial per device).
    /// </summary>
    [HttpPost("trial")]
    [ProducesResponseType(typeof(LicenseResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(LicenseResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(LicenseResult), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Trial([FromBody] TrialLicenseRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(LicenseResult.Fail("Invalid request payload."));
        }

        var ipAddress = GetClientIpAddress();
        var result = await _licensingService.TrialAsync(request, ipAddress, cancellationToken);

        if (!result.Success)
        {
            if (result.Message.Contains("already been issued", StringComparison.OrdinalIgnoreCase))
            {
                return Conflict(result);
            }
            return BadRequest(result);
        }

        return Ok(result);
    }

    /// <summary>
    /// Retrieves the RSA public key in PEM format for offline signature verification by client applications.
    /// </summary>
    [HttpGet("public-key")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public IActionResult GetPublicKey()
    {
        var pem = _tokenSigner.GetPublicKeyPem();
        return Ok(new { publicKeyPem = pem });
    }

    private string? GetClientIpAddress()
    {
        // Check X-Forwarded-For header if behind reverse proxy
        if (Request.Headers.TryGetValue("X-Forwarded-For", out var forwardedFor))
        {
            var ip = forwardedFor.FirstOrDefault()?.Split(',').FirstOrDefault()?.Trim();
            if (!string.IsNullOrEmpty(ip))
            {
                return ip;
            }
        }

        return HttpContext.Connection.RemoteIpAddress?.ToString();
    }
}
