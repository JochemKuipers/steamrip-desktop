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
    IReadOnlyList<Mirror> Mirrors,
    long? SizeBytes = null,
    IReadOnlyList<long>? CategoryIds = null
)
{
    /// <summary>WordPress category ids; the site uses categories as genres. Empty until the post was fetched with <c>categories</c> in the field list.</summary>
    public IReadOnlyList<long> CategoryIds { get; init; } = CategoryIds ?? [];

    /// <summary>The value that decides whether an installed copy is outdated: Steam build when the site has one, else the title's version string.</summary>
    public string? VersionKey => Build ?? TitleVersion;
}

public sealed record Mirror(Hoster Host, string Url);

/// <summary>A WordPress category (= genre on steamrip.com) with the number of posts in it.</summary>
public sealed record Category(long Id, string Name, int Count);

/// <summary>Order of a Browse listing; applied in SQL so the row window follows the key.</summary>
public enum PostSort
{
    Updated,
    Added,
    Name,
    SizeDesc,
    SizeAsc,
}

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
