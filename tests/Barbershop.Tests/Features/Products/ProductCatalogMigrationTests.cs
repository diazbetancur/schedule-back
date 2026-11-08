using System.Globalization;
using Barbershop.Domain.Appointments;
using Barbershop.Domain.Customers;
using Barbershop.Domain.Media;
using Barbershop.Domain.Products;
using Barbershop.Domain.Services;
using Barbershop.Domain.Staff;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Barbershop.Tests.Features.Products;

/// <summary>
/// AddProductCatalog (ADR-0016 B5) over a database with history: users, a professional with a photo, the service
/// catalog with a rate and an offering, a customer and an appointment. The data is created at the latest migration and
/// the schema is rolled back to B4, which is what production has before the deploy. Applying the migration must only
/// add the two empty product tables (every existing table byte for byte the same) with their constraints, and rolling
/// it back must restore every table exactly.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "ProductCatalogMigration")]
public sealed class ProductCatalogMigrationTests
{
  private const string MigrationBeforeProducts = "20261106232048_AddServiceToAppointmentsAndRecords";
  private static readonly string[] NewTables = ["product_categories", "products"];

  private readonly PostgresContainerFixture _postgres;

  public ProductCatalogMigrationTests(PostgresContainerFixture postgres) => _postgres = postgres;

  [Fact]
  public async Task ExistingTablesAreUntouched_TheProductTablesAreConstrained_AndTheRollbackRestoresEveryTable()
  {
    if (!_postgres.IsAvailable) return; // Only with PostgresContainerFixture.AllowSkipVariable set on purpose.

    await SeedHistoryAtLatestAsync();

    await using var db = CreateContext();
    await db.GetService<IMigrator>().MigrateAsync(MigrationBeforeProducts);
    foreach (var table in NewTables)
    {
      Assert.False(await TableExistsAsync(db, table));
    }

    var before = await SnapshotAllTablesAsync(db);
    Assert.Single(before["staff_profiles"]);
    Assert.Single(before["media_assets"]);
    Assert.Single(before["appointments"]);
    Assert.Equal(2, before["general_services"].Count);

    await db.GetService<IMigrator>().MigrateAsync();

    var after = await SnapshotAllTablesAsync(db);
    Assert.Equal(before.Keys.Order(), after.Keys.Except(NewTables).Order());
    foreach (var (table, rows) in before)
    {
      Assert.True(rows.SequenceEqual(after[table]), $"{table} changed");
    }

    // The new tables start empty: nothing is inferred from services or anything else.
    Assert.Empty(after["product_categories"]);
    Assert.Empty(after["products"]);

    // Constraints: live names unique (per category for products), codes unique and well formed, price >= 0,
    // a category with products cannot be physically deleted, and an image row removal only clears the reference.
    var categoryId = Guid.NewGuid();
    var otherCategoryId = Guid.NewGuid();
    await InsertCategoryAsync(db, categoryId, "Ceras", "ceras001");
    await InsertCategoryAsync(db, otherCategoryId, "Aceites", "aceite01");
    var duplicateCategory = await Assert.ThrowsAsync<PostgresException>(() => InsertCategoryAsync(db, Guid.NewGuid(), "Ceras", "ceras002"));
    Assert.Equal((PostgresErrorCodes.UniqueViolation, "ux_product_categories_normalized_name_live"), (duplicateCategory.SqlState, duplicateCategory.ConstraintName));
    await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE public.product_categories SET \"IsDeleted\" = true WHERE \"Id\" = {otherCategoryId}");
    await InsertCategoryAsync(db, Guid.NewGuid(), "Aceites", "aceite02"); // the name of a deleted category is free

