using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SteamRip.Core.Install;

namespace SteamRip.Core.Launch;

public sealed record LaunchSpec(
    long PostId,
    string Name,
    string GameRoot,
    string ExeRelative,
    string? ProtonPath,
    string? Arguments,
    long? SteamAppId,
    LaunchOptions? Options = null
);

public sealed record RunningGame(LaunchSpec Spec, Process Process, DateTime StartedUtc);

/// <summary>The fully composed process: wrappers, runner, exe, args and the env to add. Pure data so it can be asserted in tests.</summary>
public sealed record LaunchCommand(
    IReadOnlyList<string> Command,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<string> Warnings
)
{
    public string FileName => Command[0];
    public IEnumerable<string> Arguments => Command.Skip(1);
}

/// <summary>
/// Runs games in a per-game Wine prefix (<c>&lt;game&gt;/prefix</c>) through umu-run + Proton on Linux, or directly on Windows.
/// Tracks what is running so updates can wait for the player to quit, and reports playtime when a game exits.
/// </summary>
public sealed class GameLauncher(ILogger<GameLauncher>? logger = null)
{
    private readonly ILogger _log = logger ?? NullLogger<GameLauncher>.Instance;
    private readonly ConcurrentDictionary<long, RunningGame> _running = new();

    public static string? UmuPath => ArchiveTool.Find("umu-run");
    public static string? WinePath => ArchiveTool.Find("wine");

    public static string PrefixDir(string gameRoot) => Path.Combine(gameRoot, "prefix");

    public event Action<long>? Started;
    public event Action<long, TimeSpan, int>? Exited;

    public bool IsRunning(long postId) =>
        _running.TryGetValue(postId, out var r) && !r.Process.HasExited;

    public IReadOnlyCollection<long> RunningIds =>
        [.. _running.Where(kv => !kv.Value.Process.HasExited).Select(kv => kv.Key)];

    public static string Describe() =>
        OperatingSystem.IsWindows()
            ? "native"
            : $"umu-run: {UmuPath ?? "missing"}, wine: {WinePath ?? "missing"}";

