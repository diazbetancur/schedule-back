using Barbershop.Application.Authorization;
using Barbershop.Application.Bills;
using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Loyalty.Redemptions;
using Barbershop.Domain.Bills;
using Barbershop.Domain.Common;
using Barbershop.Domain.Customers;
using Barbershop.Domain.Loyalty;
using Barbershop.Domain.Services;
using Barbershop.Domain.Staff;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Authorization;
using Barbershop.Infrastructure.Bills;
using Barbershop.Infrastructure.Loyalty;
using Barbershop.Infrastructure.Memberships;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Barbershop.Tests.Features.Bills;

/// <summary>
/// SONDA lote K on real PostgreSQL: the charge copy of the bills and its backfill (BUG-121), one redemption in a single
/// bill under a race (BUG-129) and two saves of the same role at once (BUG-161).
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "BillsPostgres")]
public sealed class BillsLoteKPostgresTests
{
  private const string MigrationBeforeChargeSnapshot = "20261116021029_RetireServiceRecordRegistration";

  private readonly PostgresContainerFixture _postgres;

  public BillsLoteKPostgresTests(PostgresContainerFixture postgres) => _postgres = postgres;

  [Fact]
  public async Task BUG121_TheCashClosingKeepsWhatWasCharged_AndTheMigrationRebuildsItOnlyWithoutAmbiguity()
  {
    if (!_postgres.IsAvailable) return;
    var seeded = await ResetAndSeedAsync();
    Guid card;
    await using (var context = CreateContext())
    {
      var method = new PaymentMethod("Tarjeta prueba", 90, DateTime.UtcNow);
      context.PaymentMethods.Add(method);
      await context.SaveChangesAsync();
      card = method.Id;
    }

    // A: closed and never touched. B: closed in cash, then corrected (another rate, card). C: like B, and then the cash
    // method is renamed, so the name in its «Closed» event no longer identifies a method.
    var a = await CloseAsync(seeded, await AddServiceAsync(seeded, await OpenAsync(seeded), seeded.RateId));
    var b = await CorrectAsync(seeded, await CloseAsync(seeded, await AddServiceAsync(seeded, await OpenAsync(seeded), seeded.RateId)), card);
    var c = await CorrectAsync(seeded, await CloseAsync(seeded, await AddServiceAsync(seeded, await OpenAsync(seeded), seeded.RateId)), card);

    await using (var context = CreateContext())
    {
      var cash = await Service(context).GetCashClosingAsync(new CashClosingQuery());
      // What was charged: 3 × 25.000, whatever the corrections say now (each corrected bill is 20.000). ADR-0021 L11
      // (SONDA BUG-172): correcting the payment method moves the copy of the charge, so B and C count by card.
      Assert.Equal((3, 75000L), (cash.BillCount, cash.Total));
      Assert.Equal((1, 25000L), cash.Methods.Where(method => method.PaymentMethodId == seeded.Cash).Select(method => (method.BillCount, method.Total)).Single());
      Assert.Equal((2, 50000L), cash.Methods.Where(method => method.PaymentMethodId == card).Select(method => (method.BillCount, method.Total)).Single());
    }

    // The backfill: down (the copy is dropped) and up again (it is rebuilt from bill_events).
    await using (var db = CreateContext())
    {
      await db.GetService<IMigrator>().MigrateAsync(MigrationBeforeChargeSnapshot);
      await db.Database.ExecuteSqlInterpolatedAsync(
          $"UPDATE public.payment_methods SET \"Name\" = 'Efectivo renombrado' WHERE \"Id\" = {seeded.Cash}");
      await db.GetService<IMigrator>().MigrateAsync();
    }

    await using var check = CreateContext();
    var bills = await check.Bills.AsNoTracking().ToDictionaryAsync(bill => bill.Id);
    // A: method never changed → the current one (renamed, still the same method).
    Assert.Equal((25000, seeded.Cash), (bills[a.Id].ChargedTotal, bills[a.Id].ChargedPaymentMethodId));
    // B and C: changed after the close and no method is called «Efectivo» any more → no copy: ambiguous.
    Assert.Null(bills[b.Id].ChargedTotal);
    Assert.Null(bills[c.Id].ChargedPaymentMethodId);
  }

  [Fact]
  public async Task BUG121_TheBackfillRebuildsAChangedMethod_WhenItsNameStillIdentifiesIt()
  {
    if (!_postgres.IsAvailable) return;
    var seeded = await ResetAndSeedAsync();
    Guid card;
    await using (var context = CreateContext())
    {
      var method = new PaymentMethod("Tarjeta prueba", 90, DateTime.UtcNow);
      context.PaymentMethods.Add(method);
      await context.SaveChangesAsync();
      card = method.Id;
    }

    var bill = await CorrectAsync(seeded, await CloseAsync(seeded, await AddServiceAsync(seeded, await OpenAsync(seeded), seeded.RateId)), card);
    await using (var db = CreateContext())
    {
      await db.GetService<IMigrator>().MigrateAsync(MigrationBeforeChargeSnapshot);
      await db.GetService<IMigrator>().MigrateAsync();
    }

    await using var check = CreateContext();
    var stored = await check.Bills.AsNoTracking().SingleAsync(candidate => candidate.Id == bill.Id);
    Assert.Equal((25000, seeded.Cash, "Efectivo"), (stored.ChargedTotal, stored.ChargedPaymentMethodId, stored.ChargedPaymentMethodName));
    Assert.Equal((20000, card), (stored.Total, stored.PaymentMethodId!.Value));
  }

