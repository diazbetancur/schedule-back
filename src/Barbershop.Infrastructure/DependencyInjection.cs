using Barbershop.Application.Customer;
using Barbershop.Application.Email;
using Barbershop.Application.Finance.Admin;
using Barbershop.Application.Landing;
using Barbershop.Application.Appointments;
using Barbershop.Application.Auth;
using Barbershop.Application.Authorization;
using Barbershop.Application.Availability;
using Barbershop.Application.Media;
using Barbershop.Application.Notifications;
using Barbershop.Application.PublicContent;
using Barbershop.Application.Reviews;
using Barbershop.Application.Services.Admin;
using Barbershop.Application.Staff.Admin;
using Barbershop.Application.Staff.SelfService;
using Barbershop.Application.Storage;
using Barbershop.Infrastructure.Appointments;
using Barbershop.Infrastructure.Authorization;
using Barbershop.Infrastructure.Availability;
using Barbershop.Infrastructure.Configuration;
using Barbershop.Infrastructure.Customer;
using Barbershop.Infrastructure.Email;
using Barbershop.Infrastructure.Finance;
using Barbershop.Infrastructure.Identity;
using Barbershop.Infrastructure.Landing;
using Barbershop.Infrastructure.Media;
using Barbershop.Infrastructure.Notifications;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Infrastructure.Reviews;
using Barbershop.Infrastructure.Services;
using Barbershop.Infrastructure.Staff;
using Barbershop.Infrastructure.Users;
using Barbershop.Application.Users.Admin;
using Barbershop.Infrastructure.Storage;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Text;

