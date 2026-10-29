using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Barbershop.Application.Auth;
using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Customers.Admin;
using Barbershop.Domain.Customers;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Barbershop.Tests.Features.Customers;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "AdminCustomersHttpIntegration")]
public sealed class AdminCustomersHttpIntegrationTests
{
  private const string DefaultPassword = "Secret123!";

  private readonly PostgresContainerFixture _postgres;
  private readonly ITestOutputHelper _output;

  public AdminCustomersHttpIntegrationTests(PostgresContainerFixture postgres, ITestOutputHelper output)
  {
    _postgres = postgres;
    _output = output;
  }

  [Fact]
  public async Task Admin_CanCreateLinkUnlinkAndMergeCustomers()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var admin = await RegisterAndPromoteAdminAsync(context, "lifecycle");
    var customerAccount = await RegisterCustomerAsync(context.Client, "lifecycle");
    AuthenticateAs(context.Client, admin.AccessToken);

    using var createResponse = await context.Client.PostAsJsonAsync(
        "/api/v1/admin/customers", new CustomerCreateRequest("Carlos Pérez", "+57 300 123 4567", "carlos@example.com"));
    Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
    var target = (await createResponse.Content.ReadFromJsonAsync<CustomerDetailView>())!;

    using var duplicateResponse = await context.Client.PostAsJsonAsync(
        "/api/v1/admin/customers", new CustomerCreateRequest("Carlos P.", "573001234567", "carlos.p@example.com"));
    Assert.Equal(HttpStatusCode.Created, duplicateResponse.StatusCode);
    var source = (await duplicateResponse.Content.ReadFromJsonAsync<CustomerDetailView>())!;

    var duplicates = await context.Client.GetFromJsonAsync<List<CustomerDuplicateCandidate>>(
        $"/api/v1/admin/customers/duplicates?phone=3001234567&excludeCustomerId={target.Id}");
    Assert.Contains(duplicates!, candidate => candidate.Id == source.Id);
    Assert.DoesNotContain(duplicates!, candidate => candidate.Id == target.Id);

    var search = await context.Client.GetFromJsonAsync<List<CustomerListItem>>("/api/v1/admin/customers?q=carlos");
    Assert.Equal(2, search!.Count);

    var candidates = await context.Client.GetFromJsonAsync<List<CustomerAccountCandidate>>("/api/v1/admin/customers/account-candidates");
    Assert.Contains(candidates!, candidate => candidate.UserId == customerAccount.UserId);
    Assert.DoesNotContain(candidates!, candidate => candidate.UserId == admin.UserId);

    using var unverifiedLink = await context.Client.PostAsJsonAsync(
        $"/api/v1/admin/customers/{source.Id}/link-account", new CustomerLinkAccountRequest(customerAccount.UserId, IdentityVerified: false));
    Assert.Equal(HttpStatusCode.UnprocessableEntity, unverifiedLink.StatusCode);

    using var linkResponse = await context.Client.PostAsJsonAsync(
        $"/api/v1/admin/customers/{source.Id}/link-account", new CustomerLinkAccountRequest(customerAccount.UserId, IdentityVerified: true));
    Assert.Equal(HttpStatusCode.OK, linkResponse.StatusCode);
    var linked = (await linkResponse.Content.ReadFromJsonAsync<CustomerDetailView>())!;
    Assert.Equal(customerAccount.UserId, linked.LinkedAccount!.UserId);

    using var secondLink = await context.Client.PostAsJsonAsync(
        $"/api/v1/admin/customers/{target.Id}/link-account", new CustomerLinkAccountRequest(customerAccount.UserId, IdentityVerified: true));
    Assert.Equal(HttpStatusCode.Conflict, secondLink.StatusCode);

    using var unlinkWithoutReason = await context.Client.PostAsJsonAsync(
        $"/api/v1/admin/customers/{source.Id}/unlink-account", new CustomerUnlinkAccountRequest(""));
    Assert.Equal(HttpStatusCode.UnprocessableEntity, unlinkWithoutReason.StatusCode);

