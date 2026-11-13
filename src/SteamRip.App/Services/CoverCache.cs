using System.Collections.Concurrent;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.Logging;
using SteamRip.Core.Catalog;

namespace SteamRip.App.Services;

/// <summary>
/// Library/browse covers: SteamGridDB 600×900 grids when an API key is set, else Steam CDN
/// <c>library_600x900.jpg</c>. Disk cache under <c>&lt;config&gt;/covers/</c>, decoded at 140 px.
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
    SteamGridDbClient? steamGridDb,
    ILogger<CoverCache> log
)
{
    public const int DecodeWidth = 140;
    private const int MaxAttempts = 3;
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
            _mem.TryRemove(id, out _);
            TryDelete(file);
            return null;
        }
    }

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
            if (attempt == MaxAttempts)
            {
                log.LogDebug("Cover {AppId}: no SteamGridDB grid and no Steam portrait", id);
                return false;
            }
            await Task.Delay(Backoffs[attempt - 1]);
        }
        return false;
    }

    private async Task<bool> FetchAsync(long id, string file)
    {
        if (steamGridDb is not null && await steamGridDb.TrySaveGridAsync(id, file))
            return true;

        await _gate.WaitAsync();
        try
        {
            using var r = await http.GetAsync(
                $"https://cdn.cloudflare.steamstatic.com/steam/apps/{id}/library_600x900.jpg"
            );
            if (!r.IsSuccessStatusCode)
                return false;
            var tmp = file + ".part";
            await using (var fs = File.Create(tmp))
                await r.Content.CopyToAsync(fs);
            File.Move(tmp, file, overwrite: true);
            log.LogDebug(
                "Cover {AppId}: Steam CDN library_600x900 ({Bytes} bytes)",
                id,
                new FileInfo(file).Length
            );
            return true;
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
            // gone already, or locked
        }
    }
}
