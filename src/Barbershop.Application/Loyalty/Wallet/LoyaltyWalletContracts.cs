namespace Barbershop.Application.Loyalty.Wallet;

/// <summary>
/// A lot as the admin sees it. <c>UsablePoints</c> is 0 once <c>ExpiresOn</c> (inclusive, Bogotá) has passed, even
/// before the daily job records the expiration.
/// </summary>
public sealed record LoyaltyLotView(
    Guid Id,
    string SourceType,
    Guid SourceId,
    string SourceLabel,
    int InitialPoints,
    int RemainingPoints,
    int UsablePoints,
    DateOnly AccruedOn,
    int ValidityDays,
    DateOnly ExpiresOn,
    bool IsExpired,
    Guid? OriginLotId,
    Guid? OriginCustomerId,
    int ReservedPoints = 0);

/// <summary><c>IncurredAt</c>: when the original debt arose (kept through merges; it sets the settlement order).</summary>
public sealed record LoyaltyDebtView(
    Guid Id,
    string Origin,
    int Amount,
    int OutstandingPoints,
    Guid? OriginDebtId,
    Guid? OriginCustomerId,
    DateTime CreatedAt,
    DateTime IncurredAt);

public sealed record LoyaltyMovementView(
    Guid Id,
    string Type,
    int Points,
    Guid? LotId,
    Guid? DebtId,
    string? Reason,
    Guid? RelatedCustomerId,
    string? PerformedByName,
    DateTime PerformedAt,
    Guid? RedemptionId = null);

/// <summary>
/// The wallet of one profile. A merged profile keeps its own history (mostly closed by the merge);
/// <c>LiveCustomerId</c> is the surviving profile that now holds the points.
/// </summary>
public sealed record LoyaltyWalletView(
    Guid CustomerId,
    Guid LiveCustomerId,
    bool IsMerged,
    bool ProgramEnabled,
    int AvailablePoints,
    int DebtPoints,
    int ExpiringSoonPoints,
    DateOnly? NextExpiresOn,
    IReadOnlyList<LoyaltyLotView> Lots,
    IReadOnlyList<LoyaltyDebtView> Debts,
    IReadOnlyList<LoyaltyMovementView> Movements,
    int ReservedPoints = 0,
    IReadOnlyList<Barbershop.Application.Loyalty.Redemptions.LoyaltyRedemptionView>? Redemptions = null);

/// <summary>A manual credit or debit; the debit never goes beyond the available balance.</summary>
public sealed record LoyaltyAdjustmentRequest(string? Direction, int Points, string? Reason, Guid IdempotencyKey);

/// <summary>The customer's own points: no internal notes, actors or other profiles.</summary>
public sealed record MyLoyaltyLotView(int UsablePoints, DateOnly AccruedOn, DateOnly ExpiresOn, string SourceLabel);

public sealed record MyLoyaltyMovementView(string Type, int Points, DateTime PerformedAt);

public sealed record MyLoyaltyView(
    bool HasProfile,
    bool ProgramEnabled,
    int AvailablePoints,
    int DebtPoints,
    int ExpiringSoonPoints,
    DateOnly? NextExpiresOn,
    IReadOnlyList<MyLoyaltyLotView> Lots,
    IReadOnlyList<MyLoyaltyMovementView> Movements,
    int ReservedPoints = 0);

/// <summary>A lot a merge would move to the surviving profile, with its own expiry.</summary>
public sealed record MergeLoyaltyLotPreview(Guid LotId, string SourceLabel, int Points, DateOnly ExpiresOn, bool ExpiresSoon);

/// <summary>
/// Points side of a merge preview (ADR-0013 D7): balances and debts of both profiles and the result after the
/// common settlement rule. Expired points are not moved and are reported apart.
/// </summary>
public sealed record MergeLoyaltyPreview(
    int SourceAvailablePoints,
    int SourceDebtPoints,
    int TargetAvailablePoints,
    int TargetDebtPoints,
    int ResultAvailablePoints,
    int ResultDebtPoints,
    int SourceExpiredPointsNotMoved,
    IReadOnlyList<MergeLoyaltyLotPreview> LotsToTransfer);

public interface IMyLoyaltyService
{
    Task<MyLoyaltyView> GetMineAsync(Guid currentUserId, CancellationToken cancellationToken = default);
}

public interface IAdminCustomerLoyaltyService
{
    Task<LoyaltyWalletView> GetWalletAsync(Guid customerId, CancellationToken cancellationToken = default);

    Task<LoyaltyWalletView> AdjustAsync(Guid currentUserId, Guid customerId, LoyaltyAdjustmentRequest request, CancellationToken cancellationToken = default);
}
