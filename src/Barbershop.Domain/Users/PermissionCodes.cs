namespace Barbershop.Domain.Users;

/// <summary>
/// Fixed permission catalog, seeded into the database and carried as claims in the access token.
/// <para>
/// A token keeps the permissions it was issued with until it is refreshed (up to the access-token lifetime), so a
/// permission removed in the meantime still appears in it. Critical loyalty operations (approving service records,
/// confirming membership payments, approving or delivering redemptions, adjusting points, approving profile claims
/// and changing loyalty settings) are therefore listed in <see cref="Critical"/>: their policies also check, on every
/// request, that the acting user still holds the permission according to the database.
/// </para>
/// </summary>
public static class PermissionCodes
{
  public const string SalesRegister = "sales.register";

  /// <summary>A professional registers a service they actually performed.</summary>
  public const string ServiceRecordsRegister = "service-records.register";

  /// <summary>Reviews, corrects and confirms registered services. An admin who is also a professional may confirm
  /// their own records; the operation records who approved it.</summary>
  public const string ServiceRecordsApprove = "service-records.approve";

  public const string LoyaltySettingsManage = "loyalty.settings.manage";
  public const string LoyaltyRewardsManage = "loyalty.rewards.manage";

  /// <summary>Approves or rejects a customer's redemption request.</summary>
  public const string LoyaltyRedemptionsApprove = "loyalty.redemptions.approve";

  /// <summary>Confirms that an approved reward was handed over, which makes the points discount final.</summary>
  public const string LoyaltyRedemptionsDeliver = "loyalty.redemptions.deliver";

  public const string LoyaltyPointsAdjust = "loyalty.points.adjust";
  public const string MembershipPaymentsConfirm = "memberships.payments.confirm";
  public const string CustomerClaimsApprove = "customers.claims.approve";

  public static IReadOnlyCollection<string> All { get; } =
  [
    SalesRegister,
    ServiceRecordsRegister,
    ServiceRecordsApprove,
    LoyaltySettingsManage,
    LoyaltyRewardsManage,
    LoyaltyRedemptionsApprove,
    LoyaltyRedemptionsDeliver,
    LoyaltyPointsAdjust,
    MembershipPaymentsConfirm,
    CustomerClaimsApprove,
  ];

  /// <summary>
  /// The critical permissions: their policies also check the database on every request, so a revocation applies to
  /// the next operation even with a token issued before it. The other permissions are authorized from the token alone.
  /// </summary>
  public static IReadOnlyCollection<string> Critical { get; } =
  [
    ServiceRecordsApprove,
    LoyaltySettingsManage,
    LoyaltyRedemptionsApprove,
    LoyaltyRedemptionsDeliver,
    LoyaltyPointsAdjust,
    MembershipPaymentsConfirm,
    CustomerClaimsApprove,
  ];
}
