using SteamRip.Core.Catalog;
using SteamRip.Core.Solver;

namespace SteamRip.Core.Hosters;

/// <summary>FileDitch runs its own WASM browser check; FlareSolverr passes it and returns the real page, whose main button is a signed CDN URL (~12 h) that needs no cookies.</summary>
public sealed class FileDitchResolver(FlareSolverrClient solver) : IHostResolver
{
    public Hoster Host => Hoster.FileDitch;

    public async Task<ResolvedDownload> ResolveAsync(string shareUrl, CancellationToken ct = default)
    {
        SolveResult page;
        try { page = await solver.GetAsync(shareUrl, disableMedia: true, ct: ct); }
        catch (SolverException e) { throw new HostResolveException(Host, e.Message, e); }
        try
        {
            var (url, expires, fileName) = HostPages.ParseFileDitch(page.Html);
            return new ResolvedDownload(Host, url, new Dictionary<string, string>(), fileName, ExpiresUtc: expires);
        }
        catch (FormatException e) { throw new HostResolveException(Host, e.Message, e); }
    }
}
