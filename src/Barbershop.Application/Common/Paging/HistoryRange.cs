using Barbershop.Application.Common.Exceptions;

namespace Barbershop.Application.Common.Paging;

/// <summary>An inclusive range of Bogotá calendar dates.</summary>
public sealed record DateRange(DateOnly From, DateOnly To)
{
    public int Days => To.DayNumber - From.DayNumber + 1;
}

/// <summary>
/// Date window of the paginated histories. Every query covers at most <see cref="MaxDays"/> calendar days (both ends
/// inclusive) but may start at any date, so old periods stay reachable one window at a time. The limit is enforced here,
/// on the server; the screens only mirror it.
/// <list type="bullet">
/// <item><see cref="Resolve"/> (histories): with no dates the window is the last <see cref="MaxDays"/> days ending today.</item>
/// <item><see cref="ResolveOptional"/> (queues and current items): with no dates there is no date filter, so old pending
/// items are never hidden; a given range is still limited.</item>
/// </list>
/// With only one end, the window extends <see cref="MaxDays"/> days from it (forwards from <c>from</c>, backwards from
/// <c>to</c>).
/// </summary>
public static class HistoryRange
{
    public const int MaxDays = 30;

    public static DateRange Resolve(
        DateOnly? from, DateOnly? to, DateOnly today, string fromField = "from", string toField = "to")
        => ResolveOptional(from, to, fromField, toField) ?? new DateRange(today.AddDays(-(MaxDays - 1)), today);

    public static DateRange? ResolveOptional(
        DateOnly? from, DateOnly? to, string fromField = "from", string toField = "to")
    {
        if (from is null && to is null)
        {
            return null;
        }

        var start = from ?? to!.Value.AddDays(-(MaxDays - 1));
        var end = to ?? from!.Value.AddDays(MaxDays - 1);
        ListFilters.ValidateRange(start, end, fromField, toField);

        var range = new DateRange(start, end);
        if (range.Days > MaxDays)
        {
            throw new ValidationProblemException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                [toField] = [$"El rango de fechas puede cubrir como máximo {MaxDays} días ({fromField} y {toField} incluidos)."]
            });
        }

        return range;
    }
}
