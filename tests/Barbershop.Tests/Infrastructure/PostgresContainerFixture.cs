using Testcontainers.PostgreSql;
using Xunit;

namespace Barbershop.Tests.Infrastructure;

public sealed class PostgresContainerFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public string ConnectionString => IsAvailable && _container is not null ? _container.GetConnectionString() : string.Empty;

    public bool IsAvailable { get; private set; }

    public string? UnavailableReason { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("barbershop_tests")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .Build();

            await _container.StartAsync();
            IsAvailable = true;
            UnavailableReason = null;
        }
        catch (Exception exception)
        {
            IsAvailable = false;
            UnavailableReason =
                "PostgreSQL Testcontainer is unavailable. "
                + "Enable Docker/Testcontainers to run HTTP integration tests. "
                + $"Details: {exception.Message}";
        }
    }

    public async Task DisposeAsync()
    {
        if (IsAvailable)
        {
            await _container!.DisposeAsync().AsTask();
        }
    }
}