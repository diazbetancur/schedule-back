using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Barbershop.Application.Common.Exceptions;

namespace Api.Barbershop.Features.Auth;

internal static class ClaimsPrincipalExtensions
{
  public static Guid GetRequiredUserId(this ClaimsPrincipal principal)
  {
    var rawUserId = principal.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? principal.FindFirstValue(JwtRegisteredClaimNames.Sub);

    if (!Guid.TryParse(rawUserId, out var userId))
    {
      throw new UnauthorizedException("The current access token is invalid.");
    }

    return userId;
  }
}