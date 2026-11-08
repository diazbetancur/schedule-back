using Testcontainers.PostgreSql;
using Xunit;

namespace Barbershop.Tests.Infrastructure;

/// <summary>
/// A disposable PostgreSQL for integration tests. When it cannot start, the tests using it fail with the reason, so a
/// run without Docker never looks green. Setting <see cref="AllowSkipVariable"/>=1 opts out explicitly: the tests then
/// see <see cref="IsAvailable"/> false and return without checking anything.
/// </summary>
public sealed class PostgresContainerFixture : IAsyncLifetime
{
    public const string AllowSkipVariable = "BARBERSHOP_ALLOW_SKIP_POSTGRES_TESTS";

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

            if (Environment.GetEnvironmentVariable(AllowSkipVariable) != "1")
            {
                throw new InvalidOperationException(
                    $"{UnavailableReason} Set {AllowSkipVariable}=1 to skip these tests on purpose.", exception);
            }
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
