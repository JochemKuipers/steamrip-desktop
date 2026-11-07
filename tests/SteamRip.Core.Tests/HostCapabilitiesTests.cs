using SteamRip.Core.Launch;

namespace SteamRip.Core.Tests;

public class HostCapabilitiesTests
{
    [Fact]
    public void Parses_kscreen_doctor_outputs()
    {
        var d = HostCapabilities.ParseKScreen(Fixture.Read("kscreen_outputs.txt"));
        Assert.Equal(
            [
                new DisplayInfo("HDMI-A-2", HdrState.Incapable, 1920, 1080),
                new DisplayInfo("DP-2", HdrState.Enabled, 1920, 1080, SdrNits: 500, PeakNits: 750),
                new DisplayInfo("DP-1", HdrState.Enabled, 3440, 1440, SdrNits: 300, PeakNits: 465),
            ],
            d
        );
    }

    [Fact]
    public void Parses_mutter_current_state()
    {
        var d = HostCapabilities.ParseMutter(Fixture.Read("mutter_current_state.txt"));
        Assert.Equal(
            [
                new DisplayInfo("DP-1", HdrState.Enabled, 3440, 1440),
                new DisplayInfo("HDMI-2", HdrState.Incapable, 1920, 1080),
                new DisplayInfo("DP-2", HdrState.Disabled, 1920, 1080),
            ],
            d
        );
    }

    [Fact]
    public void Parses_sway_and_hyprland_outputs()
    {
        Assert.Equal(
            [
                new DisplayInfo("DP-1", HdrState.Enabled, 3440, 1440),
                new DisplayInfo("HDMI-A-2", HdrState.Disabled, 1920, 1080),
            ],
            HostCapabilities.ParseWlrOutputs(Fixture.Read("sway_outputs.json"))
        );
        // Hyprland does not print an hdr key: state unknown, size still read
        Assert.Equal(
            [
                new DisplayInfo("DP-1", HdrState.Unknown, 3440, 1440),
                new DisplayInfo("HDMI-A-2", HdrState.Unknown, 1920, 1080),
            ],
            HostCapabilities.ParseWlrOutputs(Fixture.Read("hyprctl_monitors.json"))
        );
        Assert.Empty(HostCapabilities.ParseWlrOutputs(""));
    }

    [Fact]
    public void Edid_hdr_static_metadata_block_detected()
    {
        Assert.True(HostCapabilities.EdidHasHdr(File.ReadAllBytes(Fixture.Path("edid_hdr.bin"))));
        Assert.False(
            HostCapabilities.EdidHasHdr(File.ReadAllBytes(Fixture.Path("edid_nohdr.bin")))
        );
        Assert.False(HostCapabilities.EdidHasHdr(new byte[128]));
    }

    [Fact]
    public void Vulkaninfo_picks_discrete_gpu_and_judges_hdr_wsi()
    {
        var g = HostCapabilities.ParseVulkanInfo(Fixture.Read("vulkaninfo_summary.txt"))!;
        Assert.Equal("AMD", g.Vendor);
        Assert.Equal("radv", g.Driver);
        Assert.Equal("Mesa 26.2.6-arch3.1", g.DriverInfo);
        Assert.True(g.HdrWsiBuiltIn);
        Assert.False(new GpuInfo("AMD", "radv", "Mesa 24.3.4", "24.3.4").HdrWsiBuiltIn);
        Assert.True(new GpuInfo("NVIDIA", "NVIDIA", "", "595.58.3.0").HdrWsiBuiltIn);
        Assert.False(new GpuInfo("NVIDIA", "NVIDIA", "", "580.82.7.0").HdrWsiBuiltIn);
        Assert.Null(new GpuInfo("Intel", null, null, null).HdrWsiBuiltIn);
        Assert.Null(HostCapabilities.ParseVulkanInfo(""));
    }

