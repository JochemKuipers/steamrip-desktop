using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SteamRip.Core.Catalog;
using SteamRip.Core.Solver;

namespace SteamRip.Core.Hosters;

/// <summary>
/// FileDitch gates on Referer == steamrip.com (direct FlareSolverr get bounces there) and a WASM check.
/// After landing, the Download button has no <c>href</c> until JS fetches it (<c>X-FD-DL</c>) on pointerenter;
/// we hop like BuzzHeavier, arm the button, then read the signed CDN URL (no cookies needed to download).
/// </summary>
public sealed class FileDitchResolver(
    FlareSolverrClient solver,
    ILogger<FileDitchResolver>? logger = null
) : IHostResolver
{
    private readonly ILogger _log = logger ?? NullLogger<FileDitchResolver>.Instance;

    public Hoster Host => Hoster.FileDitch;

    public async Task<ResolvedDownload> ResolveAsync(
        string shareUrl,
        CancellationToken ct = default
    )
    {
        try
        {
            await using var s = await solver.CreateSessionAsync(ct);
            await s.GetAsync("https://steamrip.com/", disableMedia: true, ct: ct);
            _log.LogDebug("FileDitch: hopping from steamrip.com to {ShareUrl}", shareUrl);
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
                        selector = "//a[contains(@class,'btn-main')]",
                        timeout = 60000,
                    },
                    new
                    {
                        type = "eval",
                        script = "document.querySelector('a.btn-main').dispatchEvent(new PointerEvent('pointerenter',{bubbles:true}));",
                        returnResult = false,
                    },
                    new { type = "wait", seconds = 4 },
                    new
                    {
                        type = "eval",
                        script = "const a=document.querySelector('a.btn-main');return JSON.stringify({href:a&&a.getAttribute('href'),title:document.title,url:location.href,html:document.documentElement.outerHTML})",
                        returnResult = true,
                    },
                ],
                ct
            );
            if (info is null)
                throw new HostResolveException(Host, "no eval result from browser");
            using var doc = JsonDocument.Parse(info);
            var landedAt = doc.RootElement.GetProperty("url").GetString();
            var href = doc.RootElement.TryGetProperty("href", out var h) ? h.GetString() : null;
            var title = doc.RootElement.TryGetProperty("title", out var t) ? t.GetString() : null;
            var html = doc.RootElement.TryGetProperty("html", out var ht)
                ? ht.GetString() ?? ""
                : "";

            string url;
            DateTime? expires;
            string? fileName;
            if (!string.IsNullOrEmpty(href))
            {
                url = href;
                expires = HostPages.FileDitchExpiry(url);
                fileName = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
            }
            else
            {
                // Older FileDitch pages still embed href on btn-main in the HTML.
                (url, expires, fileName) = HostPages.ParseFileDitch(html);
            }

            _log.LogInformation(
                "FileDitch resolved {File}: {Url} (expires {Expires:u}, in {Minutes} min, landed {Landed})",
                fileName,
                Redact(url),
                expires,
                expires is { } x ? (int)(x - DateTime.UtcNow).TotalMinutes : -1,
                landedAt
            );
            return new ResolvedDownload(
                Host,
                url,
                new Dictionary<string, string>(),
                fileName,
                ExpiresUtc: expires
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

    /// <summary>Signed URLs are bearer tokens; keep the host but trim the token in logs.</summary>
    internal static string Redact(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
            ? $"{u.Scheme}://{u.Host}/…{u.AbsolutePath[^Math.Min(8, u.AbsolutePath.Length)..]}"
            : "<url>";
}
