using SteamRip.Core.Catalog;

namespace SteamRip.Core.Hosters;

/// <summary>A direct, range-capable URL for the game archive plus whatever headers the host needs on the GET.</summary>
public sealed record ResolvedDownload(
    Hoster Host,
    string Url,
    IReadOnlyDictionary<string, string> Headers,
    string? FileName = null,
    long? Size = null,
    string? Md5 = null,
    string? Sha256 = null,
    DateTime? ExpiresUtc = null
);

public interface IHostResolver
{
    Hoster Host { get; }
    Task<ResolvedDownload> ResolveAsync(string shareUrl, CancellationToken ct = default);
}

public sealed class HostResolveException(Hoster host, string message, Exception? inner = null)
    : Exception($"{host}: {message}", inner)
{
    public Hoster Host => host;
}
