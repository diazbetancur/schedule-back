using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Users.Admin;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Infrastructure.Users;

internal sealed class AdminUsersService : IAdminUsersService
{
    private readonly AppDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public AdminUsersService(AppDbContext dbContext, TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<AdminUserListItem>> GetAllActiveAsync(CancellationToken cancellationToken = default)
    {
        return await _dbContext.Users
            .Where(u => u.IsActive)
            .Include(u => u.UserRoles)
            .ThenInclude(ur => ur.Role)
            .OrderBy(u => u.FullName)
            .Select(u => ToListItem(u))
            .ToListAsync(cancellationToken);
    }

    public async Task<AdminUserListItem> GetByIdAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await _dbContext.Users
            .Where(u => u.Id == userId && u.IsActive)
            .Include(u => u.UserRoles)
            .ThenInclude(ur => ur.Role)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException($"User {userId} not found.");

        return ToListItem(user);
    }

    public async Task<AdminUserListItem> UpdateAsync(Guid userId, AdminUserUpdateRequest request, CancellationToken cancellationToken = default)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(request.FullName) || request.FullName.Trim().Length is < 2 or > 120)
            errors["fullName"] = ["El nombre debe tener entre 2 y 120 caracteres."];
        if (!string.IsNullOrWhiteSpace(request.PhoneNumber) && request.PhoneNumber.Trim().Length > 40)
            errors["phoneNumber"] = ["El teléfono debe tener 40 caracteres o menos."];
        if (errors.Count > 0)
            throw new ValidationProblemException(errors);

        var user = await _dbContext.Users
            .Where(u => u.Id == userId && u.IsActive)
            .Include(u => u.UserRoles)
            .ThenInclude(ur => ur.Role)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException($"User {userId} not found.");

        var updatedAt = _timeProvider.GetUtcNow().UtcDateTime;
        user.UpdateCustomerProfile(request.FullName, request.PhoneNumber, null, updatedAt);

        await _dbContext.SaveChangesAsync(cancellationToken);
        return ToListItem(user);
    }

    public async Task DeactivateAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await _dbContext.Users
            .Where(u => u.Id == userId && u.IsActive)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException($"User {userId} not found.");

        user.Deactivate(_timeProvider.GetUtcNow().UtcDateTime);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private static AdminUserListItem ToListItem(Barbershop.Domain.Users.User u) =>
        new(
            u.Id,
            u.FullName,
            u.Email,
            u.PhoneNumber,
            u.UserRoles.Select(ur => ur.Role.Name).OrderBy(r => r).ToArray(),
            u.IsActive,
            u.CreatedAt);
}
