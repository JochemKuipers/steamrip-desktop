using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SteamRip.Core.Catalog;

namespace SteamRip.Core.Hosters;

/// <summary>GoFile public API with a guest account token; the download itself needs <c>Cookie: accountToken=…</c>.</summary>
public sealed partial class GoFileResolver(HttpClient http) : IHostResolver
{
    public const string UserAgent = "Mozilla/5.0";
    private const string Salt = "12af056dacea0b"; // ponytail: site constant as of the spike; if the API starts answering "error-wt", re-derive from gofile's JS

    public Hoster Host => Hoster.GoFile;

    [GeneratedRegex(@"gofile\.io/d/([A-Za-z0-9]+)")]
    private static partial Regex ContentId();

    public static string WebsiteToken(string accountToken, DateTimeOffset now)
    {
        var slot = now.ToUnixTimeSeconds() / 14400;
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{UserAgent}::en-US::{accountToken}::{slot}::{Salt}")));
    }

    public async Task<ResolvedDownload> ResolveAsync(string shareUrl, CancellationToken ct = default)
    {
        var idm = ContentId().Match(shareUrl);
        if (!idm.Success) throw new HostResolveException(Host, "not a gofile.io/d/ link: " + shareUrl);
        var contentId = idm.Groups[1].Value;

        var acct = await SendAsync(HttpMethod.Post, "https://api.gofile.io/accounts", null, ct);
        var token = acct.GetProperty("data").GetProperty("token").GetString() ?? throw new HostResolveException(Host, "no guest token");

        var contents = await SendAsync(HttpMethod.Get, $"https://api.gofile.io/contents/{contentId}?cache=true&sortField=createTime&sortDirection=1", token, ct);
        var data = contents.GetProperty("data");
        if (!data.TryGetProperty("children", out var children))
            throw new HostResolveException(Host, "folder has no children (password-protected or removed?)");

        JsonElement? best = null;
        foreach (var child in children.EnumerateObject())
        {
            var c = child.Value;
            if (c.GetProperty("type").GetString() != "file") continue;
            if (best is null || c.GetProperty("size").GetInt64() > best.Value.GetProperty("size").GetInt64()) best = c;
        }
        if (best is null) throw new HostResolveException(Host, "no files in folder");
        var f = best.Value;
        return new ResolvedDownload(Host, f.GetProperty("link").GetString()!,
            new Dictionary<string, string> { ["Cookie"] = "accountToken=" + token, ["User-Agent"] = UserAgent },
            f.GetProperty("name").GetString(), f.GetProperty("size").GetInt64(),
            Md5: f.TryGetProperty("md5", out var md5) ? md5.GetString() : null);
    }

    private async Task<JsonElement> SendAsync(HttpMethod method, string url, string? token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.TryAddWithoutValidation("Origin", "https://gofile.io");
        req.Headers.TryAddWithoutValidation("Referer", "https://gofile.io/");
        req.Headers.TryAddWithoutValidation("X-Website-Token", WebsiteToken(token ?? "", DateTimeOffset.UtcNow));
        req.Headers.TryAddWithoutValidation("X-BL", "en-US");
        if (token is not null)
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.TryAddWithoutValidation("Cookie", "accountToken=" + token);
        }
        if (method == HttpMethod.Post) req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var resp = await http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement.Clone();
        if (root.TryGetProperty("status", out var st) && st.GetString() != "ok")
            throw new HostResolveException(Host, $"API {url} → {st.GetString()}");
        return root;
    }
}
