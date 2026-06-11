using Barbershop.Domain.Common;

namespace Barbershop.Domain.Users;

public sealed class PushSubscription
{
  private PushSubscription()
  {
  }

  public PushSubscription(Guid userId, string endpoint, string p256dhKey, string authKey, DateTime createdAt, string? userAgent = null)
  {
    UserId = userId;
    Endpoint = DomainValidation.Required(endpoint, nameof(endpoint), 2048);
    P256dhKey = DomainValidation.Required(p256dhKey, nameof(p256dhKey), 256);
    AuthKey = DomainValidation.Required(authKey, nameof(authKey), 256);
    UserAgent = DomainValidation.Optional(userAgent, 256);
    CreatedAt = DomainValidation.EnsureUtc(createdAt, nameof(createdAt));
  }

  public Guid Id { get; private set; } = Guid.NewGuid();
  public Guid UserId { get; private set; }
  public string Endpoint { get; private set; } = string.Empty;
  public string P256dhKey { get; private set; } = string.Empty;
  public string AuthKey { get; private set; } = string.Empty;
  public string? UserAgent { get; private set; }
  public DateTime CreatedAt { get; private set; }
  public User User { get; private set; } = null!;
}
