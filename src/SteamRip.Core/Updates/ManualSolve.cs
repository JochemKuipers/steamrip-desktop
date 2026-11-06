using Microsoft.Extensions.Logging;
using SteamRip.Core.Catalog;
using SteamRip.Core.Hosters;
using SteamRip.Core.Solver;

namespace SteamRip.Core.Updates;

/// <summary>User-triggered fallback: open a visible browser on a FileDitch mirror, let the user pass the check, parse the direct link.</summary>
public static class ManualSolve
{
    public static async Task<ResolvedDownload> ViaBrowserAsync(
        CatalogPost post,
        ILoggerFactory? loggers = null,
        CancellationToken ct = default
    )
    {
        var log = loggers?.CreateLogger("ManualSolve");
        var mirror =
            post.Mirrors.FirstOrDefault(m => m.Host == Hoster.FileDitch)
            ?? throw new NeedsAttentionException(
                "This post has no FileDitch mirror; the browser fallback only supports FileDitch."
            );
        log?.LogInformation("Manual browser solve for {Name} via {Url}", post.Name, mirror.Url);
        await BrowserSolver.EnsureInstalledAsync(log);
        var solved = await new BrowserSolver(loggers?.CreateLogger<BrowserSolver>()).SolveAsync(
            mirror.Url,
            "https://steamrip.com/",
            ct: ct
        );
        var (url, expires, fileName) = HostPages.ParseFileDitch(solved.Html);
        log?.LogInformation(
            "Manual solve produced {File} (expires {Expires:u})",
            fileName,
            expires
        );
        return new ResolvedDownload(
            Hoster.FileDitch,
            url,
            new Dictionary<string, string>(),
            fileName,
            ExpiresUtc: expires
        );
    }
}