    [Fact]
    public void Proton_script_env_vars()
    {
        var vars = HostCapabilities.ProtonEnvVars(Fixture.Read("proton_cachyos_excerpt.py"));
        var p = new ProtonCapabilities("Proton-CachyOS", "/p", vars);
        Assert.True(p.Wayland);
        Assert.True(p.FsyncToggle);
        Assert.False(p.LegacyHdr);
        Assert.Contains("PROTON_USE_WOW64", vars);
    }

    private static HostCapabilities Caps(
        string session = "wayland",
        HdrState display = HdrState.Enabled,
        string? gamescope = "/usr/bin/gamescope",
        bool protonWayland = true
    ) =>
        HostCapabilities.None with
        {
            Session = session,
            Desktop = "KDE",
            Displays = [new DisplayInfo("DP-1", display)],
            Gpu = new GpuInfo("AMD", "radv", "Mesa 26.2.6", "26.2.6"),
            GamescopePath = gamescope,
            GamescopeHdr = gamescope is not null,
            Protons =
            [
                new ProtonCapabilities(
                    "P",
                    "/p",
                    protonWayland
                        ? new HashSet<string> { "PROTON_ENABLE_WAYLAND" }
                        : new HashSet<string>()
                ),
            ],
        };

    [SkippableFact]
    public void Hdr_warning_explains_what_blocks_it()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "HDR advice is Linux only");
        var hdr = LaunchOptions.AppDefaults with { Hdr = true };
        var hdrScope = hdr with { Gamescope = true };
        Assert.Null(Caps().HdrWarning(LaunchOptions.AppDefaults, "/p"));
        Assert.Null(Caps().HdrWarning(hdr, "/p"));
        Assert.Null(Caps().HdrWarning(hdrScope, "/p"));
        Assert.Contains("X11", Caps(session: "x11").HdrWarning(hdr, "/p"));
        Assert.Contains(
            "No connected display",
            Caps(display: HdrState.Incapable).HdrWarning(hdr, "/p")
        );
        Assert.Contains("switched off", Caps(display: HdrState.Disabled).HdrWarning(hdr, "/p"));
        Assert.Contains(
            "gamescope is not installed",
            Caps(gamescope: null).HdrWarning(hdrScope, "/p")
        );
        Assert.Contains(
            "only works through gamescope",
            Caps(protonWayland: false).HdrWarning(hdr, "/p")
        );
        Assert.Null(Caps(protonWayland: false).HdrWarning(hdr, null)); // umu default build: unknown, no nagging
        Assert.Null(Caps(protonWayland: false).HdrWarning(hdrScope, "/p"));
        // Proton 10 builds force HDR through env vars instead of detecting it
        var legacy = Caps() with
        {
            Protons =
            [
                new ProtonCapabilities(
                    "UMU-Proton-10",
                    "/p",
                    new HashSet<string> { "PROTON_ENABLE_WAYLAND", "DXVK_HDR" }
                ),
            ],
        };
        Assert.Contains("without HDR auto-detection", legacy.HdrWarning(hdr, "/p"));
        // KDE with SDR brightness at the peak: no headroom, Wine reports SDR (gamescope is unaffected)
        var flat = Caps() with
        {
            Displays = [new DisplayInfo("DP-2", HdrState.Enabled, SdrNits: 500, PeakNits: 500)],
        };
        Assert.Contains("PROTON_FORCE_HDR", flat.HdrWarning(hdr, "/p"));
        Assert.Null(flat.HdrWarning(hdrScope, "/p"));
        Assert.Null(
            (
                flat with
                {
                    Displays =
                    [
                        new DisplayInfo("DP-2", HdrState.Enabled, SdrNits: 300, PeakNits: 465),
                    ],
                }
            ).HdrWarning(hdr, "/p")
        );
    }

    [Fact]
    public void Probe_never_throws_and_lines_are_readable()
    {
        var caps = HostCapabilities.Probe();
        var lines = caps.Lines().ToList();
        Assert.NotEmpty(lines);
        Assert.StartsWith("Session:", lines[0]);
    }
}
