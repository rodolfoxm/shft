using System.Windows.Forms;
using ClonaDock.Core;

namespace ClonaDock.Native;

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

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    public static string Describe(MonitorInfo m, int index) =>
        $"{index + 1}: {m.Bounds.Width}×{m.Bounds.Height}{(m.Primary ? " (principal)" : "")}";
}
