namespace Barbershop.Application.Auth;

/// <summary>
/// The user making the current request. Null outside of an authenticated request
/// (background jobs, tests). Used, for example, to avoid notifying someone about an action they did themselves.
/// </summary>
public interface ICurrentUserAccessor
{
  Guid? UserId { get; }
}
