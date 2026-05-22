using Barbershop.Application.Auth;
using Barbershop.Infrastructure.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using System.Security.Claims;

namespace Api.Barbershop.Features.Auth;

public static class AuthEndpoints
{
    private const string RefreshTokenCookieName = "X-Refresh-Token";

    // Response type for the API layer — does not expose the refresh token in the body.
    // The refresh token lives exclusively in the HttpOnly cookie set by the server.
    private sealed record AuthApiTokenResponse(string AccessToken, int ExpiresInSeconds, AuthUserResponse User);

    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder api)
    {
        var auth = api.MapGroup("/auth")
            .WithTags("Auth");

        auth.MapPost("/register", RegisterAsync)
            .WithName("Register")
            .Produces<AuthApiTokenResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        auth.MapPost("/login", LoginAsync)
            .WithName("Login")
            .Produces<AuthApiTokenResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        auth.MapPost("/refresh", RefreshAsync)
            .WithName("RefreshToken")
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
            new AuthApiTokenResponse(response.AccessToken, response.ExpiresInSeconds, response.User),
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
        return Results.Ok(new AuthApiTokenResponse(response.AccessToken, response.ExpiresInSeconds, response.User));
    }

    private static async Task<IResult> RefreshAsync(
        HttpContext httpContext,
        IAuthService authService,
        IOptions<JwtOptions> jwtOptions,
        CancellationToken cancellationToken)
    {
        var refreshToken = httpContext.Request.Cookies[RefreshTokenCookieName];
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return Results.Unauthorized();
        }

        var response = await authService.RefreshAsync(new RefreshRequest(refreshToken), cancellationToken);
        SetRefreshTokenCookie(httpContext, response.RefreshToken, jwtOptions.Value.RefreshTokenDays);
        return Results.Ok(new AuthApiTokenResponse(response.AccessToken, response.ExpiresInSeconds, response.User));
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

    private static void SetRefreshTokenCookie(HttpContext context, string refreshToken, int refreshTokenDays)
    {
        var options = BuildRefreshCookieOptions(context);
        options.MaxAge = TimeSpan.FromDays(refreshTokenDays);
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
