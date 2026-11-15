using Barbershop.Domain.Finance;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Tests.Infrastructure;

/// <summary>
/// ADR-0020 Q18: new service income is no longer created by hand (the bills post it). Tests that need a manual service
/// income stored before seed it here, exactly as the manual creation stored it.
/// </summary>
internal static class ManualIncomeSeed
{
  public static async Task<IncomeEntry> ServiceIncomeAsync(
      AppDbContext db, Guid serviceId, Guid staffProfileId, int amount, DateOnly occurredOn, Guid createdByUserId, bool isPromo = false)
  {
    var service = await db.Services.AsNoTracking().SingleAsync(candidate => candidate.Id == serviceId);
    var entry = new IncomeEntry(
        service.Id, service.Name, service.BasePrice, staffProfileId, amount, isPromo, occurredOn, createdByUserId, DateTime.UtcNow,
        service.BusinessPercentage);
    db.IncomeEntries.Add(entry);
    await db.SaveChangesAsync();
    return entry;
  }
}
