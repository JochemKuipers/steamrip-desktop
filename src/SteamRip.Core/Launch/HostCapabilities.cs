using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SteamRip.Core.Launch;

public enum HdrState
{
    Unknown,
    Incapable,

    /// <summary>The panel advertises HDR (EDID) but the compositor did not tell us whether it is switched on.</summary>
    Capable,
    Disabled,
    Enabled,
}

/// <summary>One output. SdrNits/PeakNits are the compositor's SDR-white and maximum luminance (0 = unknown); Wine only reports HDR when peak &gt; SDR white.</summary>
public sealed record DisplayInfo(
    string Name,
    HdrState Hdr,
    int Width = 0,
    int Height = 0,
    int SdrNits = 0,
    int PeakNits = 0
)
{
    /// <summary>HDR is on but SDR white sits at (or above) the peak, so there is no headroom and Wine's Wayland driver reports SDR.</summary>
    public bool NoHdrHeadroom =>
        Hdr == HdrState.Enabled && SdrNits > 0 && PeakNits > 0 && SdrNits >= PeakNits;
}

/// <summary>Which environment variables a Proton build's <c>proton</c> script actually reads.</summary>
public sealed record ProtonCapabilities(string Name, string Path, IReadOnlySet<string> EnvVars)
{
    public bool Wayland =>
        EnvVars.Contains("PROTON_ENABLE_WAYLAND") || EnvVars.Contains("PROTON_USE_WAYLAND");

    /// <summary>Proton 10-era build whose HDR has to be forced with <c>DXVK_HDR</c>/<c>PROTON_ENABLE_HDR</c>; 11-based builds detect it and mention neither.</summary>
    public bool LegacyHdr => EnvVars.Contains("PROTON_ENABLE_HDR") || EnvVars.Contains("DXVK_HDR");
    public bool FsyncToggle => EnvVars.Contains("PROTON_NO_FSYNC");
}

public sealed partial record GpuInfo(
    string Vendor,
    string? Driver,
    string? DriverInfo,
    string? DriverVersion
)
{
    /// <summary>Mesa ≥ 25.1 and NVIDIA ≥ 595.58 ship the Vulkan HDR WSI; older drivers need the external layer. Null = cannot tell.</summary>
    public bool? HdrWsiBuiltIn
    {
        get
        {
            if (MesaVersion().Match(DriverInfo ?? "") is { Success: true } m)
                return Version.Parse(m.Groups[1].Value) >= new Version(25, 1);
            if (
                Vendor == "NVIDIA"
                && NumericPrefix().Match(DriverVersion ?? "") is { Success: true } n
            )
                return Version.Parse(n.Groups[1].Value) >= new Version(595, 58);
            return null;
        }
    }

    [GeneratedRegex(@"Mesa (\d+\.\d+)")]
    private static partial Regex MesaVersion();

    [GeneratedRegex(@"^(\d+\.\d+)")]
    private static partial Regex NumericPrefix();
}

