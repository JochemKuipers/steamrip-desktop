using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamRip.Core.Updates;

namespace SteamRip.App.ViewModels;

public partial class QueueViewModel : ObservableObject
{
    private readonly AppServices _s;
    public ObservableCollection<JobItem> Items { get; } = [];
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private bool _paused;

    public QueueViewModel(AppServices s)
    {
        _s = s;
        Paused = s.Settings.AutoUpdatesPaused;
        s.Updates.JobsChanged += () => Dispatcher.UIThread.Post(Rebuild);
        Rebuild();
    }

    private void Rebuild()
    {
        var jobs = _s.Updates.Jobs;
        foreach (var stale in Items.Where(i => !jobs.Contains(i.Job)).ToList()) Items.Remove(stale);
        foreach (var j in jobs.Where(j => Items.All(i => i.Job != j))) Items.Add(new JobItem(this, j));
        IsEmpty = Items.Count == 0;
    }

    partial void OnPausedChanged(bool value)
    {
        _s.Settings.AutoUpdatesPaused = value; _s.Settings.Save();
        if (!value) _s.Updates.CheckForUpdates();
    }

    public void Cancel(JobItem item) => _s.Updates.Cancel(item.Job);
    public void Retry(JobItem item) => _s.Updates.Enqueue(item.Job.Post, item.Job.IsUpdate);

    public async Task SolveInBrowserAsync(JobItem item)
    {
        item.SetBusy("Opening browser… pass the check in the window that appears.");
        try
        {
            var resolved = await ManualSolve.ViaBrowserAsync(item.Job.Post);
            _s.Updates.Enqueue(item.Job.Post, item.Job.IsUpdate, resolved);
        }
        catch (Exception e) { item.SetBusy("Browser fallback failed: " + e.Message); }
    }

    [RelayCommand] private void ClearFinished() => _s.Updates.ClearFinished();
}

public sealed partial class JobItem : ObservableObject
{
    private readonly QueueViewModel _owner;
    public Job Job { get; }
    [ObservableProperty] private string _status;
    [ObservableProperty] private double _fraction;
    [ObservableProperty] private bool _indeterminate;
    [ObservableProperty] private bool _active;
    [ObservableProperty] private bool _needsAttention;
    [ObservableProperty] private bool _failed;
    [ObservableProperty] private bool _done;
    [ObservableProperty] private string? _error;

    public string Name => Job.Post.Name;
    public string Kind => Job.IsUpdate ? "Update" : "Install";
    public string Version => UpdateDetector.Normalize(Job.Post.VersionKey) ?? "";
    public bool CanSolveInBrowser => Job.Post.Mirrors.Any(m => m.Host == Core.Catalog.Hoster.FileDitch);

    public JobItem(QueueViewModel owner, Job job)
    {
        _owner = owner; Job = job; _status = job.Status;
        job.Changed += _ => Dispatcher.UIThread.Post(Sync);
        Sync();
    }

    private void Sync()
    {
        Status = Job.Status;
        Fraction = Job.Fraction ?? 0;
        Active = Job.Stage is JobStage.Queued or JobStage.Resolving or JobStage.Downloading or JobStage.Installing;
        Indeterminate = Active && Job.Fraction is null;
        NeedsAttention = Job.Stage == JobStage.NeedsAttention;
        Failed = Job.Stage is JobStage.Failed or JobStage.Cancelled;
        Done = Job.Stage == JobStage.Done;
        Error = Job.Error;
    }

    public void SetBusy(string text) { Status = text; }

    [RelayCommand] private void Cancel() => _owner.Cancel(this);
    [RelayCommand] private void Retry() => _owner.Retry(this);
    [RelayCommand] private Task SolveInBrowser() => _owner.SolveInBrowserAsync(this);
}
