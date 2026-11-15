using System.Globalization;
using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Products;
using Barbershop.Domain.Finance;
using Barbershop.Domain.Products;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Infrastructure.Products;
using Barbershop.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Barbershop.Tests.Features.Products;

/// <summary>
/// ADR-0020 stage 1 on real PostgreSQL: the AddInventory migration only adds two empty tables (existing products get no
/// stock and no cost), the stock card rejects updates and deletes, simultaneous movements of one product are serialized by
/// the product lock so the stock equals the sum of the movements, and the rollback restores every table.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "InventoryPostgres")]
public sealed class InventoryPostgresTests
{
  private const string MigrationBeforeInventory = "20261115105101_AddSelfApprovedFlagsAndNotificationPreferences";
  private static readonly string[] NewTables = ["product_stock", "inventory_movements"];

  private readonly PostgresContainerFixture _postgres;

  public InventoryPostgresTests(PostgresContainerFixture postgres) => _postgres = postgres;

  [Fact]
  public async Task TheMigration_OnlyAddsEmptyTables_AndItsRollbackRestoresEveryTable()
  {
    if (!_postgres.IsAvailable) return; // Only with PostgresContainerFixture.AllowSkipVariable set on purpose.

    await using (var seed = CreateContext())
    {
      await seed.Database.EnsureDeletedAsync();
      await seed.Database.MigrateAsync();
      var now = DateTime.UtcNow;
      var admin = new User("Admin Principal", "admin@example.com", "hash", now);
      seed.Users.Add(admin);
      var category = new ProductCategory("Ceras", 1, now);
      seed.ProductCategories.Add(category);
      seed.Products.Add(new Product(category.Id, "Cera mate", null, 35000, 0, now));
      seed.ExpenseEntries.Add(new ExpenseEntry(null, "Arriendo", 900000, DateOnly.FromDateTime(now), admin.Id, now));
      await seed.SaveChangesAsync();
    }

    await using var db = CreateContext();
    await db.GetService<IMigrator>().MigrateAsync(MigrationBeforeInventory);
    foreach (var table in NewTables)
    {
      Assert.False(await TableExistsAsync(db, table));
    }

    var before = await SnapshotAllTablesAsync(db);
    Assert.Single(before["products"]);

    await db.GetService<IMigrator>().MigrateAsync();

    var after = await SnapshotAllTablesAsync(db);
    Assert.Equal(before.Keys.Order(), after.Keys.Except(NewTables).Order());
    foreach (var (table, rows) in before)
    {
      Assert.True(rows.SequenceEqual(after[table]), $"{table} changed");
    }

    // Nothing is made up: the existing product has no stock row (0 units, no cost) and the card is empty.
    Assert.Empty(after["product_stock"]);
    Assert.Empty(after["inventory_movements"]);

    await db.GetService<IMigrator>().MigrateAsync(MigrationBeforeInventory);
    var rolledBack = await SnapshotAllTablesAsync(db);
    Assert.Equal(before.Keys.Order(), rolledBack.Keys.Order());
    foreach (var (table, rows) in before)
    {
      Assert.True(rows.SequenceEqual(rolledBack[table]), $"{table} changed after the rollback");
    }

    await db.GetService<IMigrator>().MigrateAsync();
  }

