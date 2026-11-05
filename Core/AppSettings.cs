namespace Shft.Core;

public enum DockEdge { Bottom, Left, Right, Top }

public enum TaskbarMode { Keep, AutoHide, Hide }

public enum ThemeMode { Auto, Dark, Light }

/// <summary>Top bar background. Adaptive leaves it clear and picks text colours from what is behind it.</summary>
public enum TopBarStyle { Solid, Translucent, Adaptive }

public enum MotionMode { Auto, Full, Reduced }

public enum DockItemKind { App, Folder, File, Spacer, Widget }

public enum WidgetKind
{
    Clock,
    Calendar,
    Weather,
    Note,
    Todo,
    FocusTimer,
    SystemActivity,
    Network,
    Battery,
    Water,
    Countdown,
    TimeProgress,
    NowPlaying,
}

/// <summary>One thing the user put in the Dock: an app, a folder, a file, a spacer or a widget.</summary>
public sealed class DockItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DockItemKind Kind { get; set; }
    public string Path { get; set; } = "";
    public string? Arguments { get; set; }
    public string? Name { get; set; }
    public bool LargeSpacer { get; set; }
    public WidgetKind Widget { get; set; }

    /// <summary>Per-item state, e.g. a note's text or a countdown's target date.</summary>
    public Dictionary<string, string> Data { get; set; } = new();

    public string? Get(string key) => Data.TryGetValue(key, out var v) ? v : null;

    public void Set(string key, string? value)
    {
        if (value is null) Data.Remove(key);
        else Data[key] = value;
    }

    /// <summary>Folders and files sit after the separator, like the right side of a macOS Dock.</summary>
    public bool IsTrailing => Kind is DockItemKind.Folder or DockItemKind.File;
}

public sealed class AppSettings
{
    // Placement
    public DockEdge Edge { get; set; } = DockEdge.Bottom;
    /// <summary>Index into the monitor list; -1 means the primary monitor.</summary>
    public int Monitor { get; set; } = -1;
    public double EdgeGap { get; set; } = 6;

    // Size and motion
    public double IconSize { get; set; } = 48;
    /// <summary>How much the hovered icon grows; 1 turns magnification off.</summary>
    public double Magnification { get; set; } = 1.7;
    public bool BounceOnLaunch { get; set; } = true;
    public double TileWidth { get; set; } = 1.7;

    // Look
    public ThemeMode Theme { get; set; } = ThemeMode.Auto;
    /// <summary>Blur what is behind the bar.</summary>
    public bool Glass { get; set; } = true;
    /// <summary>With <see cref="Glass"/>: Windows' acrylic (tinted, grainy) instead of a plain soft blur.</summary>
    public bool Acrylic { get; set; } = true;
    /// <summary>How opaque the bar's own fill is, 0..1; 0 leaves only the glass.</summary>
    public double BarOpacity { get; set; } = 0.45;
    /// <summary>Liquid Glass rim: specular highlight and a red/cyan fringe that imitates chromatic aberration.</summary>
    public bool LiquidGlass { get; set; }
    public double CornerRadius { get; set; } = 18;
    public string Tint { get; set; } = "";
    public string IndicatorColor { get; set; } = "";
    public bool Reflection { get; set; }

    // Content
    public bool ShowLauncher { get; set; } = true;
    /// <summary>Picture or app whose icon replaces the launcher's dot grid; empty keeps the grid.</summary>
    public string LauncherIcon { get; set; } = "";
    public bool ShowOpenApps { get; set; } = true;
    public bool ShowDownloads { get; set; } = true;
    public bool ShowRecycleBin { get; set; } = true;
    public bool ShowLabels { get; set; } = true;
    /// <summary>Red unread badges from the apps' Windows badges (e.g. WhatsApp's unread count).</summary>
    public bool ShowBadges { get; set; } = true;

    // Behaviour
    public bool AutoHide { get; set; }
    public int AutoHideDelayMs { get; set; } = 250;
    public bool ReserveSpace { get; set; } = true;
    public bool HideOnFullScreen { get; set; } = true;
    public TaskbarMode Taskbar { get; set; } = TaskbarMode.Keep;
    public bool StartWithWindows { get; set; }

    // Widgets that share one setting
    public string WeatherCity { get; set; } = "";
    public double WeatherLatitude { get; set; }
    public double WeatherLongitude { get; set; }
    public bool Fahrenheit { get; set; }

    // Top bar
    public bool TopBarEnabled { get; set; } = true;
    public TopBarStyle TopBarStyle { get; set; } = TopBarStyle.Translucent;
    /// <summary>Fill opacity of the translucent mode, 0..1.</summary>
    public double TopBarOpacity { get; set; } = 0.55;
    public bool TopBarBlur { get; set; } = true;
    public double TopBarHeight { get; set; } = 32;
    public bool TopBarAllMonitors { get; set; } = true;
    public bool TopBarReserve { get; set; } = true;
    /// <summary>Adaptive mode: a soft layer behind the content when the background is mixed.</summary>
    public bool TopBarScrim { get; set; } = true;
    /// <summary>Picture on the bar's left; empty uses the brand mark.</summary>
    public string TopBarLogo { get; set; } = "";
    /// <summary>Paint the logo in the text colour, so it follows light and dark backgrounds.</summary>
    public bool TopBarLogoTint { get; set; } = true;
    /// <summary>Name by the logo; empty uses the Windows display name.</summary>
    public string TopBarUserName { get; set; } = "";
    public bool TopBarShowUser { get; set; } = true;
    public bool TopBarShowApp { get; set; } = true;
    public bool TopBarShowMemory { get; set; } = true;
    /// <summary>.NET date format of the clock.</summary>
    public string TopBarClockFormat { get; set; } = "ddd d MMM, HH:mm";
    /// <summary>Tray icons kept on the bar (lower-case exe paths); the rest live in the tray panel.</summary>
    public List<string> PinnedTray { get; set; } = new();

    // Panels
    /// <summary>Tint over the blurred background of panels, 0..1.</summary>
    public double PanelOpacity { get; set; } = 0.5;
    /// <summary>How soft the panel's glass is: screen pixels merged into one sample.</summary>
    public int PanelBlur { get; set; } = 10;
    public bool PanelRim { get; set; } = true;

    // Applications
    public string AppsHotkey { get; set; } = "Alt+Space";
    public string ControlCenterHotkey { get; set; } = "";
    public List<string> AppsFavorites { get; set; } = new();
    /// <summary>Most recent first.</summary>
    public List<string> AppsRecent { get; set; } = new();

    // Motion
    public MotionMode Motion { get; set; } = MotionMode.Auto;
    /// <summary>1 is normal; higher is quicker.</summary>
    public double MotionSpeed { get; set; } = 1;

    public bool FirstRunDone { get; set; }

    public List<DockItem> Items { get; set; } = new();

    /// <summary>Open apps never shown in the Dock (match keys: lower-case exe path or shell:AppsFolder entry).</summary>
    public List<HiddenApp> HiddenApps { get; set; } = new();
}

public sealed class HiddenApp
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
}
