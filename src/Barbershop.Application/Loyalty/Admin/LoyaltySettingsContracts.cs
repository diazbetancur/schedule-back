namespace Barbershop.Application.Loyalty.Admin;

/// <summary><c>Version</c> must be sent back unchanged when updating, so a concurrent edit is detected.</summary>
public sealed record LoyaltySettingsView(
    bool IsEnabled,
    int PointsValidityDays,
    uint Version,
    DateTime? UpdatedAt,
    string? UpdatedByName);

public sealed record LoyaltySettingsUpdateRequest(bool IsEnabled, int PointsValidityDays, uint? Version);

public sealed record LoyaltySettingsChangeView(
    Guid Id,
    DateTime ChangedAt,
    Guid ChangedByUserId,
    string? ChangedByName,
    bool PreviousIsEnabled,
    bool NewIsEnabled,
    int PreviousPointsValidityDays,
    int NewPointsValidityDays);
