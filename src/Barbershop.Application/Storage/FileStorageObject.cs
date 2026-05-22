namespace Barbershop.Application.Storage;

public sealed record FileStorageObject(
    string ObjectKey,
    Stream Content,
    string ContentType,
    long? ContentLength = null);
