using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Barbershop.Application.Auth;
using Barbershop.Tests.Infrastructure;
using Xunit.Abstractions;

namespace Barbershop.Tests.Features.Media;

/// <summary>
/// Upload routes over real HTTP: unreadable multipart bodies and oversized bodies (BUG-095), purpose outside the enum
/// (BUG-098) and files whose content is not what they declare (BUG-020, BUG-058).
/// </summary>
[Collection(IntegrationTestCollection.Name)]
[Trait("Category", "UploadRequestValidationHttpIntegration")]
public sealed class UploadRequestValidationHttpIntegrationTests
{
  private const string DefaultPassword = "Secret123!";
  private const string MediaUpload = "/api/v1/media/upload";
  private const string CustomerPhoto = "/api/v1/customer/profile/photo";

  private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01, 0x02];

  private readonly PostgresContainerFixture _postgres;
  private readonly ITestOutputHelper _output;

  public UploadRequestValidationHttpIntegrationTests(PostgresContainerFixture postgres, ITestOutputHelper output)
  {
    _postgres = postgres;
    _output = output;
  }

  /// <summary>BUG-095: malformed, without boundary and truncated multipart bodies answered 500.</summary>
  [Theory]
  [InlineData(MediaUpload, "malformed")]
  [InlineData(MediaUpload, "no-boundary")]
  [InlineData(MediaUpload, "truncated")]
  [InlineData(CustomerPhoto, "malformed")]
  [InlineData(CustomerPhoto, "no-boundary")]
  [InlineData(CustomerPhoto, "truncated")]
  public async Task AnUnreadableMultipartBody_Answers400(string route, string shape)
  {
    using var context = await CreateCustomerClientAsync();
    if (context is null)
    {
      return;
    }

    var (contentType, body) = shape switch
    {
      "malformed" => ("multipart/form-data; boundary=x", "{"),
      "no-boundary" => ("multipart/form-data", "--x\r\nContent-Disposition: form-data; name=\"purpose\"\r\n\r\nCustomerReference\r\n--x--\r\n"),
      _ => ("multipart/form-data; boundary=XYZ",
          "--XYZ\r\nContent-Disposition: form-data; name=\"purpose\"\r\n\r\nCustomerReference\r\n"
          + "--XYZ\r\nContent-Disposition: form-data; name=\"file\"; filename=\"a.png\"\r\nContent-Type: image/png\r\n\r\n\u0089PNG")
    };
    using var content = new ByteArrayContent(Encoding.Latin1.GetBytes(body));
    content.Headers.TryAddWithoutValidation("Content-Type", contentType);

    using var response = await context.Client.PostAsync(route, content);

    var problem = await ReadProblemAsync(response);
    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    Assert.Equal("invalid_request", problem.GetProperty("code").GetString());
    Assert.Contains("formulario", problem.GetProperty("detail").GetString());
  }

  /// <summary>BUG-095: 50 and 200 MB bodies answered 500 after reading them; now 413 before reading.</summary>
  [Theory]
  [InlineData(MediaUpload, "file")]
  [InlineData(CustomerPhoto, "photo")]
  public async Task ABodyAboveTheUploadLimit_Answers413(string route, string field)
  {
    using var context = await CreateCustomerClientAsync();
    if (context is null)
    {
      return;
    }

    var payload = new byte[12 * 1024 * 1024];
    PngBytes.CopyTo(payload, 0);
    using var content = new MultipartFormDataContent();
    content.Add(new StringContent("CustomerReference"), "purpose");
    var file = new ByteArrayContent(payload);
    file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
    content.Add(file, field, "big.png");

    using var response = await context.Client.PostAsync(route, content);

    var problem = await ReadProblemAsync(response);
    Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    Assert.Equal("payload_too_large", problem.GetProperty("code").GetString());
    Assert.Contains("demasiado grande", problem.GetProperty("detail").GetString());
  }

  /// <summary>BUG-098: the purpose was bound by number without checking it is defined.</summary>
  [Theory]
  [InlineData("999")]
  [InlineData("6")]
  [InlineData("CustomerReference, Logo")]
  public async Task APurposeThatIsNotAnEnumName_Answers422(string purpose)
  {
    using var context = await CreateCustomerClientAsync();
    if (context is null)
    {
      return;
    }

    using var response = await context.Client.PostAsync(MediaUpload, Multipart("file", "photo.png", "image/png", PngBytes, purpose));

    var problem = await ReadProblemAsync(response);
    Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    Assert.True(problem.GetProperty("errors").TryGetProperty("purpose", out _));
  }

  /// <summary>BUG-020: HTML sent as image/png by a customer was stored on public storage.</summary>
  [Fact]
  public async Task HtmlDeclaredAsPng_Answers422()
  {
    using var context = await CreateCustomerClientAsync();
    if (context is null)
    {
      return;
    }

    var html = Encoding.UTF8.GetBytes("<html><body><script>alert(document.domain)</script></body></html>");
    using var response = await context.Client.PostAsync(MediaUpload, Multipart("file", "fake.png", "image/png", html, "CustomerReference"));

    var problem = await ReadProblemAsync(response);
    Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    Assert.StartsWith("El archivo no es una imagen", problem.GetProperty("errors").GetProperty("contentType")[0].GetString());
  }

  /// <summary>BUG-058: the customer's profile photo accepted a real PDF and showed a broken image.</summary>
  [Theory]
  [InlineData("application/pdf")]
  [InlineData("image/png")]
  public async Task APdfAsTheCustomerProfilePhoto_Answers422(string declaredContentType)
  {
    using var context = await CreateCustomerClientAsync();
    if (context is null)
    {
      return;
    }

    var pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj << /Type /Catalog >> endobj\n%%EOF\n");
    using var response = await context.Client.PostAsync(CustomerPhoto, Multipart("photo", "foto.png", declaredContentType, pdf));

    var problem = await ReadProblemAsync(response);
    Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    Assert.Contains("solo se aceptan imágenes", problem.GetProperty("errors").GetProperty("contentType")[0].GetString());
  }

  private static MultipartFormDataContent Multipart(string field, string fileName, string contentType, byte[] bytes, string? purpose = null)
  {
    var content = new MultipartFormDataContent();
    if (purpose is not null)
    {
      content.Add(new StringContent(purpose), "purpose");
    }

    var file = new ByteArrayContent(bytes);
    file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
    content.Add(file, field, fileName);
    return content;
  }

  private async Task<JsonElement> ReadProblemAsync(HttpResponseMessage response)
  {
    var body = await response.Content.ReadAsStringAsync();
    _output.WriteLine($"{(int)response.StatusCode} {body}");
    return string.IsNullOrWhiteSpace(body) ? default : JsonDocument.Parse(body).RootElement.Clone();
  }

  private async Task<ClientContext?> CreateCustomerClientAsync()
  {
    if (!_postgres.IsAvailable)
    {
      _output.WriteLine(_postgres.UnavailableReason ?? "PostgreSQL Testcontainer is unavailable.");
      return null;
    }

    var factory = new IntegrationTestFactory(_postgres.ConnectionString);
    await factory.ResetDatabaseAsync();
    var client = factory.CreateClient();

    using var register = await client.PostAsJsonAsync(
        "/api/v1/auth/register",
        new RegisterRequest("Upload Customer", $"upload-{Guid.NewGuid():N}@example.com", DefaultPassword, null));
    Assert.Equal(HttpStatusCode.Created, register.StatusCode);
    var session = await register.Content.ReadFromJsonAsync<TokenResponse>();
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session!.AccessToken);

    return new ClientContext(factory, client);
  }

  private sealed class ClientContext(IntegrationTestFactory factory, HttpClient client) : IDisposable
  {
    public HttpClient Client { get; } = client;

    public void Dispose()
    {
      Client.Dispose();
      factory.Dispose();
    }
  }
}
