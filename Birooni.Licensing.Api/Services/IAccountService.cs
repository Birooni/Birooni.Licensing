using Birooni.Licensing.Api.DTOs.Requests;
using Birooni.Licensing.Api.DTOs.Responses;

namespace Birooni.Licensing.Api.Services;

public interface IAccountService
{
    Task<AuthResponse> SignupAsync(SignupRequest request, CancellationToken cancellationToken = default);
    Task<AuthResponse> SigninAsync(SigninRequest request, CancellationToken cancellationToken = default);
    Task<AuthResponse> VerifyEmailAsync(string token, CancellationToken cancellationToken = default);
    Task<AuthResponse> ResendVerificationAsync(string email, CancellationToken cancellationToken = default);
    Task<AccountProfileDto?> GetProfileAsync(Guid accountId, CancellationToken cancellationToken = default);
    Task<List<PurchaseDto>> GetPurchasesAsync(Guid accountId, CancellationToken cancellationToken = default);
}
