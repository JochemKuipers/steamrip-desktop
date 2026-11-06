using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
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
            Services = AppServices.Build();
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
    private void CheckNow_OnClick(object? sender, EventArgs e) => _ = Services.Updates.SyncCatalogAsync();
    private void Pause_OnClick(object? sender, EventArgs e)
    {
        if (sender is NativeMenuItem item)
        {
            Services.Settings.AutoUpdatesPaused = item.IsChecked;
            Services.Settings.Save();
            if (!item.IsChecked) Services.Updates.CheckForUpdates();
        }
    }
    private void Quit_OnClick(object? sender, EventArgs e)
    {
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

    public static AppServices Build()
    {
        var settings = Settings.Load();
        settings.Save(); // materialise defaults on first run so the user can find the file
        var db = new Database(Settings.DefaultDbPath);
        var handler = new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All, PooledConnectionLifetime = TimeSpan.FromMinutes(10), AllowAutoRedirect = false };
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", Downloader.DefaultUserAgent);
        var catalogHttp = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(60) };
        catalogHttp.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", Downloader.DefaultUserAgent);
        var solverHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        var solver = new FlareSolverrClient(solverHttp, () => settings.FlareSolverrUrl);
        var apiHttp = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(60) };

        var order = settings.HosterOrder.Select(h => Enum.TryParse<Hoster>(h, true, out var v) ? v : Hoster.Unknown).Where(h => h != Hoster.Unknown).ToList();
        var resolvers = new ResolverChain(
        [
            new FileDitchResolver(solver),
            new GoFileResolver(apiHttp),
            new MegaDbResolver(),
            new BuzzHeavierResolver(solver),
        ], order.Count > 0 ? order : null);

        var archive = new ArchiveTool();
        var notifier = new Notifier();
        var updates = new UpdateService(db, settings, new CatalogClient(catalogHttp), resolvers, new Downloader(http), new Installer(archive), notifier);
        return new AppServices
        {
            Settings = settings, Db = db, Http = http, Solver = solver, Resolvers = resolvers, Archive = archive, Updates = updates,
            Secrets = SecretStore.Create(Settings.DefaultDir), Notifier = notifier,
        };
    }

    public async ValueTask DisposeAsync()
    {
        await Updates.DisposeAsync();
        Db.Dispose();
    }
}
