using Avalonia;

namespace SteamRip.App;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Single instance: a second launch just asks the first to show its window (via a marker file the first one watches).
        using var mutex = new Mutex(true, "steamrip-desktop-single-instance", out var first);
        if (!first)
        {
            File.WriteAllText(ShowRequestPath, DateTime.UtcNow.ToString("o"));
            return;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static string ShowRequestPath => Path.Combine(Core.Data.Settings.DefaultDir, ".show");

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
