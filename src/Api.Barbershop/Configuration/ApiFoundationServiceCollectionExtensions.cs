using Api.Barbershop.Features.Auth;
using Api.Barbershop.Middleware;
using Barbershop.Application.Auth;
using Barbershop.Infrastructure.Configuration;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Routing;
using System.Threading.RateLimiting;
using AppCorsOptions = Barbershop.Infrastructure.Configuration.CorsOptions;

namespace Api.Barbershop.Configuration;

public static class ApiFoundationServiceCollectionExtensions
{
    public static IServiceCollection AddApiFoundation(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        services.Configure<JsonOptions>(options =>
        {
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
            // BUG-069 / BUG-081: value properties of write requests cannot be left out of the body.
            options.SerializerOptions.TypeInfoResolver = (options.SerializerOptions.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver())
                .WithAddedModifier(RequiredRequestValues.Apply);
        });

        // A body or parameter that cannot be read reaches GlobalExceptionHandler, which answers in Spanish with the
        // field instead of the framework's bare 400 (BUG-021, BUG-069, BUG-081).
        services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);

        services.AddProblemDetails(options =>
        {
            options.CustomizeProblemDetails = context =>
                ProblemDetailsExtensions.CustomizeProblemDetails(context, environment);
        });

        services.AddExceptionHandler<GlobalExceptionHandler>();

        var corsOptions = configuration.GetSection(AppCorsOptions.SectionName).Get<AppCorsOptions>() ?? new AppCorsOptions();
        services.AddCors(options =>
        {
            options.AddPolicy(AppCorsOptions.PolicyName, policyBuilder => ConfigureCorsPolicy(policyBuilder, corsOptions, environment));
        });

        var jwtOptions = configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();

        if (!jwtOptions.Enabled)
        {
            throw new InvalidOperationException($"{JwtOptions.SectionName}:Enabled cannot be false. JWT authentication cannot be disabled.");
        }

        if (string.IsNullOrWhiteSpace(jwtOptions.Issuer)
            || string.IsNullOrWhiteSpace(jwtOptions.Audience)
            || string.IsNullOrWhiteSpace(jwtOptions.SigningKey))
        {
            throw new InvalidOperationException($"{JwtOptions.SectionName} is not fully configured. Issuer, Audience, and SigningKey are required.");
        }

