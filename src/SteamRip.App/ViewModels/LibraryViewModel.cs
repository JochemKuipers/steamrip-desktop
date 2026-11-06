using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamRip.Core.Catalog;
using SteamRip.Core.Data;
using SteamRip.Core.Updates;

namespace SteamRip.App.ViewModels;

public partial class LibraryViewModel(AppServices s) : ObservableObject
{
    public ObservableCollection<LibraryItem> Items { get; } = [];
    [ObservableProperty] private int _updatesAvailable;
    [ObservableProperty] private bool _isEmpty = true;

    public void Refresh()
    {
        var installs = s.Db.GetInstalls();
        var jobs = s.Updates.Jobs;
        Items.Clear();
        var updates = 0;
        foreach (var g in installs)
        {
            var post = s.Db.GetPost(g.PostId);
            var job = jobs.FirstOrDefault(j => j.Post.Id == g.PostId && j.Stage is not (JobStage.Done or JobStage.Cancelled));
            var hasUpdate = post is not null && g.State == InstallState.Installed && UpdateDetector.IsUpdate(g, post);
            if (hasUpdate) updates++;
            Items.Add(new LibraryItem(this, g, post, job, hasUpdate));
        }
        UpdatesAvailable = updates;
        IsEmpty = Items.Count == 0;
    }

    public void Update(LibraryItem item) { if (item.Post is not null) s.Updates.Enqueue(item.Post, isUpdate: true); Refresh(); }
    public void ToggleAuto(LibraryItem item) { s.Db.UpsertInstall(item.Game with { AutoUpdate = !item.Game.AutoUpdate }); Refresh(); }
    public void Uninstall(LibraryItem item, bool deleteFiles) { s.Updates.Uninstall(item.Game.PostId, deleteFiles); Refresh(); }
    public void OpenFolder(LibraryItem item) => OpenPath(item.Game.InstallPath);
    public void OpenPage(LibraryItem item) { if (item.Post is not null) OpenPath(item.Post.Url); }

    public static void OpenPath(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch { if (OperatingSystem.IsLinux()) Process.Start("xdg-open", target); }
    }
}

public sealed partial class LibraryItem(LibraryViewModel owner, InstalledGame game, CatalogPost? post, Job? job, bool hasUpdate) : ObservableObject
{
    public InstalledGame Game => game;
    public CatalogPost? Post => post;
    public string Name => game.Name;
    public string InstalledVersion => game.InstalledVersion ?? "—";
    public string SiteVersion => UpdateDetector.Normalize(post?.VersionKey) ?? "—";
    public bool HasUpdate => hasUpdate;
    public bool IsBusy => job is not null && job.Stage is JobStage.Queued or JobStage.Resolving or JobStage.Downloading or JobStage.Installing;
    public bool IsIdle => !IsBusy;
    public bool NeedsAttention => game.State is InstallState.Failed or InstallState.NeedsAttention;
    public bool AutoUpdate => game.AutoUpdate;
    public string AutoLabel => game.AutoUpdate ? "Auto-update on" : "Auto-update off";
    public string StatusLine => job is not null && IsBusy ? job.Status
        : NeedsAttention ? (game.LastError?.Split('\n')[0] ?? "Needs attention")
        : hasUpdate ? "Update available" + (game.AutoUpdate ? " · installs automatically" : " · auto-update off")
        : game.State == InstallState.Installed ? $"Installed {MainViewModel.Humanize(game.InstalledUtc)}" : game.State.ToString();
    public string InstallPath => game.InstallPath;
    public string SizeText => post?.SizeText ?? "";

    [RelayCommand] private void UpdateNow() => owner.Update(this);
    [RelayCommand] private void ToggleAuto() => owner.ToggleAuto(this);
    [RelayCommand] private void OpenFolder() => owner.OpenFolder(this);
    [RelayCommand] private void OpenPage() => owner.OpenPage(this);
    [RelayCommand] private void Forget() => owner.Uninstall(this, deleteFiles: false);
    [RelayCommand] private void Delete() => owner.Uninstall(this, deleteFiles: true);
}
