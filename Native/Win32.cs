using System.Runtime.InteropServices;
using System.Text;

namespace Shft.Native;

[StructLayout(LayoutKind.Sequential)]
public struct RECT
{
    public int Left, Top, Right, Bottom;
    public RECT(int l, int t, int r, int b) { Left = l; Top = t; Right = r; Bottom = b; }
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public override string ToString() => $"({Left},{Top})-({Right},{Bottom})";
}

[StructLayout(LayoutKind.Sequential)]
public struct POINT
{
    public int X, Y;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct MONITORINFOEX
{
    public int cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public uint dwFlags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string szDevice;
}

[StructLayout(LayoutKind.Sequential)]
public struct APPBARDATA
{
    public int cbSize;
    public IntPtr hWnd;
    public uint uCallbackMessage;
    public uint uEdge;
    public RECT rc;
    public IntPtr lParam;
}

[StructLayout(LayoutKind.Sequential)]
public struct FILETIME
{
    public uint Low, High;
    public ulong Value => ((ulong)High << 32) | Low;
}

[StructLayout(LayoutKind.Sequential)]
public struct MEMORYSTATUSEX
{
    public uint dwLength;
    public uint dwMemoryLoad;
    public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
}

[StructLayout(LayoutKind.Sequential)]
public struct WindowCompositionAttributeData
{
    public int Attribute;
    public IntPtr Data;
    public int SizeOfData;
}

[StructLayout(LayoutKind.Sequential)]
public struct AccentPolicy
{
    public int AccentState;
    public int AccentFlags;
    public uint GradientColor;
    public int AnimationId;
}

public static class Win32
{
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;
    public const int GWLP_HWNDPARENT = -8;
    public const long WS_EX_TOOLWINDOW = 0x00000080;
    public const long WS_EX_APPWINDOW = 0x00040000;
    public const long WS_EX_NOACTIVATE = 0x08000000;
    public const long WS_EX_TOPMOST = 0x00000008;
    public const long WS_CHILD = 0x40000000;
    public const long WS_DISABLED = 0x08000000;

    public const int SW_HIDE = 0;
    public const int SW_SHOWNORMAL = 1;
    public const int SW_SHOWMINIMIZED = 2;
    public const int SW_SHOWNOACTIVATE = 4;
    public const int SW_SHOW = 5;
    public const int SW_MINIMIZE = 6;
    public const int SW_SHOWNA = 8;
    public const int SW_RESTORE = 9;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_NOOWNERZORDER = 0x0200;
    public static readonly IntPtr HWND_TOPMOST = new(-1);

    public const uint GW_OWNER = 4;
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_SYSCOMMAND = 0x0112;
    public const int SC_CLOSE = 0xF060;

    public const uint MONITOR_DEFAULTTONEAREST = 2;

    // AppBar messages and notifications
    public const uint ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3, ABM_GETSTATE = 4, ABM_GETTASKBARPOS = 5, ABM_SETSTATE = 10;
    public const int ABN_STATECHANGE = 0, ABN_POSCHANGED = 1, ABN_FULLSCREENAPP = 2;
    public const uint ABE_LEFT = 0, ABE_TOP = 1, ABE_RIGHT = 2, ABE_BOTTOM = 3;
    public const int ABS_AUTOHIDE = 1, ABS_ALWAYSONTOP = 2;

    // WinEvents
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    public const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    public const uint EVENT_OBJECT_CREATE = 0x8000;
    public const uint EVENT_OBJECT_DESTROY = 0x8001;
    public const uint EVENT_OBJECT_SHOW = 0x8002;
    public const uint EVENT_OBJECT_HIDE = 0x8003;
    public const uint EVENT_OBJECT_NAMECHANGE = 0x800C;
    public const uint EVENT_OBJECT_CLOAKED = 0x8017;
    public const uint EVENT_OBJECT_UNCLOAKED = 0x8018;
    public const uint WINEVENT_OUTOFCONTEXT = 0;
    public const uint WINEVENT_SKIPOWNPROCESS = 2;

