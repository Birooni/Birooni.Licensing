using Birooni.Licensing.Api.Common;
using Birooni.Licensing.Api.DTOs.Requests;
using Birooni.Licensing.Api.DTOs.Responses;
using Birooni.Licensing.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Birooni.Licensing.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[AdminApiKey]
[Produces("application/json")]
public class AdminController : ControllerBase
{
    private readonly ILicensingService _licensingService;
    private readonly IUpdateService _updateService;
    private readonly IEmailSender _email;
    private readonly ILogger<AdminController> _logger;

    public AdminController(
        ILicensingService licensingService,
        IUpdateService updateService,
        IEmailSender email,
        ILogger<AdminController> logger)
    {
        _licensingService = licensingService;
        _updateService = updateService;
        _email = email;
        _logger = logger;
    }

    [HttpGet("smtp-status")]
    public IActionResult SmtpStatus() => Ok(new { configured = _email.IsConfigured });

    [HttpPost("test-email")]
    public async Task<IActionResult> TestEmail([FromBody] TestEmailRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.To) || !request.To.Contains('@'))
        {
            return BadRequest(new { error = "Provide a valid 'to' address." });
        }

        try
        {
            await _email.SendAsync(
                request.To.Trim(),
                "Ibrooni SMTP test",
                "<p>If you received this, verification mail can send.</p>",
                cancellationToken);
            return Ok(new { success = true, message = "Test email sent." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SMTP test failed");
            return StatusCode(500, new { error = "SMTP send failed: " + ex.Message });
        }
    }

    /// <summary>
    /// Returns high-level statistics for the admin dashboard.
    /// </summary>
    [HttpGet("stats")]
    [ProducesResponseType(typeof(AdminStatsResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetStats(CancellationToken cancellationToken)
    {
        var stats = await _licensingService.GetAdminStatsAsync(cancellationToken);
        return Ok(stats);
    }

    /// <summary>
    /// Lists all licenses with optional filtering by search term, mode, or active status.
    /// </summary>
    [HttpGet("licenses")]
    [ProducesResponseType(typeof(List<AdminLicenseDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetLicenses(
        [FromQuery] string? search,
        [FromQuery] string? mode,
        [FromQuery] bool? isActive,
        CancellationToken cancellationToken)
    {
        var list = await _licensingService.GetAdminLicensesAsync(search, mode, isActive, cancellationToken);
        return Ok(list);
    }

    /// <summary>
    /// Creates a custom or commercial license (Individual, SiteLicense, or Floating).
    /// </summary>
    [HttpPost("licenses")]
    [ProducesResponseType(typeof(LicenseResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(LicenseResult), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateLicense(
        [FromBody] CreateCustomLicenseRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(LicenseResult.Fail("Invalid request payload."));
        }

        var result = await _licensingService.CreateCustomLicenseAsync(request, cancellationToken);
        if (!result.Success)
        {
            return BadRequest(result);
        }

        return Ok(result);
    }

    /// <summary>
    /// Revokes a license, immediately invalidating all active activations.
    /// </summary>
    [HttpPost("licenses/{id:guid}/revoke")]
    public async Task<IActionResult> RevokeLicense(
        Guid id,
        [FromQuery] string? reason,
        CancellationToken cancellationToken)
    {
        var success = await _licensingService.RevokeLicenseAsync(id, reason, cancellationToken);
        if (!success)
        {
            return NotFound(new { error = $"License with ID '{id}' was not found." });
        }

        return Ok(new { success = true, message = "License and all active device seats revoked successfully." });
    }

    /// <summary>
    /// Lists activated machines across all licenses.
    /// </summary>
    [HttpGet("devices")]
    [ProducesResponseType(typeof(List<AdminDeviceDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDevices(
        [FromQuery] string? search,
        CancellationToken cancellationToken)
    {
        var devices = await _licensingService.GetAdminDevicesAsync(search, cancellationToken);
        return Ok(devices);
    }

    /// <summary>
    /// Releases a specific machine's activation seat.
    /// </summary>
    [HttpPost("devices/{id:guid}/release")]
    public async Task<IActionResult> ReleaseDevice(Guid id, CancellationToken cancellationToken)
    {
        var success = await _licensingService.ReleaseDeviceAsync(id, cancellationToken);
        if (!success)
        {
            return NotFound(new { error = $"Device activation with ID '{id}' was not found." });
        }

        return Ok(new { success = true, message = "Device activation seat released successfully." });
    }

    /// <summary>
    /// Publishes a new software release / update.
    /// </summary>
    [HttpPost("releases")]
    public async Task<IActionResult> CreateRelease(
        [FromBody] CreateReleaseRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(new { error = "Invalid release payload." });
        }

        var release = await _updateService.CreateReleaseAsync(request, cancellationToken);
        return Ok(release);
    }

    /// <summary>
    /// Lists all published software releases.
    /// </summary>
    [HttpGet("releases")]
    public async Task<IActionResult> GetReleases(
        [FromQuery] string? product,
        CancellationToken cancellationToken)
    {
        var releases = await _updateService.GetReleasesAsync(product, cancellationToken);
        return Ok(releases);
    }
}
