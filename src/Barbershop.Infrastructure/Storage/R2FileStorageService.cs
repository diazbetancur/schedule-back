using Barbershop.Application.Storage;
using Barbershop.Infrastructure.Configuration;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Barbershop.Infrastructure.Storage;

public sealed class R2FileStorageService : IFileStorageService
{
    private readonly R2StorageOptions _storageOptions;
    private readonly ILogger<R2FileStorageService> _logger;
    private readonly Lazy<IAmazonS3> _s3Client;

    public R2FileStorageService(IOptions<R2StorageOptions> storageOptions, ILogger<R2FileStorageService> logger)
    {
        _storageOptions = storageOptions.Value;
        _logger = logger;
        _s3Client = new Lazy<IAmazonS3>(CreateS3Client);
    }

    public async Task<StoredFileResult> UploadAsync(FileStorageObject file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(file.Content);

        EnsureConfigured();

        var putRequest = new PutObjectRequest
        {
            BucketName = _storageOptions.BucketName,
            Key = file.ObjectKey,
            InputStream = file.Content,
            ContentType = file.ContentType,
            AutoCloseStream = false,
            UseChunkEncoding = false   // Cloudflare R2 does not support STREAMING-AWS4-HMAC-SHA256-PAYLOAD-TRAILER
        };

        if (file.ContentLength.HasValue)
        {
            putRequest.Headers.ContentLength = file.ContentLength.Value;
        }

        try
        {
            await _s3Client.Value.PutObjectAsync(putRequest, cancellationToken);
        }
        catch (AmazonS3Exception s3Ex)
        {
            _logger.LogError(s3Ex,
                "R2 upload failed for {ObjectKey}: HTTP {StatusCode}, code={ErrorCode}, message={Message}",
                file.ObjectKey, (int)s3Ex.StatusCode, s3Ex.ErrorCode, s3Ex.Message);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error uploading {ObjectKey} to R2", file.ObjectKey);
            throw;
        }

        _logger.LogInformation("Uploaded object {ObjectKey} to R2 bucket {BucketName}", file.ObjectKey, _storageOptions.BucketName);
        return new StoredFileResult(file.ObjectKey, BuildPublicUri(file.ObjectKey));
    }

    public async Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(objectKey))
        {
            return;
        }

        EnsureConfigured();

        var deleteRequest = new DeleteObjectRequest
        {
            BucketName = _storageOptions.BucketName,
            Key = objectKey
        };

        await _s3Client.Value.DeleteObjectAsync(deleteRequest, cancellationToken);
        _logger.LogInformation("Deleted object {ObjectKey} from R2 bucket {BucketName}", objectKey, _storageOptions.BucketName);
    }

    private IAmazonS3 CreateS3Client()
    {
        var config = new AmazonS3Config
        {
            ServiceURL = _storageOptions.Endpoint,
            ForcePathStyle = true,
            AuthenticationRegion = string.IsNullOrWhiteSpace(_storageOptions.Region) ? "auto" : _storageOptions.Region
        };

        var credentials = new BasicAWSCredentials(_storageOptions.AccessKeyId, _storageOptions.SecretAccessKey);
        return new AmazonS3Client(credentials, config);
    }

    private void EnsureConfigured()
    {
        var missingSettings = new List<string>();

        if (!OptionsValidationHelpers.IsConfigured(_storageOptions.BucketName))
        {
            missingSettings.Add($"{R2StorageOptions.SectionName}:BucketName");
        }

        if (!OptionsValidationHelpers.IsConfigured(_storageOptions.AccessKeyId))
        {
            missingSettings.Add($"{R2StorageOptions.SectionName}:AccessKeyId");
        }

        if (!OptionsValidationHelpers.IsConfigured(_storageOptions.SecretAccessKey))
        {
            missingSettings.Add($"{R2StorageOptions.SectionName}:SecretAccessKey");
        }

        if (!OptionsValidationHelpers.IsConfigured(_storageOptions.Endpoint))
        {
            missingSettings.Add($"{R2StorageOptions.SectionName}:Endpoint");
        }

        if (!OptionsValidationHelpers.IsConfigured(_storageOptions.PublicBaseUrl))
        {
            missingSettings.Add($"{R2StorageOptions.SectionName}:PublicBaseUrl");
        }

        if (missingSettings.Count > 0)
        {
            var message = $"R2 storage is not fully configured. Missing settings: {string.Join(", ", missingSettings)}.";
            _logger.LogError(message);
            throw new InvalidOperationException(message);
        }
    }

    public string? GetPublicUrl(string storageKey) => BuildPublicUri(storageKey)?.ToString();

    private Uri? BuildPublicUri(string objectKey)
    {
        if (!Uri.TryCreate(_storageOptions.PublicBaseUrl, UriKind.Absolute, out var baseUri))
        {
            return null;
        }

        var encodedKey = string.Join(
            "/",
            objectKey
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Uri.EscapeDataString));

        return new Uri(baseUri, encodedKey);
    }
}
