using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SteamRip.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly AppServices _s;
    private readonly DispatcherTimer _health;
    private readonly DispatcherTimer _runningPoll;

    public LibraryViewModel Library { get; }
    public BrowseViewModel Browse { get; }
    public QueueViewModel Queue { get; }
    public ActivityViewModel Activity { get; }
    public SettingsViewModel Settings { get; }

    [ObservableProperty]
    public partial object Current { get; set; }

    [ObservableProperty]
    public partial string Section { get; set; } = "Library";

    [ObservableProperty]
    public partial string SyncLine { get; set; } = "Never synced";

    [ObservableProperty]
    public partial bool Syncing { get; set; }

    [ObservableProperty]
    public partial bool SolverHealthy { get; set; }

    [ObservableProperty]
    public partial string SolverLine { get; set; } = "FlareSolverr: checking…";

    [ObservableProperty]
    public partial int QueueCount { get; set; }

    [ObservableProperty]
    public partial int AttentionCount { get; set; }

    public MainViewModel(AppServices s)
    {
        _s = s;
        Library = new LibraryViewModel(s);
        Browse = new BrowseViewModel(s);
        Queue = new QueueViewModel(s);
        Activity = new ActivityViewModel(s);
        Settings = new SettingsViewModel(s);
        Current = Library;

        s.Updates.SyncChanged += () => Dispatcher.UIThread.Post(RefreshSync);
        s.Updates.JobsChanged += () => Dispatcher.UIThread.Post(RefreshCounts);
        RefreshSync();
        RefreshCounts();

        _health = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _health.Tick += async (_, _) => await CheckSolverAsync();
        _health.Start();
        _runningPoll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _runningPoll.Tick += (_, _) =>
        {
            Library.Refresh();
            if (_s.Launcher.RunningIds.Count == 0)
                _runningPoll.Stop();
        };
        _ = CheckSolverAsync();
    }

    [RelayCommand]
    private void Navigate(string section)
    {
        Section = section;
        Current = section switch
        {
            "Browse" => Browse,
            "Queue" => Queue,
            "Activity" => Activity,
            "Settings" => Settings,
            _ => Library,
        };
        switch (Current)
        {
            case LibraryViewModel l:
                l.Refresh();
                break;
            case ActivityViewModel a:
                a.Refresh();
                break;
            case BrowseViewModel b:
                b.Refresh();
                break;
        }
    }

    [RelayCommand]
    private async Task SyncNow() => await _s.Updates.SyncCatalogAsync();

    private void RefreshSync()
    {
        Syncing = _s.Updates.Syncing;
        SyncLine =
            Syncing ? "Syncing catalog…"
            : _s.Updates.LastSyncUtc is { } t
                ? $"Synced {Humanize(t)} · {_s.Updates.LastSyncStatus}"
            : _s.Updates.LastSyncStatus;
        Library.Refresh();
        Browse.Refresh();
    }

    private void RefreshCounts()
    {
        var jobs = _s.Updates.Jobs;
        QueueCount = jobs.Count(j =>
            j.Stage
                is Core.Updates.JobStage.Queued
                    or Core.Updates.JobStage.Resolving
                    or Core.Updates.JobStage.Downloading
                    or Core.Updates.JobStage.Installing
        );
        AttentionCount = jobs.Count(j =>
            j.Stage is Core.Updates.JobStage.NeedsAttention or Core.Updates.JobStage.Failed
        );
        Library.Refresh();
        if (_s.Launcher.RunningIds.Count > 0 && !_runningPoll.IsEnabled)
            _runningPoll.Start();
    }

    private async Task CheckSolverAsync()
    {
        var ok = await _s.Solver.IsHealthyAsync();
        SolverHealthy = ok;
        SolverLine = ok
            ? $"FlareSolverr reachable at {_s.Solver.BaseUrl}"
            : $"FlareSolverr unreachable at {_s.Solver.BaseUrl} — FileDitch and BuzzHeavier mirrors will fail";
    }

    public static string Humanize(DateTime utc)
    {
        var d = DateTime.UtcNow - utc;
        return d.TotalSeconds < 60 ? "just now"
            : d.TotalMinutes < 60 ? $"{(int)d.TotalMinutes} min ago"
            : d.TotalHours < 24 ? $"{(int)d.TotalHours} h ago"
            : $"{(int)d.TotalDays} d ago";
    }
}
