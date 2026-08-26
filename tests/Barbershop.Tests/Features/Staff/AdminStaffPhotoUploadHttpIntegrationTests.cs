using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Barbershop.Application.Auth;
using Barbershop.Application.Staff;
using Barbershop.Application.Staff.Admin;
using Barbershop.Domain.Users;
using Barbershop.Infrastructure.Persistence;
using Barbershop.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Barbershop.Tests.Features.Staff;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "AdminStaffPhotoHttpIntegration")]
public sealed class AdminStaffPhotoUploadHttpIntegrationTests
{
  private const string DefaultPassword = "Secret123!";

  private readonly PostgresContainerFixture _postgres;
  private readonly ITestOutputHelper _output;

  public AdminStaffPhotoUploadHttpIntegrationTests(PostgresContainerFixture postgres, ITestOutputHelper output)
  {
    _postgres = postgres;
    _output = output;
  }

  [Fact]
  public async Task UploadPhotoAsAdmin_ViaRealMultipartRequest_FindsThePhotoField()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var admin = await RegisterAdminAsync(context.Client, context.Factory, "photo-upload");
    AuthenticateAs(context.Client, admin.AccessToken);
    var staffProfileId = await CreateStaffAsync(context.Client, "photo-upload");

    var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01, 0x02 };
    using var content = new MultipartFormDataContent();
    using var fileContent = new ByteArrayContent(pngBytes);
    fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
    content.Add(fileContent, "photo", "photo.png");

    using var response = await context.Client.PostAsync($"/api/v1/admin/staff/{staffProfileId}/photo", content);
    var body = await response.Content.ReadAsStringAsync();
    _output.WriteLine($"Status: {response.StatusCode}");
    _output.WriteLine($"Body: {body}");

    // Igual que ProfilePhotoUploadHttpIntegrationTests: no se puede asegurar 200 porque el
    // almacenamiento real (R2) puede no estar configurado en el entorno de test. Lo que este
    // test prueba es que el campo multipart "photo" SÍ es encontrado (si no, da 422 con
    // "A file is required").
    var isFileRequiredError = response.StatusCode == HttpStatusCode.UnprocessableEntity
        && body.Contains("A file is required", StringComparison.Ordinal);

    Assert.False(isFileRequiredError, $"Multipart 'photo' field was not found by the backend. Status={response.StatusCode}, Body={body}");
  }

  [Fact]
  public async Task UploadPhotoAsPlainCustomer_ReturnsForbidden()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var admin = await RegisterAdminAsync(context.Client, context.Factory, "photo-403-setup");
    AuthenticateAs(context.Client, admin.AccessToken);
    var staffProfileId = await CreateStaffAsync(context.Client, "photo-403");

    var customer = await RegisterCustomerAsync(context.Client, "photo-403");
    AuthenticateAs(context.Client, customer.AccessToken);

    var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
    using var content = new MultipartFormDataContent();
    using var fileContent = new ByteArrayContent(pngBytes);
    fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
    content.Add(fileContent, "photo", "photo.png");

    using var response = await context.Client.PostAsync($"/api/v1/admin/staff/{staffProfileId}/photo", content);

    Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
  }

  [Fact]
  public async Task UploadPhotoAsAdmin_UnknownStaffId_ReturnsNotFound()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var admin = await RegisterAdminAsync(context.Client, context.Factory, "photo-404");
    AuthenticateAs(context.Client, admin.AccessToken);

    var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 };
    using var content = new MultipartFormDataContent();
    using var fileContent = new ByteArrayContent(pngBytes);
    fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
    content.Add(fileContent, "photo", "photo.png");

    using var response = await context.Client.PostAsync($"/api/v1/admin/staff/{Guid.NewGuid()}/photo", content);

    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
  }

  [Fact]
  public async Task RemovePhotoAsAdmin_WhenNoPhotoExists_ReturnsOkWithNullPhoto()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var admin = await RegisterAdminAsync(context.Client, context.Factory, "photo-remove");
    AuthenticateAs(context.Client, admin.AccessToken);
    var staffProfileId = await CreateStaffAsync(context.Client, "photo-remove");

    using var response = await context.Client.DeleteAsync($"/api/v1/admin/staff/{staffProfileId}/photo");

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    var payload = await response.Content.ReadFromJsonAsync<StaffManagementView>();
    Assert.NotNull(payload);
    Assert.Null(payload!.PhotoMediaAssetId);
  }

  [Fact]
  public async Task UploadTipsQrAsAdmin_ViaRealMultipartRequest_FindsTheFileField()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var admin = await RegisterAdminAsync(context.Client, context.Factory, "qr-upload");
    AuthenticateAs(context.Client, admin.AccessToken);
    var staffProfileId = await CreateStaffAsync(context.Client, "qr-upload");

    var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01, 0x02 };
    using var content = new MultipartFormDataContent();
    using var fileContent = new ByteArrayContent(pngBytes);
    fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
    content.Add(fileContent, "file", "qr.png");

    using var response = await context.Client.PostAsync($"/api/v1/admin/staff/{staffProfileId}/tips-qr", content);
    var body = await response.Content.ReadAsStringAsync();
    _output.WriteLine($"Status: {response.StatusCode}");
    _output.WriteLine($"Body: {body}");

    var isFileRequiredError = response.StatusCode == HttpStatusCode.UnprocessableEntity
        && body.Contains("A file is required", StringComparison.Ordinal);

    Assert.False(isFileRequiredError, $"Multipart 'file' field was not found by the backend. Status={response.StatusCode}, Body={body}");
  }

  [Fact]
  public async Task RemoveTipsQrAsAdmin_WhenNoQrExists_ReturnsOkWithNullQr()
  {
    using var context = await CreateContextAsync();
    if (context is null) return;

    var admin = await RegisterAdminAsync(context.Client, context.Factory, "qr-remove");
    AuthenticateAs(context.Client, admin.AccessToken);
    var staffProfileId = await CreateStaffAsync(context.Client, "qr-remove");

    using var response = await context.Client.DeleteAsync($"/api/v1/admin/staff/{staffProfileId}/tips-qr");

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    var payload = await response.Content.ReadFromJsonAsync<StaffManagementView>();
    Assert.NotNull(payload);
    Assert.Null(payload!.TipsQrMediaAssetId);
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

  private static async Task<Guid> CreateStaffAsync(HttpClient client, string label)
  {
    var request = new AdminStaffCreateRequest(
        FullName: $"Staff {label}",
        Email: UniqueEmail($"staff-{label}"),
        DisplayName: $"Staff {label}",
        InitialPassword: DefaultPassword,
        PhoneNumber: null,
        Bio: null,
        DefaultAppointmentDurationMinutes: null,
        PhotoMediaAssetId: null,
        TipsQrMediaAssetId: null,
        IsActive: true);

    using var response = await client.PostAsJsonAsync("/api/v1/admin/staff", request);
    Assert.Equal(HttpStatusCode.Created, response.StatusCode);

    var payload = await response.Content.ReadFromJsonAsync<StaffManagementView>();
    Assert.NotNull(payload);
    return payload!.StaffProfileId;
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

  private static async Task<AuthSession> RegisterAdminAsync(HttpClient client, IntegrationTestFactory factory, string label)
  {
    var email = UniqueEmail($"admin-{label}");

    var registerRequest = new RegisterRequest(
        FullName: $"Admin {label}",
        Email: email,
        Password: DefaultPassword,
        PhoneNumber: null);

    using var registerResponse = await client.PostAsJsonAsync("/api/v1/auth/register", registerRequest);
    Assert.Equal(HttpStatusCode.Created, registerResponse.StatusCode);

    await PromoteToAdminAsync(factory, email);

    var loginRequest = new LoginRequest(email, DefaultPassword);

    using var loginResponse = await client.PostAsJsonAsync("/api/v1/auth/login", loginRequest);
    Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

    var payload = await loginResponse.Content.ReadFromJsonAsync<TokenResponse>();
    Assert.NotNull(payload);

    return new AuthSession(payload!.User.Id, payload.AccessToken);
  }

  private static async Task PromoteToAdminAsync(IntegrationTestFactory factory, string email)
  {
    using var scope = factory.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    var normalizedEmail = email.ToUpperInvariant();
    var user = await dbContext.Users
        .Include(candidate => candidate.UserRoles)
        .SingleAsync(candidate => candidate.NormalizedEmail == normalizedEmail);

    var adminRole = await dbContext.Roles
        .SingleAsync(role => role.NormalizedName == RoleNames.Admin.ToUpperInvariant());

    if (user.UserRoles.All(assignment => assignment.RoleId != adminRole.Id))
    {
      user.UserRoles.Add(new UserRole(user.Id, adminRole.Id, DateTime.UtcNow));
      await dbContext.SaveChangesAsync();
    }
  }

  private static void AuthenticateAs(HttpClient client, string accessToken)
  {
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
  }

  private static string UniqueEmail(string prefix)
  {
    return $"{prefix}-{Guid.NewGuid():N}@example.com";
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
