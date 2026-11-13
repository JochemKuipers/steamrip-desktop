using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SteamRip.Core.Launch;

namespace SteamRip.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly AppServices _s;

    [ObservableProperty]
    public partial string LibraryRoot { get; set; }

    [ObservableProperty]
    public partial string DownloadsRoot { get; set; }

    [ObservableProperty]
    public partial ProtonBuild? DefaultProton { get; set; }
    public ObservableCollection<ProtonBuild> Protons { get; } = [];
    public string LauncherLine => "Runner: " + GameLauncher.Describe();

    /// <summary>The app-wide launch defaults; saved with everything else.</summary>
    public LaunchOptionsEditor Launch { get; }

    [ObservableProperty]
    public partial string CapabilityText { get; set; } = "Checking what this machine supports…";

    partial void OnDefaultProtonChanged(ProtonBuild? value) => Launch.Refresh();

    [ObservableProperty]
    public partial string FlareSolverrUrl { get; set; }

    /// <summary>SteamGridDB API key (secret store); empty clears it.</summary>
    [ObservableProperty]
    public partial string SteamGridDbApiKey { get; set; }

    [ObservableProperty]
    public partial int CheckIntervalMinutes { get; set; }

    [ObservableProperty]
    public partial bool NotifyNewGames { get; set; }

    [ObservableProperty]
    public partial bool StartHidden { get; set; }

    [ObservableProperty]
    public partial bool Autostart { get; set; }

    [ObservableProperty]
    public partial bool ShortcutMenu { get; set; }

    [ObservableProperty]
    public partial bool ShortcutDesktop { get; set; }

    [ObservableProperty]
    public partial bool DeleteArchiveAfterInstall { get; set; }

    [ObservableProperty]
    public partial int KeepBackups { get; set; }

    [ObservableProperty]
    public partial bool VerboseLogging { get; set; }

    [ObservableProperty]
    public partial string HosterOrder { get; set; }

    /// <summary>Fires after an auto-save with the text for the toast.</summary>
    public event Action<string>? Saved;

    private readonly DispatcherTimer _debounce = new()
    {
        Interval = TimeSpan.FromMilliseconds(700),
    };
    private readonly bool _ready; // false while the constructor is still copying values in

    [ObservableProperty]
    public partial string SolverTest { get; set; } = "";

    public string VersionLine => "SteamRIP Desktop " + AppServices.Version;
    public string ToolsLine => _s.Archive.Describe();
    public string SecretsLine => "Secrets backend: " + _s.Secrets.Backend;
    public string ConfigDir => Core.Data.Settings.DefaultDir;
    public string LogDir => Core.Data.Settings.DefaultLogDir;

    public SettingsViewModel(AppServices s)
    {
        _s = s;
        var c = s.Settings;
        Launch = new LaunchOptionsEditor(
            () => null,
            () => s.CapabilitiesNow,
            () => DefaultProton?.Path
        );
        Launch.Load(c.DefaultLaunch.MergeOver(LaunchOptions.AppDefaults));
        _ = ShowCapabilitiesAsync(s.Capabilities);
        LibraryRoot = c.LibraryRoot;
        DownloadsRoot = c.DownloadsRoot;
        foreach (var p in ProtonLocator.Managed.Concat(ProtonLocator.Find()))
            Protons.Add(p);
        var current = ProtonLocator.Effective(c.DefaultProtonPath);
        DefaultProton = Protons.FirstOrDefault(p => p.Path == current) ?? Protons[0];
        FlareSolverrUrl = c.FlareSolverrUrl;
        SteamGridDbApiKey = s.Secrets.Read(Core.Catalog.SteamGridDb.SecretKey) ?? "";
        CheckIntervalMinutes = c.CheckIntervalMinutes;
        NotifyNewGames = c.NotifyNewGames;
        StartHidden = c.StartHidden;
        Autostart = Core.Platform.Autostart.IsEnabled();
        ShortcutMenu = c.ShortcutMenu;
        ShortcutDesktop = c.ShortcutDesktop;
        DeleteArchiveAfterInstall = c.DeleteArchiveAfterInstall;
        KeepBackups = c.KeepBackups;
        HosterOrder = string.Join(", ", c.HosterOrder);
        VerboseLogging = c.VerboseLogging;
        _ready = true;
        Launch.Changed += _ => QueueSave();
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Save();
        };
    }

    /// <summary>Every edit schedules a save; typing in a field just keeps pushing it back.</summary>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_ready || e.PropertyName is nameof(SolverTest) or nameof(CapabilityText))
            return;
        QueueSave();
    }

    /// <summary>Toggling applies to every installed game right away, not just future installs.</summary>
    partial void OnShortcutMenuChanged(bool value)
    {
        if (_ready)
            _s.Updates.SetShortcutsForAll(Core.Platform.ShortcutPlace.Menu, value);
    }

    partial void OnShortcutDesktopChanged(bool value)
    {
        if (_ready)
            _s.Updates.SetShortcutsForAll(Core.Platform.ShortcutPlace.Desktop, value);
    }

    private void QueueSave()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private void Save()
    {
        var c = _s.Settings;
        c.LibraryRoot = LibraryRoot.Trim();
        c.DownloadsRoot = DownloadsRoot.Trim();
        c.DefaultProtonPath = DefaultProton?.Path ?? ProtonLocator.GeProton;
        c.FlareSolverrUrl = Core.Solver.FlareSolverrClient.NormalizeUrl(FlareSolverrUrl);
        var sgdbKey = SteamGridDbApiKey.Trim();
        _s.Secrets.Write(Core.Catalog.SteamGridDb.SecretKey, sgdbKey.Length > 0 ? sgdbKey : null);
        c.CheckIntervalMinutes = Math.Max(5, CheckIntervalMinutes);
        c.NotifyNewGames = NotifyNewGames;
        c.StartHidden = StartHidden;
        c.DeleteArchiveAfterInstall = DeleteArchiveAfterInstall;
        c.KeepBackups = Math.Max(0, KeepBackups);
        c.HosterOrder =
        [
            .. HosterOrder.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            ),
        ];
        c.Autostart = Autostart;
        c.ShortcutMenu = ShortcutMenu;
        c.ShortcutDesktop = ShortcutDesktop;
        c.VerboseLogging = VerboseLogging;
        c.DefaultLaunch = Launch.ToOptions().MergeOver(LaunchOptions.AppDefaults);
        _s.LogFile.MinimumLevel = VerboseLogging ? LogLevel.Debug : LogLevel.Information;
        _s.Log.LogInformation(
            "Settings saved: library {Library}, solver {Solver}, order [{Order}], interval {Interval} min, verbose {Verbose}, launch defaults {Launch}",
            c.LibraryRoot,
            c.FlareSolverrUrl,
            string.Join(", ", c.HosterOrder),
            c.CheckIntervalMinutes,
            c.VerboseLogging,
            c.DefaultLaunch
        );
        try
        {
            if (Autostart != Core.Platform.Autostart.IsEnabled())
                Core.Platform.Autostart.Set(Autostart);
        }
        catch (Exception e)
        {
            c.Save();
            Saved?.Invoke("Settings saved, but autostart failed: " + e.Message);
            return;
        }
        c.Save();
        Saved?.Invoke("Settings saved");
    }

    /// <summary>Tests the URL as typed (not the saved one) and, when it answers, saves it right away so a working address never needs a second click.</summary>
    [RelayCommand]
    private async Task TestSolver()
    {
        var url = Core.Solver.FlareSolverrClient.NormalizeUrl(FlareSolverrUrl);
        if (url.Length == 0)
        {
            SolverTest = "Enter a URL first, e.g. http://192.168.1.10:8191";
            return;
        }
        FlareSolverrUrl = url;
        SolverTest = $"Testing {url}…";
        var ok = await _s.Solver.IsHealthyAsync(url);
        if (ok)
        {
            var changed = _s.Settings.FlareSolverrUrl != url;
            if (changed)
            {
                _s.Settings.FlareSolverrUrl = url;
                _s.Settings.Save();
                _s.Log.LogInformation("FlareSolverr URL set to {Url} (test passed)", url);
            }
            SolverTest = changed ? $"{url} answered. Saved." : $"{url} answered. Good.";
        }
        else
            SolverTest =
                $"No answer from {url} within 5 s. Is it running, is the port right, and does the firewall allow it?";
    }

    [RelayCommand]
    private Task Recheck()
    {
        CapabilityText = "Checking…";
        return ShowCapabilitiesAsync(_s.ProbeCapabilities());
    }

    private async Task ShowCapabilitiesAsync(Task<HostCapabilities> probe)
    {
        var caps = await probe;
        CapabilityText = string.Join('\n', caps.Lines());
        Launch.Refresh();
    }

    [RelayCommand]
    private Task TestNotification() =>
        _s.Notifier.NotifyAsync("SteamRIP Desktop", "Notifications work.");

    [RelayCommand]
    private void OpenConfigDir() => LibraryViewModel.OpenPath(ConfigDir);

    [RelayCommand]
    private void OpenLogDir() => LibraryViewModel.OpenPath(LogDir);
}
