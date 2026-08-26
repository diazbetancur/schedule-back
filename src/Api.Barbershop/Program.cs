using Api.Barbershop.Features.Admin.Appointments;
using Api.Barbershop.Configuration;
using Api.Barbershop.Features.Admin.Finance;
using Api.Barbershop.Features.Admin.Landing;
using Api.Barbershop.Features.Admin.Media;
using Api.Barbershop.Features.Admin.Notifications;
using Api.Barbershop.Features.Admin.Roles;
using Api.Barbershop.Features.Admin.Services;
using Api.Barbershop.Features.Admin.Staff;
using Api.Barbershop.Features.Admin.Users;
using Api.Barbershop.Features.Auth;
using Api.Barbershop.Features.Customer.Appointments;
using Api.Barbershop.Features.Customer.Profile;
using Api.Barbershop.Features.Customer.Reviews;
using Api.Barbershop.Features.Notifications;
using Api.Barbershop.Features.Public.Availability;
using Api.Barbershop.Features.Public.Content;
using Api.Barbershop.Features.Public.Reviews;
using Api.Barbershop.Features.Public.Staff;
using Api.Barbershop.Features.Staff.Appointments;
using Api.Barbershop.Features.Staff.Availability;
using Api.Barbershop.Features.Staff.Profile;
using Api.Barbershop.Middleware;
using Barbershop.Application;
using Barbershop.Infrastructure;
using Barbershop.Infrastructure.Configuration;
using Barbershop.Infrastructure.Identity;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);
builder.Services.AddApiFoundation(builder.Configuration, builder.Environment);

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    // IIdentitySeedService.EnsureSeededAsync internally runs the pending-migrations
    // check/migration (EnsureDatabaseAsync) before seeding roles/permissions, so it
    // replaces the separate Database.MigrateAsync() call that used to live here.
    // Running it on every startup (not just on first login) keeps the permission
    // catalog and the Admin role's full-catalog grant in sync from the moment the
    // app comes up — see the plan's Global Constraint #3 ("el seed sincroniza en
    // cada arranque"). It is idempotent and safe to call unconditionally in every
    // environment (the admin-user-creation part stays gated behind
    // SeedAdmin__Enabled/Development/Testing).
    var identitySeedService = scope.ServiceProvider.GetRequiredService<IIdentitySeedService>();
    await identitySeedService.EnsureSeededAsync();
}

// Cuando el backend corre detrás de un proxy HTTPS (Vercel, Railway, Render, etc.)
// el proxy termina TLS y reenvía HTTP internamente. Sin esto, IsHttps = false y el
// cookie de refresh token se setea con SameSite=Lax, lo que impide que el browser
// lo envíe en llamadas fetch() cross-origin → la sesión no persiste entre arranques.
var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
};
forwardedHeadersOptions.KnownNetworks.Clear();
forwardedHeadersOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeadersOptions);

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseCors(CorsOptions.PolicyName);
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

var appOptions = app.Services.GetRequiredService<IOptions<AppOptions>>().Value;
var api = app.MapGroup(appOptions.ApiBasePath);
api.MapAuthEndpoints();
api.MapCustomerAppointmentsEndpoints();
api.MapCustomerProfileEndpoints();
api.MapCustomerReviewsEndpoints();
api.MapAdminStaffEndpoints();
api.MapAdminServicesEndpoints();
api.MapAdminIncomeEndpoints();
api.MapAdminFixedExpensesEndpoints();
api.MapAdminExpensesEndpoints();
api.MapAdminReportsEndpoints();
api.MapAdminUsersEndpoints();
api.MapAdminRolesEndpoints();
api.MapAdminStaffAvailabilityEndpoints();
api.MapAdminAppointmentsEndpoints();
api.MapAdminNotificationsEndpoints();
api.MapMediaEndpoints();
api.MapNotificationsEndpoints();
api.MapAdminContentEndpoints();
api.MapPublicAvailabilityEndpoints();
api.MapPublicContentEndpoints();
api.MapPublicStaffEndpoints();
api.MapPublicStaffReviewsEndpoints();
api.MapStaffAppointmentsEndpoints();
api.MapStaffAvailabilityEndpoints();
api.MapStaffProfileEndpoints();

app.MapGet("/", (HttpContext httpContext) => Results.Ok(new
{
    service = appOptions.Name,
    version = appOptions.Version,
    environment = app.Environment.EnvironmentName,
    correlationId = httpContext.GetCorrelationId()
}))
.WithName("GetRoot");

app.MapHealthChecks("/health")
   .WithName("GetHealth");

// GET + HEAD: monitores de uptime (UptimeRobot, etc.) suelen pingear con HEAD.
// Un MapGet por sí solo respondería 405 a HEAD.
api.MapMethods("/health", new[] { "GET", "HEAD" }, (HttpContext httpContext) => Results.Ok(new
{
    status = "Healthy",
    correlationId = httpContext.GetCorrelationId()
}))
.WithName("GetApiHealth");

// Endpoint ultra liviano para keep-alive (Render free duerme tras inactividad).
// No toca BD ni dependencias: solo confirma que el proceso responde.
app.MapMethods("/ping", new[] { "GET", "HEAD" }, () => Results.Ok("OK"))
   .WithName("GetPing");

app.Run();

public partial class Program;
