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

    public static IReadOnlyList<ProtonBuild> Find()
    {
        if (!OperatingSystem.IsLinux())
            return [];
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] roots =
        [
            "/usr/share/steam/compatibilitytools.d",
            System.IO.Path.Combine(home, ".steam", "root", "compatibilitytools.d"),
            System.IO.Path.Combine(home, ".steam", "steam", "compatibilitytools.d"),
            System.IO.Path.Combine(home, ".local", "share", "Steam", "compatibilitytools.d"),
            System.IO.Path.Combine(home, ".local", "share", "Steam", "steamapps", "common"),
            System.IO.Path.Combine(home, ".steam", "steam", "steamapps", "common"),
            System.IO.Path.Combine(home, ".config", "heroic", "tools", "proton"),
            System.IO.Path.Combine(home, ".local", "share", "lutris", "runners", "proton"),
            System.IO.Path.Combine(home, ".local", "share", "umu", "compatibilitytools"),
            System.IO.Path.Combine(home, ".local", "share", "Steam", "compatibilitytools.d"),
        ];
        var seen = new HashSet<string>();
        var found = new List<ProtonBuild>();
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                if (!File.Exists(System.IO.Path.Combine(dir, "proton")))
                    continue;
                var vdf = System.IO.Path.Combine(dir, "compatibilitytool.vdf");
                var name =
                    File.Exists(vdf)
                    && DisplayName().Match(File.ReadAllText(vdf)) is { Success: true } m
                        ? m.Groups[1].Value
                        : System.IO.Path.GetFileName(dir);
                // ~/.steam/root and ~/.steam/steam are symlinks to the same place; identify a build by name + version file, not by path.
                var versionFile = System.IO.Path.Combine(dir, "version");
                var identity =
                    name
                    + "|"
                    + (
                        File.Exists(versionFile)
                            ? File.ReadAllText(versionFile).Trim()
                            : new FileInfo(System.IO.Path.Combine(dir, "proton")).Length.ToString()
                    );
                if (!seen.Add(identity))
                    continue;
                found.Add(new ProtonBuild(name, dir));
            }
        }
        // Preference: CachyOS / GE builds first (they carry the game fixes), then Steam's own, then anything else.
        return found
            .OrderBy(p =>
                p.Name.Contains("cachyos", StringComparison.OrdinalIgnoreCase) ? 0
                : p.Name.Contains("GE", StringComparison.Ordinal) ? 1
                : p.Name.Contains("Experimental", StringComparison.OrdinalIgnoreCase) ? 2
                : 3
            )
            .ThenByDescending(p => p.Name)
            .ToList();
    }
}
