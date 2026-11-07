using System.Data.Common;
using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Services.Admin;
using Barbershop.Domain.Services;
using Barbershop.Infrastructure.Identity;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Infrastructure.Services;
using Barbershop.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Barbershop.Tests.Features.Services;

/// <summary>
/// Simultaneous catalog changes on real PostgreSQL, forced deterministically in both orders: the first operation is
/// held right after the statement that takes its lock (or writes its row), the second is observed waiting in
/// pg_stat_activity, then the first is released. Whatever the order, a parent that ends inactive or deleted has no
/// live child added, and two equivalent names end as one row plus a controlled conflict.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "ServiceCatalogPostgres")]
public sealed class ServiceCatalogConcurrencyPostgresTests
{
  private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
  private static readonly Guid Barberia = ServiceCatalogDefaults.BarberiaCategoryId;
  private static readonly Guid Corte = ServiceCatalogDefaults.CorteGeneralServiceId;

  private readonly PostgresContainerFixture _postgres;

  public ServiceCatalogConcurrencyPostgresTests(PostgresContainerFixture postgres) => _postgres = postgres;

  // ── Category ↔ general service ────────────────────────────────────────────

  [Fact]
  public async Task DeactivatingTheCategoryFirst_MakesTheWaitingGeneralServiceCreationAConflict()
  {
    if (!_postgres.IsAvailable) return; // Only with PostgresContainerFixture.AllowSkipVariable set on purpose.
    var hair = await SeedCategoryAsync("Peluquería");

    var (deactivate, create) = await RaceAsync(
        Statement("service_categories", "FOR UPDATE"),
        context => Catalog(context).UpdateCategoryStatusAsync(hair, new ServiceCategoryStatusUpdateRequest(false)),
        context => Catalog(context).CreateGeneralServiceAsync(new GeneralServiceCreateRequest(hair, "Corte de dama")));

    Assert.Null(deactivate);
    Assert.IsType<ConflictException>(create);
    await using var verify = CreateContext(null);
    Assert.False((await verify.ServiceCategories.AsNoTracking().SingleAsync(category => category.Id == hair)).IsActive);
    Assert.False(await verify.GeneralServices.AnyAsync(service => service.CategoryId == hair));
  }

  [Fact]
  public async Task CreatingAGeneralServiceFirst_MakesTheWaitingCategoryDeactivationAConflict()
  {
    if (!_postgres.IsAvailable) return;
    var hair = await SeedCategoryAsync("Peluquería");

    var (create, deactivate) = await RaceAsync(
        Statement("service_categories", "FOR SHARE"),
        context => Catalog(context).CreateGeneralServiceAsync(new GeneralServiceCreateRequest(hair, "Corte de dama")),
        context => Catalog(context).UpdateCategoryStatusAsync(hair, new ServiceCategoryStatusUpdateRequest(false)));

    Assert.Null(create);
    Assert.IsType<ConflictException>(deactivate);
    await using var verify = CreateContext(null);
    Assert.True((await verify.ServiceCategories.AsNoTracking().SingleAsync(category => category.Id == hair)).IsActive);
    Assert.True((await verify.GeneralServices.AsNoTracking().SingleAsync(service => service.CategoryId == hair)).IsActive);
  }

  [Fact]
  public async Task DeletingTheCategoryFirst_RefusesTheWaitingMoveIntoIt()
  {
    if (!_postgres.IsAvailable) return;
    var hair = await SeedCategoryAsync("Peluquería");

    var (delete, move) = await RaceAsync(
        Statement("service_categories", "FOR UPDATE"),
        context => Catalog(context).DeleteCategoryAsync(hair),
        context => Catalog(context).UpdateGeneralServiceAsync(Corte, new GeneralServiceUpdateRequest(hair, "Corte")));

    Assert.Null(delete);
    Assert.IsType<ValidationProblemException>(move); // the category no longer exists
    await using var verify = CreateContext(null);
    Assert.True((await verify.ServiceCategories.AsNoTracking().SingleAsync(category => category.Id == hair)).IsDeleted);
    Assert.Equal(Barberia, (await verify.GeneralServices.AsNoTracking().SingleAsync(service => service.Id == Corte)).CategoryId);
  }

