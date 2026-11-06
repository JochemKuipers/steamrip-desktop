namespace SteamRip.Core.Launch;

/// <summary>Picks the game's main executable out of an extracted SteamRIP folder. Heuristic, overridable per game.</summary>
public static class ExeFinder
{
    private static readonly string[] NoiseDirs = ["_commonredist", "_redist", "redist", "redistributables", "prefix", "__macosx", "_setup", "directx", "dotnet", "vcredist", "engine", "support", "tools"];
    private static readonly string[] NoiseNames =
    [
        "unins", "uninstall", "setup", "install", "redist", "vcredist", "vc_redist", "dxsetup", "dxwebsetup", "dotnet", "oalinst", "physx", "crashhandler", "crashreport",
        "crashreportclient", "unitycrashhandler", "ue4prereqsetup", "uecrash", "launcherpatch", "cleanup", "report", "eac", "easyanticheat", "battleye", "beservice",
        "steamservice", "steam_api", "steamclient", "python", "wine", "7z", "unrar", "rar", "touchup", "activat", "update", "patch", "cheat", "trainer", "config", "settings",
    ];

    public sealed record Candidate(string RelativePath, long Size, int Depth, int Score);

    /// <summary>All plausible executables, best first.</summary>
    public static IReadOnlyList<Candidate> Candidates(string gameRoot, string? gameName = null)
    {
        if (!Directory.Exists(gameRoot)) return [];
        var list = new List<Candidate>();
        var nameTokens = Tokens(gameName ?? "");
        var nameSquashed = Squash(gameName ?? "");
        Walk(new DirectoryInfo(gameRoot), 0);
        return list.OrderByDescending(c => c.Score).ThenBy(c => c.Depth).ThenByDescending(c => c.Size).ToList();

        void Walk(DirectoryInfo dir, int depth)
        {
            if (depth > 4) return;
            IEnumerable<FileSystemInfo> entries;
            try { entries = dir.EnumerateFileSystemInfos(); } catch { return; }
            foreach (var e in entries)
            {
                if (e is DirectoryInfo d)
                {
                    if (d.LinkTarget is not null || NoiseDirs.Contains(d.Name.ToLowerInvariant()) || Directory.Exists(Path.Combine(d.FullName, "drive_c"))) continue;
                    Walk(d, depth + 1);
                }
                else if (e is FileInfo f && f.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    var lower = Path.GetFileNameWithoutExtension(f.Name).ToLowerInvariant();
                    if (NoiseNames.Any(n => lower.Contains(n))) continue;
                    var rel = Path.GetRelativePath(gameRoot, f.FullName);
                    var score = 0;
                    var fileTokens = Tokens(lower);
                    score += 10 * nameTokens.Count(t => fileTokens.Contains(t));                  // name match is the strongest signal
                    if (nameSquashed.Length > 3 && (Squash(lower).Contains(nameSquashed) || nameSquashed.Contains(Squash(lower)) && lower.Length > 3)) score += 10; // "5dchesswithmultiversetimetravel"
                    if (lower.Contains("shipping")) score += 8;                                       // UE packaged binary
                    if (lower.Contains("win64") || lower.Contains("x64")) score += 2;
                    if (rel.Contains("Binaries", StringComparison.OrdinalIgnoreCase)) score += 3;    // UE layout
                    if (lower.EndsWith("launcher") || lower.Contains("launcher")) score -= 3;        // usually a stub, but sometimes the only way in
                    if (depth == 0) score += 2;                                                        // top-level exe in the collapsed game dir
                    if (f.Length > 50 << 20) score += 3; else if (f.Length < 300 << 10) score -= 2;   // real games aren't tiny
                    list.Add(new Candidate(rel, f.Length, depth, score));
                }
            }
        }
    }

    public static string? Best(string gameRoot, string? gameName = null) => Candidates(gameRoot, gameName).FirstOrDefault()?.RelativePath;

    private static string Squash(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static HashSet<string> Tokens(string s) =>
        s.ToLowerInvariant().Split([' ', '-', '_', '.', ':', '\'', '’', '(', ')', '[', ']'], StringSplitOptions.RemoveEmptyEntries)
         .Where(t => t.Length > 1 && t is not ("the" or "of" or "and" or "a" or "an" or "win64" or "win32" or "exe"))
         .ToHashSet();
}