    using var unlinkResponse = await context.Client.PostAsJsonAsync(
        $"/api/v1/admin/customers/{source.Id}/unlink-account", new CustomerUnlinkAccountRequest("Vinculada por error"));
    Assert.Equal(HttpStatusCode.OK, unlinkResponse.StatusCode);
    var unlinked = (await unlinkResponse.Content.ReadFromJsonAsync<CustomerDetailView>())!;
    Assert.Null(unlinked.LinkedAccount);
    Assert.Contains(unlinked.Events, customerEvent => customerEvent.Type == "AccountUnlinked" && customerEvent.Reason == "Vinculada por error");

    // Link it back to the source so the merge has to hand the account over to the target.
    using var relink = await context.Client.PostAsJsonAsync(
        $"/api/v1/admin/customers/{source.Id}/link-account", new CustomerLinkAccountRequest(customerAccount.UserId, IdentityVerified: true));
    Assert.Equal(HttpStatusCode.OK, relink.StatusCode);

    var preview = await context.Client.GetFromJsonAsync<CustomerMergePreview>(
        $"/api/v1/admin/customers/{target.Id}/merge-preview?sourceCustomerId={source.Id}");
    Assert.True(preview!.CanMerge);
    Assert.Equal(customerAccount.UserId, preview.AccountToTransfer!.UserId);

    using var mergeResponse = await context.Client.PostAsJsonAsync(
        $"/api/v1/admin/customers/{target.Id}/merge", new CustomerMergeRequest(source.Id));
    Assert.Equal(HttpStatusCode.OK, mergeResponse.StatusCode);
    var merged = (await mergeResponse.Content.ReadFromJsonAsync<CustomerDetailView>())!;
    Assert.Equal(customerAccount.UserId, merged.LinkedAccount!.UserId);

    var sourceAfter = await context.Client.GetFromJsonAsync<CustomerDetailView>($"/api/v1/admin/customers/{source.Id}");
    Assert.Equal(target.Id, sourceAfter!.MergedIntoCustomerId);

    var searchAfter = await context.Client.GetFromJsonAsync<List<CustomerListItem>>("/api/v1/admin/customers?q=carlos");
    Assert.Equal(target.Id, Assert.Single(searchAfter!).Id);

