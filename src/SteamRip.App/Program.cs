using Avalonia;

namespace SteamRip.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Single instance: a second launch hands its request to the first through a marker file it watches:
        // ".show" (bring the window up) or ".play" holding a post id (a game shortcut was clicked).
        // "Global\" matters: without it .NET scopes a named mutex to the Unix session id (/tmp/.dotnet/shm/session<sid>/),
        // and KDE/GNOME start every .desktop launch through systemd --user in a session of its own, so each shortcut
        // click became a second tray icon. With the prefix the mutex lives in /tmp/.dotnet/shm/global/ for the whole user.
        using var mutex = new Mutex(
            true,
            @"Global\steamrip-desktop-single-instance",
            out var first
        );
        if (!first)
        {
            Directory.CreateDirectory(Core.Data.Settings.DefaultDir);
            if (PlayArg(args) is { } id)
                File.WriteAllText(PlayRequestPath, id.ToString());
            else
                File.WriteAllText(ShowRequestPath, DateTime.UtcNow.ToString("o"));
            return;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static string ShowRequestPath => Path.Combine(Core.Data.Settings.DefaultDir, ".show");
    public static string PlayRequestPath => Path.Combine(Core.Data.Settings.DefaultDir, ".play");

    /// <summary>The post id after <c>--play</c>, if any.</summary>
    public static long? PlayArg(string[] args)
    {
        var i = Array.IndexOf(args, "--play");
        return i >= 0 && i + 1 < args.Length && long.TryParse(args[i + 1], out var id) ? id : null;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder
            .Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
