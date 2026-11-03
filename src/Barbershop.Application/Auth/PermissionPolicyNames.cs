using Barbershop.Domain.Users;

namespace Barbershop.Application.Auth;

/// <summary>
/// One authorization policy per permission code; each requires the matching permission claim. The claim reflects the
/// permissions at token issue time, so the policies of <see cref="PermissionCodes.Critical"/> also check the database
/// through <see cref="IUserPermissionVerifier"/> on every request.
/// </summary>
public static class PermissionPolicyNames
{
    public const string SalesRegister = "SalesRegisterPolicy";
    public const string ServiceRecordsRegister = "ServiceRecordsRegisterPolicy";
    public const string ServiceRecordsApprove = "ServiceRecordsApprovePolicy";
    public const string LoyaltySettingsManage = "LoyaltySettingsManagePolicy";
    public const string LoyaltyRewardsManage = "LoyaltyRewardsManagePolicy";
    public const string LoyaltyRedemptionsApprove = "LoyaltyRedemptionsApprovePolicy";
    public const string LoyaltyRedemptionsDeliver = "LoyaltyRedemptionsDeliverPolicy";
    public const string LoyaltyPointsAdjust = "LoyaltyPointsAdjustPolicy";
    public const string MembershipPaymentsConfirm = "MembershipPaymentsConfirmPolicy";
    public const string CustomerClaimsApprove = "CustomerClaimsApprovePolicy";

    private static readonly IReadOnlyDictionary<string, string> ByPermissionCode =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PermissionCodes.SalesRegister] = SalesRegister,
            [PermissionCodes.ServiceRecordsRegister] = ServiceRecordsRegister,
            [PermissionCodes.ServiceRecordsApprove] = ServiceRecordsApprove,
            [PermissionCodes.LoyaltySettingsManage] = LoyaltySettingsManage,
            [PermissionCodes.LoyaltyRewardsManage] = LoyaltyRewardsManage,
            [PermissionCodes.LoyaltyRedemptionsApprove] = LoyaltyRedemptionsApprove,
            [PermissionCodes.LoyaltyRedemptionsDeliver] = LoyaltyRedemptionsDeliver,
            [PermissionCodes.LoyaltyPointsAdjust] = LoyaltyPointsAdjust,
            [PermissionCodes.MembershipPaymentsConfirm] = MembershipPaymentsConfirm,
            [PermissionCodes.CustomerClaimsApprove] = CustomerClaimsApprove,
        };

    public static string For(string permissionCode)
        => ByPermissionCode.TryGetValue(permissionCode, out var policyName)
            ? policyName
            : throw new ArgumentException($"There is no policy for the permission '{permissionCode}'.", nameof(permissionCode));
}
