using System.Collections.ObjectModel;
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

    [ObservableProperty]
    public partial string FlareSolverrUrl { get; set; }

    [ObservableProperty]
    public partial int CheckIntervalMinutes { get; set; }

    [ObservableProperty]
    public partial bool NotifyNewGames { get; set; }

    [ObservableProperty]
    public partial bool StartHidden { get; set; }

    [ObservableProperty]
    public partial bool Autostart { get; set; }

    [ObservableProperty]
    public partial bool DeleteArchiveAfterInstall { get; set; }

    [ObservableProperty]
    public partial int KeepBackups { get; set; }

    [ObservableProperty]
    public partial bool VerboseLogging { get; set; }

    [ObservableProperty]
    public partial string HosterOrder { get; set; }

    [ObservableProperty]
    public partial string SaveStatus { get; set; } = "";

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
        LibraryRoot = c.LibraryRoot;
        DownloadsRoot = c.DownloadsRoot;
        Protons.Add(new ProtonBuild("umu default (latest GE-Proton, downloaded by umu)", ""));
        foreach (var p in ProtonLocator.Find())
            Protons.Add(p);
        DefaultProton = Protons.FirstOrDefault(p => p.Path == c.DefaultProtonPath) ?? Protons[0];
        FlareSolverrUrl = c.FlareSolverrUrl;
        CheckIntervalMinutes = c.CheckIntervalMinutes;
        NotifyNewGames = c.NotifyNewGames;
        StartHidden = c.StartHidden;
        Autostart = Core.Platform.Autostart.IsEnabled();
        DeleteArchiveAfterInstall = c.DeleteArchiveAfterInstall;
        KeepBackups = c.KeepBackups;
        HosterOrder = string.Join(", ", c.HosterOrder);
        VerboseLogging = c.VerboseLogging;
    }

    [RelayCommand]
    private void Save()
    {
        var c = _s.Settings;
        c.LibraryRoot = LibraryRoot.Trim();
        c.DownloadsRoot = DownloadsRoot.Trim();
        c.DefaultProtonPath = DefaultProton?.Path ?? "";
        c.FlareSolverrUrl = Core.Solver.FlareSolverrClient.NormalizeUrl(FlareSolverrUrl);
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
        c.VerboseLogging = VerboseLogging;
        _s.LogFile.MinimumLevel = VerboseLogging ? LogLevel.Debug : LogLevel.Information;
        _s.Log.LogInformation(
            "Settings saved: library {Library}, solver {Solver}, order [{Order}], interval {Interval} min, verbose {Verbose}",
            c.LibraryRoot,
            c.FlareSolverrUrl,
            string.Join(", ", c.HosterOrder),
            c.CheckIntervalMinutes,
            c.VerboseLogging
        );
        try
        {
            Core.Platform.Autostart.Set(Autostart);
        }
        catch (Exception e)
        {
            SaveStatus = "Saved, but autostart failed: " + e.Message;
            c.Save();
            return;
        }
        c.Save();
        SaveStatus =
            $"Saved {DateTime.Now:HH:mm:ss}. Hoster order and FlareSolverr URL apply to the next job; library paths apply to new installs.";
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
    private Task TestNotification() =>
        _s.Notifier.NotifyAsync("SteamRIP Desktop", "Notifications work.");

    [RelayCommand]
    private void OpenConfigDir() => LibraryViewModel.OpenPath(ConfigDir);

    [RelayCommand]
    private void OpenLogDir() => LibraryViewModel.OpenPath(LogDir);
}
