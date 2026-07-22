using Barbershop.Application.Common.Exceptions;

namespace Api.Barbershop.Features.Uploads;

internal static class MultipartFileReader
{
  public static async Task<IFormFile> ReadSingleFileAsync(
      HttpRequest request,
      string fieldName,
      long maxBytes,
      ILogger logger,
      CancellationToken cancellationToken)
  {
    if (!request.HasFormContentType)
    {
      logger.LogWarning(
          "Upload request for {Path} was not multipart form-data. ContentType={ContentType}; ContentLength={ContentLength}; ClientFileSize={ClientFileSize}; ClientFileType={ClientFileType}.",
          request.Path,
          request.ContentType,
          request.ContentLength,
          request.Headers["X-Upload-File-Size"].ToString(),
          request.Headers["X-Upload-File-Type"].ToString());

      throw new ValidationProblemException(new Dictionary<string, string[]>
      {
        [fieldName] = ["Multipart form-data content is required."]
      });
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
          "Upload request for {Path} did not include a non-empty file in field {ExpectedFieldName}. ContentType={ContentType}; ContentLength={ContentLength}; ClientFileSize={ClientFileSize}; ClientFileType={ClientFileType}; FormFields={FormFields}; FileFields={FileFields}.",
          request.Path,
          fieldName,
          request.ContentType,
          request.ContentLength,
          request.Headers["X-Upload-File-Size"].ToString(),
          request.Headers["X-Upload-File-Type"].ToString(),
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

    return file;
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
}
