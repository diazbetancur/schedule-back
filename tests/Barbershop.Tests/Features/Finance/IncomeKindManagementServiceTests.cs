using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Finance.Admin;
using Barbershop.Domain.Services;
using Barbershop.Domain.Staff;
using Barbershop.Infrastructure.Finance;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Tests.Features.Finance;

public sealed class IncomeKindManagementServiceTests : IDisposable
{
  private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-5));

  private readonly AppDbContext _dbContext;
  private readonly IAdminIncomeService _service;

  public IncomeKindManagementServiceTests()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options;

    _dbContext = new AppDbContext(options);
    _service = new IncomeManagementService(_dbContext, TimeProvider.System);
  }

  public void Dispose() => _dbContext.Dispose();

  [Fact]
  public async Task CreateAsync_WithoutKind_IsServiceIncome_LikeOlderClientsExpect()
  {
    var service = await AddServiceAsync("Corte", 25000, businessPercentage: 40);
    var staff = await AddStaffAsync("Alex");

    var view = await _service.CreateAsync(Guid.NewGuid(), new IncomeEntryCreateRequest(service.Id, staff.Id, 20000, false, Today));

    Assert.Equal("Service", view.Kind);
    Assert.Equal("Corte", view.ServiceName);
    Assert.Equal("Alex", view.StaffDisplayName);
    Assert.Equal(40, view.BusinessPercentage);
    Assert.Equal(8000, view.BusinessAmount);
    Assert.Equal(12000, view.ProfessionalAmount);
  }

  [Fact]
  public async Task CreateAsync_MembershipSale_IsAllBusiness_WithoutServiceOrProfessional()
  {
    var userId = Guid.NewGuid();

    var view = await _service.CreateAsync(userId, new IncomeEntryCreateRequest(null, null, 250000, false, Today, "MembershipSale"));

    Assert.Equal("MembershipSale", view.Kind);
    Assert.Null(view.ServiceId);
    Assert.Null(view.ServiceName);
    Assert.Null(view.StaffProfileId);
    Assert.Null(view.StaffDisplayName);
    Assert.Equal(0, view.BasePrice);
    Assert.Equal(100, view.BusinessPercentage);
    Assert.Equal(250000, view.BusinessAmount);
    Assert.Equal(0, view.ProfessionalAmount);
    Assert.False(view.IsPromo);
    Assert.Equal(userId, (await _dbContext.IncomeEntries.SingleAsync()).CreatedByUserId);
    // No catalog service was needed (or created) to record it.
    Assert.Empty(await _dbContext.Services.ToListAsync());
  }

  [Fact]
  public async Task CreateAsync_MembershipSale_RejectsServiceProfessionalAndPromo()
  {
    var service = await AddServiceAsync("Corte", 25000);
    var staff = await AddStaffAsync("Alex");

    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.CreateAsync(Guid.NewGuid(), new IncomeEntryCreateRequest(service.Id, staff.Id, 250000, true, Today, "MembershipSale")));

    Assert.Contains("serviceId", exception.Errors.Keys);
    Assert.Contains("staffProfileId", exception.Errors.Keys);
    Assert.Contains("isPromo", exception.Errors.Keys);
    Assert.Empty(await _dbContext.IncomeEntries.ToListAsync());
  }

  [Fact]
  public async Task CreateAsync_ServiceIncome_StillRequiresServiceAndProfessional()
  {
    var service = await AddServiceAsync("Corte", 25000);
    var staff = await AddStaffAsync("Alex");

    var missingService = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.CreateAsync(Guid.NewGuid(), new IncomeEntryCreateRequest(null, staff.Id, 20000, false, Today, "Service")));
    Assert.Contains("serviceId", missingService.Errors.Keys);

    var missingStaff = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.CreateAsync(Guid.NewGuid(), new IncomeEntryCreateRequest(service.Id, null, 20000, false, Today)));
    Assert.Contains("staffProfileId", missingStaff.Errors.Keys);
  }

  [Theory]
  [InlineData("Venta")]
  [InlineData("2")]
  public async Task CreateAsync_RejectsUnknownKinds(string kind)
  {
    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.CreateAsync(Guid.NewGuid(), new IncomeEntryCreateRequest(null, null, 1000, false, Today, kind)));

    Assert.Contains("kind", exception.Errors.Keys);
  }

  [Fact]
  public async Task CreateAsync_MembershipSale_ValidatesAmountAndDateLikeServiceIncome()
  {
    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.CreateAsync(Guid.NewGuid(), new IncomeEntryCreateRequest(null, null, -1, false, Today.AddDays(1), "MembershipSale")));

    Assert.Contains("amount", exception.Errors.Keys);
    Assert.Contains("occurredOn", exception.Errors.Keys);
  }

  [Fact]
  public async Task UpdateAsync_EditsAMembershipSale_AndKeepsItAllBusiness()
  {
    var created = await _service.CreateAsync(Guid.NewGuid(), new IncomeEntryCreateRequest(null, null, 250000, false, Today, "MembershipSale"));

    // Kind may be omitted on update; the entry keeps its kind.
    var updated = await _service.UpdateAsync(created.Id, new IncomeEntryUpdateRequest(null, null, 300000, false, Today.AddDays(-1)));

    Assert.Equal("MembershipSale", updated.Kind);
    Assert.Equal(300000, updated.Amount);
    Assert.Equal(Today.AddDays(-1), updated.OccurredOn);
    Assert.Equal(300000, updated.BusinessAmount);
    Assert.Equal(0, updated.ProfessionalAmount);
  }

  [Fact]
  public async Task UpdateAsync_NeverChangesTheKind()
  {
    var service = await AddServiceAsync("Corte", 25000);
    var staff = await AddStaffAsync("Alex");
    var serviceIncome = await _service.CreateAsync(Guid.NewGuid(), new IncomeEntryCreateRequest(service.Id, staff.Id, 20000, false, Today));
    var sale = await _service.CreateAsync(Guid.NewGuid(), new IncomeEntryCreateRequest(null, null, 250000, false, Today, "MembershipSale"));

    var toSale = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.UpdateAsync(serviceIncome.Id, new IncomeEntryUpdateRequest(null, null, 20000, false, Today, "MembershipSale")));
    Assert.Contains("kind", toSale.Errors.Keys);

    var toService = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.UpdateAsync(sale.Id, new IncomeEntryUpdateRequest(service.Id, staff.Id, 250000, false, Today, "Service")));
    Assert.Contains("kind", toService.Errors.Keys);

    // An edit that keeps the kind (sent or omitted) still works for both.
    Assert.Equal("Service", (await _service.UpdateAsync(serviceIncome.Id, new IncomeEntryUpdateRequest(service.Id, staff.Id, 22000, false, Today))).Kind);
    Assert.Equal("MembershipSale", (await _service.UpdateAsync(sale.Id, new IncomeEntryUpdateRequest(null, null, 260000, false, Today, "membershipsale"))).Kind);
  }

  [Fact]
  public async Task UpdateAsync_MembershipSale_RejectsAddingAServiceOrProfessional()
  {
    var service = await AddServiceAsync("Corte", 25000);
    var sale = await _service.CreateAsync(Guid.NewGuid(), new IncomeEntryCreateRequest(null, null, 250000, false, Today, "MembershipSale"));

    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.UpdateAsync(sale.Id, new IncomeEntryUpdateRequest(service.Id, null, 250000, false, Today)));

    Assert.Contains("serviceId", exception.Errors.Keys);
  }

  [Fact]
  public async Task GetAsync_FilteringByProfessional_LeavesMembershipSalesOut()
  {
    var service = await AddServiceAsync("Corte", 25000);
    var alex = await AddStaffAsync("Alex");
    await _service.CreateAsync(Guid.NewGuid(), new IncomeEntryCreateRequest(service.Id, alex.Id, 20000, false, Today));
    await _service.CreateAsync(Guid.NewGuid(), new IncomeEntryCreateRequest(null, null, 250000, false, Today, "MembershipSale"));

    var all = await _service.GetAsync(Today, null);
    Assert.Equal(2, all.Count);
    Assert.Contains(all, entry => entry.Kind == "MembershipSale" && entry.StaffDisplayName is null);

    var alexOnly = await _service.GetAsync(Today, alex.Id);
    Assert.Equal("Service", Assert.Single(alexOnly).Kind);
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
