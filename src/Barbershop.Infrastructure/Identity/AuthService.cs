using System.IdentityModel.Tokens.Jwt;
using System.Net.Mail;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Barbershop.Application.Auth;
using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Email;
using Barbershop.Domain.Common;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Configuration;
using Barbershop.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace Barbershop.Infrastructure.Identity;

internal sealed class AuthService : IAuthService
{
    private const string DuplicateEmailMessage = "Ya existe una cuenta con ese correo.";

    // Name of the unique index on users."NormalizedEmail" (created by the InitialDomainModel migration).
    private const string UsersEmailUniqueIndex = "IX_users_NormalizedEmail";

    private const int MaxFailedLoginAttempts = 5;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The one answer of a failed sign-in (BUG-005): unknown email, wrong password, inactive and locked accounts all
    /// get this same 401, so a locked account no longer confirms that an email is registered. The lockout itself is
    /// kept (while locked even the right password fails) and the message tells a real user to wait.
    /// </summary>
    internal const string InvalidCredentialsMessage =
        "Correo o contraseña incorrectos. Si fallaste varias veces seguidas, espera unos minutos antes de volver a intentarlo.";

    /// <summary>
    /// A rotated refresh token presented again after this window is treated as stolen and its whole session is revoked
    /// (BUG-094). Inside it, it is a legitimate race: another tab or an in-flight request still carried the token the
    /// first tab had just rotated (the front adopts the stored successor), so only that request is refused.
    /// </summary>
    internal static readonly TimeSpan RotatedTokenReuseGrace = TimeSpan.FromSeconds(60);

    /// <summary>Hash compared for unknown emails, so they take as long as a real password check (BUG-005).</summary>
    private static readonly Lazy<string> TimingEqualizerHash =
        new(() => new PasswordHasher<object>().HashPassword(new object(), Convert.ToHexString(RandomNumberGenerator.GetBytes(16))));

    private readonly AppDbContext _dbContext;
    private readonly IPasswordHasher<object> _passwordHasher;
    private readonly IEmailSender _emailSender;
    private readonly IOptions<JwtOptions> _jwtOptions;
    private readonly IOptions<AppOptions> _appOptions;
    private readonly TimeProvider _timeProvider;

    public AuthService(
        AppDbContext dbContext,
        IPasswordHasher<object> passwordHasher,
        IEmailSender emailSender,
        IOptions<JwtOptions> jwtOptions,
        IOptions<AppOptions> appOptions,
        TimeProvider timeProvider)
    {
        _dbContext = dbContext;
        _passwordHasher = passwordHasher;
        _emailSender = emailSender;
        _jwtOptions = jwtOptions;
        _appOptions = appOptions;
        _timeProvider = timeProvider;
    }

    public async Task<TokenResponse> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRegisterRequest(request);
        EnsureJwtConfigured();

        // An inactive account still owns its email: registering again must never reactivate it or replace its
        // password, since whoever did so would inherit its linked customer profile. Same answer for active and
        // inactive accounts, so the response does not reveal which one it is.
        var normalizedEmail = NormalizeEmail(request.Email);
        if (await _dbContext.Users.AnyAsync(u => u.NormalizedEmail == normalizedEmail, cancellationToken))
        {
            throw new ConflictException(DuplicateEmailMessage);
        }

        var createdAt = _timeProvider.GetUtcNow().UtcDateTime;
        var passwordHash = _passwordHasher.HashPassword(new object(), request.Password);

        var user = new User(request.FullName, request.Email, passwordHash, createdAt, request.PhoneNumber);
        var customerRole = await _dbContext.Roles.SingleAsync(role => role.NormalizedName == RoleNames.Customer.ToUpperInvariant(), cancellationToken);
        user.UserRoles.Add(new UserRole(user.Id, customerRole.Id, createdAt));
        _dbContext.Users.Add(user);

