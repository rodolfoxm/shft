using System.Windows.Forms;
using Shft.Core;

namespace Shft.Native;

public sealed record MonitorInfo(IntPtr Handle, RECT Bounds, RECT WorkArea, bool Primary, string Device, double Scale);

/// <summary>Monitor geometry in physical pixels, plus each monitor's DPI scale.</summary>
public static class Monitors
{
    public static IReadOnlyList<MonitorInfo> All()
    {
        var list = new List<MonitorInfo>();
        foreach (var screen in Screen.AllScreens)
        {
            var b = screen.Bounds;
            var center = new POINT { X = b.Left + b.Width / 2, Y = b.Top + b.Height / 2 };
            var handle = MonitorFromPoint(center, Win32.MONITOR_DEFAULTTONEAREST);
            var info = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
            Win32.GetMonitorInfo(handle, ref info);
            double scale = Win32.GetDpiForMonitor(handle, 0, out uint dpi, out _) == 0 ? dpi / 96.0 : 1.0;
            list.Add(new MonitorInfo(handle, info.rcMonitor, info.rcWork, screen.Primary, screen.DeviceName, scale));
        }
        return list;
    }

    public static MonitorInfo Pick(int index)
    {
        var all = All();
        if (index >= 0 && index < all.Count) return all[index];
        return all.FirstOrDefault(m => m.Primary) ?? all[0];
    }

    public static MonitorInfo ForWindow(IntPtr hwnd)
    {
        var handle = Win32.MonitorFromWindow(hwnd, Win32.MONITOR_DEFAULTTONEAREST);
        return All().FirstOrDefault(m => m.Handle == handle) ?? Pick(-1);
    }

    /// <summary>
    /// A window that comes up over a full-screen game without the player leaving it: Steam's popups, the task
    /// switcher, Start. Anything smaller than 60% of the screen counts; so do Windows' own switching surfaces.
    /// </summary>
    public static bool IsOverlay(string windowClass, RECT window, RECT monitor)
    {
        if (windowClass is "XamlExplorerHostIslandWindow" or "Windows.UI.Core.CoreWindow" or "MultitaskingViewFrame" or "ForegroundStaging") return true;
        long area = (long)Math.Max(0, window.Width) * Math.Max(0, window.Height);
        long screen = (long)monitor.Width * monitor.Height;
        return screen > 0 && area * 10 < screen * 6;
    }

    /// <summary>
    /// The full-screen window that <see cref="IsFullScreen"/> last saw on <paramref name="monitor"/>, updated for the
    /// current foreground: kept while it still covers the screen and only an overlay is in front of it.
    /// </summary>
    public static IntPtr FullScreenWindow(IntPtr last, IntPtr front, MonitorInfo monitor)
    {
        if (IsFullScreen(front, monitor)) return front;
        if (last == IntPtr.Zero || !Win32.IsWindow(last) || !Win32.IsWindowVisible(last) || Win32.IsIconic(last) || !IsFullScreen(last, monitor)) return IntPtr.Zero;
        if (front == IntPtr.Zero) return last;
        Win32.GetWindowRect(front, out var r);
        return IsOverlay(Win32.ClassName(front), r, monitor.Bounds) ? last : IntPtr.Zero;
    }

    /// <summary>"game.exe UnityWndClass (0,0)-(1920,1080)" for the log.</summary>
    public static string Describe(IntPtr window)
    {
        Win32.GetWindowThreadProcessId(window, out uint pid);
        Win32.GetWindowRect(window, out var r);
        return $"{System.IO.Path.GetFileName(Win32.ProcessPath(pid) ?? "?")} {Win32.ClassName(window)} {r}";
    }

    /// <summary>
    /// A full-screen window that Windows put back at the work area's corner instead of the screen's: a game restored
    /// after Alt+Tab is placed in work-area coordinates, so a top or left bar's strip shifts it (CS2 came back at
    /// (0,32) with the mouse off). Only borderless windows exactly the monitor's size, shifted by exactly the strip.
    /// </summary>
    internal static bool Displaced(RECT window, RECT bounds, RECT work, long style)
    {
        const long WS_CAPTION = 0x00C00000;
        if ((style & WS_CAPTION) == WS_CAPTION) return false;
        // at least the monitor's size: CS2 keeps a 1920x1080 window on its 1280x960 mode
        if (window.Width < bounds.Width || window.Height < bounds.Height) return false;
        int dx = window.Left - bounds.Left, dy = window.Top - bounds.Top;
        return (dx != 0 || dy != 0) && dx == work.Left - bounds.Left && dy == work.Top - bounds.Top;
    }

    /// <summary>True when the foreground window is full screen on a monitor that <paramref name="strip"/> touches.</summary>
    public static bool FullScreenOver(RECT strip)
    {
        var fg = Win32.GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        var m = ForWindow(fg);
        return IsFullScreen(fg, m) && Overlaps(m.Bounds, strip);
    }

