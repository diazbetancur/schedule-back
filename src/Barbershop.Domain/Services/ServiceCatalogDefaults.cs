namespace Barbershop.Domain.Services;

/// <summary>
/// The category and the general service created by the AddServiceCatalog migration. Every rate that existed before
/// the catalog was split is attached to them provisionally (ADR-0016 D4); administration reassigns them afterwards.
/// Nothing is inferred from names.
/// </summary>
public static class ServiceCatalogDefaults
{
  public static readonly Guid BarberiaCategoryId = new("5f0c2a8e-3b1d-4c7a-9e51-0b6d2f4a8c01");
  public const string BarberiaCategoryName = "Barbería";
  public const string BarberiaCategoryCode = "barberia";

  public static readonly Guid CorteGeneralServiceId = new("8a4e1c27-6d93-4b0f-a2c5-7e19d3b6f402");
  public const string CorteGeneralServiceName = "Corte";
  public const string CorteGeneralServiceCode = "corte";

  /// <summary>Creation time of both seeded rows (the day the catalog was split).</summary>
  public static readonly DateTime SeededAt = new(2026, 11, 6, 0, 0, 0, DateTimeKind.Utc);
}
