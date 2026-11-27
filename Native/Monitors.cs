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
    public static IntPtr FullScreenWindow(IntPtr front, MonitorInfo monitor)
    {
        if (IsFullScreen(front, monitor))
        {
            if ((Win32.GetStyle(front) & 0x00C00000) != 0x00C00000) _game = front; // borderless: a game
            return front;
        }
        if (IsReturningGame(front) && ForWindow(front).Device == monitor.Device) return front;
        // Not only the focused app: a video full screen on this monitor while you type on the other one is still in
        // front here (the bar came back over a YouTube video when WhatsApp on the second monitor took the focus).
        return TopCovering(monitor);
    }

    internal enum Layer { Skip, FullScreen, Covered }

    /// <summary>
    /// One window on the way down the z-order of a monitor: things that float over a game (small popups, the task
    /// switcher, click-through overlays, tool windows) are skipped; the first real window decides.
    /// </summary>
    internal static Layer Classify(RECT window, RECT monitor, string cls, long style, long exStyle, bool zoomed)
    {
        const long WS_EX_TRANSPARENT = 0x20;
        if (!Overlaps(window, monitor)) return Layer.Skip;
        if ((exStyle & (WS_EX_TRANSPARENT | Win32.WS_EX_TOOLWINDOW)) != 0) return Layer.Skip;
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return Layer.Covered; // the desktop
        if (IsOverlay(cls, window, monitor) && !Covers(window, monitor)) return Layer.Skip;
        if (cls is "XamlExplorerHostIslandWindow" or "Windows.UI.Core.CoreWindow" or "MultitaskingViewFrame" or "ForegroundStaging") return Layer.Skip;
        return FullScreenShape(window, monitor, style, zoomed) ? Layer.FullScreen : Layer.Covered;
    }

    /// <summary>The topmost real window on <paramref name="monitor"/> when it covers the monitor, else zero.</summary>
    static IntPtr TopCovering(MonitorInfo monitor)
    {
        for (var w = GetTopWindow(IntPtr.Zero); w != IntPtr.Zero; w = GetWindow(w, 2 /* GW_HWNDNEXT */))
        {
            if (!Win32.IsWindowVisible(w) || Win32.IsIconic(w) || Cloaked(w) || !Win32.GetWindowRect(w, out var r)) continue;
            Win32.GetWindowThreadProcessId(w, out uint pid);
            if (pid == Environment.ProcessId) continue;
            switch (Classify(r, monitor.Bounds, Win32.ClassName(w), Win32.GetStyle(w), Win32.GetExStyle(w), Win32.IsZoomed(w)))
            {
                case Layer.FullScreen: return w;
                case Layer.Covered: return IntPtr.Zero;
            }
        }
        return IntPtr.Zero;
    }

    static bool Cloaked(IntPtr w) => DwmGetWindowAttribute(w, 14 /* DWMWA_CLOAKED */, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetTopWindow(IntPtr parent);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr w, uint cmd);
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr w, int attribute, out int value, int size);

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
        return (IsFullScreen(fg, m) || IsReturningGame(fg)) && Overlaps(m.Bounds, strip);
    }

    static IntPtr _game;

    /// <summary>
    /// The last borderless full-screen window, taking the front again while still minimized or restoring. CS2 is in
    /// front ~1.6 s before it switches to 4:3 and sizes its window; a top strip still reserved (or redone) at that
    /// moment left its mouse scaled wrong. Treating it as full screen from the first moment releases the strip first.
    /// </summary>
    public static bool IsReturningGame(IntPtr window)
    {
        if (window == IntPtr.Zero || window != _game) return false;
        if (!Win32.IsWindow(window)) { _game = IntPtr.Zero; return false; }
        Win32.GetWindowRect(window, out var r);
        if (Restoring(Win32.IsIconic(window), r)) return true;
        if (!Covers(r, ForWindow(window).Bounds)) _game = IntPtr.Zero; // back as a normal window (a browser out of F11)
        return false;
    }

    /// <summary>Minimized, or still parked off screen where Windows keeps minimized windows (-32000).</summary>
    internal static bool Restoring(bool iconic, RECT window) => iconic || window.Left <= -30000 || window.Top <= -30000;

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
        // shortcut: only moves (never resizes): CS2 in 4:3 keeps a window bigger than its mode on purpose and sized
        // it back every 2 s when shft. fitted it.
        if (!Displaced(r, m.Bounds, m.WorkArea, style)) { _misfit = default; return false; }
        // only a state that lasts: while a game switches resolution its window and the mode change a moment apart
        var seen = (window, r, m.Bounds);
        if (!_misfit.State.Equals(seen)) { _misfit = (seen, Environment.TickCount64); return false; }
        if (Environment.TickCount64 - _misfit.Since < 1500) return false;
        _misfit = default;
        var b = m.Bounds;
        Win32.SetWindowPos(window, IntPtr.Zero, b.Left, b.Top, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
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
        if (m.Device != monitor.Device) return false; // by name: a monitor's handle can change with its resolution
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
