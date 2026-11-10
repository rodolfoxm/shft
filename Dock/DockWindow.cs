using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Shft.Core;
using Shft.Native;

namespace Shft.Dock;

/// <summary>
/// The transparent strip along a screen edge that hosts the Dock. It never takes focus,
/// reserves screen space through an app bar, follows auto-hide and steps aside for full-screen apps.
/// </summary>
public sealed class DockWindow : Window
{
    readonly DockView _view = new();
    readonly BackdropWindow _backdrop = new();
    readonly DispatcherTimer _poll;
    IntPtr _hwnd;
    HwndSource? _source;
    AppBar? _appBar;
    MonitorInfo _monitor = Monitors.Pick(-1);
    RECT _windowRect;
    bool _fullScreen;
    DateTime? _edgeSince, _awaySince;
    int _tick;

    public DockController Controller { get; }
    public DockView View => _view;
    public MonitorInfo Monitor => _monitor;

    public DockWindow()
    {
        Title = "shft.";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Left = -32000;
        Top = -32000;
        Width = 10;
        Height = 10;
        AllowDrop = true;
        Content = _view;
        // Whole-pixel snapping made magnified icons jump a pixel at a time; let them move smoothly.
        UseLayoutRounding = false;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        RenderOptions.SetEdgeMode(this, EdgeMode.Unspecified);

        Controller = new DockController(this, _view);
        _view.BarRectChanged += _ => SyncBackdrop();
        _view.ContentLengthChanged += length =>
        {
            if (Math.Abs(length - _contentLength) < 1) return;
            _contentLength = length;
            Dispatcher.BeginInvoke(PlaceWindow, DispatcherPriority.Background);
        };
        _poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _poll.Tick += (_, _) => Poll();
        Drop += (_, e) => Controller.HandleDrop(e);
        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
            e.Handled = true;
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source.AddHook(WndProc);
        Win32.AddExStyle(_hwnd, Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE);

        _appBar = new AppBar(_source);
        _appBar.FullScreenChanged += full => { _fullScreen = full; ApplyVisibility(); };
        _appBar.NeedsLayout += () => Dispatcher.BeginInvoke(ApplyLayout, DispatcherPriority.Background);

        // Owned by the backdrop so the Dock always stays above its blur.
        _backdrop.Show();
        Win32.SetWindowLongPtr(_hwnd, Win32.GWLP_HWNDPARENT, _backdrop.Handle);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        _poll.Start();
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_MOUSEACTIVATE = 0x0021, MA_NOACTIVATE = 3;
        const int WM_DISPLAYCHANGE = 0x007E, WM_DPICHANGED = 0x02E0, WM_SETTINGCHANGE = 0x001A;
        switch (msg)
        {
            case WM_MOUSEACTIVATE:
                handled = true;
                return new IntPtr(MA_NOACTIVATE);
            case WM_DISPLAYCHANGE:
            case WM_DPICHANGED:
                Dispatcher.BeginInvoke(ApplyLayout, DispatcherPriority.Background);
                break;
            case WM_SETTINGCHANGE:
                // Windows' colour mode or work area changed.
                Dispatcher.BeginInvoke(() => Controller.ApplySettings(), DispatcherPriority.Background);
                break;
        }
        return IntPtr.Zero;
    }

