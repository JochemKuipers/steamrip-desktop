using SteamRip.Core.Catalog;
using SteamRip.Core.Data;

namespace SteamRip.Core.Updates;

public static class UpdateDetector
{
    /// <summary>
    /// A post is an update for an install when its version key (acf build, else title version) differs from what is installed.
    /// A changed <c>modified</c> alone is not enough: the site touches posts for tag edits too.
    /// </summary>
    public static bool IsUpdate(InstalledGame installed, CatalogPost post)
    {
        var current = post.VersionKey;
        if (current is null) return false; // can't tell; don't churn
        return !string.Equals(Normalize(current), Normalize(installed.InstalledVersion), StringComparison.OrdinalIgnoreCase);
    }

    public static string? Normalize(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim().Replace("Build ", "", StringComparison.OrdinalIgnoreCase).Trim();
}
