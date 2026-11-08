using Barbershop.Application.Auth;
using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Memberships.Admin;
using Barbershop.Application.Services.Admin;
using Barbershop.Domain.Loyalty;
using Barbershop.Domain.Services;
using Barbershop.Infrastructure.Identity;
using Barbershop.Infrastructure.Memberships;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Barbershop.Tests.Features.Loyalty;

/// <summary>Fixed loyalty points of services and membership types: values, compatibility and audit.</summary>
public sealed class LoyaltyCatalogPointsTests : IDisposable
{
  private const string Terms = "La membresía es personal e intransferible.";

  private readonly AppDbContext _dbContext;
  private readonly Guid _adminId = Guid.NewGuid();
  private readonly IAdminServicesService _services;
  private readonly IAdminMembershipPlansService _plans;
  private readonly Service _haircut;

  public LoyaltyCatalogPointsTests()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
        .Options;

    _dbContext = new AppDbContext(options);
    _dbContext.Database.EnsureCreated(); // seeds the default category and general service (ADR-0016 D4)
    var currentUser = new FixedCurrentUserAccessor(_adminId);
    _services = new ServiceManagementService(_dbContext, TimeProvider.System, currentUser);
    _plans = new MembershipPlanManagementService(_dbContext, TimeProvider.System, currentUser);

