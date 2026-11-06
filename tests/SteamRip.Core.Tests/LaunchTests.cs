using SteamRip.Core.Launch;

namespace SteamRip.Core.Tests;

public class LaunchTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sr-launch-" + Guid.NewGuid().ToString("N")
    );

    public LaunchTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            Directory.Delete(_root, true);
        }
        catch { }
    }

    private void Exe(string rel, int kb = 1024)
    {
        var p = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, new byte[kb * 1024]);
    }

    [Fact]
    public void Picks_game_exe_over_redist_uninstaller_and_crash_handler()
    {
        Exe("_CommonRedist/vcredist_x64.exe");
        Exe("LEGO Batman Legacy of the Dark Knight/LEGOBatmanLotDK.exe", 60 * 1024);
        Exe("LEGO Batman Legacy of the Dark Knight/UnityCrashHandler64.exe");
        Exe("LEGO Batman Legacy of the Dark Knight/unins000.exe");
        Exe("prefix/drive_c/windows/explorer.exe");
        Assert.Equal(
            Path.Combine("LEGO Batman Legacy of the Dark Knight", "LEGOBatmanLotDK.exe"),
            ExeFinder.Best(_root, "LEGO Batman: Legacy of the Dark Knight")
        );
    }

    [Fact]
    public void Prefers_unreal_shipping_binary_over_root_stub()
    {
        Exe("CoolGame.exe", 200); // UE bootstrap stub
        Exe("CoolGame/Binaries/Win64/CoolGame-Win64-Shipping.exe", 80 * 1024);
        Exe("Engine/Binaries/Win64/CrashReportClient.exe");
        var best = ExeFinder.Best(_root, "Cool Game");
        Assert.Equal(
            Path.Combine("CoolGame", "Binaries", "Win64", "CoolGame-Win64-Shipping.exe"),
            best
        );
        Assert.DoesNotContain(
            ExeFinder.Candidates(_root),
            c => c.RelativePath.Contains("CrashReport")
        );
    }

    [Fact]
    public void Returns_null_when_only_noise()
    {
        Exe("_CommonRedist/dxwebsetup.exe");
        Exe("setup.exe");
        Assert.Null(ExeFinder.Best(_root, "Anything"));
    }

    [Fact]
    public void Proton_locator_runs_without_throwing()
    {
        var builds = ProtonLocator.Find();
        Assert.All(builds, b => Assert.True(File.Exists(Path.Combine(b.Path, "proton"))));
    }
}
