namespace Barbershop.Tests.Infrastructure;

/// <summary>
/// Helpers for the migration tests that compare every table before and after migrating to the latest version. Rows are
/// snapshots like <c>Column=value|Column=value</c> (columns sorted by name).
/// </summary>
internal static class MigrationSnapshot
{
  /// <summary>
  /// The tables later migrations create. ADR-0019 (notification preferences), ADR-0020 stage 1 (inventory) and stages 2
  /// and 3 (bills); payment_methods and bill_number_series start with their seed rows.
  /// </summary>
  public static readonly string[] LaterTables =
  [
    "user_notification_preferences",
    "product_stock",
    "inventory_movements",
    "bills",
    "bill_lines",
    "bill_events",
    "bill_approvals",
    "bill_postings",
    "payment_methods",
    "bill_number_series",
  ];

  /// <summary>
  /// True when the rows are the same, ignoring the columns a later additive migration added to the table, which must be
  /// NULL on every existing row (ADR-0020 adds nullable columns to income_entries and loyalty_redemptions). Any other
  /// difference, or a later column with a value, makes it false.
  /// </summary>
  public static bool SameRows(IReadOnlyList<string> before, IReadOnlyList<string> after)
  {
    if (before.Count != after.Count)
    {
      return false;
    }

    if (before.Count == 0)
    {
      return true;
    }

    var columns = Columns(before[0]).Select(part => part.Name).ToHashSet(StringComparer.Ordinal);
    var trimmed = new List<string>();
    foreach (var row in after)
    {
      var parts = Columns(row).ToList();
      if (parts.Any(part => !columns.Contains(part.Name) && part.Value != "null"))
      {
        return false;
      }

      trimmed.Add(string.Join("|", parts.Where(part => columns.Contains(part.Name)).Select(part => $"{part.Name}={part.Value}")));
    }

    trimmed.Sort(StringComparer.Ordinal);
    return before.SequenceEqual(trimmed);
  }

  private static IEnumerable<(string Name, string Value)> Columns(string row)
      => row.Split('|').Select(part =>
      {
        var index = part.IndexOf('=', StringComparison.Ordinal);
        return (part[..index], part[(index + 1)..]);
      });
}

/// <summary>Every row of every public table (except the migrations history), each row sorted by column name.</summary>
internal static class MigrationSnapshotReader
{
  public static async Task<Dictionary<string, List<string>>> ReadAsync(Barbershop.Infrastructure.Persistence.AppDbContext db)
  {
    var connection = (Npgsql.NpgsqlConnection)Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.GetDbConnection(db.Database);
    if (connection.State != System.Data.ConnectionState.Open)
    {
      await connection.OpenAsync();
    }

    var tables = new List<string>();
    await using (var list = new Npgsql.NpgsqlCommand(
        "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE' AND table_name <> '__EFMigrationsHistory' ORDER BY table_name",
        connection))
    await using (var reader = await list.ExecuteReaderAsync())
    {
      while (await reader.ReadAsync())
      {
        tables.Add(reader.GetString(0));
      }
    }

    var snapshot = new Dictionary<string, List<string>>();
    foreach (var table in tables)
    {
      await using var command = new Npgsql.NpgsqlCommand($"SELECT * FROM public.\"{table}\"", connection);
      await using var reader = await command.ExecuteReaderAsync();
      var rows = new List<string>();
      while (await reader.ReadAsync())
      {
        rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount)
            .OrderBy(index => reader.GetName(index), StringComparer.Ordinal)
            .Select(index => $"{reader.GetName(index)}={(reader.IsDBNull(index) ? "null" : Convert.ToString(reader.GetValue(index), System.Globalization.CultureInfo.InvariantCulture))}")));
      }

      rows.Sort(StringComparer.Ordinal);
      snapshot[table] = rows;
    }

    return snapshot;
  }
}
