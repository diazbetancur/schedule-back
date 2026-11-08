using Barbershop.Application.Common.Exceptions;

namespace Barbershop.Application.Common.Paging;

/// <summary>Shared validation of the filters of the paginated lists; every failure is a 422 like other validation errors.</summary>
public static class ListFilters
{
    /// <summary>Both ends are optional and inclusive; <c>from</c> after <c>to</c> is rejected.</summary>
    public static void ValidateRange(DateOnly? from, DateOnly? to, string fromField = "from", string toField = "to")
    {
        if (from is { } start && to is { } end && start > end)
        {
            throw new ValidationProblemException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [fromField] = [$"La fecha inicial no puede ser posterior a la final ({toField})."]
            });
        }
    }

    /// <summary>
    /// Parses an optional enum filter by name (any casing, never numbers). Empty means "no filter"; an unknown name is
    /// a 422 that lists the accepted values.
    /// </summary>
    public static TEnum? ParseEnum<TEnum>(string? value, string field)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var name = Enum.GetNames<TEnum>().FirstOrDefault(candidate =>
            string.Equals(candidate, value.Trim(), StringComparison.OrdinalIgnoreCase));
        if (name is null)
        {
            throw new ValidationProblemException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [field] = [$"Valor no válido. Valores aceptados: {string.Join(", ", Enum.GetNames<TEnum>())}."]
            });
        }

        return Enum.Parse<TEnum>(name);
    }

    /// <summary>
    /// Parses a comma-separated list of enum names (e.g. "Pending,Confirmed"). Empty means "no filter" (null); any
    /// unknown name is a 422.
    /// </summary>
    public static IReadOnlyCollection<TEnum>? ParseEnumList<TEnum>(string? value, string field)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(name => ParseEnum<TEnum>(name, field)!.Value)
            .Distinct()
            .ToArray();
    }

    /// <summary>Parses one of a fixed set of names (any casing). Empty means "no filter".</summary>
    public static string? ParseOption(string? value, string field, params string[] options)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = options.FirstOrDefault(option => string.Equals(option, value.Trim(), StringComparison.OrdinalIgnoreCase));
        return match ?? throw new ValidationProblemException(new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [field] = [$"Valor no válido. Valores aceptados: {string.Join(", ", options)}."]
        });
    }

    /// <summary>Trimmed text filter, or null when empty.</summary>
    public static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Active/inactive filter of the catalog lists (rates and products).</summary>
public static class CatalogStatusFilter
{
    public const string Active = "Active";
    public const string Inactive = "Inactive";

    /// <summary>null = both; true = only active; false = only inactive.</summary>
    public static bool? Parse(string? value)
        => ListFilters.ParseOption(value, "status", Active, Inactive) switch
        {
            Active => true,
            Inactive => false,
            _ => null,
        };
}