    public async Task<RunningGame> LaunchAsync(LaunchSpec spec)
    {
        if (IsRunning(spec.PostId))
            throw new LaunchException($"{spec.Name} is already running.");
        var exe = Path.Combine(spec.GameRoot, spec.ExeRelative);
        if (!File.Exists(exe))
            throw new LaunchException(
                $"Executable not found: {exe}. Pick another one from the game's menu."
            );

        string? runner = null;
        string? prefix = null;
        if (!OperatingSystem.IsWindows())
        {
            prefix = PrefixDir(spec.GameRoot);
            Directory.CreateDirectory(prefix);
            runner = UmuPath ?? WinePath;
            if (runner is null)
                throw new LaunchException(
                    "Neither umu-run nor wine is installed. Install umu-launcher (recommended) to play."
                );
            if (UmuPath is null)
                _log.LogWarning(
                    "umu-run not found; falling back to plain wine for {Name} (no Proton, no game fixes)",
                    spec.Name
                );
            await EnsureVcRedistAsync(spec.GameRoot, spec.ProtonPath);
        }
        var cmd = Compose(
            spec,
            runner,
            exe,
            prefix,
            ArchiveTool.Find,
            HostCapabilities.LargestScreen()
        );
        foreach (var w in cmd.Warnings)
            _log.LogWarning("{Name}: {Warning}", spec.Name, w);

        var psi = new ProcessStartInfo
        {
            FileName = cmd.FileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        foreach (var a in cmd.Arguments)
            psi.ArgumentList.Add(a);
        foreach (var (k, v) in cmd.Environment)
            psi.Environment[k] = v;
        psi.Environment.Remove("UMU_RUNTIME_UPDATE"); // let umu keep its runtime current

        _log.LogInformation(
            "Launching {Name}: {Exe} via {Runner} (prefix {Prefix}, proton {Proton}, GAMEID {GameId}, args \"{Args}\", wrappers [{Wrappers}], env {Env})",
            spec.Name,
            exe,
            Path.GetFileName(runner ?? exe),
            prefix ?? "n/a",
            spec.ProtonPath ?? "umu default (UMU-Proton)",
            cmd.Environment.GetValueOrDefault("GAMEID", "n/a"),
            spec.Arguments ?? "",
            string.Join(
                ' ',
                cmd.Command.TakeWhile(c => c != runner && c != exe).Select(Path.GetFileName)
            ),
            string.Join(
                ' ',
                cmd.Environment.Where(kv => !BaseEnv.Contains(kv.Key))
                    .Select(kv => $"{kv.Key}={kv.Value}")
            )
        );

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var started = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();
        var tail = new ConcurrentQueue<string>();
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
        return running;

        void Keep(string? line)
        {
            if (line is null)
                return;
            tail.Enqueue(line);
            while (tail.Count > 40)
                tail.TryDequeue(out _);
            _log.LogTrace("[{Name}] {Line}", spec.Name, line);
        }
    }

    /// <summary>
    /// Once per prefix: run VC++ installers from <c>_CommonRedist</c> into the Wine prefix so games
    /// that need <c>mfc140.dll</c> / VC runtimes start without winetricks.
    /// </summary>
    public async Task EnsureVcRedistAsync(
        string gameRoot,
        string? protonPath,
        CancellationToken ct = default
    )
    {
        if (OperatingSystem.IsWindows())
            return;
        var prefix = PrefixDir(gameRoot);
        if (!RedistInstaller.NeedsInstall(prefix))
            return;
        var installers = RedistInstaller.FindVcInstallers(gameRoot);
        if (installers.Count == 0)
        {
            RedistInstaller.MarkDone(prefix);
            return;
        }
        _log.LogInformation(
            "Installing {Count} VC redistributable(s) from _CommonRedist into {Prefix}",
            installers.Count,
            prefix
        );
        foreach (var inst in installers)
        {
            ct.ThrowIfCancellationRequested();
            _log.LogInformation("Running {Installer}", Path.GetFileName(inst.FilePath));
            try
            {
                var code = await RunInPrefixAsync(
                    gameRoot,
                    protonPath,
                    inst.FilePath,
                    inst.Args,
                    ct: ct
                );
                _log.LogInformation(
                    "{Installer} exited {Code}",
                    Path.GetFileName(inst.FilePath),
                    code
                );
            }
            catch (Exception e)
            {
                _log.LogWarning(
                    "{Installer} failed: {Error}",
                    Path.GetFileName(inst.FilePath),
                    e.Message
                );
            }
        }
        RedistInstaller.MarkDone(prefix);
    }

    /// <summary>
    /// Run a Windows exe/msi inside the game's Proton prefix via umu-run (or wine), wait for exit.
    /// No gamemode/gamescope — for redistributables and the "Run exe…" menu.
    /// </summary>
    public async Task<int> RunInPrefixAsync(
        string gameRoot,
        string? protonPath,
        string windowsExe,
        IReadOnlyList<string>? args = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default
    )
    {
        if (OperatingSystem.IsWindows())
            throw new LaunchException("Run-in-prefix is only used on Linux.");
        if (!File.Exists(windowsExe))
            throw new LaunchException($"File not found: {windowsExe}");
        var runner =
            UmuPath
            ?? WinePath
            ?? throw new LaunchException(
                "Neither umu-run nor wine is installed. Install umu-launcher (recommended)."
            );
        var prefix = PrefixDir(gameRoot);
        Directory.CreateDirectory(prefix);
        var psi = new ProcessStartInfo(runner)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(windowsExe)!,
        };
        if (windowsExe.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add("msiexec");
            psi.ArgumentList.Add("/i");
            psi.ArgumentList.Add(windowsExe);
        }
        else
            psi.ArgumentList.Add(windowsExe);
        if (args is not null)
            foreach (var a in args)
                psi.ArgumentList.Add(a);
        psi.Environment["WINEPREFIX"] = prefix;
        psi.Environment["GAMEID"] = "umu-default";
        psi.Environment["STORE"] = "none";
        if (!string.IsNullOrEmpty(protonPath))
            psi.Environment["PROTONPATH"] = protonPath;
        psi.Environment.Remove("UMU_RUNTIME_UPDATE");

        _log.LogInformation(
            "Prefix run: {Runner} {Exe} [{Args}] (prefix {Prefix}, proton {Proton})",
            Path.GetFileName(runner),
            Path.GetFileName(windowsExe),
            args is null ? "" : string.Join(' ', args),
            prefix,
            protonPath ?? "umu default"
        );

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        var wait = timeout ?? TimeSpan.FromMinutes(15);
        try
        {
            var finished = await Task.Run(() => proc.WaitForExit((int)wait.TotalMilliseconds), ct);
            if (!finished)
            {
                TryKill(proc);
                throw new LaunchException(
                    $"{Path.GetFileName(windowsExe)} timed out after {wait.TotalMinutes:0} min"
                );
            }
            return proc.ExitCode;
        }
        catch (OperationCanceledException)
        {
            TryKill(proc);
            throw;
        }

        static void TryKill(Process p)
        {
            try
            {
                if (!p.HasExited)
                    p.Kill(entireProcessTree: true);
            }
            catch
            {
                /* already gone */
            }
        }
    }

