using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SteamRip.App.ViewModels;

public partial class ActivityViewModel : ObservableObject
{
    private readonly AppServices _s;
    public ObservableCollection<ActivityLine> Events { get; } = [];
    public ObservableCollection<string> Live { get; } = [];
    public string LogDir { get; private set; } = "";
    [RelayCommand] private void OpenLogDir() => LibraryViewModel.OpenPath(LogDir);

    public ActivityViewModel(AppServices s)
    {
        _s = s;
        s.LogFile.LineWritten += line => Dispatcher.UIThread.Post(() =>
        {
            Live.Insert(0, line);
            while (Live.Count > 300) Live.RemoveAt(Live.Count - 1);
        });
        LogDir = Core.Data.Settings.DefaultLogDir;
        s.Updates.JobsChanged += () => Dispatcher.UIThread.Post(Refresh);
        Refresh();
    }

    public void Refresh()
    {
        Events.Clear();
        foreach (var (at, kind, message) in _s.Db.RecentEvents(100))
            Events.Add(new ActivityLine(at.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), kind, message));
    }
}

public sealed record ActivityLine(string When, string Kind, string Message)
{
    public bool IsError => Kind is "error" or "sync-error" or "attention";
}
