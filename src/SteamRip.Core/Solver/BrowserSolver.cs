using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;

namespace SteamRip.Core.Solver;

/// <summary>
/// Fallback solver: opens a visible Chromium so the user can pass an interactive challenge, then hands cookies + user agent back.
/// Never invoked automatically; the queue parks the item in "needs you" and the user clicks to start this.
/// </summary>
public sealed class BrowserSolver(ILogger<BrowserSolver>? logger = null)
{
    private readonly ILogger _log = logger ?? NullLogger<BrowserSolver>.Instance;

    public static Task EnsureInstalledAsync(ILogger? log = null)
    {
        log?.LogInformation("Checking Playwright Chromium (playwright install chromium)…");
        var code = Program.Main(["install", "chromium"]);
        if (code != 0)
        {
            log?.LogError("playwright install chromium exited with {Code}", code);
            throw new SolverException($"playwright install chromium exited with {code}");
        }
        return Task.CompletedTask;
    }

    public async Task<SolveResult> SolveAsync(
        string url,
        string? referer = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default
    )
    {
        _log.LogInformation(
            "Opening visible browser for {Url} (referer {Referer})",
            url,
            referer ?? "none"
        );
        using var pw = await Playwright.CreateAsync();
        await using var browser = await pw.Chromium.LaunchAsync(new() { Headless = false });
        var context = await browser.NewContextAsync(
            referer is null
                ? new()
                : new()
                {
                    ExtraHTTPHeaders = new Dictionary<string, string> { ["Referer"] = referer },
                }
        );
        var page = await context.NewPageAsync();
        await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded });

        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromMinutes(3));
        var stable = 0;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (page.IsClosed)
            {
                _log.LogWarning("Browser window closed before the challenge was solved");
                throw new SolverException(
                    "Browser window was closed before the challenge was solved."
                );
            }
            var title = await page.TitleAsync();
            var challenged =
                title.Contains("Just a moment", StringComparison.OrdinalIgnoreCase)
                || title.Contains("Attention Required", StringComparison.OrdinalIgnoreCase)
                || title.Contains("Verifying", StringComparison.OrdinalIgnoreCase);
            stable = challenged ? 0 : stable + 1;
            if (stable >= 6)
                break; // ~3 s on the real page
            await Task.Delay(500, ct);
        }
        if (stable < 6)
        {
            _log.LogWarning("Timed out waiting for the user to solve the challenge at {Url}", url);
            throw new SolverException("Timed out waiting for the challenge to be solved.");
        }

        var cookies = (await context.CookiesAsync())
            .Where(c =>
                page.Url.Contains(c.Domain.TrimStart('.'), StringComparison.OrdinalIgnoreCase)
            )
            .ToDictionary(c => c.Name, c => c.Value);
        var ua = await page.EvaluateAsync<string>("navigator.userAgent");
        var html = await page.ContentAsync();
        _log.LogInformation(
            "Browser solve finished at {FinalUrl} ({Bytes} bytes, cookies [{Cookies}])",
            page.Url,
            html.Length,
            string.Join(", ", cookies.Keys)
        );
        return new SolveResult(html, ua, cookies, page.Url, 200, "Solved in browser");
    }
}
