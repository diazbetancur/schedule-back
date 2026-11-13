using System.Text.RegularExpressions;
using Api.Barbershop.Middleware;

namespace Barbershop.Tests.Features.Localization;

/// <summary>
/// SONDA BUG-007 / BUG-034: the messages the API sends to the client are in Spanish. This reads the sources and fails
/// when an exception that reaches the client, or a field error, carries an English text, so they do not come back.
/// </summary>
public sealed partial class ClientMessagesLanguageTests
{
  private static readonly string[] ClientExceptions =
  [
    "ConflictException", "ValidationProblemException", "KeyNotFoundException", "NotFoundException",
    "UnauthorizedException", "UnauthorizedAccessException", "ServiceUnavailableException",
    "PayloadTooLargeException", "BadRequestException", "TooManyRequestsException",
  ];

  [Fact]
  public void Exceptions_And_FieldErrors_That_Reach_The_Client_Are_In_Spanish()
  {
    var sourceRoot = Path.Combine(FindRepositoryRoot(), "src");
    var offenders = new List<string>();

    foreach (var file in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
    {
      if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
          || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
          || file.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}")
          || file.Contains($"{Path.DirectorySeparatorChar}Barbershop.Domain{Path.DirectorySeparatorChar}"))
      {
        continue;
      }

      var text = File.ReadAllText(file);
      var relative = Path.GetRelativePath(sourceRoot, file);

      foreach (Match statement in ThrowStatementRegex().Matches(text))
      {
        if (!ClientExceptions.Contains(statement.Groups["type"].Value))
        {
          continue;
        }

        foreach (Match literal in LiteralRegex().Matches(statement.Groups["args"].Value))
        {
          if (LooksEnglish(literal.Groups[1].Value))
          {
            offenders.Add($"{relative}: {literal.Groups[1].Value}");
          }
        }
      }

      foreach (Match fieldError in FieldErrorRegex().Matches(text))
      {
        if (LooksEnglish(fieldError.Groups[1].Value))
        {
          offenders.Add($"{relative}: {fieldError.Groups[1].Value}");
        }
      }
    }

    Assert.True(offenders.Count == 0, "Mensajes al cliente en inglés:\n" + string.Join('\n', offenders));
  }

  [Theory]
  [InlineData(404, "No encontrado", "No encontramos lo que buscas.")]
  [InlineData(405, "Acción no permitida", "Esta acción no está permitida.")]
  [InlineData(500, "Error del servidor", ProblemDetailsExtensions.UnexpectedErrorMessage)]
  public void Framework_Statuses_Get_A_Spanish_Title_And_Detail(int status, string title, string detail)
  {
    Assert.Equal(title, ProblemDetailsExtensions.GetTitle(status));
    Assert.Equal(detail, ProblemDetailsExtensions.GetDefaultDetail(status));
  }

  [Fact]
  public void The_Check_Recognises_English_And_Leaves_Spanish_Alone()
  {
    Assert.True(LooksEnglish("The income entry was not found."));
    Assert.True(LooksEnglish("Only pending or confirmed appointments can be rescheduled."));
    Assert.False(LooksEnglish("No encontramos el ingreso. Es posible que otra persona lo haya eliminado."));
    Assert.False(LooksEnglish("Hubo un error al guardar"));
  }

  /// <summary>ASCII-only text with common English words (the same rule as the front's fallback).</summary>
  internal static bool LooksEnglish(string text)
      => !SpanishLettersRegex().IsMatch(text) && EnglishWordsRegex().IsMatch(text);

  private static string FindRepositoryRoot()
  {
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Barbershop.sln")))
    {
      directory = directory.Parent;
    }

    return directory?.FullName ?? throw new InvalidOperationException("Barbershop.sln was not found above the test folder.");
  }

  [GeneratedRegex(@"throw\s+new\s+(?<type>\w+)\((?<args>[^;]*?)\);", RegexOptions.Singleline)]
  private static partial Regex ThrowStatementRegex();

  // Every literal in order, so a match never starts at the closing quote of the previous one.
  [GeneratedRegex(@"\$?""((?:[^""\\\n]|\\.)*)""")]
  private static partial Regex LiteralRegex();

  // errors["field"] = ["message"] and ["field"] = ["message"] inside a dictionary.
  [GeneratedRegex(@"\]\s*=\s*\[\s*\$?""((?:[^""\\\n]|\\.)*\s(?:[^""\\\n]|\\.)*)""")]
  private static partial Regex FieldErrorRegex();

  [GeneratedRegex("[áéíóúñÁÉÍÓÚÑ¿¡]")]
  private static partial Regex SpanishLettersRegex();

  [GeneratedRegex(@"\b(the|is|are|was|were|must|cannot|can|not|be|already|required|invalid|only|found|please|unexpected|occurred|request|exists|allowed|should|failed|unable|forbidden|unauthorized|characters|between|or fewer|greater)\b", RegexOptions.IgnoreCase)]
  private static partial Regex EnglishWordsRegex();
}
