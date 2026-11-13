namespace Barbershop.Application.Common.Exceptions;

/// <summary>The request body is larger than the server accepts: answered with 413 without reading it.</summary>
public sealed class PayloadTooLargeException : Exception
{
  public PayloadTooLargeException(string message)
      : base(message)
  {
  }

  public PayloadTooLargeException(string message, Exception innerException)
      : base(message, innerException)
  {
  }
}
