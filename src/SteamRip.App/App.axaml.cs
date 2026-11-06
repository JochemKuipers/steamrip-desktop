using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using SteamRip.Core.Logging;
using SteamRip.App.ViewModels;
using SteamRip.App.Views;
using SteamRip.Core.Catalog;
using SteamRip.Core.Data;
using SteamRip.Core.Downloads;
using SteamRip.Core.Hosters;
using SteamRip.Core.Install;
using SteamRip.Core.Platform;
using SteamRip.Core.Solver;
using SteamRip.Core.Updates;

namespace SteamRip.App;

public partial class App : Application
{
    public AppServices Services { get; private set; } = null!;
    private MainWindow? _window;
    private FileSystemWatcher? _showWatcher;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Services = AppServices.Build(desktop.Args ?? []);
            HookCrashLogging(Services.Log);
            Services.Updates.Start();

            var hidden = desktop.Args?.Contains("--hidden") == true || (Services.Settings.StartHidden && desktop.Args?.Contains("--show") != true);
            if (!hidden) ShowWindow();

            var pause = TrayIcon.GetIcons(this)?.FirstOrDefault()?.Menu?.Items.OfType<NativeMenuItem>().FirstOrDefault(i => i.ToggleType == MenuItemToggleType.CheckBox);
            if (pause is not null) pause.IsChecked = Services.Settings.AutoUpdatesPaused;

            WatchShowRequests();
            desktop.ShutdownRequested += async (_, _) => await Services.DisposeAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static void HookCrashLogging(ILogger log)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => log.LogCritical(e.ExceptionObject as Exception, "Unhandled exception (terminating: {Terminating})", e.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, e) => { log.LogError(e.Exception, "Unobserved task exception"); e.SetObserved(); };
        Dispatcher.UIThread.UnhandledException += (_, e) => { log.LogError(e.Exception, "Unhandled exception on the UI thread"); e.Handled = true; };
        // SIGTERM / session logout skip the Avalonia shutdown path; make sure the last lines reach the file.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { log.LogInformation("Process exit"); ((App)Current!).Services.LogFile.Dispose(); };
    }

    public void ShowWindow()
    {
        if (_window is null)
        {
            _window = new MainWindow { DataContext = new MainViewModel(Services) };
            _window.Closing += (_, e) => { e.Cancel = true; _window.Hide(); };
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
            _showWatcher = new FileSystemWatcher(Settings.DefaultDir, ".show") { EnableRaisingEvents = true };
            _showWatcher.Created += (_, _) => Dispatcher.UIThread.Post(() => { try { File.Delete(Program.ShowRequestPath); } catch { } ShowWindow(); });
            _showWatcher.Changed += (_, _) => Dispatcher.UIThread.Post(() => { try { File.Delete(Program.ShowRequestPath); } catch { } ShowWindow(); });
        }
        catch { /* not fatal */ }
    }

    private void TrayIcon_OnClicked(object? sender, EventArgs e) => ShowWindow();
    private void Show_OnClick(object? sender, EventArgs e) => ShowWindow();
    private void CheckNow_OnClick(object? sender, EventArgs e) { Services.Log.LogInformation("Tray: check for updates now"); _ = Services.Updates.SyncCatalogAsync(); }
    private void Pause_OnClick(object? sender, EventArgs e)
    {
        if (sender is NativeMenuItem item)
        {
            Services.Settings.AutoUpdatesPaused = item.IsChecked;
            Services.Log.LogInformation("Tray: auto-updates {State}", item.IsChecked ? "paused" : "resumed");
            Services.Settings.Save();
            if (!item.IsChecked) Services.Updates.CheckForUpdates();
        }
    }
    private void Quit_OnClick(object? sender, EventArgs e)
    {
        Services.Log.LogInformation("Tray: quit");
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
    }
}

/// <summary>Composition root. Everything the UI needs, built once.</summary>
public sealed class AppServices : IAsyncDisposable
{
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
    public required FileLoggerProvider LogFile { get; init; }
    public required ILogger Log { get; init; }

    public static AppServices Build(string[] args)
    {
        var settings = Settings.Load();
        settings.Save(); // materialise defaults on first run so the user can find the file

        var logFile = new FileLoggerProvider(Settings.DefaultLogDir) { MinimumLevel = settings.VerboseLogging || args.Contains("--verbose") ? LogLevel.Debug : LogLevel.Information };
        var loggers = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logFile));
        var log = loggers.CreateLogger("App");
        log.LogInformation("SteamRIP Desktop {Version} starting on {OS} ({Arch}), .NET {Runtime}; args [{Args}]; config {Config}; log level {Level}",
            typeof(App).Assembly.GetName().Version, System.Runtime.InteropServices.RuntimeInformation.OSDescription, System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture,
            Environment.Version, string.Join(' ', args), Settings.DefaultDir, logFile.MinimumLevel);

        var db = new Database(Settings.DefaultDbPath);
        var handler = new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All, PooledConnectionLifetime = TimeSpan.FromMinutes(10), AllowAutoRedirect = false };
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", Downloader.DefaultUserAgent);
        var catalogHttp = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(60) };
        catalogHttp.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", Downloader.DefaultUserAgent);
        var solverHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        var solver = new FlareSolverrClient(solverHttp, () => settings.FlareSolverrUrl, loggers.CreateLogger<FlareSolverrClient>());
        var apiHttp = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(60) };

        var order = settings.HosterOrder.Select(h => Enum.TryParse<Hoster>(h, true, out var v) ? v : Hoster.Unknown).Where(h => h != Hoster.Unknown).ToList();
        var resolvers = new ResolverChain(
        [
            new FileDitchResolver(solver, loggers.CreateLogger<FileDitchResolver>()),
            new GoFileResolver(apiHttp, loggers.CreateLogger<GoFileResolver>()),
            new MegaDbResolver(logger: loggers.CreateLogger<MegaDbResolver>()),
            new BuzzHeavierResolver(solver, loggers.CreateLogger<BuzzHeavierResolver>()),
        ], order.Count > 0 ? order : null, loggers.CreateLogger<ResolverChain>());

        var archive = new ArchiveTool(loggers.CreateLogger<ArchiveTool>());
        log.LogInformation("Archive tools: {Tools}", archive.Describe());
        var notifier = new Notifier(loggers.CreateLogger<Notifier>());
        var updates = new UpdateService(db, settings, new CatalogClient(catalogHttp, logger: loggers.CreateLogger<CatalogClient>()), resolvers,
            new Downloader(http, loggers.CreateLogger<Downloader>()), new Installer(archive, loggers.CreateLogger<Installer>()), notifier, loggers.CreateLogger<UpdateService>());
        var secrets = SecretStore.Create(Settings.DefaultDir);
        log.LogInformation("Secrets backend: {Backend}; catalog has {Posts} posts, {Installs} installs", secrets.Backend, db.PostCount(), db.GetInstalls().Count);
        return new AppServices
        {
            Settings = settings, Db = db, Http = http, Solver = solver, Resolvers = resolvers, Archive = archive, Updates = updates,
            Secrets = secrets, Notifier = notifier, Loggers = loggers, LogFile = logFile, Log = log,
        };
    }

    public async ValueTask DisposeAsync()
    {
        await Updates.DisposeAsync();
        Db.Dispose();
        Log.LogInformation("SteamRIP Desktop stopped");
        Loggers.Dispose();
    }
}
