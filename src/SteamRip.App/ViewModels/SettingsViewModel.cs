using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SteamRip.Core.Platform;

namespace SteamRip.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly AppServices _s;

    [ObservableProperty] private string _libraryRoot;
    [ObservableProperty] private string _downloadsRoot;
    [ObservableProperty] private string _prefixRoots;
    [ObservableProperty] private string _flareSolverrUrl;
    [ObservableProperty] private int _checkIntervalMinutes;
    [ObservableProperty] private bool _notifyNewGames;
    [ObservableProperty] private bool _startHidden;
    [ObservableProperty] private bool _autostart;
    [ObservableProperty] private bool _deleteArchiveAfterInstall;
    [ObservableProperty] private int _keepBackups;
    [ObservableProperty] private bool _verboseLogging;
    [ObservableProperty] private string _hosterOrder;
    [ObservableProperty] private string _saveStatus = "";
    [ObservableProperty] private string _solverTest = "";

    public string ToolsLine => _s.Archive.Describe();
    public string SecretsLine => "Secrets backend: " + _s.Secrets.Backend;
    public string ConfigDir => Core.Data.Settings.DefaultDir;
    public string LogDir => Core.Data.Settings.DefaultLogDir;

    public SettingsViewModel(AppServices s)
    {
        _s = s;
        var c = s.Settings;
        _libraryRoot = c.LibraryRoot; _downloadsRoot = c.DownloadsRoot; _prefixRoots = string.Join("\n", c.PrefixRoots);
        _flareSolverrUrl = c.FlareSolverrUrl; _checkIntervalMinutes = c.CheckIntervalMinutes; _notifyNewGames = c.NotifyNewGames;
        _startHidden = c.StartHidden; _autostart = Core.Platform.Autostart.IsEnabled(); _deleteArchiveAfterInstall = c.DeleteArchiveAfterInstall;
        _keepBackups = c.KeepBackups; _hosterOrder = string.Join(", ", c.HosterOrder); _verboseLogging = c.VerboseLogging;
    }

    [RelayCommand]
    private void Save()
    {
        var c = _s.Settings;
        c.LibraryRoot = LibraryRoot.Trim(); c.DownloadsRoot = DownloadsRoot.Trim();
        c.PrefixRoots = PrefixRoots.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        c.FlareSolverrUrl = FlareSolverrUrl.Trim().TrimEnd('/');
        c.CheckIntervalMinutes = Math.Max(5, CheckIntervalMinutes);
        c.NotifyNewGames = NotifyNewGames; c.StartHidden = StartHidden; c.DeleteArchiveAfterInstall = DeleteArchiveAfterInstall;
        c.KeepBackups = Math.Max(0, KeepBackups);
        c.HosterOrder = HosterOrder.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        c.Autostart = Autostart;
        c.VerboseLogging = VerboseLogging;
        _s.LogFile.MinimumLevel = VerboseLogging ? LogLevel.Debug : LogLevel.Information;
        _s.Log.LogInformation("Settings saved: library {Library}, solver {Solver}, order [{Order}], interval {Interval} min, verbose {Verbose}", c.LibraryRoot, c.FlareSolverrUrl, string.Join(", ", c.HosterOrder), c.CheckIntervalMinutes, c.VerboseLogging);
        try { Core.Platform.Autostart.Set(Autostart); } catch (Exception e) { SaveStatus = "Saved, but autostart failed: " + e.Message; c.Save(); return; }
        c.Save();
        SaveStatus = $"Saved {DateTime.Now:HH:mm:ss}. Hoster order and FlareSolverr URL apply to the next job; library paths apply to new installs.";
    }

    [RelayCommand]
    private async Task TestSolver()
    {
        SolverTest = "Testing…";
        var ok = await _s.Solver.IsHealthyAsync();
        SolverTest = ok ? "FlareSolverr answered. Good." : $"No answer from {_s.Solver.BaseUrl}. Is it running, and is the URL right?";
    }

    [RelayCommand] private Task TestNotification() => _s.Notifier.NotifyAsync("SteamRIP Desktop", "Notifications work.");
    [RelayCommand] private void OpenConfigDir() => LibraryViewModel.OpenPath(ConfigDir);
    [RelayCommand] private void OpenLogDir() => LibraryViewModel.OpenPath(LogDir);
}