    /// <summary>Re-reads the settings and places the window, the app bar and the backdrop.</summary>
    public void ApplyLayout()
    {
        if (_hwnd == IntPtr.Zero || _appBar == null) return;
        var s = SettingsStore.Current;
        _monitor = Monitors.Pick(s.Monitor);
        double scale = _monitor.Scale;

        double icon = Math.Clamp(s.IconSize, 24, 128);
        double mag = Math.Clamp(s.Magnification, 1, 2.5);
        double pad = icon * 0.14;
        double gap = Math.Clamp(s.EdgeGap, 0, 40);
        double reserveDip = gap + icon + 2 * pad + 2;
        double windowDip = gap + pad + icon * Math.Max(mag, 1.5) + 48;
        int reservePx = (int)Math.Ceiling(reserveDip * scale);
        int thickPx = (int)Math.Ceiling(windowDip * scale);

        RECT area;
        if (s.ReserveSpace && !s.AutoHide)
        {
            area = _appBar.Reserve(s.Edge, _monitor.Bounds, reservePx);
            // The reserved strip's outer edge is where the Dock starts.
            area = s.Edge switch
            {
                DockEdge.Bottom => new RECT(_monitor.Bounds.Left, _monitor.Bounds.Top, _monitor.Bounds.Right, area.Bottom),
                DockEdge.Top => new RECT(_monitor.Bounds.Left, area.Top, _monitor.Bounds.Right, _monitor.Bounds.Bottom),
                DockEdge.Left => new RECT(area.Left, _monitor.Bounds.Top, _monitor.Bounds.Right, _monitor.Bounds.Bottom),
                _ => new RECT(_monitor.Bounds.Left, _monitor.Bounds.Top, area.Right, _monitor.Bounds.Bottom),
            };
        }
        else
        {
            _appBar.Release();
            _monitor = Monitors.Pick(s.Monitor);
            area = s.Taskbar == TaskbarMode.Keep ? _monitor.WorkArea : _monitor.Bounds;
            if (s.TopBarEnabled && s.TopBarReserve) area.Top = _monitor.WorkArea.Top; // the top bar keeps that strip
        }

        _area = area;
        _thickPx = thickPx;
        PlaceWindow();

        if (!s.AutoHide) _view.SetHidden(false);
        _backdrop.SetStyle(s.Acrylic, Theme.Resolve(s).IsDark);
        ApplyVisibility();
        SyncBackdrop();
    }

    RECT _area;
    int _thickPx;
    double _contentLength;

    /// <summary>
    /// Sizes the window to the Dock rather than the whole screen edge: only this area is redrawn on
    /// every animation frame, so a short Dock costs a fraction of a full-width one.
    /// </summary>
    void PlaceWindow()
    {
        if (_hwnd == IntPtr.Zero || _area.Width <= 0) return;
        var s = SettingsStore.Current;
        bool horizontal = s.Edge is DockEdge.Bottom or DockEdge.Top;
        int axisPx = horizontal ? _area.Width : _area.Height;
        int lengthPx = axisPx;
        if (_contentLength > 0)
        {
            // Room for icons growing under the pointer and for name labels at the ends.
            double icon = Math.Clamp(s.IconSize, 24, 128);
            double margin = icon * (Math.Clamp(s.Magnification, 1, 2.5) - 1) * 2.6 + 120;
            lengthPx = (int)Math.Min(axisPx, Math.Ceiling((_contentLength + 2 * margin) * _monitor.Scale));
        }
        int start = (horizontal ? _area.Left : _area.Top) + (axisPx - lengthPx) / 2;

        var previous = _windowRect;
        _windowRect = s.Edge switch
        {
            DockEdge.Bottom => new RECT(start, _area.Bottom - _thickPx, start + lengthPx, _area.Bottom),
            DockEdge.Top => new RECT(start, _area.Top, start + lengthPx, _area.Top + _thickPx),
            DockEdge.Left => new RECT(_area.Left, start, _area.Left + _thickPx, start + lengthPx),
            _ => new RECT(_area.Right - _thickPx, start, _area.Right, start + lengthPx),
        };
        if (_windowRect.Equals(previous)) return;
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, _windowRect.Left, _windowRect.Top, _windowRect.Width, _windowRect.Height,
            Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
        Log.Write($"dock placed on {_monitor.Device} at {_windowRect} (scale {_monitor.Scale:0.##})");
    }

