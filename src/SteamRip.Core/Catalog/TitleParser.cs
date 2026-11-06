using System.Net;
using System.Text.RegularExpressions;

namespace SteamRip.Core.Catalog;

/// <summary>Splits "Game Name Free Download (v1.2.3 + Co-op)" into name and version. Theme-specific; fixture-tested.</summary>
public static partial class TitleParser
{
    [GeneratedRegex(
        @"^(?<name>.+?)\s+Free\s+Downl?oad(?:\s+[^()]*?)?\s*(?:\((?<ver>[^)]*)\))?\s*$",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex TitleRegex();

    public static (string Name, string? Version) Parse(string rawTitle)
    {
        var title = WebUtility.HtmlDecode(rawTitle).Trim();
        var m = TitleRegex().Match(title);
        if (!m.Success)
        {
            // Unparseable title (e.g. bracketed dual versions). Keep the whole thing as the name; no version.
            return (title, null);
        }
        var ver = m.Groups["ver"].Success ? m.Groups["ver"].Value.Trim() : null;
        return (m.Groups["name"].Value.Trim(), string.IsNullOrEmpty(ver) ? null : ver);
    }
}
