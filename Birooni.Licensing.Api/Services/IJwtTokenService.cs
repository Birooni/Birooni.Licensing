using Birooni.Licensing.Api.Models;

namespace Birooni.Licensing.Api.Services;

public interface IJwtTokenService
{
    string CreateToken(CustomerAccount account);
    byte[] GetSigningKeyBytes();
}