  [Fact]
  public async Task MovingIntoTheCategoryFirst_MakesTheWaitingCategoryDeletionAConflict()
  {
    if (!_postgres.IsAvailable) return;
    var hair = await SeedCategoryAsync("Peluquería");

    var (move, delete) = await RaceAsync(
        Statement("general_services", "FOR UPDATE"),
        context => Catalog(context).UpdateGeneralServiceAsync(Corte, new GeneralServiceUpdateRequest(hair, "Corte")),
        context => Catalog(context).DeleteCategoryAsync(hair));

    Assert.Null(move);
    Assert.IsType<ConflictException>(delete);
    await using var verify = CreateContext(null);
    Assert.False((await verify.ServiceCategories.AsNoTracking().SingleAsync(category => category.Id == hair)).IsDeleted);
    Assert.Equal(hair, (await verify.GeneralServices.AsNoTracking().SingleAsync(service => service.Id == Corte)).CategoryId);
  }

  [Fact]
  public async Task DeactivatingTheCategoryFirst_MakesTheWaitingGeneralServiceReactivationAConflict()
  {
    if (!_postgres.IsAvailable) return;
    var hair = await SeedCategoryAsync("Peluquería");
    var ladies = await SeedGeneralServiceAsync(hair, "Corte de dama", isActive: false);

    var (deactivate, reactivate) = await RaceAsync(
        Statement("service_categories", "FOR UPDATE"),
        context => Catalog(context).UpdateCategoryStatusAsync(hair, new ServiceCategoryStatusUpdateRequest(false)),
        context => Catalog(context).UpdateGeneralServiceStatusAsync(ladies, new GeneralServiceStatusUpdateRequest(true)));

    Assert.Null(deactivate);
    Assert.IsType<ConflictException>(reactivate);
    await using var verify = CreateContext(null);
    Assert.False((await verify.ServiceCategories.AsNoTracking().SingleAsync(category => category.Id == hair)).IsActive);
    Assert.False((await verify.GeneralServices.AsNoTracking().SingleAsync(service => service.Id == ladies)).IsActive);
  }

  // ── General service ↔ rate ────────────────────────────────────────────────

  [Fact]
  public async Task DeactivatingTheGeneralServiceFirst_MakesTheWaitingRateCreationAConflict()
  {
    if (!_postgres.IsAvailable) return;
    var beard = await SeedGeneralServiceAsync(Barberia, "Barba");

    var (deactivate, create) = await RaceAsync(
        Statement("general_services", "FOR UPDATE"),
        context => Catalog(context).UpdateGeneralServiceStatusAsync(beard, new GeneralServiceStatusUpdateRequest(false)),
        context => Rates(context).CreateAsync(new ServiceCreateRequest("Barba clásica", 15000, 40, GeneralServiceId: beard)));

    Assert.Null(deactivate);
    Assert.IsType<ConflictException>(create);
    await using var verify = CreateContext(null);
    Assert.False((await verify.GeneralServices.AsNoTracking().SingleAsync(service => service.Id == beard)).IsActive);
    Assert.False(await verify.Services.AnyAsync(rate => rate.GeneralServiceId == beard));
  }

  [Fact]
  public async Task CreatingARateFirst_MakesTheWaitingGeneralServiceDeactivationAConflict()
  {
    if (!_postgres.IsAvailable) return;
    var beard = await SeedGeneralServiceAsync(Barberia, "Barba");

    var (create, deactivate) = await RaceAsync(
        Statement("general_services", "FOR SHARE"),
        context => Rates(context).CreateAsync(new ServiceCreateRequest("Barba clásica", 15000, 40, GeneralServiceId: beard)),
        context => Catalog(context).UpdateGeneralServiceStatusAsync(beard, new GeneralServiceStatusUpdateRequest(false)));

    Assert.Null(create);
    Assert.IsType<ConflictException>(deactivate);
    await using var verify = CreateContext(null);
    Assert.True((await verify.GeneralServices.AsNoTracking().SingleAsync(service => service.Id == beard)).IsActive);
    Assert.True((await verify.Services.AsNoTracking().SingleAsync(rate => rate.GeneralServiceId == beard)).IsActive);
  }

