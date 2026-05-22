using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Barbershop.Application.Appointments;
using Barbershop.Application.Auth;
using Barbershop.Application.Availability;
using Barbershop.Application.Reviews;
using Barbershop.Application.Staff;
using Barbershop.Application.Staff.Admin;
using Barbershop.Domain.Appointments;
using Barbershop.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Barbershop.Tests.Features.Reviews;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "ReviewHttpIntegration")]
public sealed class ReviewsHttpIntegrationTests
{
  private const string DefaultPassword = "Secret123!";

  private readonly PostgresContainerFixture _postgres;
  private readonly ITestOutputHelper _output;

  public ReviewsHttpIntegrationTests(PostgresContainerFixture postgres, ITestOutputHelper output)
  {
    _postgres = postgres;
    _output = output;
  }

  [Fact]
  public async Task AuthenticatedCustomer_CanCreateReview_ForOwnCompletedAppointment()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var customer = await RegisterCustomerAsync(context.Client, "create-own");
    var staff = await CreateStaffAsync(context.Factory, "create-own");
    var startsAtUtc = SlotOnNextDay(hour: 10, minute: 0);

    await EnsureAvailabilityForDateAsync(context.Factory, staff.StaffProfileId, startsAtUtc);

    var appointment = await CreateCustomerAppointmentAsync(
        context.Factory,
        customer.UserId,
        staff.StaffProfileId,
        startsAtUtc);

    await CompleteAppointmentAsync(context.Factory, appointment.Id);

    AuthenticateAs(context.Client, customer.AccessToken);

    using var response = await context.Client.PostAsJsonAsync(
        $"/api/v1/customer/appointments/{appointment.Id}/review",
        new ReviewCreateRequest(5, "Great service"));

    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    Assert.NotNull(response.Headers.Location);

