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

        // Roles and permissions are bootstrapped at startup in the app, not by AuthService.
        seedService.EnsureSeededAsync().GetAwaiter().GetResult();
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

        Assert.Equal("Ya existe una cuenta con ese correo.", exception.Message);
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

    [Fact]
    public async Task Register_WithEmailOfActiveAccount_LeavesTheAccountUntouched()
    {
        var original = await _authService.RegisterAsync(new RegisterRequest("Original Owner", "owner@example.com", "Secret123!", "3001112233"));
        var before = await _dbContext.Users.AsNoTracking().SingleAsync();

        await Assert.ThrowsAsync<Barbershop.Application.Common.Exceptions.ConflictException>(() =>
            _authService.RegisterAsync(new RegisterRequest("Intruder", "OWNER@example.com", "Intruder123!", "3009998877")));

        var after = await _dbContext.Users.AsNoTracking().SingleAsync();
        Assert.Equal(original.User.Id, after.Id);
        Assert.Equal(before.PasswordHash, after.PasswordHash);
        Assert.Equal("Original Owner", after.FullName);
        Assert.Equal("3001112233", after.PhoneNumber);
        Assert.True(after.IsActive);
        Assert.Equal(1, await _dbContext.RefreshTokens.CountAsync());

        await _authService.LoginAsync(new LoginRequest("owner@example.com", "Secret123!"));
        await Assert.ThrowsAsync<Barbershop.Application.Common.Exceptions.UnauthorizedException>(() =>
            _authService.LoginAsync(new LoginRequest("owner@example.com", "Intruder123!")));
    }

    [Fact]
    public async Task Register_WithEmailOfInactiveAccount_IsRejectedLikeAnActiveOne()
    {
        await RegisterAndDeactivateAsync("Inactive Owner", "inactive@example.com", "Secret123!");

        var exception = await Assert.ThrowsAsync<Barbershop.Application.Common.Exceptions.ConflictException>(() =>
            _authService.RegisterAsync(new RegisterRequest("Someone Else", "INACTIVE@example.com", "Other123!", null)));

        // Same message as for an active account, so the response does not reveal that the account is inactive.
        Assert.Equal("Ya existe una cuenta con ese correo.", exception.Message);
        Assert.Equal(1, await _dbContext.Users.CountAsync());
    }

    [Fact]
    public async Task Register_WithEmailOfInactiveAccount_DoesNotReactivateItOrChangeItsPassword()
    {
        var userId = await RegisterAndDeactivateAsync("Inactive Owner", "dormant@example.com", "Secret123!", "3001112233");
        var before = await _dbContext.Users.AsNoTracking().SingleAsync(user => user.Id == userId);
        var refreshTokensBefore = await _dbContext.RefreshTokens.CountAsync();

        await Assert.ThrowsAsync<Barbershop.Application.Common.Exceptions.ConflictException>(() =>
            _authService.RegisterAsync(new RegisterRequest("Intruder", "dormant@example.com", "Intruder123!", "3009998877")));

        var after = await _dbContext.Users.AsNoTracking().SingleAsync(user => user.Id == userId);
        Assert.False(after.IsActive);
        Assert.Equal(before.PasswordHash, after.PasswordHash);
        Assert.Equal("Inactive Owner", after.FullName);
        Assert.Equal("3001112233", after.PhoneNumber);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Equal(refreshTokensBefore, await _dbContext.RefreshTokens.CountAsync());

        await Assert.ThrowsAsync<Barbershop.Application.Common.Exceptions.UnauthorizedException>(() =>
            _authService.LoginAsync(new LoginRequest("dormant@example.com", "Intruder123!")));
    }

    [Fact]
    public async Task Register_WithEmailOfInactiveAccount_KeepsTheLinkedCustomerProfile()
    {
        var userId = await RegisterAndDeactivateAsync("Linked Owner", "linked@example.com", "Secret123!");
        var customer = new Barbershop.Domain.Customers.CustomerProfile("Linked Owner", "3001112233", "linked@example.com", Guid.NewGuid(), DateTime.UtcNow);
        customer.LinkAccount(userId, Guid.NewGuid(), DateTime.UtcNow);
        _dbContext.Customers.Add(customer);
        await _dbContext.SaveChangesAsync();

        await Assert.ThrowsAsync<Barbershop.Application.Common.Exceptions.ConflictException>(() =>
            _authService.RegisterAsync(new RegisterRequest("Intruder", "linked@example.com", "Intruder123!", null)));

        var stored = await _dbContext.Customers.AsNoTracking().SingleAsync();
        Assert.Equal(userId, stored.UserId);
        Assert.Equal(1, await _dbContext.Users.CountAsync());
    }

    [Fact]
    public async Task Register_PlainCustomer_ExposesNoPermissions()
    {
        var registration = await _authService.RegisterAsync(new RegisterRequest("Plain Customer", "plain-perms@example.com", "Secret123!", null));

        Assert.Empty(registration.User.Permissions);
        Assert.Empty((await _authService.GetCurrentUserAsync(registration.User.Id)).Permissions);
    }

    [Fact]
    public async Task Login_Refresh_AndCurrentUser_ExposeTheSamePermissionsAsTheToken()
    {
        var registration = await _authService.RegisterAsync(new RegisterRequest("Loyalty Manager", "loyalty-manager@example.com", "Secret123!", null));

        var settingsPermission = await _dbContext.Permissions.SingleAsync(p => p.Code == PermissionCodes.LoyaltySettingsManage);
        var managerRole = new Role("Fidelización");
        managerRole.AddPermission(settingsPermission.Id, DateTime.UtcNow);
        _dbContext.Roles.Add(managerRole);
        _dbContext.UserRoles.Add(new UserRole(registration.User.Id, managerRole.Id, DateTime.UtcNow));
        await _dbContext.SaveChangesAsync();

        var login = await _authService.LoginAsync(new LoginRequest("loyalty-manager@example.com", "Secret123!"));
        var refreshed = await _authService.RefreshAsync(new RefreshRequest(login.RefreshToken));
        var current = await _authService.GetCurrentUserAsync(registration.User.Id);

        var tokenPermissions = new JwtSecurityTokenHandler().ReadJwtToken(login.AccessToken).Claims
            .Where(c => c.Type == PermissionClaimTypes.Permission)
            .Select(c => c.Value)
            .ToArray();
        Assert.Equal([PermissionCodes.LoyaltySettingsManage], tokenPermissions);
        Assert.Equal([PermissionCodes.LoyaltySettingsManage], login.User.Permissions);
        Assert.Equal([PermissionCodes.LoyaltySettingsManage], refreshed.User.Permissions);
        Assert.Equal([PermissionCodes.LoyaltySettingsManage], current.Permissions);
    }

    [Fact]
    public async Task Login_AdminUser_ExposesEveryPermissionInCatalog()
    {
        var registration = await _authService.RegisterAsync(new RegisterRequest("Admin User", "admin-exposed@example.com", "Secret123!", null));

        var adminRole = await _dbContext.Roles.SingleAsync(r => r.NormalizedName == RoleNames.Admin.ToUpperInvariant());
        _dbContext.UserRoles.Add(new UserRole(registration.User.Id, adminRole.Id, DateTime.UtcNow));
        await _dbContext.SaveChangesAsync();

        var login = await _authService.LoginAsync(new LoginRequest("admin-exposed@example.com", "Secret123!"));

        Assert.Equal(PermissionCodes.All.OrderBy(c => c, StringComparer.Ordinal), login.User.Permissions);
    }

    [Fact]
    public async Task Login_StaffUser_GetsOnlyServiceRecordRegistration()
    {
        var registration = await _authService.RegisterAsync(new RegisterRequest("Barbero", "barbero-perms@example.com", "Secret123!", null));

        var staffRole = await _dbContext.Roles.SingleAsync(r => r.NormalizedName == RoleNames.Staff.ToUpperInvariant());
        _dbContext.UserRoles.Add(new UserRole(registration.User.Id, staffRole.Id, DateTime.UtcNow));
        await _dbContext.SaveChangesAsync();

        var login = await _authService.LoginAsync(new LoginRequest("barbero-perms@example.com", "Secret123!"));

        var tokenPermissions = new JwtSecurityTokenHandler().ReadJwtToken(login.AccessToken).Claims
            .Where(c => c.Type == PermissionClaimTypes.Permission)
            .Select(c => c.Value)
            .ToArray();
        Assert.Equal([PermissionCodes.ServiceRecordsRegister], tokenPermissions);
        Assert.Equal([PermissionCodes.ServiceRecordsRegister], login.User.Permissions);
    }

    private async Task<Guid> RegisterAndDeactivateAsync(string fullName, string email, string password, string? phone = null)
    {
        var registration = await _authService.RegisterAsync(new RegisterRequest(fullName, email, password, phone));
        var user = await _dbContext.Users.SingleAsync(candidate => candidate.Id == registration.User.Id);
        user.Deactivate(DateTime.UtcNow);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        return user.Id;
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
