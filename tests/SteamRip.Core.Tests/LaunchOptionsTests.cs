using SteamRip.Core.Data;
using SteamRip.Core.Launch;

namespace SteamRip.Core.Tests;

public class LaunchOptionsTests
{
    private static readonly LaunchOptions Defaults = LaunchOptions.AppDefaults with
    {
        MangoHud = true,
        GamescopeArgs = "-f -r 144",
    };

    [Fact]
    public void MergeOver_inherits_nulls_and_blanks_but_keeps_explicit_values()
    {
        var game = new LaunchOptions(MangoHud: false, GamescopeArgs: "  ", Wrapper: "mangohud");
        var m = game.MergeOver(Defaults);
        Assert.False(m.MangoHud); // explicit false beats default true
        Assert.True(m.GameMode); // null inherits
        Assert.Equal("-f -r 144", m.GamescopeArgs); // blank inherits
        Assert.Equal("mangohud", m.Wrapper);
        Assert.True(new LaunchOptions().IsEmpty);
        Assert.False(game.IsEmpty);
    }

    [Fact]
    public void Environment_lines_parse_and_skip_noise()
    {
        var env = LaunchOptions
            .ParseEnvironment(
                "# comment\n\nDXVK_ASYNC=1\nexport VKD3D_CONFIG=\"dxr11\"\nbroken line\n=nokey\nDXVK_ASYNC=0"
            )
            .ToList();
        Assert.Equal(
            [
                new KeyValuePair<string, string>("DXVK_ASYNC", "1"),
                new KeyValuePair<string, string>("VKD3D_CONFIG", "dxr11"),
                new KeyValuePair<string, string>("DXVK_ASYNC", "0"),
            ],
            env
        );
    }

    private static LaunchSpec Spec(LaunchOptions o) =>
        new(1, "Game", "/lib/game", "Game.exe", "/proton/ge", "-dx12", 1091500, o);

    private static string? Tools(string name) =>
        name switch
        {
            "gamescope" => "/usr/bin/gamescope",
            "gamemoderun" => "/usr/bin/gamemoderun",
            _ => null,
        };

    [Fact]
    public void Compose_full_stack_under_gamescope()
    {
        var o = LaunchOptions.AppDefaults with { MangoHud = true, Gamescope = true, Hdr = true };
        var c = GameLauncher.Compose(
            Spec(o),
            "/usr/bin/umu-run",
            "/lib/game/Game.exe",
            "/lib/game/prefix",
            Tools,
            (2560, 1440)
        );
        Assert.Equal(
            "/usr/bin/gamemoderun /usr/bin/gamescope -f -W 2560 -H 1440 --hdr-enabled --mangoapp -- /usr/bin/umu-run /lib/game/Game.exe -dx12",
            string.Join(' ', c.Command)
        );
        Assert.Equal("1", c.Environment["DXVK_HDR"]);
        Assert.False(c.Environment.ContainsKey("MANGOHUD")); // --mangoapp instead of a second overlay
        Assert.False(c.Environment.ContainsKey("PROTON_ENABLE_WAYLAND")); // gamescope is the compositor
        Assert.Equal("umu-1091500", c.Environment["GAMEID"]);
        Assert.Equal("/proton/ge", c.Environment["PROTONPATH"]);
        Assert.Equal("/lib/game/prefix", c.Environment["WINEPREFIX"]);
        Assert.Empty(c.Warnings);
    }

    [Fact]
    public void Compose_hdr_without_gamescope_turns_on_wine_wayland()
    {
        var o = LaunchOptions.AppDefaults with { Hdr = true, GameMode = false };
        var c = GameLauncher.Compose(
            Spec(o),
            "/usr/bin/umu-run",
            "/x/Game.exe",
            "/x/prefix",
            Tools
        );
        Assert.Equal(["/usr/bin/umu-run", "/x/Game.exe", "-dx12"], c.Command);
        Assert.Equal("1", c.Environment["DXVK_HDR"]);
        Assert.Equal("1", c.Environment["PROTON_ENABLE_HDR"]);
        Assert.Equal("1", c.Environment["PROTON_ENABLE_WAYLAND"]);
        Assert.Contains(c.Warnings, w => w.Contains("Wayland"));
    }

