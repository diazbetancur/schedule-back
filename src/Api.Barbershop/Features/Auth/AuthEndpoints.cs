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
    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder api)
    {
        var auth = api.MapGroup("/auth")
            .WithTags("Auth");

        auth.MapPost("/register", RegisterAsync)
            .WithName("Register")
            .RequireRateLimiting(RateLimitPolicyNames.AuthCredentials)
            .Produces<RefreshTokenTransport.AuthApiTokenResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        auth.MapPost("/login", LoginAsync)
            .WithName("Login")
            .RequireRateLimiting(RateLimitPolicyNames.AuthCredentials)
            .Produces<RefreshTokenTransport.AuthApiTokenResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);

        auth.MapPost("/refresh", RefreshAsync)
            .WithName("RefreshToken")
            .RequireRateLimiting(RateLimitPolicyNames.AuthRefresh)
            .Produces<RefreshTokenTransport.AuthApiTokenResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
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
        RefreshTokenTransport.SetCookie(httpContext, response.RefreshToken, jwtOptions.Value.RefreshTokenDays, response.RefreshTokenExpiresAtUtc);
        return Results.Json(RefreshTokenTransport.ToApiResponse(response), statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        HttpContext httpContext,
        IAuthService authService,
        IOptions<JwtOptions> jwtOptions,
        CancellationToken cancellationToken)
    {
        var response = await authService.LoginAsync(request, cancellationToken);
        RefreshTokenTransport.SetCookie(httpContext, response.RefreshToken, jwtOptions.Value.RefreshTokenDays, response.RefreshTokenExpiresAtUtc);
        return Results.Ok(RefreshTokenTransport.ToApiResponse(response));
    }

    private static async Task<IResult> RefreshAsync(
        HttpContext httpContext,
        IAuthService authService,
        IOptions<JwtOptions> jwtOptions,
        CancellationToken cancellationToken)
    {
        // Cookie first (standard browsers); the body is the fallback for iOS standalone PWAs, where the cross-site
        // cookie does not arrive. A missing or empty body means "no token" (401); a JSON body that does not parse is
        // a malformed request (400). Neither reaches the server-error path (BUG-003).
        var refreshToken = RefreshTokenTransport.ReadCookie(httpContext);

        if (refreshToken is null)
        {
            var body = await RefreshTokenTransport.ReadBodyAsync(httpContext.Request, cancellationToken);
            if (body.IsMalformed)
            {
                throw new ArgumentException("La solicitud debe incluir el token de sesión en el formato esperado.");
            }

            refreshToken = body.Token;
        }

        if (refreshToken is null)
        {
            return Results.Unauthorized();
        }

        var response = await authService.RefreshAsync(new RefreshRequest(refreshToken), cancellationToken);
        RefreshTokenTransport.SetCookie(httpContext, response.RefreshToken, jwtOptions.Value.RefreshTokenDays, response.RefreshTokenExpiresAtUtc);
        return Results.Ok(RefreshTokenTransport.ToApiResponse(response));
    }

    [Authorize]
    private static async Task<IResult> LogoutAsync(
        HttpContext httpContext,
        ClaimsPrincipal user,
        IAuthService authService,
        CancellationToken cancellationToken)
    {
        // Both the cookie and the body token are revoked (BUG-002): clients without the cookie (iOS standalone PWA)
        // can only present the body one. A body that cannot be read never blocks signing out.
        var cookieToken = RefreshTokenTransport.ReadCookie(httpContext);
        var bodyToken = await RefreshTokenTransport.ReadBodyAsync(httpContext.Request, cancellationToken);

        await authService.LogoutAsync(
            user.GetRequiredUserId(), new LogoutRequest(cookieToken, bodyToken.Token), cancellationToken);
        RefreshTokenTransport.ClearCookie(httpContext);
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
}
