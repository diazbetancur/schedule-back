using Barbershop.Domain.Common;

namespace Barbershop.Domain.Users;

public sealed class RefreshToken
{
  private RefreshToken()
  {
  }

  public RefreshToken(Guid userId, string tokenHash, DateTime expiresAt, DateTime createdAt, string? deviceLabel = null)
  {
    UserId = userId;
    TokenHash = DomainValidation.Required(tokenHash, nameof(tokenHash), 512, 32);
    ExpiresAt = DomainValidation.EnsureUtc(expiresAt, nameof(expiresAt));
    CreatedAt = DomainValidation.EnsureUtc(createdAt, nameof(createdAt));
    DeviceLabel = DomainValidation.Optional(deviceLabel, 120);
  }

  public Guid Id { get; private set; } = Guid.NewGuid();
  public Guid UserId { get; private set; }
  public string TokenHash { get; private set; } = string.Empty;
  public DateTime ExpiresAt { get; private set; }
  public DateTime? RevokedAt { get; private set; }
  public Guid? ReplacedByRefreshTokenId { get; private set; }
  public DateTime CreatedAt { get; private set; }
  public string? DeviceLabel { get; private set; }
  public User User { get; private set; } = null!;

  public bool IsActive(DateTime utcNow) => RevokedAt is null && ExpiresAt > DomainValidation.EnsureUtc(utcNow, nameof(utcNow));

  public void Revoke(DateTime revokedAt, Guid? replacedByRefreshTokenId = null)
  {
    RevokedAt = DomainValidation.EnsureUtc(revokedAt, nameof(revokedAt));
    ReplacedByRefreshTokenId = replacedByRefreshTokenId;
  }
}