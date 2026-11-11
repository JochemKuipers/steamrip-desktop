using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SteamRip.Core.Catalog;
using SteamRip.Core.Data;
using SteamRip.Core.Launch;
using SteamRip.Core.Platform;
using SteamRip.Core.Updates;

namespace SteamRip.App.ViewModels;

public partial class LibraryViewModel(AppServices s) : ObservableObject
{
    public ObservableCollection<LibraryItem> Items { get; } = [];
    internal Services.CoverCache Covers => s.Covers;
    public IReadOnlyList<ProtonBuild> Protons { get; } =
    [new("App default", ""), .. ProtonLocator.Managed, .. ProtonLocator.Find()];
    internal LaunchOptions DefaultLaunch =>
        s.Settings.DefaultLaunch.MergeOver(LaunchOptions.AppDefaults);
    internal HostCapabilities? Capabilities => s.CapabilitiesNow;
    internal string DefaultProtonPath => ProtonLocator.Effective(s.Settings.DefaultProtonPath);
    internal string LibraryRoot => s.Settings.LibraryRoot;

    [ObservableProperty]
    public partial int UpdatesAvailable { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; } = true;

    [ObservableProperty]
    public partial string? LastError { get; set; }

    /// <summary>Raised when a card asks for its launch options; the shell shows them in the side sheet.</summary>
    public event Action<LibraryItem>? OptionsRequested;

    internal void RequestOptions(LibraryItem item) => OptionsRequested?.Invoke(item);

    public void Refresh()
    {
        var installs = s.Db.GetInstalls();
        var jobs = s.Updates.Jobs;
        var updates = 0;
        var seen = new HashSet<long>();
        foreach (var g in installs)
        {
            seen.Add(g.PostId);
            var post = s.Db.GetPost(g.PostId);
            var job = jobs.FirstOrDefault(j =>
                j.Post.Id == g.PostId && j.Stage is not (JobStage.Done or JobStage.Cancelled)
            );
            var hasUpdate =
                post is not null
                && g.State == InstallState.Installed
                && UpdateDetector.IsUpdate(g, post);
            if (hasUpdate)
                updates++;
            var running = s.Launcher.IsRunning(g.PostId);
            var existing = Items.FirstOrDefault(i => i.Game.PostId == g.PostId);
            if (existing is null)
                Items.Add(new LibraryItem(this, g, post, job, hasUpdate, running));
            else
                existing.Update(g, post, job, hasUpdate, running);
        }
        foreach (var gone in Items.Where(i => !seen.Contains(i.Game.PostId)).ToList())
            Items.Remove(gone);
        UpdatesAvailable = updates;
        IsEmpty = Items.Count == 0;
    }

    public async Task PlayAsync(LibraryItem item)
    {
        LastError = null;
        try
        {
            await s.Updates.PlayAsync(item.Game.PostId);
        }
        catch (Exception e)
        {
            LastError = $"{item.Name}: {e.Message}";
            s.Log.LogWarning("Play {Name} failed: {Error}", item.Name, e.Message);
        }
        Refresh();
    }

    public void Stop(LibraryItem item)
    {
        s.Launcher.Stop(item.Game.PostId);
        Refresh();
    }

    public void Update(LibraryItem item)
    {
        if (item.Post is not null)
            s.Updates.Enqueue(item.Post, isUpdate: true);
        Refresh();
    }

    public void ToggleAuto(LibraryItem item)
    {
        s.Db.UpsertInstall(item.Game with { AutoUpdate = !item.Game.AutoUpdate });
        Refresh();
    }

    public void ToggleShortcut(LibraryItem item, ShortcutPlace place)
    {
        s.Updates.SetShortcut(
            item.Game.PostId,
            place,
            !s.Updates.HasShortcut(item.Game.PostId, place)
        );
        item.RefreshShortcuts();
    }

    public bool HasShortcut(LibraryItem item, ShortcutPlace place) =>
        s.Updates.HasShortcut(item.Game.PostId, place);

