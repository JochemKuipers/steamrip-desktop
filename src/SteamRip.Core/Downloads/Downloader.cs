using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using SteamRip.Core.Hosters;

namespace SteamRip.Core.Downloads;

public sealed record DownloadProgress(long Bytes, long? Total, double BytesPerSecond)
{
    public double? Fraction => Total is > 0 ? (double)Bytes / Total : null;
}

/// <summary>Single-connection ranged downloader that resumes a <c>.part</c> file and verifies MD5/SHA-256 when the host published one.</summary>
public sealed class Downloader(HttpClient http)
{
    public const string DefaultUserAgent = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/154.0.0.0 Safari/537.36";

    public async Task<string> DownloadAsync(ResolvedDownload src, string destinationPath, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var part = destinationPath + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        long have = File.Exists(part) ? new FileInfo(part).Length : 0;

        using var req = new HttpRequestMessage(HttpMethod.Get, src.Url);
        foreach (var (k, v) in src.Headers) req.Headers.TryAddWithoutValidation(k, v);
        if (!src.Headers.ContainsKey("User-Agent")) req.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
        if (have > 0) req.Headers.Range = new RangeHeaderValue(have, null);

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.StatusCode is HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // Server says we already have everything (or the file changed). Verify what we have below.
            have = new FileInfo(part).Length;
        }
        else
        {
            if (resp.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                throw new DownloadException($"HTTP {(int)resp.StatusCode} from host (link expired or missing headers)", retryResolve: true);
            resp.EnsureSuccessStatusCode();
            var ct2 = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (ct2.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
                throw new DownloadException("Host returned an HTML page instead of the file", retryResolve: true);

            var resumed = resp.StatusCode == HttpStatusCode.PartialContent && have > 0;
            if (!resumed) have = 0;
            long? total = resp.Content.Headers.ContentRange?.Length ?? (resp.Content.Headers.ContentLength is { } len ? len + (resumed ? have : 0) : null);

            await using var fs = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            await using var body = await resp.Content.ReadAsStreamAsync(ct);
            var buf = new byte[1 << 17];
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long sinceTick = 0; var lastTick = sw.Elapsed;
            int n;
            while ((n = await body.ReadAsync(buf, ct)) > 0)
            {
                await fs.WriteAsync(buf.AsMemory(0, n), ct);
                have += n; sinceTick += n;
                var now = sw.Elapsed;
                if ((now - lastTick).TotalMilliseconds >= 500)
                {
                    progress?.Report(new DownloadProgress(have, total, sinceTick / (now - lastTick).TotalSeconds));
                    sinceTick = 0; lastTick = now;
                }
            }
            progress?.Report(new DownloadProgress(have, total, 0));
            if (total is { } t && have != t) throw new DownloadException($"Connection closed early ({have}/{t} bytes)", retryResolve: false);
        }

        await VerifyAsync(part, src, ct);
        File.Move(part, destinationPath, overwrite: true);
        return destinationPath;
    }

    private static async Task VerifyAsync(string path, ResolvedDownload src, CancellationToken ct)
    {
        if (src.Sha256 is null && src.Md5 is null) return;
        await using var fs = File.OpenRead(path);
        string actual, expected;
        if (src.Sha256 is not null) { actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(fs, ct)); expected = src.Sha256; }
        else { actual = Convert.ToHexStringLower(await MD5.HashDataAsync(fs, ct)); expected = src.Md5!; }
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            fs.Close();
            File.Delete(path);
            throw new DownloadException($"Checksum mismatch (expected {expected}, got {actual}); deleted partial file", retryResolve: true);
        }
    }
}

public sealed class DownloadException(string message, bool retryResolve) : Exception(message)
{
    /// <summary>True when the sensible retry is to resolve a fresh link (expired/403/HTML) rather than resume the same URL.</summary>
    public bool RetryResolve => retryResolve;
}
