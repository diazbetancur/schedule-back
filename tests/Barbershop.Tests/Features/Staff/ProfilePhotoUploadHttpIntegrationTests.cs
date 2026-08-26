using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Barbershop.Application.Auth;
using Barbershop.Tests.Infrastructure;
using Xunit.Abstractions;

namespace Barbershop.Tests.Features.Staff;

[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "ProfilePhotoHttpIntegration")]
public sealed class ProfilePhotoUploadHttpIntegrationTests
{
  private const string DefaultPassword = "Secret123!";

  private readonly PostgresContainerFixture _postgres;
  private readonly ITestOutputHelper _output;

  public ProfilePhotoUploadHttpIntegrationTests(PostgresContainerFixture postgres, ITestOutputHelper output)
  {
    _postgres = postgres;
    _output = output;
  }

  [Fact]
  public async Task UploadCustomerPhoto_ViaRealMultipartRequest_FindsThePhotoField()
  {
    if (!_postgres.IsAvailable)
    {
      _output.WriteLine(_postgres.UnavailableReason ?? "PostgreSQL Testcontainer is unavailable.");
      return;
    }

    var factory = new IntegrationTestFactory(_postgres.ConnectionString);
    await factory.ResetDatabaseAsync();
    using var client = factory.CreateClient();

    var registerRequest = new RegisterRequest(
        FullName: "Photo Customer",
        Email: $"photo-customer-{Guid.NewGuid():N}@example.com",
        Password: DefaultPassword,
        PhoneNumber: null);

    using var registerResponse = await client.PostAsJsonAsync("/api/v1/auth/register", registerRequest);
    Assert.Equal(HttpStatusCode.Created, registerResponse.StatusCode);

    var session = await registerResponse.Content.ReadFromJsonAsync<TokenResponse>();
    Assert.NotNull(session);
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session!.AccessToken);

    // A minimal valid PNG payload — same technique as MediaAssetUploadTests.CreateStream.
    var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01, 0x02 };

    using var content = new MultipartFormDataContent();
    using var fileContent = new ByteArrayContent(pngBytes);
    fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
    content.Add(fileContent, "photo", "photo.png");

    using var response = await client.PostAsync("/api/v1/customer/profile/photo", content);
    var body = await response.Content.ReadAsStringAsync();
    _output.WriteLine($"Status: {response.StatusCode}");
    _output.WriteLine($"Body: {body}");

    // The point of this test: prove whether ReadSingleFileAsync finds the "photo" field
    // from a real HTTP multipart request. If it does NOT, we get a 422 with
    // "A file is required." — the exact bug reported. Any other outcome (e.g. 503 because
    // R2 storage isn't configured in the Testing environment) means the field WAS found.
    var isFileRequiredError = response.StatusCode == HttpStatusCode.UnprocessableEntity
        && body.Contains("A file is required", StringComparison.Ordinal);

    Assert.False(isFileRequiredError, $"Multipart 'photo' field was not found by the backend. Status={response.StatusCode}, Body={body}");
  }

  [Fact]
  public async Task UploadCustomerPhoto_ViaRawBodyRequest_DoesNotRequireMultipartField()
  {
    if (!_postgres.IsAvailable)
    {
      _output.WriteLine(_postgres.UnavailableReason ?? "PostgreSQL Testcontainer is unavailable.");
      return;
    }

    var factory = new IntegrationTestFactory(_postgres.ConnectionString);
    await factory.ResetDatabaseAsync();
    using var client = factory.CreateClient();

    var registerRequest = new RegisterRequest(
        FullName: "Raw Photo Customer",
        Email: $"raw-photo-customer-{Guid.NewGuid():N}@example.com",
        Password: DefaultPassword,
        PhoneNumber: null);

    using var registerResponse = await client.PostAsJsonAsync("/api/v1/auth/register", registerRequest);
    Assert.Equal(HttpStatusCode.Created, registerResponse.StatusCode);

    var session = await registerResponse.Content.ReadFromJsonAsync<TokenResponse>();
    Assert.NotNull(session);
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session!.AccessToken);

    var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01, 0x02 };

    using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/customer/profile/photo")
    {
      Content = new ByteArrayContent(pngBytes)
    };
    request.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
    request.Headers.TryAddWithoutValidation("X-Upload-File-Name", Uri.EscapeDataString("photo.png"));
    request.Headers.TryAddWithoutValidation("X-Upload-File-Size", pngBytes.Length.ToString());
    request.Headers.TryAddWithoutValidation("X-Upload-File-Type", "image/png");

    using var response = await client.SendAsync(request);
    var body = await response.Content.ReadAsStringAsync();
    _output.WriteLine($"Status: {response.StatusCode}");
    _output.WriteLine($"Body: {body}");

    var isFileRequiredError = response.StatusCode == HttpStatusCode.UnprocessableEntity
        && body.Contains("A file is required", StringComparison.Ordinal);

    Assert.False(isFileRequiredError, $"Raw body upload was rejected as an empty file. Status={response.StatusCode}, Body={body}");
  }
}
