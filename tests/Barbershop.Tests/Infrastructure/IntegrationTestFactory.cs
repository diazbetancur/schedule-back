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
