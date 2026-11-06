using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SteamRip.Core.Catalog;

namespace SteamRip.Core.Hosters;

/// <summary>MegaDB: plain HTTP with Referer steamrip.com, a server-enforced wait, then a cookie-bound 302 to the storage node. The file GET needs Referer megadb.net + the cookie jar.</summary>
public sealed class MegaDbResolver : IHostResolver
{
    private readonly Func<Task>? _delayOverride;
    private readonly ILogger _log;

    public MegaDbResolver(Func<Task>? delayOverride = null, ILogger<MegaDbResolver>? logger = null)
    {
        _delayOverride = delayOverride;
        _log = logger ?? NullLogger<MegaDbResolver>.Instance;
    }

    public Hoster Host => Hoster.MegaDb;

    public async Task<ResolvedDownload> ResolveAsync(
        string shareUrl,
        CancellationToken ct = default
    )
    {
        var jar = new CookieContainer();
        using var handler = new HttpClientHandler
        {
            CookieContainer = jar,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
        };
        using var http = new HttpClient(handler);
        http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            Downloads.Downloader.DefaultUserAgent
        );

        using var pageReq = new HttpRequestMessage(HttpMethod.Get, shareUrl);
        pageReq.Headers.Referrer = new Uri("https://steamrip.com/");
        using var pageResp = await http.SendAsync(pageReq, ct);
        var html = await pageResp.Content.ReadAsStringAsync(ct);
        _log.LogDebug(
            "MegaDB landing {Url} → HTTP {Status}, {Bytes} bytes, cookies [{Cookies}]",
            shareUrl,
            (int)pageResp.StatusCode,
            html.Length,
            jar.GetCookieHeader(new Uri(shareUrl))
        );
        if (!pageResp.IsSuccessStatusCode)
            throw new HostResolveException(Host, $"landing page HTTP {(int)pageResp.StatusCode}");
        (string ptUrl, int waitSeconds, string? hash, string? fileName) parsed;
        try
        {
            parsed = HostPages.ParseMegaDb(html);
        }
        catch (FormatException e)
        {
            _log.LogWarning(
                "MegaDB page for {Url} could not be parsed: {Error}",
                shareUrl,
                e.Message
            );
            throw new HostResolveException(Host, e.Message, e);
        }

        _log.LogDebug(
            "MegaDB: waiting {Seconds}s (server-enforced) before requesting the token URL",
            parsed.waitSeconds + 2
        );
        if (_delayOverride is not null)
            await _delayOverride();
        else
            await Task.Delay(TimeSpan.FromSeconds(parsed.waitSeconds + 2), ct);

        using var ptReq = new HttpRequestMessage(HttpMethod.Get, parsed.ptUrl);
        ptReq.Headers.Referrer = new Uri(shareUrl);
        using var ptResp = await http.SendAsync(ptReq, ct);
        _log.LogDebug(
            "MegaDB token URL → HTTP {Status}, Location {Location}",
            (int)ptResp.StatusCode,
            ptResp.Headers.Location is { } l ? FileDitchResolver.Redact(l.ToString()) : "none"
        );
        if ((int)ptResp.StatusCode is < 300 or > 399 || ptResp.Headers.Location is null)
            throw new HostResolveException(
                Host,
                $"expected redirect to storage node, got HTTP {(int)ptResp.StatusCode}"
            );
        var fileUrl = ptResp.Headers.Location.IsAbsoluteUri
            ? ptResp.Headers.Location.ToString()
            : new Uri(new Uri(shareUrl), ptResp.Headers.Location).ToString();

        var cookieHeader = jar.GetCookieHeader(new Uri(fileUrl));
        if (string.IsNullOrEmpty(cookieHeader))
            cookieHeader = jar.GetCookieHeader(new Uri("https://megadb.net/"));
        var headers = new Dictionary<string, string> { ["Referer"] = "https://megadb.net/" };
        if (!string.IsNullOrEmpty(cookieHeader))
            headers["Cookie"] = cookieHeader;

        // The hash MegaDB prints on the page does not match the served file (verified: valid RAR, neither MD5 nor SHA-256 matched),
        // so it is deliberately not passed on; the installer's `unrar t` is the integrity check for this host.
        var name =
            parsed.fileName ?? Uri.UnescapeDataString(fileUrl.Split('?')[0].Split('/').Last());
        _log.LogInformation(
            "MegaDB resolved {File}: {Url}",
            name,
            FileDitchResolver.Redact(fileUrl)
        );
        return new ResolvedDownload(Host, fileUrl, headers, name);
    }
}
