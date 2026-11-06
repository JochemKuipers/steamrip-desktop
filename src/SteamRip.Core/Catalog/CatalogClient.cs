using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SteamRip.Core.Catalog;

/// <summary>Fetches posts from the steamrip.com WordPress REST API, newest-modified first.</summary>
public sealed class CatalogClient(
    HttpClient http,
    string baseUrl = "https://steamrip.com",
    ILogger<CatalogClient>? logger = null
)
{
    private const string Fields = "id,slug,link,title,modified_gmt,date_gmt,acf,content";
    private readonly ILogger _log = logger ?? NullLogger<CatalogClient>.Instance;

    public async Task<IReadOnlyList<CatalogPost>> GetPageAsync(
        int page,
        int perPage = 100,
        CancellationToken ct = default
    )
    {
        var url =
            $"{baseUrl}/wp-json/wp/v2/posts?per_page={perPage}&page={page}&orderby=modified&order=desc&_fields={Fields}";
        for (var attempt = 1; ; attempt++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var resp = await http.GetAsync(url, ct);
            switch (resp.StatusCode)
            {
                case System.Net.HttpStatusCode.BadRequest:
                    _log.LogDebug("REST page {Page}: 400 → past the last page", page);
                    return [];
                case System.Net.HttpStatusCode.TooManyRequests
                or System.Net.HttpStatusCode.ServiceUnavailable
                or System.Net.HttpStatusCode.BadGateway when attempt <= 6:
                {
                    // The site rate-limits bursts of REST calls (seen at ~40 pages). Honour Retry-After, else back off geometrically.
                    var wait =
                        resp.Headers.RetryAfter?.Delta
                        ?? TimeSpan.FromSeconds(Math.Min(120, 10 * Math.Pow(2, attempt - 1)));
                    _log.LogWarning(
                        "REST page {Page}: HTTP {Status}, backing off {Wait}s (attempt {Attempt}/6)",
                        page,
                        (int)resp.StatusCode,
                        (int)wait.TotalSeconds,
                        attempt
                    );
                    await Task.Delay(wait, ct);
                    continue;
                }
            }
            resp.EnsureSuccessStatusCode();
            var posts = CatalogParser.Parse(await resp.Content.ReadAsStringAsync(ct));
            _log.LogDebug(
                "REST page {Page}: {Count} posts in {Ms} ms",
                page,
                posts.Count,
                sw.ElapsedMilliseconds
            );
            return posts;
        }
    }

    /// <summary>Yields posts modified after <paramref name="since"/> (exclusive), stopping at the first older page. One request per 100 posts.</summary>
    public async IAsyncEnumerable<CatalogPost> GetModifiedSinceAsync(
        DateTime? since,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default
    )
    {
        _log.LogInformation(
            since is null ? "Catalog sync: full" : "Catalog sync: posts modified after {Since:u}",
            since
        );
        for (var page = 1; ; page++)
        {
            var posts = await GetPageAsync(page, 100, ct);
            if (posts.Count == 0)
                yield break;
            foreach (var p in posts)
            {
                if (since is { } s && p.ModifiedUtc <= s)
                    yield break;
                yield return p;
            }
            if (posts.Count < 100)
                yield break;
            await Task.Delay(1500, ct); // politeness delay between pages; 429s are handled in GetPageAsync
        }
    }
}
