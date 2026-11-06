using System.Net;
using System.Security.Cryptography;
using SteamRip.Core.Catalog;
using SteamRip.Core.Downloads;
using SteamRip.Core.Hosters;

namespace SteamRip.Core.Tests;

/// <summary>Runs a tiny local HTTP server that honours Range and can drop the connection mid-body, to exercise resume + verification.</summary>
public class DownloaderTests : IAsyncLifetime
{
    private HttpListener _listener = null!;
    private string _url = null!;
    private byte[] _payload = null!;
    private int _cutAfter = -1; // bytes to send before closing, -1 = never cut
    private readonly List<string?> _rangeHeaders = [];
    private CancellationTokenSource _cts = null!;

    public Task InitializeAsync()
    {
        _payload = new byte[3 * 1024 * 1024 + 123];
        RandomNumberGenerator.Fill(_payload);
        var port = Random.Shared.Next(20000, 40000);
        _url = $"http://127.0.0.1:{port}/file.rar";
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _cts = new CancellationTokenSource();
        _ = Task.Run(ServeAsync);
        return Task.CompletedTask;
    }

    private async Task ServeAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); } catch { return; }
            _ = Task.Run(async () =>
            {
                var range = ctx.Request.Headers["Range"];
                lock (_rangeHeaders) _rangeHeaders.Add(range);
                long start = 0;
                if (range is not null && range.StartsWith("bytes="))
                {
                    start = long.Parse(range[6..].Split('-')[0]);
                    ctx.Response.StatusCode = 206;
                    ctx.Response.Headers["Content-Range"] = $"bytes {start}-{_payload.Length - 1}/{_payload.Length}";
                }
                ctx.Response.ContentType = "application/vnd.rar";
                ctx.Response.ContentLength64 = _payload.Length - start;
                var toSend = _cutAfter >= 0 ? Math.Min(_cutAfter, _payload.Length - (int)start) : _payload.Length - (int)start;
                try
                {
                    await ctx.Response.OutputStream.WriteAsync(_payload.AsMemory((int)start, toSend));
                    if (toSend < _payload.Length - start) ctx.Response.Abort(); else ctx.Response.Close();
                }
                catch { }
            });
        }
    }

    public Task DisposeAsync() { _cts.Cancel(); _listener.Stop(); return Task.CompletedTask; }

    private ResolvedDownload Src(string? sha = null) => new(Hoster.FileDitch, _url, new Dictionary<string, string>(), "file.rar", _payload.Length, Sha256: sha);

    [Fact]
    public async Task Downloads_whole_file_and_verifies_sha256()
    {
        var sha = Convert.ToHexStringLower(SHA256.HashData(_payload));
        var dest = Path.Combine(Path.GetTempPath(), "sr-dl-" + Guid.NewGuid().ToString("N"), "file.rar");
        var dl = new Downloader(new HttpClient());
        var progress = new List<DownloadProgress>();
        var path = await dl.DownloadAsync(Src(sha), dest, new Progress<DownloadProgress>(progress.Add), CancellationToken.None);
        Assert.Equal(dest, path);
        Assert.Equal(_payload, await File.ReadAllBytesAsync(dest));
        Assert.False(File.Exists(dest + ".part"));
        Directory.Delete(Path.GetDirectoryName(dest)!, true);
    }

    [Fact]
    public async Task Resumes_partial_file_with_range_request()
    {
        var dest = Path.Combine(Path.GetTempPath(), "sr-dl-" + Guid.NewGuid().ToString("N"), "file.rar");
        var dl = new Downloader(new HttpClient());

        _cutAfter = 1_000_000;
        await Assert.ThrowsAnyAsync<Exception>(() => dl.DownloadAsync(Src(), dest, null, CancellationToken.None));
        Assert.True(File.Exists(dest + ".part"));
        var partial = new FileInfo(dest + ".part").Length;
        Assert.InRange(partial, 1, _payload.Length - 1);

        _cutAfter = -1;
        await dl.DownloadAsync(Src(Convert.ToHexStringLower(SHA256.HashData(_payload))), dest, null, CancellationToken.None);
        Assert.Equal(_payload, await File.ReadAllBytesAsync(dest));
        lock (_rangeHeaders) Assert.Contains(_rangeHeaders, r => r == $"bytes={partial}-");
        Directory.Delete(Path.GetDirectoryName(dest)!, true);
    }

    [Fact]
    public async Task Checksum_mismatch_deletes_file_and_asks_for_reresolve()
    {
        var dest = Path.Combine(Path.GetTempPath(), "sr-dl-" + Guid.NewGuid().ToString("N"), "file.rar");
        var dl = new Downloader(new HttpClient());
        var ex = await Assert.ThrowsAsync<DownloadException>(() => dl.DownloadAsync(Src(new string('0', 64)), dest, null, CancellationToken.None));
        Assert.True(ex.RetryResolve);
        Assert.False(File.Exists(dest));
        Assert.False(File.Exists(dest + ".part"));
        Directory.Delete(Path.GetDirectoryName(dest)!, true);
    }
}