  [Fact]
  public async Task DeletingTheGeneralServiceFirst_RefusesTheWaitingRateMoveIntoIt()
  {
    if (!_postgres.IsAvailable) return;
    var beard = await SeedGeneralServiceAsync(Barberia, "Barba");
    var rate = await SeedRateAsync(Corte, "Barba clásica");

    var (delete, move) = await RaceAsync(
        Statement("general_services", "FOR UPDATE"),
        context => Catalog(context).DeleteGeneralServiceAsync(beard),
        context => Rates(context).UpdateAsync(rate, new ServiceUpdateRequest("Barba clásica", 15000, 40, GeneralServiceId: beard)));

    Assert.Null(delete);
    Assert.IsType<ValidationProblemException>(move); // the general service no longer exists
    await using var verify = CreateContext(null);
    Assert.True((await verify.GeneralServices.AsNoTracking().SingleAsync(service => service.Id == beard)).IsDeleted);
    Assert.Equal(Corte, (await verify.Services.AsNoTracking().SingleAsync(candidate => candidate.Id == rate)).GeneralServiceId);
  }

  [Fact]
  public async Task MovingARateFirst_MakesTheWaitingGeneralServiceDeletionAConflict()
  {
    if (!_postgres.IsAvailable) return;
    var beard = await SeedGeneralServiceAsync(Barberia, "Barba");
    var rate = await SeedRateAsync(Corte, "Barba clásica");

    var (move, delete) = await RaceAsync(
        Statement("services", "FOR UPDATE"),
        context => Rates(context).UpdateAsync(rate, new ServiceUpdateRequest("Barba clásica", 15000, 40, GeneralServiceId: beard)),
        context => Catalog(context).DeleteGeneralServiceAsync(beard));

    Assert.Null(move);
    Assert.IsType<ConflictException>(delete);
    await using var verify = CreateContext(null);
    Assert.False((await verify.GeneralServices.AsNoTracking().SingleAsync(service => service.Id == beard)).IsDeleted);
    Assert.Equal(beard, (await verify.Services.AsNoTracking().SingleAsync(candidate => candidate.Id == rate)).GeneralServiceId);
  }

  [Fact]
  public async Task DeactivatingTheGeneralServiceFirst_MakesTheWaitingRateReactivationAConflict()
  {
    if (!_postgres.IsAvailable) return;
    var beard = await SeedGeneralServiceAsync(Barberia, "Barba");
    var rate = await SeedRateAsync(beard, "Barba clásica", isActive: false);

    var (deactivate, reactivate) = await RaceAsync(
        Statement("general_services", "FOR UPDATE"),
        context => Catalog(context).UpdateGeneralServiceStatusAsync(beard, new GeneralServiceStatusUpdateRequest(false)),
        context => Rates(context).UpdateStatusAsync(rate, new ServiceStatusUpdateRequest(true)));

    Assert.Null(deactivate);
    Assert.IsType<ConflictException>(reactivate);
    await using var verify = CreateContext(null);
    Assert.False((await verify.GeneralServices.AsNoTracking().SingleAsync(service => service.Id == beard)).IsActive);
    Assert.False((await verify.Services.AsNoTracking().SingleAsync(candidate => candidate.Id == rate)).IsActive);
  }

  // ── Equivalent names ──────────────────────────────────────────────────────

