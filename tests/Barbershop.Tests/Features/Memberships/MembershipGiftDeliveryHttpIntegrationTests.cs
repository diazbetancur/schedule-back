using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Barbershop.Application.Auth;
using Barbershop.Application.Common.Exceptions;
using Barbershop.Application.Customers.Admin;
using Barbershop.Application.Memberships.Admin;
using Barbershop.Application.Services.Admin;
using Barbershop.Domain.Common;
using Barbershop.Domain.Memberships;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Barbershop.Tests.Features.Memberships;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "MembershipGiftDeliveryHttpIntegration")]
public sealed class MembershipGiftDeliveryHttpIntegrationTests
{
  private const string DefaultPassword = "Secret123!";
  private const string Terms = "La membresía es personal e intransferible.";
  private static readonly DateOnly Today = DateOnly.FromDateTime(BogotaClock.ToLocal(DateTime.UtcNow));

  private readonly PostgresContainerFixture _postgres;
  private readonly ITestOutputHelper _output;

  public MembershipGiftDeliveryHttpIntegrationTests(PostgresContainerFixture postgres, ITestOutputHelper output)
  {
    _postgres = postgres;
    _output = output;
  }

  [Fact]
  public async Task Admin_DeliversAndReversesOverHttp_AndOnlyAdminsCan()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var admin = await RegisterAndPromoteAdminAsync(context, "gifts");
    var seed = await SeedActiveMembershipAsync(context.Factory, admin.UserId);
    AuthenticateAs(context.Client, admin.AccessToken);
    var deliverUrl = $"/api/v1/admin/memberships/{seed.MembershipId}/gifts/{seed.CapId}/deliveries";

    using var delivered = await context.Client.PostAsJsonAsync(deliverUrl, new MembershipGiftDeliveryRequest(Today, Guid.NewGuid()));
    Assert.Equal(HttpStatusCode.OK, delivered.StatusCode);
    var membership = (await delivered.Content.ReadFromJsonAsync<CustomerMembershipDetailView>())!;
    var cap = membership.Gifts.Single(gift => gift.Id == seed.CapId);
    Assert.Equal("Delivered", cap.Status);
    Assert.Equal(2, Assert.Single(membership.GiftDeliveries).Quantity);

    using var again = await context.Client.PostAsJsonAsync(deliverUrl, new MembershipGiftDeliveryRequest(Today, Guid.NewGuid()));
    Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    using var future = await context.Client.PostAsJsonAsync(
        $"/api/v1/admin/memberships/{seed.MembershipId}/gifts/{seed.KeyringId}/deliveries",
        new MembershipGiftDeliveryRequest(Today.AddDays(1), Guid.NewGuid()));
    Assert.Equal(HttpStatusCode.UnprocessableEntity, future.StatusCode);

