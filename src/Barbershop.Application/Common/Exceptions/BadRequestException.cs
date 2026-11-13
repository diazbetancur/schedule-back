namespace Barbershop.Application.Common.Exceptions;

/// <summary>The request itself cannot be read (for example a malformed multipart body): answered with 400.</summary>
public sealed class BadRequestException : Exception
{
  public BadRequestException(string message)
      : base(message)
  {
  }

  public BadRequestException(string message, Exception innerException)
      : base(message, innerException)
  {
  }
}
