using System.ComponentModel.DataAnnotations;

namespace Barbershop.Infrastructure.Configuration;

public sealed class SeedAdminOptions
{
  public const string SectionName = "SeedAdmin";

  public bool Enabled { get; init; }

  [EmailAddress]
  public string Email { get; init; } = string.Empty;

  public string Password { get; init; } = string.Empty;

  public string FullName { get; init; } = string.Empty;
}