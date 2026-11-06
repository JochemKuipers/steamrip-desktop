using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SteamRip.Core.Install;

/// <summary>Shells out to unrar/7z. Linux: whatever is on PATH. Windows: a bundled <c>7za.exe</c> next to the app, or 7-Zip's install dir.</summary>
public sealed partial class ArchiveTool(ILogger<ArchiveTool>? logger = null)
{
    [GeneratedRegex(@"(\d{1,3})%")]
    private static partial Regex Percent();

    private readonly ILogger _log = logger ?? NullLogger<ArchiveTool>.Instance;

    public string? UnrarPath { get; init; } = Find("unrar");
    public string? SevenZipPath { get; init; } =
        Find("7z") ?? Find("7za") ?? Find("7zz") ?? Find("7zr");

    public bool Available => UnrarPath is not null || SevenZipPath is not null;

    public string Describe() => $"unrar: {UnrarPath ?? "missing"}, 7z: {SevenZipPath ?? "missing"}";

    public async Task<bool> TestAsync(string archive, CancellationToken ct)
    {
        var (exe, args) =
            UnrarPath is not null && archive.EndsWith(".rar", StringComparison.OrdinalIgnoreCase)
                ? (UnrarPath, new[] { "t", "-y", "-idq", archive })
                : (
                    SevenZipPath ?? throw new InvalidOperationException("No archive tool found"),
                    new[] { "t", archive, "-y", "-bso0", "-bsp0" }
                );
        var code = await RunAsync(exe, args, null, ct);
        return code == 0;
    }

    public async Task ExtractAsync(
        string archive,
        string destination,
        IProgress<int>? percent,
        CancellationToken ct
    )
    {
        Directory.CreateDirectory(destination);
        var (exe, args) =
            UnrarPath is not null && archive.EndsWith(".rar", StringComparison.OrdinalIgnoreCase)
                ? (
                    UnrarPath,
                    new[]
                    {
                        "x",
                        "-y",
                        "-o+",
                        archive,
                        destination.TrimEnd('/', '\\') + Path.DirectorySeparatorChar,
                    }
                )
                : (
                    SevenZipPath ?? throw new InvalidOperationException("No archive tool found"),
                    new[] { "x", archive, "-o" + destination, "-y", "-bsp1", "-bso0" }
                );
        var code = await RunAsync(exe, args, percent, ct);
        if (code != 0)
            throw new InvalidOperationException($"{Path.GetFileName(exe)} exited with {code}");
    }

    private async Task<int> RunAsync(
        string exe,
        string[] args,
        IProgress<int>? percent,
        CancellationToken ct
    )
    {
        var sw = Stopwatch.StartNew();
        _log.LogInformation(
            "Running: {Exe} {Args}",
            exe,
            string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))
        );
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p =
            Process.Start(psi) ?? throw new InvalidOperationException("failed to start " + exe);
        var last = -1;
        var tail = new StringBuilder();
        var reader = Task.Run(
            // ReSharper disable once AccessToDisposedClosure -- awaited before the process is disposed
            async () =>
            {
                var buf = new char[256];
                int n;
                while ((n = await p.StandardOutput.ReadAsync(buf, ct)) > 0)
                {
                    var chunk = new string(buf, 0, n);
                    if (tail.Length < 4000)
                        tail.Append(chunk);
                    foreach (Match m in Percent().Matches(chunk))
                    {
                        var v = int.Parse(m.Groups[1].Value);
                        if (v != last && v <= 100)
                        {
                            last = v;
                            percent?.Report(v);
                        }
                    }
                }
            },
            ct
        );
        var errTask = p.StandardError.ReadToEndAsync(ct);
        // ReSharper disable once AccessToDisposedClosure -- registration is disposed before the process
        using var reg = ct.Register(() =>
        {
            try
            {
                p.Kill(true);
            }
            catch { }
        });
        await p.WaitForExitAsync(ct);
        await Task.WhenAll(reader, errTask);
        var stderr = errTask.Result.Trim();
        if (p.ExitCode == 0)
            _log.LogInformation(
                "{Exe} exited 0 in {Seconds:0.0}s",
                Path.GetFileName(exe),
                sw.Elapsed.TotalSeconds
            );
        else
            _log.LogError(
                "{Exe} exited {Code} in {Seconds:0.0}s\nstderr: {Stderr}\nstdout (tail): {Stdout}",
                Path.GetFileName(exe),
                p.ExitCode,
                sw.Elapsed.TotalSeconds,
                stderr.Length > 2000 ? stderr[^2000..] : stderr,
                tail.Length > 1500 ? tail.ToString()[^1500..] : tail.ToString()
            );
        return p.ExitCode;
    }

    public static string? Find(string name)
    {
        var exeNames = OperatingSystem.IsWindows() ? new[] { name + ".exe", name } : new[] { name };
        var dirs = new List<string>();
        dirs.Add(AppContext.BaseDirectory);
        dirs.Add(Path.Combine(AppContext.BaseDirectory, "tools"));
        dirs.AddRange(
            (Environment.GetEnvironmentVariable("PATH") ?? "").Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries
            )
        );
        if (OperatingSystem.IsWindows())
        {
            dirs.Add(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "7-Zip"
                )
            );
            dirs.Add(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    "7-Zip"
                )
            );
            dirs.Add(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "WinRAR"
                )
            );
        }
        foreach (var d in dirs)
        foreach (var e in exeNames)
        {
            var p = Path.Combine(d, e);
            if (File.Exists(p))
                return p;
        }
        return null;
    }
}
