using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SteamRip.Core.Launch;

namespace SteamRip.App.ViewModels;

/// <summary>
/// Edits one <see cref="LaunchOptions"/>. In Settings it edits the defaults (two-state boxes); on a game card it edits
/// the override, where every box is tri-state and "indeterminate" means "inherit" and the label says what that is.
/// Raises <see cref="Changed"/> with the current record after every user edit.
/// </summary>
public sealed partial class LaunchOptionsEditor(
    Func<LaunchOptions?> inherited,
    Func<HostCapabilities?> capabilities,
    Func<string?> protonPath
) : ObservableObject
{
    private static readonly HashSet<string> Editable =
    [
        nameof(MangoHud),
        nameof(MangoHudConfig),
        nameof(GameMode),
        nameof(Gamescope),
        nameof(GamescopeArgs),
        nameof(Hdr),
        nameof(Wayland),
        nameof(Fsr),
        nameof(Fsync),
        nameof(ProtonLog),
        nameof(DllOverrides),
        nameof(Environment),
        nameof(Wrapper),
    ];

    private bool _loading;

    public event Action<LaunchOptions>? Changed;

    public bool IsLinux => OperatingSystem.IsLinux();

    /// <summary>True on a game card: boxes cycle through on / off / inherit.</summary>
    public bool IsPerGame => inherited() is not null;

    [ObservableProperty]
    public partial bool? MangoHud { get; set; }

    [ObservableProperty]
    public partial string MangoHudConfig { get; set; } = "";

    [ObservableProperty]
    public partial bool? GameMode { get; set; }

    [ObservableProperty]
    public partial bool? Gamescope { get; set; }

    [ObservableProperty]
    public partial string GamescopeArgs { get; set; } = "";

    [ObservableProperty]
    public partial bool? Hdr { get; set; }

    [ObservableProperty]
    public partial bool? Wayland { get; set; }

    [ObservableProperty]
    public partial bool? Fsr { get; set; }

    [ObservableProperty]
    public partial bool? Fsync { get; set; }

    [ObservableProperty]
    public partial bool? ProtonLog { get; set; }

    [ObservableProperty]
    public partial string DllOverrides { get; set; } = "";

    [ObservableProperty]
    public partial string Environment { get; set; } = "";

    [ObservableProperty]
    public partial string Wrapper { get; set; } = "";

    public void Load(LaunchOptions? o)
    {
        _loading = true;
        o ??= new LaunchOptions();
        MangoHud = o.MangoHud;
        MangoHudConfig = o.MangoHudConfig ?? "";
        GameMode = o.GameMode;
        Gamescope = o.Gamescope;
        GamescopeArgs = o.GamescopeArgs ?? "";
        Hdr = o.Hdr;
        Wayland = o.Wayland;
        Fsr = o.Fsr;
        Fsync = o.Fsync;
        ProtonLog = o.ProtonLog;
        DllOverrides = o.DllOverrides ?? "";
        Environment = o.Environment ?? "";
        Wrapper = o.Wrapper ?? "";
        _loading = false;
        Refresh();
    }

    public LaunchOptions ToOptions() =>
        new(
            MangoHud,
            Blank(MangoHudConfig),
            GameMode,
            Gamescope,
            Blank(GamescopeArgs),
            Hdr,
            Wayland,
            Fsr,
            Fsync,
            ProtonLog,
            Blank(DllOverrides),
            Blank(Environment),
            Blank(Wrapper)
        );

    /// <summary>What a launch would actually use right now.</summary>
    public LaunchOptions Effective =>
        ToOptions()
            .MergeOver(inherited() ?? LaunchOptions.AppDefaults)
            .MergeOver(LaunchOptions.AppDefaults);

    /// <summary>Re-reads labels, hints and the HDR warning (after the probe finished or the defaults changed).</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);

    [RelayCommand]
    private void Reset()
    {
        Load(null);
        Changed?.Invoke(new LaunchOptions());
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading || e.PropertyName is not { } name || !Editable.Contains(name))
            return;
        OnPropertyChanged(nameof(HdrWarning));
        OnPropertyChanged(nameof(Effective));
        Changed?.Invoke(ToOptions());
    }

    // ---- labels: tool availability and, per game, the inherited default ----

    public string MangoHudLabel =>
        "MangoHud overlay"
        + Missing(capabilities() is { MangoHud: false })
        + Default(d => d.MangoHud);
    public string GameModeLabel =>
        "Feral gamemode"
        + Missing(capabilities() is { GameModePath: null })
        + Default(d => d.GameMode);
    public string GamescopeLabel =>
        "gamescope"
        + Missing(capabilities() is { GamescopePath: null })
        + Default(d => d.Gamescope);
    public string HdrLabel => "HDR" + Default(d => d.Hdr);
    public string WaylandLabel => "Wine Wayland driver" + Default(d => d.Wayland);
    public string FsrLabel => "FSR 1 fullscreen upscaling" + Default(d => d.Fsr);
    public string FsyncLabel => "fsync" + Default(d => d.Fsync);
    public string ProtonLogLabel => "Proton log" + Default(d => d.ProtonLog);

    public string MangoHudConfigPlaceholder =>
        inherited()?.MangoHudConfig ?? "fps_only  (MANGOHUD_CONFIG, optional)";
    public string GamescopeArgsPlaceholder =>
        inherited()?.GamescopeArgs ?? LaunchOptions.AppDefaults.GamescopeArgs ?? "";
    public string DllOverridesPlaceholder =>
        inherited()?.DllOverrides ?? "dinput8=n,b  (WINEDLLOVERRIDES)";
    public string EnvironmentPlaceholder =>
        inherited()?.Environment ?? "DXVK_ASYNC=1\nVKD3D_CONFIG=dxr";
    public string WrapperPlaceholder => inherited()?.Wrapper ?? "command prefixed to the launch";

    public string? HdrWarning => capabilities()?.HdrWarning(Effective, Blank(protonPath() ?? ""));
    public bool HasHdrWarning => HdrWarning is not null;

    private string Default(Func<LaunchOptions, bool?> pick) =>
        inherited() is { } d
            ? (pick(d) ?? pick(LaunchOptions.AppDefaults)) switch
            {
                true => " · default on",
                false => " · default off",
                null => "",
            }
            : "";

    private static string Missing(bool missing) => missing ? " (not installed)" : "";

    private static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
