using Api.Barbershop.Features.Admin.Appointments;
using Api.Barbershop.Configuration;
using Api.Barbershop.Features.Admin.Landing;
using Api.Barbershop.Features.Admin.Media;
using Api.Barbershop.Features.Admin.Notifications;
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
using Barbershop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
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
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
}

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
api.MapAdminUsersEndpoints();
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

api.MapGet("/health", (HttpContext httpContext) => Results.Ok(new
{
    status = "Healthy",
    correlationId = httpContext.GetCorrelationId()
}))
.WithName("GetApiHealth");

app.Run();

public partial class Program;
