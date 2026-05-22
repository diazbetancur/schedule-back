using Barbershop.Application.Availability;
using Barbershop.Application.Common.Exceptions;
using Barbershop.Domain.Scheduling;
using Barbershop.Domain.Staff;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Infrastructure.Availability;

internal sealed class AvailabilityManagementService : IStaffAvailabilityService, IAdminStaffAvailabilityService
{
  private readonly AppDbContext _dbContext;
  private readonly TimeProvider _timeProvider;

  public AvailabilityManagementService(AppDbContext dbContext, TimeProvider timeProvider)
  {
    _dbContext = dbContext;
    _timeProvider = timeProvider;
  }

  public async Task<AvailabilitySummaryResponse> GetCurrentAsync(Guid currentUserId, CancellationToken cancellationToken = default)
  {
    var staffProfile = await LoadStaffProfileByUserIdAsync(currentUserId, cancellationToken);
    return await BuildSummaryAsync(staffProfile, cancellationToken);
  }

  public async Task<AvailabilitySummaryResponse> GetAsync(Guid staffProfileId, CancellationToken cancellationToken = default)
  {
    var staffProfile = await LoadStaffProfileAsync(staffProfileId, cancellationToken);
    return await BuildSummaryAsync(staffProfile, cancellationToken);
  }

  async Task<IReadOnlyList<AvailabilityRuleResponse>> IStaffAvailabilityService.ReplaceRulesAsync(
      Guid currentUserId,
      IReadOnlyList<AvailabilityRuleRequest> rules,
      CancellationToken cancellationToken)
  {
    var staffProfile = await LoadStaffProfileByUserIdAsync(currentUserId, cancellationToken);
    return await ReplaceRulesCoreAsync(staffProfile.Id, rules, cancellationToken);
  }

  async Task<IReadOnlyList<AvailabilityRuleResponse>> IAdminStaffAvailabilityService.ReplaceRulesAsync(
      Guid staffProfileId,
      IReadOnlyList<AvailabilityRuleRequest> rules,
      CancellationToken cancellationToken)
  {
    await EnsureStaffProfileExistsAsync(staffProfileId, cancellationToken);
    return await ReplaceRulesCoreAsync(staffProfileId, rules, cancellationToken);
  }

  async Task<IReadOnlyList<UnavailablePeriodResponse>> IStaffAvailabilityService.GetUnavailablePeriodsAsync(Guid currentUserId, CancellationToken cancellationToken)
  {
    var staffProfile = await LoadStaffProfileByUserIdAsync(currentUserId, cancellationToken);
    return await LoadUnavailablePeriodsAsync(staffProfile.Id, cancellationToken);
  }

  async Task<IReadOnlyList<UnavailablePeriodResponse>> IAdminStaffAvailabilityService.GetUnavailablePeriodsAsync(Guid staffProfileId, CancellationToken cancellationToken)
  {
    await EnsureStaffProfileExistsAsync(staffProfileId, cancellationToken);
    return await LoadUnavailablePeriodsAsync(staffProfileId, cancellationToken);
  }

  async Task<UnavailablePeriodResponse> IStaffAvailabilityService.CreateUnavailablePeriodAsync(
      Guid currentUserId,
      UnavailablePeriodCreateRequest request,
      CancellationToken cancellationToken)
  {
    var staffProfile = await LoadStaffProfileByUserIdAsync(currentUserId, cancellationToken);
    return await CreateUnavailablePeriodCoreAsync(staffProfile.Id, request, cancellationToken);
  }

  async Task<UnavailablePeriodResponse> IAdminStaffAvailabilityService.CreateUnavailablePeriodAsync(
      Guid staffProfileId,
      UnavailablePeriodCreateRequest request,
      CancellationToken cancellationToken)
  {
    await EnsureStaffProfileExistsAsync(staffProfileId, cancellationToken);
    return await CreateUnavailablePeriodCoreAsync(staffProfileId, request, cancellationToken);
  }

  async Task<UnavailablePeriodResponse> IStaffAvailabilityService.UpdateUnavailablePeriodAsync(
      Guid currentUserId,
      Guid unavailablePeriodId,
      UnavailablePeriodUpdateRequest request,
      CancellationToken cancellationToken)
  {
    var staffProfile = await LoadStaffProfileByUserIdAsync(currentUserId, cancellationToken);
    return await UpdateUnavailablePeriodCoreAsync(staffProfile.Id, unavailablePeriodId, request, cancellationToken);
  }

  async Task<UnavailablePeriodResponse> IAdminStaffAvailabilityService.UpdateUnavailablePeriodAsync(
      Guid staffProfileId,
      Guid unavailablePeriodId,
      UnavailablePeriodUpdateRequest request,
      CancellationToken cancellationToken)
  {
    await EnsureStaffProfileExistsAsync(staffProfileId, cancellationToken);
    return await UpdateUnavailablePeriodCoreAsync(staffProfileId, unavailablePeriodId, request, cancellationToken);
  }

