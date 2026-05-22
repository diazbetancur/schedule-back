using Barbershop.Infrastructure.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace Barbershop.Infrastructure.Persistence;

public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    private const string UserSecretsId = "barbershop-api-local";

    public AppDbContext CreateDbContext(string[] args)
    {
        var environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Development";
        var basePath = ResolveConfigurationBasePath();

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(basePath, "appsettings.json"), optional: false)
            .AddJsonFile(Path.Combine(basePath, $"appsettings.{environmentName}.json"), optional: true)
            .AddUserSecrets(UserSecretsId, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();

        var databaseOptions = configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>() ?? new DatabaseOptions();
        var connectionString = OptionsValidationHelpers.IsConfigured(databaseOptions.ConnectionString)
            ? databaseOptions.ConnectionString
            : "Host=localhost;Port=5432;Database=barbershop_dev;Username=postgres;Password=__CHANGE_ME__";

        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        optionsBuilder.UseNpgsql(connectionString, npgsqlOptions =>
        {
            npgsqlOptions.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName);
            npgsqlOptions.CommandTimeout(databaseOptions.CommandTimeoutSeconds > 0 ? databaseOptions.CommandTimeoutSeconds : 30);

            if (OptionsValidationHelpers.IsConfigured(databaseOptions.AdminDatabase))
            {
                npgsqlOptions.UseAdminDatabase(databaseOptions.AdminDatabase);
            }
        });

        return new AppDbContext(optionsBuilder.Options);
    }

    private static string ResolveConfigurationBasePath()
    {
        var currentDirectory = Directory.GetCurrentDirectory();
        var candidates = new[]
        {
            currentDirectory,
            Path.Combine(currentDirectory, "..", "Api.Barbershop"),
            Path.Combine(currentDirectory, "..", "..", "Api.Barbershop"),
            Path.Combine(currentDirectory, "src", "Api.Barbershop")
        };

        var resolvedPath = candidates
            .Select(Path.GetFullPath)
            .FirstOrDefault(path => File.Exists(Path.Combine(path, "appsettings.json")));

        return resolvedPath ?? currentDirectory;
    }
}
