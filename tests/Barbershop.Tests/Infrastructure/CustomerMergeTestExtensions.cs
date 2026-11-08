using Barbershop.Application.Customers.Admin;

namespace Barbershop.Tests.Infrastructure;

/// <summary>A merge as the admin does it: preview first, then confirm with the preview's token (ADR-0013 D7).</summary>
internal static class CustomerMergeTestExtensions
{
  public static async Task<CustomerDetailView> MergeWithPreviewAsync(
      this IAdminCustomersService service, Guid currentUserId, Guid targetCustomerId, Guid sourceCustomerId)
  {
    var preview = await service.GetMergePreviewAsync(targetCustomerId, sourceCustomerId);
    return await service.MergeAsync(currentUserId, targetCustomerId, new CustomerMergeRequest(sourceCustomerId, preview.PreviewToken));
  }
}