    public const int DWMWA_CLOAKED = 14;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWA_BORDER_COLOR = 34;

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    public delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr hWnd, bool altTab);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string? cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);
    [DllImport("user32.dll")] public static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] public static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] public static extern int SetWindowRgn(IntPtr hWnd, IntPtr rgn, bool redraw);
    [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern int SetWindowCompositionAttribute(IntPtr hWnd, ref WindowCompositionAttributeData data);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("gdi32.dll")] public static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);

    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    [DllImport("shell32.dll")] static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);

    /// <summary>App bar call from this process. Through <see cref="TrayHost"/> Explorer stores the position but
    /// never recomputes the work area (measured), so the stand-in steps aside and the call reaches Explorer itself.</summary>
    public static UIntPtr AppBarMessage(uint msg, ref APPBARDATA data)
    {
        // Only the calls that change the work area. The taskbar check runs every second, and every step aside is a
        // moment in which a tray icon that appears reaches Explorer only and never shows up in shft.
        if (msg is not (ABM_NEW or ABM_REMOVE or ABM_QUERYPOS or ABM_SETPOS)) return SHAppBarMessage(msg, ref data);
        TrayHost.Instance.StepAside(true);
        try { return SHAppBarMessage(msg, ref data); }
        finally { TrayHost.Instance.StepAside(false); }
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int SHEmptyRecycleBin(IntPtr hwnd, string? root, uint flags);

    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hWnd, int attr, ref int value, int size);

    [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] public static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);
    [DllImport("kernel32.dll")] public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

    public static long GetExStyle(IntPtr hwnd) => GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
    public static long GetStyle(IntPtr hwnd) => GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();

    public static void AddExStyle(IntPtr hwnd, long add) =>
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(GetExStyle(hwnd) | add));

    public static string WindowText(IntPtr hwnd)
    {
        int len = GetWindowTextLength(hwnd);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static bool IsCloaked(IntPtr hwnd) =>
        DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    public static string? ProcessPath(uint pid)
    {
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint attach, uint attachTo, bool on);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern IntPtr SetActiveWindow(IntPtr hWnd);

    /// <summary>
    /// Brings a window forward even when another process holds the foreground. Windows' focus-stealing
    /// protection refuses a plain SetForegroundWindow from the (never-activated) Dock now and then, so
    /// this escalates: plain call, then sharing input with the current foreground thread, then an Alt tap.
    /// Returns false when every attempt was refused.
    /// </summary>
    public static bool Activate(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        else if (!IsWindowVisible(hwnd)) ShowWindow(hwnd, SW_SHOW);
        if (IsForeground(hwnd)) return true;
        if (SetForegroundWindow(hwnd) && IsForeground(hwnd)) return true;

        // Joining the foreground thread's input queue makes Windows treat us as the foreground for the switch.
        uint self = GetCurrentThreadId();
        uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        uint targetThread = GetWindowThreadProcessId(hwnd, out _);
        bool attachedFg = fgThread != 0 && fgThread != self && AttachThreadInput(self, fgThread, true);
        bool attachedTarget = targetThread != 0 && targetThread != self && targetThread != fgThread && AttachThreadInput(self, targetThread, true);
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
            SetActiveWindow(hwnd);
        }
        finally
        {
            if (attachedTarget) AttachThreadInput(self, targetThread, false);
            if (attachedFg) AttachThreadInput(self, fgThread, false);
        }
        if (IsForeground(hwnd)) return true;

        // Last resort: a tapped Alt key counts as fresh user input, which lifts the lock.
        const byte VK_MENU = 0x12;
        const uint KEYEVENTF_KEYUP = 0x2;
        keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
        keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        if (SetForegroundWindow(hwnd) && IsForeground(hwnd)) return true;
        SwitchToThisWindow(hwnd, true);
        return IsForeground(hwnd);
    }

    static bool IsForeground(IntPtr hwnd)
    {
        var fg = GetForegroundWindow();
        // Some apps hand focus to an owned window (dialog) right away; that still counts.
        return fg == hwnd || (fg != IntPtr.Zero && GetWindow(fg, GW_OWNER) == hwnd);
    }

    /// <summary>
    /// Asks a window to close. False when Windows blocks the request: windows of apps running as
    /// administrator ignore messages from normal apps (UIPI).
    /// </summary>
    public static bool RequestClose(IntPtr hwnd)
    {
        if (PostMessage(hwnd, WM_SYSCOMMAND, new IntPtr(SC_CLOSE), IntPtr.Zero)) return true;
        return PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MARGINS { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS margins);

    /// <summary>
    /// Windows 11's own acrylic (the one its flyouts use). Unlike the older accent policy it is
    /// clipped to the window's shape. Returns false where the system backdrop is not available.
    /// </summary>
    public static bool EnableSystemBackdrop(IntPtr hwnd, bool dark)
    {
        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
        const int DWMSBT_TRANSIENTWINDOW = 3;
        var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref margins);
        int darkMode = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
        int type = DWMSBT_TRANSIENTWINDOW;
        return DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref type, sizeof(int)) == 0;
    }

    public static void DisableSystemBackdrop(IntPtr hwnd)
    {
        const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
        int none = 1; // DWMSBT_NONE
        DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref none, sizeof(int));
    }

    /// <summary>Asks DWM for an acrylic blur of what is behind the window (Windows 10 1803+ / 11).</summary>
    public static void EnableBlur(IntPtr hwnd, bool on, bool acrylic = true)
    {
        const int WCA_ACCENT_POLICY = 19;
        const int ACCENT_DISABLED = 0;
        const int ACCENT_ENABLE_BLURBEHIND = 3;
        const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;
        // Plain blur-behind has no tint or grain; acrylic adds both. The bar draws its own tint,
        // so the acrylic's colour is almost clear (ABGR, alpha 0x01).
        var accent = new AccentPolicy
        {
            AccentState = !on ? ACCENT_DISABLED : acrylic ? ACCENT_ENABLE_ACRYLICBLURBEHIND : ACCENT_ENABLE_BLURBEHIND,
            GradientColor = 0x01101010,
        };
        int size = Marshal.SizeOf(accent);
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new WindowCompositionAttributeData { Attribute = WCA_ACCENT_POLICY, Data = ptr, SizeOfData = size };
            SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }
}
