using SteamRip.Core.Catalog;
using SteamRip.Core.Hosters;
using SteamRip.Core.Solver;

namespace SteamRip.Core.Updates;

/// <summary>User-triggered fallback: open a visible browser on a FileDitch mirror, let the user pass the check, parse the direct link.</summary>
public static class ManualSolve
{
    public static async Task<ResolvedDownload> ViaBrowserAsync(CatalogPost post, CancellationToken ct = default)
    {
        var mirror = post.Mirrors.FirstOrDefault(m => m.Host == Hoster.FileDitch)
                     ?? throw new NeedsAttentionException("This post has no FileDitch mirror; the browser fallback only supports FileDitch.");
        await BrowserSolver.EnsureInstalledAsync();
        var solved = await new BrowserSolver().SolveAsync(mirror.Url, "https://steamrip.com/", ct: ct);
        var (url, expires, fileName) = HostPages.ParseFileDitch(solved.Html);
        return new ResolvedDownload(Hoster.FileDitch, url, new Dictionary<string, string>(), fileName, ExpiresUtc: expires);
    }
}