    // An image of its own, so removing its row does not touch the seeded staff photo.
    var adminId = await db.Users.Select(user => user.Id).FirstAsync();
    var image = new MediaAsset("cera.jpg", "image/jpeg", 10, "media/productimage/cera.jpg", MediaAssetPurpose.ProductImage, adminId, DateTime.UtcNow);
    db.MediaAssets.Add(image);
    await db.SaveChangesAsync();
    var productId = Guid.NewGuid();
    await InsertProductAsync(db, productId, categoryId, "Cera mate", "cera0001", 35000, image.Id);
    var duplicateProduct = await Assert.ThrowsAsync<PostgresException>(() =>
        InsertProductAsync(db, Guid.NewGuid(), categoryId, "Cera mate", "cera0002", 1000, null));
    Assert.Equal((PostgresErrorCodes.UniqueViolation, "ux_products_category_normalized_name_live"), (duplicateProduct.SqlState, duplicateProduct.ConstraintName));
    await InsertProductAsync(db, Guid.NewGuid(), otherCategoryId, "Cera mate", "cera0003", 1000, null); // other category
    var duplicateCode = await Assert.ThrowsAsync<PostgresException>(() =>
        InsertProductAsync(db, Guid.NewGuid(), categoryId, "Pomada", "cera0001", 1000, null));
    Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicateCode.SqlState);
    var negative = await Assert.ThrowsAsync<PostgresException>(() =>
        InsertProductAsync(db, Guid.NewGuid(), categoryId, "Gel", "gel00001", -1, null));
    Assert.Equal(PostgresErrorCodes.CheckViolation, negative.SqlState);
    var badCode = await Assert.ThrowsAsync<PostgresException>(() =>
        InsertProductAsync(db, Guid.NewGuid(), categoryId, "Gel", "Con Mayúsculas", 1000, null));
    Assert.Equal(PostgresErrorCodes.CheckViolation, badCode.SqlState);
    var deleteCategory = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
        $"DELETE FROM public.product_categories WHERE \"Id\" = {categoryId}"));
    Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, deleteCategory.SqlState);

    await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM public.media_assets WHERE \"Id\" = {image.Id}");
    Assert.Equal(0, await db.Database.SqlQuery<int>(
        $"SELECT count(*)::int AS \"Value\" FROM public.products WHERE \"ImageMediaAssetId\" IS NOT NULL").SingleAsync());

    // Clean up so the rollback compares against the seeded history only.
    await db.Database.ExecuteSqlRawAsync("DELETE FROM public.products; DELETE FROM public.product_categories;");

    await db.GetService<IMigrator>().MigrateAsync(MigrationBeforeProducts);
    foreach (var table in NewTables)
    {
      Assert.False(await TableExistsAsync(db, table));
    }

    var rolledBack = await SnapshotAllTablesAsync(db);
    Assert.Equal(before.Keys.Order(), rolledBack.Keys.Order());
    foreach (var (table, rows) in before)
    {
      Assert.True(rows.SequenceEqual(rolledBack[table]), $"{table} changed after the rollback");
    }

    await db.GetService<IMigrator>().MigrateAsync();
  }

  /// <summary>History created at the latest migration (see the class summary).</summary>
  private async Task SeedHistoryAtLatestAsync()
  {
    await using var context = CreateContext();
    await context.Database.EnsureDeletedAsync();
    await context.Database.MigrateAsync();

    var now = DateTime.UtcNow;
    var admin = new User("Admin Principal", "admin@example.com", "hash", now);
    var barberUser = new User("Alex Barbero", "alex@example.com", "hash", now);
    var customerUser = new User("Carla Cliente", "carla@example.com", "hash", now);
    context.Users.AddRange(admin, barberUser, customerUser);
    var photo = new MediaAsset("alex.jpg", "image/jpeg", 1234, "media/staffphoto/alex.jpg", MediaAssetPurpose.StaffPhoto, admin.Id, now);
    photo.MarkReady("https://cdn.example.com/media/staffphoto/alex.jpg", now);
    context.MediaAssets.Add(photo);
    var staff = new StaffProfile(barberUser.Id, "Alex", 30, now);
    staff.SetPhoto(photo.Id, now);
    context.StaffProfiles.Add(staff);
    var beard = new GeneralService(ServiceCatalogDefaults.BarberiaCategoryId, "Barba", null, 1, now);
    context.GeneralServices.Add(beard);
    context.Services.Add(new Service("Corte clásico", 25000, now, 30));
    var profile = new CustomerProfile("Carla Cliente", "3001234567", "carla@example.com", admin.Id, now);
    profile.LinkAccount(customerUser.Id, admin.Id, now);
    context.Customers.Add(profile);
    context.Appointments.Add(new Appointment(
        staff.Id, "Carla Cliente", now.AddDays(1), now.AddDays(1).AddMinutes(30), AppointmentStatus.Confirmed,
        AppointmentSource.CustomerBooking, now, customerUser.Id));

    // Products created at the latest schema disappear with the rollback, like any other B5 data.
    var category = new ProductCategory("Ceras", 1, now);
    context.ProductCategories.Add(category);
    context.Products.Add(new Product(category.Id, "Cera mate", null, 35000, 0, now));
    await context.SaveChangesAsync();
  }

  private static Task<int> InsertCategoryAsync(AppDbContext db, Guid id, string name, string code)
      => db.Database.ExecuteSqlInterpolatedAsync($"""
          INSERT INTO public.product_categories ("Id", "Name", "NormalizedName", "SortOrder", "PublicCode", "IsActive", "IsDeleted", "CreatedAt")
          VALUES ({id}, {name}, {name.ToUpperInvariant()}, 0, {code}, true, false, now())
          """);

  private static Task<int> InsertProductAsync(AppDbContext db, Guid id, Guid categoryId, string name, string code, int price, Guid? imageId)
      => db.Database.ExecuteSqlInterpolatedAsync($"""
          INSERT INTO public.products ("Id", "CategoryId", "Name", "NormalizedName", "Price", "ImageMediaAssetId", "SortOrder", "PublicCode", "IsActive", "IsDeleted", "CreatedAt")
          VALUES ({id}, {categoryId}, {name}, {name.ToUpperInvariant()}, {price}, {imageId}, 0, {code}, true, false, now())
          """);

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
