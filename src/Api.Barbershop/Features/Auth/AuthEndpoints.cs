using Api.Barbershop.Configuration;
using Barbershop.Application.Auth;
using Barbershop.Infrastructure.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using System.Security.Claims;

namespace Api.Barbershop.Features.Auth;

public static class AuthEndpoints
{
    private const string RefreshTokenCookieName = "X-Refresh-Token";

    // El refresh token va en cookie HttpOnly (todos los browsers) Y en el body
    // (fallback para iOS standalone donde las cookies cross-origin son bloqueadas por ITP).
    private sealed record AuthApiTokenResponse(string AccessToken, int ExpiresInSeconds, AuthUserResponse User, string RefreshToken);

    // Cuerpo opcional en /auth/refresh — iOS standalone envía el token aquí cuando la cookie no llega.
    private sealed record RefreshBodyRequest(string? RefreshToken);

    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder api)
    {
        var auth = api.MapGroup("/auth")
            .WithTags("Auth");

        auth.MapPost("/register", RegisterAsync)
            .WithName("Register")
            .RequireRateLimiting(RateLimitPolicyNames.AuthCredentials)
            .Produces<AuthApiTokenResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        auth.MapPost("/login", LoginAsync)
            .WithName("Login")
            .RequireRateLimiting(RateLimitPolicyNames.AuthCredentials)
            .Produces<AuthApiTokenResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        auth.MapPost("/refresh", RefreshAsync)
            .WithName("RefreshToken")
            .RequireRateLimiting(RateLimitPolicyNames.AuthRefresh)
            .Produces<AuthApiTokenResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        auth.MapPost("/logout", LogoutAsync)
            .WithName("Logout")
            .RequireAuthorization()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        auth.MapGet("/me", GetMeAsync)
            .WithName("GetCurrentUser")
            .RequireAuthorization()
            .Produces<AuthUserResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        auth.MapPost("/forgot-password", ForgotPasswordAsync)
            .WithName("ForgotPassword")
            .RequireRateLimiting(RateLimitPolicyNames.AuthPasswordReset)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        auth.MapPost("/reset-password", ResetPasswordAsync)
            .WithName("ResetPassword")
            .RequireRateLimiting(RateLimitPolicyNames.AuthPasswordReset)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        return api;
    }

    private static async Task<IResult> RegisterAsync(
        RegisterRequest request,
        HttpContext httpContext,
        IAuthService authService,
        IOptions<JwtOptions> jwtOptions,
        CancellationToken cancellationToken)
    {
        var response = await authService.RegisterAsync(request, cancellationToken);
        SetRefreshTokenCookie(httpContext, response.RefreshToken, jwtOptions.Value.RefreshTokenDays);
        return Results.Json(
            new AuthApiTokenResponse(response.AccessToken, response.ExpiresInSeconds, response.User, response.RefreshToken),
            statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        HttpContext httpContext,
        IAuthService authService,
        IOptions<JwtOptions> jwtOptions,
        CancellationToken cancellationToken)
    {
        var response = await authService.LoginAsync(request, cancellationToken);
        SetRefreshTokenCookie(httpContext, response.RefreshToken, jwtOptions.Value.RefreshTokenDays);
        return Results.Ok(new AuthApiTokenResponse(response.AccessToken, response.ExpiresInSeconds, response.User, response.RefreshToken));
    }

    private static async Task<IResult> RefreshAsync(
        HttpContext httpContext,
        IAuthService authService,
        IOptions<JwtOptions> jwtOptions,
        CancellationToken cancellationToken)
    {
        // Cookie-based (browsers estándar)
        var refreshToken = httpContext.Request.Cookies[RefreshTokenCookieName];

        // Fallback body-based: iOS standalone PWA no envía cookies cross-origin confiablemente
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            var body = await httpContext.Request.ReadFromJsonAsync<RefreshBodyRequest>(cancellationToken);
            refreshToken = body?.RefreshToken;
        }

        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return Results.Unauthorized();
        }

        var response = await authService.RefreshAsync(new RefreshRequest(refreshToken), cancellationToken);
        SetRefreshTokenCookie(httpContext, response.RefreshToken, jwtOptions.Value.RefreshTokenDays);
        return Results.Ok(new AuthApiTokenResponse(response.AccessToken, response.ExpiresInSeconds, response.User, response.RefreshToken));
    }

    [Authorize]
    private static async Task<IResult> LogoutAsync(
        HttpContext httpContext,
        ClaimsPrincipal user,
        IAuthService authService,
        CancellationToken cancellationToken)
    {
        var refreshToken = httpContext.Request.Cookies[RefreshTokenCookieName];
        await authService.LogoutAsync(user.GetRequiredUserId(), new LogoutRequest(refreshToken), cancellationToken);
        ClearRefreshTokenCookie(httpContext);
        return Results.NoContent();
    }

    [Authorize]
    private static async Task<IResult> GetMeAsync(ClaimsPrincipal user, IAuthService authService, CancellationToken cancellationToken)
    {
        var response = await authService.GetCurrentUserAsync(user.GetRequiredUserId(), cancellationToken);
        return Results.Ok(response);
    }

    private static async Task<IResult> ForgotPasswordAsync(
        ForgotPasswordRequest request,
        IAuthService authService,
        CancellationToken cancellationToken)
    {
        await authService.ForgotPasswordAsync(request, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> ResetPasswordAsync(
        ResetPasswordRequest request,
        IAuthService authService,
        CancellationToken cancellationToken)
    {
        await authService.ResetPasswordAsync(request, cancellationToken);
        return Results.NoContent();
    }

    private static void SetRefreshTokenCookie(HttpContext context, string refreshToken, int refreshTokenDays)
    {
        var options = BuildRefreshCookieOptions(context);
        // iOS WebKit (PWA standalone) requiere Expires explícito — Max-Age solo
        // hace que la cookie se trate como de sesión y se borre al cerrar la app.
        var expiry = DateTimeOffset.UtcNow.AddDays(refreshTokenDays);
        options.MaxAge = TimeSpan.FromDays(refreshTokenDays);
        options.Expires = expiry;
        context.Response.Cookies.Append(RefreshTokenCookieName, refreshToken, options);
    }

    private static void ClearRefreshTokenCookie(HttpContext context)
    {
        context.Response.Cookies.Delete(RefreshTokenCookieName, BuildRefreshCookieOptions(context));
    }

    // localhost is treated as a secure context by modern browsers, so SameSite=None; Secure
    // works for cross-port dev traffic (localhost:4200 → localhost:5000). In production the
    // app runs over HTTPS, which is also required by the PWA manifest.
    private static CookieOptions BuildRefreshCookieOptions(HttpContext context)
    {
        var isSecureContext = context.Request.IsHttps
            || context.Request.Host.Host is "localhost" or "127.0.0.1";

        return new CookieOptions
        {
            HttpOnly = true,
            Secure = isSecureContext,
            SameSite = isSecureContext ? SameSiteMode.None : SameSiteMode.Lax,
            Path = "/",
            IsEssential = true,
        };
    }
}