  [Fact]
  public async Task BUG129_TheSameRedemptionAddedToTwoBillsAtOnce_LandsInOnlyOne()
  {
    if (!_postgres.IsAvailable) return;
    var seeded = await ResetAndSeedAsync();
    Guid customerId, redemptionId;
    await using (var context = CreateContext())
    {
      var now = DateTime.UtcNow;
      var customer = new CustomerProfile("Carla Cliente", "3001234567", "carla@example.com", seeded.AdminId, now);
      context.Customers.Add(customer);
      var settings = await context.LoyaltySettings.SingleAsync();
      settings.Update(true, 365, seeded.AdminId, now);
      var today = DateOnly.FromDateTime(BogotaClock.ToLocal(now));
      var lot = LoyaltyLot.Accrue(customer.Id, LoyaltySourceType.Adjustment, Guid.NewGuid(), 100, now, today, 365);
      context.LoyaltyLots.Add(lot);
      context.LoyaltyMovements.Add(new LoyaltyMovement(customer.Id, LoyaltyMovementType.AdjustmentCredit, 100, now, lotId: lot.Id, performedByUserId: seeded.AdminId));
      await context.SaveChangesAsync();
      customerId = customer.Id;
      var redemptions = new LoyaltyRedemptionService(context, TimeProvider.System);
      var reward = await redemptions.CreateAsync(seeded.AdminId, new LoyaltyRewardUpsertRequest("Corte gratis", null, "Service", seeded.RateId, 50));
      redemptionId = (await redemptions.CreateAtCounterAsync(
          seeded.AdminId, customerId, new LoyaltyCounterRedemptionRequest(reward.Id, InPersonVerified: true, ApproveNow: true, IdempotencyKey: Guid.NewGuid()))).Id;
    }

    for (var round = 0; round < 4; round++)
    {
      var first = await OpenForCustomerAsync(seeded, customerId);
      var second = await OpenForCustomerAsync(seeded, customerId);
      var outcomes = await Task.WhenAll(new[] { first, second }.Select(async bill =>
      {
        await using var context = CreateContext();
        try
        {
          await Service(context).AddLineAsync(seeded.AdminId, bill.Id, new BillLineRequest(bill.Version, "Redemption", seeded.StaffId, LoyaltyRedemptionId: redemptionId));
          return (Exception?)null;
        }
        catch (Exception exception)
        {
          return exception;
        }
      }));

      Assert.Single(outcomes, outcome => outcome is null);
      Assert.IsType<ConflictException>(Assert.Single(outcomes.OfType<Exception>()));
      await using var check = CreateContext();
      Assert.Equal(1, await check.BillLines.CountAsync(line => line.LoyaltyRedemptionId == redemptionId
          && check.Bills.Any(bill => bill.Id == line.BillId && bill.Status != BillStatus.Voided)));

      // Free the redemption for the next round: void the bill that took it.
      var taken = await check.BillLines.AsNoTracking().Where(line => line.LoyaltyRedemptionId == redemptionId)
          .Join(check.Bills.AsNoTracking().Where(bill => bill.Status != BillStatus.Voided), line => line.BillId, bill => bill.Id, (line, bill) => bill)
          .SingleAsync();
      await using var voider = CreateContext();
      await Service(voider).VoidAsync(seeded.AdminId, taken.Id, new BillReasonRequest(taken.Version, "Prueba de carrera"));
    }
  }

