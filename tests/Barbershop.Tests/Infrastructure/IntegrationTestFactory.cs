using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Barbershop.Infrastructure.Persistence;

namespace Barbershop.Tests.Infrastructure;

public sealed class IntegrationTestFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;

    public IntegrationTestFactory(string connectionString)
    {
        _connectionString = connectionString;

        // Program.cs reads builder.Configuration synchronously (AddApiFoundation validates
        // Jwt options) before builder.Build() runs. WebApplicationFactory's ConfigureAppConfiguration
        // hook is only merged into configuration AT Build() time, via the deferred IHostBuilder
        // bridge - too late for that eager read. Real process environment variables are read by
        // WebApplication.CreateBuilder(args) immediately, so they're the only override that's
        // visible in time. The IntegrationTestCollection disables parallelization, so mutating
        // process-wide env vars here is safe (tests never run concurrently against each other).
        Environment.SetEnvironmentVariable("Database__ConnectionString", _connectionString);
        Environment.SetEnvironmentVariable("Jwt__Enabled", "true");
        Environment.SetEnvironmentVariable("Jwt__Issuer", "Barbershop.Tests");
        Environment.SetEnvironmentVariable("Jwt__Audience", "Barbershop.Tests.Client");
        Environment.SetEnvironmentVariable("Jwt__SigningKey", "12345678901234567890123456789012-auth-tests-key");
        Environment.SetEnvironmentVariable("Jwt__RequireHttpsMetadata", "false");
        Environment.SetEnvironmentVariable("SeedAdmin__Enabled", "false");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configurationBuilder) =>
        {
            configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] = _connectionString,
                ["Jwt:Enabled"] = "true",
                ["Jwt:Issuer"] = "Barbershop.Tests",
                ["Jwt:Audience"] = "Barbershop.Tests.Client",
                ["Jwt:SigningKey"] = "12345678901234567890123456789012-auth-tests-key",
                ["Jwt:RequireHttpsMetadata"] = "false",
                ["SeedAdmin:Enabled"] = "false"
            });
        });
    }

    public async Task ResetDatabaseAsync()
    {
        using var scope = Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await dbContext.Database.EnsureDeletedAsync();
        await dbContext.Database.MigrateAsync();
    }
}
