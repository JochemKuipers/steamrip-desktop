namespace SteamRip.Core.Catalog;

/// <summary>Fetches posts from the steamrip.com WordPress REST API, newest-modified first.</summary>
public sealed class CatalogClient(HttpClient http, string baseUrl = "https://steamrip.com")
{
    private const string Fields = "id,slug,link,title,modified_gmt,date_gmt,acf,content";

    public async Task<IReadOnlyList<CatalogPost>> GetPageAsync(int page, int perPage = 100, CancellationToken ct = default)
    {
        var url = $"{baseUrl}/wp-json/wp/v2/posts?per_page={perPage}&page={page}&orderby=modified&order=desc&_fields={Fields}";
        for (var attempt = 1; ; attempt++)
        {
            using var resp = await http.GetAsync(url, ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.BadRequest) return []; // past the last page
            if (resp.StatusCode is System.Net.HttpStatusCode.TooManyRequests or System.Net.HttpStatusCode.ServiceUnavailable or System.Net.HttpStatusCode.BadGateway && attempt <= 6)
            {
                // The site rate-limits bursts of REST calls (seen at ~40 pages). Honour Retry-After, else back off geometrically.
                var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Min(120, 10 * Math.Pow(2, attempt - 1)));
                await Task.Delay(wait, ct);
                continue;
            }
            resp.EnsureSuccessStatusCode();
            return CatalogParser.Parse(await resp.Content.ReadAsStringAsync(ct));
        }
    }

    /// <summary>Yields posts modified after <paramref name="since"/> (exclusive), stopping at the first older page. One request per 100 posts.</summary>
    public async IAsyncEnumerable<CatalogPost> GetModifiedSinceAsync(DateTime? since, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        for (var page = 1; ; page++)
        {
            var posts = await GetPageAsync(page, 100, ct);
            if (posts.Count == 0) yield break;
            foreach (var p in posts)
            {
                if (since is { } s && p.ModifiedUtc <= s) yield break;
                yield return p;
            }
            if (posts.Count < 100) yield break;
            await Task.Delay(1500, ct); // politeness delay between pages; 429s are handled in GetPageAsync
        }
    }
}
