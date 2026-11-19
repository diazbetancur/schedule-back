using Barbershop.Application.Bills;
using Barbershop.Application.Common.Exceptions;
using Barbershop.Domain.Bills;
using Barbershop.Domain.Finance;
using Barbershop.Domain.Products;
using Barbershop.Domain.Services;
using Barbershop.Domain.Staff;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Bills;
using Barbershop.Infrastructure.Memberships;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Barbershop.Tests.Features.Bills;

/// <summary>
/// SONDA lote N on real PostgreSQL: the lines of a bill being corrected (unapproved) are referenced by the reversed
/// postings of the previous approval, so they are removed without breaking those references (BUG-177, ADR-0021 L9), and
/// the migration that allows it only adds a nullable column.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "BillsPostgres")]
public sealed class BillsLoteNPostgresTests
{
  private const string MigrationBeforeLineRemoval = "20261118205431_AddBillChargeSnapshot";

  private readonly PostgresContainerFixture _postgres;

  public BillsLoteNPostgresTests(PostgresContainerFixture postgres) => _postgres = postgres;

  [Fact]
  public async Task BUG177_RemovingLinesOfAnUnapprovedBill_Works_AndTheNewApprovalNetsLikeApprovingTheCorrectedBillFromScratch()
  {
    if (!_postgres.IsAvailable) return;
    var seeded = await ResetAndSeedAsync();

    // Service 25.000 + 2 units of wax 10.000 + tip 5.000 with the bill: approved, then unapproved.
    var bill = await OpenAsync(seeded);
    bill = await AddAsync(seeded, bill, new BillLineRequest(bill.Version, "Service", seeded.StaffId, ServiceCatalogDefaults.CorteGeneralServiceId, seeded.RateId));
    bill = await AddAsync(seeded, bill, new BillLineRequest(bill.Version, "Product", ProductId: seeded.WaxId, Quantity: 2));
    bill = await AddAsync(seeded, bill, new BillLineRequest(bill.Version, "Tip", seeded.StaffId, Amount: 5000, TipPaymentMode: "WithBill"));
    bill = await CloseAsync(seeded, bill);
    bill = await ApproveAsync(seeded, bill);
    bill = await UnapproveAsync(seeded, bill);
    Assert.Equal(BillStatus.Unapproved.ToString(), bill.Status);

    // Before the fix: 409 «Otra persona cambió estos datos…» (a foreign key violation of the reversed postings).
    var product = bill.Lines.Single(line => line.Type == "Product");
    bill = await RemoveAsync(seeded, bill, product.Id);
    var tip = bill.Lines.Single(line => line.Type == "Tip");
    bill = await RemoveAsync(seeded, bill, tip.Id);
    Assert.Equal(["Service"], bill.Lines.Select(line => line.Type));
    Assert.Equal(25000, bill.Total);

    bill = await ApproveAsync(seeded, bill);
    Assert.Equal(BillStatus.Approved.ToString(), bill.Status);

    await using var check = CreateContext();
    // The net of everything posted for the bill is what approving the corrected bill (only the service) gives.
    var incomes = await check.IncomeEntries.AsNoTracking().Where(entry => entry.BillId == bill.Id).ToListAsync();
    Assert.Equal(25000, incomes.Sum(entry => entry.Amount));
    Assert.Equal(0, incomes.Where(entry => entry.Kind == IncomeKind.ProductSale).Sum(entry => entry.Amount));
    Assert.Equal(0, incomes.Where(entry => entry.StaffProfileId == seeded.StaffId && entry.Kind == IncomeKind.Tip).Sum(entry => entry.Amount));
    // The two units went out and came back: the stock is whole again and nothing is committed by the bill.
    Assert.Equal(10, (await check.ProductStocks.AsNoTracking().SingleAsync(stock => stock.ProductId == seeded.WaxId)).OnHand);

    // The removed lines are kept (their postings point to them) but they are out of the bill.
    var stored = await check.BillLines.IgnoreQueryFilters().AsNoTracking().Where(line => line.BillId == bill.Id).ToListAsync();
    Assert.Equal(3, stored.Count);
    Assert.Equal(2, stored.Count(line => line.RemovedAt is not null));
    Assert.Single((await check.Bills.AsNoTracking().Include(candidate => candidate.Lines).SingleAsync(candidate => candidate.Id == bill.Id)).Lines);
    Assert.Equal(2, await check.BillEvents.CountAsync(billEvent => billEvent.BillId == bill.Id && billEvent.Type == BillEventType.LineRemoved));
  }

