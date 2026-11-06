using SteamRip.Core.Hosters;

namespace SteamRip.Core.Tests;

public class HostPageTests
{
    [Fact]
    public void FileDitch_solved_page_yields_signed_cdn_url_with_expiry()
    {
        var (url, expires, fileName) = HostPages.ParseFileDitch(
            Fixture.Read("fileditch_solved.html")
        );
        Assert.StartsWith("https://lumisleepsby10pm.xyz/", url);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1794033476).UtcDateTime, expires);
        Assert.Equal("CONTROL-Resonant-SteamRIP.com.rar", fileName);
    }

    [Fact]
    public void FileDitch_interstitial_is_reported_as_such()
    {
        var ex = Assert.Throws<FormatException>(() =>
            HostPages.ParseFileDitch(Fixture.Read("fileditch_interstitial.html"))
        );
        Assert.Contains("verification", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Bzzhr_page_yields_hx_path_and_hashes()
    {
        var (hx, md5, sha, name) = HostPages.ParseBzzhr(Fixture.Read("bzzhr_solved.html"));
        Assert.Equal("/abc123XYZ/download?t=1762464000", hx);
        Assert.Equal("0123456789abcdef0123456789abcdef", md5);
        Assert.Equal(64, sha!.Length);
        Assert.Equal("LEGO-Batman-SteamRIP.com.rar", name);
    }

    [Fact]
    public void MegaDb_page_yields_pt_url_wait_and_hash()
    {
        var (pt, wait, hash, name) = HostPages.ParseMegaDb(Fixture.Read("megadb_landing.html"));
        Assert.StartsWith("https://megadb.net/235fbe4f4789793f?pt=", pt);
        Assert.Equal(10, wait);
        Assert.Equal("5c759169364b30f29cedf71c690ce9f46bc07e9972091dbea2d3bfd4de3ea2a0", hash);
        Assert.Equal("Hull-Rupture-SteamRIP.com.rar", name);
    }

    [Fact]
    public void GoFile_website_token_is_deterministic_per_4h_slot()
    {
        var t0 = DateTimeOffset.FromUnixTimeSeconds(14400 * 1000);
        var a = GoFileResolver.WebsiteToken("tok", t0);
        var b = GoFileResolver.WebsiteToken("tok", t0.AddHours(3));
        var c = GoFileResolver.WebsiteToken("tok", t0.AddHours(4));
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal(64, a.Length);
    }
}
