using System.Diagnostics;
using SteamRip.Core.Install;

namespace SteamRip.Core.Tests;

public class InstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sr-inst-" + Guid.NewGuid().ToString("N")
    );
    private readonly ArchiveTool _tool = new();

    public InstallerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            Directory.Delete(_root, true);
        }
        catch
        {
            // temp dir cleanup is best effort
        }
    }

    private static bool HasSevenZip => new ArchiveTool().SevenZipPath is not null;

    /// <summary>Builds a .zip (7z format-agnostic) that looks like a SteamRIP release: one game folder + _CommonRedist.</summary>
    private string MakeArchive(string version)
    {
        var src = Path.Combine(_root, "src-" + version);
        Directory.CreateDirectory(Path.Combine(src, "Cool-Game", "Binaries"));
        Directory.CreateDirectory(Path.Combine(src, "_CommonRedist", "vcredist"));
        File.WriteAllText(Path.Combine(src, "Cool-Game", "game.exe"), "exe " + version);
        File.WriteAllText(Path.Combine(src, "Cool-Game", "Binaries", "steam_api64.dll"), "dll");
        File.WriteAllText(Path.Combine(src, "_CommonRedist", "vcredist", "x.exe"), "redist");
        var archive = Path.Combine(_root, $"Cool-Game-{version}.zip");
        var psi = new ProcessStartInfo(_tool.SevenZipPath!)
        {
            WorkingDirectory = src,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { "a", "-tzip", archive, "." })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
        return archive;
    }

    [SkippableFact]
    public async Task Fresh_install_collapses_single_dir_and_moves_redist_inside()
    {
        Skip.IfNot(HasSevenZip, "7z not installed");
        var lib = Path.Combine(_root, "lib");
        var inst = new Installer(_tool);
        var result = await inst.InstallAsync(
            MakeArchive("v1"),
            "cool-game",
            12345,
            null,
            new InstallOptions(lib),
            null,
            CancellationToken.None
        );

        Assert.Equal(Path.Combine(lib, "cool-game"), result.InstallPath);
        Assert.True(File.Exists(Path.Combine(result.InstallPath, "game.exe")));
        Assert.True(Directory.Exists(Path.Combine(result.InstallPath, "_CommonRedist")));
        Assert.False(Directory.Exists(Path.Combine(lib, ".staging", "cool-game")));
        Assert.Null(result.BackupPath);
    }

    [SkippableFact]
    public async Task Update_backs_up_saves_preserves_wine_prefix_and_prunes_backups()
    {
        Skip.IfNot(HasSevenZip, "7z not installed");
        var lib = Path.Combine(_root, "lib");
        var inst = new Installer(_tool);
        var opt = new InstallOptions(lib, KeepBackups: 2);
        await inst.InstallAsync(
            MakeArchive("v1"),
            "cool-game",
            12345,
            null,
            opt,
            null,
            CancellationToken.None
        );
        var game = Path.Combine(lib, "cool-game");

        // Simulate play: in-folder saves and the game's own Wine prefix (created by umu on first launch) holding GSE saves.
        Directory.CreateDirectory(Path.Combine(game, "Saves", "slot1"));
        await File.WriteAllTextAsync(
            Path.Combine(game, "Saves", "slot1", "save.dat"),
            "progress v1"
        );
        var innerEmu = Path.Combine(
            game,
            "prefix",
            "drive_c",
            "users",
            "steamuser",
            "AppData",
            "Roaming",
            "GSE Saves",
            "12345"
        );
        Directory.CreateDirectory(innerEmu);
        await File.WriteAllTextAsync(Path.Combine(innerEmu, "stats.bin"), "inner");
        Assert.Equal("exe v1", await File.ReadAllTextAsync(Path.Combine(game, "game.exe")));

        var r2 = await inst.InstallAsync(
            MakeArchive("v2"),
            "cool-game",
            12345,
            "v1",
            opt,
            null,
            CancellationToken.None
        );

        Assert.Equal("exe v2", await File.ReadAllTextAsync(Path.Combine(game, "game.exe")));
        Assert.Equal(
            "progress v1",
            await File.ReadAllTextAsync(Path.Combine(game, "Saves", "slot1", "save.dat"))
        ); // carried over
        Assert.Equal("inner", await File.ReadAllTextAsync(Path.Combine(innerEmu, "stats.bin"))); // prefix preserved in place
        Assert.Contains("prefix", r2.PreservedPrefixes);
        Assert.NotNull(r2.BackupPath);
        Assert.Equal(Path.Combine(lib, ".backups", "cool-game", "v1"), r2.BackupPath);
        Assert.True(
            File.Exists(Path.Combine(r2.BackupPath!, "game", "Saves", "slot1", "save.dat"))
        );
        Assert.True(
            Directory
                .EnumerateFiles(
                    Path.Combine(r2.BackupPath!, "emu"),
                    "stats.bin",
                    SearchOption.AllDirectories
                )
                .Any()
        );
        Assert.Contains(r2.BackedUpSaves, s => s == "Saves");

        // Two more updates → only the two newest backups remain.
        await inst.InstallAsync(
            MakeArchive("v3"),
            "cool-game",
            12345,
            "v2",
            opt,
            null,
            CancellationToken.None
        );
        await inst.InstallAsync(
            MakeArchive("v4"),
            "cool-game",
            12345,
            "v3",
            opt,
            null,
            CancellationToken.None
        );
        var backups = Directory
            .GetDirectories(Path.Combine(lib, ".backups", "cool-game"))
            .Select(Path.GetFileName)
            .OrderBy(x => x)
            .ToList();
        Assert.Equal(["v2", "v3"], backups);
    }

    [SkippableFact]
    public async Task Corrupt_archive_fails_before_touching_the_install()
    {
        Skip.IfNot(HasSevenZip, "7z not installed");
        var lib = Path.Combine(_root, "lib");
        var inst = new Installer(_tool);
        await inst.InstallAsync(
            MakeArchive("v1"),
            "cool-game",
            null,
            null,
            new InstallOptions(lib),
            null,
            CancellationToken.None
        );
        var bad = Path.Combine(_root, "bad.zip");
        await File.WriteAllBytesAsync(bad, new byte[1024]);
        await Assert.ThrowsAsync<InstallException>(() =>
            inst.InstallAsync(
                bad,
                "cool-game",
                null,
                "v1",
                new InstallOptions(lib),
                null,
                CancellationToken.None
            )
        );
        Assert.Equal(
            "exe v1",
            await File.ReadAllTextAsync(Path.Combine(lib, "cool-game", "game.exe"))
        );
    }

    [Fact]
    public void SaveLocator_finds_save_dirs_but_skips_prefixes_and_engine_dirs()
    {
        var game = Path.Combine(_root, "g");
        Directory.CreateDirectory(Path.Combine(game, "SaveGames"));
        Directory.CreateDirectory(Path.Combine(game, "Profile", "UserData"));
        Directory.CreateDirectory(Path.Combine(game, "Engine", "Saved")); // engine dir skipped
        Directory.CreateDirectory(Path.Combine(game, "prefix", "drive_c", "Saves")); // inside prefix skipped
        var found = SaveLocator.FindInGameFolder(game);
        Assert.Contains("SaveGames", found);
        Assert.Contains(Path.Combine("Profile", "UserData"), found);
        Assert.DoesNotContain(found, f => f.StartsWith("prefix"));
        Assert.DoesNotContain(found, f => f.StartsWith("Engine"));
        Assert.Equal(["prefix"], SaveLocator.FindWinePrefixes(game));
    }

    [Fact]
    public void CollapseSingleDir_keeps_flat_layout_when_files_at_root()
    {
        var staging = Path.Combine(_root, "staging");
        Directory.CreateDirectory(Path.Combine(staging, "Binaries"));
        File.WriteAllText(Path.Combine(staging, "game.exe"), "x");
        Assert.Equal(staging, Installer.CollapseSingleDir(staging));
    }
}