  [Fact]
  public async Task TheStockCard_IsAppendOnly_AndItsShapeIsChecked()
  {
    if (!_postgres.IsAvailable) return;
    var (adminId, productId) = await ResetAndSeedAsync();

    await using (var context = CreateContext())
    {
      await Inventory(context).ReceiveAsync(adminId, productId, new InventoryReceiptRequest(Guid.NewGuid(), 3, 10000, CreateExpense: true));
    }

    await using var db = CreateContext();
    var update = await Assert.ThrowsAsync<PostgresException>(() =>
        db.Database.ExecuteSqlRawAsync("UPDATE public.inventory_movements SET \"Quantity\" = 30"));
    Assert.Equal(PostgresErrorCodes.RestrictViolation, update.SqlState);
    var delete = await Assert.ThrowsAsync<PostgresException>(() =>
        db.Database.ExecuteSqlRawAsync("DELETE FROM public.inventory_movements"));
    Assert.Equal(PostgresErrorCodes.RestrictViolation, delete.SqlState);

    // An adjustment without a reason, or one carrying an expense, does not fit the table.
    var noReason = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO public.inventory_movements ("Id", "ProductId", "Type", "Quantity", "OnHandAfter", "OccurredOn", "IdempotencyKey", "PerformedByUserId", "CreatedAt")
        VALUES ({Guid.NewGuid()}, {productId}, 'AdjustmentOut', 1, 2, current_date, {Guid.NewGuid()}, {adminId}, now())
        """));
    Assert.Equal(PostgresErrorCodes.CheckViolation, noReason.SqlState);

    var expense = await db.ExpenseEntries.AsNoTracking().SingleAsync();
    Assert.Equal(30000, expense.Amount);
  }

  [Fact]
  public async Task SimultaneousMovementsOfOneProduct_AreSerialized_AndTheStockEqualsTheCard()
  {
    if (!_postgres.IsAvailable) return;
    var (adminId, productId) = await ResetAndSeedAsync();

    var receipts = Enumerable.Range(0, 6).Select(async index =>
    {
      await using var context = CreateContext();
      await Inventory(context).ReceiveAsync(adminId, productId, new InventoryReceiptRequest(Guid.NewGuid(), 2, 1000 * (index + 1)));
    });
    await Task.WhenAll(receipts);

    // Two withdrawals of 8 units out of 12 at the same time: the second sees the first and is refused.
    var outcomes = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
    {
      await using var context = CreateContext();
      try
      {
        await Inventory(context).AdjustAsync(adminId, productId, new InventoryAdjustmentRequest(Guid.NewGuid(), "Out", 8, "Conteo físico"));
        return (Exception?)null;
      }
      catch (Exception exception)
      {
        return exception;
      }
    }));
    Assert.Single(outcomes, outcome => outcome is null);
    Assert.Single(outcomes, outcome => outcome is ConflictException);

    await using var db = CreateContext();
    var stock = await db.ProductStocks.AsNoTracking().SingleAsync(row => row.ProductId == productId);
    var movements = await db.InventoryMovements.AsNoTracking().Where(movement => movement.ProductId == productId).ToListAsync();
    Assert.Equal(7, movements.Count);
    Assert.Equal(movements.Sum(movement => movement.SignedQuantity), stock.OnHand);
    Assert.Equal(4, stock.OnHand);
    // Each receipt adds 2 units, so the average of the six costs is 3 500 whatever the order.
    Assert.Equal(3500m, stock.AverageUnitCost);
  }

  private async Task<(Guid AdminId, Guid ProductId)> ResetAndSeedAsync()
  {
    await using var context = CreateContext();
    await context.Database.EnsureDeletedAsync();
    await context.Database.MigrateAsync();
    var now = DateTime.UtcNow;
    var admin = new User("Admin Principal", "admin@example.com", "hash", now);
    context.Users.Add(admin);
    var category = new ProductCategory("Ceras", 1, now);
    context.ProductCategories.Add(category);
    var product = new Product(category.Id, "Cera mate", null, 35000, 0, now);
    context.Products.Add(product);
    await context.SaveChangesAsync();
    return (admin.Id, product.Id);
  }

  private static InventoryManagementService Inventory(AppDbContext context) => new(context, TimeProvider.System);

  private AppDbContext CreateContext()
  {
    var options = new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql(_postgres.ConnectionString, npgsql => npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName))
        .Options;
    return new AppDbContext(options);
  }

  private static async Task<bool> TableExistsAsync(AppDbContext db, string table)
      => await db.Database.SqlQuery<int>($"""
          SELECT count(*)::int AS "Value" FROM information_schema.tables WHERE table_schema = 'public' AND table_name = {table}
          """).SingleAsync() == 1;

  /// <summary>Every row of every public table (except the migrations history), ordered by its whole content.</summary>
  private static async Task<Dictionary<string, List<string>>> SnapshotAllTablesAsync(AppDbContext db)
  {
    var connection = (NpgsqlConnection)db.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open)
    {
      await connection.OpenAsync();
    }

    var tables = new List<string>();
    await using (var list = new NpgsqlCommand(
        "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE' AND table_name <> '__EFMigrationsHistory' ORDER BY table_name",
        connection))
    await using (var reader = await list.ExecuteReaderAsync())
    {
      while (await reader.ReadAsync())
      {
        tables.Add(reader.GetString(0));
      }
    }

    var snapshot = new Dictionary<string, List<string>>();
    foreach (var table in tables)
    {
      await using var command = new NpgsqlCommand($"SELECT * FROM public.\"{table}\"", connection);
      await using var reader = await command.ExecuteReaderAsync();
      var rows = new List<string>();
      while (await reader.ReadAsync())
      {
        rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount)
            .OrderBy(index => reader.GetName(index), StringComparer.Ordinal)
            .Select(index => $"{reader.GetName(index)}={(reader.IsDBNull(index) ? "null" : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture))}")));
      }

      rows.Sort(StringComparer.Ordinal);
      snapshot[table] = rows;
    }

    return snapshot;
  }
}