  [Fact]
  public async Task BUG161_TwoSavesOfTheSameRoleAtOnce_KeepOneWholeSet_AndTheOtherIsAConflict()
  {
    if (!_postgres.IsAvailable) return;
    await ResetAndSeedAsync();
    Guid roleId;
    Guid[] setA, setB;
    string version;
    await using (var context = CreateContext())
    {
      var now = DateTime.UtcNow;
      var permissions = PermissionCodes.All.Select(code => new Permission(code, code, now)).ToList();
      context.Permissions.AddRange(permissions);
      var role = new Role("Caja prueba");
      context.Roles.Add(role);
      await context.SaveChangesAsync();
      roleId = role.Id;
      setA = permissions.Take(5).Select(permission => permission.Id).ToArray();
      setB = permissions.Skip(5).Take(6).Select(permission => permission.Id).ToArray();
      version = (await new RolesManagementService(context, TimeProvider.System).GetAllAsync()).Single(view => view.Id == roleId).Version!;
    }

    var outcomes = await Task.WhenAll(new[] { setA, setB }.Select(async set =>
    {
      await using var context = CreateContext();
      try
      {
        await new RolesManagementService(context, TimeProvider.System).UpdateAsync(roleId, new RoleUpdateRequest("Caja prueba", set, version));
        return (Exception?)null;
      }
      catch (Exception exception)
      {
        return exception;
      }
    }));

    Assert.Single(outcomes, outcome => outcome is null);
    Assert.Equal(RolesManagementService.ConcurrentChangeMessage, Assert.IsType<ConflictException>(Assert.Single(outcomes.OfType<Exception>())).Message);
    await using var check = CreateContext();
    var stored = (await check.RolePermissions.AsNoTracking().Where(grant => grant.RoleId == roleId).Select(grant => grant.PermissionId).ToListAsync()).ToHashSet();
    Assert.True(stored.SetEquals(setA) || stored.SetEquals(setB), "the role keeps exactly one of the two sets");
  }

  private sealed record Seeded(Guid AdminId, Guid StaffId, Guid RateId, Guid CheaperRateId, Guid Cash);

  private async Task<Seeded> ResetAndSeedAsync()
  {
    await using var context = CreateContext();
    await context.Database.EnsureDeletedAsync();
    await context.Database.MigrateAsync();
    var now = DateTime.UtcNow;
    var admin = new User("Admin Principal", "admin@example.com", "hash", now);
    var barberUser = new User("Alex Barbero", "alex@example.com", "hash", now);
    context.Users.AddRange(admin, barberUser);
    var adminRole = new Role(RoleNames.Admin, isSystemRole: true);
    context.Roles.Add(adminRole);
    context.UserRoles.Add(new UserRole(admin.Id, adminRole.Id, now));
    var staff = new StaffProfile(barberUser.Id, "Alex", 30, now);
    context.StaffProfiles.Add(staff);
    var rate = new Service("Corte", 25000, now, 40);
    var cheaper = new Service("Corte promo", 20000, now, 40);
    context.Services.AddRange(rate, cheaper);
    await context.SaveChangesAsync();
    var cash = await context.PaymentMethods.Where(method => method.Name == "Efectivo").Select(method => method.Id).SingleAsync();
    return new Seeded(admin.Id, staff.Id, rate.Id, cheaper.Id, cash);
  }

  private async Task<BillView> OpenAsync(Seeded seeded)
  {
    await using var context = CreateContext();
    return await Service(context).OpenAsync(seeded.AdminId, new BillOpenRequest(Guid.NewGuid(), CustomerName: "Cliente de paso", StaffProfileId: seeded.StaffId));
  }

  private async Task<BillView> OpenForCustomerAsync(Seeded seeded, Guid customerId)
  {
    await using var context = CreateContext();
    return await Service(context).OpenAsync(seeded.AdminId, new BillOpenRequest(Guid.NewGuid(), CustomerId: customerId, StaffProfileId: seeded.StaffId));
  }

  private async Task<BillView> AddServiceAsync(Seeded seeded, BillView bill, Guid rateId)
  {
    await using var context = CreateContext();
    return await Service(context).AddLineAsync(seeded.AdminId, bill.Id, new BillLineRequest(
        bill.Version, "Service", seeded.StaffId, ServiceCatalogDefaults.CorteGeneralServiceId, rateId));
  }

  private async Task<BillView> CloseAsync(Seeded seeded, BillView bill)
  {
    await using var context = CreateContext();
    return await Service(context).CloseAsync(seeded.AdminId, bill.Id, new BillCloseRequest(bill.Version, seeded.Cash));
  }

  /// <summary>The approver corrects the closed bill: a cheaper rate and the card instead of cash.</summary>
  private async Task<BillView> CorrectAsync(Seeded seeded, BillView bill, Guid card)
  {
    await using (var context = CreateContext())
    {
      bill = await Service(context).UpdateLineAsync(seeded.AdminId, bill.Id, bill.Lines.Single().Id, new BillLineRequest(
          bill.Version, StaffProfileId: seeded.StaffId, GeneralServiceId: ServiceCatalogDefaults.CorteGeneralServiceId, ServiceId: seeded.CheaperRateId));
    }

    await using var again = CreateContext();
    return await Service(again).ChangePaymentMethodAsync(seeded.AdminId, bill.Id, new BillPaymentMethodRequest(bill.Version, card, "Pagó con tarjeta"));
  }

  private static BillsService Service(AppDbContext context)
      => new(context, new MembershipLedgerService(context, new MembershipSalesService(context, TimeProvider.System), TimeProvider.System), TimeProvider.System);

  private AppDbContext CreateContext()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql(_postgres.ConnectionString, npgsql => npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName))
        .Options;
    return new AppDbContext(options);
  }
}
