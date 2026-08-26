namespace Barbershop.Infrastructure.Identity;

public interface IIdentitySeedService
{
  Task EnsureSeededAsync(CancellationToken cancellationToken = default);
}