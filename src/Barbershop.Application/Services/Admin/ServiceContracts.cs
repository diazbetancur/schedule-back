namespace Barbershop.Application.Services.Admin;

public sealed record ServiceView(
    Guid Id,
    string Name,
    int BasePrice,
    int BusinessPercentage,
    bool IsActive,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

public sealed record ServiceCreateRequest(string Name, int BasePrice, int BusinessPercentage = 0);

public sealed record ServiceUpdateRequest(string Name, int BasePrice, int BusinessPercentage = 0);

public sealed record ServiceStatusUpdateRequest(bool IsActive);
