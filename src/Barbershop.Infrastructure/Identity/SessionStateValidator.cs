using Barbershop.Application.Auth;
using Barbershop.Infrastructure.Persistence;

namespace Barbershop.Infrastructure.Identity;

internal sealed class SessionStateValidator : ISessionStateValidator
{
    private readonly AppDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public SessionStateValidator(AppDbContext dbContext, TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
    }

    public Task<bool> IsActiveAsync(Guid userId, Guid sessionId, CancellationToken cancellationToken = default)
        => AccountSessions.IsSessionActiveAsync(
            _dbContext, userId, sessionId, _timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
}