namespace Barbershop.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        services.AddValidatedOptions(configuration, environment);

        services.AddDbContext<AppDbContext>((serviceProvider, optionsBuilder) =>
        {
            var databaseOptions = serviceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value;

            optionsBuilder.UseNpgsql(databaseOptions.ConnectionString, npgsqlOptions =>
            {
                npgsqlOptions.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName);
                npgsqlOptions.CommandTimeout(databaseOptions.CommandTimeoutSeconds);

                if (OptionsValidationHelpers.IsConfigured(databaseOptions.AdminDatabase))
                {
                    npgsqlOptions.UseAdminDatabase(databaseOptions.AdminDatabase);
                }
            });

            if (databaseOptions.EnableDetailedErrors)
            {
                optionsBuilder.EnableDetailedErrors();
            }

            if (databaseOptions.EnableSensitiveDataLogging)
            {
                optionsBuilder.EnableSensitiveDataLogging();
            }
        });

        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IPasswordHasher<object>, PasswordHasher<object>>();
        services.AddScoped<IIdentitySeedService, IdentitySeedService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<AppointmentManagementService>();
        services.AddScoped<ICustomerAppointmentsService>(serviceProvider => serviceProvider.GetRequiredService<AppointmentManagementService>());
        services.AddScoped<IStaffAppointmentsService>(serviceProvider => serviceProvider.GetRequiredService<AppointmentManagementService>());
        services.AddScoped<IAdminAppointmentsService>(serviceProvider => serviceProvider.GetRequiredService<AppointmentManagementService>());
        services.AddScoped<AvailabilityManagementService>();
        services.AddScoped<IStaffAvailabilityService>(serviceProvider => serviceProvider.GetRequiredService<AvailabilityManagementService>());
        services.AddScoped<IAdminStaffAvailabilityService>(serviceProvider => serviceProvider.GetRequiredService<AvailabilityManagementService>());
        services.AddScoped<IPublicAvailabilityService, PublicAvailabilityService>();
        services.AddScoped<ReviewManagementService>();
        services.AddScoped<ICustomerReviewsService>(serviceProvider => serviceProvider.GetRequiredService<ReviewManagementService>());
        services.AddScoped<IPublicReviewsService>(serviceProvider => serviceProvider.GetRequiredService<ReviewManagementService>());
        services.AddScoped<StaffManagementService>();
        services.AddScoped<IAdminStaffService>(serviceProvider => serviceProvider.GetRequiredService<StaffManagementService>());
        services.AddScoped<IStaffProfileService>(serviceProvider => serviceProvider.GetRequiredService<StaffManagementService>());
        services.AddHttpClient("Resend", (sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<ResendOptions>>().Value;
            client.BaseAddress = new Uri(opts.ApiBaseUrl.TrimEnd('/') + "/");
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", opts.ApiKey);
        });
        services.AddScoped<IEmailSender, ResendEmailSender>();
        services.AddScoped<IPushSubscriptionService, PushSubscriptionService>();
        services.AddSingleton<WebPush.WebPushClient>();
        services.AddScoped<IPushNotificationSender, WebPushNotificationSender>();
        services.AddScoped<IAppointmentNotificationService, AppointmentNotificationService>();
        services.AddScoped<IAdminNotificationsService, AdminNotificationsService>();
        services.AddScoped<IImageTranscoder, MagickImageTranscoder>();
        services.AddScoped<IMediaAssetsService, MediaAssetManagementService>();
        services.AddScoped<ContentManagementService>();
        services.AddScoped<IPublicContentService>(serviceProvider => serviceProvider.GetRequiredService<ContentManagementService>());
        services.AddScoped<IAdminContentService>(serviceProvider => serviceProvider.GetRequiredService<ContentManagementService>());
        services.AddScoped<IPublicStaffService, PublicStaffService>();
        services.AddScoped<IFileStorageService, R2FileStorageService>();
        services.AddScoped<ICustomerProfileService, CustomerProfileService>();
        services.AddScoped<IAdminUsersService, AdminUsersService>();
        services.AddScoped<IAdminServicesService, ServiceManagementService>();
        services.AddScoped<IAdminRolesService, RolesManagementService>();
        services.AddScoped<IAdminIncomeService, IncomeManagementService>();
        services.AddScoped<IAdminFixedExpensesService, FixedExpenseManagementService>();
        services.AddScoped<IAdminExpensesService, ExpenseManagementService>();
        services.AddScoped<IAdminReportsService, ReportsQueryService>();

        return services;
    }

    private static IServiceCollection AddValidatedOptions(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var relaxedEnvironment = OptionsValidationHelpers.IsRelaxedEnvironment(environment);

        services.AddOptions<AppOptions>()
            .Bind(configuration.GetSection(AppOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(options => !string.IsNullOrWhiteSpace(options.ApiBasePath), $"{AppOptions.SectionName}:ApiBasePath is required.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.CorrelationIdHeaderName), $"{AppOptions.SectionName}:CorrelationIdHeaderName is required.")
            .ValidateOnStart();

        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(options => options.CommandTimeoutSeconds > 0, $"{DatabaseOptions.SectionName}:CommandTimeoutSeconds must be greater than zero.")
            .Validate(
                options => relaxedEnvironment || OptionsValidationHelpers.IsConfigured(options.ConnectionString),
                $"{DatabaseOptions.SectionName}:ConnectionString must be configured outside Development/Testing.")
            .ValidateOnStart();

        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .Validate(options => options.Enabled, $"{JwtOptions.SectionName}:Enabled cannot be false. JWT authentication is mandatory.")
            .Validate(options => OptionsValidationHelpers.IsConfigured(options.Issuer), $"{JwtOptions.SectionName}:Issuer must be configured.")
            .Validate(options => OptionsValidationHelpers.IsConfigured(options.Audience), $"{JwtOptions.SectionName}:Audience must be configured.")
            .Validate(options => OptionsValidationHelpers.IsConfigured(options.SigningKey), $"{JwtOptions.SectionName}:SigningKey must be configured.")
            .Validate(options => Encoding.UTF8.GetByteCount(options.SigningKey) >= 32, $"{JwtOptions.SectionName}:SigningKey must be at least 32 bytes.")
            .Validate(options => options.AccessTokenMinutes > 0, $"{JwtOptions.SectionName}:AccessTokenMinutes must be greater than zero.")
            .Validate(options => options.RefreshTokenDays > 0, $"{JwtOptions.SectionName}:RefreshTokenDays must be greater than zero.")
            .ValidateOnStart();

        services.AddOptions<SeedAdminOptions>()
            .Bind(configuration.GetSection(SeedAdminOptions.SectionName))
            .Validate(
                options => !options.Enabled || (OptionsValidationHelpers.IsConfigured(options.Email)
                    && OptionsValidationHelpers.IsConfigured(options.Password)
                    && OptionsValidationHelpers.IsConfigured(options.FullName)),
                $"{SeedAdminOptions.SectionName} must include Email, Password, and FullName when enabled.")
            .ValidateOnStart();

        services.AddOptions<CorsOptions>()
            .Bind(configuration.GetSection(CorsOptions.SectionName))
            .Validate(options => relaxedEnvironment || options.AllowedOrigins.Length > 0, $"{CorsOptions.SectionName}:AllowedOrigins must be configured outside Development/Testing.")
            .Validate(options => relaxedEnvironment || !options.AllowedOrigins.Contains("*", StringComparer.Ordinal), $"{CorsOptions.SectionName}:AllowedOrigins cannot contain '*' outside Development/Testing.")
            .ValidateOnStart();

        services.AddOptions<EmailOptions>()
            .Bind(configuration.GetSection(EmailOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(options => !options.Enabled || !string.IsNullOrWhiteSpace(options.DefaultFromName), $"{EmailOptions.SectionName}:DefaultFromName is required when email is enabled.")
            .ValidateOnStart();

        services.AddOptions<WebPushOptions>()
            .Bind(configuration.GetSection(WebPushOptions.SectionName))
            .Validate(
                options => !options.Enabled || (OptionsValidationHelpers.IsConfigured(options.PublicKey)
                    && OptionsValidationHelpers.IsConfigured(options.PrivateKey)
                    && OptionsValidationHelpers.IsConfigured(options.ContactEmail)),
                $"{WebPushOptions.SectionName} must include PublicKey, PrivateKey, and ContactEmail when enabled.")
            .ValidateOnStart();

        services.AddOptions<FileStorageOptions>()
            .Bind(configuration.GetSection(FileStorageOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(options => options.AllowedContentTypes.Length > 0, $"{FileStorageOptions.SectionName}:AllowedContentTypes must contain at least one value.")
            .Validate(options => options.MaxUploadBytes > 0, $"{FileStorageOptions.SectionName}:MaxUploadBytes must be greater than zero.")
            .ValidateOnStart();

        var emailOptions = configuration.GetSection(EmailOptions.SectionName).Get<EmailOptions>() ?? new EmailOptions();

        services.AddOptions<ResendOptions>()
            .Bind(configuration.GetSection(ResendOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => !emailOptions.Enabled || !string.Equals(emailOptions.Provider, "Resend", StringComparison.OrdinalIgnoreCase) || relaxedEnvironment || OptionsValidationHelpers.IsConfigured(options.ApiKey),
                $"{ResendOptions.SectionName}:ApiKey must be configured when Resend is enabled outside Development/Testing.")
            .ValidateOnStart();

        services.AddOptions<R2StorageOptions>()
            .Bind(configuration.GetSection(R2StorageOptions.SectionName))
            .Validate(
                options => relaxedEnvironment || (OptionsValidationHelpers.IsConfigured(options.AccountId)
                    && OptionsValidationHelpers.IsConfigured(options.BucketName)
                    && OptionsValidationHelpers.IsConfigured(options.AccessKeyId)
                    && OptionsValidationHelpers.IsConfigured(options.SecretAccessKey)
                    && OptionsValidationHelpers.IsConfigured(options.Endpoint)
                    && OptionsValidationHelpers.IsConfigured(options.PublicBaseUrl)),
                $"{R2StorageOptions.SectionName} must be fully configured outside Development/Testing.")
            .ValidateOnStart();

        return services;
    }
}
