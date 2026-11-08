using Barbershop.Application.Common.Paging;
using Barbershop.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Barbershop.Infrastructure.Persistence;

internal static class PagingExtensions
{
    /// <summary>
    /// Counts every matching row and reads one page. <paramref name="ordered"/> must already be sorted by a total order
    /// (it must end with a unique key such as the id), otherwise rows with equal sort values could move between pages.
    /// </summary>
    public static async Task<PagedResult<TOut>> ToPagedResultAsync<TIn, TOut>(
        this IQueryable<TIn> ordered,
        PageRequest page,
        Func<IReadOnlyList<TIn>, CancellationToken, Task<IReadOnlyList<TOut>>> map,
        CancellationToken cancellationToken)
    {
        var (rows, total) = await ReadPageAsync(ordered, page, cancellationToken);
        var items = rows.Count == 0 ? [] : await map(rows, cancellationToken);
        return new PagedResult<TOut>(items, page.Page, page.PageSize, total);
    }

    public static async Task<PagedResult<TOut>> ToPagedResultAsync<TIn, TOut>(
        this IQueryable<TIn> ordered,
        PageRequest page,
        Func<TIn, TOut> map,
        CancellationToken cancellationToken)
    {
        var (rows, total) = await ReadPageAsync(ordered, page, cancellationToken);
        return new PagedResult<TOut>(rows.Select(map).ToArray(), page.Page, page.PageSize, total);
    }

    public static async Task<(IReadOnlyList<T> Rows, int Total)> ReadPageAsync<T>(
        this IQueryable<T> ordered, PageRequest page, CancellationToken cancellationToken)
    {
        var total = await ordered.CountAsync(cancellationToken);
        // A page past the end is simply empty (it is not an error: rows can disappear between two requests).
        if (total == 0 || page.Skip >= total)
        {
            return ([], total);
        }

        var rows = await ordered.Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
        return (rows, total);
    }

    /// <summary>First UTC instant of a Bogotá calendar day (inclusive lower bound of a date filter).</summary>
    public static DateTime StartOfBogotaDayUtc(DateOnly date) => BogotaClock.ToUtc(date, TimeOnly.MinValue);

    /// <summary>First UTC instant after a Bogotá calendar day (exclusive upper bound of a date filter).</summary>
    public static DateTime EndOfBogotaDayUtc(DateOnly date) => BogotaClock.ToUtc(date.AddDays(1), TimeOnly.MinValue);
}
