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
  /// <summary>
  /// A professional registered a service they performed. Retired by the bills (ADR-0020 stages 2 and 6): the API answers
  /// 410 and no role holds it any more; it stays in the catalog while records registered before wait for review.
  /// </summary>
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

  /// <summary>ADR-0020: sees stock, costs, the stock card and the unit profit of the products.</summary>
  public const string InventoryView = "inventory.view";

  /// <summary>ADR-0020: records merchandise receipts with their cost (and the purchase expense) and sets the minimum stock.</summary>
  public const string InventoryReceive = "inventory.receive";

  /// <summary>ADR-0020: corrects the stock by hand, with a reason.</summary>
  public const string InventoryAdjust = "inventory.adjust";

  /// <summary>ADR-0020: opens bills and edits one's own while they are open.</summary>
  public const string BillsOpen = "bills.open";

  /// <summary>ADR-0020: closes a bill, which sends it to the approval tray.</summary>
  public const string BillsClose = "bills.close";

  /// <summary>ADR-0020: records the payment; needed to close a bill with something to charge.</summary>
  public const string BillsCharge = "bills.charge";

  /// <summary>ADR-0020 Q2 and Q10: discounts and courtesies, with no cap and always with a reason.</summary>
  public const string BillsDiscount = "bills.discount";

  /// <summary>ADR-0020: sees and edits every open bill (the counter, other professionals' bills).</summary>
  public const string BillsManage = "bills.manage";

  /// <summary>ADR-0020: the approval tray, the audited edition of closed bills and the approval with its postings.</summary>
  public const string BillsApprove = "bills.approve";

  /// <summary>ADR-0020: unapproves an approved bill with a reason (its operation arrives with stage 4).</summary>
  public const string BillsUnapprove = "bills.unapprove";

  /// <summary>ADR-0020: voids a bill without postings, with a reason. Final.</summary>
  public const string BillsVoid = "bills.void";

  /// <summary>
  /// ADR-0020 Q16: codes taken out of the catalog. Their rows stay in the database untouched (nothing is deleted), but
  /// they are not listed, not granted, not issued in tokens, and saving a role leaves them as they are.
  /// </summary>
  public static IReadOnlyCollection<string> Retired { get; } = ["sales.register"];

  public static IReadOnlyCollection<string> All { get; } =
  [
    ServiceRecordsRegister,
    ServiceRecordsApprove,
    LoyaltySettingsManage,
    LoyaltyRewardsManage,
    LoyaltyRedemptionsApprove,
    LoyaltyRedemptionsDeliver,
    LoyaltyPointsAdjust,
    MembershipPaymentsConfirm,
    CustomerClaimsApprove,
    InventoryView,
    InventoryReceive,
    InventoryAdjust,
    BillsOpen,
    BillsClose,
    BillsCharge,
    BillsDiscount,
    BillsManage,
    BillsApprove,
    BillsUnapprove,
    BillsVoid,
  ];

  public static bool IsInCatalog(string code) => All.Contains(code, StringComparer.Ordinal);

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
    InventoryAdjust,
    BillsApprove,
    BillsUnapprove,
    BillsVoid,
  ];
}
