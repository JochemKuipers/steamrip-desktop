namespace SteamRip.Core.Install;

/// <summary>Finds save-like directories: inside the game folder (heuristic names) and in Goldberg/GSE emulator dirs under Wine prefixes keyed by Steam app id.</summary>
public static class SaveLocator
{
    public static readonly string[] SaveDirNames =
    [
        "Save",
        "Saves",
        "SaveGames",
        "SaveGame",
        "SaveData",
        "Savegames",
        "Profiles",
        "Saved",
        "UserData",
        "Savedata",
    ];
    public static readonly string[] EmuSaveDirNames = ["GSE Saves", "Goldberg SteamEmu Saves"];
    private static readonly string[] SkipDirs =
    [
        "_CommonRedist",
        "_Redist",
        "redist",
        "drive_c",
        "Engine",
        "Content",
        "Binaries",
        "Shaders",
        "Win64",
        "Win32",
    ];
    private static readonly string[] SkipBelowRoot = ["Data", "data", "Resources", "Plugins"];

    /// <summary>Save-like dirs inside <paramref name="gameRoot"/> (relative paths). Shallow walk (depth ≤ 4), skipping Wine prefixes and bulky engine dirs.</summary>
    public static IReadOnlyList<string> FindInGameFolder(string gameRoot, int maxDepth = 4)
    {
        var results = new List<string>();
        if (!Directory.Exists(gameRoot))
            return results;
        Walk(new DirectoryInfo(gameRoot), 0);
        return results;

        void Walk(DirectoryInfo dir, int depth)
        {
            IEnumerable<DirectoryInfo> children;
            try
            {
                children = dir.EnumerateDirectories();
            }
            catch
            {
                return;
            }
            foreach (var child in children)
            {
                if (child.LinkTarget is not null)
                    continue;
                if (
                    SaveDirNames.Any(n => n.Equals(child.Name, StringComparison.OrdinalIgnoreCase))
                    || child.Name.StartsWith("Save", StringComparison.OrdinalIgnoreCase)
                )
                {
                    results.Add(Path.GetRelativePath(gameRoot, child.FullName));
                    continue;
                }
                if (depth + 1 >= maxDepth)
                    continue;
                if (SkipDirs.Any(s => s.Equals(child.Name, StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (
                    depth > 0
                    && SkipBelowRoot.Any(s =>
                        s.Equals(child.Name, StringComparison.OrdinalIgnoreCase)
                    )
                )
                    continue;
                if (IsWinePrefix(child.FullName))
                    continue;
                Walk(child, depth + 1);
            }
        }
    }

    public static bool IsWinePrefix(string dir) =>
        Directory.Exists(Path.Combine(dir, "drive_c"))
        || File.Exists(Path.Combine(dir, "system.reg"));

    /// <summary>Directories inside <paramref name="gameRoot"/> that are Wine prefixes. These must survive a reinstall.</summary>
    public static IReadOnlyList<string> FindWinePrefixes(string gameRoot)
    {
        if (!Directory.Exists(gameRoot))
            return [];
        return
        [
            .. Directory
                .EnumerateDirectories(gameRoot)
                .Where(IsWinePrefix)
                .Select(d => Path.GetFileName(d)),
        ];
    }

    /// <summary>Goldberg/GSE save dirs for <paramref name="steamAppId"/> inside the game's own Wine prefix(es); on Windows, the real user profile.</summary>
    public static IReadOnlyList<string> FindEmulatorSaves(long steamAppId, string gameRoot)
    {
        var userDirs = FindWinePrefixes(gameRoot)
            .Select(prefix => Path.Combine(gameRoot, prefix, "drive_c", "users"))
            .Where(Directory.Exists)
            .SelectMany(Directory.EnumerateDirectories)
            .ToList();
        if (OperatingSystem.IsWindows())
            userDirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        return
        [
            .. userDirs
                .Distinct()
                .SelectMany(user =>
                    EmuSaveDirNames.Select(emu =>
                        Path.Combine(user, "AppData", "Roaming", emu, steamAppId.ToString())
                    )
                )
                .Where(Directory.Exists),
        ];
    }
}