    _haircut = new Service("Corte", 25000, DateTime.UtcNow, 30);
    _dbContext.Services.Add(_haircut);
    _dbContext.SaveChanges();
  }

  public void Dispose() => _dbContext.Dispose();

  [Fact]
  public async Task Service_WithoutPoints_DefaultsToZero_AndWritesNoAudit()
  {
    var created = await _services.CreateAsync(new ServiceCreateRequest("Barba", 15000, 30, GeneralServiceId: ServiceCatalogDefaults.CorteGeneralServiceId));

    Assert.Equal(0, created.LoyaltyPoints);
    Assert.Equal(0, (await _services.GetByIdAsync(_haircut.Id)).LoyaltyPoints);
    Assert.Empty(await _dbContext.LoyaltyPointsChanges.ToListAsync());
  }

  [Fact]
  public async Task Service_CreatedWithPoints_StoresThem_AndAuditsTheChangeFromZero()
  {
    var created = await _services.CreateAsync(new ServiceCreateRequest("Barba", 15000, 30, 12, GeneralServiceId: ServiceCatalogDefaults.CorteGeneralServiceId));

    Assert.Equal(12, created.LoyaltyPoints);
    var change = Assert.Single(await _dbContext.LoyaltyPointsChanges.ToListAsync());
    Assert.Equal(LoyaltyPointsTarget.Service, change.TargetType);
    Assert.Equal(created.Id, change.TargetId);
    Assert.Equal(0, change.PreviousPoints);
    Assert.Equal(12, change.NewPoints);
    Assert.Equal(_adminId, change.ChangedByUserId);
  }

  [Fact]
  public async Task Service_UpdateWithoutPoints_KeepsThem_AsOldClientsDo()
  {
    await _services.UpdateAsync(_haircut.Id, new ServiceUpdateRequest("Corte", 25000, 30, 20));

    var updated = await _services.UpdateAsync(_haircut.Id, new ServiceUpdateRequest("Corte clásico", 26000, 30));

    Assert.Equal(20, updated.LoyaltyPoints);
    Assert.Equal("Corte clásico", updated.Name);
    Assert.Single(await _dbContext.LoyaltyPointsChanges.ToListAsync());
  }

  [Fact]
  public async Task Service_PointsSetToZero_StopGrantingPoints_AndEveryChangeIsAudited()
  {
    await _services.UpdateAsync(_haircut.Id, new ServiceUpdateRequest("Corte", 25000, 30, 20));
    var updated = await _services.UpdateAsync(_haircut.Id, new ServiceUpdateRequest("Corte", 25000, 30, 0));
    // Sending the same points again is not a change.
    await _services.UpdateAsync(_haircut.Id, new ServiceUpdateRequest("Corte", 25000, 30, 0));

    Assert.Equal(0, updated.LoyaltyPoints);
    var changes = await _dbContext.LoyaltyPointsChanges.OrderBy(change => change.PreviousPoints).ToListAsync();
    Assert.Equal([(0, 20), (20, 0)], changes.Select(change => (change.PreviousPoints, change.NewPoints)));
  }

  [Theory]
  [InlineData(-1)]
  [InlineData(LoyaltySettings.MaxPointsPerItem + 1)]
  public async Task Service_RejectsPointsOutsideTheRange(int points)
  {
    var onCreate = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _services.CreateAsync(new ServiceCreateRequest("Barba", 15000, 30, points, GeneralServiceId: ServiceCatalogDefaults.CorteGeneralServiceId)));
    var onUpdate = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _services.UpdateAsync(_haircut.Id, new ServiceUpdateRequest("Corte", 25000, 30, points)));

    Assert.Contains("loyaltyPoints", onCreate.Errors.Keys);
    Assert.Contains("loyaltyPoints", onUpdate.Errors.Keys);
    Assert.Equal(0, (await _services.GetByIdAsync(_haircut.Id)).LoyaltyPoints);
  }

  [Fact]
  public async Task Service_ChangingPoints_WithoutAnIdentifiedUser_IsRejected()
  {
    var anonymous = new ServiceManagementService(_dbContext, TimeProvider.System, new NullCurrentUserAccessor());

    await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
        anonymous.UpdateAsync(_haircut.Id, new ServiceUpdateRequest("Corte", 25000, 30, 5)));
    // Without points the update works as before.
    await anonymous.UpdateAsync(_haircut.Id, new ServiceUpdateRequest("Corte", 25000, 35));
  }

  [Fact]
  public async Task Plan_WithoutPoints_DefaultsToZero()
  {
    var plan = await _plans.CreateAsync(PlanRequest(loyaltyPoints: null));

    Assert.Equal(0, plan.LoyaltyPoints);
    Assert.Empty(await _dbContext.LoyaltyPointsChanges.ToListAsync());
  }

  [Fact]
  public async Task Plan_Points_AreStored_KeptWhenOmitted_SetToZero_AndAudited()
  {
    var plan = await _plans.CreateAsync(PlanRequest(loyaltyPoints: 150));
    Assert.Equal(150, plan.LoyaltyPoints);

    var kept = await _plans.UpdateAsync(plan.Id, PlanRequest(loyaltyPoints: null) with { Price = 260000 });
    Assert.Equal(150, kept.LoyaltyPoints);
    Assert.Equal(260000, kept.Price);

    var zero = await _plans.UpdateAsync(plan.Id, PlanRequest(loyaltyPoints: 0));
    Assert.Equal(0, zero.LoyaltyPoints);

    var changes = await _dbContext.LoyaltyPointsChanges.OrderBy(change => change.PreviousPoints).ToListAsync();
    Assert.All(changes, change => Assert.Equal(LoyaltyPointsTarget.MembershipPlan, change.TargetType));
    Assert.All(changes, change => Assert.Equal(plan.Id, change.TargetId));
    Assert.Equal([(0, 150), (150, 0)], changes.Select(change => (change.PreviousPoints, change.NewPoints)));
  }

  [Theory]
  [InlineData(-5)]
  [InlineData(LoyaltySettings.MaxPointsPerItem + 1)]
  public async Task Plan_RejectsPointsOutsideTheRange(int points)
  {
    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() => _plans.CreateAsync(PlanRequest(points)));

    Assert.Contains("loyaltyPoints", exception.Errors.Keys);
    Assert.Empty(await _dbContext.MembershipPlans.ToListAsync());
  }

  [Fact]
  public void Domain_RejectsNegativePointsDirectly()
  {
    Assert.Throws<ArgumentOutOfRangeException>(() => _haircut.SetLoyaltyPoints(-1, DateTime.UtcNow));
  }

  private MembershipPlanUpsertRequest PlanRequest(int? loyaltyPoints) =>
      new(
          "Membresía Oro",
          null,
          250000,
          "Pool",
          10,
          90,
          Terms,
          null,
          [new(_haircut.Id, null)],
          [],
          loyaltyPoints);

  private sealed class FixedCurrentUserAccessor(Guid userId) : ICurrentUserAccessor
  {
    public Guid? UserId { get; } = userId;
  }
}
