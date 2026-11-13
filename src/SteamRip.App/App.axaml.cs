using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using SteamRip.App.ViewModels;
using SteamRip.App.Views;
using SteamRip.Core.Catalog;
using SteamRip.Core.Data;
using SteamRip.Core.Downloads;
using SteamRip.Core.Hosters;
using SteamRip.Core.Install;
using SteamRip.Core.Launch;
using SteamRip.Core.Logging;
using SteamRip.Core.Platform;
using SteamRip.Core.Solver;
using SteamRip.Core.Updates;

namespace SteamRip.App;

[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "Application lives for the whole process; the watcher is released on exit."
)]
public partial class App : Application
{
    public AppServices Services { get; private set; } = null!;
    private MainWindow? _window;
    private FileSystemWatcher? _showWatcher,
        _playWatcher;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Services = AppServices.Build(desktop.Args ?? []);
            HookCrashLogging(Services.Log);
            Services.Updates.Start();

            var play = Program.PlayArg(desktop.Args ?? []);
            var hidden =
                desktop.Args?.Contains("--hidden") == true
                || play is not null
                || (Services.Settings.StartHidden && desktop.Args?.Contains("--show") != true);
            if (!hidden || desktop.Args?.Contains("--show") == true)
                ShowWindow();
            if (play is { } id)
                _ = PlayRequestedAsync(id);

            var pause = TrayIcon
                .GetIcons(this)
                ?.FirstOrDefault()
                ?.Menu?.Items.OfType<NativeMenuItem>()
                .FirstOrDefault(i => i.ToggleType == MenuItemToggleType.CheckBox);
            pause?.IsChecked = Services.Settings.AutoUpdatesPaused;

            WatchShowRequests();
            desktop.ShutdownRequested += async (_, _) => await Services.DisposeAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static void HookCrashLogging(ILogger log)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            log.LogCritical(
                e.ExceptionObject as Exception,
                "Unhandled exception (terminating: {Terminating})",
                e.IsTerminating
            );
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            log.LogError(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            log.LogError(e.Exception, "Unhandled exception on the UI thread");
            e.Handled = true;
        };
        // SIGTERM / session logout skip the Avalonia shutdown path; make sure the last lines reach the file.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            log.LogInformation("Process exit");
            ((App)Current!).Services.LogFile.Dispose();
        };
    }

    public void ShowWindow()
    {
        if (_window is null)
        {
            _window = new MainWindow { DataContext = new MainViewModel(Services) };
            _window.Closing += (_, e) =>
            {
                e.Cancel = true;
                _window.Hide();
            };
        }
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void WatchShowRequests()
    {
        try
        {
            Directory.CreateDirectory(Settings.DefaultDir);
            _showWatcher = new FileSystemWatcher(Settings.DefaultDir, ".show")
            {
                EnableRaisingEvents = true,
            };
            _showWatcher.Created += (_, _) => Dispatcher.UIThread.Post(OnShowRequested);
            _showWatcher.Changed += (_, _) => Dispatcher.UIThread.Post(OnShowRequested);
            _playWatcher = new FileSystemWatcher(Settings.DefaultDir, ".play")
            {
                EnableRaisingEvents = true,
            };
            _playWatcher.Created += (_, _) => Dispatcher.UIThread.Post(OnPlayRequested);
            _playWatcher.Changed += (_, _) => Dispatcher.UIThread.Post(OnPlayRequested);
        }
        catch
        { /* not fatal */
        }
    }

    private void OnPlayRequested()
    {
        long id;
        try
        {
            var text = File.ReadAllText(Program.PlayRequestPath).Trim();
            File.Delete(Program.PlayRequestPath);
            if (!long.TryParse(text, out id))
                return;
        }
        catch (IOException)
        {
            return; // the Created and Changed events both fire; the second one finds the file gone
        }
        _ = PlayRequestedAsync(id);
    }

    /// <summary>A game shortcut was clicked: play through the normal path, and tell the user when that is impossible.</summary>
    private async Task PlayRequestedAsync(long postId)
    {
        Services.Log.LogInformation("Play requested from a shortcut for post {PostId}", postId);
        try
        {
            await Services.Updates.PlayAsync(postId);
        }
        catch (Exception e)
        {
            Services.Log.LogWarning("Shortcut play {PostId} failed: {Error}", postId, e.Message);
            await Services.Notifier.NotifyAsync("Could not start the game", e.Message);
        }
    }

    private void OnShowRequested()
    {
        try
        {
            File.Delete(Program.ShowRequestPath);
        }
        catch
        {
            // marker already gone or locked by the other instance; showing the window is what matters
        }
        ShowWindow();
    }

    private void TrayIcon_OnClicked(object? sender, EventArgs e) => ShowWindow();

    private void Show_OnClick(object? sender, EventArgs e) => ShowWindow();

    private void CheckNow_OnClick(object? sender, EventArgs e)
    {
        Services.Log.LogInformation("Tray: check for updates now");
        _ = Services.Updates.SyncCatalogAsync();
    }

    private void Pause_OnClick(object? sender, EventArgs e)
    {
        if (sender is not NativeMenuItem item)
            return;
        Services.Settings.AutoUpdatesPaused = item.IsChecked;
        Services.Log.LogInformation(
            "Tray: auto-updates {State}",
            item.IsChecked ? "paused" : "resumed"
        );
        Services.Settings.Save();
        if (!item.IsChecked)
            Services.Updates.CheckForUpdates();
    }

    private void Quit_OnClick(object? sender, EventArgs e)
    {
        Services.Log.LogInformation("Tray: quit");
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }
}

