using System.Data.Common;
using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Customers.Admin;
using Barbershop.Application.Customers.Claims;
using Barbershop.Domain.Customers;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Customers;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Barbershop.Tests.Features.Customers;

/// <summary>
/// The race between giving an account a profile (own profile, admin link) and a pending claim, forced deterministically
/// against real PostgreSQL: the first operation is held right after it takes the account lock, the second is started
/// and observed waiting on that lock in pg_stat_activity, then the first is released. Whatever the order, the second
/// sees the first's result and is refused, so an account never ends with a live profile and a pending claim.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "CustomerClaimsAccountLockPostgres")]
public sealed class CustomerClaimsAccountLockPostgresTests
{
  private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

  private readonly PostgresContainerFixture _postgres;

  public CustomerClaimsAccountLockPostgresTests(PostgresContainerFixture postgres) => _postgres = postgres;

  public enum Operation
  {
    Claim,
    OwnProfile,
    AdminLink
  }

  [Theory]
  [InlineData(Operation.Claim, Operation.OwnProfile, CustomerManagementService.OwnProfilePendingClaimMessage)]
  [InlineData(Operation.OwnProfile, Operation.Claim, "Tu cuenta ya tiene un perfil de cliente.")]
  [InlineData(Operation.Claim, Operation.AdminLink, "La cuenta tiene una solicitud de vinculación pendiente. Resuélvela en la bandeja de reclamaciones.")]
  [InlineData(Operation.AdminLink, Operation.Claim, "Tu cuenta ya tiene un perfil de cliente.")]
  public async Task TheSecondOperation_WaitsForTheAccountLock_AndIsRefusedByTheFirstsResult(
      Operation first, Operation second, string expectedConflict)
  {
    if (!_postgres.IsAvailable) return; // Only with PostgresContainerFixture.AllowSkipVariable set on purpose.

    await ResetDatabaseAsync();
    var (customerId, adminId, historicalId) = await SeedAsync();

    var gate = new AccountLockGate();
    await using var holderContext = CreateContext(gate);
    await using var contenderContext = CreateContext(null);

    var holder = Task.Run(() => RunAsync(holderContext, first, customerId, adminId, historicalId));
    await gate.Locked.WaitAsync(Timeout);

    var contender = Task.Run(() => RunAsync(contenderContext, second, customerId, adminId, historicalId));
    await WaitUntilAWaiterOnTheAccountLockAsync();
    Assert.False(contender.IsCompleted);

    gate.Release();
    await holder.WaitAsync(Timeout);
    var refused = await Assert.ThrowsAsync<ConflictException>(() => contender.WaitAsync(Timeout));
    Assert.Equal(expectedConflict, refused.Message);

    await using var verify = CreateContext(null);
    var linked = await verify.Customers.AsNoTracking()
        .Where(customer => customer.UserId == customerId && customer.MergedIntoCustomerId == null).ToListAsync();
    var pending = await verify.CustomerClaims.AsNoTracking()
        .CountAsync(claim => claim.UserId == customerId && claim.Status == CustomerClaimStatus.Pending);
    Assert.False(linked.Count > 0 && pending > 0, "The account ended with a live profile and a pending claim.");

    switch (first)
    {
      case Operation.Claim:
        Assert.Equal((0, 1), (linked.Count, pending));
        break;
      case Operation.OwnProfile:
        Assert.Equal((1, 0), (linked.Count, pending));
        Assert.NotEqual(historicalId, linked[0].Id);
        break;
      case Operation.AdminLink:
        Assert.Equal((historicalId, 0), (Assert.Single(linked).Id, pending));
        break;
    }
  }

  private static async Task RunAsync(AppDbContext dbContext, Operation operation, Guid customerId, Guid adminId, Guid historicalId)
  {
    var customers = new CustomerManagementService(dbContext, TimeProvider.System);
    var claims = new CustomerClaimsService(dbContext, customers, TimeProvider.System);
    switch (operation)
    {
      case Operation.Claim:
        await claims.SubmitClaimAsync(customerId,
            new CustomerClaimCreateRequest("Carla Cliente", "3001234567", "carla@example.com", null, Guid.NewGuid()));
        break;
      case Operation.OwnProfile:
        await claims.CreateOwnProfileAsync(customerId, new OwnCustomerProfileCreateRequest("Carla Cliente", "3001234567", "carla@example.com"));
        break;
      case Operation.AdminLink:
        await customers.LinkAccountAsync(adminId, historicalId, new CustomerLinkAccountRequest(customerId, true));
        break;
    }
  }

  /// <summary>Polls until a session waits on a row lock taken FOR UPDATE: the contender is blocked by the holder.</summary>
  private async Task WaitUntilAWaiterOnTheAccountLockAsync()
  {
    await using var connection = new NpgsqlConnection(_postgres.ConnectionString);
    await connection.OpenAsync();
    var deadline = DateTime.UtcNow + Timeout;
    while (true)
    {
      await using var command = new NpgsqlCommand(
          "SELECT count(*) FROM pg_stat_activity WHERE wait_event_type = 'Lock' AND query LIKE '%FROM public.users%FOR UPDATE%'",
          connection);
      if ((long)(await command.ExecuteScalarAsync())! > 0)
      {
        return;
      }

      Assert.True(DateTime.UtcNow < deadline, "The second operation never waited on the account lock.");
      await Task.Delay(50);
    }
  }

  private async Task ResetDatabaseAsync()
  {
    await using var dbContext = CreateContext(null);
    await dbContext.Database.EnsureDeletedAsync();
    await dbContext.Database.MigrateAsync();
  }

  private async Task<(Guid CustomerId, Guid AdminId, Guid HistoricalId)> SeedAsync()
  {
    await using var dbContext = CreateContext(null);
    var customerRole = await dbContext.Roles.SingleOrDefaultAsync(role => role.NormalizedName == RoleNames.Customer.ToUpperInvariant());
    if (customerRole is null)
    {
      customerRole = new Role(RoleNames.Customer, isSystemRole: true);
      dbContext.Roles.Add(customerRole);
    }

    var customer = new User("Carla Cliente", "carla@example.com", "hash", DateTime.UtcNow);
    customer.UserRoles.Add(new UserRole(customer.Id, customerRole.Id, DateTime.UtcNow));
    var admin = new User("Admin Principal", "admin@example.com", "hash", DateTime.UtcNow);
    dbContext.Users.AddRange(customer, admin);
    await dbContext.SaveChangesAsync();

    var historical = await new CustomerManagementService(dbContext, TimeProvider.System)
        .CreateAsync(admin.Id, new CustomerCreateRequest("Carla Histórica", "3001234567", "historica@example.com"));
    return (customer.Id, admin.Id, historical.Id);
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

  /// <summary>Holds the first account lock taken through its context until <see cref="Release"/>.</summary>
  private sealed class AccountLockGate : DbCommandInterceptor
  {
    private readonly TaskCompletionSource _locked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Locked => _locked.Task;

    public void Release() => _released.TrySetResult();

    public override async ValueTask<int> NonQueryExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
      if (command.CommandText.Contains("FROM public.users", StringComparison.Ordinal)
          && command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal)
          && _locked.TrySetResult())
      {
        await _released.Task.WaitAsync(Timeout, cancellationToken);
      }

      return result;
    }
  }
}
