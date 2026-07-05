namespace Barbershop.Application.Services.Admin;

public sealed record ServiceView(
    Guid Id,
    string Name,
    int BasePrice,
    bool IsActive,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

public sealed record ServiceCreateRequest(string Name, int BasePrice);

public sealed record ServiceUpdateRequest(string Name, int BasePrice);

public sealed record ServiceStatusUpdateRequest(bool IsActive);
