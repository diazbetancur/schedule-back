namespace Barbershop.Application.Loyalty.Admin;

/// <summary><c>Version</c> must be sent back unchanged when updating, so a concurrent edit is detected.</summary>
public sealed record LoyaltySettingsView(
    bool IsEnabled,
    int PointsValidityDays,
    uint Version,
    DateTime? UpdatedAt,
    string? UpdatedByName,
    int RedemptionApprovalDays = 30,
    int RedemptionDeliveryDays = 30);

/// <summary>Null redemption deadlines keep the current ones (clients that predate them do not send them).</summary>
public sealed record LoyaltySettingsUpdateRequest(
    bool IsEnabled,
    int PointsValidityDays,
    uint? Version,
    int? RedemptionApprovalDays = null,
    int? RedemptionDeliveryDays = null);

public sealed record LoyaltySettingsChangeView(
    Guid Id,
    DateTime ChangedAt,
    Guid ChangedByUserId,
    string? ChangedByName,
    bool PreviousIsEnabled,
    bool NewIsEnabled,
    int PreviousPointsValidityDays,
    int NewPointsValidityDays,
    int? PreviousRedemptionApprovalDays = null,
    int? NewRedemptionApprovalDays = null,
    int? PreviousRedemptionDeliveryDays = null,
    int? NewRedemptionDeliveryDays = null);
