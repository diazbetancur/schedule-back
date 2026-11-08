using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Barbershop.Application.Auth;
using Barbershop.Application.Products;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Barbershop.Tests.Features.Products;

/// <summary>ADR-0016 B5 (admin masters, Admin role) and B6 (anonymous public catalog) over HTTP.</summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "ProductCatalogHttpIntegration")]
public sealed class ProductCatalogHttpIntegrationTests
{
  private const string DefaultPassword = "Secret123!";

  private readonly PostgresContainerFixture _postgres;
  private readonly ITestOutputHelper _output;

  public ProductCatalogHttpIntegrationTests(PostgresContainerFixture postgres, ITestOutputHelper output)
  {
    _postgres = postgres;
    _output = output;
  }

  [Fact]
  public async Task Admin_ManagesProductCategoriesAndProducts()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var admin = await RegisterAndPromoteAdminAsync(context, "products");
    AuthenticateAs(context.Client, admin.AccessToken);

    Assert.Empty((await context.Client.GetFromJsonAsync<List<ProductCategoryView>>("/api/v1/admin/product-categories"))!);

    using var createWaxes = await context.Client.PostAsJsonAsync("/api/v1/admin/product-categories", new ProductCategoryCreateRequest("Ceras", 1));
    Assert.Equal(HttpStatusCode.Created, createWaxes.StatusCode);
    var waxes = (await createWaxes.Content.ReadFromJsonAsync<ProductCategoryView>())!;
    using var duplicateCategory = await context.Client.PostAsJsonAsync("/api/v1/admin/product-categories", new ProductCategoryCreateRequest(" CERAS ", 2));
    Assert.Equal(HttpStatusCode.Conflict, duplicateCategory.StatusCode);
    // D1: the same name as the seeded service category is fine here.
    using var sameAsService = await context.Client.PostAsJsonAsync("/api/v1/admin/product-categories", new ProductCategoryCreateRequest("Barbería", 2));
    Assert.Equal(HttpStatusCode.Created, sameAsService.StatusCode);
    var barberia = (await sameAsService.Content.ReadFromJsonAsync<ProductCategoryView>())!;

    using var withoutPrice = await context.Client.PostAsJsonAsync("/api/v1/admin/products", new { categoryId = waxes.Id, name = "Cera mate" });
    Assert.Equal(HttpStatusCode.UnprocessableEntity, withoutPrice.StatusCode);

    using var createWax = await context.Client.PostAsJsonAsync(
        "/api/v1/admin/products", new ProductCreateRequest(waxes.Id, "Cera mate", 35000, "Acabado natural", 1));
    Assert.Equal(HttpStatusCode.Created, createWax.StatusCode);
    var wax = (await createWax.Content.ReadFromJsonAsync<ProductView>())!;
    Assert.Equal(("Ceras", "Cera mate", 35000, true), (wax.CategoryName, wax.Name, wax.Price, wax.IsActive));

    using var duplicateProduct = await context.Client.PostAsJsonAsync("/api/v1/admin/products", new ProductCreateRequest(waxes.Id, "cera MATE", 1000));
    Assert.Equal(HttpStatusCode.Conflict, duplicateProduct.StatusCode);

    using var move = await context.Client.PutAsJsonAsync(
        $"/api/v1/admin/products/{wax.Id}", new ProductUpdateRequest(barberia.Id, "Cera mate", 36000, "Acabado natural", 1));
    Assert.Equal(HttpStatusCode.OK, move.StatusCode);
    var moved = (await move.Content.ReadFromJsonAsync<ProductView>())!;
    Assert.Equal((wax.PublicCode, barberia.Id, 36000), (moved.PublicCode, moved.CategoryId, moved.Price));

    var filtered = await context.Client.GetPageItemsAsync<ProductView>($"/api/v1/admin/products?categoryId={barberia.Id}");
    Assert.Equal([wax.Id], filtered!.Select(product => product.Id));
    Assert.Empty((await context.Client.GetPageItemsAsync<ProductView>($"/api/v1/admin/products?categoryId={waxes.Id}"))!);

    using var deactivateCategory = await context.Client.PatchAsJsonAsync(
        $"/api/v1/admin/product-categories/{barberia.Id}/status", new ProductCategoryStatusUpdateRequest(false));
    Assert.Equal(HttpStatusCode.Conflict, deactivateCategory.StatusCode);
    using var deleteCategory = await context.Client.DeleteAsync($"/api/v1/admin/product-categories/{barberia.Id}");
    Assert.Equal(HttpStatusCode.Conflict, deleteCategory.StatusCode);

    using var deactivateProduct = await context.Client.PatchAsJsonAsync(
        $"/api/v1/admin/products/{wax.Id}/status", new ProductStatusUpdateRequest(false));
    Assert.False((await deactivateProduct.Content.ReadFromJsonAsync<ProductView>())!.IsActive);
    using var deleteProduct = await context.Client.DeleteAsync($"/api/v1/admin/products/{wax.Id}");
    Assert.Equal(HttpStatusCode.NoContent, deleteProduct.StatusCode);
    using var deletedProduct = await context.Client.GetAsync($"/api/v1/admin/products/{wax.Id}");
    Assert.Equal(HttpStatusCode.NotFound, deletedProduct.StatusCode);
    using var deleteNow = await context.Client.DeleteAsync($"/api/v1/admin/product-categories/{barberia.Id}");
    Assert.Equal(HttpStatusCode.NoContent, deleteNow.StatusCode);

