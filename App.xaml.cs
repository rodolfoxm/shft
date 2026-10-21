using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ClonaDock.Core;
using ClonaDock.Dock;
using ClonaDock.Native;
using ClonaDock.Ui;

namespace ClonaDock;

public partial class App : Application
{
    Mutex? _single;
    DockWindow? _dock;
    TrayIcon? _tray;
    SettingsWindow? _settings;

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

        // `ClonaDock.exe --quit` asks this instance to close cleanly (taskbar and reserved space restored).
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
        Log.Write("ClonaDock starting");

        _dock = new DockWindow();
        _dock.Show();
        _dock.Controller.Start();

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

    void OnSettingsChanged()
    {
        _dock?.Controller.ApplySettings();
        ApplySystemSettings();
    }

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
    }

    public static DockController? Dock => Current._dock?.Controller;

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
            _tray?.Dispose();
            try { _dock?.Close(); } catch (InvalidOperationException) { }
            TaskbarControl.Restore();
            Log.Write("ClonaDock stopped");
        }
        finally
        {
            _single?.Dispose();
            base.OnExit(e);
        }
    }
}
