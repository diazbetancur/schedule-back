using Barbershop.Domain.Users;

namespace Barbershop.Tests.Infrastructure;

/// <summary>The default grants of the seed since ADR-0020 stage 6.</summary>
internal static class PermissionDefaults
{
  /// <summary>Admin gets every permission of the catalog except the retired registration of services performed.</summary>
  public static readonly string[] Admin =
      PermissionCodes.All.Where(code => code != PermissionCodes.ServiceRecordsRegister).ToArray();

  /// <summary>Staff opens, closes and charges bills (Q16).</summary>
  public static readonly string[] Staff = [PermissionCodes.BillsCharge, PermissionCodes.BillsClose, PermissionCodes.BillsOpen];
}
