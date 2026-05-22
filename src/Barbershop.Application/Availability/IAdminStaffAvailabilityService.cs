namespace Barbershop.Application.Availability;

public interface IAdminStaffAvailabilityService
{
  Task<AvailabilitySummaryResponse> GetAsync(Guid staffProfileId, CancellationToken cancellationToken = default);

  Task<IReadOnlyList<AvailabilityRuleResponse>> ReplaceRulesAsync(
      Guid staffProfileId,
      IReadOnlyList<AvailabilityRuleRequest> rules,
      CancellationToken cancellationToken = default);

  Task<IReadOnlyList<UnavailablePeriodResponse>> GetUnavailablePeriodsAsync(Guid staffProfileId, CancellationToken cancellationToken = default);

  Task<UnavailablePeriodResponse> CreateUnavailablePeriodAsync(
      Guid staffProfileId,
      UnavailablePeriodCreateRequest request,
      CancellationToken cancellationToken = default);

  Task<UnavailablePeriodResponse> UpdateUnavailablePeriodAsync(
      Guid staffProfileId,
      Guid unavailablePeriodId,
      UnavailablePeriodUpdateRequest request,
      CancellationToken cancellationToken = default);

  Task DeleteUnavailablePeriodAsync(Guid staffProfileId, Guid unavailablePeriodId, CancellationToken cancellationToken = default);
}