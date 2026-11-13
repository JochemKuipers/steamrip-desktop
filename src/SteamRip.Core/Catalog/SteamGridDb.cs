using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SteamRip.Core.Catalog;

/// <summary>
/// SteamGridDB API v2: 600×900 grids for library cards, square icons for .desktop shortcuts.
/// Auth is a Bearer key from the user's account (stored via <c>ISecretStore</c>, never in settings.json).
/// </summary>
public static class SteamGridDb
{
    public const string SecretKey = "steamgriddb-api-key";
    public const string BaseUrl = "https://www.steamgriddb.com/api/v2";

    /// <summary>Best grid image URL from a <c>/grids/…</c> JSON body, or null.</summary>
    public static string? BestGridUrl(string json) => BestUrl(json, preferPng: false);

    /// <summary>Best icon image URL from a <c>/icons/…</c> JSON body, or null (png preferred).</summary>
    public static string? BestIconUrl(string json) => BestUrl(json, preferPng: true);

    private static string? BestUrl(string json, bool preferPng)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False)
            return null;
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return null;

        string? best = null;
        var bestScore = int.MinValue;
        var bestIsPng = false;
        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("url", out var urlEl))
                continue;
            var url = urlEl.GetString();
            if (string.IsNullOrWhiteSpace(url))
                continue;
            var score = item.TryGetProperty("score", out var s) && s.TryGetInt32(out var n) ? n : 0;
            var isPng = url.Contains(".png", StringComparison.OrdinalIgnoreCase);
            if (
                best is null
                || score > bestScore
                || (preferPng && isPng && !bestIsPng && score >= bestScore)
            )
            {
                best = url;
                bestScore = score;
                bestIsPng = isPng;
            }
        }
        return best;
    }

    /// <summary>File extension for a CDN URL (<c>.png</c> / <c>.jpg</c> / <c>.webp</c>), else <paramref name="fallback"/>.</summary>
    public static string ExtensionFor(string url, string fallback)
    {
        try
        {
            var ext = Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant();
            return ext switch
            {
                ".png" or ".jpg" or ".webp" => ext,
                ".jpeg" => ".jpg",
                _ => fallback,
            };
        }
        catch
        {
            return fallback;
        }
    }
}

/// <summary>Downloads SteamGridDB grids/icons when an API key is available.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "Process-lifetime singleton; the semaphore dies with the app."
)]
public sealed class SteamGridDbClient(
    HttpClient http,
    Func<string?> apiKey,
    ILogger<SteamGridDbClient>? logger = null
)
{
    private readonly ILogger _log = logger ?? NullLogger<SteamGridDbClient>.Instance;
    private readonly SemaphoreSlim _gate = new(4);

    public async Task<bool> TrySaveGridAsync(
        long steamAppId,
        string destFile,
        CancellationToken ct = default
    )
    {
        var url = await ResolveAssetUrlAsync(
            $"grids/steam/{steamAppId}?dimensions=600x900&types=static&nsfw=false&humor=false&limit=10",
            SteamGridDb.BestGridUrl,
            steamAppId,
            "grid",
            ct
        );
        return url is not null && await DownloadAsync(url, destFile, ct);
    }

    public async Task<bool> TrySaveIconAsync(
        long steamAppId,
        string destFile,
        CancellationToken ct = default
    )
    {
        var url = await ResolveAssetUrlAsync(
            $"icons/steam/{steamAppId}?types=static&nsfw=false&humor=false&limit=10",
            SteamGridDb.BestIconUrl,
            steamAppId,
            "icon",
            ct
        );
        return url is not null && await DownloadAsync(url, destFile, ct);
    }

    /// <summary>
    /// Ensures <c>icons/&lt;id&gt;.*</c> exists (SGDB when keyed). Returns the path, or null.
    /// </summary>
    public async Task<string?> EnsureIconFileAsync(
        long steamAppId,
        string iconsDir,
        CancellationToken ct = default
    )
    {
        Directory.CreateDirectory(iconsDir);
        foreach (var ext in new[] { ".png", ".jpg", ".webp" })
        {
            var existing = Path.Combine(iconsDir, steamAppId + ext);
            if (File.Exists(existing))
                return existing;
        }
        if (string.IsNullOrWhiteSpace(apiKey()))
            return null;

        // Probe URL first so we pick the right extension.
        var url = await ResolveAssetUrlAsync(
            $"icons/steam/{steamAppId}?types=static&nsfw=false&humor=false&limit=10",
            SteamGridDb.BestIconUrl,
            steamAppId,
            "icon",
            ct
        );
        if (url is null)
            return null;
        var dest = Path.Combine(iconsDir, steamAppId + SteamGridDb.ExtensionFor(url, ".png"));
        return await DownloadAsync(url, dest, ct) ? dest : null;
    }

    private async Task<string?> ResolveAssetUrlAsync(
        string pathAndQuery,
        Func<string, string?> pick,
        long steamAppId,
        string kind,
        CancellationToken ct
    )
    {
        var key = apiKey()?.Trim();
        if (string.IsNullOrEmpty(key))
            return null;

        await _gate.WaitAsync(ct);
        try
        {
            using var req = new HttpRequestMessage(
                HttpMethod.Get,
                SteamGridDb.BaseUrl + "/" + pathAndQuery
            );
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            using var r = await http.SendAsync(req, ct);
            var body = await r.Content.ReadAsStringAsync(ct);
            if (!r.IsSuccessStatusCode)
            {
                _log.LogDebug(
                    "SteamGridDB {Kind} for {AppId}: HTTP {Status}",
                    kind,
                    steamAppId,
                    (int)r.StatusCode
                );
                return null;
            }
            var url = pick(body);
            if (url is null)
                _log.LogDebug("SteamGridDB {Kind} for {AppId}: no usable asset", kind, steamAppId);
            return url;
        }
        catch (Exception e)
            when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            _log.LogDebug(
                "SteamGridDB {Kind} for {AppId} failed: {Error}",
                kind,
                steamAppId,
                e.Message
            );
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> DownloadAsync(string url, string destFile, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var r = await http.GetAsync(url, ct);
            if (!r.IsSuccessStatusCode)
                return false;
            Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
            var tmp = destFile + ".part";
            await using (var fs = File.Create(tmp))
                await r.Content.CopyToAsync(fs, ct);
            File.Move(tmp, destFile, overwrite: true);
            _log.LogDebug(
                "SteamGridDB cached {File} ({Bytes} bytes)",
                destFile,
                new FileInfo(destFile).Length
            );
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException)
        {
            _log.LogDebug("SteamGridDB download failed: {Error}", e.Message);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }
}
