using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Barbershop.Application.Auth;
using Barbershop.Application.Services.Admin;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;
using Barbershop.Domain.Services;

namespace Barbershop.Tests.Features.Services;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "AdminServicesHttpIntegration")]
public sealed class AdminServicesHttpIntegrationTests
{
  private const string DefaultPassword = "Secret123!";

  private readonly PostgresContainerFixture _postgres;
  private readonly ITestOutputHelper _output;

  public AdminServicesHttpIntegrationTests(PostgresContainerFixture postgres, ITestOutputHelper output)
  {
    _postgres = postgres;
    _output = output;
  }

  [Fact]
  public async Task Admin_CanRunFullServiceCrudLifecycle()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var admin = await RegisterAndPromoteAdminAsync(context, "crud");
    AuthenticateAs(context.Client, admin.AccessToken);

    using var createResponse = await context.Client.PostAsJsonAsync(
        "/api/v1/admin/services", new ServiceCreateRequest("Corte de Cabello", 25000, BusinessPercentage: 20, GeneralServiceId: ServiceCatalogDefaults.CorteGeneralServiceId));
    Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
    var created = await createResponse.Content.ReadFromJsonAsync<ServiceView>();
    Assert.NotNull(created);
    Assert.Equal("Corte de Cabello", created!.Name);
    Assert.Equal(25000, created.BasePrice);
    Assert.Equal(20, created.BusinessPercentage);
    Assert.True(created.IsActive);

    using var listResponse = await context.Client.GetAsync("/api/v1/admin/services");
    Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
    var list = await listResponse.Content.ReadPageItemsAsync<ServiceView>();
    Assert.Contains(list!, s => s.Id == created.Id);

    using var updateResponse = await context.Client.PutAsJsonAsync(
        $"/api/v1/admin/services/{created.Id}", new ServiceUpdateRequest("Corte Premium", 30000, BusinessPercentage: 35));
    Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
    var updated = await updateResponse.Content.ReadFromJsonAsync<ServiceView>();
    Assert.Equal("Corte Premium", updated!.Name);
    Assert.Equal(30000, updated.BasePrice);
    Assert.Equal(35, updated.BusinessPercentage);

    using var statusResponse = await context.Client.PatchAsJsonAsync(
        $"/api/v1/admin/services/{created.Id}/status", new ServiceStatusUpdateRequest(false));
    Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
    var deactivated = await statusResponse.Content.ReadFromJsonAsync<ServiceView>();
    Assert.False(deactivated!.IsActive);

    using var deleteResponse = await context.Client.DeleteAsync($"/api/v1/admin/services/{created.Id}");
    Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

