using System.ComponentModel.DataAnnotations;

namespace Barbershop.Infrastructure.Configuration;

public sealed class R2StorageOptions
{
    public const string SectionName = "R2Storage";

    public string AccountId { get; init; } = string.Empty;
    public string BucketName { get; init; } = string.Empty;
    public string AccessKeyId { get; init; } = string.Empty;
    public string SecretAccessKey { get; init; } = string.Empty;

    [Url]
    public string Endpoint { get; init; } = string.Empty;

    [Url]
    public string PublicBaseUrl { get; init; } = string.Empty;

    public string Region { get; init; } = "auto";
}
