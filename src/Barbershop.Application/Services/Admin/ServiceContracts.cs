namespace Barbershop.Application.Services.Admin;

// A "service" here is a settlement rate (ADR-0016 D3): price, business share and points, attached to one general
// service. GeneralServiceId/Name, CategoryId/Name describe that general service.
public sealed record ServiceView(
    Guid Id,
    string Name,
    int BasePrice,
    int BusinessPercentage,
    bool IsActive,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    int LoyaltyPoints = 0,
    Guid GeneralServiceId = default,
    string GeneralServiceName = "",
    bool GeneralServiceIsActive = true,
    Guid CategoryId = default,
    string CategoryName = "");

// LoyaltyPoints is optional: null keeps the current points (0 for a new service), so clients that do not know
// about loyalty keep working. Sending it requires the loyalty.settings.manage permission.
// GeneralServiceId is required to create a rate; on update null keeps the current general service.

public sealed record ServiceCreateRequest(
    string Name, int BasePrice, int? BusinessPercentage = null, int? LoyaltyPoints = null, Guid? GeneralServiceId = null);

public sealed record ServiceUpdateRequest(
    string Name, int BasePrice, int? BusinessPercentage = null, int? LoyaltyPoints = null, Guid? GeneralServiceId = null);

public sealed record ServiceStatusUpdateRequest(bool IsActive);

/// <summary>
/// Filters of <c>GET /admin/services</c> (ADR-0016 B7): <c>Q</c> matches part of the rate name, <c>GeneralServiceId</c>
/// or <c>CategoryId</c> the general service it belongs to, and <c>Status</c> is Active or Inactive.
/// </summary>
public sealed record ServiceListQuery(
    string? Q = null,
    Guid? GeneralServiceId = null,
    Guid? CategoryId = null,
    string? Status = null,
    int? Page = null,
    int? PageSize = null);

/// <summary>
/// What still points at a rate (BUG-103): the active membership plans that include it and the active loyalty rewards
/// that deliver it. Shown before deleting it, so the admin knows those plans and rewards stop being sellable.
/// </summary>
public sealed record ServiceUsageView(IReadOnlyList<string> ActivePlans, IReadOnlyList<string> ActiveRewards);
