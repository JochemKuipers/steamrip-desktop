using Microsoft.Data.Sqlite;
using SteamRip.Core.Catalog;
using SteamRip.Core.Launch;

namespace SteamRip.Core.Data;

public enum InstallState
{
    // ReSharper disable once UnusedMember.Global -- value 0 is the column default; it is stored as an int, so it must stay
    NotInstalled,
    Queued,
    Downloading,
    Installing,
    Installed,
    Failed,
    NeedsAttention,
}

public sealed record InstalledGame(
    long PostId,
    string Slug,
    string Name,
    string? InstalledVersion,
    string InstallPath,
    DateTime InstalledUtc,
    bool AutoUpdate,
    InstallState State,
    string? LastError,
    string? ExeRelative = null,
    string? ProtonPath = null,
    string? LaunchArgs = null,
    DateTime? LastPlayedUtc = null,
    long PlaytimeSeconds = 0,
    LaunchOptions? Launch = null
);

/// <summary>Hand-written SQLite schema with a migrations table. One file, WAL mode.</summary>
public sealed class Database : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly Lock _lock = new();

    public Database(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _db = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
            }.ToString()
        );
        _db.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;");
        Migrate();
    }

    private static readonly string[] Migrations =
    [
        // 1
        """
            CREATE TABLE posts(
                id INTEGER PRIMARY KEY, slug TEXT NOT NULL, url TEXT NOT NULL, title TEXT NOT NULL, name TEXT NOT NULL,
                title_version TEXT, build TEXT, steam_app_id INTEGER, modified_utc TEXT NOT NULL, published_utc TEXT NOT NULL,
                size_text TEXT, mirrors_json TEXT NOT NULL, seen_utc TEXT NOT NULL);
            CREATE INDEX posts_modified ON posts(modified_utc DESC);
            CREATE INDEX posts_name ON posts(name COLLATE NOCASE);
            CREATE TABLE installs(
                post_id INTEGER PRIMARY KEY REFERENCES posts(id), slug TEXT NOT NULL, name TEXT NOT NULL, installed_version TEXT,
                install_path TEXT NOT NULL, installed_utc TEXT NOT NULL, auto_update INTEGER NOT NULL DEFAULT 1,
                state INTEGER NOT NULL DEFAULT 0, last_error TEXT);
            CREATE TABLE events(id INTEGER PRIMARY KEY AUTOINCREMENT, at_utc TEXT NOT NULL, post_id INTEGER, kind TEXT NOT NULL, message TEXT NOT NULL);
            CREATE TABLE kv(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """,
        // 2: the app launches games itself
        """
            ALTER TABLE installs ADD COLUMN exe_rel TEXT;
            ALTER TABLE installs ADD COLUMN proton TEXT;
            ALTER TABLE installs ADD COLUMN launch_args TEXT;
            ALTER TABLE installs ADD COLUMN last_played_utc TEXT;
            ALTER TABLE installs ADD COLUMN playtime_sec INTEGER NOT NULL DEFAULT 0;
            """,
        // 3: per-game launch tweaks (LaunchOptions as JSON; NULL = inherit the defaults)
        "ALTER TABLE installs ADD COLUMN launch_json TEXT;",
        // 4: numeric size for sorting (backfilled from size_text right after this migration, see Migrate)
        """
            ALTER TABLE posts ADD COLUMN size_bytes INTEGER;
            CREATE INDEX posts_size ON posts(size_bytes);
            """,
        // 5: WordPress categories = genres. Clearing catalog_complete makes the next sync a full one so every post gets its categories.
        """
            CREATE TABLE categories(id INTEGER PRIMARY KEY, name TEXT NOT NULL, count INTEGER NOT NULL);
            CREATE TABLE post_categories(post_id INTEGER NOT NULL, category_id INTEGER NOT NULL, PRIMARY KEY(post_id, category_id));
            CREATE INDEX post_categories_cat ON post_categories(category_id);
            DELETE FROM kv WHERE key='catalog_complete';
            """,
    ];

    /// <summary>Set by <see cref="Migrate"/> when a migration needs C# work afterwards (e.g. 4: parse size_text into size_bytes).</summary>
    private readonly HashSet<int> _applied = [];

    private void Migrate()
    {
        Exec(
            "CREATE TABLE IF NOT EXISTS schema_migrations(version INTEGER PRIMARY KEY, applied_utc TEXT NOT NULL)"
        );
        var current = (long)(
            Scalar("SELECT COALESCE(MAX(version),0) FROM schema_migrations") ?? 0L
        );
        for (var v = (int)current; v < Migrations.Length; v++)
        {
            // ReSharper disable once InconsistentlySynchronizedField -- constructor, nothing else holds the connection yet
            using var tx = _db.BeginTransaction();
            Exec(Migrations[v], tx);
            Exec(
                "INSERT INTO schema_migrations(version, applied_utc) VALUES ($v, $t)",
                tx,
                ("$v", v + 1),
                ("$t", Now())
            );
            tx.Commit();
            _applied.Add(v + 1);
        }
        if (_applied.Contains(4))
            BackfillSizes();
    }

    private void BackfillSizes()
    {
        var rows = Query(
            "SELECT id, size_text FROM posts WHERE size_text IS NOT NULL",
            r => (Id: r.GetInt64(0), Size: r.GetString(1))
        );
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            foreach (var (id, text) in rows)
                Exec(
                    "UPDATE posts SET size_bytes=$b WHERE id=$id",
                    tx,
                    ("$b", CatalogParser.ParseSize(text)),
                    ("$id", id)
                );
            tx.Commit();
        }
    }

    // ---- posts ----

    public void UpsertPosts(IEnumerable<CatalogPost> posts)
    {
        var list = posts as IReadOnlyCollection<CatalogPost> ?? [.. posts];
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            foreach (var p in list)
                Exec(
                    """
                    INSERT INTO posts(id,slug,url,title,name,title_version,build,steam_app_id,modified_utc,published_utc,size_text,mirrors_json,seen_utc,size_bytes)
                    VALUES($id,$slug,$url,$title,$name,$tv,$build,$app,$mod,$pub,$size,$mirrors,$seen,$bytes)
                    ON CONFLICT(id) DO UPDATE SET slug=excluded.slug,url=excluded.url,title=excluded.title,name=excluded.name,title_version=excluded.title_version,
                      build=excluded.build,steam_app_id=excluded.steam_app_id,modified_utc=excluded.modified_utc,published_utc=excluded.published_utc,
                      size_text=excluded.size_text,mirrors_json=excluded.mirrors_json,size_bytes=excluded.size_bytes
                    """,
                    tx,
                    ("$id", p.Id),
                    ("$slug", p.Slug),
                    ("$url", p.Url),
                    ("$title", p.Title),
                    ("$name", p.Name),
                    ("$tv", p.TitleVersion),
                    ("$build", p.Build),
                    ("$app", p.SteamAppId),
                    ("$mod", Iso(p.ModifiedUtc)),
                    ("$pub", Iso(p.PublishedUtc)),
                    ("$size", p.SizeText),
                    ("$mirrors", System.Text.Json.JsonSerializer.Serialize(p.Mirrors)),
                    ("$seen", Now()),
                    ("$bytes", p.SizeBytes ?? CatalogParser.ParseSize(p.SizeText))
                );
            // A post without category ids (old cache, old fixture) must not wipe what a full sync wrote.
            foreach (var p in list.Where(p => p.CategoryIds.Count > 0))
            {
                Exec("DELETE FROM post_categories WHERE post_id=$id", tx, ("$id", p.Id));
                foreach (var c in p.CategoryIds)
                    Exec(
                        "INSERT OR IGNORE INTO post_categories(post_id, category_id) VALUES($p,$c)",
                        tx,
                        ("$p", p.Id),
                        ("$c", c)
                    );
            }
            tx.Commit();
        }
    }

    public void UpsertCategories(IEnumerable<Category> categories)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            foreach (var c in categories)
                Exec(
                    "INSERT INTO categories(id,name,count) VALUES($id,$n,$c) ON CONFLICT(id) DO UPDATE SET name=excluded.name,count=excluded.count",
                    tx,
                    ("$id", c.Id),
                    ("$n", c.Name),
                    ("$c", c.Count)
                );
            tx.Commit();
        }
    }

    /// <summary>Categories by size, largest first.</summary>
    public IReadOnlyList<Category> GetCategories() =>
        Query(
            "SELECT id, name, count FROM categories ORDER BY count DESC, name",
            r => new Category(r.GetInt64(0), r.GetString(1), r.GetInt32(2))
        );

    /// <summary>True once at least one post carries categories, i.e. the backfilling full sync has written something.</summary>
    public bool HasPostCategories() => Scalar("SELECT 1 FROM post_categories LIMIT 1") is not null;

    public CatalogPost? GetPost(long id) =>
        Query("SELECT * FROM posts WHERE id=$id", ReadPost, ("$id", id)).FirstOrDefault();

    public IReadOnlyList<CatalogPost> SearchPosts(
        string? text,
        PostSort sort = PostSort.Updated,
        long? categoryId = null,
        int limit = 200
    )
    {
        var where = new List<string>();
        var args = new List<(string, object?)>();
        if (!string.IsNullOrWhiteSpace(text))
        {
            where.Add("p.name LIKE $q COLLATE NOCASE");
            args.Add(("$q", "%" + text.Trim() + "%"));
        }
        if (categoryId is { } cat)
        {
            where.Add(
                "EXISTS (SELECT 1 FROM post_categories pc WHERE pc.post_id=p.id AND pc.category_id=$cat)"
            );
            args.Add(("$cat", cat));
        }
        var order = sort switch
        {
            PostSort.Added => "p.published_utc DESC",
            PostSort.Name => "p.name COLLATE NOCASE ASC",
            PostSort.SizeDesc => "p.size_bytes DESC NULLS LAST, p.modified_utc DESC",
            PostSort.SizeAsc => "p.size_bytes ASC NULLS LAST, p.modified_utc DESC",
            _ => "p.modified_utc DESC",
        };
        var sql =
            "SELECT p.* FROM posts p"
            + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")
            + $" ORDER BY {order} LIMIT {limit}";
        return Query(sql, ReadPost, [.. args]);
    }

    public int PostCount() => (int)(long)(Scalar("SELECT COUNT(*) FROM posts") ?? 0L);

    public DateTime? NewestModified() =>
        Scalar("SELECT MAX(modified_utc) FROM posts") is string s
            ? DateTime.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind)
            : null;

    private static CatalogPost ReadPost(SqliteDataReader r) =>
        new(
            r.GetInt64(r.GetOrdinal("id")),
            r.GetString(r.GetOrdinal("slug")),
            r.GetString(r.GetOrdinal("url")),
            r.GetString(r.GetOrdinal("title")),
            r.GetString(r.GetOrdinal("name")),
            Str(r, "title_version"),
            Str(r, "build"),
            r.IsDBNull(r.GetOrdinal("steam_app_id"))
                ? null
                : r.GetInt64(r.GetOrdinal("steam_app_id")),
            DateTime.Parse(
                r.GetString(r.GetOrdinal("modified_utc")),
                null,
                System.Globalization.DateTimeStyles.RoundtripKind
            ),
            DateTime.Parse(
                r.GetString(r.GetOrdinal("published_utc")),
                null,
                System.Globalization.DateTimeStyles.RoundtripKind
            ),
            Str(r, "size_text"),
            System.Text.Json.JsonSerializer.Deserialize<List<Mirror>>(
                r.GetString(r.GetOrdinal("mirrors_json"))
            ) ?? [],
            r.IsDBNull(r.GetOrdinal("size_bytes")) ? null : r.GetInt64(r.GetOrdinal("size_bytes"))
        );

    // ---- installs ----

    public void UpsertInstall(InstalledGame g)
    {
        lock (_lock)
            Exec(
                """
                INSERT INTO installs(post_id,slug,name,installed_version,install_path,installed_utc,auto_update,state,last_error,exe_rel,proton,launch_args,last_played_utc,playtime_sec,launch_json)
                VALUES($id,$slug,$name,$ver,$path,$at,$auto,$state,$err,$exe,$proton,$args,$played,$playtime,$launch)
                ON CONFLICT(post_id) DO UPDATE SET slug=excluded.slug,name=excluded.name,installed_version=excluded.installed_version,install_path=excluded.install_path,
                  installed_utc=excluded.installed_utc,auto_update=excluded.auto_update,state=excluded.state,last_error=excluded.last_error,
                  exe_rel=excluded.exe_rel,proton=excluded.proton,launch_args=excluded.launch_args,last_played_utc=excluded.last_played_utc,playtime_sec=excluded.playtime_sec,
                  launch_json=excluded.launch_json
                """,
                null,
                ("$id", g.PostId),
                ("$slug", g.Slug),
                ("$name", g.Name),
                ("$ver", g.InstalledVersion),
                ("$path", g.InstallPath),
                ("$at", Iso(g.InstalledUtc)),
                ("$auto", g.AutoUpdate ? 1 : 0),
                ("$state", (int)g.State),
                ("$err", g.LastError),
                ("$exe", g.ExeRelative),
                ("$proton", g.ProtonPath),
                ("$args", g.LaunchArgs),
                ("$played", g.LastPlayedUtc is { } lp ? Iso(lp) : null),
                ("$playtime", g.PlaytimeSeconds),
                (
                    "$launch",
                    g.Launch is { IsEmpty: false }
                        ? System.Text.Json.JsonSerializer.Serialize(g.Launch)
                        : null
                )
            );
    }

    public void SetInstallState(long postId, InstallState state, string? error = null)
    {
        lock (_lock)
            Exec(
                "UPDATE installs SET state=$s, last_error=$e WHERE post_id=$id",
                null,
                ("$s", (int)state),
                ("$e", error),
                ("$id", postId)
            );
    }

    public void RemoveInstall(long postId)
    {
        lock (_lock)
            Exec("DELETE FROM installs WHERE post_id=$id", null, ("$id", postId));
    }

    public InstalledGame? GetInstall(long postId) =>
        Query("SELECT * FROM installs WHERE post_id=$id", ReadInstall, ("$id", postId))
            .FirstOrDefault();

    public IReadOnlyList<InstalledGame> GetInstalls() =>
        Query("SELECT * FROM installs ORDER BY name COLLATE NOCASE", ReadInstall);

    private static InstalledGame ReadInstall(SqliteDataReader r) =>
        new(
            r.GetInt64(r.GetOrdinal("post_id")),
            r.GetString(r.GetOrdinal("slug")),
            r.GetString(r.GetOrdinal("name")),
            Str(r, "installed_version"),
            r.GetString(r.GetOrdinal("install_path")),
            DateTime.Parse(
                r.GetString(r.GetOrdinal("installed_utc")),
                null,
                System.Globalization.DateTimeStyles.RoundtripKind
            ),
            r.GetInt32(r.GetOrdinal("auto_update")) != 0,
            (InstallState)r.GetInt32(r.GetOrdinal("state")),
            Str(r, "last_error"),
            Str(r, "exe_rel"),
            Str(r, "proton"),
            Str(r, "launch_args"),
            Str(r, "last_played_utc") is { } lp
                ? DateTime.Parse(lp, null, System.Globalization.DateTimeStyles.RoundtripKind)
                : null,
            r.GetInt64(r.GetOrdinal("playtime_sec")),
            Str(r, "launch_json") is { } lj ? ParseLaunch(lj) : null
        );

    private static LaunchOptions? ParseLaunch(string json)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<LaunchOptions>(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null; // hand-edited or from a newer build: inherit the defaults rather than refuse to load
        }
    }

    // ---- events / kv ----

    public void LogEvent(string kind, string message, long? postId = null)
    {
        lock (_lock)
            Exec(
                "INSERT INTO events(at_utc,post_id,kind,message) VALUES($t,$p,$k,$m)",
                null,
                ("$t", Now()),
                ("$p", postId),
                ("$k", kind),
                ("$m", message)
            );
    }

    public IReadOnlyList<(DateTime At, string Kind, string Message)> RecentEvents(
        int limit = 100
    ) =>
        Query(
            $"SELECT at_utc,kind,message FROM events ORDER BY id DESC LIMIT {limit}",
            r =>
                (
                    DateTime.Parse(
                        r.GetString(0),
                        null,
                        System.Globalization.DateTimeStyles.RoundtripKind
                    ),
                    r.GetString(1),
                    r.GetString(2)
                )
        );

    public string? GetKv(string key) =>
        Scalar("SELECT value FROM kv WHERE key=$k", ("$k", key)) as string;

    public void SetKv(string key, string? value)
    {
        lock (_lock)
        {
            if (value is null)
                Exec("DELETE FROM kv WHERE key=$k", null, ("$k", key));
            else
                Exec(
                    "INSERT INTO kv(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
                    null,
                    ("$k", key),
                    ("$v", value)
                );
        }
    }

    // ---- plumbing ----

    private void Exec(string sql, SqliteTransaction? tx = null, params (string, object?)[] args)
    {
        // ReSharper disable once InconsistentlySynchronizedField -- callers hold _lock (or are the constructor)
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (k, v) in args)
            cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private object? Scalar(string sql, params (string, object?)[] args)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (k, v) in args)
                cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
            var r = cmd.ExecuteScalar();
            return r is DBNull ? null : r;
        }
    }

    private List<T> Query<T>(
        string sql,
        Func<SqliteDataReader, T> map,
        params (string, object?)[] args
    )
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (k, v) in args)
                cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
            using var r = cmd.ExecuteReader();
            var list = new List<T>();
            while (r.Read())
                list.Add(map(r));
            return list;
        }
    }

    private static string? Str(SqliteDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    private static string Iso(DateTime dt) =>
        DateTime.SpecifyKind(dt, DateTimeKind.Utc).ToString("o");

    private static string Now() => DateTime.UtcNow.ToString("o");

    public void Dispose() => _db.Dispose();
}
