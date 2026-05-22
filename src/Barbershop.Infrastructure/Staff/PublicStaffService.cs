using Barbershop.Application.PublicContent;
using Barbershop.Application.Storage;
using Barbershop.Domain.Appointments;
using Barbershop.Domain.Staff;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Infrastructure.Staff;

internal sealed class PublicStaffService : IPublicStaffService
{
  private readonly AppDbContext _dbContext;
  private readonly IFileStorageService _fileStorageService;

  public PublicStaffService(AppDbContext dbContext, IFileStorageService fileStorageService)
  {
    _dbContext = dbContext;
    _fileStorageService = fileStorageService;
  }

  public async Task<IReadOnlyList<PublicStaffListItemResponse>> GetPublicStaffAsync(
      string? search = null,
      CancellationToken cancellationToken = default)
  {
    var normalizedSearch = string.IsNullOrWhiteSpace(search)
        ? null
        : search.Trim().ToUpperInvariant();

    var query = _dbContext.StaffProfiles
        .AsNoTracking()
        .Include(staffProfile => staffProfile.User)
        .Where(staffProfile => staffProfile.IsActive && staffProfile.User.IsActive);

    if (normalizedSearch is not null)
    {
      query = query.Where(staffProfile =>
          staffProfile.DisplayName.ToUpper().Contains(normalizedSearch)
          || (staffProfile.Bio != null && staffProfile.Bio.ToUpper().Contains(normalizedSearch)));
    }

    var staffProfiles = await query
        .OrderBy(staffProfile => staffProfile.DisplayName)
        .ToListAsync(cancellationToken);

    if (staffProfiles.Count == 0)
    {
      return [];
    }

    var staffProfileIds = staffProfiles
        .Select(staffProfile => staffProfile.Id)
        .ToArray();

    var mediaUrls = await LoadMediaUrlsAsync(
        staffProfiles.Select(staffProfile => staffProfile.PhotoMediaAssetId),
        cancellationToken);

    var servicesByStaffProfile = await LoadActiveServicesByStaffProfileAsync(staffProfileIds, cancellationToken);
    var reviewSummariesByStaffProfile = await LoadReviewSummariesAsync(staffProfileIds, cancellationToken);

    return staffProfiles
        .Select(staffProfile =>
        {
          var reviewSummary = GetReviewSummary(staffProfile.Id, reviewSummariesByStaffProfile);
          servicesByStaffProfile.TryGetValue(staffProfile.Id, out var services);

          return new PublicStaffListItemResponse(
                  staffProfile.Id,
                  staffProfile.DisplayName,
                  staffProfile.Bio,
                  staffProfile.PhoneNumber ?? staffProfile.User.PhoneNumber,
                  staffProfile.PhotoMediaAssetId,
                  ResolveMediaUrl(staffProfile.PhotoMediaAssetId, mediaUrls),
                  staffProfile.DefaultAppointmentDurationMinutes,
                  reviewSummary.AverageRating,
                  reviewSummary.ReviewCount,
                  services ?? []);
        })
        .ToArray();
  }

  public async Task<PublicStaffProfileResponse> GetPublicStaffByIdAsync(
      Guid staffProfileId,
      CancellationToken cancellationToken = default)
  {
    var staffProfile = await LoadActiveStaffProfileAsync(staffProfileId, cancellationToken);

    var mediaUrls = await LoadMediaUrlsAsync(
        [staffProfile.PhotoMediaAssetId, staffProfile.TipsQrMediaAssetId],
        cancellationToken);

    var servicesByStaffProfile = await LoadActiveServicesByStaffProfileAsync([staffProfile.Id], cancellationToken);
    var reviewSummariesByStaffProfile = await LoadReviewSummariesAsync([staffProfile.Id], cancellationToken);
    var reviewSummary = GetReviewSummary(staffProfile.Id, reviewSummariesByStaffProfile);

    servicesByStaffProfile.TryGetValue(staffProfile.Id, out var services);

    return new PublicStaffProfileResponse(
        staffProfile.Id,
        staffProfile.DisplayName,
        staffProfile.Bio,
        staffProfile.PhoneNumber ?? staffProfile.User.PhoneNumber,
        staffProfile.PhotoMediaAssetId,
        ResolveMediaUrl(staffProfile.PhotoMediaAssetId, mediaUrls),
        staffProfile.TipsQrMediaAssetId,
        ResolveMediaUrl(staffProfile.TipsQrMediaAssetId, mediaUrls),
        staffProfile.DefaultAppointmentDurationMinutes,
        reviewSummary.AverageRating,
        reviewSummary.ReviewCount,
        services ?? [],
        staffProfile.InstagramUrl,
        staffProfile.FacebookUrl,
        staffProfile.TikTokUrl,
        staffProfile.YoutubeUrl,
        staffProfile.XUrl);
  }

