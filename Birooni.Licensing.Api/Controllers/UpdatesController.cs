using Birooni.Licensing.Api.DTOs.Responses;
using Birooni.Licensing.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Birooni.Licensing.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public class UpdatesController : ControllerBase
{
    private readonly IUpdateService _updateService;

    public UpdatesController(IUpdateService updateService)
    {
        _updateService = updateService;
    }

    /// <summary>
    /// Checks if a newer version of a Birooni plugin is available for a given Revit version.
    /// </summary>
    /// <param name="product">Product code (e.g. BiruBox, BirooniSuite)</param>
    /// <param name="version">Currently installed plugin version (e.g. 1.0.0)</param>
    /// <param name="revitVersion">Installed Autodesk Revit version year (e.g. 2025, 2026, 2027)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    [HttpGet("check")]
    [ProducesResponseType(typeof(UpdateCheckResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Check(
        [FromQuery] string product,
        [FromQuery] string version,
        [FromQuery] string? revitVersion,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(product) || string.IsNullOrWhiteSpace(version))
        {
            return BadRequest(new { error = "Parameters 'product' and 'version' are required." });
        }

        var result = await _updateService.CheckForUpdateAsync(product, version, revitVersion, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Returns the release history for a product.
    /// </summary>
    [HttpGet("releases")]
    public async Task<IActionResult> GetReleases([FromQuery] string? product, CancellationToken cancellationToken)
    {
        var releases = await _updateService.GetReleasesAsync(product, cancellationToken);
        return Ok(releases);
    }
}
