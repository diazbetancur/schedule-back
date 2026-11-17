namespace Barbershop.Application.Auth;

public static class AuthPolicyNames
{
  public const string Admin = "AdminPolicy";
  public const string Staff = "StaffPolicy";
  public const string Customer = "CustomerPolicy";

  /// <summary>
  /// ADR-0018 D4 (SONDA BUG-077): Admin, or a role holding loyalty.points.adjust or memberships.payments.confirm (checked
  /// against the database). Reading customers, their wallet and their memberships is what those permissions need to act.
  /// </summary>
  public const string CustomerLoyaltyOrPaymentsAccess = "CustomerLoyaltyOrPaymentsAccessPolicy";

  /// <summary>
  /// ADR-0020 stage 1: Admin, or a role holding any inventory permission (checked against the database). Reading the
  /// stock and its card is what receiving and adjusting need to act.
  /// </summary>
  public const string InventoryAccess = "InventoryAccessPolicy";

  /// <summary>ADR-0020: Admin, or any permission that works with bills (each action checks its own permission again).</summary>
  public const string BillsAccess = "BillsAccessPolicy";

  /// <summary>ADR-0020: Admin, or bills.approve still granted in the database (critical).</summary>
  public const string BillsApproveAccess = "BillsApproveAccessPolicy";

  /// <summary>ADR-0020 stage 4: Admin, or bills.unapprove still granted in the database (critical).</summary>
  public const string BillsUnapproveAccess = "BillsUnapproveAccessPolicy";

  /// <summary>ADR-0020: Admin, or bills.void still granted in the database (critical).</summary>
  public const string BillsVoidAccess = "BillsVoidAccessPolicy";
}