  private async Task<StaffProfile> LoadActiveStaffProfileAsync(Guid staffProfileId, CancellationToken cancellationToken)
  {
    var staffProfile = await _dbContext.StaffProfiles
        .AsNoTracking()
        .Include(profile => profile.User)
        .SingleOrDefaultAsync(profile => profile.Id == staffProfileId, cancellationToken)
        ?? throw new KeyNotFoundException("The staff profile was not found.");

    if (!staffProfile.IsActive || !staffProfile.User.IsActive)
    {
      throw new KeyNotFoundException("The staff profile was not found.");
    }

    return staffProfile;
  }

  private async Task<Dictionary<Guid, string?>> LoadMediaUrlsAsync(
      IEnumerable<Guid?> mediaAssetIds,
      CancellationToken cancellationToken)
  {
    var ids = mediaAssetIds
        .Where(mediaAssetId => mediaAssetId.HasValue && mediaAssetId.Value != Guid.Empty)
        .Select(mediaAssetId => mediaAssetId!.Value)
        .Distinct()
        .ToArray();

    if (ids.Length == 0)
    {
      return [];
    }

    var assets = await _dbContext.MediaAssets
        .AsNoTracking()
        .Where(mediaAsset => ids.Contains(mediaAsset.Id))
        .Select(mediaAsset => new { mediaAsset.Id, mediaAsset.StorageKey, mediaAsset.PublicUrl })
        .ToListAsync(cancellationToken);

    return assets.ToDictionary(
        asset => asset.Id,
        asset => asset.PublicUrl ?? _fileStorageService.GetPublicUrl(asset.StorageKey));
  }

  private async Task<Dictionary<Guid, IReadOnlyList<PublicStaffServiceResponse>>> LoadActiveServicesByStaffProfileAsync(
      IReadOnlyCollection<Guid> staffProfileIds,
      CancellationToken cancellationToken)
  {
    if (staffProfileIds.Count == 0)
    {
      return [];
    }

    var services = await _dbContext.StaffServices
        .AsNoTracking()
        .Where(service => staffProfileIds.Contains(service.StaffProfileId) && service.IsActive)
        .OrderBy(service => service.Name)
        .ToListAsync(cancellationToken);

    return services
        .GroupBy(service => service.StaffProfileId)
        .ToDictionary(
            group => group.Key,
            group => (IReadOnlyList<PublicStaffServiceResponse>)group.Select(MapService).ToArray());
  }

  private async Task<Dictionary<Guid, ReviewSummary>> LoadReviewSummariesAsync(
      IReadOnlyCollection<Guid> staffProfileIds,
      CancellationToken cancellationToken)
  {
    if (staffProfileIds.Count == 0)
    {
      return [];
    }

    var summaries = await _dbContext.Reviews
        .AsNoTracking()
        .Where(review =>
            staffProfileIds.Contains(review.Appointment.StaffProfileId)
            && review.Appointment.Status == AppointmentStatus.Completed)
        .GroupBy(review => review.Appointment.StaffProfileId)
        .Select(group => new
        {
          StaffProfileId = group.Key,
          ReviewCount = group.Count(),
          AverageRating = group.Average(review => (decimal)review.Stars)
        })
        .ToListAsync(cancellationToken);

    return summaries.ToDictionary(
        summary => summary.StaffProfileId,
        summary => new ReviewSummary(
            summary.ReviewCount,
            decimal.Round(summary.AverageRating, 2, MidpointRounding.AwayFromZero)));
  }

  private static PublicStaffServiceResponse MapService(StaffService service)
      => new(
          service.Id,
          service.Name,
          service.Description,
          service.DurationMinutes,
          service.Price,
          service.IsActive);

  private static string? ResolveMediaUrl(Guid? mediaAssetId, IReadOnlyDictionary<Guid, string?> mediaUrls)
  {
    if (!mediaAssetId.HasValue)
    {
      return null;
    }

    return mediaUrls.TryGetValue(mediaAssetId.Value, out var mediaUrl)
        ? mediaUrl
        : null;
  }

  private static ReviewSummary GetReviewSummary(Guid staffProfileId, IReadOnlyDictionary<Guid, ReviewSummary> reviewSummaries)
  {
    return reviewSummaries.TryGetValue(staffProfileId, out var reviewSummary)
        ? reviewSummary
        : ReviewSummary.Empty;
  }

  private readonly record struct ReviewSummary(int ReviewCount, decimal AverageRating)
  {
    public static ReviewSummary Empty => new(0, 0m);
  }
}
