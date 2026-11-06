using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamRip.Core.Catalog;
using SteamRip.Core.Updates;

namespace SteamRip.App.ViewModels;

public partial class BrowseViewModel : ObservableObject
{
    private readonly AppServices _s;

    [ObservableProperty]
    private string _query = "";

    [ObservableProperty]
    private string _summary = "";
    public ObservableCollection<BrowseItem> Items { get; } = [];

    public BrowseViewModel(AppServices s)
    {
        _s = s;
        Refresh();
    }

    partial void OnQueryChanged(string value) => Refresh();

    public void Refresh()
    {
        var posts = _s.Db.SearchPosts(Query, 150);
        var installed = _s.Db.GetInstalls().ToDictionary(g => g.PostId);
        Items.Clear();
        foreach (var p in posts)
            Items.Add(new BrowseItem(this, p, installed.ContainsKey(p.Id), _s.Resolvers.Supported));
        var total = _s.Db.PostCount();
        Summary =
            total == 0
                ? "No catalog yet — the first sync runs a few seconds after start, or press Sync now."
            : string.IsNullOrWhiteSpace(Query)
                ? $"{total:N0} games · showing the {Items.Count} most recently updated"
            : $"{Items.Count} match{(Items.Count == 1 ? "" : "es")} of {total:N0}";
    }

    public void Install(BrowseItem item)
    {
        _s.Updates.Enqueue(item.Post, isUpdate: false);
        Refresh();
    }

    [RelayCommand]
    private Task Sync() => _s.Updates.SyncCatalogAsync();

    [RelayCommand]
    private Task FullSync() => _s.Updates.SyncCatalogAsync(full: true);
}

public sealed partial class BrowseItem(
    BrowseViewModel owner,
    CatalogPost post,
    bool installed,
    IReadOnlyCollection<Hoster> supported
) : ObservableObject
{
    public CatalogPost Post => post;
    public string Name => post.Name;
    public string Version => UpdateDetector.Normalize(post.VersionKey) ?? "—";
    public string Size => post.SizeText ?? "";
    public string Updated => MainViewModel.Humanize(post.ModifiedUtc);
    public string Mirrors =>
        string.Join(
            " · ",
            post.Mirrors.Select(m => m.Host)
                .Distinct()
                .Select(h => h == Hoster.Unknown ? "other" : h.ToString())
        );
    public bool Installed => installed;
    public bool CanInstall => !installed && post.Mirrors.Any(m => supported.Contains(m.Host));
    public bool Unsupported => !installed && !CanInstall;

    [RelayCommand]
    private void Install() => owner.Install(this);

    [RelayCommand]
    private void OpenPage() => LibraryViewModel.OpenPath(post.Url);
}
