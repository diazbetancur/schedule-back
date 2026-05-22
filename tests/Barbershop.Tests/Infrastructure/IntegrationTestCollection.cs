using Xunit;

namespace Barbershop.Tests.Infrastructure;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class IntegrationTestCollection : ICollectionFixture<PostgresContainerFixture>
{
  public const string Name = "PostgreSqlIntegrationCollection";
}
