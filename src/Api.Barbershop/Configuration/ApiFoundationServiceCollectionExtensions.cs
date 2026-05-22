using Api.Barbershop.Middleware;
using Barbershop.Application.Auth;
using Barbershop.Infrastructure.Configuration;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using System.Text.Json.Serialization;
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
        });

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
            });

        services.AddAuthorization(options =>
        {
            options.AddPolicy(AuthPolicyNames.Admin, policy => policy.RequireRole(AuthRoleNames.Admin));
            options.AddPolicy(AuthPolicyNames.Staff, policy => policy.RequireRole(AuthRoleNames.Staff));
            options.AddPolicy(AuthPolicyNames.Customer, policy => policy.RequireRole(AuthRoleNames.Customer));
        });

        return services;
    }

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
