using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamRip.Core.Catalog;
using SteamRip.Core.Updates;

namespace SteamRip.App.ViewModels;

public partial class BrowseViewModel : ObservableObject
{
    private readonly AppServices _s;
    private readonly bool _ready;

    [ObservableProperty]
    public partial string Query { get; set; } = "";

    [ObservableProperty]
    public partial string Summary { get; set; } = "";
    public ObservableCollection<BrowseItem> Items { get; } = [];

    public IReadOnlyList<SortOption> Sorts { get; } =
    [
        new(PostSort.Updated, "Recently updated", "most recently updated"),
        new(PostSort.Popular, "Most popular", "most popular"),
        new(PostSort.Added, "Recently added", "most recently added"),
        new(PostSort.Name, "Name A–Z", "first by name"),
        new(PostSort.SizeDesc, "Largest first", "largest"),
        new(PostSort.SizeAsc, "Smallest first", "smallest"),
    ];

    [ObservableProperty]
    public partial SortOption Sort { get; set; }

    /// <summary>"All genres" (Id 0) followed by the site's categories, largest first.</summary>
    public ObservableCollection<Category> Genres { get; } = [];

    [ObservableProperty]
    public partial Category Genre { get; set; }

    private static readonly Category AllGenres = new(0, "All genres", 0);
    private const int Limit = 150;

    public BrowseViewModel(AppServices s)
    {
        _s = s;
        Sort = Sorts[0];
        Genre = AllGenres;
        RefreshGenres();
        _ready = true;
        Refresh();
    }

    partial void OnQueryChanged(string value) => Refresh();

    partial void OnSortChanged(SortOption value)
    {
        if (_ready)
            Refresh();
    }

    partial void OnGenreChanged(Category value)
    {
        if (_ready)
            Refresh();
    }

    /// <summary>Re-reads the category list after a sync; keeps the current choice when it still exists.</summary>
    public void RefreshGenres()
    {
        var current = Genre.Id;
        var fresh = _s.Db.GetCategories().Where(c => c.Name != "Uncategorized").ToList();
        if (
            Genres.Count == fresh.Count + 1
            && fresh.Select(c => c.Id).SequenceEqual(Genres.Skip(1).Select(c => c.Id))
        )
            return;
        Genres.Clear();
        Genres.Add(AllGenres);
        foreach (var c in fresh)
            Genres.Add(c);
        Genre = Genres.FirstOrDefault(c => c.Id == current) ?? AllGenres;
    }

    public void Refresh()
    {
        var posts = _s.Db.SearchPosts(Query, Sort.Sort, Genre.Id == 0 ? null : Genre.Id, Limit);
        var installed = _s.Db.GetInstalls().ToDictionary(g => g.PostId);
        Items.Clear();
        foreach (var p in posts)
            Items.Add(
                new BrowseItem(
                    this,
                    p,
                    installed.ContainsKey(p.Id),
                    _s.Resolvers.Supported,
                    _s.Covers
                )
            );
        var total = _s.Db.PostCount();
        var filtered = !string.IsNullOrWhiteSpace(Query) || Genre.Id != 0;
        var genreNote =
            Genres.Count == 1 && _s.Updates.Syncing ? " · genres appear when this sync finishes"
            : Genre.Id != 0 && !_s.Db.HasPostCategories()
                ? " · genres are being filled in by the running sync"
            : Sort.Sort == PostSort.Popular && _s.Db.RankedPostCount() == 0
                ? " · popularity arrives with the next sync"
            : "";
        Summary =
            total == 0
                ? "No catalog yet — the first sync runs a few seconds after start, or press Sync now."
            : !filtered ? $"{total:N0} games · showing the {Items.Count} {Sort.Summary}{genreNote}"
            : Items.Count >= Limit
                ? $"Showing the {Limit} {Sort.Summary} matches of {total:N0}{genreNote}"
            : $"{Items.Count} match{(Items.Count == 1 ? "" : "es")} of {total:N0}{genreNote}";
    }

    public void Install(BrowseItem item)
    {
        _s.Updates.Enqueue(item.Post, isUpdate: false);
        Refresh();
    }

    [RelayCommand]
    private async Task Sync() => await _s.Updates.SyncCatalogAsync();

    [RelayCommand]
    private async Task FullSync() => await _s.Updates.SyncCatalogAsync(full: true);
}

/// <summary>One entry of the sort dropdown; <see cref="Summary"/> completes "showing the 150 …".</summary>
public sealed record SortOption(PostSort Sort, string Label, string Summary);

public sealed partial class BrowseItem(
    BrowseViewModel owner,
    CatalogPost post,
    bool installed,
    IReadOnlyCollection<Hoster> supported,
    Services.CoverCache covers
) : ObservableObject
{
    private Bitmap? _cover;
    private bool _coverRequested;

    /// <summary>Fetched on first read, i.e. when the card is actually rendered.</summary>
    public Bitmap? Cover
    {
        get
        {
            if (_coverRequested)
                return _cover;
            _coverRequested = true;
            _ = LoadCoverAsync();
            return _cover;
        }
    }
    public string Initial => post.Name.Length > 0 ? post.Name[..1].ToUpperInvariant() : "?";

    private async Task LoadCoverAsync()
    {
        var bmp = await covers.GetAsync(post.SteamAppId);
        if (bmp is null)
            return;
        _cover = bmp;
        OnPropertyChanged(nameof(Cover));
    }

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
