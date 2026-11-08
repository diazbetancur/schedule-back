namespace Barbershop.Application.Common.Paging;

/// <summary>
/// One page of a server-side paginated list (ADR-0016 T12). <c>Page</c> starts at 1 and <c>TotalCount</c> counts every
/// row that matches the filters, so the client can compute the number of pages. Lists that return it are always
/// sorted by a total order (usually date descending and then id), so rows with equal dates never repeat or disappear
/// between pages.
/// </summary>
public record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);
