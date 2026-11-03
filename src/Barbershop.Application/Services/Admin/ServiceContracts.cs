namespace Barbershop.Application.Services.Admin;

public sealed record ServiceView(
    Guid Id,
    string Name,
    int BasePrice,
    int BusinessPercentage,
    bool IsActive,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    int LoyaltyPoints = 0);

// LoyaltyPoints is optional: null keeps the current points (0 for a new service), so clients that do not know
// about loyalty keep working. Sending it requires the loyalty.settings.manage permission.

public sealed record ServiceCreateRequest(string Name, int BasePrice, int BusinessPercentage = 0, int? LoyaltyPoints = null);

public sealed record ServiceUpdateRequest(string Name, int BasePrice, int BusinessPercentage = 0, int? LoyaltyPoints = null);

public sealed record ServiceStatusUpdateRequest(bool IsActive);
