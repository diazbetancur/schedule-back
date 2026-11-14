namespace Barbershop.Application.Common.Exceptions;

public sealed class ValidationProblemException : Exception
{
  public ValidationProblemException(IReadOnlyDictionary<string, string[]> errors)
      : base("Hay datos que no son válidos. Revisa los campos marcados.")
  {
    Errors = errors;
  }

  public IReadOnlyDictionary<string, string[]> Errors { get; }
}