    public void Uninstall(LibraryItem item, bool deleteFiles)
    {
        s.Updates.Uninstall(item.Game.PostId, deleteFiles);
        Refresh();
    }

    public void OpenFolder(LibraryItem item) => OpenPath(item.ResolvedPath);

    public void OpenPrefix(LibraryItem item)
    {
        var p = GameLauncher.PrefixDir(item.ResolvedPath);
        if (Directory.Exists(p))
            OpenPath(p);
        else
            LastError = $"{item.Name}: no prefix yet — it is created on first launch.";
    }

    public void OpenPage(LibraryItem item)
    {
        if (item.Post is not null)
            OpenPath(item.Post.Url);
    }

    public void SetExe(LibraryItem item, string rel)
    {
        s.Db.UpsertInstall(item.Game with { ExeRelative = rel });
        s.Log.LogInformation("{Name}: executable set to {Exe}", item.Name, rel);
        Refresh();
    }

    public void SetInstallPath(LibraryItem item, string? path)
    {
        var custom = string.IsNullOrWhiteSpace(path) ? "" : path.Trim();
        if (custom == item.Game.InstallPath)
            return;
        s.Db.UpsertInstall(item.Game with { InstallPath = custom });
        s.Log.LogInformation(
            "{Name}: install folder set to {Path}",
            item.Name,
            string.IsNullOrEmpty(custom)
                ? $"default ({Path.Combine(LibraryRoot, item.Game.Slug)})"
                : custom
        );
        Refresh();
    }

    public void SetProton(LibraryItem item, ProtonBuild? p)
    {
        s.Db.UpsertInstall(
            item.Game with
            {
                ProtonPath = string.IsNullOrEmpty(p?.Path) ? null : p.Path,
            }
        );
        s.Log.LogInformation("{Name}: Proton set to {Proton}", item.Name, p?.Name ?? "default");
        Refresh();
    }

    public void SetArgs(LibraryItem item, string? args)
    {
        s.Db.UpsertInstall(
            item.Game with
            {
                LaunchArgs = string.IsNullOrWhiteSpace(args) ? null : args.Trim(),
            }
        );
        Refresh();
    }

    public void SetLaunch(LibraryItem item, LaunchOptions o)
    {
        var stored = o.IsEmpty ? null : o;
        if (stored == item.Game.Launch)
            return;
        s.Db.UpsertInstall(item.Game with { Launch = stored });
        s.Log.LogInformation(
            "{Name}: launch tweaks set to {Launch}",
            item.Name,
            stored?.ToString() ?? "inherit"
        );
        Refresh();
    }

    public static void OpenPath(string target)
    {
        try
        {
            // UseShellExecute cannot set Environment on Unix; spawn the opener ourselves and drop the
            // throwaway XDG_* from make run-dev / F5 so Firefox/Dolphin use the real user profile.
            if (OperatingSystem.IsLinux())
            {
                Process.Start(
                    WithoutThrowawayXdg(
                        new ProcessStartInfo("xdg-open", target) { UseShellExecute = false }
                    )
                );
                return;
            }
            if (OperatingSystem.IsMacOS())
            {
                Process.Start(
                    WithoutThrowawayXdg(
                        new ProcessStartInfo("open", target) { UseShellExecute = false }
                    )
                );
                return;
            }
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch
        {
            if (OperatingSystem.IsLinux())
                Process.Start(
                    WithoutThrowawayXdg(
                        new ProcessStartInfo("xdg-open", target) { UseShellExecute = false }
                    )
                );
        }
    }

    private static ProcessStartInfo WithoutThrowawayXdg(ProcessStartInfo psi)
    {
        psi.Environment.Remove("XDG_CONFIG_HOME");
        psi.Environment.Remove("XDG_DATA_HOME");
        return psi;
    }
}

public sealed partial class LibraryItem : ObservableObject
{
    private readonly LibraryViewModel _owner;
    private Job? _job;

