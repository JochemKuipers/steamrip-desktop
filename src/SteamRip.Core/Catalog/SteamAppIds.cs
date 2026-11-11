using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SteamRip.Core.Catalog;

/// <summary>
/// Steam app ids from the site's ACF are sometimes wrong (copy-paste). Validate against the Store
/// name and fall back to <c>storesearch</c> by game title. Results are cached under <c>dir</c>.
/// </summary>
public sealed partial class SteamAppIdResolver(
    HttpClient http,
    string dir,
    ILogger<SteamAppIdResolver>? logger = null
)
{
    private readonly ILogger _log = logger ?? NullLogger<SteamAppIdResolver>.Instance;

    /// <summary>
    /// Returns a Steam app id whose store name matches <paramref name="gameName"/>, preferring
    /// <paramref name="claimed"/> when it already matches; null when nothing fits.
    /// </summary>
    public async Task<long?> ResolveAsync(
        long? claimed,
        string? gameName,
        CancellationToken ct = default
    )
    {
        gameName = string.IsNullOrWhiteSpace(gameName) ? null : gameName.Trim();
        if (claimed is not > 0 && gameName is null)
            return null;

        Directory.CreateDirectory(dir);
        var cacheKey = claimed is > 0 ? $"id-{claimed.Value}" : $"name-{StableHash(gameName!)}";
        var cachePath = Path.Combine(dir, cacheKey + ".json");
        if (TryReadCache(cachePath, out var cached))
            return cached;

        long? resolved = null;
        try
        {
            if (claimed is > 0)
            {
                var steamName = await AppNameAsync(claimed.Value, ct);
                if (steamName is not null && (gameName is null || NamesMatch(gameName, steamName)))
                    resolved = claimed;
                else if (steamName is not null)
                    _log.LogDebug(
                        "Steam app {AppId} is '{SteamName}', not '{Game}' — searching",
                        claimed,
                        steamName,
                        gameName
                    );
            }

            if (resolved is null && gameName is not null)
                resolved = await SearchAsync(gameName, ct);
        }
        catch (Exception e)
            when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            _log.LogDebug("Steam app id resolve for '{Game}' failed: {Error}", gameName, e.Message);
            // don't cache failures — next start / next card can retry
            return claimed is > 0 ? claimed : null;
        }

        WriteCache(cachePath, resolved);
        if (resolved is > 0 && claimed is > 0 && resolved != claimed)
            _log.LogInformation(
                "Steam app id for '{Game}': site had {Claimed}, using {Resolved}",
                gameName,
                claimed,
                resolved
            );
        return resolved;
    }

    private async Task<string?> AppNameAsync(long appId, CancellationToken ct)
    {
        using var r = await http.GetAsync(
            $"https://store.steampowered.com/api/appdetails?appids={appId}&filters=basic",
            ct
        );
        if (!r.IsSuccessStatusCode)
            return null;
        await using var s = await r.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);
        if (
            !doc.RootElement.TryGetProperty(appId.ToString(), out var entry)
            || !entry.TryGetProperty("success", out var ok)
            || !ok.GetBoolean()
            || !entry.TryGetProperty("data", out var data)
            || !data.TryGetProperty("name", out var name)
        )
            return null;
        return name.GetString();
    }

    private async Task<long?> SearchAsync(string gameName, CancellationToken ct)
    {
        var url =
            "https://store.steampowered.com/api/storesearch/?l=english&cc=US&term="
            + Uri.EscapeDataString(gameName);
        using var r = await http.GetAsync(url, ct);
        if (!r.IsSuccessStatusCode)
            return null;
        var payload = await r.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        if (
            !payload.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array
        )
            return null;
        var hits = items
            .EnumerateArray()
            .Select(i =>
                (
                    Id: i.TryGetProperty("id", out var id) && id.TryGetInt64(out var n) ? n : 0L,
                    Name: i.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                    Type: i.TryGetProperty("type", out var t) ? t.GetString() ?? "" : ""
                )
            )
            .Where(h => h.Id > 0 && h.Type is "app" or "game" or "")
            .Select(h => (h.Id, h.Name));
        return PickSearchMatch(gameName, hits);
    }

    /// <summary>Significant token overlap; ignores trademark noise and short stopwords.</summary>
    public static bool NamesMatch(string gameName, string steamName)
    {
        var a = Tokens(gameName);
        var b = Tokens(steamName);
        if (a.Count == 0 || b.Count == 0)
            return false;
        var overlap = a.Intersect(b).Count();
        // need most of the shorter name, at least one strong token
        var need = Math.Max(1, (int)Math.Ceiling(Math.Min(a.Count, b.Count) * 0.6));
        return overlap >= need;
    }

    /// <summary>Best storesearch hit for <paramref name="gameName"/>, or null.</summary>
    public static long? PickSearchMatch(string gameName, IEnumerable<(long Id, string Name)> hits)
    {
        var want = Tokens(gameName);
        if (want.Count == 0)
            return null;
        (long Id, int Score)? best = null;
        foreach (var (id, name) in hits)
        {
            var got = Tokens(name);
            var overlap = want.Intersect(got).Count();
            if (overlap == 0)
                continue;
            var need = Math.Max(1, (int)Math.Ceiling(want.Count * 0.6));
            if (overlap < need)
                continue;
            // prefer exact-ish coverage and shorter Steam names (base game over DLC)
            var score = overlap * 100 - got.Count;
            if (best is null || score > best.Value.Score)
                best = (id, score);
        }
        return best?.Id;
    }

    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the",
        "and",
        "for",
        "of",
        "a",
        "an",
        "to",
        "in",
        "on",
        "pc",
        "free",
        "download",
        "edition",
        "deluxe",
        "ultimate",
        "complete",
        "goty",
        "game",
        "remastered",
        "remake",
        "definitive",
        "enhanced",
        "year",
    };

    [GeneratedRegex(@"[a-z0-9]+", RegexOptions.IgnoreCase)]
    private static partial Regex Word();

    public static HashSet<string> Tokens(string s) =>
        [
            .. Word()
                .Matches(s)
                .Select(m => m.Value.ToLowerInvariant())
                .Where(t => t.Length > 2 && !Stopwords.Contains(t)),
        ];

    private static string StableHash(string s) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s.ToLowerInvariant())))[
            ..16
        ];

    private static bool TryReadCache(string path, out long? id)
    {
        id = null;
        try
        {
            if (!File.Exists(path))
                return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("id", out var el))
                return false;
            if (el.ValueKind == JsonValueKind.Null)
                return true;
            if (el.TryGetInt64(out var n))
            {
                id = n > 0 ? n : null;
                return true;
            }
        }
        catch
        {
            // corrupt cache → resolve again
        }
        return false;
    }

    private static void WriteCache(string path, long? id)
    {
        try
        {
            File.WriteAllText(path, id is > 0 ? $"{{\"id\":{id}}}" : "{\"id\":null}");
        }
        catch
        {
            // cache is best-effort
        }
    }
}
