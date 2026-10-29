using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Memberships.Admin;
using Barbershop.Domain.Services;
using Barbershop.Infrastructure.Memberships;
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Tests.Features.Memberships;

public sealed class MembershipPlanManagementServiceTests : IDisposable
{
  private const string Terms = "La membresía es personal e intransferible.";

  private readonly AppDbContext _dbContext;
  private readonly IAdminMembershipPlansService _service;
  private readonly Service _haircut;
  private readonly Service _beard;
  private readonly Service _dye;

  public MembershipPlanManagementServiceTests()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options;

    _dbContext = new AppDbContext(options);
    _service = new MembershipPlanManagementService(_dbContext, TimeProvider.System);

    _haircut = new Service("Corte", 25000, DateTime.UtcNow, 30);
    _beard = new Service("Barba", 15000, DateTime.UtcNow, 30);
    _dye = new Service("Tinte", 40000, DateTime.UtcNow, 30);
    _dbContext.Services.AddRange(_haircut, _beard, _dye);
    _dbContext.SaveChanges();
  }

  public void Dispose() => _dbContext.Dispose();

  [Fact]
  public async Task CreateAsync_PoolPlan_StoresSharedQuantity_ServicesByReference_AndGiftRules()
  {
    var view = await _service.CreateAsync(PoolRequest(
        gifts:
        [
          new MembershipPlanGiftRuleRequest(null, "Service", _haircut.Id, null, 1, "FirstPurchaseOfPlan"),
          new MembershipPlanGiftRuleRequest(null, "physical", null, "  Cera para cabello ", 2, "everypurchase"),
        ]));

    Assert.Equal("Pool", view.Mode);
    Assert.Equal(10, view.PoolQuantity);
    Assert.True(view.IsActive);
    Assert.All(view.Services, service => Assert.Null(service.Quantity));
    Assert.Equal(["Barba", "Corte"], view.Services.Select(service => service.ServiceName));
    Assert.Equal(25000, view.Services.Single(service => service.ServiceId == _haircut.Id).ServiceBasePrice);

    var serviceGift = view.GiftRules[0];
    Assert.Equal("Service", serviceGift.Kind);
    Assert.Equal("Corte", serviceGift.ServiceName);
    Assert.Equal("FirstPurchaseOfPlan", serviceGift.Condition);
    var physicalGift = view.GiftRules[1];
    Assert.Equal("Physical", physicalGift.Kind);
    Assert.Equal("Cera para cabello", physicalGift.Description);
    Assert.Equal("EveryPurchase", physicalGift.Condition);
    Assert.Equal(2, physicalGift.Quantity);
  }

  [Fact]
  public async Task CreateAsync_PerServicePlan_StoresIndependentQuantities()
  {
    var view = await _service.CreateAsync(PerServiceRequest());

    Assert.Equal("PerService", view.Mode);
    Assert.Null(view.PoolQuantity);
    Assert.Equal(8, view.Services.Single(service => service.ServiceId == _haircut.Id).Quantity);
    Assert.Equal(4, view.Services.Single(service => service.ServiceId == _beard.Id).Quantity);
  }

  [Theory]
  [InlineData(0)]
  [InlineData(-3)]
  [InlineData(1001)]
  public async Task CreateAsync_RejectsInvalidPoolQuantity(int poolQuantity)
  {
    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.CreateAsync(PoolRequest() with { PoolQuantity = poolQuantity }));

    Assert.Contains("poolQuantity", exception.Errors.Keys);
  }

  [Theory]
  [InlineData(0)]
  [InlineData(-1)]
  public async Task CreateAsync_RejectsZeroOrNegativeServiceAndGiftQuantities(int quantity)
  {
    var request = PerServiceRequest() with
    {
      Services = [new(_haircut.Id, quantity)],
      GiftRules = [new(null, "Physical", null, "Gorra", quantity, "EveryPurchase")],
    };

    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() => _service.CreateAsync(request));

    Assert.Contains("services[0].quantity", exception.Errors.Keys);
    Assert.Contains("giftRules[0].quantity", exception.Errors.Keys);
  }

  [Theory]
  [InlineData(0)]
  [InlineData(-30)]
  [InlineData(3651)]
  public async Task CreateAsync_RejectsInvalidDuration(int days)
  {
    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.CreateAsync(PoolRequest() with { DefaultDurationDays = days }));

    Assert.Contains("defaultDurationDays", exception.Errors.Keys);
  }

  [Fact]
  public async Task CreateAsync_RejectsNegativePrice_EmptyTerms_AndMissingServices()
  {
    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.CreateAsync(PoolRequest() with { Price = -1, Terms = "  ", Services = [] }));

    Assert.Contains("price", exception.Errors.Keys);
    Assert.Contains("terms", exception.Errors.Keys);
    Assert.Contains("services", exception.Errors.Keys);
  }

  [Fact]
  public async Task CreateAsync_RejectsTheSameServiceTwice()
  {
    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.CreateAsync(PerServiceRequest() with { Services = [new(_haircut.Id, 2), new(_haircut.Id, 3)] }));

    Assert.Contains("services", exception.Errors.Keys);
  }

  [Fact]
  public async Task CreateAsync_RejectsGiftingAServiceThatIsNotIncluded()
  {
    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.CreateAsync(PoolRequest(gifts: [new(null, "Service", _dye.Id, null, 1, "EveryPurchase")])));

    Assert.Contains("giftRules[0].serviceId", exception.Errors.Keys);
  }

  [Fact]
  public async Task CreateAsync_RejectsConfigurationsIncompatibleWithTheMode()
  {
    var poolWithServiceQuantities = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.CreateAsync(PoolRequest() with { Services = [new(_haircut.Id, 3)] }));
    Assert.Contains("services[0].quantity", poolWithServiceQuantities.Errors.Keys);

    var perServiceWithPool = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.CreateAsync(PerServiceRequest() with { PoolQuantity = 10 }));
    Assert.Contains("poolQuantity", perServiceWithPool.Errors.Keys);

    var perServiceWithoutQuantity = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.CreateAsync(PerServiceRequest() with { Services = [new(_haircut.Id, null)] }));
    Assert.Contains("services[0].quantity", perServiceWithoutQuantity.Errors.Keys);
  }

  [Theory]
  [InlineData("Bolsa")]
  [InlineData("1")]
  [InlineData("")]
  public async Task CreateAsync_RejectsUnknownModes_IncludingNumericStrings(string mode)
  {
    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.CreateAsync(PoolRequest() with { Mode = mode }));

    Assert.Contains("mode", exception.Errors.Keys);
  }

  [Fact]
  public async Task CreateAsync_ValidatesTheShapeOfEachGiftKind()
  {
    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.CreateAsync(PoolRequest(gifts:
        [
          new(null, "Physical", null, " ", 1, "EveryPurchase"),
          new(null, "Physical", _haircut.Id, "Gorra", 1, "EveryPurchase"),
          new(null, "Service", _haircut.Id, "Texto", 1, "EveryPurchase"),
          new(null, "Producto", null, "Gorra", 1, "EveryPurchase"),
          new(null, "Physical", null, "Gorra", 1, "Siempre"),
        ])));

    Assert.Contains("giftRules[0].description", exception.Errors.Keys);
    Assert.Contains("giftRules[1].serviceId", exception.Errors.Keys);
    Assert.Contains("giftRules[2].description", exception.Errors.Keys);
    Assert.Contains("giftRules[3].kind", exception.Errors.Keys);
    Assert.Contains("giftRules[4].condition", exception.Errors.Keys);
  }

  [Fact]
  public async Task CreateAsync_RejectsUnknownDeletedAndInactiveServices()
  {
    _dye.Deactivate(DateTime.UtcNow);
    _beard.MarkDeleted(DateTime.UtcNow);
    await _dbContext.SaveChangesAsync();

    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.CreateAsync(PoolRequest() with { Services = [new(Guid.NewGuid(), null), new(_beard.Id, null), new(_dye.Id, null)] }));

    Assert.Contains("services[0].serviceId", exception.Errors.Keys);
    Assert.Contains("services[1].serviceId", exception.Errors.Keys);
    Assert.Contains("services[2].serviceId", exception.Errors.Keys);
  }

  [Fact]
  public async Task UpdateAsync_KeepsAServiceThatWasDeactivatedAfterItWasAdded()
  {
    var plan = await _service.CreateAsync(PoolRequest());
    _beard.Deactivate(DateTime.UtcNow);
    await _dbContext.SaveChangesAsync();

    var updated = await _service.UpdateAsync(plan.Id, PoolRequest() with { Price = 300000 });

    Assert.Equal(300000, updated.Price);
    Assert.False(updated.Services.Single(service => service.ServiceId == _beard.Id).ServiceIsActive);
  }

  [Fact]
  public async Task CreateAsync_RejectsDuplicateNamesCaseInsensitive_ButAllowsReusingADeletedName()
  {
    var first = await _service.CreateAsync(PoolRequest());

    var exception = await Assert.ThrowsAsync<ConflictException>(() =>
        _service.CreateAsync(PoolRequest() with { Name = "  MEMBRESÍA ORO " }));
    Assert.Equal("Ya existe un tipo de membresía con ese nombre.", exception.Message);

    await _service.SoftDeleteAsync(first.Id);
    var recreated = await _service.CreateAsync(PoolRequest());
    Assert.NotEqual(first.Id, recreated.Id);
  }

  [Fact]
  public async Task UpdateAsync_SyncsServices_AndKeepsGiftRuleIdsStable()
  {
    var plan = await _service.CreateAsync(PerServiceRequest(gifts:
    [
      new(null, "Service", _beard.Id, null, 1, "EveryPurchase"),
      new(null, "Physical", null, "Gorra", 1, "FirstPurchaseOfPlan"),
    ]));
    var keptRuleId = plan.GiftRules.Single(rule => rule.Kind == "Physical").Id;
    var removedRuleId = plan.GiftRules.Single(rule => rule.Kind == "Service").Id;

    var updated = await _service.UpdateAsync(plan.Id, PerServiceRequest() with
    {
      Services = [new(_haircut.Id, 6), new(_dye.Id, 2)],
      GiftRules =
      [
        new(keptRuleId, "Physical", null, "Gorra edición especial", 1, "FirstPurchaseOfPlan"),
        new(null, "Service", _dye.Id, null, 1, "EveryPurchase"),
      ],
    });

    Assert.Equal([(_haircut.Id, (int?)6), (_dye.Id, (int?)2)],
        updated.Services.OrderBy(service => service.ServiceName).Select(service => (service.ServiceId, service.Quantity)));
    Assert.Equal(2, updated.GiftRules.Count);
    // Rules come back in the order they were configured.
    Assert.Equal(keptRuleId, updated.GiftRules[0].Id);
    Assert.Equal("Service", updated.GiftRules[1].Kind);
    var editedRule = updated.GiftRules.Single(rule => rule.Id == keptRuleId);
    Assert.Equal("Gorra edición especial", editedRule.Description);
    Assert.DoesNotContain(updated.GiftRules, rule => rule.Id == removedRuleId);

    // The removed rule is soft-deleted, not physically removed, so future sold gifts can keep pointing to it.
    var removedRule = await _dbContext.MembershipPlanGiftRules.SingleAsync(rule => rule.Id == removedRuleId);
    Assert.True(removedRule.IsDeleted);
  }

  [Fact]
  public async Task GiftRules_SurviveRepeatedEdits_AndStatusChanges_WithoutDuplicatesOrLosses()
  {
    var created = await _service.CreateAsync(PoolRequest(gifts:
    [
      new(null, "Service", _haircut.Id, null, 1, "FirstPurchaseOfPlan"),
      new(null, "Physical", null, "Cera", 1, "EveryPurchase"),
      new(null, "Physical", null, "Gorra", 2, "FirstPurchaseOfPlan"),
    ]));
    var (cut, wax, cap) = (created.GiftRules[0].Id, created.GiftRules[1].Id, created.GiftRules[2].Id);

    // Edit 1: reorder and change a field; every rule keeps its id.
    var edit1 = await _service.UpdateAsync(created.Id, PoolRequest(gifts:
    [
      new(cap, "Physical", null, "Gorra", 3, "FirstPurchaseOfPlan"),
      new(cut, "Service", _haircut.Id, null, 1, "FirstPurchaseOfPlan"),
      new(wax, "Physical", null, "Cera", 1, "EveryPurchase"),
    ]));
    Assert.Equal([cap, cut, wax], edit1.GiftRules.Select(rule => rule.Id));
    Assert.Equal(3, edit1.GiftRules[0].Quantity);

    // Edit 2: unchanged resubmission (what the form sends on a plain save) must not duplicate anything.
    var edit2 = await _service.UpdateAsync(created.Id, PoolRequest(gifts: edit1.GiftRules.Select(ToRequest).ToArray()));
    Assert.Equal(edit1.GiftRules, edit2.GiftRules);

    // Edit 3: drop one rule and add a new one at the end.
    var edit3 = await _service.UpdateAsync(created.Id, PoolRequest(gifts:
    [
      .. edit2.GiftRules.Where(rule => rule.Id != wax).Select(ToRequest),
      new(null, "Service", _beard.Id, null, 1, "EveryPurchase"),
    ]));
    Assert.Equal(3, edit3.GiftRules.Count);
    Assert.Equal([cap, cut], edit3.GiftRules.Take(2).Select(rule => rule.Id));
    Assert.Equal("Barba", edit3.GiftRules[2].ServiceName);

    // Deactivating and reactivating keeps the whole configuration.
    await _service.UpdateStatusAsync(created.Id, new MembershipPlanStatusUpdateRequest(false));
    var reactivated = await _service.UpdateStatusAsync(created.Id, new MembershipPlanStatusUpdateRequest(true));
    Assert.Equal(edit3.GiftRules, reactivated.GiftRules);
    Assert.Equal(edit3.Services, reactivated.Services);
    Assert.Equal(edit3.Terms, reactivated.Terms);

    // Stored rows: the four rules ever created, exactly one of them soft-deleted (the wax), no extras.
    var stored = await _dbContext.MembershipPlanGiftRules.Where(rule => rule.MembershipPlanId == created.Id).ToListAsync();
    Assert.Equal(4, stored.Count);
    Assert.Equal(wax, Assert.Single(stored, rule => rule.IsDeleted).Id);
    Assert.Equal(2, await _dbContext.MembershipPlanServices.CountAsync(service => service.MembershipPlanId == created.Id));
  }

  [Fact]
  public async Task UpdateAsync_RejectsGiftRuleIdsFromAnotherPlan()
  {
    var other = await _service.CreateAsync(PoolRequest(gifts: [new(null, "Physical", null, "Gorra", 1, "EveryPurchase")]) with { Name = "Otra" });
    var plan = await _service.CreateAsync(PoolRequest());

    var exception = await Assert.ThrowsAsync<ValidationProblemException>(() =>
        _service.UpdateAsync(plan.Id, PoolRequest(gifts: [new(other.GiftRules[0].Id, "Physical", null, "Gorra", 1, "EveryPurchase")])));

    Assert.Contains("giftRules[0].id", exception.Errors.Keys);
  }

  [Fact]
  public async Task UpdateStatusAsync_DeactivatesAndReactivates_WithoutHidingThePlan()
  {
    var plan = await _service.CreateAsync(PoolRequest());

    var inactive = await _service.UpdateStatusAsync(plan.Id, new MembershipPlanStatusUpdateRequest(false));
    Assert.False(inactive.IsActive);
    Assert.Contains(await _service.GetAllAsync(), listed => listed.Id == plan.Id && !listed.IsActive);

    var active = await _service.UpdateStatusAsync(plan.Id, new MembershipPlanStatusUpdateRequest(true));
    Assert.True(active.IsActive);
    Assert.Equal(plan.Services.Count, active.Services.Count);
  }

  [Fact]
  public async Task SoftDeleteAsync_HidesThePlan_ButKeepsTheRow()
  {
    var plan = await _service.CreateAsync(PoolRequest());

    await _service.SoftDeleteAsync(plan.Id);

    Assert.DoesNotContain(await _service.GetAllAsync(), listed => listed.Id == plan.Id);
    await Assert.ThrowsAsync<NotFoundException>(() => _service.GetByIdAsync(plan.Id));
    Assert.True((await _dbContext.MembershipPlans.SingleAsync(row => row.Id == plan.Id)).IsDeleted);
  }

  private static MembershipPlanGiftRuleRequest ToRequest(MembershipPlanGiftRuleView rule)
      => new(rule.Id, rule.Kind, rule.ServiceId, rule.Description, rule.Quantity, rule.Condition);

  private MembershipPlanUpsertRequest PoolRequest(IReadOnlyList<MembershipPlanGiftRuleRequest>? gifts = null) =>
      new(
          "Membresía Oro",
          "Diez servicios intercambiables",
          250000,
          "Pool",
          10,
          90,
          Terms,
          null,
          [new(_haircut.Id, null), new(_beard.Id, null)],
          gifts ?? []);

  private MembershipPlanUpsertRequest PerServiceRequest(IReadOnlyList<MembershipPlanGiftRuleRequest>? gifts = null) =>
      new(
          "Membresía Plata",
          null,
          180000,
          "PerService",
          null,
          60,
          Terms,
          "Válida de lunes a jueves.",
          [new(_haircut.Id, 8), new(_beard.Id, 4)],
          gifts ?? []);
}
