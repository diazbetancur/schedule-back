using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Finance.Admin;
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

    var view = await _service.CreateAsync(userId, new IncomeEntryCreateRequest(
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
  public async Task CreateAsync_UnknownService_ThrowsValidation()
  {
    var staff = await AddStaffAsync("Alex");

    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.CreateAsync(Guid.NewGuid(), new IncomeEntryCreateRequest(
            Guid.NewGuid(), staff.Id, 10000, false, Today)));

    Assert.Contains("serviceId", exception.Errors.Keys);
  }

  [Fact]
  public async Task CreateAsync_UnknownStaff_ThrowsValidation()
  {
    var service = await AddServiceAsync("Barba", 10000);

    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.CreateAsync(Guid.NewGuid(), new IncomeEntryCreateRequest(
            service.Id, Guid.NewGuid(), 10000, false, Today)));

    Assert.Contains("staffProfileId", exception.Errors.Keys);
  }

  [Fact]
  public async Task CreateAsync_NegativeAmount_ThrowsValidation()
  {
    var service = await AddServiceAsync("Barba", 10000);
    var staff = await AddStaffAsync("Alex");

    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.CreateAsync(Guid.NewGuid(), new IncomeEntryCreateRequest(
            service.Id, staff.Id, -1, false, Today)));

    Assert.Contains("amount", exception.Errors.Keys);
  }

  [Fact]
  public async Task CreateAsync_FutureDate_ThrowsValidation()
  {
    var service = await AddServiceAsync("Barba", 10000);
    var staff = await AddStaffAsync("Alex");

    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.CreateAsync(Guid.NewGuid(), new IncomeEntryCreateRequest(
            service.Id, staff.Id, 10000, false, Today.AddDays(1))));

    Assert.Contains("occurredOn", exception.Errors.Keys);
  }

  [Fact]
  public async Task UpdateAsync_ChangingService_ReSnapshots()
  {
    var first = await AddServiceAsync("Corte", 20000);
    var second = await AddServiceAsync("Tinte", 45000);
    var staff = await AddStaffAsync("Alex");

    var created = await _service.CreateAsync(Guid.NewGuid(), new IncomeEntryCreateRequest(
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
    var created = await _service.CreateAsync(Guid.NewGuid(), new IncomeEntryCreateRequest(
        service.Id, staff.Id, 20000, false, Today));

    await _service.SoftDeleteAsync(created.Id);

    var list = await _service.GetAsync(Today, null);
    Assert.DoesNotContain(list, e => e.Id == created.Id);
  }

  [Fact]
  public async Task GetAsync_FiltersByDateAndStaff()
  {
    var service = await AddServiceAsync("Corte", 20000);
    var alex = await AddStaffAsync("Alex");
    var bianca = await AddStaffAsync("Bianca");

    await _service.CreateAsync(Guid.NewGuid(), new IncomeEntryCreateRequest(service.Id, alex.Id, 20000, false, Today));
    await _service.CreateAsync(Guid.NewGuid(), new IncomeEntryCreateRequest(service.Id, bianca.Id, 20000, false, Today));
    await _service.CreateAsync(Guid.NewGuid(), new IncomeEntryCreateRequest(service.Id, alex.Id, 20000, false, Today.AddDays(-1)));

    var todayAll = await _service.GetAsync(Today, null);
    Assert.Equal(2, todayAll.Count);

    var todayAlex = await _service.GetAsync(Today, alex.Id);
    Assert.Single(todayAlex);
    Assert.All(todayAlex, e => Assert.Equal(alex.Id, e.StaffProfileId));
  }

  [Fact]
  public async Task CreateAsync_SnapshotsBusinessPercentageFromService()
  {
    var service = await AddServiceAsync("Corte", 20000, businessPercentage: 30);
    var staff = await AddStaffAsync("Alex");

    var view = await _service.CreateAsync(Guid.NewGuid(), new IncomeEntryCreateRequest(
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

    var view = await _service.CreateAsync(Guid.NewGuid(), new IncomeEntryCreateRequest(
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

    var created = await _service.CreateAsync(Guid.NewGuid(), new IncomeEntryCreateRequest(
        first.Id, staff.Id, 20000, false, Today));
    Assert.Equal(10, created.BusinessPercentage);

    var updated = await _service.UpdateAsync(created.Id, new IncomeEntryUpdateRequest(
        second.Id, staff.Id, 45000, false, Today));

    Assert.Equal(50, updated.BusinessPercentage);
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
}
