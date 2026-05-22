namespace Barbershop.Application.Availability;

public interface IStaffAvailabilityService
{
  Task<AvailabilitySummaryResponse> GetCurrentAsync(Guid currentUserId, CancellationToken cancellationToken = default);

  Task<IReadOnlyList<AvailabilityRuleResponse>> ReplaceRulesAsync(
      Guid currentUserId,
      IReadOnlyList<AvailabilityRuleRequest> rules,
      CancellationToken cancellationToken = default);

  Task<IReadOnlyList<UnavailablePeriodResponse>> GetUnavailablePeriodsAsync(Guid currentUserId, CancellationToken cancellationToken = default);

  Task<UnavailablePeriodResponse> CreateUnavailablePeriodAsync(
      Guid currentUserId,
      UnavailablePeriodCreateRequest request,
      CancellationToken cancellationToken = default);

  Task<UnavailablePeriodResponse> UpdateUnavailablePeriodAsync(
      Guid currentUserId,
      Guid unavailablePeriodId,
      UnavailablePeriodUpdateRequest request,
      CancellationToken cancellationToken = default);

  Task DeleteUnavailablePeriodAsync(Guid currentUserId, Guid unavailablePeriodId, CancellationToken cancellationToken = default);
}