    internal static bool Overlaps(RECT a, RECT b) => a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

    /// <summary>Puts a <see cref="Displaced"/> window back at the screen's corner. True when it moved one.</summary>
    static ((IntPtr, RECT, RECT) State, long Since) _misfit;

    public static bool SnapDisplaced(IntPtr window)
    {
        if (window == IntPtr.Zero || !Win32.GetWindowRect(window, out var r)) return false;
        Win32.GetWindowThreadProcessId(window, out uint pid);
        if (pid == Environment.ProcessId) return false;
        var m = ForWindow(window);
        long style = Win32.GetStyle(window);
        bool spills = OnAnyMonitor(r.Right - 1, r.Top) && !Inside(r.Right - 1, r.Top, m.Bounds)
                   || OnAnyMonitor(r.Left, r.Bottom - 1) && !Inside(r.Left, r.Bottom - 1, m.Bounds);
        bool displaced = Displaced(r, m.Bounds, m.WorkArea, style), oversized = Oversized(r, m.Bounds, style, spills);
        if (!displaced && !oversized) { _misfit = default; return false; }
        // only a state that lasts: while a game switches resolution its window and the mode change a moment apart
        var seen = (window, r, m.Bounds);
        if (!_misfit.State.Equals(seen)) { _misfit = (seen, Environment.TickCount64); return false; }
        if (Environment.TickCount64 - _misfit.Since < 1500) return false;
        _misfit = default;
        var b = m.Bounds;
        Win32.SetWindowPos(window, IntPtr.Zero, b.Left, b.Top, b.Width, b.Height, Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
        Log.Write($"full screen window {(displaced ? "shifted by a bar's strip" : "bigger than its display mode")}, fitted to {b}: " + Describe(window));
        return true;
    }

    /// <summary>
    /// A borderless full-screen window at the screen's corner but bigger than the monitor's mode, with the extra off
    /// every monitor: CS2 in 4:3 came back from Alt+Tab with its 1920x1080 window on the 1280x960 mode and the mouse
    /// off. A window spanning onto another monitor (a game across screens) is left alone.
    /// </summary>
    internal static bool Oversized(RECT window, RECT bounds, long style, bool spillsOntoAnotherMonitor)
    {
        const long WS_CAPTION = 0x00C00000;
        if ((style & WS_CAPTION) == WS_CAPTION || spillsOntoAnotherMonitor) return false;
        return window.Left == bounds.Left && window.Top == bounds.Top
            && window.Width >= bounds.Width && window.Height >= bounds.Height
            && (window.Width > bounds.Width || window.Height > bounds.Height);
    }

    static bool Inside(int x, int y, RECT r) => x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;

    static bool OnAnyMonitor(int x, int y) => MonitorFromPoint(new POINT { X = x, Y = y }, 0 /* MONITOR_DEFAULTTONULL */) != IntPtr.Zero;

    /// <summary>True when <paramref name="window"/> (normally the foreground one) covers all of the monitor.</summary>
    public static bool IsFullScreen(IntPtr window, MonitorInfo monitor)
    {
        if (window == IntPtr.Zero || window == Win32.GetShellWindow() || window == Win32.GetDesktopWindow()) return false;
        if (Win32.ClassName(window) is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Windows.UI.Core.CoreWindow") return false;
        Win32.GetWindowThreadProcessId(window, out uint pid);
        if (pid == Environment.ProcessId || !Win32.GetWindowRect(window, out var r)) return false;
        var m = ForWindow(window);
        if (m.Handle != monitor.Handle) return false;
        return FullScreenShape(r, m.Bounds, Win32.GetStyle(window), Win32.IsZoomed(window));
    }

    /// <summary>
    /// Covers the monitor and is not just a maximized window with a title bar: with the bars' strip released (an
    /// update, a resolution change) a maximized browser covers the screen too, and taking it for a game kept the
    /// strip from ever coming back.
    /// </summary>
    internal static bool FullScreenShape(RECT window, RECT bounds, long style, bool zoomed)
    {
        const long WS_CAPTION = 0x00C00000;
        if (zoomed && (style & WS_CAPTION) == WS_CAPTION) return false;
        return Covers(window, bounds);
    }

    /// <summary>The window covers the whole monitor (equal or larger, at any resolution or aspect).</summary>
    internal static bool Covers(RECT window, RECT bounds) =>
        window.Left <= bounds.Left && window.Top <= bounds.Top && window.Right >= bounds.Right && window.Bottom >= bounds.Bottom;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    public static string Describe(MonitorInfo m, int index) =>
        $"{index + 1}: {m.Bounds.Width}×{m.Bounds.Height}{(m.Primary ? " (principal)" : "")}";
}