    using var listAfterDelete = await context.Client.GetAsync("/api/v1/admin/services");
    var listAfter = await listAfterDelete.Content.ReadPageItemsAsync<ServiceView>();
    Assert.DoesNotContain(listAfter!, s => s.Id == created.Id);
  }

  [Fact]
  public async Task CreateService_RejectsDuplicateName()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var admin = await RegisterAndPromoteAdminAsync(context, "dup");
    AuthenticateAs(context.Client, admin.AccessToken);

    using var first = await context.Client.PostAsJsonAsync(
        "/api/v1/admin/services", new ServiceCreateRequest("Barba", 10000, GeneralServiceId: ServiceCatalogDefaults.CorteGeneralServiceId));
    Assert.Equal(HttpStatusCode.Created, first.StatusCode);

    using var second = await context.Client.PostAsJsonAsync(
        "/api/v1/admin/services", new ServiceCreateRequest("barba", 12000, GeneralServiceId: ServiceCatalogDefaults.CorteGeneralServiceId));
    Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
  }

  [Fact]
  public async Task CreateService_AsPlainCustomer_ReturnsForbidden()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var customer = await RegisterCustomerAsync(context.Client, "forbidden");
    AuthenticateAs(context.Client, customer.AccessToken);

    using var response = await context.Client.PostAsJsonAsync(
        "/api/v1/admin/services", new ServiceCreateRequest("Tinte", 40000, GeneralServiceId: ServiceCatalogDefaults.CorteGeneralServiceId));
    Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
  }

  [Fact]
  public async Task ListServices_Unauthenticated_ReturnsUnauthorized()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    using var response = await context.Client.GetAsync("/api/v1/admin/services");
    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
  }

  [Fact]
  public async Task Admin_ManagesCategoriesAndGeneralServices_AndMovesARate()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var admin = await RegisterAndPromoteAdminAsync(context, "catalog");
    AuthenticateAs(context.Client, admin.AccessToken);

    // The migration seeded Barbería and Corte.
    var categories = await context.Client.GetFromJsonAsync<List<ServiceCategoryView>>("/api/v1/admin/service-categories");
    Assert.Equal([ServiceCatalogDefaults.BarberiaCategoryId], categories!.Select(category => category.Id));

    using var createCategory = await context.Client.PostAsJsonAsync(
        "/api/v1/admin/service-categories", new ServiceCategoryCreateRequest("Cuidado facial", 1));
    Assert.Equal(HttpStatusCode.Created, createCategory.StatusCode);
    var care = (await createCategory.Content.ReadFromJsonAsync<ServiceCategoryView>())!;

    using var createGeneral = await context.Client.PostAsJsonAsync(
        "/api/v1/admin/general-services", new GeneralServiceCreateRequest(care.Id, "Limpieza facial", "Limpieza profunda", 0));
    Assert.Equal(HttpStatusCode.Created, createGeneral.StatusCode);
    var cleaning = (await createGeneral.Content.ReadFromJsonAsync<GeneralServiceView>())!;
    Assert.Equal(("Cuidado facial", "Limpieza facial"), (cleaning.CategoryName, cleaning.Name));

    // Unique per category: an equivalent name in the same category is a conflict, the same name elsewhere is fine.
    using var duplicate = await context.Client.PostAsJsonAsync(
        "/api/v1/admin/general-services", new GeneralServiceCreateRequest(care.Id, " LIMPIEZA FACIAL "));
    Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    using var sameNameElsewhere = await context.Client.PostAsJsonAsync(
        "/api/v1/admin/general-services", new GeneralServiceCreateRequest(care.Id, "Corte"));
    Assert.Equal(HttpStatusCode.Created, sameNameElsewhere.StatusCode);

    // A rate must name its general service; then it can be moved to another one.
    using var withoutGeneral = await context.Client.PostAsJsonAsync("/api/v1/admin/services", new { name = "Mascarilla", basePrice = 30000 });
    Assert.Equal(HttpStatusCode.UnprocessableEntity, withoutGeneral.StatusCode);
    using var createRate = await context.Client.PostAsJsonAsync(
        "/api/v1/admin/services", new ServiceCreateRequest("Mascarilla", 30000, 40, GeneralServiceId: ServiceCatalogDefaults.CorteGeneralServiceId));
    var rate = (await createRate.Content.ReadFromJsonAsync<ServiceView>())!;
    using var moveRate = await context.Client.PutAsJsonAsync(
        $"/api/v1/admin/services/{rate.Id}", new ServiceUpdateRequest("Mascarilla", 30000, 40, GeneralServiceId: cleaning.Id));
    Assert.Equal(HttpStatusCode.OK, moveRate.StatusCode);
    var moved = (await moveRate.Content.ReadFromJsonAsync<ServiceView>())!;
    Assert.Equal((rate.Id, cleaning.Id, "Limpieza facial", care.Id, "Cuidado facial"),
        (moved.Id, moved.GeneralServiceId, moved.GeneralServiceName, moved.CategoryId, moved.CategoryName));

    // Dependencies block deactivation and deletion with a conflict.
    using var deactivateCategory = await context.Client.PatchAsJsonAsync(
        $"/api/v1/admin/service-categories/{care.Id}/status", new ServiceCategoryStatusUpdateRequest(false));
    Assert.Equal(HttpStatusCode.Conflict, deactivateCategory.StatusCode);
    using var deleteGeneral = await context.Client.DeleteAsync($"/api/v1/admin/general-services/{cleaning.Id}");
    Assert.Equal(HttpStatusCode.Conflict, deleteGeneral.StatusCode);

    var filtered = await context.Client.GetFromJsonAsync<List<GeneralServiceView>>($"/api/v1/admin/general-services?categoryId={care.Id}");
    Assert.Equal(["Corte", "Limpieza facial"], filtered!.Select(service => service.Name).Order());
    var withCounts = await context.Client.GetFromJsonAsync<GeneralServiceView>($"/api/v1/admin/general-services/{cleaning.Id}");
    Assert.Equal((1, 1), (withCounts!.RateCount, withCounts.ActiveRateCount));
  }

  [Fact]
  public async Task TheCatalogMasters_AreForAdministratorsOnly()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    using (var anonymous = await context.Client.GetAsync("/api/v1/admin/service-categories"))
    {
      Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    var customer = await RegisterCustomerAsync(context.Client, "catalog-forbidden");
    AuthenticateAs(context.Client, customer.AccessToken);
    using (var categories = await context.Client.GetAsync("/api/v1/admin/service-categories"))
    {
      Assert.Equal(HttpStatusCode.Forbidden, categories.StatusCode);
    }

    using var general = await context.Client.PostAsJsonAsync(
        "/api/v1/admin/general-services", new GeneralServiceCreateRequest(ServiceCatalogDefaults.BarberiaCategoryId, "Barba"));
    Assert.Equal(HttpStatusCode.Forbidden, general.StatusCode);
  }

  private async Task<AuthSession> RegisterAndPromoteAdminAsync(TestHttpContext context, string label)
  {
    var email = UniqueEmail($"admin-{label}");
    using (var register = await context.Client.PostAsJsonAsync(
        "/api/v1/auth/register", new RegisterRequest($"Admin {label}", email, DefaultPassword, null)))
    {
      Assert.Equal(HttpStatusCode.Created, register.StatusCode);
    }

    await WithScopeAsync(context.Factory, async serviceProvider =>
    {
      var db = serviceProvider.GetRequiredService<AppDbContext>();
      var user = await db.Users
          .Include(candidate => candidate.UserRoles)
          .SingleAsync(candidate => candidate.NormalizedEmail == email.ToUpperInvariant());
      var adminRole = await db.Roles.SingleAsync(role => role.NormalizedName == RoleNames.Admin.ToUpperInvariant());
      if (user.UserRoles.All(assignment => assignment.RoleId != adminRole.Id))
      {
        user.UserRoles.Add(new UserRole(user.Id, adminRole.Id, DateTime.UtcNow));
      }
      await db.SaveChangesAsync();
    });

    using var login = await context.Client.PostAsJsonAsync(
        "/api/v1/auth/login", new LoginRequest(email, DefaultPassword));
    Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    var payload = await login.Content.ReadFromJsonAsync<TokenResponse>();
    Assert.NotNull(payload);
    return new AuthSession(payload!.User.Id, payload.AccessToken);
  }

  private static async Task<AuthSession> RegisterCustomerAsync(HttpClient client, string label)
  {
    using var response = await client.PostAsJsonAsync(
        "/api/v1/auth/register",
        new RegisterRequest($"Customer {label}", UniqueEmail($"customer-{label}"), DefaultPassword, null));
    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    var payload = await response.Content.ReadFromJsonAsync<TokenResponse>();
    Assert.NotNull(payload);
    return new AuthSession(payload!.User.Id, payload.AccessToken);
  }

  private async Task<TestHttpContext?> CreateContextAsync()
  {
    if (!_postgres.IsAvailable)
    {
      _output.WriteLine(_postgres.UnavailableReason ?? "PostgreSQL Testcontainer is unavailable.");
      return null;
    }

    var factory = new IntegrationTestFactory(_postgres.ConnectionString);
    await factory.ResetDatabaseAsync();
    return new TestHttpContext(factory, factory.CreateClient());
  }

  private static async Task WithScopeAsync(IntegrationTestFactory factory, Func<IServiceProvider, Task> action)
  {
    using var scope = factory.Services.CreateScope();
    await action(scope.ServiceProvider);
  }

  private static void AuthenticateAs(HttpClient client, string accessToken)
      => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

  private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@example.com";

  private sealed record AuthSession(Guid UserId, string AccessToken);

  private sealed class TestHttpContext : IDisposable
  {
    public TestHttpContext(IntegrationTestFactory factory, HttpClient client)
    {
      Factory = factory;
      Client = client;
    }

    public IntegrationTestFactory Factory { get; }
    public HttpClient Client { get; }

    public void Dispose()
    {
      Client.Dispose();
      Factory.Dispose();
    }
  }
}
