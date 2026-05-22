namespace Barbershop.Application.Common.Exceptions;

public sealed class ValidationProblemException : Exception
{
  public ValidationProblemException(IReadOnlyDictionary<string, string[]> errors)
      : base("One or more validation errors occurred.")
  {
    Errors = errors;
  }

  public IReadOnlyDictionary<string, string[]> Errors { get; }
}