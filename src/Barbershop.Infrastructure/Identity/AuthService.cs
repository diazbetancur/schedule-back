using System.IdentityModel.Tokens.Jwt;
using System.Net.Mail;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Barbershop.Application.Auth;
using Barbershop.Application.Common.Exceptions;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Configuration;
using Barbershop.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Barbershop.Infrastructure.Identity;

internal sealed class AuthService : IAuthService
{
    private readonly AppDbContext _dbContext;
    private readonly IPasswordHasher<object> _passwordHasher;
    private readonly IIdentitySeedService _identitySeedService;
    private readonly IOptions<JwtOptions> _jwtOptions;
    private readonly TimeProvider _timeProvider;

    public AuthService(
        AppDbContext dbContext,
        IPasswordHasher<object> passwordHasher,
        IIdentitySeedService identitySeedService,
        IOptions<JwtOptions> jwtOptions,
        TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _passwordHasher = passwordHasher;
        _identitySeedService = identitySeedService;
        _jwtOptions = jwtOptions;
        _timeProvider = timeProvider;
    }

    public async Task<TokenResponse> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRegisterRequest(request);
        EnsureJwtConfigured();

        await _identitySeedService.EnsureSeededAsync(cancellationToken);

        var normalizedEmail = NormalizeEmail(request.Email);
        var emailExists = await _dbContext.Users.AnyAsync(user => user.NormalizedEmail == normalizedEmail, cancellationToken);
        if (emailExists)
        {
            throw new ConflictException("A user with this email already exists.");
        }

        var createdAt = _timeProvider.GetUtcNow().UtcDateTime;
        var passwordHash = _passwordHasher.HashPassword(new object(), request.Password);
        var user = new User(request.FullName, request.Email, passwordHash, createdAt, request.PhoneNumber);

        var customerRole = await _dbContext.Roles.SingleAsync(role => role.NormalizedName == RoleNames.Customer.ToUpperInvariant(), cancellationToken);
        user.UserRoles.Add(new UserRole(user.Id, customerRole.Id, createdAt));

        var refreshToken = CreateRefreshToken(user.Id, createdAt);
        user.RefreshTokens.Add(refreshToken.Entity);

