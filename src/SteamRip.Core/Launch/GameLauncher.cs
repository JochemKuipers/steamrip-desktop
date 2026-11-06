using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SteamRip.Core.Launch;

public sealed record LaunchSpec(
    long PostId,
    string Name,
    string GameRoot,
    string ExeRelative,
    string? ProtonPath,
    string? Arguments,
    long? SteamAppId
);

public sealed record RunningGame(LaunchSpec Spec, Process Process, DateTime StartedUtc);

/// <summary>
/// Runs games in a per-game Wine prefix (<c>&lt;game&gt;/prefix</c>) through umu-run + Proton on Linux, or directly on Windows.
/// Tracks what is running so updates can wait for the player to quit, and reports playtime when a game exits.
/// </summary>
public sealed class GameLauncher(ILogger<GameLauncher>? logger = null)
{
    private readonly ILogger _log = logger ?? NullLogger<GameLauncher>.Instance;
    private readonly ConcurrentDictionary<long, RunningGame> _running = new();

    public static string? UmuPath => Install.ArchiveTool.Find("umu-run");
    public static string? WinePath => Install.ArchiveTool.Find("wine");

    public static string PrefixDir(string gameRoot) => Path.Combine(gameRoot, "prefix");

    public event Action<long>? Started;
    public event Action<long, TimeSpan, int>? Exited;

    public bool IsRunning(long postId) =>
        _running.TryGetValue(postId, out var r) && !r.Process.HasExited;

    public IReadOnlyCollection<long> RunningIds =>
        _running.Where(kv => !kv.Value.Process.HasExited).Select(kv => kv.Key).ToList();

    public string Describe() =>
        OperatingSystem.IsWindows()
            ? "native"
            : $"umu-run: {UmuPath ?? "missing"}, wine: {WinePath ?? "missing"}";

    public Task<RunningGame> LaunchAsync(LaunchSpec spec)
    {
        if (IsRunning(spec.PostId))
            throw new LaunchException($"{spec.Name} is already running.");
        var exe = Path.Combine(spec.GameRoot, spec.ExeRelative);
        if (!File.Exists(exe))
            throw new LaunchException(
                $"Executable not found: {exe}. Pick another one from the game's menu."
            );

        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        if (OperatingSystem.IsWindows())
        {
            psi.FileName = exe;
            if (!string.IsNullOrWhiteSpace(spec.Arguments))
                psi.Arguments = spec.Arguments;
        }
        else
        {
            var prefix = PrefixDir(spec.GameRoot);
            Directory.CreateDirectory(prefix);
            var umu = UmuPath;
            if (umu is not null)
            {
                psi.FileName = umu;
                psi.ArgumentList.Add(exe);
                psi.Environment["WINEPREFIX"] = prefix;
                psi.Environment["GAMEID"] = spec.SteamAppId is { } id ? $"umu-{id}" : "umu-default";
                psi.Environment["STORE"] = "none";
                if (!string.IsNullOrEmpty(spec.ProtonPath))
                    psi.Environment["PROTONPATH"] = spec.ProtonPath;
                psi.Environment.Remove("UMU_RUNTIME_UPDATE"); // let umu keep its runtime current
            }
            else if (WinePath is { } wine)
            {
                psi.FileName = wine;
                psi.ArgumentList.Add(exe);
                psi.Environment["WINEPREFIX"] = prefix;
                _log.LogWarning(
                    "umu-run not found; falling back to plain wine for {Name} (no Proton, no game fixes)",
                    spec.Name
                );
            }
            else
                throw new LaunchException(
                    "Neither umu-run nor wine is installed. Install umu-launcher (recommended) to play."
                );
            foreach (var a in SplitArgs(spec.Arguments))
                psi.ArgumentList.Add(a);
        }

        _log.LogInformation(
            "Launching {Name}: {Exe} via {Runner} (prefix {Prefix}, proton {Proton}, GAMEID {GameId}, args \"{Args}\")",
            spec.Name,
            exe,
            Path.GetFileName(psi.FileName),
            psi.Environment.TryGetValue("WINEPREFIX", out var p) ? p : "n/a",
            spec.ProtonPath ?? "umu default",
            psi.Environment.TryGetValue("GAMEID", out var g) ? g : "n/a",
            spec.Arguments ?? ""
        );

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var started = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();
        var tail = new ConcurrentQueue<string>();
        void Keep(string? line)
        {
            if (line is null)
                return;
            tail.Enqueue(line);
            while (tail.Count > 40)
                tail.TryDequeue(out _);
            _log.LogTrace("[{Name}] {Line}", spec.Name, line);
        }
        proc.OutputDataReceived += (_, e) => Keep(e.Data);
        proc.ErrorDataReceived += (_, e) => Keep(e.Data);
        proc.Exited += (_, _) =>
        {
            var code = proc.ExitCode;
            _running.TryRemove(spec.PostId, out _);
            if (_stopped.TryRemove(spec.PostId, out _))
                _log.LogInformation(
                    "{Name} stopped after {Minutes:0.0} min",
                    spec.Name,
                    sw.Elapsed.TotalMinutes
                );
            else if (code != 0 || sw.Elapsed < TimeSpan.FromSeconds(10))
                _log.LogWarning(
                    "{Name} exited with {Code} after {Seconds:0.0}s; last output:\n{Tail}",
                    spec.Name,
                    code,
                    sw.Elapsed.TotalSeconds,
                    string.Join('\n', tail)
                );
            else
                _log.LogInformation(
                    "{Name} exited with {Code} after {Minutes:0.0} min",
                    spec.Name,
                    code,
                    sw.Elapsed.TotalMinutes
                );
            Exited?.Invoke(spec.PostId, sw.Elapsed, code);
            proc.Dispose();
        };
        try
        {
            proc.Start();
        }
        catch (Exception e)
        {
            throw new LaunchException(
                $"Could not start {Path.GetFileName(psi.FileName)}: {e.Message}"
            );
        }
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        var running = new RunningGame(spec, proc, started);
        _running[spec.PostId] = running;
        Started?.Invoke(spec.PostId);
        return Task.FromResult(running);
    }

