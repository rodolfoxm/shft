using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Interop;
using Shft.Bar;
using Shft.Core;
using Shft.Dock;
using Shft.Native;
using Shft.Ui;

namespace Shft;

public partial class App : Application
{
    Mutex? _single;
    DockWindow? _dock;
    TrayIcon? _tray;
    SettingsWindow? _settings;
    MenuBars? _bars;
    HwndSource? _hotkeyHost;
    GlobalHotkey? _appsHotkey, _controlHotkey;

    public static new App Current => (App)Application.Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Program.Main already handled helper runs and the single-instance check.
        _single = Program.SingleInstance;
        var quit = new EventWaitHandle(false, EventResetMode.AutoReset, Program.QuitEvent);
        var showSettings = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowSettingsEvent);
        new Thread(() =>
        {
            while (showSettings.WaitOne())
                Dispatcher.BeginInvoke(() => ShowSettings());
        }) { IsBackground = true, Name = "settings listener" }.Start();

        Listen(Program.ToggleAppsEvent, () => SpotlightWindow.Toggle());
        Listen(Program.ToggleControlsEvent, () => _bars?.ToggleControlCenter());

        // `shft.exe --quit` asks this instance to close cleanly (taskbar and reserved space restored).
        new Thread(() =>
        {
            quit.WaitOne();
            Dispatcher.BeginInvoke(() => Shutdown());
        }) { IsBackground = true, Name = "quit listener" }.Start();

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Write("unhandled: " + args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Write("fatal: " + args.ExceptionObject);
            TaskbarControl.Restore();
        };
        SessionEnding += (_, _) => TaskbarControl.Restore();

        TaskbarControl.RecoverFromCrash();
        var settings = SettingsStore.Load();
        Log.Write("shft. starting");

        _dock = new DockWindow();
        _dock.Show();
        _dock.Controller.Start();

        TrayHost.Instance.Start();
        _ = ConnectivityService.Instance.StartAsync();
        _ = NotificationService.Instance.StartAsync();
        _bars = new MenuBars();
        _bars.Start();
        AppCatalog.Refresh();

        _hotkeyHost = new HwndSource(new HwndSourceParameters("shft. hotkeys") { Width = 0, Height = 0, WindowStyle = 0 });
        _hotkeyHost.AddHook(OnHostMessage);
        _appsHotkey = new GlobalHotkey(_hotkeyHost, () => SpotlightWindow.Toggle());
        _controlHotkey = new GlobalHotkey(_hotkeyHost, () => _bars?.ToggleControlCenter());

        ApplySystemSettings(); // also starts the taskbar watchdog when the taskbar gets hidden

        _tray = new TrayIcon();
        SettingsStore.Changed += OnSettingsChanged;
        MemoryTrim.Soon(Dispatcher, 5); // startup allocations are done by then

        if (!settings.FirstRunDone)
        {
            settings.FirstRunDone = true;
            SettingsStore.Save(notify: false);
            Dispatcher.BeginInvoke(() => ShowSettings(), DispatcherPriority.ApplicationIdle);
        }
    }

    void Listen(string eventName, Action action)
    {
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
        new Thread(() =>
        {
            while (signal.WaitOne())
                Dispatcher.BeginInvoke(action);
        }) { IsBackground = true, Name = eventName }.Start();
    }

    void OnSettingsChanged()
    {
        _dock?.Controller.ApplySettings();
        try
        {
            _bars?.Apply();
        }
        catch (Exception ex)
        {
            Log.Write("top bar not updated: " + ex); // hotkeys, startup and the taskbar below must still apply
        }
        ApplySystemSettings();
    }

    /// <summary>Monitors, wallpaper and colour mode changes reach this hidden window like any top-level one.</summary>
    IntPtr OnHostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_SETTINGCHANGE = 0x001A, WM_DISPLAYCHANGE = 0x007E;
        if (msg == WM_DISPLAYCHANGE)
        {
            ControlCenterWindow.ForgetScreens();
            Dispatcher.BeginInvoke(() => _bars?.Apply(), DispatcherPriority.Background);
        }
        else if (msg == WM_SETTINGCHANGE)
        {
            Dispatcher.BeginInvoke(() => _bars?.Apply(), DispatcherPriority.Background);
        }
        return IntPtr.Zero;
    }

    string? _appliedAppsKey, _appliedControlKey;

    TaskbarMode _appliedTaskbar = TaskbarMode.Keep;
    bool? _appliedStartup;

    void ApplySystemSettings()
    {
        var s = SettingsStore.Current;
        if (s.Taskbar != _appliedTaskbar)
        {
            bool wasKeep = _appliedTaskbar == TaskbarMode.Keep;
            TaskbarControl.Apply(s.Taskbar);
            TaskbarControl.WatchForReappearance();
            _appliedTaskbar = s.Taskbar;
            if (wasKeep && s.Taskbar != TaskbarMode.Keep && _dock != null) TaskbarControl.StartWatchdog();
            // The work area changes once the taskbar moves; lay the Dock out again.
            _dock?.Dispatcher.BeginInvoke(() => _dock.ApplyLayout(), DispatcherPriority.Background);
        }
        if (_appliedAppsKey != s.AppsHotkey)
        {
            _appliedAppsKey = s.AppsHotkey;
            AppsHotkeyFree = _appsHotkey?.Set(s.AppsHotkey) ?? true;
        }
        if (_appliedControlKey != s.ControlCenterHotkey)
        {
            _appliedControlKey = s.ControlCenterHotkey;
            ControlsHotkeyFree = _controlHotkey?.Set(s.ControlCenterHotkey) ?? true;
        }
        if (_appliedStartup != s.StartWithWindows)
        {
            StartWithWindows.Set(s.StartWithWindows);
            _appliedStartup = s.StartWithWindows;
        }
    }

    public static void ShowSettings(string? page = null)
    {
        var app = Current;
        if (app._settings == null || !app._settings.IsLoaded)
        {
            app._settings = new SettingsWindow();
            app._settings.Closed += (_, _) =>
            {
                app._settings = null;
                MemoryTrim.Soon(app.Dispatcher);
            };
            app._settings.Show();
        }
        if (page != null) app._settings.Select(page);
        app._settings.Activate();
    }

    public static void ApplyTheme(Theme theme)
    {
        var r = Current.Resources;
        r["MenuBackBrush"] = Theme.Brush(theme.MenuBack);
        r["MenuTextBrush"] = Theme.Brush(theme.MenuText);
        r["MenuSubtleBrush"] = Theme.Brush(Color.FromArgb(0xA0, theme.MenuText.R, theme.MenuText.G, theme.MenuText.B));
        r["MenuHoverBrush"] = Theme.Brush(theme.MenuHover);
        r["MenuBorderBrush"] = Theme.Brush(theme.MenuSeparator);
        r["MenuSeparatorBrush"] = Theme.Brush(theme.MenuSeparator);
        r["AccentBrush"] = Theme.Brush(theme.Accent);
        r["PanelTintBrush"] = Theme.Brush(theme.PanelTint);
        r["TileBrush"] = Theme.Brush(theme.TileBack);
        r["TileHoverBrush"] = Theme.Brush(theme.TileHover);
        r["TileOnBrush"] = Theme.Brush(theme.TileOn);
        r["TileOnTextBrush"] = Theme.Brush(theme.TileOnText);
        r["TileRimBrush"] = Theme.Brush(theme.TileRim);
        r["TextBrush"] = Theme.Brush(theme.Text);
        r["TextSubtleBrush"] = Theme.Brush(theme.TextSubtle);
    }

    public static DockController? Dock => Current._dock?.Controller;

    public static MenuBars? Bars => Current._bars;

    public static bool AppsHotkeyFree { get; private set; } = true;

    public static bool ControlsHotkeyFree { get; private set; } = true;

    public static void Notify(string title, string text) => Current._tray?.Notify(title, text);

    protected override void OnExit(ExitEventArgs e)
    {
        // Helper runs (watchdog, --quit, a second launch) own no Dock and must not touch the taskbar marker.
        if (_dock == null)
        {
            _single?.Dispose();
            base.OnExit(e);
            return;
        }
        try
        {
            _appsHotkey?.Dispose();
            _controlHotkey?.Dispose();
            _hotkeyHost?.Dispose();
            _bars?.Dispose();
            TrayHost.Instance.Dispose();
            _tray?.Dispose();
            try { _dock?.Close(); } catch (InvalidOperationException) { }
            TaskbarControl.Restore();
            Log.Write("shft. stopped");
        }
        finally
        {
            _single?.Dispose();
            base.OnExit(e);
        }
    }
}
