namespace Barbershop.Application.Storage;

public interface IFileStorageService
{
    Task<StoredFileResult> UploadAsync(FileStorageObject file, CancellationToken cancellationToken = default);
    Task DeleteAsync(string objectKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Derives the public URL for a previously stored object from the current base URL configuration.
    /// Returns null if the provider is not configured with a public base URL.
    /// </summary>
    string? GetPublicUrl(string storageKey);
}