    /// <summary>Puts the blur exactly under the bar.</summary>
    void SyncBackdrop()
    {
        var s = SettingsStore.Current;
        var bar = _view.BarRect;
        if (!s.Glass || bar.IsEmpty || _fullScreen || !IsVisible || _hwnd == IntPtr.Zero)
        {
            _backdrop.Conceal();
            return;
        }

        // DockView works in DIPs; the backdrop needs physical pixels.
        var topLeft = _view.PointToScreen(bar.TopLeft);
        var bottomRight = _view.PointToScreen(bar.BottomRight);
        var rect = new RECT((int)Math.Round(topLeft.X) + 1, (int)Math.Round(topLeft.Y) + 1,
                            (int)Math.Round(bottomRight.X) - 1, (int)Math.Round(bottomRight.Y) - 1);

        // Clip to the monitor so a sliding (auto-hidden) bar does not blur the next screen.
        rect.Left = Math.Max(rect.Left, _monitor.Bounds.Left);
        rect.Top = Math.Max(rect.Top, _monitor.Bounds.Top);
        rect.Right = Math.Min(rect.Right, _monitor.Bounds.Right);
        rect.Bottom = Math.Min(rect.Bottom, _monitor.Bounds.Bottom);
        if (rect.Width <= 2 || rect.Height <= 2)
        {
            _backdrop.Conceal();
            return;
        }

        double radius = Math.Min(s.CornerRadius, _view.BarThickness / 2) * _monitor.Scale;
        _backdrop.Place(rect, (int)Math.Round(radius));
    }

    void ApplyVisibility()
    {
        bool show = !(_fullScreen && SettingsStore.Current.HideOnFullScreen);
        if (show && !IsVisible) Show();
        Opacity = show ? 1 : 0;
        IsHitTestVisible = show;
        SyncBackdrop();
    }

    /// <summary>Ten times a second: auto-hide, full-screen apps and the taskbar.</summary>
    void Poll()
    {
        var s = SettingsStore.Current;
        _tick++;

        if (_tick % 5 == 0)
        {
            bool full = ForegroundIsFullScreen();
            if (full != _fullScreen)
            {
                _fullScreen = full;
                Log.Write((full ? "full screen app in front: hiding the Dock" : "full screen app gone: the Dock comes back") + " | front: " + Monitors.Describe(Win32.GetForegroundWindow()));
                ApplyVisibility();
            }
        }
        if (_tick % 10 == 0) TaskbarControl.Enforce();

        if (!Win32.GetCursorPos(out var c)) return;
        bool inWindow = c.X >= _windowRect.Left && c.X < _windowRect.Right && c.Y >= _windowRect.Top && c.Y < _windowRect.Bottom;
        if (inWindow) _view.Wake();

        if (!s.AutoHide)
        {
            _edgeSince = _awaySince = null;
            return;
        }

        var b = _monitor.Bounds;
        bool onMonitorAxis = s.Edge is DockEdge.Bottom or DockEdge.Top ? c.X >= b.Left && c.X < b.Right : c.Y >= b.Top && c.Y < b.Bottom;
        bool atEdge = onMonitorAxis && s.Edge switch
        {
            DockEdge.Bottom => c.Y >= b.Bottom - 2,
            DockEdge.Top => c.Y <= b.Top + 1,
            DockEdge.Left => c.X <= b.Left + 1,
            _ => c.X >= b.Right - 2,
        };

        if (_view.IsHidden)
        {
            if (atEdge)
            {
                _edgeSince ??= DateTime.UtcNow;
                if ((DateTime.UtcNow - _edgeSince.Value).TotalMilliseconds >= s.AutoHideDelayMs)
                {
                    _view.SetHidden(false);
                    _edgeSince = null;
                }
            }
            else _edgeSince = null;
            return;
        }

        bool keep = _view.IsPointerOver || _view.IsDragging || Controller.IsBusy || atEdge || NearBar(c);
        if (keep) _awaySince = null;
        else
        {
            _awaySince ??= DateTime.UtcNow;
            if ((DateTime.UtcNow - _awaySince.Value).TotalMilliseconds > 700)
            {
                _view.SetHidden(true);
                _awaySince = null;
            }
        }
    }

    bool NearBar(POINT c)
    {
        if (_view.BarRect.IsEmpty || PresentationSource.FromVisual(_view) == null) return false;
        var p = _view.PointFromScreen(new Point(c.X, c.Y));
        var r = _view.BarRect;
        r.Inflate(12, 12);
        return r.Contains(p);
    }

    bool ForegroundIsFullScreen() => Monitors.IsFullScreen(Win32.GetForegroundWindow(), _monitor);

    protected override void OnClosed(EventArgs e)
    {
        _poll.Stop();
        _appBar?.Dispose();
        Controller.Dispose();
        // On app shutdown WPF may already be closing the backdrop.
        try { _backdrop.Close(); } catch (InvalidOperationException) { }
        base.OnClosed(e);
    }
}