        _dbContext.Users.Add(user);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return CreateTokenResponse(user, [RoleNames.Customer], refreshToken.RawToken, null);
    }

    public async Task<TokenResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        ValidateLoginRequest(request);
        EnsureJwtConfigured();

        await _identitySeedService.EnsureSeededAsync(cancellationToken);

        var normalizedEmail = NormalizeEmail(request.Email);
        var user = await _dbContext.Users
            .Include(candidate => candidate.UserRoles)
            .ThenInclude(userRole => userRole.Role)
            .SingleOrDefaultAsync(candidate => candidate.NormalizedEmail == normalizedEmail, cancellationToken);

        if (user is null || !user.IsActive)
        {
            throw new UnauthorizedException("Invalid email or password.");
        }

        var verification = _passwordHasher.VerifyHashedPassword(new object(), user.PasswordHash, request.Password);
        if (verification == PasswordVerificationResult.Failed)
        {
            throw new UnauthorizedException("Invalid email or password.");
        }

        var issuedAt = _timeProvider.GetUtcNow().UtcDateTime;
        var refreshToken = CreateRefreshToken(user.Id, issuedAt);

        _dbContext.RefreshTokens.Add(refreshToken.Entity);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var roles = GetRoleNames(user);
        var profilePhotoUrl = await GetProfilePhotoUrlAsync(user.ProfilePhotoMediaAssetId, cancellationToken);

        return CreateTokenResponse(user, roles, refreshToken.RawToken, profilePhotoUrl);
    }

    public async Task<TokenResponse> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRefreshRequest(request);
        EnsureJwtConfigured();

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var refreshTokenHash = ComputeTokenHash(request.RefreshToken);

        var existingToken = await _dbContext.RefreshTokens
            .Include(token => token.User)
            .ThenInclude(user => user.UserRoles)
            .ThenInclude(userRole => userRole.Role)
            .SingleOrDefaultAsync(token => token.TokenHash == refreshTokenHash, cancellationToken);

        if (existingToken is null
            || existingToken.ReplacedByRefreshTokenId is not null
            || !existingToken.IsActive(utcNow)
            || !existingToken.User.IsActive)
        {
            throw new UnauthorizedException("The refresh token is invalid or expired.");
        }

        var rotatedToken = CreateRefreshToken(existingToken.UserId, utcNow);
        existingToken.Revoke(utcNow, rotatedToken.Entity.Id);

        _dbContext.RefreshTokens.Add(rotatedToken.Entity);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var roles = GetRoleNames(existingToken.User);
        var profilePhotoUrl = await GetProfilePhotoUrlAsync(existingToken.User.ProfilePhotoMediaAssetId, cancellationToken);

        return CreateTokenResponse(existingToken.User, roles, rotatedToken.RawToken, profilePhotoUrl);
    }

    public async Task LogoutAsync(Guid currentUserId, LogoutRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            return;
        }

        var refreshTokenHash = ComputeTokenHash(request.RefreshToken);
        var refreshToken = await _dbContext.RefreshTokens
            .SingleOrDefaultAsync(token => token.UserId == currentUserId && token.TokenHash == refreshTokenHash, cancellationToken);

        if (refreshToken is null || refreshToken.RevokedAt is not null)
        {
            return;
        }

        refreshToken.Revoke(_timeProvider.GetUtcNow().UtcDateTime);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<AuthUserResponse> GetCurrentUserAsync(Guid currentUserId, CancellationToken cancellationToken = default)
    {
        var user = await _dbContext.Users
            .Include(candidate => candidate.UserRoles)
            .ThenInclude(userRole => userRole.Role)
            .SingleOrDefaultAsync(candidate => candidate.Id == currentUserId, cancellationToken);

        if (user is null || !user.IsActive)
        {
            throw new UnauthorizedException("The current user is not available.");
        }

        var roles = GetRoleNames(user);
        var profilePhotoUrl = await GetProfilePhotoUrlAsync(user.ProfilePhotoMediaAssetId, cancellationToken);

        return CreateUserResponse(user, roles, profilePhotoUrl);
    }

    private TokenResponse CreateTokenResponse(User user, IReadOnlyList<string> roles, string refreshToken, string? profilePhotoUrl)
    {
        var jwtOptions = _jwtOptions.Value;
        var expiresAt = _timeProvider.GetUtcNow().UtcDateTime.AddMinutes(jwtOptions.AccessTokenMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(ClaimTypes.Email, user.Email),
            new(ClaimTypes.Name, user.FullName),
            new("full_name", user.FullName)
        };

        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SigningKey)),
            SecurityAlgorithms.HmacSha256);

        var securityToken = new JwtSecurityToken(
            issuer: jwtOptions.Issuer,
            audience: jwtOptions.Audience,
            claims: claims,
            notBefore: _timeProvider.GetUtcNow().UtcDateTime,
            expires: expiresAt,
            signingCredentials: credentials);

        var accessToken = new JwtSecurityTokenHandler().WriteToken(securityToken);
        return new TokenResponse(accessToken, (int)TimeSpan.FromMinutes(jwtOptions.AccessTokenMinutes).TotalSeconds, refreshToken, CreateUserResponse(user, roles, profilePhotoUrl));
    }

    private AuthUserResponse CreateUserResponse(User user, IReadOnlyList<string> roles, string? profilePhotoUrl)
        => new(
            user.Id,
            user.FullName,
            user.Email,
            user.PhoneNumber,
            roles,
            user.ProfilePhotoMediaAssetId,
            profilePhotoUrl);

    private RefreshTokenEnvelope CreateRefreshToken(Guid userId, DateTime createdAt)
    {
        var jwtOptions = _jwtOptions.Value;
        var rawToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(64));
        var hash = ComputeTokenHash(rawToken);
        var expiresAt = createdAt.AddDays(jwtOptions.RefreshTokenDays);
        var entity = new RefreshToken(userId, hash, expiresAt, createdAt);

        return new RefreshTokenEnvelope(rawToken, entity);
    }

    private static string ComputeTokenHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim()));
        return Convert.ToHexString(bytes);
    }

    private async Task<string?> GetProfilePhotoUrlAsync(Guid? profilePhotoId, CancellationToken cancellationToken)
    {
        if (profilePhotoId is null)
        {
            return null;
        }

        return await _dbContext.MediaAssets
            .Where(asset => asset.Id == profilePhotoId.Value)
            .Select(asset => asset.PublicUrl)
            .SingleOrDefaultAsync(cancellationToken);
    }

    private void EnsureJwtConfigured()
    {
        var options = _jwtOptions.Value;
        if (!options.Enabled
            || !OptionsValidationHelpers.IsConfigured(options.SigningKey)
            || !OptionsValidationHelpers.IsConfigured(options.Issuer)
            || !OptionsValidationHelpers.IsConfigured(options.Audience))
        {
            throw new ServiceUnavailableException("JWT authentication is not fully configured.");
        }
    }

    private static IReadOnlyList<string> GetRoleNames(User user)
        => user.UserRoles
            .Select(userRole => userRole.Role.Name)
            .OrderBy(role => role, StringComparer.Ordinal)
            .ToArray();

    private static string NormalizeEmail(string value) => value.Trim().ToUpperInvariant();

    private static void ValidateRegisterRequest(RegisterRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        AddErrorIf(errors, "fullName", string.IsNullOrWhiteSpace(request.FullName) || request.FullName.Trim().Length is < 2 or > 120,
            "Full name must be between 2 and 120 characters.");
        AddErrorIf(errors, "email", !IsValidEmail(request.Email), "Email must be a valid email address.");
        AddErrorIf(errors, "password", string.IsNullOrWhiteSpace(request.Password) || request.Password.Length is < 8 or > 128,
            "Password must be between 8 and 128 characters.");
        AddErrorIf(errors, "phoneNumber", !string.IsNullOrWhiteSpace(request.PhoneNumber) && request.PhoneNumber.Trim().Length > 40,
            "Phone number must be 40 characters or fewer.");

        ThrowIfAnyErrors(errors);
    }

    private static void ValidateLoginRequest(LoginRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        AddErrorIf(errors, "email", !IsValidEmail(request.Email), "Email must be a valid email address.");
        AddErrorIf(errors, "password", string.IsNullOrWhiteSpace(request.Password), "Password is required.");

        ThrowIfAnyErrors(errors);
    }

    private static void ValidateRefreshRequest(RefreshRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        AddErrorIf(errors, "refreshToken", string.IsNullOrWhiteSpace(request.RefreshToken), "Refresh token is required.");
        ThrowIfAnyErrors(errors);
    }

    private static void AddErrorIf(IDictionary<string, string[]> errors, string key, bool condition, string message)
    {
        if (condition)
        {
            errors[key] = [message];
        }
    }

    private static bool IsValidEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return false;
        }

        try
        {
            _ = new MailAddress(email.Trim());
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void ThrowIfAnyErrors(Dictionary<string, string[]> errors)
    {
        if (errors.Count > 0)
        {
            throw new ValidationProblemException(errors);
        }
    }

    private sealed record RefreshTokenEnvelope(string RawToken, RefreshToken Entity);
}
