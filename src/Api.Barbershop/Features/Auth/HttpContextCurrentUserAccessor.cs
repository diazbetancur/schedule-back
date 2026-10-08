using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Barbershop.Application.Auth;

namespace Api.Barbershop.Features.Auth;

internal sealed class HttpContextCurrentUserAccessor : ICurrentUserAccessor
{
  private readonly IHttpContextAccessor _httpContextAccessor;

  public HttpContextCurrentUserAccessor(IHttpContextAccessor httpContextAccessor)
  {
    _httpContextAccessor = httpContextAccessor;
  }

  public Guid? UserId
  {
    get
    {
      var principal = _httpContextAccessor.HttpContext?.User;
      if (principal?.Identity?.IsAuthenticated != true)
      {
        return null;
      }

      var rawUserId = principal.FindFirstValue(ClaimTypes.NameIdentifier)
          ?? principal.FindFirstValue(JwtRegisteredClaimNames.Sub);

      return Guid.TryParse(rawUserId, out var userId) ? userId : null;
    }
  }
}
