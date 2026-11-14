namespace Barbershop.Application.Services.Admin;

// ADR-0016 B1: masters of service categories and general services. The settlement rates keep their own contracts
// (ServiceContracts) and point to a general service.

public sealed record ServiceCategoryView(
    Guid Id,
    string Name,
    int SortOrder,
    string PublicCode,
    bool IsActive,
    int GeneralServiceCount,
    int ActiveGeneralServiceCount,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

public sealed record ServiceCategoryCreateRequest(string Name, int SortOrder = 0);

/// <param name="SortOrder">Omitted: the current position is kept.</param>
public sealed record ServiceCategoryUpdateRequest(string Name, int? SortOrder = null);

public sealed record ServiceCategoryStatusUpdateRequest(bool IsActive);

public sealed record GeneralServiceView(
    Guid Id,
    Guid CategoryId,
    string CategoryName,
    bool CategoryIsActive,
    string Name,
    string? Description,
    int SortOrder,
    string PublicCode,
    bool IsActive,
    int RateCount,
    int ActiveRateCount,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

public sealed record GeneralServiceCreateRequest(Guid CategoryId, string Name, string? Description = null, int SortOrder = 0);

/// <param name="SortOrder">Omitted: the current position is kept.</param>
public sealed record GeneralServiceUpdateRequest(Guid CategoryId, string Name, string? Description = null, int? SortOrder = null);

public sealed record GeneralServiceStatusUpdateRequest(bool IsActive);

public interface IAdminServiceCatalogService
{
    Task<IReadOnlyList<ServiceCategoryView>> GetCategoriesAsync(CancellationToken cancellationToken = default);

    Task<ServiceCategoryView> GetCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default);

    Task<ServiceCategoryView> CreateCategoryAsync(ServiceCategoryCreateRequest request, CancellationToken cancellationToken = default);

    Task<ServiceCategoryView> UpdateCategoryAsync(Guid categoryId, ServiceCategoryUpdateRequest request, CancellationToken cancellationToken = default);

    Task<ServiceCategoryView> UpdateCategoryStatusAsync(Guid categoryId, ServiceCategoryStatusUpdateRequest request, CancellationToken cancellationToken = default);

    Task DeleteCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GeneralServiceView>> GetGeneralServicesAsync(Guid? categoryId, CancellationToken cancellationToken = default);

    Task<GeneralServiceView> GetGeneralServiceAsync(Guid generalServiceId, CancellationToken cancellationToken = default);

    Task<GeneralServiceView> CreateGeneralServiceAsync(GeneralServiceCreateRequest request, CancellationToken cancellationToken = default);

    Task<GeneralServiceView> UpdateGeneralServiceAsync(Guid generalServiceId, GeneralServiceUpdateRequest request, CancellationToken cancellationToken = default);

    Task<GeneralServiceView> UpdateGeneralServiceStatusAsync(Guid generalServiceId, GeneralServiceStatusUpdateRequest request, CancellationToken cancellationToken = default);

    Task DeleteGeneralServiceAsync(Guid generalServiceId, CancellationToken cancellationToken = default);
}
