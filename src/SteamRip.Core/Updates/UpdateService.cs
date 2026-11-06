using System.Collections.Concurrent;
using SteamRip.Core.Catalog;
using SteamRip.Core.Data;
using SteamRip.Core.Downloads;
using SteamRip.Core.Hosters;
using SteamRip.Core.Install;
using SteamRip.Core.Platform;

namespace SteamRip.Core.Updates;

/// <summary>
/// The single background engine: periodic catalog sync, update detection for installed games, and a one-at-a-time job queue
/// (resolve → download → verify → install → record → notify). The UI only observes and enqueues.
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
    private readonly ConcurrentQueue<Job> _queue = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Job> _jobs = [];
    private Task? _worker, _scheduler;

    public UpdateService(Database db, Settings settings, CatalogClient catalog, ResolverChain resolvers, Downloader downloader, Installer installer, INotifier notifier)
    {
        _db = db; _settings = settings; _catalog = catalog; _resolvers = resolvers; _downloader = downloader; _installer = installer; _notifier = notifier;
    }

    public IReadOnlyList<Job> Jobs { get { lock (_jobs) return _jobs.ToList(); } }
    public Job? Current { get; private set; }
    public DateTime? LastSyncUtc { get; private set; }
    public string LastSyncStatus { get; private set; } = "Never synced";
    public bool Syncing { get; private set; }

    public event Action? JobsChanged;
    public event Action? SyncChanged;
    public event Action<string>? Log;

    public void Start()
    {
        _worker ??= Task.Run(WorkerLoopAsync);
        _scheduler ??= Task.Run(SchedulerLoopAsync);
    }

    // ---- catalog ----

    public async Task<int> SyncCatalogAsync(bool full = false, CancellationToken ct = default)
    {
        if (Syncing) return 0;
        Syncing = true; SyncChanged?.Invoke();
        try
        {
            // Until one full pass has finished, every sync is a full one (a 429 or crash mid-way must not leave holes in the catalog).
            var complete = _db.GetKv("catalog_complete") == "1";
            var since = full || !complete ? null : _db.NewestModified();
            var batch = new List<CatalogPost>();
            var known = new HashSet<long>();
            var newPosts = new List<CatalogPost>();
            var count = 0;
            await foreach (var p in _catalog.GetModifiedSinceAsync(since, ct))
            {
                if (_db.GetPost(p.Id) is null) newPosts.Add(p);
                batch.Add(p); count++;
                if (batch.Count >= 100) { _db.UpsertPosts(batch); batch.Clear(); }
            }
            if (batch.Count > 0) _db.UpsertPosts(batch);
            if (since is null) _db.SetKv("catalog_complete", "1");
            LastSyncUtc = DateTime.UtcNow;
            LastSyncStatus = $"{count} changed post(s), {_db.PostCount()} total";
            _db.LogEvent("sync", LastSyncStatus);
            Emit($"Catalog sync: {LastSyncStatus}");

            if (_settings.NotifyNewGames && since is not null && newPosts.Count > 0)
                await _notifier.NotifyAsync("New on SteamRIP", string.Join("\n", newPosts.Take(5).Select(p => p.Name)) + (newPosts.Count > 5 ? $"\n…and {newPosts.Count - 5} more" : ""));

            CheckForUpdates();
            return count;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            LastSyncStatus = "Sync failed: " + e.Message;
            _db.LogEvent("sync-error", e.Message);
            Emit(LastSyncStatus);
            return 0;
        }
        finally { Syncing = false; SyncChanged?.Invoke(); }
    }

    /// <summary>Compares each installed game with its current post; enqueues updates unless auto-updates are paused or the game opted out.</summary>
    public IReadOnlyList<(InstalledGame Game, CatalogPost Post)> CheckForUpdates()
    {
        var pending = new List<(InstalledGame, CatalogPost)>();
        foreach (var g in _db.GetInstalls())
        {
            if (g.State is InstallState.Downloading or InstallState.Installing or InstallState.Queued) continue;
            var post = _db.GetPost(g.PostId);
            if (post is null || !UpdateDetector.IsUpdate(g, post)) continue;
            pending.Add((g, post));
            if (g.AutoUpdate && !_settings.AutoUpdatesPaused) Enqueue(post, isUpdate: true);
        }
        return pending;
    }

    // ---- queue ----

    public Job Enqueue(CatalogPost post, bool isUpdate, ResolvedDownload? preResolved = null)
    {
        lock (_jobs)
        {
            var existing = _jobs.FirstOrDefault(j => j.Post.Id == post.Id && j.Stage is JobStage.Queued or JobStage.Resolving or JobStage.Downloading or JobStage.Installing);
            if (existing is not null) return existing;
            _jobs.RemoveAll(j => j.Post.Id == post.Id); // drop a stale failed/attention entry for the same game
            var job = new Job(post, isUpdate) { PreResolved = preResolved };
            _jobs.Add(job);
            _queue.Enqueue(job);
            var g = _db.GetInstall(post.Id);
            _db.UpsertInstall(g is null
                ? new InstalledGame(post.Id, post.Slug, post.Name, null, Path.Combine(_settings.LibraryRoot, post.Slug), DateTime.UtcNow, true, InstallState.Queued, null)
                : g with { State = InstallState.Queued, LastError = null });
            _wake.Release();
            JobsChanged?.Invoke();
            return job;
        }
    }

    public void Cancel(Job job)
    {
        job.Cts.Cancel();
        if (job.Stage == JobStage.Queued)
        {
            job.Set(JobStage.Cancelled, "Cancelled");
            RestoreStateAfterFailure(job.Post.Id, null);
            JobsChanged?.Invoke();
        }
    }

    public void ClearFinished()
    {
        lock (_jobs) _jobs.RemoveAll(j => j.Stage is JobStage.Done or JobStage.Failed or JobStage.Cancelled or JobStage.NeedsAttention);
        JobsChanged?.Invoke();
    }

    public void Uninstall(long postId, bool deleteFiles)
    {
        var g = _db.GetInstall(postId);
        if (g is null) return;
        if (deleteFiles && Directory.Exists(g.InstallPath)) Directory.Delete(g.InstallPath, true);
        _db.RemoveInstall(postId);
        _db.LogEvent("uninstall", g.Name, postId);
        JobsChanged?.Invoke();
    }

    private async Task WorkerLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { await _wake.WaitAsync(_cts.Token); } catch (OperationCanceledException) { break; }
            while (_queue.TryDequeue(out var job))
            {
                if (job.Cts.IsCancellationRequested) continue;
                Current = job; JobsChanged?.Invoke();
                await RunJobAsync(job);
                Current = null; JobsChanged?.Invoke();
            }
        }
    }

    private async Task RunJobAsync(Job job)
    {
        var post = job.Post;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, job.Cts.Token);
        var ct = linked.Token;
        var previous = _db.GetInstall(post.Id);
        try
        {
            // Refresh the post so we use the latest mirrors.
            post = _db.GetPost(post.Id) ?? post;

            var archivePath = await DownloadWithRetryAsync(job, post, ct);

            job.Set(JobStage.Installing, "Installing…");
            _db.SetInstallState(post.Id, InstallState.Installing);
            var opt = new InstallOptions(_settings.LibraryRoot, _settings.PrefixRoots, _settings.KeepBackups);
            var result = await _installer.InstallAsync(archivePath, post.Slug, post.SteamAppId, previous?.InstalledVersion, opt,
                new Progress<(string Stage, int Percent)>(p => job.Set(JobStage.Installing, $"{p.Stage} {p.Percent}%", p.Percent / 100.0)), ct);
            if (_settings.DeleteArchiveAfterInstall) { try { File.Delete(archivePath); } catch { } }

            _db.UpsertInstall(new InstalledGame(post.Id, post.Slug, post.Name, UpdateDetector.Normalize(post.VersionKey), result.InstallPath, DateTime.UtcNow,
                previous?.AutoUpdate ?? true, InstallState.Installed, null));
            var what = job.IsUpdate ? $"Updated to {post.VersionKey}" : $"Installed {post.VersionKey}";
            _db.LogEvent(job.IsUpdate ? "update" : "install", $"{post.Name}: {what}" + (result.BackupPath is not null ? $" (saves backed up to {result.BackupPath})" : ""), post.Id);
            job.Set(JobStage.Done, what, 1);
            await _notifier.NotifyAsync(post.Name, what + (result.BackedUpSaves.Count > 0 ? $" — {result.BackedUpSaves.Count} save folder(s) backed up" : ""));
        }
        catch (OperationCanceledException) when (job.Cts.IsCancellationRequested)
        {
            job.Set(JobStage.Cancelled, "Cancelled");
            RestoreStateAfterFailure(post.Id, null, previous);
        }
        catch (NeedsAttentionException e)
        {
            job.Set(JobStage.NeedsAttention, e.Message, error: e.Message);
            _db.SetInstallState(post.Id, InstallState.NeedsAttention, e.Message);
            _db.LogEvent("attention", $"{post.Name}: {e.Message}", post.Id);
            await _notifier.NotifyAsync(post.Name + " needs you", e.Message);
        }
        catch (Exception e)
        {
            job.Set(JobStage.Failed, e.Message, error: e.ToString());
            RestoreStateAfterFailure(post.Id, e.Message, previous);
            _db.LogEvent("error", $"{post.Name}: {e.Message}", post.Id);
            await _notifier.NotifyAsync(post.Name + " failed", e.Message);
        }
    }

    private async Task<string> DownloadWithRetryAsync(Job job, CatalogPost post, CancellationToken ct)
    {
        Directory.CreateDirectory(_settings.DownloadsRoot);
        var attempts = 0;
        while (true)
        {
            attempts++;
            job.Set(JobStage.Resolving, attempts == 1 ? "Resolving download link…" : $"Resolving download link (attempt {attempts})…");
            _db.SetInstallState(post.Id, InstallState.Downloading);
            ResolvedDownload? resolved; IReadOnlyList<string> errors = [];
            if (attempts == 1 && job.PreResolved is not null) resolved = job.PreResolved;
            else (resolved, errors) = await _resolvers.ResolveAsync(post.Mirrors, Emit, ct);
            if (resolved is null)
                throw new NeedsAttentionException("No mirror could be resolved:\n" + string.Join("\n", errors));

            var fileName = Installer.SafeName(resolved.FileName ?? post.Slug + ".rar");
            if (!Path.HasExtension(fileName)) fileName += ".rar";
            var dest = Path.Combine(_settings.DownloadsRoot, post.Slug, fileName);
            if (File.Exists(dest)) return dest; // verified file from an interrupted install

            CheckDiskSpace(resolved.Size, _settings.DownloadsRoot, _settings.LibraryRoot);
            job.Set(JobStage.Downloading, $"Downloading from {resolved.Host}…", 0);
            Emit($"{post.Name}: downloading from {resolved.Host} → {dest}");
            try
            {
                return await _downloader.DownloadAsync(resolved, dest, new Progress<DownloadProgress>(job.Progress), ct);
            }
            catch (DownloadException e) when (e.RetryResolve && attempts < 3)
            {
                Emit($"{post.Name}: {e.Message}; re-resolving");
            }
            catch (HttpRequestException e) when (attempts < 3)
            {
                Emit($"{post.Name}: network error ({e.Message}); retrying in 15 s");
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
            }
            catch (IOException e) when (attempts < 3)
            {
                Emit($"{post.Name}: I/O error ({e.Message}); retrying in 15 s");
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
            }
        }
    }

    private static void CheckDiskSpace(long? archiveSize, string downloadsRoot, string libraryRoot)
    {
        if (archiveSize is not { } size) return;
        Directory.CreateDirectory(downloadsRoot); Directory.CreateDirectory(libraryRoot);
        var dl = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(downloadsRoot))!);
        var lib = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(libraryRoot))!);
        // archive + extracted (~1.3x) if same drive; be conservative.
        var need = dl.Name == lib.Name ? (long)(size * 2.4) : size;
        if (dl.AvailableFreeSpace < need)
            throw new NeedsAttentionException($"Not enough disk space: need ~{Job.Fmt(need)}, have {Job.Fmt(dl.AvailableFreeSpace)} on {dl.Name}");
    }

    private void RestoreStateAfterFailure(long postId, string? error, InstalledGame? previous = null)
    {
        previous ??= _db.GetInstall(postId);
        if (previous is null) return;
        if (previous.InstalledVersion is null && !Directory.Exists(previous.InstallPath))
        {
            if (error is null) _db.RemoveInstall(postId);
            else _db.SetInstallState(postId, InstallState.Failed, error);
        }
        else _db.SetInstallState(postId, error is null ? InstallState.Installed : InstallState.Failed, error);
    }

    private async Task SchedulerLoopAsync()
    {
        // First sync shortly after start; then on the configured interval.
        try { await Task.Delay(TimeSpan.FromSeconds(5), _cts.Token); } catch { return; }
        while (!_cts.IsCancellationRequested)
        {
            try { await SyncCatalogAsync(ct: _cts.Token); } catch (OperationCanceledException) { break; } catch (Exception e) { Emit("Scheduler: " + e.Message); }
            try { await Task.Delay(TimeSpan.FromMinutes(Math.Max(5, _settings.CheckIntervalMinutes)), _cts.Token); } catch { break; }
        }
    }

    private void Emit(string msg) => Log?.Invoke(msg);

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        foreach (var j in Jobs) j.Cts.Cancel();
        if (_worker is not null) try { await _worker; } catch { }
        if (_scheduler is not null) try { await _scheduler; } catch { }
    }
}

/// <summary>The job cannot proceed without the user (no mirror resolvable, disk full, challenge needs a real browser).</summary>
public sealed class NeedsAttentionException(string message) : Exception(message);
