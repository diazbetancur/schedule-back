namespace Barbershop.Application.Common.Paging;

/// <summary>Query string of a paginated list without other filters (<c>?page=&amp;pageSize=</c>).</summary>
public sealed record PageQuery(int? Page = null, int? PageSize = null)
{
    public PageRequest ToRequest() => PageRequest.Create(Page, PageSize);
}
