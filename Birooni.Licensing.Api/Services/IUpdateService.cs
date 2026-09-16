using Birooni.Licensing.Api.DTOs.Requests;
using Birooni.Licensing.Api.DTOs.Responses;
using Birooni.Licensing.Api.Models;

namespace Birooni.Licensing.Api.Services;

public interface IUpdateService
{
    Task<UpdateCheckResponse> CheckForUpdateAsync(string product, string currentVersion, string? revitVersion = null, CancellationToken cancellationToken = default);
    Task<ProductRelease> CreateReleaseAsync(CreateReleaseRequest request, CancellationToken cancellationToken = default);
    Task<List<ProductRelease>> GetReleasesAsync(string? product = null, CancellationToken cancellationToken = default);
}
