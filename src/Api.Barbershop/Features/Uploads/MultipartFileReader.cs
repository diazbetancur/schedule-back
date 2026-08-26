using Barbershop.Application.Common.Exceptions;
using System.Globalization;

namespace Api.Barbershop.Features.Uploads;

internal static class MultipartFileReader
{
  private const string FileNameHeaderName = "X-Upload-File-Name";
  private const string FileSizeHeaderName = "X-Upload-File-Size";
  private const string FileTypeHeaderName = "X-Upload-File-Type";

  public static async Task<UploadedRequestFile> ReadSingleFileAsync(
      HttpRequest request,
      string fieldName,
      string fallbackFileNameBase,
      long maxBytes,
      ILogger logger,
      CancellationToken cancellationToken)
  {
    if (!request.HasFormContentType)
    {
      return ReadRawBodyFile(request, fieldName, fallbackFileNameBase, maxBytes, logger);
    }

    var form = await request.ReadFormAsync(cancellationToken);
    var file = form.Files.GetFile(fieldName);

    if ((file is null || file.Length == 0)
        && form.Files.Count == 1
        && form.Files[0].Length > 0
        && !string.Equals(form.Files[0].Name, fieldName, StringComparison.Ordinal))
    {
      file = form.Files[0];
      logger.LogWarning(
          "Upload request for {Path} expected file field {ExpectedFieldName} but received single file field {ActualFieldName}; accepting it as fallback.",
          request.Path,
          fieldName,
          file.Name);
    }

    if (file is null || file.Length == 0)
    {
      logger.LogWarning(
          "Upload request for {Path} did not include a non-empty file in field {ExpectedFieldName}. ContentType={ContentType}; ContentLength={ContentLength}; ClientFileName={ClientFileName}; ClientFileSize={ClientFileSize}; ClientFileType={ClientFileType}; FormFields={FormFields}; FileFields={FileFields}.",
          request.Path,
          fieldName,
          request.ContentType,
          request.ContentLength,
          request.Headers[FileNameHeaderName].ToString(),
          request.Headers[FileSizeHeaderName].ToString(),
          request.Headers[FileTypeHeaderName].ToString(),
          DescribeFormFields(form),
          DescribeFileFields(form.Files));

      throw new ValidationProblemException(new Dictionary<string, string[]>
      {
        [fieldName] = ["A file is required."]
      });
    }

    if (file.Length > maxBytes)
    {
      throw new ValidationProblemException(new Dictionary<string, string[]>
      {
        [fieldName] = [$"File size must not exceed {maxBytes / 1024 / 1024} MB."]
      });
    }

    return new UploadedRequestFile(
        file.FileName,
        file.ContentType,
        file.Length,
        file.OpenReadStream(),
        DisposeContent: true);
  }

  private static UploadedRequestFile ReadRawBodyFile(
      HttpRequest request,
      string fieldName,
      string fallbackFileNameBase,
      long maxBytes,
      ILogger logger)
  {
    var length = request.ContentLength.GetValueOrDefault();
    if (length <= 0)
    {
      logger.LogWarning(
          "Upload request for {Path} did not include a non-empty raw body. ContentType={ContentType}; ContentLength={ContentLength}; ClientFileName={ClientFileName}; ClientFileSize={ClientFileSize}; ClientFileType={ClientFileType}.",
          request.Path,
          request.ContentType,
          request.ContentLength,
          request.Headers[FileNameHeaderName].ToString(),
          request.Headers[FileSizeHeaderName].ToString(),
          request.Headers[FileTypeHeaderName].ToString());

      throw new ValidationProblemException(new Dictionary<string, string[]>
      {
        [fieldName] = ["A file is required."]
      });
    }

    if (length > maxBytes)
    {
      throw new ValidationProblemException(new Dictionary<string, string[]>
      {
        [fieldName] = [$"File size must not exceed {maxBytes / 1024 / 1024} MB."]
      });
    }

    var contentType = ResolveRawContentType(request);
    var fileName = ResolveRawFileName(request, fallbackFileNameBase, contentType);

    logger.LogInformation(
        "Upload request for {Path} is using raw body upload. ContentType={ContentType}; ContentLength={ContentLength}; FileName={FileName}.",
        request.Path,
        contentType,
        length,
        fileName);

    return new UploadedRequestFile(
        fileName,
        contentType,
        length,
        request.Body,
        DisposeContent: false);
  }

  private static string DescribeFormFields(IFormCollection form)
      => form.Keys.Count == 0 ? "(none)" : string.Join(", ", form.Keys);

  private static string DescribeFileFields(IFormFileCollection files)
  {
    if (files.Count == 0)
    {
      return "(none)";
    }

    return string.Join(
        "; ",
        files.Select(file =>
            $"field={file.Name}, contentType={file.ContentType}, length={file.Length}, hasFileName={!string.IsNullOrWhiteSpace(file.FileName)}"));
  }

  private static string ResolveRawContentType(HttpRequest request)
  {
    var contentType = NormalizeContentType(request.ContentType);
    if (!string.IsNullOrWhiteSpace(contentType))
    {
      return contentType;
    }

    contentType = NormalizeContentType(request.Headers[FileTypeHeaderName].ToString());
    return string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType;
  }

  private static string NormalizeContentType(string? contentType)
  {
    if (string.IsNullOrWhiteSpace(contentType))
    {
      return string.Empty;
    }

    var separatorIndex = contentType.IndexOf(';', StringComparison.Ordinal);
    return (separatorIndex >= 0 ? contentType[..separatorIndex] : contentType).Trim();
  }

  private static string ResolveRawFileName(
      HttpRequest request,
      string fallbackFileNameBase,
      string contentType)
  {
    var fileName = request.Headers[FileNameHeaderName].ToString();

    if (!string.IsNullOrWhiteSpace(fileName))
    {
      try
      {
        fileName = Uri.UnescapeDataString(fileName.Trim());
      }
      catch (UriFormatException)
      {
        fileName = string.Empty;
      }

      fileName = Path.GetFileName(fileName);

      if (!string.IsNullOrWhiteSpace(fileName))
      {
        return fileName;
      }
    }

    return $"{fallbackFileNameBase}{ResolveExtension(contentType)}";
  }

  private static string ResolveExtension(string contentType)
      => contentType.ToLower(CultureInfo.InvariantCulture) switch
      {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        "image/heic" => ".heic",
        "image/heif" => ".heif",
        "application/pdf" => ".pdf",
        _ => ".bin"
      };
}

internal sealed record UploadedRequestFile(
    string FileName,
    string ContentType,
    long Length,
    Stream Content,
    bool DisposeContent) : IDisposable
{
  public void Dispose()
  {
    if (DisposeContent)
    {
      Content.Dispose();
    }
  }
}
