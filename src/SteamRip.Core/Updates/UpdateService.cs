using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SteamRip.Core.Catalog;
using SteamRip.Core.Data;
using SteamRip.Core.Downloads;
using SteamRip.Core.Hosters;
using SteamRip.Core.Install;
using SteamRip.Core.Launch;
using SteamRip.Core.Platform;

namespace SteamRip.Core.Updates;

/// <summary>
/// The single background engine: periodic catalog sync, update detection for installed games, and a job queue.
/// Resolves are one-at-a-time (FlareSolverr); downloads run in parallel when they use different hosters;
/// installs run freely (each game has its own staging path).
/// </summary>
public sealed class UpdateService : IAsyncDisposable
{
    private readonly Database _db;
    private readonly Settings _settings;
    private readonly CatalogClient _catalog;
    private readonly ResolverChain _resolvers;
    private readonly Downloader _downloader;
    private readonly Installer _installer;
    private readonly INotifier _notifier;
    private readonly GameLauncher _launcher;
    private readonly SteamAppIdResolver? _steamApps;
    private readonly SteamGridDbClient? _steamGridDb;
    private readonly ILogger _log;
    private readonly ConcurrentQueue<Job> _queue = new();
    private readonly ProtonDownloader? _protons;
    private readonly SemaphoreSlim _wake = new(0);
    private readonly SemaphoreSlim _resolveGate = new(1, 1);
    private readonly ConcurrentDictionary<Hoster, SemaphoreSlim> _hostGates = new();
    private readonly object _inFlightLock = new();
    private readonly HashSet<Task> _inFlight = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Job> _jobs = [];

    private Task? _worker,
        _scheduler;

    private SemaphoreSlim HostGate(Hoster host) =>
        _hostGates.GetOrAdd(host, static _ => new SemaphoreSlim(1, 1));

    public UpdateService(
        Database db,
        Settings settings,
        CatalogClient catalog,
        ResolverChain resolvers,
        Downloader downloader,
        Installer installer,
        INotifier notifier,
        GameLauncher launcher,
        ILogger<UpdateService>? logger = null,
        ProtonDownloader? protons = null,
        SteamAppIdResolver? steamApps = null,
        SteamGridDbClient? steamGridDb = null
    )
    {
        _db = db;
        _steamApps = steamApps;
        _steamGridDb = steamGridDb;
        _protons = protons;
        _settings = settings;
        _catalog = catalog;
        _resolvers = resolvers;
        _downloader = downloader;
        _installer = installer;
        _notifier = notifier;
        _launcher = launcher;
        _log = logger ?? NullLogger<UpdateService>.Instance;
        _launcher.Exited += OnGameExited;
    }