  async Task IStaffAvailabilityService.DeleteUnavailablePeriodAsync(Guid currentUserId, Guid unavailablePeriodId, CancellationToken cancellationToken)
  {
    var staffProfile = await LoadStaffProfileByUserIdAsync(currentUserId, cancellationToken);
    await DeleteUnavailablePeriodCoreAsync(staffProfile.Id, unavailablePeriodId, cancellationToken);
  }

  async Task IAdminStaffAvailabilityService.DeleteUnavailablePeriodAsync(Guid staffProfileId, Guid unavailablePeriodId, CancellationToken cancellationToken)
  {
    await EnsureStaffProfileExistsAsync(staffProfileId, cancellationToken);
    await DeleteUnavailablePeriodCoreAsync(staffProfileId, unavailablePeriodId, cancellationToken);
  }

  private async Task<AvailabilitySummaryResponse> BuildSummaryAsync(StaffProfile staffProfile, CancellationToken cancellationToken)
  {
    var rules = await _dbContext.StaffAvailabilityRules
        .Where(rule => rule.StaffProfileId == staffProfile.Id)
        .OrderBy(rule => rule.DayOfWeek)
        .ThenBy(rule => rule.StartTime)
        .ToListAsync(cancellationToken);

    var unavailablePeriods = await _dbContext.StaffUnavailablePeriods
        .Where(period => period.StaffProfileId == staffProfile.Id)
        .OrderBy(period => period.StartsAt)
        .ToListAsync(cancellationToken);

    return new AvailabilitySummaryResponse(
        staffProfile.Id,
        staffProfile.DefaultAppointmentDurationMinutes,
        rules.Select(Map).ToArray(),
        unavailablePeriods.Select(Map).ToArray());
  }

  private async Task<IReadOnlyList<AvailabilityRuleResponse>> ReplaceRulesCoreAsync(
      Guid staffProfileId,
      IReadOnlyList<AvailabilityRuleRequest> rules,
      CancellationToken cancellationToken)
  {
    ValidateRules(rules);

    var existingRules = await _dbContext.StaffAvailabilityRules
        .Where(rule => rule.StaffProfileId == staffProfileId)
        .ToListAsync(cancellationToken);

    _dbContext.StaffAvailabilityRules.RemoveRange(existingRules);

    var updatedRules = rules
        .Select(rule => new StaffAvailabilityRule(
            staffProfileId,
            (DayOfWeek)rule.DayOfWeek,
            rule.StartTime,
            rule.EndTime,
            rule.IsActive))
        .ToArray();

    _dbContext.StaffAvailabilityRules.AddRange(updatedRules);
    await _dbContext.SaveChangesAsync(cancellationToken);

    return updatedRules
        .OrderBy(rule => rule.DayOfWeek)
        .ThenBy(rule => rule.StartTime)
        .Select(Map)
        .ToArray();
  }

  private async Task<IReadOnlyList<UnavailablePeriodResponse>> LoadUnavailablePeriodsAsync(Guid staffProfileId, CancellationToken cancellationToken)
  {
    var periods = await _dbContext.StaffUnavailablePeriods
        .Where(period => period.StaffProfileId == staffProfileId)
        .OrderBy(period => period.StartsAt)
        .ToListAsync(cancellationToken);

    return periods.Select(Map).ToArray();
  }

  private async Task<UnavailablePeriodResponse> CreateUnavailablePeriodCoreAsync(
      Guid staffProfileId,
      UnavailablePeriodCreateRequest request,
      CancellationToken cancellationToken)
  {
    ValidateUnavailablePeriod(request.StartsAtUtc, request.EndsAtUtc, request.Reason);

    var unavailablePeriod = new StaffUnavailablePeriod(
        staffProfileId,
        request.StartsAtUtc,
        request.EndsAtUtc,
        _timeProvider.GetUtcNow().UtcDateTime,
        request.Reason);

    _dbContext.StaffUnavailablePeriods.Add(unavailablePeriod);
    await _dbContext.SaveChangesAsync(cancellationToken);

    return Map(unavailablePeriod);
  }

  private async Task<UnavailablePeriodResponse> UpdateUnavailablePeriodCoreAsync(
      Guid staffProfileId,
      Guid unavailablePeriodId,
      UnavailablePeriodUpdateRequest request,
      CancellationToken cancellationToken)
  {
    ValidateUnavailablePeriod(request.StartsAtUtc, request.EndsAtUtc, request.Reason);

    var unavailablePeriod = await _dbContext.StaffUnavailablePeriods
        .SingleOrDefaultAsync(
            period => period.StaffProfileId == staffProfileId && period.Id == unavailablePeriodId,
            cancellationToken)
        ?? throw new KeyNotFoundException("The unavailable period was not found.");

    unavailablePeriod.Update(request.StartsAtUtc, request.EndsAtUtc, request.Reason);
    await _dbContext.SaveChangesAsync(cancellationToken);

    return Map(unavailablePeriod);
  }

