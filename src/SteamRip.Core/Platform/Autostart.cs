namespace SteamRip.Core.Platform;

/// <summary>Linux: XDG autostart .desktop file. Windows: HKCU\…\Run value. Both point at the running executable with <c>--hidden</c>.</summary>
public static class Autostart
{
    private const string AppId = "steamrip-desktop";

    /// <summary>Inside an AppImage the process runs from a transient mount; $APPIMAGE is the file the user keeps.</summary>
    public static string ExecutablePath =>
        Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } appImage
            ? appImage
            : Environment.ProcessPath ?? System.Reflection.Assembly.GetEntryAssembly()!.Location;

    public static bool IsEnabled()
    {
        if (OperatingSystem.IsLinux())
            return File.Exists(DesktopFilePath());
        if (OperatingSystem.IsWindows())
            return ReadRunValue() is not null;
        return false;
    }

    public static void Set(bool enabled)
    {
        if (OperatingSystem.IsLinux())
        {
            var path = DesktopFilePath();
            if (!enabled)
            {
                if (File.Exists(path))
                    File.Delete(path);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(
                path,
                $"""
                [Desktop Entry]
                Type=Application
                Name=SteamRIP Desktop
                Comment=Keeps your SteamRIP library up to date
                Exec="{ExecutablePath}" --hidden
                Icon={AppId}
                Terminal=false
                X-GNOME-Autostart-enabled=true
                X-KDE-autostart-after=panel

                """
            );
        }
        else if (OperatingSystem.IsWindows())
        {
            WriteRunValue(enabled ? $"\"{ExecutablePath}\" --hidden" : null);
        }
    }

    private static string DesktopFilePath()
    {
        var cfg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x
            ? x
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config"
            );
        return Path.Combine(cfg, "autostart", AppId + ".desktop");
    }

    private static string? ReadRunValue()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run"
        );
        return key?.GetValue("SteamRIP Desktop") as string;
    }

    private static void WriteRunValue(string? value)
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run"
        );
        if (value is null)
            key.DeleteValue("SteamRIP Desktop", false);
        else
            key.SetValue("SteamRIP Desktop", value);
    }
}
