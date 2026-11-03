using Barbershop.Application.Auth;
using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Email;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Configuration;
using Barbershop.Infrastructure.Identity;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace Barbershop.Tests.Features.Auth;

/// <summary>
/// Reproduces the race between two registrations of the same email deterministically against real PostgreSQL:
/// the competing account is inserted after the "is the email taken?" check passed and right before the save,
/// so only the unique index on users."NormalizedEmail" can stop the second one.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "RegisterEmailRacePostgres")]
public sealed class RegisterEmailRacePostgresTests
{
  private readonly PostgresContainerFixture _postgres;
  private readonly ITestOutputHelper _output;

  public RegisterEmailRacePostgresTests(PostgresContainerFixture postgres, ITestOutputHelper output)
  {
    _postgres = postgres;
    _output = output;
  }

  [Theory]
  [InlineData(true)]
  [InlineData(false)]
  public async Task Register_LosingTheRaceOnTheUniqueIndex_ReturnsTheUsualConflict(bool competitorIsActive)
  {
    if (!_postgres.IsAvailable)
    {
      _output.WriteLine(_postgres.UnavailableReason ?? "PostgreSQL Testcontainer is unavailable.");
      return;
    }

    await ResetDatabaseAsync();
    var interceptor = new CompetingRegistrationInterceptor(CreateContext, competitorIsActive);
    await using var dbContext = CreateContext(interceptor);
    var authService = CreateAuthService(dbContext);

    var exception = await Assert.ThrowsAsync<ConflictException>(() =>
        authService.RegisterAsync(new RegisterRequest("Second Person", "Race@Example.com", "Second123!", "3009998877")));

    Assert.True(interceptor.Fired);
    // Same message as the regular duplicate check, whatever the state of the account that won.
    Assert.Equal("A user with this email already exists.", exception.Message);

    await using var verify = CreateContext();
    var users = await verify.Users.AsNoTracking().Where(user => user.NormalizedEmail == "RACE@EXAMPLE.COM").ToListAsync();
    var winner = Assert.Single(users);
    Assert.Equal(interceptor.CompetitorId, winner.Id);
    Assert.Equal("First Person", winner.FullName);
    Assert.Equal(CompetingRegistrationInterceptor.CompetitorPasswordHash, winner.PasswordHash);
    Assert.Equal(competitorIsActive, winner.IsActive);
    Assert.Equal(0, await verify.RefreshTokens.CountAsync());
  }

  private async Task ResetDatabaseAsync()
  {
    await using var dbContext = CreateContext();
    await dbContext.Database.EnsureDeletedAsync();
    await dbContext.Database.MigrateAsync();
    // The app bootstraps roles at startup; registration needs the Customer role.
    await new IdentitySeedService(
        dbContext, new PasswordHasher<object>(), Options.Create(new SeedAdminOptions()), new TestHostEnvironment(), TimeProvider.System)
        .EnsureSeededAsync();
  }

  private AppDbContext CreateContext() => CreateContext(null);

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

  private static AuthService CreateAuthService(AppDbContext dbContext)
  {
    var passwordHasher = new PasswordHasher<object>();
    return new AuthService(
        dbContext,
        passwordHasher,
        new NoOpEmailSender(),
        Options.Create(new JwtOptions
        {
          Enabled = true,
          Issuer = "Barbershop.Tests",
          Audience = "Barbershop.Tests.Client",
          SigningKey = "12345678901234567890123456789012-auth-tests-key",
          AccessTokenMinutes = 60,
          RefreshTokenDays = 7,
          RequireHttpsMetadata = false
        }),
        Options.Create(new AppOptions()),
        TimeProvider.System);
  }

  /// <summary>Just before the registration's save, commits another account with the same email on a separate connection.</summary>
  private sealed class CompetingRegistrationInterceptor : SaveChangesInterceptor
  {
    public const string CompetitorPasswordHash = "competitor-password-hash";

    private readonly Func<AppDbContext> _createContext;
    private readonly bool _competitorIsActive;

    public CompetingRegistrationInterceptor(Func<AppDbContext> createContext, bool competitorIsActive)
    {
      _createContext = createContext;
      _competitorIsActive = competitorIsActive;
    }

    public bool Fired { get; private set; }
    public Guid CompetitorId { get; private set; }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
      // The seed also saves through this context; only the registration adds a user.
      var registersUser = eventData.Context!.ChangeTracker.Entries<User>().Any(entry => entry.State == EntityState.Added);
      if (!Fired && registersUser)
      {
        Fired = true;
        await using var competitor = _createContext();
        var customerRole = await competitor.Roles.SingleAsync(role => role.NormalizedName == RoleNames.Customer.ToUpperInvariant(), cancellationToken);
        var user = new User("First Person", "race@example.com", CompetitorPasswordHash, DateTime.UtcNow);
        user.UserRoles.Add(new UserRole(user.Id, customerRole.Id, DateTime.UtcNow));
        if (!_competitorIsActive)
        {
          user.Deactivate(DateTime.UtcNow);
        }

        competitor.Users.Add(user);
        await competitor.SaveChangesAsync(cancellationToken);
        CompetitorId = user.Id;
      }

      return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }
  }

  private sealed class TestHostEnvironment : IHostEnvironment
  {
    public string EnvironmentName { get; set; } = "Testing";
    public string ApplicationName { get; set; } = "Barbershop.Tests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(AppContext.BaseDirectory);
  }

  private sealed class NoOpEmailSender : IEmailSender
  {
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
  }
}
