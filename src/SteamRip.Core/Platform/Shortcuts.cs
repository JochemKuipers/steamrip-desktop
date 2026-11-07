using System.Diagnostics;
using System.Text.RegularExpressions;

namespace SteamRip.Core.Platform;

public enum ShortcutPlace
{
    Desktop,
    Menu,
}

/// <summary>
/// Per-game launchers that run the app with <c>--play &lt;postId&gt;</c>, so a shortcut goes through the same
/// Proton, launch options and playtime bookkeeping as the Play button. Linux: freedesktop <c>.desktop</c> files
/// (<c>~/.local/share/applications</c> for the menu, the XDG desktop dir for the desktop). Windows: <c>.lnk</c> files
/// written through WScript.Shell in PowerShell (Start Menu\Programs\SteamRIP, Desktop).
/// </summary>
// ponytail: the Linux icon is the cached Steam cover .jpg by absolute path (KDE and GNOME load it, the spec says png);
// Windows shortcuts use the app's own icon because a .lnk needs an .ico.
public static partial class Shortcuts
{
    private const string Prefix = "steamrip-game-";

    public static bool Exists(long postId, string name, ShortcutPlace place) =>
        File.Exists(PathFor(postId, name, place));

    /// <summary>Writes (or rewrites) the shortcut. Never throws for a missing desktop dir: it is created.</summary>
    public static void Create(long postId, string name, string? iconPath, ShortcutPlace place)
    {
        var path = PathFor(postId, name, place);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (OperatingSystem.IsLinux())
        {
            File.WriteAllText(path, DesktopEntry(postId, name, iconPath));
            // KDE and GNOME treat a .desktop file on the desktop as untrusted until it is executable.
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead
                    | UnixFileMode.UserWrite
                    | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead
                    | UnixFileMode.OtherRead
            );
        }
        else if (OperatingSystem.IsWindows())
        {
            WriteLnk(path, postId, name);
        }
    }

    public static void Remove(long postId, string name, ShortcutPlace place)
    {
        var path = PathFor(postId, name, place);
        if (File.Exists(path))
            File.Delete(path);
    }

    public static void RemoveAll(long postId, string name)
    {
        Remove(postId, name, ShortcutPlace.Desktop);
        Remove(postId, name, ShortcutPlace.Menu);
    }

    /// <summary>The cached Steam cover the library cards use, when it has been fetched; null otherwise.</summary>
    public static string? CoverIcon(long? steamAppId)
    {
        if (steamAppId is not > 0)
            return null;
        var file = Path.Combine(Data.Settings.DefaultDir, "covers", $"{steamAppId}.jpg");
        return File.Exists(file) ? file : null;
    }

    /// <summary>Pure: the .desktop file body.</summary>
    public static string DesktopEntry(long postId, string name, string? iconPath) =>
        $"""
            [Desktop Entry]
            Type=Application
            Name={name}
            Comment=Play {name} (SteamRIP Desktop)
            Exec="{Autostart.ExecutablePath}" --play {postId}
            Icon={iconPath ?? "steamrip-desktop"}
            Terminal=false
            Categories=Game;
            StartupNotify=false
            X-SteamRip-PostId={postId}

            """;

    public static string PathFor(long postId, string name, ShortcutPlace place)
    {
        if (OperatingSystem.IsWindows())
        {
            var dir =
                place == ShortcutPlace.Desktop
                    ? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
                    : Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                        "SteamRIP"
                    );
            return Path.Combine(dir, SafeName(name) + ".lnk");
        }
        var linuxDir =
            place == ShortcutPlace.Desktop
                ? DesktopDir()
                : Path.Combine(DataHome(), "applications");
        return Path.Combine(linuxDir, $"{Prefix}{postId}.desktop");
    }

    /// <summary>File-system-safe version of a game name for the Windows .lnk file name.</summary>
    public static string SafeName(string name)
    {
        var s = UnsafeChars().Replace(name, " ").Trim();
        return s.Length == 0 ? "game" : s;
    }

    private static string DataHome() =>
        Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x
            ? x
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local",
                "share"
            );

    /// <summary><c>xdg-user-dir DESKTOP</c> when available (localised desktops), else <c>~/Desktop</c>.</summary>
    private static string DesktopDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        try
        {
            using var p = Process.Start(
                new ProcessStartInfo("xdg-user-dir", "DESKTOP")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                }
            );
            if (p is not null)
            {
                var line = p.StandardOutput.ReadLine()?.Trim();
                p.WaitForExit(2000);
                // xdg-user-dir prints $HOME when the dir is unset; that is not a desktop
                if (!string.IsNullOrEmpty(line) && line != home)
                    return line;
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException)
        {
            // no xdg-user-dirs installed; fall through
        }
        return Path.Combine(home, "Desktop");
    }

    private static void WriteLnk(string path, long postId, string name)
    {
        var exe = Autostart.ExecutablePath;
        var script =
            $"$s=(New-Object -ComObject WScript.Shell).CreateShortcut('{Ps(path)}');"
            + $"$s.TargetPath='{Ps(exe)}';$s.Arguments='--play {postId}';"
            + $"$s.WorkingDirectory='{Ps(Path.GetDirectoryName(exe) ?? "")}';"
            + $"$s.IconLocation='{Ps(exe)},0';$s.Description='Play {Ps(name)} (SteamRIP Desktop)';$s.Save()";
        using var p = Process.Start(
            new ProcessStartInfo("powershell", "-NoProfile -NonInteractive -Command -")
            {
                RedirectStandardInput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        )!;
        p.StandardInput.WriteLine(script);
        p.StandardInput.Close();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
        if (p.ExitCode != 0 || !File.Exists(path))
            throw new IOException($"Could not create {path}: {err.Trim()}");
    }

    private static string Ps(string s) => s.Replace("'", "''", StringComparison.Ordinal);

    [GeneratedRegex("""[<>:"/\\|?*\p{C}\s]+""")]
    private static partial Regex UnsafeChars();
}
