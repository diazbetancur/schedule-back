using Barbershop.Application.Common.Exceptions;

namespace Barbershop.Application.Common.Paging;

/// <summary>A validated page request: <c>Page</c> ≥ 1 and <c>PageSize</c> between 1 and <see cref="MaxPageSize"/>.</summary>
public sealed record PageRequest
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;

    /// <summary>
    /// SONDA BUG-141/158: page=2147483647 overflowed (page - 1) × pageSize into a 500. Far beyond any real list, and
    /// (MaxPage - 1) × MaxPageSize still fits in an int.
    /// </summary>
    public const int MaxPage = 1_000_000;

    private PageRequest(int page, int pageSize)
    {
        Page = page;
        PageSize = pageSize;
    }

    public int Page { get; }

    public int PageSize { get; }

    public int Skip => (Page - 1) * PageSize;

    public static PageRequest Default { get; } = new(1, DefaultPageSize);

    /// <summary>Missing values take the defaults (page 1, <see cref="DefaultPageSize"/>); out-of-range values are a 422.</summary>
    public static PageRequest Create(int? page, int? pageSize)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (page is < 1 or > MaxPage)
        {
            errors["page"] = ["La página debe estar entre 1 y 1.000.000."];
        }

        if (pageSize is < 1 or > MaxPageSize)
        {
            errors["pageSize"] = [$"El tamaño de página debe estar entre 1 y {MaxPageSize}."];
        }

        if (errors.Count > 0)
        {
            throw new ValidationProblemException(errors);
        }

        return new PageRequest(page ?? 1, pageSize ?? DefaultPageSize);
    }
}
