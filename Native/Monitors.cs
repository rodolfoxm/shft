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
    public static bool SnapDisplaced(IntPtr window)
    {
        if (window == IntPtr.Zero || !Win32.GetWindowRect(window, out var r)) return false;
        Win32.GetWindowThreadProcessId(window, out uint pid);
        if (pid == Environment.ProcessId) return false;
        var m = ForWindow(window);
        if (!Displaced(r, m.Bounds, m.WorkArea, Win32.GetStyle(window))) return false;
        Win32.SetWindowPos(window, IntPtr.Zero, m.Bounds.Left, m.Bounds.Top, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
        Log.Write("full screen window shifted by a bar's strip, put back: " + Describe(window));
        return true;
    }

    /// <summary>True when <paramref name="window"/> (normally the foreground one) covers all of the monitor.</summary>
    public static bool IsFullScreen(IntPtr window, MonitorInfo monitor)
    {
        if (window == IntPtr.Zero || window == Win32.GetShellWindow() || window == Win32.GetDesktopWindow()) return false;
        if (Win32.ClassName(window) is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Windows.UI.Core.CoreWindow") return false;
        Win32.GetWindowThreadProcessId(window, out uint pid);
        if (pid == Environment.ProcessId || !Win32.GetWindowRect(window, out var r)) return false;
        var m = ForWindow(window);
        if (m.Handle != monitor.Handle) return false;
        return Covers(r, m.Bounds);
    }

    /// <summary>The window covers the whole monitor (equal or larger, at any resolution or aspect).</summary>
    internal static bool Covers(RECT window, RECT bounds) =>
        window.Left <= bounds.Left && window.Top <= bounds.Top && window.Right >= bounds.Right && window.Bottom >= bounds.Bottom;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    public static string Describe(MonitorInfo m, int index) =>
        $"{index + 1}: {m.Bounds.Width}×{m.Bounds.Height}{(m.Primary ? " (principal)" : "")}";
}