    public LibraryItem(
        LibraryViewModel owner,
        InstalledGame game,
        CatalogPost? post,
        Job? job,
        bool hasUpdate,
        bool running
    )
    {
        _owner = owner;
        Game = game;
        Post = post;
        _job = job;
        HasUpdate = hasUpdate;
        IsRunning = running;
        LaunchArgs = game.LaunchArgs ?? "";
        CustomInstallPath = game.InstallPath;
        Launch = new LaunchOptionsEditor(
            () => _owner.DefaultLaunch,
            () => _owner.Capabilities,
            () => Game.ProtonPath ?? _owner.DefaultProtonPath
        );
        Launch.Load(game.Launch);
        Launch.Changed += o => _owner.SetLaunch(this, o);
        RefreshExes();
    }

    /// <summary>Per-game tweaks; null fields inherit the Settings defaults.</summary>
    public LaunchOptionsEditor Launch { get; }

    public void Update(
        InstalledGame game,
        CatalogPost? post,
        Job? job,
        bool hasUpdate,
        bool running
    )
    {
        var exeChanged =
            game.ExeRelative != Game.ExeRelative
            || game.InstallPath != Game.InstallPath
            || game.InstalledVersion != Game.InstalledVersion;
        Game = game;
        Post = post;
        _job = job;
        HasUpdate = hasUpdate;
        IsRunning = running;
        if (exeChanged)
            RefreshExes();
        if (CustomInstallPath != game.InstallPath)
            CustomInstallPath = game.InstallPath;
        if (LaunchArgs != (game.LaunchArgs ?? ""))
            LaunchArgs = game.LaunchArgs ?? "";
        if (
            game.Launch != Launch.ToOptions()
            && !(game.Launch is null && Launch.ToOptions().IsEmpty)
        )
            Launch.Load(game.Launch);
        else
            Launch.Refresh();
        OnPropertyChanged(string.Empty);
    }

    private void RefreshExes()
    {
        Exes.Clear();
        foreach (var c in ExeFinder.Candidates(ResolvedPath, Game.Name))
            Exes.Add(c.RelativePath);
        if (Game.ExeRelative is { } cur && !Exes.Contains(cur))
            Exes.Insert(0, cur);
        _selectedExe = Game.ExeRelative;
        _selectedProton =
            _owner.Protons.FirstOrDefault(p => p.Path == (Game.ProtonPath ?? ""))
            ?? _owner.Protons[0];
        OnPropertyChanged(nameof(SelectedExe));
        OnPropertyChanged(nameof(SelectedProton));
    }

    public InstalledGame Game { get; private set; }
    public CatalogPost? Post { get; private set; }

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
    public string Initial => Game.Name.Length > 0 ? Game.Name[..1].ToUpperInvariant() : "?";

    private async Task LoadCoverAsync()
    {
        var bmp = await _owner.Covers.GetAsync(Post?.SteamAppId, Post?.Name ?? Game.Name);
        if (bmp is null)
            return;
        _cover = bmp;
        OnPropertyChanged(nameof(Cover));
    }

    public string Name => Game.Name;
    public string ResolvedPath => Game.ResolvePath(_owner.LibraryRoot);
    public string DefaultInstallPath => Path.Combine(_owner.LibraryRoot, Game.Slug);
    public string InstalledVersion => Game.InstalledVersion ?? "—";
    public string SiteVersion => UpdateDetector.Normalize(Post?.VersionKey) ?? "—";
    public bool HasUpdate { get; private set; }
    public bool IsRunning { get; private set; }
    public bool IsBusy =>
        _job?.Stage
            is JobStage.Queued
                or JobStage.Resolving
                or JobStage.Downloading
                or JobStage.Installing;
    public bool CanPlay => !IsRunning && !IsBusy && Game.State == InstallState.Installed;
    public bool IsIdle => !IsBusy;
    public bool NeedsAttention => Game.State is InstallState.Failed or InstallState.NeedsAttention;
    public string AutoLabel => Game.AutoUpdate ? "Auto-update on" : "Auto-update off";
    public string DesktopShortcutLabel =>
        _owner.HasShortcut(this, ShortcutPlace.Desktop)
            ? "Remove desktop shortcut"
            : "Add desktop shortcut";
    public string MenuShortcutLabel =>
        _owner.HasShortcut(this, ShortcutPlace.Menu) ? "Remove from app menu" : "Add to app menu";

