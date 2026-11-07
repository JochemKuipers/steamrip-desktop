using System.Text.RegularExpressions;

namespace SteamRip.Core.Launch;

public sealed record ProtonBuild(string Name, string Path)
{
    public override string ToString() => Name;
}

/// <summary>Finds Proton builds in the places Steam, Heroic, Lutris, umu and distro packages put them.</summary>
public static partial class ProtonLocator
{
    [GeneratedRegex("\"display_name\"\\s+\"([^\"]+)\"")]
    private static partial Regex DisplayName();

    /// <summary>umu keyword: it downloads the latest GE-Proton release itself and keeps using it. The app default.</summary>
    public const string GeProton = "GE-Proton";

    /// <summary>App keyword: <see cref="ProtonDownloader"/> fetches the latest DW-Proton (Dawn Winery) release before launch.</summary>
    public const string DwProton = "DW-Proton";

    /// <summary>Builds that are not directories but "latest of" keywords, shown first in every Proton list.</summary>
    public static readonly IReadOnlyList<ProtonBuild> Managed =
    [
        new("GE-Proton (latest, downloaded and updated by umu)", GeProton),
        new("DW-Proton (latest, downloaded and updated by the app)", DwProton),
    ];

    public static bool IsKeyword(string? path) => path is GeProton or DwProton;

    /// <summary>What the launcher will actually use for a stored value: empty/null falls back to GE-Proton, never UMU-Proton.</summary>
    public static string Effective(string? stored) =>
        string.IsNullOrEmpty(stored) ? GeProton : stored;

    /// <summary>Where umu (and we) put downloaded builds; the only place keyword builds are resolved from.</summary>
    public static string DownloadDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local",
            "share",
            "Steam",
            "compatibilitytools.d"
        );

    /// <summary>The newest installed directory a keyword currently maps to (<c>GE-Proton11-3</c>, <c>dwproton-11.0-16</c>), or null before the first download.</summary>
    public static string? ResolveInstalled(string? keyword, string? dir = null) =>
        keyword switch
        {
            GeProton => Newest(dir ?? DownloadDir, "GE-Proton"),
            DwProton => Newest(dir ?? DownloadDir, "dwproton-"),
            _ => null,
        };

    private static string? Newest(string dir, string prefix) =>
        Directory.Exists(dir)
            ? Directory
                .EnumerateDirectories(dir)
                .Where(d =>
                    Path.GetFileName(d).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(Path.Combine(d, "proton"))
                )
                .OrderByDescending(d => VersionKey(Path.GetFileName(d)), VersionComparer.Instance)
                .FirstOrDefault()
            : null;

    /// <summary>Digits in order of appearance, so <c>GE-Proton11-3</c> &gt; <c>GE-Proton9-27</c> and <c>dwproton-11.0-16</c> &gt; <c>dwproton-11.0-9</c>.</summary>
    public static int[] VersionKey(string name) =>
        [.. Digits().Matches(name).Select(m => int.Parse(m.Value))];

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();

    private sealed class VersionComparer : IComparer<int[]>
    {
        public static readonly VersionComparer Instance = new();

        public int Compare(int[]? x, int[]? y)
        {
            x ??= [];
            y ??= [];
            for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
            {
                var c = (i < x.Length ? x[i] : 0).CompareTo(i < y.Length ? y[i] : 0);
                if (c != 0)
                    return c;
            }
            return 0;
        }
    }

    public static IReadOnlyList<ProtonBuild> Find()
    {
        if (!OperatingSystem.IsLinux())
            return [];
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] roots =
        [
            "/usr/share/steam/compatibilitytools.d",
            Path.Combine(home, ".steam", "root", "compatibilitytools.d"),
            Path.Combine(home, ".steam", "steam", "compatibilitytools.d"),
            Path.Combine(home, ".local", "share", "Steam", "compatibilitytools.d"),
            Path.Combine(home, ".local", "share", "Steam", "steamapps", "common"),
            Path.Combine(home, ".steam", "steam", "steamapps", "common"),
            Path.Combine(home, ".config", "heroic", "tools", "proton"),
            Path.Combine(home, ".local", "share", "lutris", "runners", "proton"),
            Path.Combine(home, ".local", "share", "umu", "compatibilitytools"),
            Path.Combine(home, ".local", "share", "Steam", "compatibilitytools.d"),
        ];
        var seen = new HashSet<string>();
        var found = new List<ProtonBuild>();
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                if (!File.Exists(Path.Combine(dir, "proton")))
                    continue;
                var vdf = Path.Combine(dir, "compatibilitytool.vdf");
                var name =
                    File.Exists(vdf)
                    && DisplayName().Match(File.ReadAllText(vdf)) is { Success: true } m
                        ? m.Groups[1].Value
                        : Path.GetFileName(dir);
                // ~/.steam/root and ~/.steam/steam are symlinks to the same place; identify a build by name + version file, not by path.
                var versionFile = Path.Combine(dir, "version");
                var identity =
                    name
                    + "|"
                    + (
                        File.Exists(versionFile)
                            ? File.ReadAllText(versionFile).Trim()
                            : new FileInfo(Path.Combine(dir, "proton")).Length.ToString()
                    );
                if (!seen.Add(identity))
                    continue;
                found.Add(new ProtonBuild(name, dir));
            }
        }
        // Preference: CachyOS / GE builds first (they carry the game fixes), then Steam's own, then anything else.
        return
        [
            .. found
                .OrderBy(p =>
                    p.Name.Contains("cachyos", StringComparison.OrdinalIgnoreCase) ? 0
                    : p.Name.Contains("GE", StringComparison.Ordinal) ? 1
                    : p.Name.Contains("Experimental", StringComparison.OrdinalIgnoreCase) ? 2
                    : 3
                )
                .ThenByDescending(p => p.Name),
        ];
    }
}