/// <summary>
/// What this machine can do for launch tweaks: session type, per-display HDR state, GPU driver, the wrapper tools,
/// and which env vars each Proton build honours. Probing shells out to the compositor's own CLI; every parser is a
/// pure function over its text so it can be fixture-tested. Never throws; unknown stays unknown.
/// </summary>
public sealed partial record HostCapabilities(
    string Session,
    string Desktop,
    IReadOnlyList<DisplayInfo> Displays,
    GpuInfo? Gpu,
    bool HdrWsiLayer,
    string? GamescopePath,
    bool GamescopeHdr,
    bool GamescopeMangoApp,
    bool MangoHud,
    string? GameModePath,
    bool NtSync,
    IReadOnlyList<ProtonCapabilities> Protons
)
{
    public static readonly HostCapabilities None = new(
        "",
        "",
        [],
        null,
        false,
        null,
        false,
        false,
        false,
        null,
        false,
        []
    );

    public bool IsX11 => Session.Equals("x11", StringComparison.OrdinalIgnoreCase);

    /// <summary>Capabilities of a stored Proton value; keywords (and empty = GE-Proton) resolve to the newest installed build, null if not downloaded yet.</summary>
    public ProtonCapabilities? Proton(string? path)
    {
        var effective = ProtonLocator.Effective(path);
        var dir = ProtonLocator.IsKeyword(effective)
            ? ProtonLocator.ResolveInstalled(effective)
            : effective;
        return dir is null ? null : Protons.FirstOrDefault(p => p.Path == dir);
    }

    /// <summary>One human line per fact, for the Settings page and the log.</summary>
    public IEnumerable<string> Lines()
    {
        yield return $"Session: {(Session.Length > 0 ? Session : "unknown")}"
            + (Desktop.Length > 0 ? $" ({Desktop})" : "");
        if (Displays.Count == 0)
            yield return "Displays: none detected";
        foreach (var d in Displays)
            yield return $"{d.Name}: HDR {Describe(d.Hdr)}"
                + (
                    d is { SdrNits: > 0, PeakNits: > 0 }
                        ? $", SDR white {d.SdrNits} / peak {d.PeakNits} nits"
                            + (d.NoHdrHeadroom ? " (no headroom: games will see SDR)" : "")
                        : ""
                )
                + (d.Width > 0 ? $", {d.Width}x{d.Height}" : "");
        yield return Gpu is null
            ? "GPU: unknown (vulkaninfo not found)"
            : $"GPU: {Gpu.Vendor} {Gpu.Driver} {Gpu.DriverInfo ?? Gpu.DriverVersion}; HDR WSI "
                + Gpu.HdrWsiBuiltIn switch
                {
                    true => "built in",
                    false => HdrWsiLayer ? "via layer" : "missing (needs the Vulkan HDR layer)",
                    null => HdrWsiLayer ? "via layer" : "unknown",
                };
        yield return GamescopePath is null
            ? "gamescope: not installed"
            : $"gamescope: {GamescopePath}, HDR {(GamescopeHdr ? "yes" : "no")}, mangoapp {(GamescopeMangoApp ? "yes" : "no")}";
        yield return "MangoHud: " + (MangoHud ? "installed" : "not installed");
        yield return "gamemode: " + (GameModePath ?? "not installed");
        yield return "NTSync: " + (NtSync ? "/dev/ntsync present (wine uses it by itself)" : "no");
        foreach (var p in Protons)
            yield return $"{p.Name}: Wayland {(p.Wayland ? "yes" : "no")}, HDR {(p.LegacyHdr ? "forced via env (old build)" : "auto-detect")}, fsync toggle {(p.FsyncToggle ? "yes" : "no")}";
    }

    private static string Describe(HdrState s) =>
        s switch
        {
            HdrState.Enabled => "enabled",
            HdrState.Disabled => "off (capable; turn it on in the display settings)",
            HdrState.Capable => "capable (cannot tell whether it is on)",
            HdrState.Incapable => "incapable",
            _ => "unknown",
        };

    /// <summary>Why HDR will not work with these effective options, or null when it should. Advisory only.</summary>
    public string? HdrWarning(LaunchOptions effective, string? protonPath)
    {
        if (effective.Hdr != true || !OperatingSystem.IsLinux())
            return null;
        if (IsX11)
            return "HDR needs a Wayland session; this is X11.";
        var noHdrOutput =
            Displays.Count > 0
            && Displays.All(d => d.Hdr is HdrState.Incapable or HdrState.Disabled);
        if (noHdrOutput)
            return Displays.All(d => d.Hdr == HdrState.Incapable)
                ? "No connected display supports HDR."
                : "HDR is switched off in the display settings.";
        if (effective.Gamescope == true)
            return GamescopePath is null
                    ? "gamescope is not installed; HDR without it needs Wine's Wayland driver."
                : GamescopeHdr ? null
                : "This gamescope build has no --hdr-enabled.";
        switch (Proton(protonPath))
        {
            case { Wayland: false } p:
                return $"{p.Name} has no Wayland driver support; HDR only works through gamescope here.";
            case { LegacyHdr: true } p:
                return $"{p.Name} is a Proton 10 build without HDR auto-detection; use GE-Proton, DW-Proton or Proton-CachyOS.";
        }
        if (Displays.FirstOrDefault(d => d.NoHdrHeadroom) is { } flat)
            return $"{flat.Name}: SDR brightness ({flat.SdrNits} nits) is at the peak ({flat.PeakNits}), so Wine sees an SDR display. Lower the SDR brightness in Display settings, or add PROTON_FORCE_HDR=1 to the environment.";
        return Gpu is { HdrWsiBuiltIn: false } && !HdrWsiLayer
            ? "The GPU driver has no HDR Vulkan WSI; update it, install the HDR layer, or use gamescope."
            : null;
    }

    // ---- probing ----

    public static HostCapabilities Probe()
    {
        if (!OperatingSystem.IsLinux())
            return None;
        var session = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "";
        var desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "";
        var displays = ProbeDisplays(desktop);
        var gpu = ProbeGpu();
        var layerDir = new[]
        {
            "/usr/share/vulkan/implicit_layer.d",
            "/etc/vulkan/implicit_layer.d",
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "vulkan",
                "implicit_layer.d"
            ),
        };
        var layers = layerDir
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.json"))
            .Select(Path.GetFileName)
            .ToList();
        var gamescope = Install.ArchiveTool.Find("gamescope");
        var help = gamescope is null ? "" : Run(gamescope, "--help");
        var protons = ProtonLocator
            .Find()
            .Select(p => new ProtonCapabilities(
                p.Name,
                p.Path,
                ProtonEnvVars(ReadOrEmpty(Path.Combine(p.Path, "proton")))
            ))
            .ToList();
        return new HostCapabilities(
            session,
            desktop,
            displays,
            gpu,
            layers.Any(f => f!.Contains("hdr", StringComparison.OrdinalIgnoreCase)),
            gamescope,
            help.Contains("--hdr-enabled", StringComparison.Ordinal),
            help.Contains("--mangoapp", StringComparison.Ordinal),
            layers.Any(f => f!.StartsWith("MangoHud", StringComparison.OrdinalIgnoreCase))
                || Install.ArchiveTool.Find("mangohud") is not null,
            Install.ArchiveTool.Find("gamemoderun"),
            File.Exists("/dev/ntsync"),
            protons
        );
    }

    /// <summary>Largest connected output's preferred mode, for gamescope's <c>-W/-H</c> (it defaults to 1280x720).</summary>
    public static (int Width, int Height)? LargestScreen()
    {
        if (!OperatingSystem.IsLinux())
            return null;
        (int W, int H)? best = null;
        foreach (var (_, modes) in ConnectedOutputs())
        {
            var m = ScreenMode().Match(modes);
            if (!m.Success)
                continue;
            var w = int.Parse(m.Groups[1].Value);
            var h = int.Parse(m.Groups[2].Value);
            if (best is null || w * h > best.Value.W * best.Value.H)
                best = (w, h);
        }
        return best;
    }

    private static IReadOnlyList<DisplayInfo> ProbeDisplays(string desktop)
    {
        List<DisplayInfo> fromCompositor = [];
        try
        {
            if (desktop.Contains("KDE", StringComparison.OrdinalIgnoreCase))
                fromCompositor = [.. ParseKScreen(Run("kscreen-doctor", "-o"))];
            else if (desktop.Contains("GNOME", StringComparison.OrdinalIgnoreCase))
                fromCompositor =
                [
                    .. ParseMutter(
                        Run(
                            "gdbus",
                            "call",
                            "--session",
                            "--dest",
                            "org.gnome.Mutter.DisplayConfig",
                            "--object-path",
                            "/org/gnome/Mutter/DisplayConfig",
                            "--method",
                            "org.gnome.Mutter.DisplayConfig.GetCurrentState"
                        )
                    ),
                ];
            else if (desktop.Contains("Hyprland", StringComparison.OrdinalIgnoreCase))
                fromCompositor = [.. ParseWlrOutputs(Run("hyprctl", "monitors", "-j"))];
            else if (desktop.Contains("sway", StringComparison.OrdinalIgnoreCase))
                fromCompositor = [.. ParseWlrOutputs(Run("swaymsg", "-t", "get_outputs"))];
        }
        catch
        {
            // compositor CLI missing or odd output: fall back to the EDID below
        }
        // EDID knows capability, not state: fill in whatever the compositor left unknown.
        var edid = new Dictionary<string, HdrState>(StringComparer.OrdinalIgnoreCase);
        foreach (var (dir, _) in ConnectedOutputs())
        {
            var name = Path.GetFileName(dir);
            name = name[(name.IndexOf('-') + 1)..]; // card1-DP-1 → DP-1
            var bytes = ReadBytesOrEmpty(Path.Combine(dir, "edid"));
            edid[name] =
                bytes.Length < 128 ? HdrState.Unknown
                : EdidHasHdr(bytes) ? HdrState.Capable
                : HdrState.Incapable;
        }
        if (fromCompositor.Count == 0)
            return [.. edid.Select(kv => new DisplayInfo(kv.Key, kv.Value))];
        return
        [
            .. fromCompositor.Select(d =>
                d.Hdr == HdrState.Unknown && edid.TryGetValue(d.Name, out var e)
                    ? d with
                    {
                        Hdr = e,
                    }
                    : d
            ),
        ];
    }

    private static IEnumerable<(string Dir, string Modes)> ConnectedOutputs()
    {
        const string drm = "/sys/class/drm";
        if (!Directory.Exists(drm))
            yield break;
        foreach (var dir in Directory.EnumerateDirectories(drm, "card*-*"))
        {
            if (ReadOrEmpty(Path.Combine(dir, "status")).Trim() != "connected")
                continue;
            yield return (dir, ReadOrEmpty(Path.Combine(dir, "modes")));
        }
    }

    private static GpuInfo? ProbeGpu()
    {
        var vendorIds = Directory.Exists("/sys/class/drm")
            ? Directory
                .EnumerateDirectories("/sys/class/drm", "card?")
                .Select(d => ReadOrEmpty(Path.Combine(d, "device", "vendor")).Trim())
                .Where(v => v.Length > 0)
                .ToList()
            : [];
        var vk = Install.ArchiveTool.Find("vulkaninfo") is { } tool ? Run(tool, "--summary") : "";
        var parsed = ParseVulkanInfo(vk);
        if (parsed is not null)
            return parsed;
        return vendorIds.Count > 0 ? new GpuInfo(VendorName(vendorIds[0]), null, null, null) : null;
    }

    // ---- pure parsers (fixture-tested) ----

    /// <summary>KDE: <c>kscreen-doctor -o</c>. Verified on Plasma 6.</summary>
    public static IReadOnlyList<DisplayInfo> ParseKScreen(string text)
    {
        var list = new List<DisplayInfo>();
        DisplayInfo? cur = null;
        foreach (var raw in StripAnsi(text).Split('\n'))
        {
            var line = raw.Trim();
            if (KScreenOutput().Match(line) is { Success: true } m)
            {
                if (cur is not null)
                    list.Add(cur);
                cur = new DisplayInfo(m.Groups[1].Value, HdrState.Unknown);
            }
            else if (cur is not null && line.StartsWith("HDR:", StringComparison.Ordinal))
                cur = cur with
                {
                    Hdr = line[4..].Trim() switch
                    {
                        "enabled" => HdrState.Enabled,
                        "disabled" => HdrState.Disabled,
                        "incapable" => HdrState.Incapable,
                        _ => HdrState.Unknown,
                    },
                };
            else if (
                cur is not null
                && line.StartsWith("SDR brightness:", StringComparison.Ordinal)
            )
                cur = cur with { SdrNits = FirstInt(line) };
            else if (
                cur is not null
                && line.StartsWith("Peak brightness:", StringComparison.Ordinal)
            )
                // "418 nits, overridden with: 750 nits": the override is what KWin reports to clients
                cur = cur with
                {
                    PeakNits = LastInt(line),
                };
            else if (
                cur is not null
                && line.StartsWith("Geometry:", StringComparison.Ordinal)
                && ScreenMode().Match(line) is { Success: true } g
            )
                cur = cur with
                {
                    Width = int.Parse(g.Groups[1].Value),
                    Height = int.Parse(g.Groups[2].Value),
                };
        }
        if (cur is not null)
            list.Add(cur);
        return list;
    }

    /// <summary>
    /// GNOME: the GVariant text <c>gdbus</c> prints for <c>DisplayConfig.GetCurrentState</c>. Mutter ≥ 48 puts
    /// <c>color-mode</c> (1 = bt2100 = HDR on) and <c>supported-color-modes</c> in each monitor's property dict.
    /// </summary>
    // ponytail: written against mutter's D-Bus XML and gdbus's printing rules, not against a GNOME box; fixture only.
    public static IReadOnlyList<DisplayInfo> ParseMutter(string text)
    {
        var heads = MutterMonitor().Matches(text);
        var list = new List<DisplayInfo>();
        for (var i = 0; i < heads.Count; i++)
        {
            var start = heads[i].Index;
            var end = i + 1 < heads.Count ? heads[i + 1].Index : text.Length;
            var seg = text[start..end];
            var supported = MutterSupported().Match(seg);
            var mode = MutterColorMode().Match(seg);
            var state = HdrState.Unknown;
            if (supported.Success)
            {
                var hdrListed = supported
                    .Groups[1]
                    .Value.Split(',', StringSplitOptions.TrimEntries)
                    .Any(v => v.Replace("uint32", "").Trim() == "1");
                state =
                    !hdrListed ? HdrState.Incapable
                    : mode.Success && mode.Groups[1].Value == "1" ? HdrState.Enabled
                    : mode.Success ? HdrState.Disabled
                    : HdrState.Capable;
            }
            var size = MutterCurrentMode().Match(seg);
            list.Add(
                new DisplayInfo(
                    heads[i].Groups[1].Value,
                    state,
                    size.Success ? int.Parse(size.Groups[1].Value) : 0,
                    size.Success ? int.Parse(size.Groups[2].Value) : 0
                )
            );
        }
        return list;
    }

    /// <summary>Hyprland <c>hyprctl monitors -j</c> and sway <c>swaymsg -t get_outputs</c>: an array of outputs with <c>name</c> and, when the compositor exposes it, an <c>hdr</c> flag.</summary>
    // ponytail: the hdr key is read when present; builds that do not print it end up Unknown and fall back to the EDID.
    public static IReadOnlyList<DisplayInfo> ParseWlrOutputs(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return [];
        var list = new List<DisplayInfo>();
        foreach (var o in doc.RootElement.EnumerateArray())
        {
            if (!o.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String)
                continue;
            var state = HdrState.Unknown;
            if (o.TryGetProperty("hdr", out var h))
                state = h.ValueKind switch
                {
                    JsonValueKind.True => HdrState.Enabled,
                    JsonValueKind.False => HdrState.Disabled,
                    _ => HdrState.Unknown,
                };
            var w = 0;
            var hgt = 0;
            if (o.TryGetProperty("width", out var wj) && wj.ValueKind == JsonValueKind.Number)
                (w, hgt) = (wj.GetInt32(), o.GetProperty("height").GetInt32());
            else if (o.TryGetProperty("current_mode", out var cm))
                (w, hgt) = (
                    cm.GetProperty("width").GetInt32(),
                    cm.GetProperty("height").GetInt32()
                );
            list.Add(new DisplayInfo(n.GetString()!, state, w, hgt));
        }
        return list;
    }

    private static int FirstInt(string line) =>
        Digits().Match(line) is { Success: true } m ? int.Parse(m.Value) : 0;

    private static int LastInt(string line) =>
        Digits().Matches(line) is { Count: > 0 } ms ? int.Parse(ms[^1].Value) : 0;

    /// <summary>True when any CTA-861 extension block carries the HDR Static Metadata data block (extended tag 6).</summary>
    public static bool EdidHasHdr(ReadOnlySpan<byte> edid)
    {
        for (var block = 128; block + 128 <= edid.Length; block += 128)
        {
            var b = edid.Slice(block, 128);
            if (b[0] != 0x02)
                continue;
            int dtdStart = b[2];
            var i = 4;
            while (i < dtdStart && i < 127)
            {
                var tag = b[i] >> 5;
                var len = b[i] & 0x1f;
                if (tag == 7 && len >= 1 && b[i + 1] == 6)
                    return true;
                i += len + 1;
            }
        }
        return false;
    }

    /// <summary><c>vulkaninfo --summary</c>: the discrete GPU when there is one, else the first device.</summary>
    public static GpuInfo? ParseVulkanInfo(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var blocks = VulkanGpuHeader().Split(text).Skip(1).ToList();
        if (blocks.Count == 0)
            return null;
        var block =
            blocks.FirstOrDefault(b => b.Contains("DISCRETE_GPU", StringComparison.Ordinal))
            ?? blocks[0];
        var fields = VulkanField()
            .Matches(block)
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.Trim());
        return new GpuInfo(
            fields.TryGetValue("vendorID", out var vendor) ? VendorName(vendor) : "unknown",
            fields.GetValueOrDefault("driverName"),
            fields.GetValueOrDefault("driverInfo"),
            fields.GetValueOrDefault("driverVersion")
        );
    }

    /// <summary>Every <c>PROTON_*</c> (plus <c>DXVK_HDR</c>, <c>WINE_FULLSCREEN_FSR</c>) name the script mentions.</summary>
    public static IReadOnlySet<string> ProtonEnvVars(string script) =>
        ProtonVar().Matches(script).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);

    public static string VendorName(string pciVendor) =>
        pciVendor.Trim().ToLowerInvariant() switch
        {
            "0x1002" => "AMD",
            "0x10de" => "NVIDIA",
            "0x8086" => "Intel",
            var v => v,
        };

    // ---- helpers ----

    private static string Run(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var a in args)
                psi.ArgumentList.Add(a);
            psi.Environment["NO_COLOR"] = "1";
            using var p = Process.Start(psi);
            if (p is null)
                return "";
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(5000))
            {
                p.Kill(entireProcessTree: true);
                return "";
            }
            var text = outTask.GetAwaiter().GetResult();
            // gamescope prints --help on stderr
            return text.Length > 0 ? text : errTask.GetAwaiter().GetResult();
        }
        catch
        {
            return "";
        }
    }

    private static string ReadOrEmpty(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }
        catch
        {
            return "";
        }
    }

    private static byte[] ReadBytesOrEmpty(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : [];
        }
        catch
        {
            return [];
        }
    }

    private static string StripAnsi(string s) => Ansi().Replace(s, "");

    [GeneratedRegex(@"\x1b\[[0-9;]*[A-Za-z]")]
    private static partial Regex Ansi();

    [GeneratedRegex(@"^Output:\s+\d+\s+(\S+)")]
    private static partial Regex KScreenOutput();

    [GeneratedRegex(@"(\d{3,5})x(\d{3,5})")]
    private static partial Regex ScreenMode();

    [GeneratedRegex(@"\(\('([^']+)', '[^']*', '[^']*', '[^']*'\)")]
    private static partial Regex MutterMonitor();

    [GeneratedRegex(@"'supported-color-modes':\s*<\[([^\]]*)\]>")]
    private static partial Regex MutterSupported();

    [GeneratedRegex(@"'color-mode':\s*<uint32 (\d+)>")]
    private static partial Regex MutterColorMode();

    [GeneratedRegex(
        @"\('[^']*',\s*(\d+),\s*(\d+),\s*[\d.]+,\s*[\d.]+,\s*\[[^\]]*\],\s*\{[^}]*'is-current':\s*<true>"
    )]
    private static partial Regex MutterCurrentMode();

    [GeneratedRegex(@"^GPU\d+:", RegexOptions.Multiline)]
    private static partial Regex VulkanGpuHeader();

    [GeneratedRegex(@"^\s*(\w+)\s*=\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex VulkanField();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();

    [GeneratedRegex(@"\b(PROTON_[A-Z0-9_]+|DXVK_HDR|WINE_FULLSCREEN_FSR)\b")]
    private static partial Regex ProtonVar();
}
