using Barbershop.Domain.Users;

namespace Barbershop.Application.Auth;

/// <summary>
/// One authorization policy per permission code; each requires the matching permission claim. The claim reflects the
/// permissions at token issue time, so the policies of <see cref="PermissionCodes.Critical"/> also check the database
/// through <see cref="IUserPermissionVerifier"/> on every request.
/// </summary>
public static class PermissionPolicyNames
{
    public const string ServiceRecordsRegister = "ServiceRecordsRegisterPolicy";
    public const string ServiceRecordsApprove = "ServiceRecordsApprovePolicy";
    public const string LoyaltySettingsManage = "LoyaltySettingsManagePolicy";
    public const string LoyaltyRewardsManage = "LoyaltyRewardsManagePolicy";
    public const string LoyaltyRedemptionsApprove = "LoyaltyRedemptionsApprovePolicy";
    public const string LoyaltyRedemptionsDeliver = "LoyaltyRedemptionsDeliverPolicy";
    public const string LoyaltyPointsAdjust = "LoyaltyPointsAdjustPolicy";
    public const string MembershipPaymentsConfirm = "MembershipPaymentsConfirmPolicy";
    public const string CustomerClaimsApprove = "CustomerClaimsApprovePolicy";
    public const string InventoryView = "InventoryViewPolicy";
    public const string InventoryReceive = "InventoryReceivePolicy";
    public const string InventoryAdjust = "InventoryAdjustPolicy";
    public const string BillsOpen = "BillsOpenPolicy";
    public const string BillsClose = "BillsClosePolicy";
    public const string BillsCharge = "BillsChargePolicy";
    public const string BillsDiscount = "BillsDiscountPolicy";
    public const string BillsManage = "BillsManagePolicy";
    public const string BillsApprove = "BillsApprovePolicy";
    public const string BillsUnapprove = "BillsUnapprovePolicy";
    public const string BillsVoid = "BillsVoidPolicy";

    private static readonly IReadOnlyDictionary<string, string> ByPermissionCode =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PermissionCodes.ServiceRecordsRegister] = ServiceRecordsRegister,
            [PermissionCodes.ServiceRecordsApprove] = ServiceRecordsApprove,
            [PermissionCodes.LoyaltySettingsManage] = LoyaltySettingsManage,
            [PermissionCodes.LoyaltyRewardsManage] = LoyaltyRewardsManage,
            [PermissionCodes.LoyaltyRedemptionsApprove] = LoyaltyRedemptionsApprove,
            [PermissionCodes.LoyaltyRedemptionsDeliver] = LoyaltyRedemptionsDeliver,
            [PermissionCodes.LoyaltyPointsAdjust] = LoyaltyPointsAdjust,
            [PermissionCodes.MembershipPaymentsConfirm] = MembershipPaymentsConfirm,
            [PermissionCodes.CustomerClaimsApprove] = CustomerClaimsApprove,
            [PermissionCodes.InventoryView] = InventoryView,
            [PermissionCodes.InventoryReceive] = InventoryReceive,
            [PermissionCodes.InventoryAdjust] = InventoryAdjust,
            [PermissionCodes.BillsOpen] = BillsOpen,
            [PermissionCodes.BillsClose] = BillsClose,
            [PermissionCodes.BillsCharge] = BillsCharge,
            [PermissionCodes.BillsDiscount] = BillsDiscount,
            [PermissionCodes.BillsManage] = BillsManage,
            [PermissionCodes.BillsApprove] = BillsApprove,
            [PermissionCodes.BillsUnapprove] = BillsUnapprove,
            [PermissionCodes.BillsVoid] = BillsVoid,
        };

    public static string For(string permissionCode)
        => ByPermissionCode.TryGetValue(permissionCode, out var policyName)
            ? policyName
            : throw new ArgumentException($"There is no policy for the permission '{permissionCode}'.", nameof(permissionCode));
}
