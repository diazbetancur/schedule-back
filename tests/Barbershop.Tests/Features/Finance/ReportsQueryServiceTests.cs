using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Finance.Admin;
using Barbershop.Domain.Finance;
using Barbershop.Domain.Staff;
using Barbershop.Infrastructure.Finance;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Tests.Features.Finance;

public sealed class ReportsQueryServiceTests : IDisposable
{
  private readonly AppDbContext _dbContext;
  private readonly IAdminReportsService _service;

  public ReportsQueryServiceTests()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options;

    _dbContext = new AppDbContext(options);
    _service = new ReportsQueryService(_dbContext, TimeProvider.System);
  }

  public void Dispose() => _dbContext.Dispose();

  [Fact]
  public async Task GetSummaryAsync_AggregatesTotalsNetAndCounts()
  {
    var alex = await AddStaffAsync("Alex");
    var day = new DateOnly(2026, 6, 10);

    await AddIncomeAsync(alex.Id, 25000, isPromo: false, day);
    await AddIncomeAsync(alex.Id, 15000, isPromo: true, day);
    await AddExpenseAsync("Arriendo", 30000, day);

    var summary = await _service.GetSummaryAsync(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30));

    Assert.Equal(40000, summary.TotalIncome);
    Assert.Equal(30000, summary.TotalExpenses);
    Assert.Equal(10000, summary.NetProfit);
    Assert.Equal(2, summary.IncomeCount);
    Assert.Equal(15000, summary.PromoIncome);
    Assert.Equal(25000, summary.NormalIncome);
  }

  [Fact]
  public async Task GetSummaryAsync_GroupsIncomeByProfessional()
  {
    var alex = await AddStaffAsync("Alex");
    var bianca = await AddStaffAsync("Bianca");
    var day = new DateOnly(2026, 6, 10);

    await AddIncomeAsync(alex.Id, 20000, false, day);
    await AddIncomeAsync(alex.Id, 10000, false, day);
    await AddIncomeAsync(bianca.Id, 50000, false, day);

    var summary = await _service.GetSummaryAsync(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30));

    Assert.Equal(2, summary.ByProfessional.Count);
    Assert.Equal(bianca.Id, summary.ByProfessional[0].StaffProfileId); // ordered desc by total
    Assert.Equal(50000, summary.ByProfessional[0].IncomeTotal);
    Assert.Equal(1, summary.ByProfessional[0].IncomeCount);
    var alexRow = summary.ByProfessional.Single(p => p.StaffProfileId == alex.Id);
    Assert.Equal("Alex", alexRow.DisplayName);
    Assert.Equal(30000, alexRow.IncomeTotal);
    Assert.Equal(2, alexRow.IncomeCount);
  }

  [Fact]
  public async Task GetSummaryAsync_GroupsExpensesByConcept()
  {
    var day = new DateOnly(2026, 6, 10);
    await AddExpenseAsync("Arriendo", 30000, day);
    await AddExpenseAsync("Insumos", 5000, day);
    await AddExpenseAsync("Insumos", 7000, day);

    var summary = await _service.GetSummaryAsync(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30));

    Assert.Equal(2, summary.ByExpenseConcept.Count);
    Assert.Equal("Arriendo", summary.ByExpenseConcept[0].Name); // 30000 desc first
    Assert.Equal(12000, summary.ByExpenseConcept.Single(c => c.Name == "Insumos").Total);
  }

  [Fact]
  public async Task GetSummaryAsync_ExcludesOutOfRangeAndDeleted()
  {
    var alex = await AddStaffAsync("Alex");
    await AddIncomeAsync(alex.Id, 10000, false, new DateOnly(2026, 6, 10));
    await AddIncomeAsync(alex.Id, 99999, false, new DateOnly(2026, 5, 31)); // before range
    var deleted = await AddIncomeAsync(alex.Id, 88888, false, new DateOnly(2026, 6, 15));
    deleted.MarkDeleted(DateTime.UtcNow);
    await _dbContext.SaveChangesAsync();

    var summary = await _service.GetSummaryAsync(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30));

    Assert.Equal(10000, summary.TotalIncome);
    Assert.Equal(1, summary.IncomeCount);
  }

  [Fact]
  public async Task GetSummaryAsync_ShortRange_UsesDailyBuckets()
  {
    var alex = await AddStaffAsync("Alex");
    await AddIncomeAsync(alex.Id, 10000, false, new DateOnly(2026, 6, 1));
    await AddIncomeAsync(alex.Id, 20000, false, new DateOnly(2026, 6, 3));

    var summary = await _service.GetSummaryAsync(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 3));

    Assert.Equal(3, summary.Trend.Count); // one per day, zero-filled
    Assert.Equal("2026-06-01", summary.Trend[0].Bucket);
    Assert.Equal(10000, summary.Trend[0].Income);
    Assert.Equal(0, summary.Trend[1].Income);
    Assert.Equal(20000, summary.Trend[2].Income);
  }

  [Fact]
  public async Task GetSummaryAsync_LongRange_UsesMonthlyBuckets()
  {
    var alex = await AddStaffAsync("Alex");
    await AddIncomeAsync(alex.Id, 10000, false, new DateOnly(2026, 1, 15));
    await AddExpenseAsync("Arriendo", 4000, new DateOnly(2026, 3, 2));

    var summary = await _service.GetSummaryAsync(new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31));

    Assert.Equal(3, summary.Trend.Count); // Jan, Feb, Mar
    Assert.Equal("2026-01", summary.Trend[0].Bucket);
    Assert.Equal(10000, summary.Trend[0].Income);
    Assert.Equal(0, summary.Trend[1].Income);
    Assert.Equal(4000, summary.Trend[2].Expenses);
  }

  [Fact]
  public async Task GetSummaryAsync_RejectsFromAfterTo()
  {
    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.GetSummaryAsync(new DateOnly(2026, 6, 30), new DateOnly(2026, 6, 1)));

    Assert.Contains("range", exception.Errors.Keys);
  }

  [Fact]
  public async Task GetSummaryAsync_SplitsBusinessAndProfessionalTotals()
  {
    var alex = await AddStaffAsync("Alex");
    var bianca = await AddStaffAsync("Bianca");
    var day = new DateOnly(2026, 6, 10);

    await AddIncomeAsync(alex.Id, 20000, false, day, businessPercentage: 40); // business 8000, professional 12000
    await AddIncomeAsync(bianca.Id, 10000, false, day, businessPercentage: 10); // business 1000, professional 9000

    var summary = await _service.GetSummaryAsync(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30));

    Assert.Equal(9000, summary.BusinessIncome);
    Assert.Equal(21000, summary.ProfessionalIncome);
    Assert.Equal(summary.TotalIncome, summary.BusinessIncome + summary.ProfessionalIncome);

    var alexRow = summary.ByProfessional.Single(p => p.StaffProfileId == alex.Id);
    Assert.Equal(8000, alexRow.BusinessTotal);
    Assert.Equal(12000, alexRow.ProfessionalTotal);
  }

  private async Task<StaffProfile> AddStaffAsync(string displayName)
  {
    var staff = new StaffProfile(Guid.NewGuid(), displayName, 30, DateTime.UtcNow);
    _dbContext.StaffProfiles.Add(staff);
    await _dbContext.SaveChangesAsync();
    return staff;
  }

  private async Task<IncomeEntry> AddIncomeAsync(Guid staffProfileId, int amount, bool isPromo, DateOnly occurredOn, int businessPercentage = 0)
  {
    var entry = new IncomeEntry(Guid.NewGuid(), "Corte", 25000, staffProfileId, amount, isPromo, occurredOn, Guid.NewGuid(), DateTime.UtcNow, businessPercentage);
    _dbContext.IncomeEntries.Add(entry);
    await _dbContext.SaveChangesAsync();
    return entry;
  }

  private async Task<ExpenseEntry> AddExpenseAsync(string name, int amount, DateOnly occurredOn)
  {
    var entry = new ExpenseEntry(null, name, amount, occurredOn, Guid.NewGuid(), DateTime.UtcNow);
    _dbContext.ExpenseEntries.Add(entry);
    await _dbContext.SaveChangesAsync();
    return entry;
  }
}
