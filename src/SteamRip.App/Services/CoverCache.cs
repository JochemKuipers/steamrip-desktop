using System.Collections.Concurrent;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.Logging;

namespace SteamRip.App.Services;

/// <summary>
/// Game covers from Steam's public CDN keyed by <c>steam_app_id</c> (no API key, ~25 KB each).
/// Lazy per card, 4 downloads in flight, disk cache under <c>&lt;config&gt;/covers/</c>, decoded once at thumbnail width
/// and kept in memory, 404s remembered for a week so missing covers are not re-asked on every search.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "Process-lifetime singleton; the semaphore dies with the app."
)]
public sealed class CoverCache(HttpClient http, string dir, ILogger<CoverCache> log)
{
    public const int DecodeWidth = 140;
    private static readonly string[] Variants = ["library_600x900.jpg", "header.jpg"];

    private readonly ConcurrentDictionary<long, Task<Bitmap?>> _mem = new();
    private readonly SemaphoreSlim _gate = new(4);

    public Task<Bitmap?> GetAsync(long? steamAppId) =>
        steamAppId is > 0
            ? _mem.GetOrAdd(steamAppId.Value, LoadAsync)
            : Task.FromResult<Bitmap?>(null);

    private async Task<Bitmap?> LoadAsync(long id)
    {
        var file = Path.Combine(dir, $"{id}.jpg");
        var missing = Path.Combine(dir, $"{id}.missing");
        try
        {
            Directory.CreateDirectory(dir);
            if (!File.Exists(file))
            {
                if (
                    File.Exists(missing)
                    && File.GetLastWriteTimeUtc(missing) > DateTime.UtcNow.AddDays(-7)
                )
                    return null;
                if (!await FetchAsync(id, file))
                {
                    File.WriteAllText(missing, "");
                    return null;
                }
            }
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
            try
            {
                File.Delete(file);
            }
            catch { }
            return null;
        }
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
            log.LogDebug("Cover {AppId}: none on Steam CDN", id);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }
}
