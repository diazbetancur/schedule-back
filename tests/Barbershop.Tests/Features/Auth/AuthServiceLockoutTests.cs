using Barbershop.Application.Auth;
using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Email;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Configuration;
using Barbershop.Infrastructure.Identity;
using Barbershop.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Barbershop.Tests.Features.Auth;

public sealed class AuthServiceLockoutTests : IDisposable
{
    private readonly AppDbContext _dbContext;
    private readonly AuthService _authService;
    private readonly ManualTimeProvider _timeProvider;

    public AuthServiceLockoutTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        _dbContext = new AppDbContext(options);

        var passwordHasher = new PasswordHasher<object>();
        var hostEnvironment = new TestHostEnvironment();
        _timeProvider = new ManualTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var seedService = new IdentitySeedService(
            _dbContext,
            passwordHasher,
            Options.Create(new SeedAdminOptions()),
            hostEnvironment,
            _timeProvider);

        _authService = new AuthService(
            _dbContext,
            passwordHasher,
            seedService,
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
            _timeProvider);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
    }

    [Fact]
    public async Task Login_LocksAccount_After5ConsecutiveFailures()
    {
        await _authService.RegisterAsync(new RegisterRequest("Lockout User", "lockout@example.com", "Secret123!", null));

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await Assert.ThrowsAsync<UnauthorizedException>(() =>
                _authService.LoginAsync(new LoginRequest("lockout@example.com", "WrongPassword!")));
        }

        // 6th attempt, even with the CORRECT password, must be rejected because the account is locked.
        await Assert.ThrowsAsync<TooManyRequestsException>(() =>
            _authService.LoginAsync(new LoginRequest("lockout@example.com", "Secret123!")));
    }

    [Fact]
    public async Task Login_LockedAccount_ReturnsSpanishMessage()
    {
        await _authService.RegisterAsync(new RegisterRequest("Lockout Message User", "lockout-message@example.com", "Secret123!", null));

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await Assert.ThrowsAsync<UnauthorizedException>(() =>
                _authService.LoginAsync(new LoginRequest("lockout-message@example.com", "WrongPassword!")));
        }

        var exception = await Assert.ThrowsAsync<TooManyRequestsException>(() =>
            _authService.LoginAsync(new LoginRequest("lockout-message@example.com", "Secret123!")));

        Assert.Equal("Demasiados intentos fallidos. Intenta de nuevo más tarde.", exception.Message);
    }

    [Fact]
    public async Task Login_SuccessfulLogin_ResetsFailedAttemptCounter()
    {
        await _authService.RegisterAsync(new RegisterRequest("Reset Counter User", "reset-counter@example.com", "Secret123!", null));

        for (var attempt = 0; attempt < 4; attempt++)
        {
            await Assert.ThrowsAsync<UnauthorizedException>(() =>
                _authService.LoginAsync(new LoginRequest("reset-counter@example.com", "WrongPassword!")));
        }

        // Successful login should reset the counter back to zero.
        await _authService.LoginAsync(new LoginRequest("reset-counter@example.com", "Secret123!"));

        // 4 more failures should still not lock the account, since the counter was reset.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            await Assert.ThrowsAsync<UnauthorizedException>(() =>
                _authService.LoginAsync(new LoginRequest("reset-counter@example.com", "WrongPassword!")));
        }

        // A correct login should still work (account not locked).
        var response = await _authService.LoginAsync(new LoginRequest("reset-counter@example.com", "Secret123!"));
        Assert.False(string.IsNullOrWhiteSpace(response.AccessToken));
    }

    [Fact]
    public async Task Login_LockoutExpires_AfterConfiguredDuration()
    {
        await _authService.RegisterAsync(new RegisterRequest("Expiry User", "expiry@example.com", "Secret123!", null));

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await Assert.ThrowsAsync<UnauthorizedException>(() =>
                _authService.LoginAsync(new LoginRequest("expiry@example.com", "WrongPassword!")));
        }

        await Assert.ThrowsAsync<TooManyRequestsException>(() =>
            _authService.LoginAsync(new LoginRequest("expiry@example.com", "Secret123!")));

        // Advance time past the 15-minute lockout window.
        _timeProvider.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(1));

        var response = await _authService.LoginAsync(new LoginRequest("expiry@example.com", "Secret123!"));
        Assert.False(string.IsNullOrWhiteSpace(response.AccessToken));
    }

    [Fact]
    public async Task Login_WrongPassword_DoesNotRevealLockoutState()
    {
        await _authService.RegisterAsync(new RegisterRequest("Generic Message User", "generic-message@example.com", "Secret123!", null));

        var exception = await Assert.ThrowsAsync<UnauthorizedException>(() =>
            _authService.LoginAsync(new LoginRequest("generic-message@example.com", "WrongPassword!")));

        Assert.Equal("Invalid email or password.", exception.Message);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public ManualTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow += delta;
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
