using SteamRip.Core.Launch;

namespace SteamRip.Core.Tests;

public class ProtonTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        "sr-proton-" + Guid.NewGuid().ToString("N")
    );

    public ProtonTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        Directory.Delete(_dir, true);
    }

    private void Build(string name, bool withScript = true)
    {
        var d = Path.Combine(_dir, name);
        Directory.CreateDirectory(d);
        if (withScript)
            File.WriteAllText(Path.Combine(d, "proton"), "#!/usr/bin/env python3\n");
    }

    [Fact]
    public void Keywords_resolve_to_the_newest_installed_build_by_numeric_version()
    {
        Build("GE-Proton9-27");
        Build("GE-Proton11-3");
        Build("GE-Proton11-12");
        Build("GE-Proton12-1", withScript: false); // half-extracted: ignored
        Build("dwproton-11.0-9");
        Build("dwproton-11.0-16");
        Build("UMU-Proton-10.0-4");
        Assert.Equal(
            Path.Combine(_dir, "GE-Proton11-12"),
            ProtonLocator.ResolveInstalled(ProtonLocator.GeProton, _dir)
        );
        Assert.Equal(
            Path.Combine(_dir, "dwproton-11.0-16"),
            ProtonLocator.ResolveInstalled(ProtonLocator.DwProton, _dir)
        );
        Assert.Null(ProtonLocator.ResolveInstalled("/some/dir", _dir));
        Assert.Null(
            ProtonLocator.ResolveInstalled(ProtonLocator.GeProton, Path.Combine(_dir, "nope"))
        );
    }

    [Fact]
    public void Empty_or_null_means_ge_proton_never_umu()
    {
        Assert.Equal(ProtonLocator.GeProton, ProtonLocator.Effective(null));
        Assert.Equal(ProtonLocator.GeProton, ProtonLocator.Effective(""));
        Assert.Equal("/x", ProtonLocator.Effective("/x"));
        Assert.Equal(ProtonLocator.GeProton, new Data.Settings().DefaultProtonPath);
        Assert.True(ProtonLocator.IsKeyword(ProtonLocator.DwProton));
        Assert.False(ProtonLocator.IsKeyword("/x"));
    }

    [Fact]
    public void Dwproton_release_json_yields_tarball_and_checksum()
    {
        var r = ProtonDownloader.ParseLatest(Fixture.Read("dwproton_release.json"))!;
        Assert.Equal("dwproton-11.0-16", r.Tag);
        Assert.Equal("dwproton-11.0-16-x86_64.tar.xz", r.ArchiveName);
        Assert.EndsWith("/dwproton-11.0-16/dwproton-11.0-16-x86_64.tar.xz", r.ArchiveUrl);
        Assert.EndsWith(".sha512sum", r.ChecksumUrl);
        Assert.Null(ProtonDownloader.ParseLatest("""{"tag_name":"x","assets":[]}"""));
    }

    [Fact]
    public void Sha512sum_line_is_matched_by_file_name()
    {
        const string text = "abc123  dwproton-11.0-16-x86_64.tar.xz\ndef456 *other.tar.xz\n";
        Assert.Equal(
            "abc123",
            ProtonDownloader.ParseChecksum(text, "dwproton-11.0-16-x86_64.tar.xz")
        );
        Assert.Equal("def456", ProtonDownloader.ParseChecksum(text, "other.tar.xz"));
        Assert.Null(ProtonDownloader.ParseChecksum(text, "missing.tar.xz"));
    }
}
