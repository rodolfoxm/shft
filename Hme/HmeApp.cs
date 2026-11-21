using System.Windows;
using Shft.Core;
using Shft.Native;

namespace Shft.Hme;

/// <summary>The standalone app: shft.'s shared look, one engine, a tray icon and the panel above it.</summary>
public sealed class HmeApp : Application
{
    readonly EngineHost _host;
    readonly bool _showPanel;
    HmeTray? _tray;
    HmePanel? _panel;
    DateTime _panelClosed;

    public HmeApp(EngineHost host, bool showPanel)
    {
        (_host, _showPanel) = (host, showPanel);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Ui/SharedResources.xaml") });
        App.ApplyTheme(Theme.Resolve(SettingsStore.Current));
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Write("hme: unhandled: " + args.Exception);
            args.Handled = true;
        };
        _tray = new HmeTray(_host, this);
        await _host.Start();
        if (_showPanel) ShowPanel(atCursor: false);
    }

    /// <summary>Opens the panel above the tray icon; a click on the icon while it is open closes it.</summary>
    public void TogglePanel()
    {
        if (_panel != null)
        {
            _panel.SafeClose();
            return;
        }
        if ((DateTime.UtcNow - _panelClosed).TotalMilliseconds < 350) return; // that click is what closed it
        ShowPanel();
    }

    /// <summary>Above the tray icon when it was clicked; otherwise by the clock, at the corner of the main screen.</summary>
    public void ShowPanel(bool atCursor = true)
    {
        if (_panel != null)
        {
            _panel.Activate();
            return;
        }
        App.ApplyTheme(Theme.Resolve(SettingsStore.Current)); // follows Windows' light or dark mode as it is now
        Win32.GetCursorPos(out var p);
        if (!atCursor)
        {
            var work = Monitors.Pick(-1).WorkArea;
            (p.X, p.Y) = (work.Right - 200, work.Bottom);
        }
        _panel = new HmePanel(_host);
        _panel.Closed += (_, _) =>
        {
            _panel = null;
            _panelClosed = DateTime.UtcNow;
            MemoryTrim.Soon(Dispatcher);
        };
        _panel.ShowAbove(p.X, p.Y);
    }

    public void Quit()
    {
        _tray?.Dispose();
        _host.Dispose();
        Shutdown();
    }
}
