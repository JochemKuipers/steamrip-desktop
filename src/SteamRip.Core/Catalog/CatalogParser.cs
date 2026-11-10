using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SteamRip.Core.Catalog;

/// <summary>Turns a WordPress REST <c>/wp/v2/posts</c> JSON array into <see cref="CatalogPost"/>s.</summary>
public static partial class CatalogParser
{
    [GeneratedRegex(@"<a\b[^>]*\bshortc-button\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex MirrorAnchor();

    [GeneratedRegex("href=\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex Href();

    [GeneratedRegex(@"Game Size:\s*</strong>\s*([0-9.,]+\s*[KMGT]B)", RegexOptions.IgnoreCase)]
    private static partial Regex GameSize();

    [GeneratedRegex(@"^\s*([0-9]+(?:[.,][0-9]+)?)\s*([KMGT])B\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SizeValue();

    /// <summary>"12.3 GB" → bytes (decimal units, like the site); null when it does not look like a size.</summary>
    public static long? ParseSize(string? sizeText)
    {
        if (sizeText is null || SizeValue().Match(sizeText) is not { Success: true } m)
            return null;
        var number = double.Parse(
            m.Groups[1].Value.Replace(',', '.'),
            System.Globalization.CultureInfo.InvariantCulture
        );
        var scale = char.ToUpperInvariant(m.Groups[2].Value[0]) switch
        {
            'K' => 1e3,
            'M' => 1e6,
            'G' => 1e9,
            _ => 1e12,
        };
        return (long)Math.Round(number * scale);
    }

    [GeneratedRegex(@"class=""post-item post-(\d+)\b")]
    private static partial Regex TopGamesItem();

    /// <summary>Post ids in rank order from one page of the site's <c>/top-games/</c> listing (its "most viewed" ranking, 24 per page).</summary>
    public static IReadOnlyList<long> ParseTopGames(string html) =>
        [.. TopGamesItem().Matches(html).Select(m => long.Parse(m.Groups[1].Value)).Distinct()];

    /// <summary><c>/wp/v2/categories</c> JSON array → categories.</summary>
    public static IReadOnlyList<Category> ParseCategories(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return
        [
            .. doc
                .RootElement.EnumerateArray()
                .Select(c => new Category(
                    c.GetProperty("id").GetInt64(),
                    WebUtility.HtmlDecode(c.GetProperty("name").GetString() ?? ""),
                    c.TryGetProperty("count", out var n) ? n.GetInt32() : 0
                )),
        ];
    }

    public static IReadOnlyList<CatalogPost> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return [.. doc.RootElement.EnumerateArray().Select(ParseOne).OfType<CatalogPost>()];
    }

    public static CatalogPost? ParseOne(JsonElement p)
    {
        if (!p.TryGetProperty("id", out var idEl))
            return null;
        var title = p.GetProperty("title").GetProperty("rendered").GetString() ?? "";
        var (name, titleVersion) = TitleParser.Parse(title);
        var content =
            p.TryGetProperty("content", out var c) && c.TryGetProperty("rendered", out var r)
                ? r.GetString() ?? ""
                : "";

        string? build = null;
        long? appId = null;
        if (p.TryGetProperty("acf", out var acf) && acf.ValueKind == JsonValueKind.Object)
        {
            if (
                acf.TryGetProperty("tagmetafield", out var tm)
                && tm.ValueKind == JsonValueKind.String
            )
                build = NullIfEmpty(tm.GetString());
            if (acf.TryGetProperty("steam_app_id", out var sa))
                appId = sa switch
                {
                    { ValueKind: JsonValueKind.Number } when sa.TryGetInt64(out var n) && n > 0 =>
                        n,
                    { ValueKind: JsonValueKind.String }
                        when long.TryParse(sa.GetString(), out var s) && s > 0 => s,
                    _ => appId,
                };
        }

        var mirrors = new List<Mirror>();
        foreach (Match a in MirrorAnchor().Matches(content))
        {
            var h = Href().Match(a.Value);
            if (!h.Success)
                continue;
            var url = HosterDetector.Normalize(WebUtility.HtmlDecode(h.Groups[1].Value));
            if (mirrors.Any(x => x.Url == url))
                continue;
            mirrors.Add(new Mirror(HosterDetector.Detect(url), url));
        }
        var size = GameSize().Match(content) is { Success: true } gs
            ? gs.Groups[1].Value.Trim()
            : null;
        IReadOnlyList<long> categories =
            p.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Array
                ?
                [
                    .. cats.EnumerateArray()
                        .Where(id => id.TryGetInt64(out _))
                        .Select(id => id.GetInt64()),
                ]
                : [];

        return new CatalogPost(
            idEl.GetInt64(),
            p.TryGetProperty("slug", out var slug) ? slug.GetString() ?? "" : "",
            p.GetProperty("link").GetString() ?? "",
            WebUtility.HtmlDecode(title),
            name,
            titleVersion,
            build,
            appId,
            ParseUtc(p, "modified_gmt", "modified"),
            ParseUtc(p, "date_gmt", "date"),
            size,
            mirrors,
            ParseSize(size),
            categories
        );
    }

    private static DateTime ParseUtc(JsonElement p, params string[] names)
    {
        foreach (var n in names)
            if (
                p.TryGetProperty(n, out var el)
                && el.ValueKind == JsonValueKind.String
                && DateTime.TryParse(
                    el.GetString(),
                    null,
                    System.Globalization.DateTimeStyles.AssumeUniversal
                        | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var dt
                )
            )
                return dt;
        return DateTime.MinValue;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
