using System.Net.Http.Json;
using Barbershop.Application.Common.Paging;

namespace Barbershop.Tests.Infrastructure;

/// <summary>Reads the items of a paginated list response (ADR-0016 B7) for tests that only check its rows.</summary>
public static class PagedResponseExtensions
{
    public static async Task<List<T>> GetPageItemsAsync<T>(this HttpClient client, string requestUri)
    {
        var page = await client.GetFromJsonAsync<PagedResult<T>>(requestUri);
        Assert.NotNull(page);
        return page!.Items.ToList();
    }

    public static async Task<List<T>> ReadPageItemsAsync<T>(this HttpContent content)
    {
        var page = await content.ReadFromJsonAsync<PagedResult<T>>();
        Assert.NotNull(page);
        return page!.Items.ToList();
    }
}
