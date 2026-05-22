using System.ComponentModel.DataAnnotations;

namespace Barbershop.Infrastructure.Configuration;

public sealed class FileStorageOptions
{
  public const string SectionName = "FileStorage";

  [Range(1, long.MaxValue)]
  public long MaxUploadBytes { get; init; } = 5 * 1024 * 1024;

  public string[] AllowedContentTypes { get; init; } =
  [
      "image/jpeg",
      "image/png",
      "image/webp",
      "image/gif",
      "application/pdf"
  ];
}
