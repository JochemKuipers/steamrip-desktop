namespace SteamRip.Core.Install;

/// <summary>
/// Picks VC++ redistributables from a SteamRIP <c>_CommonRedist</c> folder and tracks whether they
/// have already been run into a game's Wine prefix (marker file, so first launch is enough).
/// </summary>
public static class RedistInstaller
{
    public const string MarkerName = ".steamrip-vc-redist";

    /// <summary>Silent args for one installer under Wine/Proton.</summary>
    public sealed record VcInstaller(string FilePath, IReadOnlyList<string> Args);

    public static string MarkerPath(string prefixDir) => Path.Combine(prefixDir, MarkerName);

    public static bool NeedsInstall(string prefixDir) => !File.Exists(MarkerPath(prefixDir));

    public static void MarkDone(string prefixDir)
    {
        Directory.CreateDirectory(prefixDir);
        File.WriteAllText(MarkerPath(prefixDir), DateTime.UtcNow.ToString("O") + "\n");
    }

    /// <summary>
    /// VC installers under <paramref name="gameRoot"/>/<c>_CommonRedist</c>: every
    /// <c>vcredist*</c> / <c>vc_redist*</c> (x64 before x86). Skips <c>VisualCppRedist_AIO*</c> —
    /// that 7z SFX often never exits under Proton <c>waitforexitandrun</c>, which greys out Play forever.
    /// </summary>
    public static IReadOnlyList<VcInstaller> FindVcInstallers(string gameRoot)
    {
        var dir = FindCommonRedist(gameRoot);
        if (dir is null)
            return [];

        return
        [
            .. Directory
                .EnumerateFiles(dir)
                .Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                .Where(IsVcRedistName)
                .OrderBy(VcSortKey)
                .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
                .Select(f => new VcInstaller(f, MsArgs)),
        ];
    }

    // ponytail: AIO packs ignored; if a title only ships AIO, first play marks done and launches without VC install.
    private static readonly string[] MsArgs = ["/install", "/quiet", "/norestart"];

    private static bool IsVcRedistName(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith("vcredist", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("vc_redist", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>x64 before x86; 2015-2019 / newer names before bare <c>vcredist_x64</c>.</summary>
    private static int VcSortKey(string path)
    {
        var n = Path.GetFileName(path).ToLowerInvariant();
        var arch = n.Contains("x86") && !n.Contains("x64") ? 1 : 0;
        var age =
            n.Contains("2015") || n.Contains("2017") || n.Contains("2019") || n.Contains("2022")
                ? 0
                : 1;
        return arch * 10 + age;
    }

    public static string? FindCommonRedist(string gameRoot)
    {
        if (!Directory.Exists(gameRoot))
            return null;
        return Directory
            .EnumerateDirectories(gameRoot)
            .FirstOrDefault(d =>
                string.Equals(
                    Path.GetFileName(d),
                    "_CommonRedist",
                    StringComparison.OrdinalIgnoreCase
                )
            );
    }
}
