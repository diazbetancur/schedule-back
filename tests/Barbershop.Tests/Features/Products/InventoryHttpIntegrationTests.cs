using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Barbershop.Application.Auth;
using Barbershop.Application.Authorization;
using Barbershop.Application.Common.Paging;
using Barbershop.Application.Products;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Barbershop.Tests.Features.Products;

/// <summary>
/// ADR-0020 stage 1 over HTTP: the inventory permissions. Reading needs Admin or any inventory permission; receipts and
/// the minimum stock need inventory.receive; adjustments need inventory.adjust, which is critical, so revoking it denies
/// the next request with the same token (ADR-0005). One user per test (sign-in limit per IP, SONDA BUG-117).
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "InventoryHttpIntegration")]
public sealed class InventoryHttpIntegrationTests
{
  private const string DefaultPassword = "Secret123!";

  private readonly PostgresContainerFixture _postgres;
  private readonly ITestOutputHelper _output;

  public InventoryHttpIntegrationTests(PostgresContainerFixture postgres, ITestOutputHelper output)
  {
    _postgres = postgres;
    _output = output;
  }

  [Fact]
  public async Task Admin_ReceivesAdjustsAndReadsTheCard()
  {
    using var factory = await CreateFactoryAsync();
    if (factory is null) return;
    using var client = factory.CreateClient();
    var admin = await RegisterAdminAsync(factory, client, "inventory");
    var productId = await CreateProductAsync(client, admin.AccessToken);

    using (var receipt = await client.PostAsJsonAsync(
               $"/api/v1/admin/inventory/{productId}/receipts",
               new { IdempotencyKey = Guid.NewGuid(), Quantity = 10, UnitCost = 20000, CreateExpense = true }))
    {
      Assert.Equal(HttpStatusCode.OK, receipt.StatusCode);
      var result = await receipt.Content.ReadFromJsonAsync<InventoryMovementResultView>();
      Assert.Equal(10, result!.Item.OnHand);
      Assert.NotNull(result.Movement.ExpenseEntryId);
    }

    using (var adjust = await client.PostAsJsonAsync(
               $"/api/v1/admin/inventory/{productId}/adjustments",
               new { IdempotencyKey = Guid.NewGuid(), Direction = "Out", Quantity = 11, Reason = "Conteo físico" }))
    {
      Assert.Equal(HttpStatusCode.Conflict, adjust.StatusCode);
    }

    using (var minStock = await client.PutAsJsonAsync($"/api/v1/admin/inventory/{productId}/min-stock", new { MinStock = 12 }))
    {
      Assert.Equal(HttpStatusCode.OK, minStock.StatusCode);
    }

    var summary = await client.GetFromJsonAsync<InventorySummaryView>("/api/v1/admin/inventory/summary");
    Assert.Equal(1, summary!.LowStockCount);
    Assert.Equal(200000, summary.StockValue);
    var card = await client.GetFromJsonAsync<PagedResult<InventoryMovementView>>($"/api/v1/admin/inventory/{productId}/movements");
    Assert.Equal(1, card!.TotalCount);
    var list = await client.GetFromJsonAsync<PagedResult<InventoryItemView>>("/api/v1/admin/inventory?lowStock=true");
    Assert.Equal([productId], list!.Items.Select(item => item.ProductId));

    using var missing = await client.PostAsJsonAsync(
        $"/api/v1/admin/inventory/{productId}/receipts", new { IdempotencyKey = Guid.NewGuid(), UnitCost = 1000 });
    Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);
  }

  [Fact]
  public async Task ARoleWithInventoryView_ReadsButCannotReceiveOrAdjust()
  {
    using var factory = await CreateFactoryAsync();
    if (factory is null) return;
    using var client = factory.CreateClient();
    var admin = await RegisterAdminAsync(factory, client, "viewer");
    var productId = await CreateProductAsync(client, admin.AccessToken);
    var permissions = await GetPermissionsAsync(client, admin.AccessToken);
    var role = await CreateRoleAsync(client, admin.AccessToken, "Consulta de inventario", permissions[PermissionCodes.InventoryView]);
    var viewer = await CreateUserWithRolesAsync(client, admin.AccessToken, "viewer", role.Id);

    AuthenticateAs(client, viewer.AccessToken);
    Assert.Equal(HttpStatusCode.OK, await StatusAsync(client.GetAsync("/api/v1/admin/inventory")));
    Assert.Equal(HttpStatusCode.OK, await StatusAsync(client.GetAsync("/api/v1/admin/inventory/summary")));
    Assert.Equal(HttpStatusCode.OK, await StatusAsync(client.GetAsync($"/api/v1/admin/inventory/{productId}/movements")));
    Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(client.PostAsJsonAsync(
        $"/api/v1/admin/inventory/{productId}/receipts", new { IdempotencyKey = Guid.NewGuid(), Quantity = 1, UnitCost = 1000 })));
    Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(client.PostAsJsonAsync(
        $"/api/v1/admin/inventory/{productId}/adjustments",
        new { IdempotencyKey = Guid.NewGuid(), Direction = "In", Quantity = 1, Reason = "Conteo físico" })));
    Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(client.PutAsJsonAsync(
        $"/api/v1/admin/inventory/{productId}/min-stock", new { MinStock = 1 })));
    // Products themselves stay Admin-only.
    Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(client.GetAsync("/api/v1/admin/products")));
  }

  [Fact]
  public async Task ARoleWithInventoryAdjust_Adjusts_AndLosesItWithTheSameTokenWhenRevoked()
  {
    using var factory = await CreateFactoryAsync();
    if (factory is null) return;
    using var client = factory.CreateClient();
    var admin = await RegisterAdminAsync(factory, client, "adjust");
    var productId = await CreateProductAsync(client, admin.AccessToken);
    var permissions = await GetPermissionsAsync(client, admin.AccessToken);
    var role = await CreateRoleAsync(client, admin.AccessToken, "Ajustes de inventario", permissions[PermissionCodes.InventoryAdjust]);
    var adjuster = await CreateUserWithRolesAsync(client, admin.AccessToken, "adjuster", role.Id);

    AuthenticateAs(client, adjuster.AccessToken);
    Assert.Equal(HttpStatusCode.OK, await StatusAsync(client.GetAsync("/api/v1/admin/inventory")));
    Assert.Equal(HttpStatusCode.OK, await StatusAsync(client.PostAsJsonAsync(
        $"/api/v1/admin/inventory/{productId}/adjustments",
        new { IdempotencyKey = Guid.NewGuid(), Direction = "In", Quantity = 2, Reason = "Conteo físico" })));
    Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(client.PostAsJsonAsync(
        $"/api/v1/admin/inventory/{productId}/receipts", new { IdempotencyKey = Guid.NewGuid(), Quantity = 1, UnitCost = 1000 })));

    AuthenticateAs(client, admin.AccessToken);
    using (var update = await client.PutAsJsonAsync($"/api/v1/admin/roles/{role.Id}", new RoleUpdateRequest("Ajustes de inventario", [])))
    {
      Assert.Equal(HttpStatusCode.OK, update.StatusCode);
    }

    AuthenticateAs(client, adjuster.AccessToken);
    Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(client.PostAsJsonAsync(
        $"/api/v1/admin/inventory/{productId}/adjustments",
        new { IdempotencyKey = Guid.NewGuid(), Direction = "In", Quantity = 1, Reason = "Conteo físico" })));
    Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(client.GetAsync("/api/v1/admin/inventory")));
  }

  private async Task<IntegrationTestFactory?> CreateFactoryAsync()
  {
    if (!_postgres.IsAvailable)
    {
      _output.WriteLine(_postgres.UnavailableReason ?? "PostgreSQL Testcontainer is unavailable.");
      return null;
    }

    var factory = new IntegrationTestFactory(_postgres.ConnectionString);
    await factory.ResetDatabaseAsync();
    return factory;
  }

  private static async Task<Guid> CreateProductAsync(HttpClient client, string adminToken)
  {
    AuthenticateAs(client, adminToken);
    using var category = await client.PostAsJsonAsync("/api/v1/admin/product-categories", new { Name = "Ceras", SortOrder = 1 });
    Assert.Equal(HttpStatusCode.Created, category.StatusCode);
    var categoryId = (await category.Content.ReadFromJsonAsync<IdPayload>())!.Id;
    using var product = await client.PostAsJsonAsync("/api/v1/admin/products", new { CategoryId = categoryId, Name = "Cera mate", Price = 35000 });
    Assert.Equal(HttpStatusCode.Created, product.StatusCode);
    return (await product.Content.ReadFromJsonAsync<IdPayload>())!.Id;
  }

  private static async Task<HttpStatusCode> StatusAsync(Task<HttpResponseMessage> request)
  {
    using var response = await request;
    return response.StatusCode;
  }

  private static async Task<IReadOnlyDictionary<string, Guid>> GetPermissionsAsync(HttpClient client, string adminToken)
  {
    AuthenticateAs(client, adminToken);
    var permissions = await client.GetFromJsonAsync<List<PermissionView>>("/api/v1/admin/permissions");
    Assert.NotNull(permissions);
    return permissions!.ToDictionary(permission => permission.Code, permission => permission.Id, StringComparer.Ordinal);
  }

  private static async Task<RoleView> CreateRoleAsync(HttpClient client, string adminToken, string name, params Guid[] permissionIds)
  {
    AuthenticateAs(client, adminToken);
    using var response = await client.PostAsJsonAsync("/api/v1/admin/roles", new RoleCreateRequest(name, permissionIds));
    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    var role = await response.Content.ReadFromJsonAsync<RoleView>();
    Assert.NotNull(role);
    return role!;
  }

  private static async Task<AuthPayload> CreateUserWithRolesAsync(HttpClient client, string adminToken, string label, params Guid[] roleIds)
  {
    var email = UniqueEmail(label);
    var registered = await RegisterAsync(client, $"User {label}", email);
    AuthenticateAs(client, adminToken);
    using var assign = await client.PatchAsJsonAsync($"/api/v1/admin/users/{registered.User.Id}/roles", new { RoleIds = roleIds });
    Assert.Equal(HttpStatusCode.OK, assign.StatusCode);
    return await LoginAsync(client, email);
  }

  private static async Task<AuthPayload> RegisterAsync(HttpClient client, string fullName, string email)
  {
    client.DefaultRequestHeaders.Authorization = null;
    using var response = await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(fullName, email, DefaultPassword, "3001112233"));
    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    var payload = await response.Content.ReadFromJsonAsync<AuthPayload>();
    Assert.NotNull(payload);
    return payload!;
  }

  private static async Task<AuthPayload> RegisterAdminAsync(IntegrationTestFactory factory, HttpClient client, string label)
  {
    var email = UniqueEmail($"admin-{label}");
    await RegisterAsync(client, $"Admin {label}", email);

    using (var scope = factory.Services.CreateScope())
    {
      var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
      var user = await dbContext.Users.Include(candidate => candidate.UserRoles)
          .SingleAsync(candidate => candidate.NormalizedEmail == email.ToUpperInvariant());
      var adminRole = await dbContext.Roles.SingleAsync(role => role.NormalizedName == RoleNames.Admin.ToUpperInvariant());
      user.UserRoles.Add(new UserRole(user.Id, adminRole.Id, DateTime.UtcNow));
      await dbContext.SaveChangesAsync();
    }

    return await LoginAsync(client, email);
  }

  private static async Task<AuthPayload> LoginAsync(HttpClient client, string email)
  {
    client.DefaultRequestHeaders.Authorization = null;
    using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, DefaultPassword));
    Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    var payload = await login.Content.ReadFromJsonAsync<AuthPayload>();
    Assert.NotNull(payload);
    AuthenticateAs(client, payload!.AccessToken);
    return payload;
  }

  private static void AuthenticateAs(HttpClient client, string accessToken)
      => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

  private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@example.com";

  private sealed record AuthPayload(string AccessToken, AuthUserResponse User);

  private sealed record IdPayload(Guid Id);
}