/// <summary>Composition root. Everything the UI needs, built once.</summary>
public sealed class AppServices : IAsyncDisposable
{
    /// <summary>"1.2.3+abc1234" from the release tag and commit, or "0.0.0-dev+…" for local builds.</summary>
    public static string Version { get; } = ShortVersion();

    private static string ShortVersion()
    {
        var v =
            typeof(AppServices)
                .Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
            ?? typeof(AppServices).Assembly.GetName().Version?.ToString()
            ?? "unknown";
        var plus = v.IndexOf('+');
        return plus > 0 && v.Length > plus + 8 ? v[..(plus + 8)] : v;
    }

    public required Settings Settings { get; init; }
    public required Database Db { get; init; }
    public required HttpClient Http { get; init; }
    public required FlareSolverrClient Solver { get; init; }
    public required ResolverChain Resolvers { get; init; }
    public required ArchiveTool Archive { get; init; }
    public required UpdateService Updates { get; init; }
    public required ISecretStore Secrets { get; init; }
    public required INotifier Notifier { get; init; }
    public required ILoggerFactory Loggers { get; init; }
    public required GameLauncher Launcher { get; init; }
    public required FileLoggerProvider LogFile { get; init; }
    public required ILogger Log { get; init; }
    public required Services.CoverCache Covers { get; init; }

    /// <summary>Background probe of what the machine supports (HDR, gamescope, Proton env vars). Replaced by "Re-check" in Settings.</summary>
    public Task<HostCapabilities> Capabilities { get; private set; } =
        Task.FromResult(HostCapabilities.None);

    /// <summary>The probe result when it has finished, else null (UI shows "checking…" and never blocks on it).</summary>
    public HostCapabilities? CapabilitiesNow =>
        Capabilities.IsCompletedSuccessfully ? Capabilities.Result : null;