    [Fact]
    public void Compose_respects_user_size_and_skips_missing_tools()
    {
        var o = LaunchOptions.AppDefaults with
        {
            Gamescope = true,
            GamescopeArgs = "-W 1920 -H 1080 -r 60",
            MangoHud = true,
        };
        var c = GameLauncher.Compose(
            Spec(o),
            "/usr/bin/umu-run",
            "/x/Game.exe",
            "/x/prefix",
            _ => null,
            (3440, 1440)
        );
        // neither tool installed: both skipped, two warnings, still launches
        Assert.Equal(["/usr/bin/umu-run", "/x/Game.exe", "-dx12"], c.Command);
        Assert.Equal(2, c.Warnings.Count);
        Assert.Equal("1", c.Environment["MANGOHUD"]); // no gamescope → plain MangoHud layer

        var with = GameLauncher.Compose(
            Spec(o),
            "/usr/bin/umu-run",
            "/x/Game.exe",
            "/x/prefix",
            Tools,
            (3440, 1440)
        );
        Assert.DoesNotContain("3440", string.Join(' ', with.Command));
        Assert.Contains("-W 1920 -H 1080 -r 60 --mangoapp --", string.Join(' ', with.Command));
    }

    [Fact]
    public void Compose_env_toggles_and_free_form_override()
    {
        var o = LaunchOptions.AppDefaults with
        {
            GameMode = false,
            Fsync = false,
            Fsr = true,
            ProtonLog = true,
            Wayland = true,
            MangoHud = true,
            MangoHudConfig = "fps_only",
            DllOverrides = "dinput8=n,b",
            Environment = "DXVK_HDR=0\nVKD3D_CONFIG=dxr\nPROTON_LOG=0",
            Wrapper = "env FOO=1",
        };
        var c = GameLauncher.Compose(
            Spec(o),
            "/usr/bin/umu-run",
            "/x/Game.exe",
            "/x/prefix",
            Tools
        );
        Assert.Equal(["env", "FOO=1", "/usr/bin/umu-run", "/x/Game.exe", "-dx12"], c.Command);
        Assert.Equal("1", c.Environment["PROTON_NO_FSYNC"]);
        Assert.Equal("1", c.Environment["WINE_FULLSCREEN_FSR"]);
        Assert.Equal("1", c.Environment["PROTON_ENABLE_WAYLAND"]);
        Assert.Equal("fps_only", c.Environment["MANGOHUD_CONFIG"]);
        Assert.Equal("dinput8=n,b", c.Environment["WINEDLLOVERRIDES"]);
        Assert.Equal("0", c.Environment["PROTON_LOG"]); // free-form wins over the toggle
        Assert.Equal("dxr", c.Environment["VKD3D_CONFIG"]);
    }

    [Fact]
    public void Compose_on_windows_runs_exe_directly_with_only_env_and_wrapper()
    {
        var o = LaunchOptions.AppDefaults with
        {
            Gamescope = true,
            Hdr = true,
            Environment = "FOO=bar",
        };
        var c = GameLauncher.Compose(Spec(o), null, @"C:\g\Game.exe", null, Tools);
        Assert.Equal([@"C:\g\Game.exe", "-dx12"], c.Command);
        Assert.Equal([new KeyValuePair<string, string>("FOO", "bar")], c.Environment.ToList());
        Assert.Empty(c.Warnings);
    }

    [Fact]
    public void Launch_options_round_trip_through_the_database()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sr-launch-{Guid.NewGuid():N}.db");
        try
        {
            using var db = new Database(path);
            db.UpsertPosts([
                new Catalog.CatalogPost(
                    7,
                    "g",
                    "https://steamrip.com/g/",
                    "G Free Download",
                    "G",
                    null,
                    null,
                    null,
                    DateTime.UtcNow,
                    DateTime.UtcNow,
                    null,
                    []
                ),
            ]);
            var g = new InstalledGame(
                7,
                "g",
                "G",
                "1.0",
                "/lib/g",
                DateTime.UtcNow,
                true,
                InstallState.Installed,
                null,
                Launch: new LaunchOptions(Gamescope: true, GamescopeArgs: "-f -r 120")
            );
            db.UpsertInstall(g);
            var back = db.GetInstall(7)!;
            Assert.Equal(g.Launch, back.Launch);
            Assert.Null(back.Launch!.MangoHud);

            db.UpsertInstall(back with { Launch = new LaunchOptions() });
            Assert.Null(db.GetInstall(7)!.Launch); // empty override is stored as NULL
        }
        finally
        {
            File.Delete(path);
        }
    }
}