  [Fact]
  public async Task TwoEquivalentGeneralServiceNames_InTheSameCategory_EndAsOneRowAndAConflict()
  {
    if (!_postgres.IsAvailable) return;
    await ResetAsync();

    var (first, second) = await RaceAsync(
        Statement("INSERT INTO public.general_services"),
        context => Catalog(context).CreateGeneralServiceAsync(new GeneralServiceCreateRequest(Barberia, "Barba")),
        context => Catalog(context).CreateGeneralServiceAsync(new GeneralServiceCreateRequest(Barberia, "  BARBA ")));

    Assert.Null(first);
    var conflict = Assert.IsType<ConflictException>(second);
    Assert.Equal("Ya existe un servicio general con ese nombre en esta categoría.", conflict.Message);
    await using var verify = CreateContext(null);
    Assert.Equal(["Barba"], await verify.GeneralServices.Where(service => service.NormalizedName == "BARBA").Select(service => service.Name).ToListAsync());
  }

  [Fact]
  public async Task ARenameAndACreation_ToTheSameName_EndAsOneRowAndAConflict()
  {
    if (!_postgres.IsAvailable) return;
    var beard = await SeedGeneralServiceAsync(Barberia, "Barba");

    var (rename, create) = await RaceAsync(
        Statement("UPDATE public.general_services"),
        context => Catalog(context).UpdateGeneralServiceAsync(beard, new GeneralServiceUpdateRequest(Barberia, "Bigote")),
        context => Catalog(context).CreateGeneralServiceAsync(new GeneralServiceCreateRequest(Barberia, "bigote")));

    Assert.Null(rename);
    Assert.IsType<ConflictException>(create);
    await using var verify = CreateContext(null);
    Assert.Equal([beard], await verify.GeneralServices.Where(service => service.NormalizedName == "BIGOTE").Select(service => service.Id).ToListAsync());
  }

  [Fact]
  public async Task TwoEquivalentCategoryNames_EndAsOneRowAndAConflict()
  {
    if (!_postgres.IsAvailable) return;
    await ResetAsync();

    var (first, second) = await RaceAsync(
        Statement("INSERT INTO public.service_categories"),
        context => Catalog(context).CreateCategoryAsync(new ServiceCategoryCreateRequest("Peluquería", 1)),
        context => Catalog(context).CreateCategoryAsync(new ServiceCategoryCreateRequest(" peluquería ", 2)));

    Assert.Null(first);
    var conflict = Assert.IsType<ConflictException>(second);
    Assert.Equal("Ya existe una categoría de servicios con ese nombre.", conflict.Message);
    await using var verify = CreateContext(null);
    Assert.Equal(1, await verify.ServiceCategories.CountAsync(category => category.NormalizedName == "PELUQUERÍA"));
  }

