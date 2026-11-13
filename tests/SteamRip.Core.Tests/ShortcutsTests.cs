using SteamRip.Core.Platform;

namespace SteamRip.Core.Tests;

public class ShortcutsTests
{
    [Fact]
    public void Desktop_entry_plays_through_the_app()
    {
        var body = Shortcuts.DesktopEntry(4242, "Cyberpunk 2077", "/c/1091500.jpg");
        Assert.Contains("Name=Cyberpunk 2077\n", body);
        Assert.Contains($"Exec=\"{Autostart.ExecutablePath}\" --play 4242\n", body);
        Assert.Contains("Icon=/c/1091500.jpg\n", body);
        Assert.Contains("Categories=Game;\n", body);
        Assert.Contains("Icon=steamrip-desktop\n", Shortcuts.DesktopEntry(1, "x", null));
    }

    [Fact]
    public void Safe_name_strips_what_file_systems_reject()
    {
        Assert.Equal("Half-Life 2 Episode One", Shortcuts.SafeName("Half-Life 2: Episode One"));
        Assert.Equal("a b", Shortcuts.SafeName("a/\\b"));
        Assert.Equal("game", Shortcuts.SafeName("???"));
    }

    [Fact]
    public void GameIcon_prefers_icons_dir_over_covers()
    {
        var root = Path.Combine(Path.GetTempPath(), "sr-icons-" + Guid.NewGuid().ToString("N"));
        var old = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        // Settings.DefaultDir uses XDG_CONFIG_HOME/steamrip-desktop on Linux.
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", root);
        try
        {
            var cfg = Path.Combine(root, "steamrip-desktop");
            Directory.CreateDirectory(Path.Combine(cfg, "covers"));
            Directory.CreateDirectory(Path.Combine(cfg, "icons"));
            File.WriteAllText(Path.Combine(cfg, "covers", "99.jpg"), "cover");
            Assert.EndsWith(
                Path.Combine("covers", "99.jpg"),
                Shortcuts.GameIcon(99)!.Replace('/', Path.DirectorySeparatorChar)
            );
            File.WriteAllText(Path.Combine(cfg, "icons", "99.png"), "icon");
            Assert.EndsWith(
                Path.Combine("icons", "99.png"),
                Shortcuts.GameIcon(99)!.Replace('/', Path.DirectorySeparatorChar)
            );
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", old);
            try
            {
                Directory.Delete(root, true);
            }
            catch
            {
                // best-effort
            }
        }
    }

    [SkippableFact]
    public void Menu_shortcut_round_trip_under_xdg_data_home()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), ".desktop files are Linux only");
        var dir = Path.Combine(Path.GetTempPath(), "sr-xdg-" + Guid.NewGuid().ToString("N"));
        var old = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", dir);
        try
        {
            Assert.False(Shortcuts.Exists(7, "G", ShortcutPlace.Menu));
            Shortcuts.Create(7, "G", null, ShortcutPlace.Menu);
            var path = Path.Combine(dir, "applications", "steamrip-game-7.desktop");
            Assert.Equal(path, Shortcuts.PathFor(7, "G", ShortcutPlace.Menu));
            Assert.True(Shortcuts.Exists(7, "G", ShortcutPlace.Menu));
            if (OperatingSystem.IsLinux())
                Assert.True(
                    File.GetUnixFileMode(path).HasFlag(UnixFileMode.UserExecute),
                    "desktop files must be executable to be trusted"
                );
            Shortcuts.RemoveAll(7, "G");
            Assert.False(File.Exists(path));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", old);
            Directory.Delete(dir, true);
        }
    }
}
