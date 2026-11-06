using System.Text.Json;
using System.Text.Json.Serialization;

namespace SteamRip.Core.Data;

/// <summary>Plain JSON settings next to the database. Secrets never go here (see <see cref="Platform.ISecretStore"/>).</summary>
public sealed class Settings
{
    public string LibraryRoot { get; set; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Games",
            "SteamRIP"
        );
    public string DownloadsRoot { get; set; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Games",
            "SteamRIP",
            ".downloads"
        );

    /// <summary>Proton build dir used for new games; empty = first one <see cref="Launch.ProtonLocator"/> finds (umu's default if none).</summary>
    public string DefaultProtonPath { get; set; } = "";
    public string FlareSolverrUrl { get; set; } = "http://localhost:8191";
    public int CheckIntervalMinutes { get; set; } = 60;
    public bool AutoUpdatesPaused { get; set; }
    public bool NotifyNewGames { get; set; }
    public bool StartHidden { get; set; } = true;
    public bool Autostart { get; set; }
    public bool DeleteArchiveAfterInstall { get; set; } = true;
    public int KeepBackups { get; set; } = 2;
    public bool VerboseLogging { get; set; }
    public List<string> HosterOrder { get; set; } =
    ["FileDitch", "GoFile", "MegaDb", "BuzzHeavier"];

    public static string DefaultDir =>
        Path.Combine(
            OperatingSystem.IsWindows()
                    ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
                : Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".config"
                ),
            "steamrip-desktop"
        );

    public static string DefaultPath => Path.Combine(DefaultDir, "settings.json");
    public static string DefaultDbPath => Path.Combine(DefaultDir, "steamrip.db");
    public static string DefaultLogDir => Path.Combine(DefaultDir, "logs");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static Settings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Json)
                    ?? new Settings();
        }
        catch
        { /* corrupt settings → defaults; the user can fix in UI */
        }
        return new Settings();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }
}
