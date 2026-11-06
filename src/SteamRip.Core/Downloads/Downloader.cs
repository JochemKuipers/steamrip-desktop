using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SteamRip.Core.Hosters;

namespace SteamRip.Core.Downloads;

public sealed record DownloadProgress(long Bytes, long? Total, double BytesPerSecond)
{
    public double? Fraction => Total is > 0 ? (double)Bytes / Total : null;
}

/// <summary>Single-connection ranged downloader that resumes a <c>.part</c> file and verifies MD5/SHA-256 when the host published one.</summary>
public sealed class Downloader(HttpClient http, ILogger<Downloader>? logger = null)
{
    public const string DefaultUserAgent =
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/154.0.0.0 Safari/537.36";
    private readonly ILogger _log = logger ?? NullLogger<Downloader>.Instance;

    public async Task<string> DownloadAsync(
        ResolvedDownload src,
        string destinationPath,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct
    )
    {
        var part = destinationPath + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        long have = File.Exists(part) ? new FileInfo(part).Length : 0;

        using var req = new HttpRequestMessage(HttpMethod.Get, src.Url);
        foreach (var (k, v) in src.Headers)
            req.Headers.TryAddWithoutValidation(k, v);
        if (!src.Headers.ContainsKey("User-Agent"))
            req.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
        if (have > 0)
            req.Headers.Range = new RangeHeaderValue(have, null);
        _log.LogInformation(
            "GET {Host} {Url} → {Dest}{Resume} (headers: {Headers})",
            src.Host,
            FileDitchResolver.Redact(src.Url),
            destinationPath,
            have > 0 ? $", resuming at {have}" : "",
            string.Join(", ", src.Headers.Keys)
        );

        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        _log.LogDebug(
            "Response: HTTP {Status}, Content-Type {Type}, Content-Length {Length}, Content-Range {Range}, Accept-Ranges {Accept}",
            (int)resp.StatusCode,
            resp.Content.Headers.ContentType?.MediaType,
            resp.Content.Headers.ContentLength,
            resp.Content.Headers.ContentRange,
            string.Join(",", resp.Headers.AcceptRanges)
        );

        if (resp.StatusCode is HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // Server says we already have everything (or the file changed). Verify what we have below.
            _log.LogInformation(
                "416 for range {Have}-: assuming the .part file is complete, verifying",
                have
            );
            have = new FileInfo(part).Length;
        }
        else
        {
            if (
                resp.StatusCode
                is HttpStatusCode.Redirect
                    or HttpStatusCode.Found
                    or HttpStatusCode.Forbidden
                    or HttpStatusCode.Unauthorized
            )
            {
                _log.LogWarning(
                    "HTTP {Status} from {Host} (Location {Location}) → link expired or headers rejected; asking for re-resolve",
                    (int)resp.StatusCode,
                    src.Host,
                    resp.Headers.Location
                );
                throw new DownloadException(
                    $"HTTP {(int)resp.StatusCode} from host (link expired or missing headers)",
                    retryResolve: true
                );
            }
            resp.EnsureSuccessStatusCode();
            var ct2 = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (ct2.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
            {
                _log.LogWarning(
                    "{Host} returned text/html instead of the file; asking for re-resolve",
                    src.Host
                );
                throw new DownloadException(
                    "Host returned an HTML page instead of the file",
                    retryResolve: true
                );
            }

            var resumed = resp.StatusCode == HttpStatusCode.PartialContent && have > 0;
            if (have > 0 && !resumed)
                _log.LogWarning(
                    "Server ignored the Range header (HTTP {Status}); restarting from 0",
                    (int)resp.StatusCode
                );
            if (!resumed)
                have = 0;
            long? total =
                resp.Content.Headers.ContentRange?.Length
                ?? (
                    resp.Content.Headers.ContentLength is { } len
                        ? len + (resumed ? have : 0)
                        : null
                );

            await using var fs = new FileStream(
                part,
                resumed ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                1 << 16,
                useAsync: true
            );
            await using var body = await resp.Content.ReadAsStreamAsync(ct);
            var buf = new byte[1 << 17];
            long sinceTick = 0;
            var lastTick = sw.Elapsed;
            var lastLog = sw.Elapsed;
            var startBytes = have;
            int n;
            while ((n = await body.ReadAsync(buf, ct)) > 0)
            {
                await fs.WriteAsync(buf.AsMemory(0, n), ct);
                have += n;
                sinceTick += n;
                var now = sw.Elapsed;
                if ((now - lastTick).TotalMilliseconds >= 500)
                {
                    progress?.Report(
                        new DownloadProgress(have, total, sinceTick / (now - lastTick).TotalSeconds)
                    );
                    sinceTick = 0;
                    lastTick = now;
                }
                if ((now - lastLog).TotalSeconds >= 30)
                {
                    _log.LogDebug(
                        "… {Have}/{Total} bytes ({Pct}%), avg {Rate:0.0} MiB/s",
                        have,
                        total,
                        total is > 0 ? have * 100 / total : -1,
                        (have - startBytes) / (1024.0 * 1024.0) / now.TotalSeconds
                    );
                    lastLog = now;
                }
            }
            progress?.Report(new DownloadProgress(have, total, 0));
            _log.LogInformation(
                "Transfer finished: {Bytes} bytes in {Seconds:0.0}s ({Rate:0.0} MiB/s)",
                have - startBytes,
                sw.Elapsed.TotalSeconds,
                (have - startBytes) / (1024.0 * 1024.0) / Math.Max(0.001, sw.Elapsed.TotalSeconds)
            );
            if (total is { } t && have != t)
            {
                _log.LogWarning(
                    "Connection closed early: {Have}/{Total} bytes; .part kept for resume",
                    have,
                    t
                );
                throw new DownloadException(
                    $"Connection closed early ({have}/{t} bytes)",
                    retryResolve: false
                );
            }
        }

        await VerifyAsync(part, src, ct);
        File.Move(part, destinationPath, overwrite: true);
        return destinationPath;
    }

    private async Task VerifyAsync(string path, ResolvedDownload src, CancellationToken ct)
    {
        if (src.Sha256 is null && src.Md5 is null)
        {
            _log.LogInformation(
                "No checksum published by {Host}; relying on archive test",
                src.Host
            );
            return;
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await using var fs = File.OpenRead(path);
        string actual,
            expected,
            algo;
        if (src.Sha256 is not null)
        {
            algo = "SHA-256";
            actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(fs, ct));
            expected = src.Sha256;
        }
        else
        {
            algo = "MD5";
            actual = Convert.ToHexStringLower(await MD5.HashDataAsync(fs, ct));
            expected = src.Md5!;
        }
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            fs.Close();
            File.Delete(path);
            _log.LogError(
                "{Algo} mismatch: expected {Expected}, got {Actual}; deleted {Path}",
                algo,
                expected,
                actual,
                path
            );
            throw new DownloadException(
                $"Checksum mismatch (expected {expected}, got {actual}); deleted partial file",
                retryResolve: true
            );
        }
        _log.LogInformation(
            "{Algo} verified ({Hash}) in {Seconds:0.0}s",
            algo,
            actual,
            sw.Elapsed.TotalSeconds
        );
    }
}

public sealed class DownloadException(string message, bool retryResolve) : Exception(message)
{
    /// <summary>True when the sensible retry is to resolve a fresh link (expired/403/HTML) rather than resume the same URL.</summary>
    public bool RetryResolve => retryResolve;
}