    private readonly ConcurrentDictionary<long, byte> _stopped = new();

    public void Stop(long postId)
    {
        if (!_running.TryGetValue(postId, out var r))
            return;
        _log.LogInformation("Stopping {Name} (pid {Pid})", r.Spec.Name, r.Process.Id);
        _stopped[postId] = 0;
        try
        {
            r.Process.Kill(entireProcessTree: true);
        }
        catch (Exception e)
        {
            _log.LogWarning("Kill failed: {Error}", e.Message);
        }
        if (!OperatingSystem.IsWindows() && UmuPath is not null)
        {
            // Wine processes outlive the umu wrapper; ask the prefix's wineserver to shut everything down.
            try
            {
                var psi = new ProcessStartInfo(UmuPath)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                psi.ArgumentList.Add("wineboot");
                psi.ArgumentList.Add("-k");
                psi.Environment["WINEPREFIX"] = PrefixDir(r.Spec.GameRoot);
                psi.Environment["GAMEID"] = "umu-default";
                if (!string.IsNullOrEmpty(r.Spec.ProtonPath))
                    psi.Environment["PROTONPATH"] = r.Spec.ProtonPath;
                using var p = Process.Start(psi);
                p?.WaitForExit(15000);
            }
            catch (Exception e)
            {
                _log.LogDebug("wineboot -k failed: {Error}", e.Message);
            }
        }
    }

    /// <summary>Blocks until the game for <paramref name="postId"/> has exited (or the token fires). Used before an update swaps folders.</summary>
    public async Task WaitForExitAsync(long postId, CancellationToken ct)
    {
        while (IsRunning(postId))
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }

    private static IEnumerable<string> SplitArgs(string? args)
    {
        if (string.IsNullOrWhiteSpace(args))
            yield break;
        var cur = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var ch in args)
        {
            if (ch == '"')
            {
                quoted = !quoted;
                continue;
            }
            if (char.IsWhiteSpace(ch) && !quoted)
            {
                if (cur.Length > 0)
                {
                    yield return cur.ToString();
                    cur.Clear();
                }
                continue;
            }
            cur.Append(ch);
        }
        if (cur.Length > 0)
            yield return cur.ToString();
    }
}

public sealed class LaunchException(string message) : Exception(message);