    using var editMerged = await context.Client.PutAsJsonAsync(
        $"/api/v1/admin/customers/{source.Id}", new CustomerUpdateRequest("Otro", "3001234567", "otro@example.com"));
    Assert.Equal(HttpStatusCode.Conflict, editMerged.StatusCode);
  }

  [Fact]
  public async Task CustomerEndpoints_AsPlainCustomer_ReturnForbidden()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var customer = await RegisterCustomerAsync(context.Client, "forbidden");
    AuthenticateAs(context.Client, customer.AccessToken);

    using var list = await context.Client.GetAsync("/api/v1/admin/customers");
    Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);

    using var create = await context.Client.PostAsJsonAsync(
        "/api/v1/admin/customers", new CustomerCreateRequest("Intruso", "3001234567", "intruso@example.com"));
    Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
  }

  [Fact]
  public async Task CustomerEndpoints_Unauthenticated_ReturnUnauthorized()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    using var response = await context.Client.GetAsync("/api/v1/admin/customers");
    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
  }

  [Fact]
  public async Task UniqueIndex_PreventsTwoLiveProfilesFromSharingAnAccount()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var account = await RegisterCustomerAsync(context.Client, "unique");
    var (firstId, secondId) = await SeedTwoCustomersAsync(context.Factory);

    // Bypass the service-level check to prove the database itself refuses the second link.
    using var scopeA = context.Factory.Services.CreateScope();
    using var scopeB = context.Factory.Services.CreateScope();
    var dbA = scopeA.ServiceProvider.GetRequiredService<AppDbContext>();
    var dbB = scopeB.ServiceProvider.GetRequiredService<AppDbContext>();

    (await dbA.Customers.SingleAsync(customer => customer.Id == firstId)).LinkAccount(account.UserId, account.UserId, DateTime.UtcNow);
    (await dbB.Customers.SingleAsync(customer => customer.Id == secondId)).LinkAccount(account.UserId, account.UserId, DateTime.UtcNow);

    await dbA.SaveChangesAsync();
    await Assert.ThrowsAsync<DbUpdateException>(() => dbB.SaveChangesAsync());
  }

  [Fact]
  public async Task ConcurrencyToken_RejectsStaleWrites()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var (customerId, _) = await SeedTwoCustomersAsync(context.Factory);

    using var scopeA = context.Factory.Services.CreateScope();
    using var scopeB = context.Factory.Services.CreateScope();
    var dbA = scopeA.ServiceProvider.GetRequiredService<AppDbContext>();
    var dbB = scopeB.ServiceProvider.GetRequiredService<AppDbContext>();

    var copyA = await dbA.Customers.SingleAsync(customer => customer.Id == customerId);
    var copyB = await dbB.Customers.SingleAsync(customer => customer.Id == customerId);

    copyA.UpdateDetails("Versión A", "3001234567", "a@example.com", DateTime.UtcNow);
    await dbA.SaveChangesAsync();

    copyB.UpdateDetails("Versión B", "3001234567", "b@example.com", DateTime.UtcNow);
    await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => dbB.SaveChangesAsync());
  }

  [Fact]
  public async Task ConcurrentLinksOfTheSameAccount_SucceedExactlyOnce()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var account = await RegisterCustomerAsync(context.Client, "race");
    var (firstId, secondId) = await SeedTwoCustomersAsync(context.Factory);

    var outcomes = await Task.WhenAll(
        TryLinkAsync(context.Factory, firstId, account.UserId),
        TryLinkAsync(context.Factory, secondId, account.UserId));

    Assert.Equal(1, outcomes.Count(succeeded => succeeded));

    using var scope = context.Factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    Assert.Equal(1, await db.Customers.CountAsync(customer => customer.UserId == account.UserId));
  }

  [Fact]
  public async Task Admin_CanCreateALinkedProfileFromARegisteredAccount()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var admin = await RegisterAndPromoteAdminAsync(context, "from-account");
    var account = await RegisterCustomerAsync(context.Client, "from-account");
    AuthenticateAs(context.Client, admin.AccessToken);

    // Registration without a phone: the profile cannot be created until one is given.
    using var withoutPhone = await context.Client.PostAsJsonAsync(
        "/api/v1/admin/customers/from-account", new CustomerCreateFromAccountRequest(account.UserId, null, IdentityVerified: true));
    Assert.Equal(HttpStatusCode.UnprocessableEntity, withoutPhone.StatusCode);

    using var unverified = await context.Client.PostAsJsonAsync(
        "/api/v1/admin/customers/from-account", new CustomerCreateFromAccountRequest(account.UserId, "3001234567", IdentityVerified: false));
    Assert.Equal(HttpStatusCode.UnprocessableEntity, unverified.StatusCode);

    using var created = await context.Client.PostAsJsonAsync(
        "/api/v1/admin/customers/from-account", new CustomerCreateFromAccountRequest(account.UserId, "3001234567", IdentityVerified: true));
    Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    var profile = (await created.Content.ReadFromJsonAsync<CustomerDetailView>())!;
    Assert.Equal($"Customer from-account", profile.FullName);
    Assert.Equal(account.UserId, profile.LinkedAccount!.UserId);
    Assert.Contains(profile.Events, customerEvent => customerEvent.Type == "Created");
    Assert.Contains(profile.Events, customerEvent => customerEvent.Type == "AccountLinked");

    using var again = await context.Client.PostAsJsonAsync(
        "/api/v1/admin/customers/from-account", new CustomerCreateFromAccountRequest(account.UserId, "3001234567", IdentityVerified: true));
    Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

    var duplicates = await context.Client.GetFromJsonAsync<List<CustomerDuplicateCandidate>>(
        $"/api/v1/admin/customers/duplicates?phone=3001234567&excludeUserId={account.UserId}");
    var match = Assert.Single(duplicates!);
    Assert.Equal(profile.Id, match.Id);
    Assert.True(match.IsLinked);
  }

  [Fact]
  public async Task CreateFromAccount_AsPlainCustomer_ReturnsForbidden()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var customer = await RegisterCustomerAsync(context.Client, "from-account-forbidden");
    AuthenticateAs(context.Client, customer.AccessToken);

    using var response = await context.Client.PostAsJsonAsync(
        "/api/v1/admin/customers/from-account", new CustomerCreateFromAccountRequest(customer.UserId, "3001234567", true));
    Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

    using var scope = context.Factory.Services.CreateScope();
    Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Customers.CountAsync());
  }

  [Fact]
  public async Task ConcurrentCreateFromAccountAndLink_OfTheSameAccount_SucceedExactlyOnce()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var account = await RegisterCustomerAsync(context.Client, "race-from-account");
    var (existingId, _) = await SeedTwoCustomersAsync(context.Factory);

    var outcomes = await Task.WhenAll(
        TryCreateFromAccountAsync(context.Factory, account.UserId),
        TryCreateFromAccountAsync(context.Factory, account.UserId),
        TryLinkAsync(context.Factory, existingId, account.UserId));

    Assert.Equal(1, outcomes.Count(succeeded => succeeded));

    using var scope = context.Factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    Assert.Equal(1, await db.Customers.CountAsync(customer => customer.UserId == account.UserId));
    // A failed create-from-account must not leave an unlinked profile or orphan events behind.
    Assert.Equal(2 + outcomes.Take(2).Count(succeeded => succeeded), await db.Customers.CountAsync());
  }

  private static async Task<bool> TryCreateFromAccountAsync(IntegrationTestFactory factory, Guid userId)
  {
    using var scope = factory.Services.CreateScope();
    var service = scope.ServiceProvider.GetRequiredService<IAdminCustomersService>();
    try
    {
      await service.CreateFromAccountAsync(userId, new CustomerCreateFromAccountRequest(userId, "3001234567", IdentityVerified: true));
      return true;
    }
    catch (ConflictException)
    {
      return false;
    }
  }

  private static async Task<bool> TryLinkAsync(IntegrationTestFactory factory, Guid customerId, Guid userId)
  {
    using var scope = factory.Services.CreateScope();
    var service = scope.ServiceProvider.GetRequiredService<IAdminCustomersService>();
    try
    {
      await service.LinkAccountAsync(userId, customerId, new CustomerLinkAccountRequest(userId, IdentityVerified: true));
      return true;
    }
    catch (ConflictException)
    {
      return false;
    }
  }

  private static async Task<(Guid FirstId, Guid SecondId)> SeedTwoCustomersAsync(IntegrationTestFactory factory)
  {
    using var scope = factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var createdBy = Guid.NewGuid();
    var first = new CustomerProfile("Primero", "3001234567", "primero@example.com", createdBy, DateTime.UtcNow);
    var second = new CustomerProfile("Segundo", "3007654321", "segundo@example.com", createdBy, DateTime.UtcNow);
    db.Customers.AddRange(first, second);
    await db.SaveChangesAsync();
    return (first.Id, second.Id);
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
      var customerRole = await db.Roles.SingleAsync(role => role.NormalizedName == RoleNames.Customer.ToUpperInvariant());
      // A plain admin, not a customer, so it never shows up as a linkable account.
      var customerAssignment = user.UserRoles.SingleOrDefault(assignment => assignment.RoleId == customerRole.Id);
      if (customerAssignment is not null)
      {
        user.UserRoles.Remove(customerAssignment);
      }

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
