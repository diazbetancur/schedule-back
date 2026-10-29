using Barbershop.Application.Finance.Admin;
using Barbershop.Domain.Finance;
using Barbershop.Domain.Staff;
using Barbershop.Infrastructure.Finance;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Tests.Features.Finance;

public sealed class ReportsIncomeKindTests : IDisposable
{
  private static readonly DateOnly Day = new(2026, 6, 10);

  private readonly AppDbContext _dbContext;
  private readonly IAdminReportsService _service;

  public ReportsIncomeKindTests()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options;

    _dbContext = new AppDbContext(options);
    _service = new ReportsQueryService(_dbContext, TimeProvider.System);
  }

  public void Dispose() => _dbContext.Dispose();

  [Fact]
  public async Task MembershipSales_CountAsBusinessIncome_ButNotAsServicesOrProfessionalIncome()
  {
    var alex = await AddStaffAsync("Alex");
    await AddServiceIncomeAsync(alex.Id, 20000, isPromo: false, businessPercentage: 40); // business 8000, professional 12000
    await AddServiceIncomeAsync(alex.Id, 10000, isPromo: true, businessPercentage: 40);  // business 4000, professional 6000
    await AddMembershipSaleAsync(250000);
    await AddMembershipSaleAsync(180000);

    var summary = await _service.GetSummaryAsync(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30));

    Assert.Equal(460000, summary.TotalIncome);
    Assert.Equal(2, summary.IncomeCount);                 // "# Cortes": services performed only
    Assert.Equal(10000, summary.PromoIncome);
    Assert.Equal(20000, summary.NormalIncome);
    Assert.Equal(430000, summary.MembershipSalesIncome);
    Assert.Equal(2, summary.MembershipSalesCount);
    Assert.Equal(summary.TotalIncome, summary.PromoIncome + summary.NormalIncome + summary.MembershipSalesIncome);

    Assert.Equal(12000 + 430000, summary.BusinessIncome);
    Assert.Equal(18000, summary.ProfessionalIncome);      // no commission on membership sales
    Assert.Equal(summary.TotalIncome, summary.BusinessIncome + summary.ProfessionalIncome);

    var row = Assert.Single(summary.ByProfessional);       // the sales never show up as a professional's work
    Assert.Equal(alex.Id, row.StaffProfileId);
    Assert.Equal(30000, row.IncomeTotal);
    Assert.Equal(2, row.IncomeCount);
    Assert.Equal(12000, row.BusinessTotal);
    Assert.Equal(18000, row.ProfessionalTotal);

    Assert.Equal(460000, summary.Trend.Single(point => point.From == Day).Income);
  }

  [Fact]
  public async Task PeriodWithOnlyServiceIncome_ReportsZeroMembershipSales()
  {
    var alex = await AddStaffAsync("Alex");
    await AddServiceIncomeAsync(alex.Id, 20000, isPromo: false, businessPercentage: 40);

    var summary = await _service.GetSummaryAsync(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30));

    Assert.Equal(0, summary.MembershipSalesIncome);
    Assert.Equal(0, summary.MembershipSalesCount);
    Assert.Equal(summary.TotalIncome, summary.NormalIncome + summary.PromoIncome);
  }

  private async Task<StaffProfile> AddStaffAsync(string displayName)
  {
    var staff = new StaffProfile(Guid.NewGuid(), displayName, 30, DateTime.UtcNow);
    _dbContext.StaffProfiles.Add(staff);
    await _dbContext.SaveChangesAsync();
    return staff;
  }

  private async Task AddServiceIncomeAsync(Guid staffProfileId, int amount, bool isPromo, int businessPercentage)
  {
    _dbContext.IncomeEntries.Add(new IncomeEntry(
        Guid.NewGuid(), "Corte", 25000, staffProfileId, amount, isPromo, Day, Guid.NewGuid(), DateTime.UtcNow, businessPercentage));
    await _dbContext.SaveChangesAsync();
  }

  private async Task AddMembershipSaleAsync(int amount)
  {
    _dbContext.IncomeEntries.Add(IncomeEntry.CreateMembershipSale(amount, Day, Guid.NewGuid(), DateTime.UtcNow));
    await _dbContext.SaveChangesAsync();
  }
}