  private async Task DeleteUnavailablePeriodCoreAsync(Guid staffProfileId, Guid unavailablePeriodId, CancellationToken cancellationToken)
  {
    var unavailablePeriod = await _dbContext.StaffUnavailablePeriods
        .SingleOrDefaultAsync(
            period => period.StaffProfileId == staffProfileId && period.Id == unavailablePeriodId,
            cancellationToken)
        ?? throw new KeyNotFoundException("The unavailable period was not found.");

    _dbContext.StaffUnavailablePeriods.Remove(unavailablePeriod);
    await _dbContext.SaveChangesAsync(cancellationToken);
  }

  private async Task<StaffProfile> LoadStaffProfileAsync(Guid staffProfileId, CancellationToken cancellationToken)
  {
    return await _dbContext.StaffProfiles
        .SingleOrDefaultAsync(staffProfile => staffProfile.Id == staffProfileId, cancellationToken)
        ?? throw new KeyNotFoundException("The staff profile was not found.");
  }

  private async Task<StaffProfile> LoadStaffProfileByUserIdAsync(Guid userId, CancellationToken cancellationToken)
  {
    return await _dbContext.StaffProfiles
        .SingleOrDefaultAsync(staffProfile => staffProfile.UserId == userId, cancellationToken)
        ?? throw new KeyNotFoundException("The staff profile was not found for the current user.");
  }

  private async Task EnsureStaffProfileExistsAsync(Guid staffProfileId, CancellationToken cancellationToken)
  {
    _ = await LoadStaffProfileAsync(staffProfileId, cancellationToken);
  }

  private static AvailabilityRuleResponse Map(StaffAvailabilityRule rule)
      => new(rule.Id, (int)rule.DayOfWeek, rule.StartTime, rule.EndTime, rule.IsActive);

  private static UnavailablePeriodResponse Map(StaffUnavailablePeriod unavailablePeriod)
      => new(
          unavailablePeriod.Id,
          unavailablePeriod.StartsAt,
          unavailablePeriod.EndsAt,
          unavailablePeriod.Reason,
          unavailablePeriod.CreatedAt);

  private static void ValidateRules(IReadOnlyList<AvailabilityRuleRequest> rules)
  {
    var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
    var activeRules = new List<AvailabilityRuleRequest>();

    for (var index = 0; index < rules.Count; index++)
    {
      var rule = rules[index];
      var dayOfWeek = (DayOfWeek)rule.DayOfWeek;

      if (!Enum.IsDefined(dayOfWeek))
      {
        errors[$"rules[{index}].dayOfWeek"] = ["DayOfWeek must be between 0 and 6."];
      }

      if (rule.EndTime <= rule.StartTime)
      {
        errors[$"rules[{index}].endTime"] = ["EndTime must be after StartTime."];
      }

      if (rule.IsActive && Enum.IsDefined(dayOfWeek) && rule.EndTime > rule.StartTime)
      {
        activeRules.Add(rule);
      }
    }

    var overlapsExist = activeRules
        .GroupBy(rule => rule.DayOfWeek)
        .Any(group => group
            .OrderBy(rule => rule.StartTime)
            .Zip(group.OrderBy(rule => rule.StartTime).Skip(1), (current, next) => current.EndTime > next.StartTime)
            .Any(overlaps => overlaps));

    if (overlapsExist)
    {
      errors["rules"] = ["Active availability rules cannot overlap on the same day."];
    }

    ThrowIfAnyErrors(errors);
  }

  private static void ValidateUnavailablePeriod(DateTime startsAtUtc, DateTime endsAtUtc, string? reason)
  {
    var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

    if (startsAtUtc.Kind != DateTimeKind.Utc)
    {
      errors["startsAtUtc"] = ["StartsAtUtc must be provided in UTC."];
    }

    if (endsAtUtc.Kind != DateTimeKind.Utc)
    {
      errors["endsAtUtc"] = ["EndsAtUtc must be provided in UTC."];
    }

    if (endsAtUtc <= startsAtUtc)
    {
      errors["endsAtUtc"] = ["EndsAtUtc must be after StartsAtUtc."];
    }

    if (!string.IsNullOrWhiteSpace(reason) && reason.Trim().Length > 500)
    {
      errors["reason"] = ["Reason must be 500 characters or fewer."];
    }

    ThrowIfAnyErrors(errors);
  }

  private static void ThrowIfAnyErrors(Dictionary<string, string[]> errors)
  {
    if (errors.Count > 0)
    {
      throw new ValidationProblemException(errors);
    }
  }
}