using System.IdentityModel.Tokens.Jwt;
using Barbershop.Application.Auth;
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

public sealed class AuthServiceTests : IDisposable
{
    private readonly AppDbContext _dbContext;
    private readonly AuthService _authService;

    public AuthServiceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        _dbContext = new AppDbContext(options);

        var passwordHasher = new PasswordHasher<object>();
        var hostEnvironment = new TestHostEnvironment();
        var timeProvider = TimeProvider.System;
        var seedService = new IdentitySeedService(
            _dbContext,
            passwordHasher,
            Options.Create(new SeedAdminOptions()),
            hostEnvironment,
            timeProvider);

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
            timeProvider);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
    }

    [Fact]
    public async Task Register_CreatesCustomerUser_ByDefault()
    {
        var response = await _authService.RegisterAsync(new RegisterRequest("Alex Diaz", "alex@example.com", "Secret123!", "+123456789"));

        Assert.False(string.IsNullOrWhiteSpace(response.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(response.RefreshToken));
        Assert.Contains(RoleNames.Customer, response.User.Roles);
        Assert.Equal("Alex Diaz", response.User.FullName);
        Assert.Equal("alex@example.com", response.User.Email);
        Assert.Equal("+123456789", response.User.PhoneNumber);

        var user = await _dbContext.Users.SingleAsync();
        Assert.True(user.IsActive);
        Assert.NotEqual("Secret123!", user.PasswordHash);
        Assert.Equal(3, await _dbContext.Roles.CountAsync());
        Assert.Single(await _dbContext.UserRoles.ToListAsync());
    }

    [Fact]
    public async Task Register_RejectsDuplicateEmail()
    {
        await _authService.RegisterAsync(new RegisterRequest("First User", "duplicate@example.com", "Secret123!", null));

        var exception = await Assert.ThrowsAsync<Barbershop.Application.Common.Exceptions.ConflictException>(() =>
            _authService.RegisterAsync(new RegisterRequest("Second User", "DUPLICATE@example.com", "Secret123!", null)));

        Assert.Equal("A user with this email already exists.", exception.Message);
    }

    [Fact]
    public async Task Login_Succeeds_WithValidCredentials()
    {
        await _authService.RegisterAsync(new RegisterRequest("Login User", "login@example.com", "Secret123!", null));

        var response = await _authService.LoginAsync(new LoginRequest("login@example.com", "Secret123!"));

        Assert.False(string.IsNullOrWhiteSpace(response.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(response.RefreshToken));
        Assert.Contains(RoleNames.Customer, response.User.Roles);
    }

    [Fact]
    public async Task Login_Rejects_InvalidPassword()
    {
        await _authService.RegisterAsync(new RegisterRequest("Wrong Password User", "wrong-password@example.com", "Secret123!", null));

        await Assert.ThrowsAsync<Barbershop.Application.Common.Exceptions.UnauthorizedException>(() =>
            _authService.LoginAsync(new LoginRequest("wrong-password@example.com", "WrongPassword!")));
    }

    [Fact]
    public async Task GetCurrentUser_ReturnsCurrentUserState()
    {
        var registration = await _authService.RegisterAsync(new RegisterRequest("Current User", "me@example.com", "Secret123!", null));

        var currentUser = await _authService.GetCurrentUserAsync(registration.User.Id);

        Assert.Equal(registration.User.Id, currentUser.Id);
        Assert.Equal("Current User", currentUser.FullName);
        Assert.Contains(RoleNames.Customer, currentUser.Roles);
    }

    [Fact]
    public async Task Refresh_RotatesRefreshToken()
    {
        var registration = await _authService.RegisterAsync(new RegisterRequest("Refresh User", "refresh@example.com", "Secret123!", null));

        var refreshed = await _authService.RefreshAsync(new RefreshRequest(registration.RefreshToken));

        Assert.NotEqual(registration.RefreshToken, refreshed.RefreshToken);
        Assert.Equal(2, await _dbContext.RefreshTokens.CountAsync());
        Assert.Equal(1, await _dbContext.RefreshTokens.CountAsync(token => token.RevokedAt != null));
    }

    [Fact]
    public async Task Logout_IsIdempotent_AndRevokesRefreshToken()
    {
        var registration = await _authService.RegisterAsync(new RegisterRequest("Logout User", "logout@example.com", "Secret123!", null));

        await _authService.LogoutAsync(registration.User.Id, new LogoutRequest(registration.RefreshToken));
        await _authService.LogoutAsync(registration.User.Id, new LogoutRequest(registration.RefreshToken));

        Assert.Equal(1, await _dbContext.RefreshTokens.CountAsync(token => token.RevokedAt != null));
    }

    [Fact]
    public async Task RoleSeed_DoesNotDuplicateRoles()
    {
        await _authService.RegisterAsync(new RegisterRequest("First User", "roles-1@example.com", "Secret123!", null));
        await _authService.RegisterAsync(new RegisterRequest("Second User", "roles-2@example.com", "Secret123!", null));

        Assert.Equal(3, await _dbContext.Roles.CountAsync());
    }

    [Fact]
    public async Task Login_IncludesPermissionClaim_ForUserWithCustomRolePermission()
    {
        var registration = await _authService.RegisterAsync(new RegisterRequest("Vendedor User", "vendedor@example.com", "Secret123!", null));

        var salesPermission = await _dbContext.Permissions.SingleAsync(p => p.Code == PermissionCodes.SalesRegister);
        var sellerRole = new Role("Vendedor");
        sellerRole.AddPermission(salesPermission.Id, DateTime.UtcNow);
        _dbContext.Roles.Add(sellerRole);
        _dbContext.UserRoles.Add(new UserRole(registration.User.Id, sellerRole.Id, DateTime.UtcNow));
        await _dbContext.SaveChangesAsync();

        var login = await _authService.LoginAsync(new LoginRequest("vendedor@example.com", "Secret123!"));

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(login.AccessToken);
        var permissionClaims = jwt.Claims.Where(c => c.Type == PermissionClaimTypes.Permission).Select(c => c.Value).ToList();
        Assert.Contains(PermissionCodes.SalesRegister, permissionClaims);
    }

    [Fact]
    public async Task Login_AdminUser_IncludesEveryPermissionInCatalog()
    {
        var registration = await _authService.RegisterAsync(new RegisterRequest("Admin User", "admin-perm@example.com", "Secret123!", null));

        var adminRole = await _dbContext.Roles.SingleAsync(r => r.NormalizedName == RoleNames.Admin.ToUpperInvariant());
        _dbContext.UserRoles.Add(new UserRole(registration.User.Id, adminRole.Id, DateTime.UtcNow));
        await _dbContext.SaveChangesAsync();

        var login = await _authService.LoginAsync(new LoginRequest("admin-perm@example.com", "Secret123!"));

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(login.AccessToken);
        var permissionClaims = jwt.Claims.Where(c => c.Type == PermissionClaimTypes.Permission).Select(c => c.Value).ToList();
        Assert.Equal(PermissionCodes.All.OrderBy(c => c), permissionClaims.OrderBy(c => c));
    }

    [Fact]
    public async Task Register_PlainCustomer_HasNoPermissionClaims()
    {
        var registration = await _authService.RegisterAsync(new RegisterRequest("Plain Customer", "plain@example.com", "Secret123!", null));

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(registration.AccessToken);
        Assert.DoesNotContain(jwt.Claims, c => c.Type == PermissionClaimTypes.Permission);
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
