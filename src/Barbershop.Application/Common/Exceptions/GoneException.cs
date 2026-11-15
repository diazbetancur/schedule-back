namespace Barbershop.Application.Common.Exceptions;

/// <summary>410: the operation was retired for good (the message says where it moved).</summary>
public sealed class GoneException : Exception
{
  public GoneException(string message)
      : base(message)
  {
  }
}