    /// <summary>
    /// Open winetricks GUI against this game's prefix, using the same Proton wine binary Play would use.
    /// </summary>
    public void OpenWinetricks(string gameRoot, string? protonPath)
    {
        if (OperatingSystem.IsWindows())
            throw new LaunchException("Winetricks is a Linux tool.");
        var wt =
            ArchiveTool.Find("winetricks")
            ?? throw new LaunchException(
                "winetricks is not installed. Install it to manage this game's prefix."
            );
        var prefix = PrefixDir(gameRoot);
        Directory.CreateDirectory(prefix);
        var psi = new ProcessStartInfo(wt) { UseShellExecute = false, WorkingDirectory = gameRoot };
        psi.ArgumentList.Add("--gui");
        psi.Environment["WINEPREFIX"] = prefix;
        if (ProtonWineBins(protonPath) is { } bins)
        {
            psi.Environment["WINE"] = bins.Wine;
            psi.Environment["WINESERVER"] = bins.Wineserver;
            _log.LogInformation(
                "Winetricks for {Prefix} with Proton wine {Wine}",
                prefix,
                bins.Wine
            );
        }
        else
            _log.LogWarning(
                "Winetricks for {Prefix}: Proton wine not found for {Proton}; winetricks will use system wine",
                prefix,
                protonPath ?? "default"
            );
        Process.Start(psi);
    }

    /// <summary>Proton's <c>files/bin/wine</c> + <c>wineserver</c> for a stored path or GE/DW keyword.</summary>
    public static (string Wine, string Wineserver)? ProtonWineBins(string? protonPath)
    {
        var dir = ProtonLocator.IsKeyword(protonPath)
            ? ProtonLocator.ResolveInstalled(protonPath)
            : protonPath;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return null;
        var wine = Path.Combine(dir, "files", "bin", "wine");
        var server = Path.Combine(dir, "files", "bin", "wineserver");
        if (!File.Exists(wine) || !File.Exists(server))
            return null;
        return (wine, server);
    }

    private static readonly HashSet<string> BaseEnv =
    [
        "WINEPREFIX",
        "GAMEID",
        "STORE",
        "PROTONPATH",
    ];