    internal void RefreshShortcuts()
    {
        OnPropertyChanged(nameof(DesktopShortcutLabel));
        OnPropertyChanged(nameof(MenuShortcutLabel));
    }

    public string Playtime =>
        Game.PlaytimeSeconds switch
        {
            0 => "",
            < 3600 => $"{Game.PlaytimeSeconds / 60} min played",
            var s => $"{s / 3600.0:0.0} h played",
        };
    public string StatusLine =>
        IsRunning ? "Running"
        : _job is not null && IsBusy ? _job.Status
        : NeedsAttention ? Game.LastError?.Split('\n')[0] ?? "Needs attention"
        : HasUpdate
            ? "Update available"
                + (Game.AutoUpdate ? " · installs automatically" : " · auto-update off")
        : Game.State == InstallState.Installed
            ? Game.LastPlayedUtc is { } lp ? $"Last played {MainViewModel.Humanize(lp)}"
                : $"Installed {MainViewModel.Humanize(Game.InstalledUtc)} · never played"
        : Game.State.ToString();
    public string SizeText => Post?.SizeText ?? "";
    public string ExeLine => Game.ExeRelative ?? "No executable chosen";

    public ObservableCollection<string> Exes { get; } = [];
    public IReadOnlyList<ProtonBuild> Protons => _owner.Protons;

    private string? _selectedExe;
    public string? SelectedExe
    {
        get => _selectedExe;
        set
        {
            if (value is null || value == _selectedExe)
                return;
            _selectedExe = value;
            OnPropertyChanged();
            _owner.SetExe(this, value);
        }
    }

    private ProtonBuild? _selectedProton;
    public ProtonBuild? SelectedProton
    {
        get => _selectedProton;
        set
        {
            if (value is null || value == _selectedProton)
                return;
            _selectedProton = value;
            OnPropertyChanged();
            _owner.SetProton(this, value);
        }
    }

    [ObservableProperty]
    public partial string LaunchArgs { get; set; }

    partial void OnLaunchArgsChanged(string value)
    {
        if (value != (Game.LaunchArgs ?? ""))
            _owner.SetArgs(this, value);
    }

    /// <summary>Empty = under the Settings library folder; otherwise an absolute custom install directory.</summary>
    [ObservableProperty]
    public partial string CustomInstallPath { get; set; }

    partial void OnCustomInstallPathChanged(string value)
    {
        if (value != Game.InstallPath)
            _owner.SetInstallPath(this, value);
    }

    [RelayCommand]
    private Task Play() => _owner.PlayAsync(this);

    [RelayCommand]
    private void Stop() => _owner.Stop(this);

    [RelayCommand]
    private void Options() => _owner.RequestOptions(this);

    [RelayCommand]
    private void UpdateNow() => _owner.Update(this);

    [RelayCommand]
    private void ToggleAuto() => _owner.ToggleAuto(this);

    [RelayCommand]
    private void ToggleDesktopShortcut() => _owner.ToggleShortcut(this, ShortcutPlace.Desktop);

    [RelayCommand]
    private void ToggleMenuShortcut() => _owner.ToggleShortcut(this, ShortcutPlace.Menu);

    [RelayCommand]
    private void OpenFolder() => _owner.OpenFolder(this);

    [RelayCommand]
    private void OpenPrefix() => _owner.OpenPrefix(this);

    [RelayCommand]
    private void OpenPage() => _owner.OpenPage(this);

    [RelayCommand]
    private void Forget() => _owner.Uninstall(this, deleteFiles: false);

    [RelayCommand]
    private void Delete() => _owner.Uninstall(this, deleteFiles: true);
}
