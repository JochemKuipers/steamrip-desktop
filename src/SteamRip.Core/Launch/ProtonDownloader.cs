using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SteamRip.Core.Catalog;
using SteamRip.Core.Downloads;
using SteamRip.Core.Hosters;

namespace SteamRip.Core.Launch;

/// <summary>One release of a Proton fork on GitHub: tag, tarball and its checksum file.</summary>
public sealed record ProtonRelease(
    string Tag,
    string ArchiveUrl,
    string ArchiveName,
    string? ChecksumUrl
);

/// <summary>
/// Keeps DW-Proton (Dawn Winery's Proton fork, mirrored on GitHub) installed next to umu's builds. umu only knows
/// how to fetch GE-Proton and UMU-Proton, so this does the same job for DW: check the latest release once per
/// session, download the tarball with resume, verify its SHA-512, extract with <c>tar</c>, and hand back the directory.
/// Offline it falls back to the newest installed build.
/// </summary>
// ponytail: one hard-coded mirror and one archive layout (<tag>-x86_64.tar.xz with a single top-level dir); old DW builds are never pruned.
public sealed class ProtonDownloader(
    HttpClient http,
    Downloader downloader,
    ILogger<ProtonDownloader>? logger = null
)
{
    public const string DwReleasesApi =
        "https://api.github.com/repos/dawn-winery/dwproton-mirror/releases/latest";

    private readonly ILogger _log = logger ?? NullLogger<ProtonDownloader>.Instance;
    private readonly Lock _gate = new();
    private Task<string>? _inFlight;
    private DateTime _lastCheckUtc = DateTime.MinValue;
    private string? _lastResolved;

    public string Dir { get; init; } = ProtonLocator.DownloadDir;

    /// <summary>Returns the directory to use as <c>PROTONPATH</c> for DW-Proton, downloading or updating it first when possible.</summary>
    /// <summary>Returns the directory to use as <c>PROTONPATH</c> for DW-Proton, downloading or updating it first when possible. Concurrent callers share one download.</summary>
    public Task<string> EnsureDwProtonAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_inFlight is { IsCompleted: false })
                return _inFlight;
            // one release check per 6 h; a launch should not wait on GitHub every time
            if (
                _lastResolved is not null
                && DateTime.UtcNow - _lastCheckUtc < TimeSpan.FromHours(6)
                && Directory.Exists(_lastResolved)
            )
                return Task.FromResult(_lastResolved);
            _inFlight = ResolveAsync(ct);
            return _inFlight;
        }
    }

    private async Task<string> ResolveAsync(CancellationToken ct)
    {
        var installed = ProtonLocator.ResolveInstalled(ProtonLocator.DwProton, Dir);
        ProtonRelease? latest = null;
        try
        {
            latest = ParseLatest(await FetchJsonAsync(DwReleasesApi, ct));
        }
        catch (Exception e)
            when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            _log.LogWarning(
                "DW-Proton release check failed ({Error}); using {Installed}",
                e.Message,
                installed ?? "nothing (none installed)"
            );
        }
        if (latest is null)
            return installed
                ?? throw new LaunchException(
                    "DW-Proton is not installed yet and GitHub could not be reached to download it. Pick another Proton or try again online."
                );
        var target = Path.Combine(Dir, latest.Tag);
        if (!File.Exists(Path.Combine(target, "proton")))
        {
            _log.LogInformation(
                "DW-Proton {Tag} is not installed ({Have}); downloading {Url}",
                latest.Tag,
                installed is null ? "none installed" : "have " + Path.GetFileName(installed),
                latest.ArchiveUrl
            );
            await InstallAsync(latest, target, ct);
        }
        _lastCheckUtc = DateTime.UtcNow;
        _lastResolved = target;
        return target;
    }

    private async Task InstallAsync(ProtonRelease rel, string target, CancellationToken ct)
    {
        var dl = Path.Combine(Dir, ".downloads");
        Directory.CreateDirectory(dl);
        var archive = Path.Combine(dl, rel.ArchiveName);
        await downloader.DownloadAsync(
            new ResolvedDownload(Hoster.Unknown, rel.ArchiveUrl, new Dictionary<string, string>()),
            archive,
            null,
            ct
        );
        if (rel.ChecksumUrl is not null)
        {
            var expected = ParseChecksum(
                await FetchJsonAsync(rel.ChecksumUrl, ct),
                rel.ArchiveName
            );
            if (expected is not null)
            {
                await using var fs = File.OpenRead(archive);
                var actual = Convert.ToHexStringLower(await SHA512.HashDataAsync(fs, ct));
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(archive);
                    throw new LaunchException(
                        $"DW-Proton download failed its SHA-512 check ({rel.ArchiveName}); deleted, try again."
                    );
                }
                _log.LogDebug("DW-Proton {Tag}: SHA-512 verified", rel.Tag);
            }
        }
        var staging = Path.Combine(Dir, ".staging-" + rel.Tag);
        if (Directory.Exists(staging))
            Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);
        var sw = Stopwatch.StartNew();
        await RunTarAsync(archive, staging, ct);
        // the tarball has a single top-level dir named after the tag; move it into place
        var top = Directory.EnumerateDirectories(staging).ToList();
        var src = top.Count == 1 ? top[0] : staging;
        if (!File.Exists(Path.Combine(src, "proton")))
            throw new LaunchException(
                $"DW-Proton archive {rel.ArchiveName} did not contain a proton script."
            );
        if (Directory.Exists(target))
            Directory.Delete(target, true);
        Directory.Move(src, target);
        if (Directory.Exists(staging))
            Directory.Delete(staging, true);
        File.Delete(archive);
        _log.LogInformation(
            "DW-Proton {Tag} installed to {Dir} (extract {Seconds:0.0}s)",
            rel.Tag,
            target,
            sw.Elapsed.TotalSeconds
        );
    }

    private static async Task RunTarAsync(string archive, string into, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("tar")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "-xJf", archive, "-C", into })
            psi.ArgumentList.Add(a);
        using var p =
            Process.Start(psi)
            ?? throw new LaunchException("tar is not installed; cannot unpack DW-Proton.");
        var err = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0)
            throw new LaunchException($"tar failed on {Path.GetFileName(archive)}: {await err}");
    }

    private async Task<string> FetchJsonAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", "steamrip-desktop");
        req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    // ---- pure parsers ----

    /// <summary>GitHub "latest release" JSON → the x86_64 tarball and its checksum file.</summary>
    public static ProtonRelease? ParseLatest(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("tag_name", out var tagEl) || tagEl.GetString() is not { } tag)
            return null;
        string? archive = null;
        string? archiveName = null;
        string? checksum = null;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            var url = a.GetProperty("browser_download_url").GetString() ?? "";
            if (name.EndsWith("-x86_64.tar.xz", StringComparison.Ordinal))
                (archive, archiveName) = (url, name);
            else if (name.EndsWith("-x86_64.sha512sum", StringComparison.Ordinal))
                checksum = url;
        }
        return archive is null ? null : new ProtonRelease(tag, archive, archiveName!, checksum);
    }

    /// <summary><c>sha512sum</c> output: <c>&lt;hex&gt;  &lt;file&gt;</c> per line.</summary>
    public static string? ParseChecksum(string text, string fileName) =>
        text.Split('\n')
            .Select(l => l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(p => p.Length >= 2 && p[^1].TrimStart('*') == fileName)
            .Select(p => p[0])
            .FirstOrDefault();
}