    public Task<HostCapabilities> ProbeCapabilities()
    {
        Capabilities = Task.Run(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var caps = HostCapabilities.Probe();
            Log.LogInformation(
                "Capabilities ({Ms} ms):\n  {Lines}",
                sw.ElapsedMilliseconds,
                string.Join("\n  ", caps.Lines())
            );
            return caps;
        });
        return Capabilities;
    }

    public static AppServices Build(string[] args)
    {
        var settings = Settings.Load();
        settings.Save(); // materialise defaults on first run so the user can find the file

        var logFile = new FileLoggerProvider(Settings.DefaultLogDir)
        {
            MinimumLevel =
                settings.VerboseLogging || args.Contains("--verbose")
                    ? LogLevel.Debug
                    : LogLevel.Information,
        };
        var loggers = LoggerFactory.Create(b =>
            b.SetMinimumLevel(LogLevel.Trace).AddProvider(logFile)
        );
        var log = loggers.CreateLogger("App");
        log.LogInformation(
            "SteamRIP Desktop {Version} starting on {OS} ({Arch}), .NET {Runtime}; args [{Args}]; config {Config}; log level {Level}",
            Version,
            System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture,
            Environment.Version,
            string.Join(' ', args),
            Settings.DefaultDir,
            logFile.MinimumLevel
        );

        var db = new Database(Settings.DefaultDbPath);
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AllowAutoRedirect = false,
        };
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            Downloader.DefaultUserAgent
        );
        var catalogHttp = new HttpClient(
            new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All }
        )
        {
            Timeout = TimeSpan.FromSeconds(60),
        };
        catalogHttp.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            Downloader.DefaultUserAgent
        );
        var solverHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        var solver = new FlareSolverrClient(
            solverHttp,
            () => settings.FlareSolverrUrl,
            loggers.CreateLogger<FlareSolverrClient>()
        );
        var apiHttp = new HttpClient(
            new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All }
        )
        {
            Timeout = TimeSpan.FromSeconds(60),
        };

        var resolvers = new ResolverChain(
            [
                new FileDitchResolver(solver, loggers.CreateLogger<FileDitchResolver>()),
                new GoFileResolver(apiHttp, loggers.CreateLogger<GoFileResolver>()),
                new MegaDbResolver(logger: loggers.CreateLogger<MegaDbResolver>()),
                new BuzzHeavierResolver(solver, loggers.CreateLogger<BuzzHeavierResolver>()),
            ],
            () =>
                [
                    .. settings
                        .HosterOrder.Select(h =>
                            Enum.TryParse<Hoster>(h, true, out var v) ? v : Hoster.Unknown
                        )
                        .Where(h => h != Hoster.Unknown),
                ],
            loggers.CreateLogger<ResolverChain>()
        );

        var archive = new ArchiveTool(loggers.CreateLogger<ArchiveTool>());
        log.LogInformation("Archive tools: {Tools}", archive.Describe());
        var notifier = new Notifier(loggers.CreateLogger<Notifier>());
        var launcher = new GameLauncher(loggers.CreateLogger<GameLauncher>());
        log.LogInformation(
            "Launcher: {Runner}; Proton builds: [{Protons}]; default {Default}",
            GameLauncher.Describe(),
            string.Join(", ", ProtonLocator.Find().Select(p => p.Name)),
            ProtonLocator.Effective(settings.DefaultProtonPath)
        );
        var downloader = new Downloader(http, loggers.CreateLogger<Downloader>());
        var steamHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var steamApps = new SteamAppIdResolver(
            steamHttp,
            Path.Combine(Settings.DefaultDir, "steam-apps"),
            loggers.CreateLogger<SteamAppIdResolver>()
        );
        var secrets = SecretStore.Create(Settings.DefaultDir);
        var steamGridDb = new SteamGridDbClient(
            steamHttp,
            () => secrets.Read(SteamGridDb.SecretKey),
            loggers.CreateLogger<SteamGridDbClient>()
        );
        var updates = new UpdateService(
            db,
            settings,
            new CatalogClient(catalogHttp, logger: loggers.CreateLogger<CatalogClient>()),
            resolvers,
            downloader,
            new Installer(archive, loggers.CreateLogger<Installer>()),
            notifier,
            launcher,
            loggers.CreateLogger<UpdateService>(),
            new ProtonDownloader(http, downloader, loggers.CreateLogger<ProtonDownloader>()),
            steamApps,
            steamGridDb
        );
        log.LogInformation(
            "Secrets backend: {Backend}; catalog has {Posts} posts, {Installs} installs",
            secrets.Backend,
            db.PostCount(),
            db.GetInstalls().Count
        );
        var services = new AppServices
        {
            Settings = settings,
            Db = db,
            Http = http,
            Solver = solver,
            Resolvers = resolvers,
            Archive = archive,
            Updates = updates,
            Secrets = secrets,
            Notifier = notifier,
            Loggers = loggers,
            LogFile = logFile,
            Log = log,
            Launcher = launcher,
            Covers = new Services.CoverCache(
                steamHttp,
                Path.Combine(Settings.DefaultDir, "covers"),
                steamApps,
                steamGridDb,
                loggers.CreateLogger<Services.CoverCache>()
            ),
        };
        _ = services.ProbeCapabilities();
        return services;
    }

    public async ValueTask DisposeAsync()
    {
        await Updates.DisposeAsync();
        Db.Dispose();
        Log.LogInformation("SteamRIP Desktop stopped");
        Loggers.Dispose();
    }
}
