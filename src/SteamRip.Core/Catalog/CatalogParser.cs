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
            mirrors
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
