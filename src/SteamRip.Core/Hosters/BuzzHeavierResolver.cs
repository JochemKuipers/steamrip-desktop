using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SteamRip.Core.Catalog;
using SteamRip.Core.Solver;

namespace SteamRip.Core.Hosters;

/// <summary>
/// BuzzHeavier gates on Referer == https://steamrip.com/ and Cloudflare. Flow (proven in spike): FlareSolverr session loads steamrip.com,
/// hops to the share URL via location.href so the referer is genuine, then an in-page HTMX-style fetch of the download endpoint yields
/// an <c>hx-redirect</c> to a direct URL that needs no cookies.
/// </summary>
public sealed class BuzzHeavierResolver(
    FlareSolverrClient solver,
    ILogger<BuzzHeavierResolver>? logger = null
) : IHostResolver
{
    private readonly ILogger _log = logger ?? NullLogger<BuzzHeavierResolver>.Instance;

    public Hoster Host => Hoster.BuzzHeavier;

    public async Task<ResolvedDownload> ResolveAsync(
        string shareUrl,
        CancellationToken ct = default
    )
    {
        try
        {
            await using var s = await solver.CreateSessionAsync(ct);
            await s.GetAsync("https://steamrip.com/", disableMedia: true, ct: ct);
            _log.LogDebug("BuzzHeavier: hopping from steamrip.com to {ShareUrl}", shareUrl);
            var info = await s.ActionAsync(
                [
                    new
                    {
                        type = "eval",
                        script = $"window.location.href = {JsonSerializer.Serialize(shareUrl)};",
                        returnResult = false,
                    },
                    new
                    {
                        type = "wait_for",
                        selector = "//a[contains(@class,'download-btn')]",
                        timeout = 30000,
                    },
                    new
                    {
                        type = "eval",
                        script = "return JSON.stringify({url: location.href, html: document.documentElement.outerHTML})",
                        returnResult = true,
                    },
                ],
                ct
            );
            if (info is null)
                throw new HostResolveException(Host, "no eval result from browser");
            using var doc = JsonDocument.Parse(info);
            var landedAt = doc.RootElement.GetProperty("url").GetString();
            var html = doc.RootElement.GetProperty("html").GetString() ?? "";
            var (hxPath, md5, sha256, fileName) = HostPages.ParseBzzhr(html);
            _log.LogDebug(
                "BuzzHeavier landed at {Url}; hx-get {HxPath}; md5 {Md5}; sha256 {Sha}",
                landedAt,
                hxPath,
                md5,
                sha256
            );

            var js =
                "window.__sr=null; fetch("
                + JsonSerializer.Serialize(hxPath)
                + ", {headers:{'HX-Request':'true','HX-Current-URL':location.href}, redirect:'manual'})"
                + ".then(r=>{window.__sr={status:r.status, redirect:r.headers.get('hx-redirect')}}).catch(e=>{window.__sr={error:String(e)}});";
            var res = await s.ActionAsync(
                [
                    new
                    {
                        type = "eval",
                        script = js,
                        returnResult = false,
                    },
                    new { type = "wait", seconds = 2 },
                    new
                    {
                        type = "eval",
                        script = "return JSON.stringify(window.__sr)",
                        returnResult = true,
                    },
                ],
                ct
            );
            if (res is null or "null")
                throw new HostResolveException(Host, "download endpoint did not answer");
            _log.LogDebug("BuzzHeavier in-page fetch result: {Result}", res);
            using var rd = JsonDocument.Parse(res);
            if (rd.RootElement.TryGetProperty("error", out var err))
                throw new HostResolveException(Host, "in-page fetch failed: " + err.GetString());
            var redirect = rd.RootElement.TryGetProperty("redirect", out var r)
                ? r.GetString()
                : null;
            if (string.IsNullOrEmpty(redirect))
                throw new HostResolveException(
                    Host,
                    "no hx-redirect in response (status "
                        + rd.RootElement.GetProperty("status").GetRawText()
                        + ")"
                );

            _log.LogInformation(
                "BuzzHeavier resolved {File}: {Url}",
                fileName,
                FileDitchResolver.Redact(redirect)
            );
            return new ResolvedDownload(
                Host,
                redirect,
                new Dictionary<string, string>(),
                fileName,
                Md5: md5,
                Sha256: sha256
            );
        }
        catch (SolverException e)
        {
            throw new HostResolveException(Host, e.Message, e);
        }
        catch (FormatException e)
        {
            throw new HostResolveException(Host, e.Message, e);
        }
    }
}
