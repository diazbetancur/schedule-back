namespace Barbershop.Domain.Media;

public sealed class PendingFileDeletion
{
  private PendingFileDeletion() { }

  public PendingFileDeletion(string storageKey, string? reason, DateTime createdAt)
  {
    StorageKey = storageKey;
    Reason = reason;
    CreatedAt = createdAt;
    Attempts = 0;
  }

  public Guid Id { get; private set; } = Guid.NewGuid();
  public string StorageKey { get; private set; } = string.Empty;
  public string? Reason { get; private set; }
  public int Attempts { get; private set; }
  public DateTime CreatedAt { get; private set; }
}