    var payload = await response.Content.ReadFromJsonAsync<CustomerReviewView>();
    Assert.NotNull(payload);
    Assert.Equal(appointment.Id, payload!.AppointmentId);
    Assert.Equal(staff.StaffProfileId, payload.StaffProfileId);
    Assert.Equal(5, payload.Stars);
    Assert.Equal("Great service", payload.Comment);
  }

  [Fact]
  public async Task CreateReview_RejectsStarsBelowOne()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var customer = await RegisterCustomerAsync(context.Client, "stars-below");
    var staff = await CreateStaffAsync(context.Factory, "stars-below");
    var startsAtUtc = SlotOnNextDay(hour: 10, minute: 0);

    await EnsureAvailabilityForDateAsync(context.Factory, staff.StaffProfileId, startsAtUtc);

    var appointment = await CreateCustomerAppointmentAsync(
        context.Factory,
        customer.UserId,
        staff.StaffProfileId,
        startsAtUtc);

    await CompleteAppointmentAsync(context.Factory, appointment.Id);

    AuthenticateAs(context.Client, customer.AccessToken);

    using var response = await context.Client.PostAsJsonAsync(
        $"/api/v1/customer/appointments/{appointment.Id}/review",
        new ReviewCreateRequest(0, "Invalid stars"));

    Assert.Equal((HttpStatusCode)422, response.StatusCode);

    var validationErrors = await ReadValidationErrorsAsync(response);
    Assert.Contains("stars", validationErrors.Keys, StringComparer.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task CreateReview_RejectsStarsAboveFive()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var customer = await RegisterCustomerAsync(context.Client, "stars-above");
    var staff = await CreateStaffAsync(context.Factory, "stars-above");
    var startsAtUtc = SlotOnNextDay(hour: 10, minute: 0);

    await EnsureAvailabilityForDateAsync(context.Factory, staff.StaffProfileId, startsAtUtc);

    var appointment = await CreateCustomerAppointmentAsync(
        context.Factory,
        customer.UserId,
        staff.StaffProfileId,
        startsAtUtc);

    await CompleteAppointmentAsync(context.Factory, appointment.Id);

    AuthenticateAs(context.Client, customer.AccessToken);

    using var response = await context.Client.PostAsJsonAsync(
        $"/api/v1/customer/appointments/{appointment.Id}/review",
        new ReviewCreateRequest(6, "Invalid stars"));

    Assert.Equal((HttpStatusCode)422, response.StatusCode);

    var validationErrors = await ReadValidationErrorsAsync(response);
    Assert.Contains("stars", validationErrors.Keys, StringComparer.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task CreateReview_RejectsNonCompletedAppointment()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var customer = await RegisterCustomerAsync(context.Client, "non-completed");
    var staff = await CreateStaffAsync(context.Factory, "non-completed");
    var startsAtUtc = SlotOnNextDay(hour: 10, minute: 0);

    await EnsureAvailabilityForDateAsync(context.Factory, staff.StaffProfileId, startsAtUtc);

    var appointment = await CreateCustomerAppointmentAsync(
        context.Factory,
        customer.UserId,
        staff.StaffProfileId,
        startsAtUtc);

    AuthenticateAs(context.Client, customer.AccessToken);

    using var response = await context.Client.PostAsJsonAsync(
        $"/api/v1/customer/appointments/{appointment.Id}/review",
        new ReviewCreateRequest(4, "Too soon"));

    Assert.Equal((HttpStatusCode)422, response.StatusCode);

    var validationErrors = await ReadValidationErrorsAsync(response);
    Assert.Contains("appointmentId", validationErrors.Keys, StringComparer.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task CreateReview_RejectsAnotherCustomerAppointment()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var owner = await RegisterCustomerAsync(context.Client, "owner");
    var intruder = await RegisterCustomerAsync(context.Client, "intruder");
    var staff = await CreateStaffAsync(context.Factory, "another-customer");
    var startsAtUtc = SlotOnNextDay(hour: 10, minute: 0);

    await EnsureAvailabilityForDateAsync(context.Factory, staff.StaffProfileId, startsAtUtc);

    var appointment = await CreateCustomerAppointmentAsync(
        context.Factory,
        owner.UserId,
        staff.StaffProfileId,
        startsAtUtc);

    await CompleteAppointmentAsync(context.Factory, appointment.Id);

    AuthenticateAs(context.Client, intruder.AccessToken);

    using var response = await context.Client.PostAsJsonAsync(
        $"/api/v1/customer/appointments/{appointment.Id}/review",
        new ReviewCreateRequest(5, "Not mine"));

    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
  }

  [Fact]
  public async Task CreateReview_RejectsDuplicateReview()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var customer = await RegisterCustomerAsync(context.Client, "duplicate");
    var staff = await CreateStaffAsync(context.Factory, "duplicate");
    var startsAtUtc = SlotOnNextDay(hour: 10, minute: 0);

    await EnsureAvailabilityForDateAsync(context.Factory, staff.StaffProfileId, startsAtUtc);

    var appointment = await CreateCustomerAppointmentAsync(
        context.Factory,
        customer.UserId,
        staff.StaffProfileId,
        startsAtUtc);

    await CompleteAppointmentAsync(context.Factory, appointment.Id);

    AuthenticateAs(context.Client, customer.AccessToken);

    using var firstResponse = await context.Client.PostAsJsonAsync(
        $"/api/v1/customer/appointments/{appointment.Id}/review",
        new ReviewCreateRequest(5, "First"));

    Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);

    using var secondResponse = await context.Client.PostAsJsonAsync(
        $"/api/v1/customer/appointments/{appointment.Id}/review",
        new ReviewCreateRequest(3, "Second"));

    Assert.Equal((HttpStatusCode)422, secondResponse.StatusCode);

    var validationErrors = await ReadValidationErrorsAsync(secondResponse);
    Assert.Contains("appointmentId", validationErrors.Keys, StringComparer.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task AuthenticatedCustomer_CanListOnlyOwnReviews()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var firstCustomer = await RegisterCustomerAsync(context.Client, "list-own-a");
    var secondCustomer = await RegisterCustomerAsync(context.Client, "list-own-b");
    var staff = await CreateStaffAsync(context.Factory, "list-own");

    var firstSlot = SlotOnNextDay(hour: 10, minute: 0);
    var secondSlot = SlotOnNextDay(hour: 11, minute: 0);
    var thirdSlot = SlotOnNextDay(hour: 12, minute: 0);

    await EnsureAvailabilityForDateAsync(context.Factory, staff.StaffProfileId, firstSlot);

    var firstAppointment = await CreateCustomerAppointmentAsync(context.Factory, firstCustomer.UserId, staff.StaffProfileId, firstSlot);
    var secondAppointment = await CreateCustomerAppointmentAsync(context.Factory, firstCustomer.UserId, staff.StaffProfileId, secondSlot);
    var thirdAppointment = await CreateCustomerAppointmentAsync(context.Factory, secondCustomer.UserId, staff.StaffProfileId, thirdSlot);

    await CompleteAppointmentAsync(context.Factory, firstAppointment.Id);
    await CompleteAppointmentAsync(context.Factory, secondAppointment.Id);
    await CompleteAppointmentAsync(context.Factory, thirdAppointment.Id);

    await CreateReviewViaServiceAsync(context.Factory, firstCustomer.UserId, firstAppointment.Id, 4, "Good");
    await CreateReviewViaServiceAsync(context.Factory, firstCustomer.UserId, secondAppointment.Id, 5, "Great");
    await CreateReviewViaServiceAsync(context.Factory, secondCustomer.UserId, thirdAppointment.Id, 2, "Not mine");

    AuthenticateAs(context.Client, firstCustomer.AccessToken);

    using var response = await context.Client.GetAsync("/api/v1/customer/reviews");

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);

    var payload = await response.Content.ReadFromJsonAsync<List<CustomerReviewView>>();
    Assert.NotNull(payload);
    Assert.Equal(2, payload!.Count);

    var appointmentIds = payload.Select(review => review.AppointmentId).ToHashSet();

    Assert.Contains(firstAppointment.Id, appointmentIds);
    Assert.Contains(secondAppointment.Id, appointmentIds);
    Assert.DoesNotContain(thirdAppointment.Id, appointmentIds);
  }

  [Fact]
  public async Task AnonymousUser_CanListPublicReviewsForStaffProfile()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var staff = await CreateStaffAsync(context.Factory, "public-list");
    var firstCustomer = await RegisterCustomerAsync(context.Client, "public-list-a");
    var secondCustomer = await RegisterCustomerAsync(context.Client, "public-list-b");

    var firstSlot = SlotOnNextDay(hour: 10, minute: 0);
    var secondSlot = SlotOnNextDay(hour: 11, minute: 0);

    await EnsureAvailabilityForDateAsync(context.Factory, staff.StaffProfileId, firstSlot);

    var firstAppointment = await CreateCustomerAppointmentAsync(context.Factory, firstCustomer.UserId, staff.StaffProfileId, firstSlot);
    var secondAppointment = await CreateCustomerAppointmentAsync(context.Factory, secondCustomer.UserId, staff.StaffProfileId, secondSlot);

    await CompleteAppointmentAsync(context.Factory, firstAppointment.Id);
    await CompleteAppointmentAsync(context.Factory, secondAppointment.Id);

    await CreateReviewViaServiceAsync(context.Factory, firstCustomer.UserId, firstAppointment.Id, 4, "Great");
    await CreateReviewViaServiceAsync(context.Factory, secondCustomer.UserId, secondAppointment.Id, 5, "Excellent");

    context.Client.DefaultRequestHeaders.Authorization = null;

    using var response = await context.Client.GetAsync($"/api/v1/public/staff/{staff.StaffProfileId}/reviews");

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);

    var payload = await response.Content.ReadFromJsonAsync<List<PublicStaffReviewView>>();
    Assert.NotNull(payload);
    Assert.Equal(2, payload!.Count);

    var appointmentIds = payload.Select(review => review.AppointmentId).ToHashSet();

    Assert.Contains(firstAppointment.Id, appointmentIds);
    Assert.Contains(secondAppointment.Id, appointmentIds);
  }

  [Fact]
  public async Task AnonymousUser_CanGetAverageRatingAndReviewCount()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var staff = await CreateStaffAsync(context.Factory, "public-summary");
    var firstCustomer = await RegisterCustomerAsync(context.Client, "public-summary-a");
    var secondCustomer = await RegisterCustomerAsync(context.Client, "public-summary-b");

    var firstSlot = SlotOnNextDay(hour: 10, minute: 0);
    var secondSlot = SlotOnNextDay(hour: 11, minute: 0);

    await EnsureAvailabilityForDateAsync(context.Factory, staff.StaffProfileId, firstSlot);

    var firstAppointment = await CreateCustomerAppointmentAsync(context.Factory, firstCustomer.UserId, staff.StaffProfileId, firstSlot);
    var secondAppointment = await CreateCustomerAppointmentAsync(context.Factory, secondCustomer.UserId, staff.StaffProfileId, secondSlot);

    await CompleteAppointmentAsync(context.Factory, firstAppointment.Id);
    await CompleteAppointmentAsync(context.Factory, secondAppointment.Id);

    await CreateReviewViaServiceAsync(context.Factory, firstCustomer.UserId, firstAppointment.Id, 4, "Good");
    await CreateReviewViaServiceAsync(context.Factory, secondCustomer.UserId, secondAppointment.Id, 5, "Excellent");

    context.Client.DefaultRequestHeaders.Authorization = null;

    using var response = await context.Client.GetAsync($"/api/v1/public/staff/{staff.StaffProfileId}/reviews/summary");

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);

    var payload = await response.Content.ReadFromJsonAsync<PublicStaffReviewsSummaryView>();
    Assert.NotNull(payload);
    Assert.Equal(staff.StaffProfileId, payload!.StaffProfileId);
    Assert.Equal(2, payload.TotalReviews);
    Assert.Equal(4.5m, payload.AverageStars);
  }

  [Fact]
  public async Task CreateCustomerReview_RequiresAuthentication()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    using var response = await context.Client.PostAsJsonAsync(
        $"/api/v1/customer/appointments/{Guid.NewGuid()}/review",
        new ReviewCreateRequest(5, "Auth required"));

    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
  }

  [Fact]
  public async Task GetCustomerReviews_RequiresAuthentication()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    using var response = await context.Client.GetAsync("/api/v1/customer/reviews");

    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
  }

  [Fact]
  public async Task PublicReviewEndpoints_DoNotRequireAuthentication()
  {
    using var context = await CreateContextAsync();
    if (context is null)
    {
      return;
    }

    var staff = await CreateStaffAsync(context.Factory, "public-no-auth");

    context.Client.DefaultRequestHeaders.Authorization = null;

    using var listResponse = await context.Client.GetAsync($"/api/v1/public/staff/{staff.StaffProfileId}/reviews");
    using var summaryResponse = await context.Client.GetAsync($"/api/v1/public/staff/{staff.StaffProfileId}/reviews/summary");

    Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
    Assert.Equal(HttpStatusCode.OK, summaryResponse.StatusCode);

    var listPayload = await listResponse.Content.ReadFromJsonAsync<List<PublicStaffReviewView>>();
    var summaryPayload = await summaryResponse.Content.ReadFromJsonAsync<PublicStaffReviewsSummaryView>();

    Assert.NotNull(listPayload);
    Assert.NotNull(summaryPayload);
    Assert.Empty(listPayload!);
    Assert.Equal(0, summaryPayload!.TotalReviews);
    Assert.Equal(0m, summaryPayload.AverageStars);
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

    var client = factory.CreateClient();
    return new TestHttpContext(factory, client);
  }

  private static async Task<AuthSession> RegisterCustomerAsync(HttpClient client, string label)
  {
    var request = new RegisterRequest(
        FullName: $"Customer {label}",
        Email: UniqueEmail($"customer-{label}"),
        Password: DefaultPassword,
        PhoneNumber: null);

    using var response = await client.PostAsJsonAsync("/api/v1/auth/register", request);
    Assert.Equal(HttpStatusCode.Created, response.StatusCode);

    var payload = await response.Content.ReadFromJsonAsync<TokenResponse>();
    Assert.NotNull(payload);

    return new AuthSession(payload!.User.Id, payload.AccessToken);
  }

  private static async Task<StaffManagementView> CreateStaffAsync(IntegrationTestFactory factory, string label)
  {
    return await WithScopeAsync(factory, async serviceProvider =>
    {
      var staffService = serviceProvider.GetRequiredService<IAdminStaffService>();

      return await staffService.CreateAsync(new AdminStaffCreateRequest(
              FullName: $"Staff {label}",
              Email: UniqueEmail($"staff-{label}"),
              DisplayName: $"Staff {label}",
              InitialPassword: DefaultPassword,
              PhoneNumber: "+5491100000000",
              Bio: null,
              DefaultAppointmentDurationMinutes: 30,
              PhotoMediaAssetId: null,
              TipsQrMediaAssetId: null,
              IsActive: true));
    });
  }

  private static async Task EnsureAvailabilityForDateAsync(IntegrationTestFactory factory, Guid staffProfileId, DateTime startsAtUtc)
  {
    await WithScopeAsync(factory, async serviceProvider =>
    {
      var availabilityService = serviceProvider.GetRequiredService<IAdminStaffAvailabilityService>();

      var rules = new[]
          {
                new AvailabilityRuleRequest(
                    DayOfWeek: (int)startsAtUtc.DayOfWeek,
                    StartTime: new TimeOnly(9, 0),
                    EndTime: new TimeOnly(18, 0),
                    IsActive: true),
        };

      await availabilityService.ReplaceRulesAsync(staffProfileId, rules);
    });
  }

  private static async Task<AppointmentView> CreateCustomerAppointmentAsync(
      IntegrationTestFactory factory,
      Guid customerUserId,
      Guid staffProfileId,
      DateTime startsAtUtc)
  {
    return await WithScopeAsync(factory, async serviceProvider =>
    {
      var appointmentService = serviceProvider.GetRequiredService<ICustomerAppointmentsService>();

      return await appointmentService.CreateAsync(
              customerUserId,
              new CustomerAppointmentCreateRequest(staffProfileId, startsAtUtc, Notes: null));
    });
  }

  private static async Task CompleteAppointmentAsync(IntegrationTestFactory factory, Guid appointmentId)
  {
    await WithScopeAsync(factory, async serviceProvider =>
    {
      var appointmentService = serviceProvider.GetRequiredService<IAdminAppointmentsService>();

      await appointmentService.UpdateStatusAsync(
              appointmentId,
              new AppointmentStatusUpdateRequest(AppointmentStatus.Completed));
    });
  }

  private static async Task<CustomerReviewView> CreateReviewViaServiceAsync(
      IntegrationTestFactory factory,
      Guid customerUserId,
      Guid appointmentId,
      int stars,
      string? comment)
  {
    return await WithScopeAsync(factory, async serviceProvider =>
    {
      var reviewsService = serviceProvider.GetRequiredService<ICustomerReviewsService>();

      return await reviewsService.CreateAsync(
              customerUserId,
              appointmentId,
              new ReviewCreateRequest(stars, comment));
    });
  }

  private static async Task<T> WithScopeAsync<T>(IntegrationTestFactory factory, Func<IServiceProvider, Task<T>> action)
  {
    using var scope = factory.Services.CreateScope();
    return await action(scope.ServiceProvider);
  }

  private static async Task WithScopeAsync(IntegrationTestFactory factory, Func<IServiceProvider, Task> action)
  {
    using var scope = factory.Services.CreateScope();
    await action(scope.ServiceProvider);
  }

  private static void AuthenticateAs(HttpClient client, string accessToken)
  {
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
  }

  private static DateTime SlotOnNextDay(int hour, int minute)
  {
    var value = DateTime.UtcNow.Date.AddDays(1).AddHours(hour).AddMinutes(minute);
    return DateTime.SpecifyKind(value, DateTimeKind.Utc);
  }

  private static string UniqueEmail(string prefix)
  {
    return $"{prefix}-{Guid.NewGuid():N}@example.com";
  }

  private static async Task<IReadOnlyDictionary<string, string[]>> ReadValidationErrorsAsync(HttpResponseMessage response)
  {
    await using var stream = await response.Content.ReadAsStreamAsync();
    using var document = await JsonDocument.ParseAsync(stream);

    if (!document.RootElement.TryGetProperty("errors", out var errorsElement)
        || errorsElement.ValueKind != JsonValueKind.Object)
    {
      return new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
    }

    var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
    foreach (var property in errorsElement.EnumerateObject())
    {
      if (property.Value.ValueKind == JsonValueKind.Array)
      {
        errors[property.Name] = property.Value
            .EnumerateArray()
            .Select(element => element.GetString() ?? string.Empty)
            .ToArray();
      }
      else
      {
        errors[property.Name] = [property.Value.ToString()];
      }
    }

    return errors;
  }

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
