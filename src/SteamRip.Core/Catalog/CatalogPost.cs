namespace SteamRip.Core.Catalog;

/// <summary>One steamrip.com post (one game). Posts are updated in place, so <see cref="Id"/> is the stable key.</summary>
public sealed record CatalogPost(
    long Id,
    string Slug,
    string Url,
    string Title,
    string Name,
    string? TitleVersion,
    string? Build,
    long? SteamAppId,
    DateTime ModifiedUtc,
    DateTime PublishedUtc,
    string? SizeText,
    IReadOnlyList<Mirror> Mirrors
)
{
    /// <summary>The value that decides whether an installed copy is outdated: Steam build when the site has one, else the title's version string.</summary>
    public string? VersionKey => Build ?? TitleVersion;
}

public sealed record Mirror(Hoster Host, string Url);

public enum Hoster
{
    Unknown,
    BuzzHeavier,
    FileDitch,
    GoFile,
    MegaDb,
    Fichier,
    FileCrypt,
}