        services.AddAuthentication(authenticationOptions =>
            {
                authenticationOptions.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                authenticationOptions.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(jwtBearerOptions =>
            {
                jwtBearerOptions.RequireHttpsMetadata = jwtOptions.RequireHttpsMetadata;
                jwtBearerOptions.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = jwtOptions.Issuer,
                    ValidAudience = jwtOptions.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SigningKey)),
                    ClockSkew = TimeSpan.FromMinutes(1)
                };
                jwtBearerOptions.Events = new JwtBearerEvents
                {
                    // BUG-013: a valid signature is not enough; the session must still be active. Tokens issued
                    // before sessions were tracked (no "sid") get 401 and the app renews them with its refresh token.
                    OnTokenValidated = ValidateSessionAsync,
                };
            });

        services.AddAuthorization(options =>
        {
            options.AddPolicy(AuthPolicyNames.Admin, policy => policy.RequireRole(AuthRoleNames.Admin));
            options.AddPolicy(AuthPolicyNames.Staff, policy => policy.RequireRole(AuthRoleNames.Staff));
            options.AddPolicy(AuthPolicyNames.Customer, policy => policy.RequireRole(AuthRoleNames.Customer));
            options.AddPermissionPolicies();
        });
        services.AddScoped<IAuthorizationHandler, CurrentPermissionAuthorizationHandler>();

        services.AddRateLimiter(ConfigureRateLimiter);

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUserAccessor, HttpContextCurrentUserAccessor>();

        return services;
    }

    private static async Task ValidateSessionAsync(TokenValidatedContext context)
    {
        var principal = context.Principal;
        var userIdValue = principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? principal?.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value;
        var sessionIdValue = principal?.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sid)?.Value
            ?? principal?.FindFirst(System.Security.Claims.ClaimTypes.Sid)?.Value;

        if (!Guid.TryParse(userIdValue, out var userId) || !Guid.TryParse(sessionIdValue, out var sessionId))
        {
            context.Fail("The access token does not belong to a session.");
            return;
        }

        var validator = context.HttpContext.RequestServices.GetRequiredService<ISessionStateValidator>();
        if (!await validator.IsActiveAsync(userId, sessionId, context.HttpContext.RequestAborted))
        {
            context.Fail("The session has ended.");
        }
    }

    private static void ConfigureRateLimiter(RateLimiterOptions options)
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        options.OnRejected = (context, cancellationToken) =>
        {
            // The real wait of the fixed window when the limiter reports it, so clients do not wait longer than needed.
            var retryAfterSeconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
                ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
                : 60;
            context.HttpContext.Response.Headers.RetryAfter = retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var policyName = context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;
            var detail = policyName is RateLimitPolicyNames.NotificationTest or RateLimitPolicyNames.NotificationBroadcast
                ? $"Enviaste demasiadas notificaciones seguidas. Espera {retryAfterSeconds} segundos y vuelve a intentarlo."
                : "Hiciste demasiadas solicitudes seguidas. Espera un momento y vuelve a intentarlo.";
            return new ValueTask(Results.Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "Too many requests",
                detail: detail)
                .ExecuteAsync(context.HttpContext));
        };

        // Auth actions a user performs by hand (login/register): tight limit per client IP. One policy, so login and
        // register share the same quota per client. The key is the client as seen by Cloudflare (ClientIpResolver),
        // not the proxy hop. In-memory and per process: with several instances each keeps its own counters.
        options.AddPolicy(RateLimitPolicyNames.AuthCredentials, httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(GetClientIp(httpContext), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

        // Password reset emails: tighter limit to curb mailbox spam/abuse.
        options.AddPolicy(RateLimitPolicyNames.AuthPasswordReset, httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(GetClientIp(httpContext), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 3,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

        // Token refresh is triggered automatically by the app (not by hand), so it gets a roomier limit.
        options.AddPolicy(RateLimitPolicyNames.AuthRefresh, httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(GetClientIp(httpContext), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

        // BUG-112: sending notifications is limited per signed-in user, so a shared IP never blocks another admin.
        options.AddPolicy(RateLimitPolicyNames.NotificationTest, httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(GetUserPartition(httpContext), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = NotificationTestPermitLimit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

        options.AddPolicy(RateLimitPolicyNames.NotificationBroadcast, httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(GetUserPartition(httpContext), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = NotificationBroadcastPermitLimit,
                Window = TimeSpan.FromMinutes(10),
                QueueLimit = 0,
            }));
    }

    public const int NotificationTestPermitLimit = 3;
    public const int NotificationBroadcastPermitLimit = 5;

    private static string GetClientIp(HttpContext httpContext) => ClientIpResolver.Resolve(httpContext);

    // Signed-in user id; an anonymous call (rejected afterwards with 401) falls in the bucket of its IP.
    private static string GetUserPartition(HttpContext httpContext)
        => httpContext.User.TryGetUserId(out var userId) ? $"user:{userId:N}" : $"ip:{GetClientIp(httpContext)}";

    private static void ConfigureCorsPolicy(CorsPolicyBuilder policyBuilder, AppCorsOptions corsOptions, IHostEnvironment environment)
    {
        if (environment.IsDevelopment() && corsOptions.AllowWildcardOriginInDevelopment && corsOptions.AllowedOrigins.Contains("*", StringComparer.Ordinal))
        {
            policyBuilder.AllowAnyOrigin();
            policyBuilder.AllowAnyHeader();
            policyBuilder.AllowAnyMethod();
            return;
        }

        if (corsOptions.AllowedOrigins.Length == 0)
        {
            return;
        }

        policyBuilder.WithOrigins(corsOptions.AllowedOrigins);
        policyBuilder.AllowAnyHeader();
        policyBuilder.AllowAnyMethod();

        if (corsOptions.AllowCredentials)
        {
            policyBuilder.AllowCredentials();
        }
    }
}
