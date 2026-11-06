using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SteamRip.Core.Catalog;
using SteamRip.Core.Solver;

namespace SteamRip.Core.Hosters;

/// <summary>FileDitch runs its own WASM browser check; FlareSolverr passes it and returns the real page, whose main button is a signed CDN URL that needs no cookies.</summary>
public sealed class FileDitchResolver(FlareSolverrClient solver, ILogger<FileDitchResolver>? logger = null) : IHostResolver
{
    private readonly ILogger _log = logger ?? NullLogger<FileDitchResolver>.Instance;

    public Hoster Host => Hoster.FileDitch;

    public async Task<ResolvedDownload> ResolveAsync(string shareUrl, CancellationToken ct = default)
    {
        SolveResult page;
        try { page = await solver.GetAsync(shareUrl, disableMedia: true, ct: ct); }
        catch (SolverException e) { throw new HostResolveException(Host, e.Message, e); }
        try
        {
            var (url, expires, fileName) = HostPages.ParseFileDitch(page.Html);
            _log.LogInformation("FileDitch resolved {File}: {Url} (expires {Expires:u}, in {Minutes} min)", fileName, Redact(url), expires, expires is { } x ? (int)(x - DateTime.UtcNow).TotalMinutes : -1);
            return new ResolvedDownload(Host, url, new Dictionary<string, string>(), fileName, ExpiresUtc: expires);
        }
        catch (FormatException e)
        {
            _log.LogWarning("FileDitch page for {ShareUrl} could not be parsed: {Error} (final URL {FinalUrl}, {Bytes} bytes)", shareUrl, e.Message, page.FinalUrl, page.Html.Length);
            throw new HostResolveException(Host, e.Message, e);
        }
    }

    /// <summary>Signed URLs are bearer tokens; keep the host but trim the token in logs.</summary>
    internal static string Redact(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? $"{u.Scheme}://{u.Host}/…{u.AbsolutePath[^Math.Min(8, u.AbsolutePath.Length)..]}" : "<url>";
}
