using SteamRip.Core.Install;

namespace SteamRip.Core.Tests;

public class RedistInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sr-redist-" + Guid.NewGuid().ToString("N")
    );

    public RedistInstallerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            Directory.Delete(_root, true);
        }
        catch
        {
            // temp cleanup is best effort
        }
    }

    [Fact]
    public void Empty_or_missing_CommonRedist_yields_nothing()
    {
        Assert.Empty(RedistInstaller.FindVcInstallers(_root));
        Directory.CreateDirectory(Path.Combine(_root, "_CommonRedist"));
        Assert.Empty(RedistInstaller.FindVcInstallers(_root));
    }

    [Fact]
    public void Skips_VisualCppRedist_AIO_and_non_VC_setup_exes()
    {
        var dir = Path.Combine(_root, "_CommonRedist");
        Directory.CreateDirectory(dir);
        Touch(dir, "vcredist_2015-2019_x64.exe");
        Touch(dir, "vcredist_x86.exe");
        Touch(dir, "VisualCppRedist_AIO_x86_x64.exe");
        Touch(dir, "dxwebsetup.exe");

        var found = RedistInstaller.FindVcInstallers(_root);
        Assert.Equal(2, found.Count);
        Assert.EndsWith("vcredist_2015-2019_x64.exe", found[0].FilePath);
        Assert.EndsWith("vcredist_x86.exe", found[1].FilePath);
        Assert.DoesNotContain(
            found,
            i => Path.GetFileName(i.FilePath).Contains("AIO", StringComparison.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public void AIO_only_CommonRedist_yields_nothing()
    {
        var dir = Path.Combine(_root, "_CommonRedist");
        Directory.CreateDirectory(dir);
        Touch(dir, "VisualCppRedist_AIO_x86_x64.exe");
        Assert.Empty(RedistInstaller.FindVcInstallers(_root));
    }

    [Fact]
    public void Without_AIO_picks_vcredist_x64_before_x86_with_MS_silent_args()
    {
        var dir = Path.Combine(_root, "_CommonRedist");
        Directory.CreateDirectory(dir);
        Touch(dir, "vcredist_x86.exe");
        Touch(dir, "vcredist_2015-2019_x64.exe");
        Touch(dir, "oalinst.exe");

        var found = RedistInstaller.FindVcInstallers(_root);
        Assert.Equal(2, found.Count);
        Assert.EndsWith("vcredist_2015-2019_x64.exe", found[0].FilePath);
        Assert.EndsWith("vcredist_x86.exe", found[1].FilePath);
        Assert.Equal(["/install", "/quiet", "/norestart"], found[0].Args);
    }

    [Fact]
    public void Marker_gates_NeedsInstall()
    {
        var prefix = Path.Combine(_root, "prefix");
        Assert.True(RedistInstaller.NeedsInstall(prefix));
        RedistInstaller.MarkDone(prefix);
        Assert.False(RedistInstaller.NeedsInstall(prefix));
        Assert.True(File.Exists(RedistInstaller.MarkerPath(prefix)));
    }

    private static void Touch(string dir, string name) =>
        File.WriteAllText(Path.Combine(dir, name), "x");
}
