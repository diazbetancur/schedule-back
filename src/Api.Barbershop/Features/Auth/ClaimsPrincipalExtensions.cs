using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Barbershop.Application.Common.Exceptions;

namespace Api.Barbershop.Features.Auth;

internal static class ClaimsPrincipalExtensions
{
  public static Guid GetRequiredUserId(this ClaimsPrincipal principal)
  {
    if (!principal.TryGetUserId(out var userId))
    {
      throw new UnauthorizedException("The current access token is invalid.");
    }

    return userId;
  }

  public static bool TryGetUserId(this ClaimsPrincipal principal, out Guid userId)
  {
    var rawUserId = principal.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? principal.FindFirstValue(JwtRegisteredClaimNames.Sub);

    return Guid.TryParse(rawUserId, out userId);
  }
}