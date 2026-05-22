namespace Barbershop.Infrastructure.Identity;

internal interface IIdentitySeedService
{
  Task EnsureSeededAsync(CancellationToken cancellationToken = default);
}