    /// <summary>Once a day, re-read the site's /top-games/ ranking (first <see cref="TopGamesPages"/> pages). A failure here never fails the sync.</summary>
    private async Task RefreshPopularityAsync(CancellationToken ct)
    {
        const string key = "top_games_utc";
        if (
            _db.GetKv(key) is { } last
            && DateTime.TryParse(
                last,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var at
            )
            && DateTime.UtcNow - at < TimeSpan.FromHours(24)
            && _db.RankedPostCount() > 0
        )
            return;
        try
        {
            var ids = await _catalog.GetTopGamesAsync(TopGamesPages, ct);
            if (ids.Count == 0)
                return;
            _db.SetPopularity(ids);
            _db.SetKv(key, DateTime.UtcNow.ToString("o"));
            _log.LogInformation("Popularity ranking stored for {Count} posts", ids.Count);
            SyncChanged?.Invoke();
        }
        catch (Exception e)
            when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _log.LogWarning(
                "Top games fetch failed: {Error}; keeping the previous ranking",
                e.Message
            );
        }
    }

    /// <summary>42 pages × 24 = the top ~1,000; about a minute with the politeness delay, once a day.</summary>
    private const int TopGamesPages = 42;

    // ---- play ----

    public async Task PlayAsync(long postId)
    {
        var g = _db.GetInstall(postId) ?? throw new LaunchException("Not installed.");
        if (g.State is not InstallState.Installed)
            throw new LaunchException($"{g.Name} is {g.State}; wait for it to finish.");
        var post = _db.GetPost(postId);
        var path = g.ResolvePath(_settings.LibraryRoot);
        var exe = g.ExeRelative;
        if (exe is null || !File.Exists(Path.Combine(path, exe)))
        {
            exe =
                ExeFinder.Best(path, g.Name)
                ?? throw new LaunchException($"No executable found in {path}.");
            _db.UpsertInstall(g with { ExeRelative = exe });
            _log.LogInformation("{Name}: auto-selected executable {Exe}", g.Name, exe);
        }

        var proton = ProtonLocator.Effective(g.ProtonPath ?? _settings.DefaultProtonPath);
        if (proton == ProtonLocator.DwProton)
        {
            if (_protons is null)
                throw new LaunchException("DW-Proton downloads are not available in this build.");
            proton = await _protons.EnsureDwProtonAsync(_cts.Token);
        }

        var steamAppId = _steamApps is null
            ? post?.SteamAppId
            : await _steamApps.ResolveAsync(post?.SteamAppId, post?.Name ?? g.Name, _cts.Token);
        await _launcher.LaunchAsync(
            new LaunchSpec(
                postId,
                g.Name,
                path,
                exe,
                proton,
                g.LaunchArgs,
                steamAppId,
                EffectiveLaunch(g)
            )
        );
        _db.UpsertInstall((_db.GetInstall(postId) ?? g) with { LastPlayedUtc = DateTime.UtcNow });
        _db.LogEvent("play", g.Name, postId);
        JobsChanged?.Invoke();
    }

    /// <summary>Game override → user defaults → app defaults, so nothing that matters is null.</summary>
    public LaunchOptions EffectiveLaunch(InstalledGame g) =>
        (g.Launch ?? new LaunchOptions())
            .MergeOver(_settings.DefaultLaunch)
            .MergeOver(LaunchOptions.AppDefaults);

    private void OnGameExited(long postId, TimeSpan played, int code)
    {
        var g = _db.GetInstall(postId);
        if (g is not null)
            _db.UpsertInstall(
                g with
                {
                    PlaytimeSeconds = g.PlaytimeSeconds + (long)played.TotalSeconds,
                }
            );
        JobsChanged?.Invoke();
    }

    public IReadOnlyList<Job> Jobs
    {
        get
        {
            lock (_jobs)
                return [.. _jobs];
        }
    }

    public Job? Current { get; private set; }
    public DateTime? LastSyncUtc { get; private set; }
    public string LastSyncStatus { get; private set; } = "Never synced";
    public bool Syncing { get; private set; }

    public event Action? JobsChanged;
    public event Action? SyncChanged;

    public void Start()
    {
        _log.LogInformation(
            "UpdateService starting: library {Library}, downloads {Downloads}, interval {Interval} min, auto-updates {Paused}, FlareSolverr {Solver}",
            _settings.LibraryRoot,
            _settings.DownloadsRoot,
            _settings.CheckIntervalMinutes,
            _settings.AutoUpdatesPaused ? "paused" : "on",
            _settings.FlareSolverrUrl
        );
        _worker ??= Task.Run(WorkerLoopAsync);
        _scheduler ??= Task.Run(SchedulerLoopAsync);
    }

    // ---- catalog ----

    public async Task<int> SyncCatalogAsync(bool full = false, CancellationToken ct = default)
    {
        if (Syncing)
        {
            _log.LogDebug("Sync requested while already syncing; ignored");
            return 0;
        }

        Syncing = true;
        SyncChanged?.Invoke();
        try
        {
            // Until one full pass has finished, every sync is a full one (a 429 or crash mid-way must not leave holes in the catalog).
            var complete = _db.GetKv("catalog_complete") == "1";
            var since = full || !complete ? null : _db.NewestModified();
            if (since is null || _db.GetCategories().Count == 0)
            {
                _db.UpsertCategories(await _catalog.GetCategoriesAsync(ct));
                SyncChanged?.Invoke(); // the genre list can show up before the (long) full pass ends
            }

            var batch = new List<CatalogPost>();
            var newPosts = new List<CatalogPost>();
            var count = 0;
            await foreach (var p in _catalog.GetModifiedSinceAsync(since, ct))
            {
                if (_db.GetPost(p.Id) is null)
                    newPosts.Add(p);
                batch.Add(p);
                count++;
                if (batch.Count < 100)
                    continue;
                _db.UpsertPosts(batch);
                batch.Clear();
            }

            if (batch.Count > 0)
                _db.UpsertPosts(batch);
            if (since is null)
                _db.SetKv("catalog_complete", "1");
            LastSyncUtc = DateTime.UtcNow;
            LastSyncStatus = $"{count} changed post(s), {_db.PostCount()} total";
            _db.LogEvent("sync", LastSyncStatus);
            _log.LogInformation(
                "Catalog sync finished: {Status}; {New} new post(s)",
                LastSyncStatus,
                newPosts.Count
            );

            if (_settings.NotifyNewGames && since is not null && newPosts.Count > 0)
                await _notifier.NotifyAsync(
                    "New on SteamRIP",
                    string.Join("\n", newPosts.Take(5).Select(p => p.Name))
                        + (newPosts.Count > 5 ? $"\n…and {newPosts.Count - 5} more" : "")
                );

            CheckForUpdates();
            await RefreshPopularityAsync(ct);
            return count;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            LastSyncStatus = "Sync failed: " + e.Message;
            _db.LogEvent("sync-error", e.Message);
            _log.LogError(e, "Catalog sync failed");
            return 0;
        }
        finally
        {
            Syncing = false;
            SyncChanged?.Invoke();
        }
    }

    /// <summary>Compares each installed game with its current post; enqueues updates unless auto-updates are paused or the game opted out.</summary>
    public IReadOnlyList<(InstalledGame Game, CatalogPost Post)> CheckForUpdates()
    {
        var pending = new List<(InstalledGame, CatalogPost)>();
        foreach (var g in _db.GetInstalls())
        {
            if (
                g.State
                is InstallState.Downloading
                    or InstallState.Installing
                    or InstallState.Queued
            )
                continue;
            var post = _db.GetPost(g.PostId);
            if (post is null || !UpdateDetector.IsUpdate(g, post))
                continue;
            pending.Add((g, post));
            _log.LogInformation(
                "Update available for {Name}: {Installed} → {Site} (auto-update {Auto}, paused {Paused})",
                g.Name,
                g.InstalledVersion,
                post.VersionKey,
                g.AutoUpdate,
                _settings.AutoUpdatesPaused
            );
            if (g.AutoUpdate && !_settings.AutoUpdatesPaused)
                Enqueue(post, isUpdate: true);
        }

        return pending;
    }

    // ---- queue ----

    public Job Enqueue(CatalogPost post, bool isUpdate, ResolvedDownload? preResolved = null)
    {
        lock (_jobs)
        {
            var existing = _jobs.FirstOrDefault(j =>
                j.Post.Id == post.Id
                && j.Stage
                    is JobStage.Queued
                        or JobStage.Resolving
                        or JobStage.Downloading
                        or JobStage.Installing
            );
            if (existing is not null)
                return existing;
            _jobs.RemoveAll(j => j.Post.Id == post.Id); // drop a stale failed/attention entry for the same game
            var job = new Job(post, isUpdate) { PreResolved = preResolved };
            _jobs.Add(job);
            _queue.Enqueue(job);
            _log.LogInformation(
                "Queued {Kind} of {Name} [{Version}]{Pre}",
                isUpdate ? "update" : "install",
                post.Name,
                post.VersionKey,
                preResolved is null ? "" : " with a pre-resolved link"
            );
            var g = _db.GetInstall(post.Id);
            _db.UpsertInstall(
                g is null
                    ? new InstalledGame(
                        post.Id,
                        post.Slug,
                        post.Name,
                        null,
                        "",
                        DateTime.UtcNow,
                        true,
                        InstallState.Queued,
                        null
                    )
                    : g with
                    {
                        State = InstallState.Queued,
                        LastError = null,
                    }
            );
            _wake.Release();
            JobsChanged?.Invoke();
            return job;
        }
    }

    public void Cancel(Job job)
    {
        _log.LogInformation(
            "Cancel requested for {Name} (stage {Stage})",
            job.Post.Name,
            job.Stage
        );
        job.Cts.Cancel();
        if (job.Stage != JobStage.Queued)
            return;
        job.Set(JobStage.Cancelled, "Cancelled");
        RestoreStateAfterFailure(job.Post.Id, null);
        JobsChanged?.Invoke();
    }

    public void ClearFinished()
    {
        lock (_jobs)
            _jobs.RemoveAll(j =>
                j.Stage
                    is JobStage.Done
                        or JobStage.Failed
                        or JobStage.Cancelled
                        or JobStage.NeedsAttention
            );
        JobsChanged?.Invoke();
    }

    public void Uninstall(long postId, bool deleteFiles)
    {
        var g = _db.GetInstall(postId);
        if (g is null)
            return;
        if (_launcher.IsRunning(postId))
            _launcher.Stop(postId);
        var path = g.ResolvePath(_settings.LibraryRoot);
        _log.LogInformation(
            "Uninstall {Name}: {Path} (delete files: {Delete})",
            g.Name,
            path,
            deleteFiles
        );
        if (deleteFiles && Directory.Exists(path))
            Directory.Delete(path, true);
        _db.RemoveInstall(postId);
        try
        {
            Shortcuts.RemoveAll(postId, g.Name);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("{Name}: could not remove shortcuts: {Error}", g.Name, e.Message);
        }
        _db.LogEvent("uninstall", g.Name, postId);
        JobsChanged?.Invoke();
    }

    // ---- shortcuts ----

    public bool HasShortcut(long postId, ShortcutPlace place) =>
        _db.GetInstall(postId) is { } g && Shortcuts.Exists(postId, g.Name, place);

    /// <summary>Creates or removes one game's shortcut; failures are logged, never thrown.</summary>
    public void SetShortcut(long postId, ShortcutPlace place, bool present) =>
        _ = SetShortcutAsync(postId, place, present);

    /// <summary>Same as <see cref="SetShortcut"/> but awaitable (install path resolves the cover id first).</summary>
    public async Task SetShortcutAsync(long postId, ShortcutPlace place, bool present)
    {
        if (_db.GetInstall(postId) is not { } g)
            return;
        var post = _db.GetPost(postId);
        try
        {
            if (present)
            {
                var id = _steamApps is null
                    ? post?.SteamAppId
                    : await _steamApps.ResolveAsync(
                        post?.SteamAppId,
                        post?.Name ?? g.Name,
                        _cts.Token
                    );
                if (id is > 0 && _steamGridDb is not null)
                    await _steamGridDb.EnsureIconFileAsync(
                        id.Value,
                        Path.Combine(Settings.DefaultDir, "icons"),
                        _cts.Token
                    );
                Shortcuts.Create(postId, g.Name, Shortcuts.GameIcon(id), place);
            }
            else
                Shortcuts.Remove(postId, g.Name, place);
            _log.LogInformation(
                "{Name}: {Action} {Place} shortcut {Path}",
                g.Name,
                present ? "created" : "removed",
                place,
                Shortcuts.PathFor(postId, g.Name, place)
            );
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(
                "{Name}: could not {What}: {Error}",
                g.Name,
                (present ? "create " : "remove ") + place + " shortcut",
                e.Message
            );
        }
    }

    /// <summary>Settings toggle: apply to every installed game at once.</summary>
    public void SetShortcutsForAll(ShortcutPlace place, bool present)
    {
        foreach (var g in _db.GetInstalls().Where(g => g.State == InstallState.Installed))
            SetShortcut(g.PostId, place, present);
    }

    private async Task WorkerLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await _wake.WaitAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            while (_queue.TryDequeue(out var job))
            {
                if (job.Cts.IsCancellationRequested)
                    continue;
                Current = job;
                JobsChanged?.Invoke();
                var task = RunJobAsync(job);
                lock (_inFlightLock)
                    _inFlight.Add(task);
                _ = task.ContinueWith(
                    t =>
                    {
                        lock (_inFlightLock)
                            _inFlight.Remove(t);
                        if (ReferenceEquals(Current, job))
                            Current = null;
                        JobsChanged?.Invoke();
                        // RunJobAsync swallows its own failures; observe the task anyway.
                        _ = t.Exception;
                    },
                    TaskScheduler.Default
                );
            }
        }
    }

    private async Task RunJobAsync(Job job)
    {
        var post = job.Post;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _cts.Token,
            job.Cts.Token
        );
        var ct = linked.Token;
        var previous = _db.GetInstall(post.Id);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _log.LogInformation(
            "=== Job start: {Kind} {Name} [{Version}] (post {Id})",
            job.IsUpdate ? "update" : "install",
            post.Name,
            post.VersionKey,
            post.Id
        );
        try
        {
            // Refresh the post so we use the latest mirrors.
            post = _db.GetPost(post.Id) ?? post;

            var archivePath = await DownloadWithRetryAsync(job, post, ct);

            if (_launcher.IsRunning(post.Id))
            {
                _log.LogInformation(
                    "{Name} is running; waiting for it to exit before installing",
                    post.Name
                );
                job.Set(
                    JobStage.Installing,
                    "Downloaded. Waiting for the game to close before installing…"
                );
                await _launcher.WaitForExitAsync(post.Id, ct);
            }

            job.Set(JobStage.Installing, "Installing…");
            _db.SetInstallState(post.Id, InstallState.Installing);
            var custom = string.IsNullOrWhiteSpace(previous?.InstallPath)
                ? null
                : previous.InstallPath;
            var opt = new InstallOptions(_settings.LibraryRoot, _settings.KeepBackups, custom);
            var result = await _installer.InstallAsync(
                archivePath,
                post.Slug,
                post.SteamAppId,
                previous?.InstalledVersion,
                opt,
                new Progress<(string Stage, int Percent)>(p =>
                    job.Set(JobStage.Installing, $"{p.Stage} {p.Percent}%", p.Percent / 100.0)
                ),
                ct
            );

            if (_settings.DeleteArchiveAfterInstall)
            {
                try
                {
                    File.Delete(archivePath);
                }
                catch
                {
                    // leaving the archive behind is harmless; the next install overwrites it
                }
            }

            var exe =
                previous?.ExeRelative is { } prevExe
                && File.Exists(Path.Combine(result.InstallPath, prevExe))
                    ? prevExe
                    : ExeFinder.Best(result.InstallPath, post.Name);
            _log.LogInformation("{Name}: executable {Exe}", post.Name, exe ?? "none found");
            _db.UpsertInstall(
                new InstalledGame(
                    post.Id,
                    post.Slug,
                    post.Name,
                    UpdateDetector.Normalize(post.VersionKey),
                    previous?.InstallPath ?? "",
                    DateTime.UtcNow,
                    previous?.AutoUpdate ?? true,
                    InstallState.Installed,
                    null,
                    exe,
                    previous?.ProtonPath,
                    previous?.LaunchArgs,
                    previous?.LastPlayedUtc,
                    previous?.PlaytimeSeconds ?? 0,
                    previous?.Launch
                )
            );
            if (_settings.ShortcutMenu)
                await SetShortcutAsync(post.Id, ShortcutPlace.Menu, true);
            if (_settings.ShortcutDesktop)
                await SetShortcutAsync(post.Id, ShortcutPlace.Desktop, true);
            var what = job.IsUpdate
                ? $"Updated to {post.VersionKey}"
                : $"Installed {post.VersionKey}";
            _db.LogEvent(
                job.IsUpdate ? "update" : "install",
                $"{post.Name}: {what}"
                    + (
                        result.BackupPath is not null
                            ? $" (saves backed up to {result.BackupPath})"
                            : ""
                    ),
                post.Id
            );
            job.Set(JobStage.Done, what, 1);
            _log.LogInformation(
                "=== Job done: {Name} {What} in {Seconds:0.0}s → {Path}",
                post.Name,
                what,
                sw.Elapsed.TotalSeconds,
                result.InstallPath
            );
            await _notifier.NotifyAsync(
                post.Name,
                what
                    + (
                        result.BackedUpSaves.Count > 0
                            ? $" — {result.BackedUpSaves.Count} save folder(s) backed up"
                            : ""
                    )
            );
        }
        catch (OperationCanceledException) when (job.Cts.IsCancellationRequested)
        {
            job.Set(JobStage.Cancelled, "Cancelled");
            _log.LogInformation(
                "=== Job cancelled: {Name} after {Seconds:0.0}s",
                post.Name,
                sw.Elapsed.TotalSeconds
            );
            RestoreStateAfterFailure(post.Id, null, previous);
        }
        catch (NeedsAttentionException e)
        {
            job.Set(
                JobStage.NeedsAttention,
                e.Message,
                error: e.Message,
                browserSolve: e.BrowserSolve
            );
            _db.SetInstallState(post.Id, InstallState.NeedsAttention, e.Message);
            _db.LogEvent("attention", $"{post.Name}: {e.Message}", post.Id);
            _log.LogWarning("=== Job needs attention: {Name}: {Message}", post.Name, e.Message);
            await _notifier.NotifyAsync(post.Name + " needs you", e.Message);
        }
        catch (Exception e)
        {
            job.Set(JobStage.Failed, e.Message, error: e.ToString());
            RestoreStateAfterFailure(post.Id, e.Message, previous);
            _db.LogEvent("error", $"{post.Name}: {e.Message}", post.Id);
            _log.LogError(
                e,
                "=== Job failed: {Name} after {Seconds:0.0}s",
                post.Name,
                sw.Elapsed.TotalSeconds
            );
            await _notifier.NotifyAsync(post.Name + " failed", e.Message);
        }
    }

    private async Task<string> DownloadWithRetryAsync(
        Job job,
        CatalogPost post,
        CancellationToken ct
    )
    {
        Directory.CreateDirectory(_settings.DownloadsRoot);
        // A complete, verified archive from an interrupted install can be reused without resolving a link again.
        var slugDir = Path.Combine(_settings.DownloadsRoot, post.Slug);
        if (
            Directory.Exists(slugDir)
            && Directory
                .EnumerateFiles(slugDir)
                .FirstOrDefault(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                is { } existing
        )
        {
            _log.LogInformation("Reusing already-verified archive {Dest}", existing);
            return existing;
        }

        var attempts = 0;
        var skipped = new HashSet<Hoster>();
        // After every other mirror fails, clear skips once and accept a slow host rather than "needs you".
        var allowSlowAbort = true;
        // At least three tries; when hosts are skipped for being slow, allow one attempt per distinct mirror host.
        var maxAttempts = Math.Max(3, post.Mirrors.Select(m => m.Host).Distinct().Count());
        while (true)
        {
            attempts++;
            job.Set(
                JobStage.Resolving,
                attempts == 1
                    ? "Resolving download link…"
                    : $"Resolving download link (attempt {attempts})…"
            );
            _db.SetInstallState(post.Id, InstallState.Downloading);
            ResolvedDownload? resolved;
            IReadOnlyList<string> errors = [];
            if (attempts == 1 && job.PreResolved is not null)
                resolved = job.PreResolved;
            else
            {
                await _resolveGate.WaitAsync(ct);
                try
                {
                    (resolved, errors) = await _resolvers.ResolveAsync(post.Mirrors, skipped, ct);
                }
                finally
                {
                    _resolveGate.Release();
                }
            }
            if (resolved is null && skipped.Count > 0 && allowSlowAbort)
            {
                _log.LogWarning(
                    "{Name}: no other mirror resolved; falling back to skipped host(s) [{Hosts}]",
                    post.Name,
                    string.Join(", ", skipped)
                );
                skipped.Clear();
                allowSlowAbort = false;
                attempts--;
                continue;
            }
            if (resolved is null)
                throw new NeedsAttentionException(
                    "No mirror could be resolved:\n" + string.Join("\n", errors),
                    browserSolve: true
                );

            var fileName = Installer.SafeName(resolved.FileName ?? post.Slug + ".rar");
            if (!Path.HasExtension(fileName))
                fileName += ".rar";
            var dest = Path.Combine(slugDir, fileName);

            CheckDiskSpace(resolved.Size, _settings.DownloadsRoot, _settings.LibraryRoot);
            // One download per hoster: same host waits, a different host runs alongside.
            var hostGate = HostGate(resolved.Host);
            job.Set(JobStage.Downloading, $"Waiting for {resolved.Host}…", 0);
            await hostGate.WaitAsync(ct);
            try
            {
                job.Set(JobStage.Downloading, $"Downloading from {resolved.Host}…", 0);
                return await _downloader.DownloadAsync(
                    resolved,
                    dest,
                    new Progress<DownloadProgress>(job.Progress),
                    ct,
                    switchMirrorIfSlow: allowSlowAbort
                );
            }
            catch (DownloadException e) when (e.RetryResolve && attempts < maxAttempts)
            {
                if (e.SkipHost)
                    skipped.Add(resolved.Host);
                _log.LogWarning(
                    "{Name}: {Error}; re-resolving (attempt {Attempt}/{Max}){Skip}",
                    post.Name,
                    e.Message,
                    attempts + 1,
                    maxAttempts,
                    e.SkipHost ? $", skipping {resolved.Host}" : ""
                );
            }
            catch (HttpRequestException e) when (attempts < maxAttempts)
            {
                _log.LogWarning(
                    e,
                    "{Name}: network error; retrying in 15 s (attempt {Attempt}/{Max})",
                    post.Name,
                    attempts + 1,
                    maxAttempts
                );
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
            }
            catch (IOException e) when (attempts < maxAttempts)
            {
                _log.LogWarning(
                    e,
                    "{Name}: I/O error; retrying in 15 s (attempt {Attempt}/{Max})",
                    post.Name,
                    attempts + 1,
                    maxAttempts
                );
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
            }
            finally
            {
                hostGate.Release();
            }
        }
    }

    private void CheckDiskSpace(long? archiveSize, string downloadsRoot, string libraryRoot)
    {
        if (archiveSize is not { } size)
            return;
        Directory.CreateDirectory(downloadsRoot);
        Directory.CreateDirectory(libraryRoot);
        // Path.GetPathRoot is always "/" on Unix, so DriveInfo must take the real path
        // (statvfs) — otherwise every check reports the boot drive.
        var dlPath = Path.GetFullPath(downloadsRoot);
        var libPath = Path.GetFullPath(libraryRoot);
        var dl = new DriveInfo(dlPath);
        var lib = new DriveInfo(libPath);
        var free = dl.AvailableFreeSpace;
        var sameVolume = OperatingSystem.IsWindows()
            ? string.Equals(
                Path.GetPathRoot(dlPath),
                Path.GetPathRoot(libPath),
                StringComparison.OrdinalIgnoreCase
            )
            : dl.TotalSize == lib.TotalSize && free == lib.AvailableFreeSpace;
        // archive + extracted (~1.3x) if same volume; be conservative.
        var need = sameVolume ? (long)(size * 2.4) : size;
        _log.LogDebug(
            "Disk check: need ~{Need} on {Drive}, free {Free}",
            Job.Fmt(need),
            dlPath,
            Job.Fmt(free)
        );
        if (free < need)
            throw new NeedsAttentionException(
                $"Not enough disk space: need ~{Job.Fmt(need)}, have {Job.Fmt(free)} on {dlPath}"
            );
    }

    private void RestoreStateAfterFailure(
        long postId,
        string? error,
        InstalledGame? previous = null
    )
    {
        previous ??= _db.GetInstall(postId);
        if (previous is null)
            return;
        if (
            previous.InstalledVersion is null
            && !Directory.Exists(previous.ResolvePath(_settings.LibraryRoot))
        )
        {
            if (error is null)
                _db.RemoveInstall(postId);
            else
                _db.SetInstallState(postId, InstallState.Failed, error);
        }
        else
            _db.SetInstallState(
                postId,
                error is null ? InstallState.Installed : InstallState.Failed,
                error
            );
    }

    private async Task SchedulerLoopAsync()
    {
        // First sync shortly after start; then on the configured interval.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), _cts.Token);
        }
        catch
        {
            return;
        }

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await SyncCatalogAsync(ct: _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                _log.LogError(e, "Scheduler tick failed");
            }

            _log.LogDebug(
                "Next catalog check in {Minutes} min",
                Math.Max(5, _settings.CheckIntervalMinutes)
            );
            try
            {
                await Task.Delay(
                    TimeSpan.FromMinutes(Math.Max(5, _settings.CheckIntervalMinutes)),
                    _cts.Token
                );
            }
            catch
            {
                break;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _log.LogInformation("UpdateService stopping");
        _launcher.Exited -= OnGameExited;
        await _cts.CancelAsync();
        foreach (var j in Jobs)
            await j.Cts.CancelAsync();
        Task[] inflight;
        lock (_inFlightLock)
            inflight = [.. _inFlight];
        if (inflight.Length > 0)
            try
            {
                await Task.WhenAll(inflight);
            }
            catch
            {
                // individual jobs already logged; cancellation is expected on shutdown
            }
        if (_worker is not null)
            try
            {
                await _worker;
            }
            catch
            {
                // the worker loop ends with OperationCanceledException by design
            }

        if (_scheduler is not null)
            try
            {
                await _scheduler;
            }
            catch
            {
                // same as above
            }
    }
}

/// <summary>The job cannot proceed without the user (no mirror resolvable, disk full, challenge needs a real browser).</summary>
public sealed class NeedsAttentionException(string message, bool browserSolve = false)
    : Exception(message)
{
    /// <summary>True when a headed FileDitch solve might unblock the job (resolve failed); false for disk space etc.</summary>
    public bool BrowserSolve { get; } = browserSolve;
}