    // Image endpoints: an unknown product is a 404, removing a missing image is a no-op.
    using var noImage = await context.Client.DeleteAsync($"/api/v1/admin/products/{Guid.NewGuid()}/image");
    Assert.Equal(HttpStatusCode.NotFound, noImage.StatusCode);
    using var otherWax = await context.Client.PostAsJsonAsync("/api/v1/admin/products", new ProductCreateRequest(waxes.Id, "Pomada", 30000));
    var pomade = (await otherWax.Content.ReadFromJsonAsync<ProductView>())!;
    using var removeMissing = await context.Client.DeleteAsync($"/api/v1/admin/products/{pomade.Id}/image");
    Assert.Equal(HttpStatusCode.OK, removeMissing.StatusCode);
  }

  [Fact]
  public async Task TheImageUpload_FindsTheImageField()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var admin = await RegisterAndPromoteAdminAsync(context, "product-image");
    AuthenticateAs(context.Client, admin.AccessToken);
    var category = await (await context.Client.PostAsJsonAsync("/api/v1/admin/product-categories", new ProductCategoryCreateRequest("Ceras", 1)))
        .Content.ReadFromJsonAsync<ProductCategoryView>();
    var product = await (await context.Client.PostAsJsonAsync("/api/v1/admin/products", new ProductCreateRequest(category!.Id, "Cera", 1000)))
        .Content.ReadFromJsonAsync<ProductView>();

    using var content = new MultipartFormDataContent();
    using var file = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01, 0x02]);
    file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
    content.Add(file, "image", "cera.png");
    using var response = await context.Client.PostAsync($"/api/v1/admin/products/{product!.Id}/image", content);
    var body = await response.Content.ReadAsStringAsync();
    _output.WriteLine($"{response.StatusCode}: {body}");

    // As in AdminStaffPhotoUploadHttpIntegrationTests: real storage (R2) is not configured in tests, so a 200 cannot be
    // required; what matters is that the "image" field reached the service (otherwise: 422 "A file is required").
    Assert.False(response.StatusCode == HttpStatusCode.UnprocessableEntity && body.Contains("A file is required", StringComparison.Ordinal), body);
  }

  [Fact]
  public async Task TheMasters_AreForAdministratorsOnly()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    using (var anonymous = await context.Client.GetAsync("/api/v1/admin/product-categories"))
    {
      Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    using (var anonymousProducts = await context.Client.GetAsync("/api/v1/admin/products"))
    {
      Assert.Equal(HttpStatusCode.Unauthorized, anonymousProducts.StatusCode);
    }

    var customer = await RegisterCustomerAsync(context.Client, "products-forbidden");
    AuthenticateAs(context.Client, customer.AccessToken);
    using (var categories = await context.Client.GetAsync("/api/v1/admin/product-categories"))
    {
      Assert.Equal(HttpStatusCode.Forbidden, categories.StatusCode);
    }

    using var create = await context.Client.PostAsJsonAsync("/api/v1/admin/products", new ProductCreateRequest(Guid.NewGuid(), "Cera", 1000));
    Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);

    using var content = new MultipartFormDataContent();
    using var file = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47]);
    file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
    content.Add(file, "image", "cera.png");
    using var upload = await context.Client.PostAsync($"/api/v1/admin/products/{Guid.NewGuid()}/image", content);
    Assert.Equal(HttpStatusCode.Forbidden, upload.StatusCode);
  }

  [Fact]
  public async Task ThePublicCatalog_IsAnonymous_ShowsOnlyActiveItems_AndOnlyPublicFields()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var admin = await RegisterAndPromoteAdminAsync(context, "public-catalog");
    var adminClient = context.Factory.CreateClient();
    AuthenticateAs(adminClient, admin.AccessToken);
    var waxes = (await (await adminClient.PostAsJsonAsync("/api/v1/admin/product-categories", new ProductCategoryCreateRequest("Ceras", 1)))
        .Content.ReadFromJsonAsync<ProductCategoryView>())!;
    var paused = (await (await adminClient.PostAsJsonAsync("/api/v1/admin/product-categories", new ProductCategoryCreateRequest("Pausada", 2)))
        .Content.ReadFromJsonAsync<ProductCategoryView>())!;
    var wax = (await (await adminClient.PostAsJsonAsync("/api/v1/admin/products", new ProductCreateRequest(waxes.Id, "Cera mate", 35000, "Acabado natural")))
        .Content.ReadFromJsonAsync<ProductView>())!;
    var retired = (await (await adminClient.PostAsJsonAsync("/api/v1/admin/products", new ProductCreateRequest(waxes.Id, "Retirada", 1000)))
        .Content.ReadFromJsonAsync<ProductView>())!;
    (await adminClient.PatchAsJsonAsync($"/api/v1/admin/products/{retired.Id}/status", new ProductStatusUpdateRequest(false))).Dispose();
    (await adminClient.PatchAsJsonAsync($"/api/v1/admin/product-categories/{paused.Id}/status", new ProductCategoryStatusUpdateRequest(false))).Dispose();

    // No Authorization header at all.
    var visitor = context.Client;

    using var list = await visitor.GetAsync("/api/v1/public/product-categories");
    Assert.Equal(HttpStatusCode.OK, list.StatusCode);
    var listJson = JsonDocument.Parse(await list.Content.ReadAsStringAsync()).RootElement;
    var onlyCategory = Assert.Single(listJson.EnumerateArray());
    Assert.Equal(["code", "name", "productCount"], onlyCategory.EnumerateObject().Select(property => property.Name).Order());
    Assert.Equal((waxes.PublicCode, 1), (onlyCategory.GetProperty("code").GetString(), onlyCategory.GetProperty("productCount").GetInt32()));

    using var category = await visitor.GetAsync($"/api/v1/public/product-categories/{waxes.PublicCode}");
    Assert.Equal(HttpStatusCode.OK, category.StatusCode);
    var categoryJson = JsonDocument.Parse(await category.Content.ReadAsStringAsync()).RootElement;
    Assert.Equal(["code", "name", "products"], categoryJson.EnumerateObject().Select(property => property.Name).Order());
    var product = Assert.Single(categoryJson.GetProperty("products").EnumerateArray());
    Assert.Equal(["code", "description", "imageUrl", "name", "price"], product.EnumerateObject().Select(property => property.Name).Order());
    Assert.Equal((wax.PublicCode, 35000), (product.GetProperty("code").GetString(), product.GetProperty("price").GetInt32()));

    using var detail = await visitor.GetAsync($"/api/v1/public/products/{wax.PublicCode}");
    Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
    var detailJson = JsonDocument.Parse(await detail.Content.ReadAsStringAsync()).RootElement;
    Assert.Equal(
        ["categoryCode", "categoryName", "code", "description", "imageUrl", "name", "price"],
        detailJson.EnumerateObject().Select(property => property.Name).Order());
    Assert.DoesNotContain(wax.Id.ToString(), detailJson.GetRawText(), StringComparison.OrdinalIgnoreCase);

    // Inactive, unknown or malformed: a plain 404, which the pages show as "No disponible".
    foreach (var url in new[]
             {
               $"/api/v1/public/products/{retired.PublicCode}",
               $"/api/v1/public/product-categories/{paused.PublicCode}",
               "/api/v1/public/products/zzzzzzzz",
               "/api/v1/public/product-categories/no-valido",
             })
    {
      using var missing = await visitor.GetAsync(url);
      Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    adminClient.Dispose();
  }

  private async Task<AuthSession> RegisterAndPromoteAdminAsync(TestHttpContext context, string label)
  {
    var email = UniqueEmail($"admin-{label}");
    using (var register = await context.Client.PostAsJsonAsync(
        "/api/v1/auth/register", new RegisterRequest($"Admin {label}", email, DefaultPassword, null)))
    {
      Assert.Equal(HttpStatusCode.Created, register.StatusCode);
    }

    using (var scope = context.Factory.Services.CreateScope())
    {
      var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
      var user = await db.Users.Include(candidate => candidate.UserRoles)
          .SingleAsync(candidate => candidate.NormalizedEmail == email.ToUpperInvariant());
      var adminRole = await db.Roles.SingleAsync(role => role.NormalizedName == RoleNames.Admin.ToUpperInvariant());
      if (user.UserRoles.All(assignment => assignment.RoleId != adminRole.Id))
      {
        user.UserRoles.Add(new UserRole(user.Id, adminRole.Id, DateTime.UtcNow));
      }

      await db.SaveChangesAsync();
    }

    using var login = await context.Client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, DefaultPassword));
    Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    var payload = (await login.Content.ReadFromJsonAsync<TokenResponse>())!;
    return new AuthSession(payload.User.Id, payload.AccessToken);
  }

  private static async Task<AuthSession> RegisterCustomerAsync(HttpClient client, string label)
  {
    using var response = await client.PostAsJsonAsync(
        "/api/v1/auth/register", new RegisterRequest($"Customer {label}", UniqueEmail($"customer-{label}"), DefaultPassword, null));
    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    var payload = (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
    return new AuthSession(payload.User.Id, payload.AccessToken);
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

  private static void AuthenticateAs(HttpClient client, string accessToken)
      => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

  private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@example.com";

  private sealed record AuthSession(Guid UserId, string AccessToken);

  private sealed class TestHttpContext(IntegrationTestFactory factory, HttpClient client) : IDisposable
  {
    public IntegrationTestFactory Factory { get; } = factory;
    public HttpClient Client { get; } = client;

    public void Dispose()
    {
      Client.Dispose();
      Factory.Dispose();
    }
  }
}
