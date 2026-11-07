namespace SteamRip.Core.Launch;

/// <summary>
/// Launch tweaks around umu-run: overlays, wrappers and Proton/Wine environment. One record serves two levels:
/// the app-wide defaults in <see cref="Data.Settings.DefaultLaunch"/> and the per-game override in the DB, where
/// <c>null</c> (or an empty string) means "inherit". Resolve with <see cref="MergeOver"/> before composing a launch.
/// Linux only except <see cref="Environment"/> and <see cref="Wrapper"/>; the rest is ignored on Windows.
/// </summary>
public sealed record LaunchOptions(
    bool? MangoHud = null,
    string? MangoHudConfig = null,
    bool? GameMode = null,
    bool? Gamescope = null,
    string? GamescopeArgs = null,
    bool? Hdr = null,
    bool? Wayland = null,
    bool? Fsr = null,
    bool? Fsync = null,
    bool? ProtonLog = null,
    string? DllOverrides = null,
    string? Environment = null,
    string? Wrapper = null
)
{
    /// <summary>What a fresh install gets: gamemode on (harmless when missing), everything else off.</summary>
    public static readonly LaunchOptions AppDefaults = new(
        MangoHud: false,
        GameMode: true,
        Gamescope: false,
        GamescopeArgs: "-f",
        Hdr: false,
        Wayland: false,
        Fsr: false,
        Fsync: true,
        ProtonLog: false
    );

    /// <summary>This record's non-null values win; blanks fall through to <paramref name="defaults"/>.</summary>
    public LaunchOptions MergeOver(LaunchOptions defaults) =>
        new(
            MangoHud ?? defaults.MangoHud,
            Text(MangoHudConfig, defaults.MangoHudConfig),
            GameMode ?? defaults.GameMode,
            Gamescope ?? defaults.Gamescope,
            Text(GamescopeArgs, defaults.GamescopeArgs),
            Hdr ?? defaults.Hdr,
            Wayland ?? defaults.Wayland,
            Fsr ?? defaults.Fsr,
            Fsync ?? defaults.Fsync,
            ProtonLog ?? defaults.ProtonLog,
            Text(DllOverrides, defaults.DllOverrides),
            Text(Environment, defaults.Environment),
            Text(Wrapper, defaults.Wrapper)
        );

    /// <summary>True when nothing is set, i.e. the game inherits everything (stored as NULL).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty =>
        MangoHud is null
        && GameMode is null
        && Gamescope is null
        && Hdr is null
        && Wayland is null
        && Fsr is null
        && Fsync is null
        && ProtonLog is null
        && string.IsNullOrWhiteSpace(MangoHudConfig)
        && string.IsNullOrWhiteSpace(GamescopeArgs)
        && string.IsNullOrWhiteSpace(DllOverrides)
        && string.IsNullOrWhiteSpace(Environment)
        && string.IsNullOrWhiteSpace(Wrapper);

    /// <summary><c>KEY=VALUE</c> per line; blank lines and <c>#</c> comments are skipped, later lines win.</summary>
    public static IEnumerable<KeyValuePair<string, string>> ParseEnvironment(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            yield break;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;
            if (line.StartsWith("export ", StringComparison.Ordinal))
                line = line[7..].TrimStart();
            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            var key = line[..eq].Trim();
            if (key.Any(c => !(char.IsLetterOrDigit(c) || c == '_')))
                continue;
            yield return new KeyValuePair<string, string>(key, line[(eq + 1)..].Trim().Trim('"'));
        }
    }

    private static string? Text(string? mine, string? theirs) =>
        string.IsNullOrWhiteSpace(mine) ? theirs : mine;
}
