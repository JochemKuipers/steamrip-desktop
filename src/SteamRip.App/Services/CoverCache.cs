using System.Collections.Concurrent;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.Logging;
using SteamRip.Core.Catalog;

namespace SteamRip.App.Services;

/// <summary>
/// Game covers from Steam's public CDN keyed by resolved <c>steam_app_id</c> (no API key, ~25 KB each).
/// Lazy per card, 4 downloads in flight, disk cache under <c>&lt;config&gt;/covers/</c>, decoded once at thumbnail width
/// and kept in memory. A miss is retried a few times, then remembered for this process only so the next start tries again.
/// Site ACF app ids are validated against the game name (and corrected via Store search) before fetch.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "Process-lifetime singleton; the semaphore dies with the app."
)]
public sealed class CoverCache(
    HttpClient http,
    string dir,
    SteamAppIdResolver appIds,
    ILogger<CoverCache> log
)
{
    public const int DecodeWidth = 140;
    private const int MaxAttempts = 3;
    private static readonly string[] Variants = ["library_600x900.jpg", "header.jpg"];
    private static readonly TimeSpan[] Backoffs =
    [
        TimeSpan.FromMilliseconds(400),
        TimeSpan.FromSeconds(1),
    ];

    private readonly ConcurrentDictionary<long, Task<Bitmap?>> _mem = new();
    private readonly SemaphoreSlim _gate = new(4);

    public async Task<Bitmap?> GetAsync(long? steamAppId, string? gameName = null)
    {
        var id = await appIds.ResolveAsync(steamAppId, gameName);
        return id is > 0 ? await _mem.GetOrAdd(id.Value, LoadAsync) : null;
    }

    private async Task<Bitmap?> LoadAsync(long id)
    {
        var file = Path.Combine(dir, $"{id}.jpg");
        try
        {
            Directory.CreateDirectory(dir);
            // Old week-long markers from earlier builds; a miss is now process-lifetime only.
            TryDelete(Path.Combine(dir, $"{id}.missing"));
            if (!File.Exists(file) && !await FetchWithRetriesAsync(id, file))
                return null;
            return await Task.Run(() =>
            {
                using var s = File.OpenRead(file);
                return Bitmap.DecodeToWidth(s, DecodeWidth);
            });
        }
        catch (Exception e)
        {
            log.LogDebug("Cover {AppId}: {Error}", id, e.Message);
            _mem.TryRemove(id, out _); // let a later card retry
            TryDelete(file);
            return null;
        }
    }

    /// <summary>Up to <see cref="MaxAttempts"/> tries; returns false so this process does not ask again until the next start.</summary>
    private async Task<bool> FetchWithRetriesAsync(long id, string file)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                if (await FetchAsync(id, file))
                    return true;
            }
            catch (Exception e)
                when (e is HttpRequestException or IOException or TaskCanceledException)
            {
                log.LogDebug(
                    "Cover {AppId}: attempt {Attempt}/{Max} failed ({Error})",
                    id,
                    attempt,
                    MaxAttempts,
                    e.Message
                );
                if (attempt == MaxAttempts)
                    return false;
                await Task.Delay(Backoffs[attempt - 1]);
                continue;
            }
            // CDN answered but none of the variants exist: another try rarely helps, but a flaky edge can 404 then 200.
            if (attempt == MaxAttempts)
            {
                log.LogDebug(
                    "Cover {AppId}: none on Steam CDN after {Max} attempts",
                    id,
                    MaxAttempts
                );
                return false;
            }
            await Task.Delay(Backoffs[attempt - 1]);
        }
        return false;
    }

    private async Task<bool> FetchAsync(long id, string file)
    {
        await _gate.WaitAsync();
        try
        {
            foreach (var name in Variants)
            {
                using var r = await http.GetAsync(
                    $"https://cdn.cloudflare.steamstatic.com/steam/apps/{id}/{name}"
                );
                if (!r.IsSuccessStatusCode)
                    continue;
                var tmp = file + ".part";
                await using (var fs = File.Create(tmp))
                    await r.Content.CopyToAsync(fs);
                File.Move(tmp, file, overwrite: true);
                log.LogDebug(
                    "Cover {AppId}: cached {Variant} ({Bytes} bytes)",
                    id,
                    name,
                    new FileInfo(file).Length
                );
                return true;
            }
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // gone already, or locked; the next attempt overwrites a half-written .jpg
        }
    }
}
