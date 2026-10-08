using Barbershop.Application.Auth;

namespace Barbershop.Infrastructure.Identity;

/// <summary>Default when there is no HTTP request (the API registers the real one).</summary>
internal sealed class NullCurrentUserAccessor : ICurrentUserAccessor
{
  public Guid? UserId => null;
}
