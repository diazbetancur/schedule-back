using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Finance.Admin;
using Barbershop.Domain.Finance;
using Barbershop.Domain.Services;
using Barbershop.Domain.Staff;
using Barbershop.Infrastructure.Finance;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Tests.Features.Finance;

public sealed class IncomeManagementServiceTests : IDisposable
{
  private readonly AppDbContext _dbContext;
  private readonly IAdminIncomeService _service;
  private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-5));

  public IncomeManagementServiceTests()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options;

    _dbContext = new AppDbContext(options);
    _service = new IncomeManagementService(_dbContext, TimeProvider.System);
  }

  public void Dispose() => _dbContext.Dispose();

  [Fact]
  public async Task CreateAsync_TakesServiceSnapshotAndSetsCreatedBy()
  {
    var service = await AddServiceAsync("Corte de Cabello", 25000);
    var staff = await AddStaffAsync("Alex");
    var userId = Guid.NewGuid();

    var view = await SeedServiceIncomeAsync(userId, new IncomeEntryCreateRequest(
        service.Id, staff.Id, 20000, IsPromo: true, Today));

    Assert.Equal(service.Id, view.ServiceId);
    Assert.Equal("Corte de Cabello", view.ServiceName);
    Assert.Equal(25000, view.BasePrice);
    Assert.Equal(staff.Id, view.StaffProfileId);
    Assert.Equal("Alex", view.StaffDisplayName);
    Assert.Equal(20000, view.Amount);
    Assert.True(view.IsPromo);

    var stored = await _dbContext.IncomeEntries.SingleAsync();
    Assert.Equal(userId, stored.CreatedByUserId);
  }

  [Fact]
  public async Task CreateAsync_ServiceIncome_IsRetired_TheBillsPostIt()
  {
    var service = await AddServiceAsync("Barba", 10000);
    var staff = await AddStaffAsync("Alex");

    var exception = await Assert.ThrowsAsync<ConflictException>(() =>
        _service.CreateAsync(Guid.NewGuid(), new IncomeEntryCreateRequest(service.Id, staff.Id, 10000, false, Today)));

    Assert.Contains("«Cuentas»", exception.Message);
    Assert.Empty(_dbContext.IncomeEntries);
  }

  [Theory]
  [InlineData("ProductSale")]
  [InlineData("Tip")]
  public async Task CreateAsync_ProductSalesAndTips_OnlyComeFromBills(string kind)
  {
    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.CreateAsync(Guid.NewGuid(), new IncomeEntryCreateRequest(null, null, 10000, false, Today, kind)));

    Assert.Contains("kind", exception.Errors.Keys);
  }

  [Fact]
  public async Task UpdateAsync_ChangingService_ReSnapshots()
  {
    var first = await AddServiceAsync("Corte", 20000);
    var second = await AddServiceAsync("Tinte", 45000);
    var staff = await AddStaffAsync("Alex");

    var created = await SeedServiceIncomeAsync(Guid.NewGuid(), new IncomeEntryCreateRequest(
        first.Id, staff.Id, 20000, false, Today));

    var updated = await _service.UpdateAsync(created.Id, new IncomeEntryUpdateRequest(
        second.Id, staff.Id, 45000, false, Today));

    Assert.Equal(second.Id, updated.ServiceId);
    Assert.Equal("Tinte", updated.ServiceName);
    Assert.Equal(45000, updated.BasePrice);
  }

  [Fact]
  public async Task SoftDeleteAsync_ExcludesFromGet()
  {
    var service = await AddServiceAsync("Corte", 20000);
    var staff = await AddStaffAsync("Alex");
    var created = await SeedServiceIncomeAsync(Guid.NewGuid(), new IncomeEntryCreateRequest(
        service.Id, staff.Id, 20000, false, Today));

    await _service.SoftDeleteAsync(created.Id);

    var list = await _service.GetAsync(new IncomeListQuery(Today, Today));
    Assert.DoesNotContain(list.Items, e => e.Id == created.Id);
  }

  [Fact]
  public async Task GetAsync_FiltersByDateAndStaff()
  {
    var service = await AddServiceAsync("Corte", 20000);
    var alex = await AddStaffAsync("Alex");
    var bianca = await AddStaffAsync("Bianca");

    await SeedServiceIncomeAsync(Guid.NewGuid(), new IncomeEntryCreateRequest(service.Id, alex.Id, 20000, false, Today));
    await SeedServiceIncomeAsync(Guid.NewGuid(), new IncomeEntryCreateRequest(service.Id, bianca.Id, 20000, false, Today));
    await SeedServiceIncomeAsync(Guid.NewGuid(), new IncomeEntryCreateRequest(service.Id, alex.Id, 20000, false, Today.AddDays(-1)));

    var todayAll = await _service.GetAsync(new IncomeListQuery(Today, Today));
    Assert.Equal(2, todayAll.TotalCount);

    var todayAlex = (await _service.GetAsync(new IncomeListQuery(Today, Today, alex.Id))).Items;
    Assert.Single(todayAlex);
    Assert.All(todayAlex, e => Assert.Equal(alex.Id, e.StaffProfileId));
  }

  [Fact]
  public async Task CreateAsync_SnapshotsBusinessPercentageFromService()
  {
    var service = await AddServiceAsync("Corte", 20000, businessPercentage: 30);
    var staff = await AddStaffAsync("Alex");

    var view = await SeedServiceIncomeAsync(Guid.NewGuid(), new IncomeEntryCreateRequest(
        service.Id, staff.Id, 20000, false, Today));

    Assert.Equal(30, view.BusinessPercentage);
    Assert.Equal(6000, view.BusinessAmount);
    Assert.Equal(14000, view.ProfessionalAmount);
  }

  [Fact]
  public async Task CreateAsync_TruncatesBusinessAmountAndGivesRemainderToProfessional()
  {
    var service = await AddServiceAsync("Corte", 100, businessPercentage: 33);
    var staff = await AddStaffAsync("Alex");

    var view = await SeedServiceIncomeAsync(Guid.NewGuid(), new IncomeEntryCreateRequest(
        service.Id, staff.Id, 100, false, Today));

    Assert.Equal(33, view.BusinessAmount);
    Assert.Equal(67, view.ProfessionalAmount);
  }

  [Fact]
  public async Task UpdateAsync_ChangingService_ReSnapshotsBusinessPercentage()
  {
    var first = await AddServiceAsync("Corte", 20000, businessPercentage: 10);
    var second = await AddServiceAsync("Tinte", 45000, businessPercentage: 50);
    var staff = await AddStaffAsync("Alex");

    var created = await SeedServiceIncomeAsync(Guid.NewGuid(), new IncomeEntryCreateRequest(
        first.Id, staff.Id, 20000, false, Today));
    Assert.Equal(10, created.BusinessPercentage);

    var updated = await _service.UpdateAsync(created.Id, new IncomeEntryUpdateRequest(
        second.Id, staff.Id, 45000, false, Today));

    Assert.Equal(50, updated.BusinessPercentage);
  }

  [Fact]
  public async Task UpdateAsync_SameService_KeepsTheHistoricalBusinessPercentage()
  {
    var service = await AddServiceAsync("Corte", 20000, businessPercentage: 30);
    var staff = await AddStaffAsync("Alex");
    var created = await SeedServiceIncomeAsync(Guid.NewGuid(), new IncomeEntryCreateRequest(
        service.Id, staff.Id, 20000, false, Today));

    // The master changes after the income was registered.
    service.Update("Corte", 20000, DateTime.UtcNow, businessPercentage: 60);
    await _dbContext.SaveChangesAsync();

    var updated = await _service.UpdateAsync(created.Id, new IncomeEntryUpdateRequest(
        service.Id, staff.Id, 18000, IsPromo: true, Today));

    Assert.Equal(30, updated.BusinessPercentage);
    Assert.Equal(5400, updated.BusinessAmount);
    Assert.Equal(12600, updated.ProfessionalAmount);
    Assert.Equal(18000, updated.Amount);
    Assert.True(updated.IsPromo);
  }

  [Fact]
  public async Task UpdateAsync_SameService_KeepsTheNameAndBasePriceSnapshots()
  {
    var service = await AddServiceAsync("Corte clásico", 20000, businessPercentage: 30);
    var staff = await AddStaffAsync("Alex");
    var created = await SeedServiceIncomeAsync(Guid.NewGuid(), new IncomeEntryCreateRequest(
        service.Id, staff.Id, 20000, false, Today));

    service.Update("Corte premium", 35000, DateTime.UtcNow, businessPercentage: 30);
    await _dbContext.SaveChangesAsync();

    var updated = await _service.UpdateAsync(created.Id, new IncomeEntryUpdateRequest(
        service.Id, staff.Id, 20000, false, Today.AddDays(-1)));

    Assert.Equal("Corte clásico", updated.ServiceName);
    Assert.Equal(20000, updated.BasePrice);
    Assert.Equal(Today.AddDays(-1), updated.OccurredOn);
  }

  [Fact]
  public async Task UpdateAsync_SameService_ChangingTheProfessional_KeepsTheSplit()
  {
    var service = await AddServiceAsync("Corte", 20000, businessPercentage: 40);
    var first = await AddStaffAsync("Alex");
    var second = await AddStaffAsync("Juan");
    var created = await SeedServiceIncomeAsync(Guid.NewGuid(), new IncomeEntryCreateRequest(
        service.Id, first.Id, 20000, false, Today));

    service.Update("Corte", 20000, DateTime.UtcNow, businessPercentage: 0);
    await _dbContext.SaveChangesAsync();

    var updated = await _service.UpdateAsync(created.Id, new IncomeEntryUpdateRequest(
        service.Id, second.Id, 20000, false, Today));

    Assert.Equal(second.Id, updated.StaffProfileId);
    Assert.Equal(40, updated.BusinessPercentage);
    Assert.Equal(8000, updated.BusinessAmount);
    Assert.Equal(12000, updated.ProfessionalAmount);
  }

  [Fact]
  public async Task UpdateAsync_ChangingToAnotherServiceAndBack_TakesTheCurrentSnapshot()
  {
    // Choosing a service is the explicit operation that captures its current values, even if it was the original one.
    var first = await AddServiceAsync("Corte", 20000, businessPercentage: 10);
    var second = await AddServiceAsync("Tinte", 45000, businessPercentage: 50);
    var staff = await AddStaffAsync("Alex");
    var created = await SeedServiceIncomeAsync(Guid.NewGuid(), new IncomeEntryCreateRequest(
        first.Id, staff.Id, 20000, false, Today));

    await _service.UpdateAsync(created.Id, new IncomeEntryUpdateRequest(second.Id, staff.Id, 45000, false, Today));
    first.Update("Corte", 22000, DateTime.UtcNow, businessPercentage: 20);
    await _dbContext.SaveChangesAsync();

    var updated = await _service.UpdateAsync(created.Id, new IncomeEntryUpdateRequest(first.Id, staff.Id, 22000, false, Today));

    Assert.Equal(20, updated.BusinessPercentage);
    Assert.Equal(22000, updated.BasePrice);
  }

  private async Task<Service> AddServiceAsync(string name, int basePrice, int businessPercentage = 0)
  {
    var service = new Service(name, basePrice, DateTime.UtcNow, businessPercentage);
    _dbContext.Services.Add(service);
    await _dbContext.SaveChangesAsync();
    return service;
  }

  private async Task<StaffProfile> AddStaffAsync(string displayName)
  {
    var staff = new StaffProfile(Guid.NewGuid(), displayName, 30, DateTime.UtcNow);
    _dbContext.StaffProfiles.Add(staff);
    await _dbContext.SaveChangesAsync();
    return staff;
  }

  /// <summary>
  /// ADR-0020 Q18: new service income is no longer created by hand (the bills post it); the manual ones stored before
  /// are still listed, edited and deleted here. They are seeded as the manual creation stored them.
  /// </summary>
  private async Task<IncomeEntryView> SeedServiceIncomeAsync(Guid userId, IncomeEntryCreateRequest request)
  {
    var service = await _dbContext.Services.SingleAsync(candidate => candidate.Id == request.ServiceId);
    var entry = new IncomeEntry(
        service.Id, service.Name, service.BasePrice, request.StaffProfileId!.Value, request.Amount, request.IsPromo, request.OccurredOn,
        userId, DateTime.UtcNow, service.BusinessPercentage);
    _dbContext.IncomeEntries.Add(entry);
    await _dbContext.SaveChangesAsync();
    return (await _service.GetAsync(new IncomeListQuery(request.OccurredOn, request.OccurredOn))).Items.Single(item => item.Id == entry.Id);
  }
}