    var reverseUrl = $"{deliverUrl}/{cap.DeliveryId}/reverse";
    using var noReason = await context.Client.PostAsJsonAsync(reverseUrl, new MembershipGiftDeliveryReverseRequest(""));
    Assert.Equal(HttpStatusCode.UnprocessableEntity, noReason.StatusCode);
    using var reversed = await context.Client.PostAsJsonAsync(reverseUrl, new MembershipGiftDeliveryReverseRequest("Entregada por error"));
    Assert.Equal(HttpStatusCode.OK, reversed.StatusCode);
    using var twice = await context.Client.PostAsJsonAsync(reverseUrl, new MembershipGiftDeliveryReverseRequest("Otra vez"));
    Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);

    var customer = await RegisterCustomerAsync(context.Client, "gifts");
    AuthenticateAs(context.Client, customer.AccessToken);
    using var asCustomer = await context.Client.PostAsJsonAsync(deliverUrl, new MembershipGiftDeliveryRequest(Today, Guid.NewGuid()));
    Assert.Equal(HttpStatusCode.Forbidden, asCustomer.StatusCode);
    using var reverseAsCustomer = await context.Client.PostAsJsonAsync(reverseUrl, new MembershipGiftDeliveryReverseRequest("Intento"));
    Assert.Equal(HttpStatusCode.Forbidden, reverseAsCustomer.StatusCode);
    context.Client.DefaultRequestHeaders.Authorization = null;
    using var anonymous = await context.Client.PostAsJsonAsync(deliverUrl, new MembershipGiftDeliveryRequest(Today, Guid.NewGuid()));
    Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

    // Nothing financial happened.
    using var scope = context.Factory.Services.CreateScope();
    Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<AppDbContext>().IncomeEntries.CountAsync());
  }

  [Fact]
  public async Task ConcurrentDeliveriesOfOneGift_StoreExactlyOne()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var adminId = await SeedAdminUserAsync(context.Factory);
    var seed = await SeedActiveMembershipAsync(context.Factory, adminId);

    var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
        TryDeliverAsync(context.Factory, adminId, seed, new MembershipGiftDeliveryRequest(Today, Guid.NewGuid())))));

    Assert.Equal(1, results.Count(result => result));
    using var scope = context.Factory.Services.CreateScope();
    Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<AppDbContext>().MembershipGiftDeliveries.CountAsync());
  }

  [Fact]
  public async Task ConcurrentRetries_WithTheSameKey_StoreOneDelivery()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var adminId = await SeedAdminUserAsync(context.Factory);
    var seed = await SeedActiveMembershipAsync(context.Factory, adminId);
    var request = new MembershipGiftDeliveryRequest(Today, Guid.NewGuid());

    var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        TryDeliverAsync(context.Factory, adminId, seed, request))));

    Assert.All(results, Assert.True); // every retry gets the delivery that was stored once
    using var scope = context.Factory.Services.CreateScope();
    Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<AppDbContext>().MembershipGiftDeliveries.CountAsync());
  }

  [Fact]
  public async Task ConcurrentReversals_OfOneDelivery_ApplyOnce()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var adminId = await SeedAdminUserAsync(context.Factory);
    var seed = await SeedActiveMembershipAsync(context.Factory, adminId);
    Guid deliveryId;
    using (var scope = context.Factory.Services.CreateScope())
    {
      var delivered = await scope.ServiceProvider.GetRequiredService<IAdminMembershipGiftDeliveryService>()
          .DeliverAsync(adminId, seed.MembershipId, seed.CapId, new MembershipGiftDeliveryRequest(Today, Guid.NewGuid()));
      deliveryId = delivered.Gifts.Single(gift => gift.Id == seed.CapId).DeliveryId!.Value;
    }

    var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
    {
      using var scope = context.Factory.Services.CreateScope();
      try
      {
        await scope.ServiceProvider.GetRequiredService<IAdminMembershipGiftDeliveryService>().ReverseAsync(
            adminId, seed.MembershipId, seed.CapId, deliveryId, new MembershipGiftDeliveryReverseRequest("Entregada por error"));
        return true;
      }
      catch (ConflictException)
      {
        return false;
      }
    })));

    Assert.Equal(1, results.Count(result => result));
    using var check = context.Factory.Services.CreateScope();
    Assert.Equal(1, await check.ServiceProvider.GetRequiredService<AppDbContext>().MembershipGiftDeliveryReversals.CountAsync());
  }

  [Fact]
  public async Task DeliveryRows_AreAppendOnly_AndUniquePerGiftAndNumber()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var adminId = await SeedAdminUserAsync(context.Factory);
    var seed = await SeedActiveMembershipAsync(context.Factory, adminId);
    using (var scope = context.Factory.Services.CreateScope())
    {
      var service = scope.ServiceProvider.GetRequiredService<IAdminMembershipGiftDeliveryService>();
      var delivered = await service.DeliverAsync(adminId, seed.MembershipId, seed.CapId, new MembershipGiftDeliveryRequest(Today, Guid.NewGuid()));
      await service.ReverseAsync(adminId, seed.MembershipId, seed.CapId,
          delivered.Gifts.Single(gift => gift.Id == seed.CapId).DeliveryId!.Value, new MembershipGiftDeliveryReverseRequest("Entregada por error"));
    }

    using var sqlScope = context.Factory.Services.CreateScope();
    var db = sqlScope.ServiceProvider.GetRequiredService<AppDbContext>();
    await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("UPDATE public.membership_gift_deliveries SET \"DeliveredOn\" = \"DeliveredOn\" - 1"));
    await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM public.membership_gift_delivery_reversals"));
    await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM public.membership_gift_deliveries"));
    // A second row for the same gift and delivery number is refused by the database itself.
    var delivery = await db.MembershipGiftDeliveries.AsNoTracking().SingleAsync();
    await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO public.membership_gift_deliveries
          ("Id", "MembershipGiftId", "CustomerMembershipId", "DeliveryNumber", "Quantity", "DeliveredOn", "IdempotencyKey", "RegisteredByUserId", "RegisteredAt")
        VALUES ({Guid.NewGuid()}, {delivery.MembershipGiftId}, {delivery.CustomerMembershipId}, 1, 2, {Today}, {Guid.NewGuid()}, {adminId}, now())
        """));
    Assert.Equal(1, await db.MembershipGiftDeliveries.CountAsync());
    Assert.Equal(1, await db.MembershipGiftDeliveryReversals.CountAsync());
  }

  private sealed record SeededMembership(Guid MembershipId, Guid CapId, Guid KeyringId);

  private static async Task<bool> TryDeliverAsync(IntegrationTestFactory factory, Guid adminId, SeededMembership seed, MembershipGiftDeliveryRequest request)
  {
    using var scope = factory.Services.CreateScope();
    try
    {
      await scope.ServiceProvider.GetRequiredService<IAdminMembershipGiftDeliveryService>().DeliverAsync(adminId, seed.MembershipId, seed.CapId, request);
      return true;
    }
    catch (ConflictException)
    {
      return false;
    }
  }

  /// <summary>An active pool membership with two physical gifts: 2 caps and 1 keyring.</summary>
  private static async Task<SeededMembership> SeedActiveMembershipAsync(IntegrationTestFactory factory, Guid adminId)
  {
    using var scope = factory.Services.CreateScope();
    var haircut = await scope.ServiceProvider.GetRequiredService<IAdminServicesService>()
        .CreateAsync(new ServiceCreateRequest($"Corte {Guid.NewGuid():N}"[..20], 25000, 30));
    var customer = await scope.ServiceProvider.GetRequiredService<IAdminCustomersService>()
        .CreateAsync(adminId, new CustomerCreateRequest("Carlos Pérez", "3001234567", $"{Guid.NewGuid():N}@example.com"));
    var plan = await scope.ServiceProvider.GetRequiredService<IAdminMembershipPlansService>().CreateAsync(new MembershipPlanUpsertRequest(
        $"Oro {Guid.NewGuid():N}"[..20], null, 250000, "Pool", 10, 90, Terms, null, [new(haircut.Id, null)],
        [new(null, "Physical", null, "Gorra", 2, "EveryPurchase"), new(null, "Physical", null, "Llavero", 1, "EveryPurchase")]));
    var membership = await scope.ServiceProvider.GetRequiredService<IAdminMembershipsService>()
        .SellAsync(adminId, customer.Id, new MembershipSaleRequest(plan.Id, Today, ActivateNow: true));
    return new SeededMembership(
        membership.Id,
        membership.Gifts.Single(gift => gift.Description == "Gorra").Id,
        membership.Gifts.Single(gift => gift.Description == "Llavero").Id);
  }

  private static async Task<Guid> SeedAdminUserAsync(IntegrationTestFactory factory)
  {
    using var scope = factory.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var user = new User("Admin Semilla", $"seed-{Guid.NewGuid():N}@example.com", "hash", DateTime.UtcNow);
    db.Users.Add(user);
    await db.SaveChangesAsync();
    return user.Id;
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
      var user = await db.Users
          .Include(candidate => candidate.UserRoles)
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
        "/api/v1/auth/register",
        new RegisterRequest($"Customer {label}", UniqueEmail($"customer-{label}"), DefaultPassword, null));
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
