using System.Net;
using System.Text.RegularExpressions;

namespace SteamRip.Core.Hosters;

/// <summary>Pure parsers for hoster landing pages. Kept separate from the network code so they can be fixture-tested.</summary>
public static partial class HostPages
{
    [GeneratedRegex("<a\\b[^>]*\\bbtn-main\\b[^>]*>")]
    private static partial Regex FileDitchAnchor();
    [GeneratedRegex("href=\"(https://[^\"]+)\"")]
    private static partial Regex Href();
    [GeneratedRegex("<title>([^<]*)</title>")]
    private static partial Regex Title();

    /// <summary>FileDitch: the primary download button is a signed CDN URL whose base64 token starts with the unix expiry.</summary>
    public static (string Url, DateTime? ExpiresUtc, string? FileName) ParseFileDitch(string html)
    {
        var anchor = FileDitchAnchor().Match(html);
        var m = anchor.Success ? Href().Match(anchor.Value) : Match.Empty;
        if (!m.Success)
        {
            if (html.Contains("Verifying your browser", StringComparison.Ordinal))
                throw new FormatException("FileDitch: page is still the browser-verification interstitial (needs a real browser solve)");
            throw new FormatException("FileDitch: no btn-main download link in page");
        }
        var url = WebUtility.HtmlDecode(m.Groups[1].Value);
        DateTime? expires = null;
        try
        {
            var token = url.Split('/').Last();
            var pad = token.Length % 4 == 0 ? "" : new string('=', 4 - token.Length % 4);
            var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(token + pad));
            if (long.TryParse(decoded.Split(':')[0], out var unix)) expires = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
        }
        catch { /* token layout changed; expiry stays unknown */ }
        var title = Title().Match(html) is { Success: true } t ? WebUtility.HtmlDecode(t.Groups[1].Value).Trim() : null;
        return (url, expires, string.IsNullOrEmpty(title) ? null : title);
    }

    [GeneratedRegex("hx-get=\"(/[^\"]+/download\\?t=[^\"&]+)\"")]
    private static partial Regex BzzhrHx();
    [GeneratedRegex("MD5:\\s*(?:<[^>]*>\\s*)*([a-fA-F0-9]{32})")]
    private static partial Regex Md5();
    [GeneratedRegex("SHA-256:\\s*(?:<[^>]*>\\s*)*([a-fA-F0-9]{64})")]
    private static partial Regex Sha256();

    public static (string HxPath, string? Md5, string? Sha256, string? FileName) ParseBzzhr(string html)
    {
        var m = BzzhrHx().Match(html);
        if (!m.Success) throw new FormatException("BuzzHeavier: no hx-get download path in page");
        var title = Title().Match(html) is { Success: true } t ? WebUtility.HtmlDecode(t.Groups[1].Value).Trim() : null;
        return (WebUtility.HtmlDecode(m.Groups[1].Value), Md5().Match(html) is { Success: true } a ? a.Groups[1].Value.ToLowerInvariant() : null,
            Sha256().Match(html) is { Success: true } b ? b.Groups[1].Value.ToLowerInvariant() : null, title);
    }

    [GeneratedRegex("var finalDownloadUrl\\s*=\\s*'([^']+)'")]
    private static partial Regex MegaDbFinal();
    [GeneratedRegex("var seconds\\s*=\\s*(\\d+)")]
    private static partial Regex MegaDbSeconds();
    [GeneratedRegex("MD5:\\s*(?:<[^>]*>\\s*)*([a-fA-F0-9]{32,64})")]
    private static partial Regex MegaDbHash();

    public static (string PtUrl, int WaitSeconds, string? Hash, string? FileName) ParseMegaDb(string html)
    {
        var m = MegaDbFinal().Match(html);
        if (!m.Success) throw new FormatException("MegaDB: no finalDownloadUrl in page");
        var secs = MegaDbSeconds().Match(html) is { Success: true } s ? int.Parse(s.Groups[1].Value) : 10;
        var title = Title().Match(html) is { Success: true } t ? WebUtility.HtmlDecode(t.Groups[1].Value).Replace(" - MegaDB", "").Trim() : null;
        return (WebUtility.HtmlDecode(m.Groups[1].Value), secs, MegaDbHash().Match(html) is { Success: true } h ? h.Groups[1].Value.ToLowerInvariant() : null, title);
    }
}
