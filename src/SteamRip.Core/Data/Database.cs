using Microsoft.Data.Sqlite;
using SteamRip.Core.Catalog;

namespace SteamRip.Core.Data;

public enum InstallState
{
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
    long PlaytimeSeconds = 0
);

/// <summary>Hand-written SQLite schema with a migrations table. One file, WAL mode.</summary>
public sealed class Database : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly object _lock = new();

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
    ];

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
            using var tx = _db.BeginTransaction();
            Exec(Migrations[v], tx);
            Exec(
                "INSERT INTO schema_migrations(version, applied_utc) VALUES ($v, $t)",
                tx,
                ("$v", v + 1),
                ("$t", Now())
            );
            tx.Commit();
        }
    }

    // ---- posts ----

    public void UpsertPosts(IEnumerable<CatalogPost> posts)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            foreach (var p in posts)
                Exec(
                    """
                    INSERT INTO posts(id,slug,url,title,name,title_version,build,steam_app_id,modified_utc,published_utc,size_text,mirrors_json,seen_utc)
                    VALUES($id,$slug,$url,$title,$name,$tv,$build,$app,$mod,$pub,$size,$mirrors,$seen)
                    ON CONFLICT(id) DO UPDATE SET slug=excluded.slug,url=excluded.url,title=excluded.title,name=excluded.name,title_version=excluded.title_version,
                      build=excluded.build,steam_app_id=excluded.steam_app_id,modified_utc=excluded.modified_utc,published_utc=excluded.published_utc,
                      size_text=excluded.size_text,mirrors_json=excluded.mirrors_json
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
                    ("$seen", Now())
                );
            tx.Commit();
        }
    }

    public CatalogPost? GetPost(long id) =>
        Query("SELECT * FROM posts WHERE id=$id", ReadPost, ("$id", id)).FirstOrDefault();

    public IReadOnlyList<CatalogPost> SearchPosts(string? text, int limit = 200)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Query($"SELECT * FROM posts ORDER BY modified_utc DESC LIMIT {limit}", ReadPost);
        return Query(
            $"SELECT * FROM posts WHERE name LIKE $q COLLATE NOCASE ORDER BY modified_utc DESC LIMIT {limit}",
            ReadPost,
            ("$q", "%" + text.Trim() + "%")
        );
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
            ) ?? []
        );

    // ---- installs ----

    public void UpsertInstall(InstalledGame g)
    {
        lock (_lock)
            Exec(
                """
                INSERT INTO installs(post_id,slug,name,installed_version,install_path,installed_utc,auto_update,state,last_error,exe_rel,proton,launch_args,last_played_utc,playtime_sec)
                VALUES($id,$slug,$name,$ver,$path,$at,$auto,$state,$err,$exe,$proton,$args,$played,$playtime)
                ON CONFLICT(post_id) DO UPDATE SET slug=excluded.slug,name=excluded.name,installed_version=excluded.installed_version,install_path=excluded.install_path,
                  installed_utc=excluded.installed_utc,auto_update=excluded.auto_update,state=excluded.state,last_error=excluded.last_error,
                  exe_rel=excluded.exe_rel,proton=excluded.proton,launch_args=excluded.launch_args,last_played_utc=excluded.last_played_utc,playtime_sec=excluded.playtime_sec
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
                ("$playtime", g.PlaytimeSeconds)
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
            r.GetInt64(r.GetOrdinal("playtime_sec"))
        );

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
