using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SteamRip.App.ViewModels;

public partial class ActivityViewModel : ObservableObject
{
    private readonly AppServices _s;
    public ObservableCollection<ActivityLine> Events { get; } = [];
    public ObservableCollection<string> Live { get; } = [];

    public ActivityViewModel(AppServices s)
    {
        _s = s;
        s.Updates.Log += msg => Dispatcher.UIThread.Post(() =>
        {
            Live.Insert(0, $"{DateTime.Now:HH:mm:ss}  {msg}");
            while (Live.Count > 200) Live.RemoveAt(Live.Count - 1);
        });
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
