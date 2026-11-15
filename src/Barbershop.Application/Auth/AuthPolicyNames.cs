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
}