        var refreshToken = CreateRefreshToken(user.Id, createdAt);
        user.RefreshTokens.Add(refreshToken.Entity);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: UsersEmailUniqueIndex,
        })
        {
            // A simultaneous registration took the email between the check above and this insert; the unique
            // index rejected this one, so nothing was written. Same answer as the regular duplicate check.
            throw new ConflictException(DuplicateEmailMessage);
        }

        return CreateTokenResponse(user, [RoleNames.Customer], [], refreshToken, null);
    }

    public async Task<TokenResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        ValidateLoginRequest(request);
        EnsureJwtConfigured();

        // No seeding here: signing in must never change roles or permissions. The roles and the catalog are
        // bootstrapped once at startup (Program.cs).

        var normalizedEmail = NormalizeEmail(request.Email);
        var userId = await _dbContext.Users
            .AsNoTracking()
            .Where(candidate => candidate.NormalizedEmail == normalizedEmail)
            .Select(candidate => (Guid?)candidate.Id)
            .SingleOrDefaultAsync(cancellationToken);

        if (userId is null)
        {
            // Same work and same answer as a wrong password (BUG-005).
            _passwordHasher.VerifyHashedPassword(new object(), TimingEqualizerHash.Value, request.Password);
            throw new UnauthorizedException(InvalidCredentialsMessage);
        }

        // Under the account lock: a password change or reset committing while this login checks the old password
        // cannot be followed by a new session issued for that old password (BUG-015).
        var (user, refreshToken, failure) = await AccountSessions.InAccountTransactionAsync<(User User, RefreshTokenEnvelope? Token, bool Failed)>(_dbContext, async () =>
        {
            await AccountSessions.LockAccountAsync(_dbContext, userId.Value, cancellationToken);

            var lockedUser = await _dbContext.Users
                .Include(candidate => candidate.UserRoles)
                .ThenInclude(userRole => userRole.Role)
                .ThenInclude(role => role.RolePermissions)
                .ThenInclude(rolePermission => rolePermission.Permission)
                .SingleOrDefaultAsync(candidate => candidate.Id == userId.Value, cancellationToken);

            if (lockedUser is null || !lockedUser.IsActive)
            {
                throw new UnauthorizedException(InvalidCredentialsMessage);
            }

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var verification = _passwordHasher.VerifyHashedPassword(new object(), lockedUser.PasswordHash, request.Password);

            // A locked account answers exactly like a wrong password (BUG-005), even with the right one: the lockout
            // stays, but it no longer tells an attacker that the email exists.
            if (lockedUser.IsLockedOut(now))
            {
                throw new UnauthorizedException(InvalidCredentialsMessage);
            }

            if (verification == PasswordVerificationResult.Failed)
            {
                // The failed attempt must be committed, so it is reported after the transaction instead of thrown.
                lockedUser.RegisterFailedLogin(now, MaxFailedLoginAttempts, LockoutDuration);
                await _dbContext.SaveChangesAsync(cancellationToken);
                return (lockedUser, (RefreshTokenEnvelope?)null, true);
            }

            if (lockedUser.FailedLoginCount > 0 || lockedUser.LockedUntil is not null)
            {
                lockedUser.RegisterSuccessfulLogin(now);
            }

            var issued = CreateRefreshToken(lockedUser.Id, _timeProvider.GetUtcNow().UtcDateTime);
            _dbContext.RefreshTokens.Add(issued.Entity);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return (lockedUser, issued, false);
        }, cancellationToken);

        if (failure || refreshToken is null)
        {
            throw new UnauthorizedException(InvalidCredentialsMessage);
        }

        var roles = GetRoleNames(user);
        var permissions = GetPermissionCodes(user);
        var profilePhotoUrl = await GetProfilePhotoUrlAsync(user.ProfilePhotoMediaAssetId, cancellationToken);

        return CreateTokenResponse(user, roles, permissions, refreshToken, profilePhotoUrl);
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
            .ThenInclude(role => role.RolePermissions)
            .ThenInclude(rolePermission => rolePermission.Permission)
            .SingleOrDefaultAsync(token => token.TokenHash == refreshTokenHash, cancellationToken);

        if (existingToken is not null && IsReuseOfRotatedToken(existingToken, utcNow))
        {
            // BUG-094: a token rotated a while ago is presented again. Either it was stolen and its successor is in
            // the thief's hands, or the thief used it first and the victim is presenting it now: end the whole session.
            await AccountSessions.InAccountTransactionAsync<object?>(_dbContext, async () =>
            {
                await AccountSessions.LockAccountAsync(_dbContext, existingToken.UserId, cancellationToken);
                await AccountSessions.RevokeSessionAsync(
                    _dbContext, existingToken.UserId, refreshTokenHash, _timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
                await _dbContext.SaveChangesAsync(cancellationToken);
                return null;
            }, cancellationToken);

            throw new UnauthorizedException("Tu sesión expiró. Inicia sesión de nuevo.");
        }

        if (existingToken is null
            || !AccountSessions.IsUsable(existingToken, utcNow)
            || !existingToken.User.IsActive)
        {
            throw new UnauthorizedException("Tu sesión expiró. Inicia sesión de nuevo.");
        }

        // Single use even under concurrency (BUG-016): the check above ran without a lock, so two requests with the
        // same token can both get here. Under the account lock they run one after the other and the token is read
        // again; the second one finds it already rotated (or revoked by a logout/password change) and gets 401.
        var rotatedToken = await AccountSessions.InAccountTransactionAsync(_dbContext, async () =>
        {
            await AccountSessions.LockAccountAsync(_dbContext, existingToken.UserId, cancellationToken);
            await AccountSessions.ReloadAsync(_dbContext, existingToken, cancellationToken);
            await AccountSessions.ReloadAsync(_dbContext, existingToken.User, cancellationToken);

            var lockedNow = _timeProvider.GetUtcNow().UtcDateTime;
            if (!AccountSessions.IsUsable(existingToken, lockedNow) || !existingToken.User.IsActive)
            {
                throw new UnauthorizedException("Tu sesión expiró. Inicia sesión de nuevo.");
            }

            // BUG-094: the successor keeps the session's expiry, so the session ends RefreshTokenDays after the
            // sign-in however often it is refreshed (absolute lifetime); rotating no longer extends it.
            var rotated = CreateRefreshToken(existingToken.UserId, lockedNow, existingToken.SessionId, existingToken.ExpiresAt);
            existingToken.Revoke(lockedNow, rotated.Entity.Id);

            _dbContext.RefreshTokens.Add(rotated.Entity);
            await _dbContext.SaveChangesAsync(cancellationToken);
            return rotated;
        }, cancellationToken);

        var roles = GetRoleNames(existingToken.User);
        var permissions = GetPermissionCodes(existingToken.User);
        var profilePhotoUrl = await GetProfilePhotoUrlAsync(existingToken.User.ProfilePhotoMediaAssetId, cancellationToken);

        return CreateTokenResponse(existingToken.User, roles, permissions, rotatedToken, profilePhotoUrl);
    }

    public async Task LogoutAsync(Guid currentUserId, LogoutRequest request, CancellationToken cancellationToken = default)
    {
        // Cookie and body (iOS/PWA fallback, BUG-002) may both arrive, and may differ when one of them is stale:
        // each one is revoked, but only if it belongs to the signed-in account.
        var tokenHashes = new[] { request.RefreshToken, request.AdditionalRefreshToken }
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .Select(token => ComputeTokenHash(token!))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (tokenHashes.Length == 0)
        {
            return;
        }

        await AccountSessions.InAccountTransactionAsync(_dbContext, async () =>
        {
            await AccountSessions.LockAccountAsync(_dbContext, currentUserId, cancellationToken);

            var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
            foreach (var tokenHash in tokenHashes)
            {
                await AccountSessions.RevokeSessionAsync(_dbContext, currentUserId, tokenHash, utcNow, cancellationToken);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
    }

    public async Task<TokenResponse> IssueSessionAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        EnsureJwtConfigured();

        var user = await _dbContext.Users
            .Include(candidate => candidate.UserRoles)
            .ThenInclude(userRole => userRole.Role)
            .ThenInclude(role => role.RolePermissions)
            .ThenInclude(rolePermission => rolePermission.Permission)
            .SingleOrDefaultAsync(candidate => candidate.Id == userId, cancellationToken);

        if (user is null || !user.IsActive)
        {
            throw new UnauthorizedException("Tu cuenta no está disponible. Inicia sesión de nuevo.");
        }

        var refreshToken = CreateRefreshToken(user.Id, _timeProvider.GetUtcNow().UtcDateTime);
        _dbContext.RefreshTokens.Add(refreshToken.Entity);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var profilePhotoUrl = await GetProfilePhotoUrlAsync(user.ProfilePhotoMediaAssetId, cancellationToken);
        return CreateTokenResponse(user, GetRoleNames(user), GetPermissionCodes(user), refreshToken, profilePhotoUrl);
    }

    public async Task<AuthUserResponse> GetCurrentUserAsync(Guid currentUserId, CancellationToken cancellationToken = default)
    {
        var user = await _dbContext.Users
            .Include(candidate => candidate.UserRoles)
            .ThenInclude(userRole => userRole.Role)
            .ThenInclude(role => role.RolePermissions)
            .ThenInclude(rolePermission => rolePermission.Permission)
            .SingleOrDefaultAsync(candidate => candidate.Id == currentUserId, cancellationToken);

        if (user is null || !user.IsActive)
        {
            throw new UnauthorizedException("Tu cuenta no está disponible. Inicia sesión de nuevo.");
        }

        var roles = GetRoleNames(user);
        var permissions = GetPermissionCodes(user);
        var profilePhotoUrl = await GetProfilePhotoUrlAsync(user.ProfilePhotoMediaAssetId, cancellationToken);

        return CreateUserResponse(user, roles, permissions, profilePhotoUrl);
    }

    public async Task ForgotPasswordAsync(ForgotPasswordRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsValidEmail(request.Email))
        {
            return;
        }

        var normalizedEmail = NormalizeEmail(request.Email);
        var user = await _dbContext.Users
            .SingleOrDefaultAsync(u => u.NormalizedEmail == normalizedEmail, cancellationToken);

        if (user is null || !user.IsActive)
        {
            return;
        }

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var rawToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var tokenHash = ComputeTokenHash(rawToken);
        var expiresAt = utcNow.AddMinutes(_jwtOptions.Value.PasswordResetTokenMinutes);

        user.SetPasswordResetToken(tokenHash, expiresAt);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var frontendUrl = _appOptions.Value.FrontendUrl.TrimEnd('/');
        var resetUrl = $"{frontendUrl}/auth/reset-password?token={rawToken}";
        var firstName = user.FullName.Split(' ')[0];

        var email = new EmailMessage(
            To: user.Email,
            Subject: "Restablece tu contraseña",
            HtmlBody: BuildResetEmailHtml(firstName, resetUrl, _jwtOptions.Value.PasswordResetTokenMinutes),
            TextBody: $"Hola {firstName},\n\nVisita el siguiente enlace para restablecer tu contraseña (válido {_jwtOptions.Value.PasswordResetTokenMinutes} minutos):\n{resetUrl}\n\nSi no solicitaste este cambio, ignora este correo.");

        await _emailSender.SendAsync(email, cancellationToken);
    }

    public async Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken = default)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        AddErrorIf(errors, "token", string.IsNullOrWhiteSpace(request.Token), "El token es requerido.");
        AddErrorIf(errors, "newPassword", string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length is < 8 or > 128,
            "La contraseña debe tener entre 8 y 128 caracteres.");
        ThrowIfAnyErrors(errors);

        var utcNow = _timeProvider.GetUtcNow().UtcDateTime;
        var tokenHash = ComputeTokenHash(request.Token);

        var user = await _dbContext.Users
            .SingleOrDefaultAsync(u => u.PasswordResetTokenHash == tokenHash, cancellationToken);

        if (user is null)
        {
            throw InvalidResetLink();
        }

        // Under the account lock, so a refresh committing at the same time cannot leave a session alive (BUG-015/016).
        await AccountSessions.InAccountTransactionAsync(_dbContext, async () =>
        {
            await AccountSessions.LockAccountAsync(_dbContext, user.Id, cancellationToken);
            await AccountSessions.ReloadAsync(_dbContext, user, cancellationToken);

            if (!user.IsActive || user.PasswordResetTokenHash != tokenHash || !user.ConsumePasswordResetToken(utcNow))
            {
                throw InvalidResetLink();
            }

            var newHash = _passwordHasher.HashPassword(new object(), request.NewPassword);
            user.SetPasswordHash(newHash, utcNow);
            await AccountSessions.RevokeAllRefreshTokensAsync(_dbContext, user.Id, utcNow, cancellationToken);

            await _dbContext.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
    }

    private static ValidationProblemException InvalidResetLink() =>
        new(new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["token"] = ["El enlace de recuperación es inválido o ha expirado."]
        });

    private static string BuildResetEmailHtml(string firstName, string resetUrl, int expiryMinutes) => $"""
        <!DOCTYPE html>
        <html lang="es">
        <head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1.0"></head>
        <body style="margin:0;padding:0;background:#1a1612;font-family:system-ui,-apple-system,sans-serif;">
          <table width="100%" cellpadding="0" cellspacing="0" style="background:#1a1612;padding:40px 16px;">
            <tr><td align="center">
              <table width="600" cellpadding="0" cellspacing="0" style="max-width:600px;width:100%;background:#231f1a;border-radius:16px;border:1px solid #3a3228;">
                <tr>
                  <td style="padding:28px 36px;border-bottom:1px solid #3a3228;">
                    <p style="margin:0;font-size:20px;font-weight:700;color:#c9a55a;">Barbershop</p>
                  </td>
                </tr>
                <tr>
                  <td style="padding:32px 36px;">
                    <p style="margin:0 0 8px;font-size:22px;font-weight:700;color:#f0e6d3;">Hola, {firstName}</p>
                    <p style="margin:0 0 24px;font-size:15px;color:#a89880;line-height:1.6;">
                      Recibimos una solicitud para restablecer la contraseña de tu cuenta.<br>
                      El enlace es válido por <strong style="color:#f0e6d3;">{expiryMinutes} minutos</strong>.
                    </p>
                    <table cellpadding="0" cellspacing="0" style="margin:0 0 28px;">
                      <tr>
                        <td style="background:#c9a55a;border-radius:999px;">
                          <a href="{resetUrl}" style="display:inline-block;padding:13px 30px;font-size:15px;font-weight:700;color:#1a1612;text-decoration:none;">
                            Restablecer contraseña
                          </a>
                        </td>
                      </tr>
                    </table>
                    <p style="margin:0 0 6px;font-size:12px;color:#7a6e64;">Si el botón no funciona, copia este enlace:</p>
                    <p style="margin:0 0 24px;font-size:12px;color:#c9a55a;word-break:break-all;">{resetUrl}</p>
                    <p style="margin:0;font-size:13px;color:#7a6e64;line-height:1.6;">
                      Si no solicitaste este cambio, ignora este correo de forma segura.
                    </p>
                  </td>
                </tr>
                <tr>
                  <td style="padding:18px 36px;border-top:1px solid #3a3228;">
                    <p style="margin:0;font-size:12px;color:#5a5048;text-align:center;">
                      © Barbershop &middot; Correo automático, no respondas a este mensaje.
                    </p>
                  </td>
                </tr>
              </table>
            </td></tr>
          </table>
        </body>
        </html>
        """;

    private TokenResponse CreateTokenResponse(User user, IReadOnlyList<string> roles, IReadOnlyList<string> permissions, RefreshTokenEnvelope refreshToken, string? profilePhotoUrl)
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
            new("full_name", user.FullName),
            // The session this access token belongs to: checked on every request, so signing out or changing the
            // password stops it right away instead of when it expires (BUG-013).
            new(JwtRegisteredClaimNames.Sid, refreshToken.Entity.SessionId.ToString())
        };

        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        claims.AddRange(permissions.Select(code => new Claim(PermissionClaimTypes.Permission, code)));

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
        return new TokenResponse(accessToken, (int)TimeSpan.FromMinutes(jwtOptions.AccessTokenMinutes).TotalSeconds, refreshToken.RawToken, CreateUserResponse(user, roles, permissions, profilePhotoUrl), refreshToken.Entity.ExpiresAt);
    }

    private static IReadOnlyList<string> GetPermissionCodes(User user)
        => user.UserRoles
            .SelectMany(userRole => userRole.Role.RolePermissions.Select(rolePermission => rolePermission.Permission.Code))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToArray();

    private AuthUserResponse CreateUserResponse(User user, IReadOnlyList<string> roles, IReadOnlyList<string> permissions, string? profilePhotoUrl)
        => new(
            user.Id,
            user.FullName,
            user.Email,
            user.PhoneNumber,
            roles,
            user.ProfilePhotoMediaAssetId,
            profilePhotoUrl,
            permissions);

    /// <param name="sessionExpiresAt">For a rotation: the expiry of the session, inherited instead of a new window.</param>
    private RefreshTokenEnvelope CreateRefreshToken(Guid userId, DateTime createdAt, Guid? sessionId = null, DateTime? sessionExpiresAt = null)
    {
        var jwtOptions = _jwtOptions.Value;
        var rawToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(64));
        var hash = ComputeTokenHash(rawToken);
        var expiresAt = sessionExpiresAt ?? createdAt.AddDays(jwtOptions.RefreshTokenDays);
        var entity = new RefreshToken(userId, hash, expiresAt, createdAt, sessionId: sessionId);

        return new RefreshTokenEnvelope(rawToken, entity);
    }

    /// <summary>
    /// A token that was rotated (it has a successor) more than <see cref="RotatedTokenReuseGrace"/> ago. A token revoked
    /// by a logout or password change has no successor and is simply refused.
    /// </summary>
    private static bool IsReuseOfRotatedToken(RefreshToken token, DateTime utcNow)
        => token.ReplacedByRefreshTokenId is not null
            && token.RevokedAt is { } rotatedAt
            && utcNow - rotatedAt > RotatedTokenReuseGrace;

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
            throw new ServiceUnavailableException("El inicio de sesión no está disponible en este momento. Inténtalo de nuevo en unos minutos.");
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

        // BUG-006: the same limits as the User entity (email 5–256), checked first so the domain's ArgumentException
        // never reaches the client.
        AddErrorIf(errors, "fullName", string.IsNullOrWhiteSpace(request.FullName) || request.FullName.Trim().Length is < 2 or > 120,
            "El nombre debe tener entre 2 y 120 caracteres.");
        AddErrorIf(errors, "email", !EmailRules.IsValid(request.Email),
            EmailRules.InvalidMessage);
        AddErrorIf(errors, "password", string.IsNullOrWhiteSpace(request.Password) || request.Password.Length is < 8 or > 128,
            "La contraseña debe tener entre 8 y 128 caracteres.");
        AddErrorIf(errors, "phoneNumber", !string.IsNullOrWhiteSpace(request.PhoneNumber) && request.PhoneNumber.Trim().Length > 40,
            "El teléfono no puede superar 40 caracteres.");

        ThrowIfAnyErrors(errors);
    }

    private static void ValidateLoginRequest(LoginRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        AddErrorIf(errors, "email", !IsValidEmail(request.Email), "Escribe un correo válido.");
        AddErrorIf(errors, "password", string.IsNullOrWhiteSpace(request.Password), "Escribe la contraseña.");

        ThrowIfAnyErrors(errors);
    }

    private static void ValidateRefreshRequest(RefreshRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        AddErrorIf(errors, "refreshToken", string.IsNullOrWhiteSpace(request.RefreshToken), "Inicia sesión de nuevo.");
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