    /// <summary>
    /// Turns the spec into <c>[wrapper] [gamemoderun] [gamescope … --] runner exe args</c> plus environment.
    /// <paramref name="runner"/> is umu-run or wine (null on Windows: run the exe directly, Proton options ignored).
    /// A missing wrapper tool is a warning, never a failed launch.
    /// </summary>
    public static LaunchCommand Compose(
        LaunchSpec spec,
        string? runner,
        string exe,
        string? prefix,
        Func<string, string?> findTool,
        (int Width, int Height)? screen = null
    )
    {
        var o = spec.Options ?? LaunchOptions.AppDefaults;
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        var warnings = new List<string>();
        var cmd = new List<string>();
        var userEnv = LaunchOptions.ParseEnvironment(o.Environment).ToList();

        if (!string.IsNullOrWhiteSpace(o.Wrapper))
            cmd.AddRange(SplitArgs(o.Wrapper));

        if (runner is not null)
        {
            env["WINEPREFIX"] = prefix!;
            env["GAMEID"] = spec.SteamAppId is { } id ? $"umu-{id}" : "umu-default";
            env["STORE"] = "none";
            if (!string.IsNullOrEmpty(spec.ProtonPath))
                env["PROTONPATH"] = spec.ProtonPath;

            var gamescope = o.Gamescope == true ? findTool("gamescope") : null;
            if (o.Gamescope == true && gamescope is null)
                warnings.Add("gamescope is enabled but not installed; launching without it.");

            if (o.GameMode == true)
            {
                if (findTool("gamemoderun") is { } gm)
                    cmd.Add(gm);
                else
                    warnings.Add(
                        "gamemode is enabled but gamemoderun is not installed; skipping it."
                    );
            }

            if (o.MangoHud == true && gamescope is null)
                env["MANGOHUD"] = "1";
            if (o.MangoHud == true && !string.IsNullOrWhiteSpace(o.MangoHudConfig))
                env["MANGOHUD_CONFIG"] = o.MangoHudConfig;

            if (o.Hdr == true)
            {
                // GE-Proton 11 / Proton-CachyOS / DW-Proton detect HDR themselves (DXVK + VKD3D-Proton query the
                // Vulkan surface). Forcing DXVK_HDR / PROTON_ENABLE_HDR makes the game output PQ onto an output Wine
                // believes is SDR: grey blacks and dull colours. So: gamescope --hdr-enabled, or Wine's Wayland
                // driver so the compositor's colour management is visible; nothing else.
                if (gamescope is null)
                {
                    env["PROTON_ENABLE_WAYLAND"] = "1";
                    if (o.Wayland != true)
                        warnings.Add(
                            "HDR without gamescope needs Wine's Wayland driver; enabling PROTON_ENABLE_WAYLAND for this launch."
                        );
                }
                else if (userEnv.Any(kv => kv.Key == "ENABLE_HDR_WSI"))
                    warnings.Add(
                        "ENABLE_HDR_WSI is set in the environment; gamescope HDR works without it and it can break the output."
                    );
            }
            warnings.AddRange(
                userEnv
                    .Where(kv =>
                        kv.Key is "DXVK_HDR" or "PROTON_ENABLE_HDR" or "PROTON_USE_HDR"
                        && kv.Value == "1"
                    )
                    .Select(kv =>
                        $"{kv.Key}=1 is set in the environment; current Proton builds detect HDR themselves and this forces PQ output onto what Wine sees as an SDR display (grey blacks). Use PROTON_FORCE_HDR=1 if you really need to force it."
                    )
            );
            if (o.Wayland == true)
                env["PROTON_ENABLE_WAYLAND"] = "1";
            if (o.Fsr == true)
                env["WINE_FULLSCREEN_FSR"] = "1";
            if (o.Fsync == false)
                env["PROTON_NO_FSYNC"] = "1";
            if (o.ProtonLog == true)
                env["PROTON_LOG"] = "1";
            if (!string.IsNullOrWhiteSpace(o.DllOverrides))
                env["WINEDLLOVERRIDES"] = o.DllOverrides.Trim();

            if (gamescope is not null)
            {
                cmd.Add(gamescope);
                var args = SplitArgs(o.GamescopeArgs).ToList();
                cmd.AddRange(args);
                if (
                    screen is { } s
                    && !args.Any(a =>
                        a is "-W" or "-H" or "--output-width" or "--output-height"
                        || a.StartsWith("--output-width=", StringComparison.Ordinal)
                        || a.StartsWith("--output-height=", StringComparison.Ordinal)
                    )
                )
                    cmd.AddRange(["-W", s.Width.ToString(), "-H", s.Height.ToString()]);
                if (o.Hdr == true && !args.Contains("--hdr-enabled"))
                    cmd.Add("--hdr-enabled");
                if (o.MangoHud == true && !args.Contains("--mangoapp"))
                    cmd.Add("--mangoapp");
                cmd.Add("--");
            }
            cmd.Add(runner);
        }

        cmd.Add(exe);
        cmd.AddRange(SplitArgs(spec.Arguments));
        foreach (var (k, v) in userEnv)
            env[k] = v;
        return new LaunchCommand(cmd, env, warnings);
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
        if (OperatingSystem.IsWindows() || UmuPath is not { } umu)
            return;
        // Wine processes outlive the umu wrapper; ask the prefix's wineserver to shut everything down.
        try
        {
            var psi = new ProcessStartInfo(umu)
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