  [Fact]
  public async Task BUG177_TheMigration_OnlyAddsTheRemovedAtColumn_AndRollsBack()
  {
    if (!_postgres.IsAvailable) return;
    var seeded = await ResetAndSeedAsync();
    var bill = await OpenAsync(seeded);
    await AddAsync(seeded, bill, new BillLineRequest(bill.Version, "Service", seeded.StaffId, ServiceCatalogDefaults.CorteGeneralServiceId, seeded.RateId));

    await using var db = CreateContext();
    var before = await MigrationSnapshotReader.ReadAsync(db);
    await db.GetService<IMigrator>().MigrateAsync(MigrationBeforeLineRemoval);
    var down = await MigrationSnapshotReader.ReadAsync(db);
    await db.GetService<IMigrator>().MigrateAsync();
    var up = await MigrationSnapshotReader.ReadAsync(db);

    Assert.Equal(before.Keys.Order(), up.Keys.Order());
    foreach (var (table, rows) in before)
    {
      Assert.True(rows.SequenceEqual(up[table]), $"{table} changed");
    }

    // Down drops only the column (every line was in the bill): same rows otherwise.
    Assert.Equal(before["bill_lines"].Count, down["bill_lines"].Count);
    Assert.All(before["bill_lines"], row => Assert.Contains("RemovedAt=", row));
    Assert.All(down["bill_lines"], row => Assert.DoesNotContain("RemovedAt=", row));
  }

  private sealed record Seeded(Guid AdminId, Guid StaffId, Guid RateId, Guid WaxId, Guid Cash);

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
    context.Services.Add(rate);
    var category = new ProductCategory("Ceras", 1, now);
    context.ProductCategories.Add(category);
    var wax = new Product(category.Id, "Cera mate", null, 10000, 0, now);
    context.Products.Add(wax);
    var stock = new ProductStock(wax.Id, now);
    context.ProductStocks.Add(stock);
    context.InventoryMovements.Add(stock.Receive(10, 4000, DateOnly.FromDateTime(now), null, null, null, Guid.NewGuid(), admin.Id, now));
    await context.SaveChangesAsync();
    var cash = await context.PaymentMethods.Where(method => method.Name == "Efectivo").Select(method => method.Id).SingleAsync();
    return new Seeded(admin.Id, staff.Id, rate.Id, wax.Id, cash);
  }

  private async Task<BillView> OpenAsync(Seeded seeded)
  {
    await using var context = CreateContext();
    return await Service(context).OpenAsync(seeded.AdminId, new BillOpenRequest(Guid.NewGuid(), CustomerName: "Cliente de paso", StaffProfileId: seeded.StaffId));
  }

  private async Task<BillView> AddAsync(Seeded seeded, BillView bill, BillLineRequest request)
  {
    await using var context = CreateContext();
    return await Service(context).AddLineAsync(seeded.AdminId, bill.Id, request);
  }

  private async Task<BillView> RemoveAsync(Seeded seeded, BillView bill, Guid lineId)
  {
    await using var context = CreateContext();
    return await Service(context).RemoveLineAsync(seeded.AdminId, bill.Id, lineId, bill.Version);
  }

  private async Task<BillView> CloseAsync(Seeded seeded, BillView bill)
  {
    await using var context = CreateContext();
    return await Service(context).CloseAsync(seeded.AdminId, bill.Id, new BillCloseRequest(bill.Version, seeded.Cash));
  }

  private async Task<BillView> ApproveAsync(Seeded seeded, BillView bill)
  {
    await using var context = CreateContext();
    return await Service(context).ApproveAsync(seeded.AdminId, bill.Id, new BillApproveRequest(bill.Version, Guid.NewGuid()));
  }

  private async Task<BillView> UnapproveAsync(Seeded seeded, BillView bill)
  {
    await using var context = CreateContext();
    return await Service(context).UnapproveAsync(seeded.AdminId, bill.Id, new BillUnapproveRequest(bill.Version, Guid.NewGuid(), "Se cobró de más"));
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