  [Fact]
  public async Task TheSameName_InTwoCategories_IsAllowedByTheDatabase()
  {
    if (!_postgres.IsAvailable) return;
    var hair = await SeedCategoryAsync("Peluquería");

    await using var context = CreateContext(null);
    await Catalog(context).CreateGeneralServiceAsync(new GeneralServiceCreateRequest(hair, "Corte"));

    Assert.Equal(2, await context.GeneralServices.CountAsync(service => service.NormalizedName == "CORTE"));
    // The public codes stay globally unique.
    var codes = await context.GeneralServices.Select(service => service.PublicCode).ToListAsync();
    Assert.Equal(codes.Count, codes.Distinct().Count());
    var duplicateCode = await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlInterpolatedAsync(
        $"UPDATE public.general_services SET \"PublicCode\" = {ServiceCatalogDefaults.CorteGeneralServiceCode} WHERE \"CategoryId\" = {hair}"));
    Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicateCode.SqlState);
  }

  // ── Helpers ───────────────────────────────────────────────────────────────

  private static Func<string, bool> Statement(params string[] fragments)
      => text => fragments.All(fragment => text.Contains(fragment, StringComparison.Ordinal));

  private async Task ResetAsync()
  {
    await using var context = CreateContext(null);
    await context.Database.EnsureDeletedAsync();
    await context.Database.MigrateAsync();
  }

  private async Task<Guid> SeedCategoryAsync(string name)
  {
    await ResetAsync();
    await using var context = CreateContext(null);
    return (await Catalog(context).CreateCategoryAsync(new ServiceCategoryCreateRequest(name, 1))).Id;
  }

  private async Task<Guid> SeedGeneralServiceAsync(Guid categoryId, string name, bool isActive = true)
  {
    if (categoryId == Barberia)
    {
      await ResetAsync();
    }

    await using var context = CreateContext(null);
    var created = await Catalog(context).CreateGeneralServiceAsync(new GeneralServiceCreateRequest(categoryId, name));
    if (!isActive)
    {
      await Catalog(context).UpdateGeneralServiceStatusAsync(created.Id, new GeneralServiceStatusUpdateRequest(false));
    }

    return created.Id;
  }

  private async Task<Guid> SeedRateAsync(Guid generalServiceId, string name, bool isActive = true)
  {
    await using var context = CreateContext(null);
    var created = await Rates(context).CreateAsync(new ServiceCreateRequest(name, 15000, 40, GeneralServiceId: generalServiceId));
    if (!isActive)
    {
      await Rates(context).UpdateStatusAsync(created.Id, new ServiceStatusUpdateRequest(false));
    }

    return created.Id;
  }

  private static ServiceCatalogManagementService Catalog(AppDbContext context) => new(context, TimeProvider.System);

  private static ServiceManagementService Rates(AppDbContext context) => new(context, TimeProvider.System, new NullCurrentUserAccessor());

  private async Task<(Exception? Holder, Exception? Contender)> RaceAsync(
      Func<string, bool> pauseAfter, Func<AppDbContext, Task> holder, Func<AppDbContext, Task> contender)
  {
    var gate = new StatementGate(pauseAfter);
    await using var holderContext = CreateContext(gate);
    await using var contenderContext = CreateContext(null);

    var holding = Task.Run(() => Capture(() => holder(holderContext)));
    await gate.Held.WaitAsync(Timeout);
    var contending = Task.Run(() => Capture(() => contender(contenderContext)));
    await WaitUntilASessionWaitsOnALockAsync();
    Assert.False(contending.IsCompleted);

    gate.Release();
    return (await holding.WaitAsync(Timeout), await contending.WaitAsync(Timeout));
  }

  private static async Task<Exception?> Capture(Func<Task> action)
  {
    try
    {
      await action();
      return null;
    }
    catch (Exception exception)
    {
      return exception;
    }
  }

  private async Task WaitUntilASessionWaitsOnALockAsync()
  {
    await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
    await connection.OpenAsync();
    var deadline = DateTime.UtcNow + Timeout;
    while (true)
    {
      await using var command = new NpgsqlCommand(
          "SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND datname = current_database()", connection);
      if ((long)(await command.ExecuteScalarAsync())! > 0)
      {
        return;
      }

      Assert.True(DateTime.UtcNow < deadline, "The second operation never waited on a lock.");
      await Task.Delay(50);
    }
  }

  private AppDbContext CreateContext(IInterceptor? interceptor)
  {
    var builder = new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql(_postgres.ConnectionString, npgsql => npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName));
    if (interceptor is not null)
    {
      builder.AddInterceptors(interceptor);
    }

    return new AppDbContext(builder.Options);
  }

  /// <summary>Holds the first command matching the predicate, after it ran (its locks taken), until released.</summary>
  private sealed class StatementGate(Func<string, bool> matches) : DbCommandInterceptor
  {
    private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Held => _held.Task;

    public void Release() => _released.TrySetResult();

    public override async ValueTask<int> NonQueryExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
      await HoldIfMatchingAsync(command, cancellationToken);
      return result;
    }

    public override async ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
      await HoldIfMatchingAsync(command, cancellationToken);
      return result;
    }

    private async Task HoldIfMatchingAsync(DbCommand command, CancellationToken cancellationToken)
    {
      if (matches(command.CommandText) && _held.TrySetResult())
      {
        await _released.Task.WaitAsync(Timeout, cancellationToken);
      }
    }
  }
}
