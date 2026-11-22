using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using WinRT;

namespace Shft.Native.Glass;

/// <summary>P/Invoke used only by the real glass (magnifier, timers, plain layer windows, capture interop).</summary>
internal static class GlassNative
{
    public delegate IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public int cbSize; public uint style; public WndProc lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)] public struct MSG { public IntPtr h; public uint m; public IntPtr w, l; public uint time; public int x, y; }
    [StructLayout(LayoutKind.Sequential)] public struct MAGTRANSFORM { public float a, b, c, d, e, f, g, h, i; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassEx(ref WNDCLASSEX c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowEx(uint ex, string cls, string? name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr h, int cmd);
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc proc, IntPtr l);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);

    /// <summary>Visible top-level windows of this process, except the glass layers themselves. Any thread.</summary>
    public static IReadOnlyList<IntPtr> OwnWindows()
    {
        var list = new List<IntPtr>();
        uint me = (uint)Environment.ProcessId;
        var cls = new System.Text.StringBuilder(32);
        EnumWindows((h, _) =>
        {
            if (IsWindowVisible(h) && Win32.GetWindowThreadProcessId(h, out uint pid) != 0 && pid == me)
            {
                cls.Clear();
                GetClassName(h, cls, 32);
                if (cls.ToString() != LayerClass) list.Add(h);
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] public static extern bool InvalidateRect(IntPtr h, IntPtr r, bool erase);
    [DllImport("user32.dll")] public static extern bool PeekMessage(out MSG m, IntPtr h, uint min, uint max, uint remove);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] public static extern IntPtr DispatchMessage(ref MSG m);
    [DllImport("user32.dll")] public static extern uint MsgWaitForMultipleObjects(uint n, IntPtr[] handles, bool all, uint ms, uint mask);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr CreateWaitableTimerEx(IntPtr attr, string? name, uint flags, uint access);
    [DllImport("kernel32.dll")] public static extern bool SetWaitableTimer(IntPtr t, ref long due, int period, IntPtr cb, IntPtr arg, bool resume);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);

    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("magnification.dll")] public static extern bool MagInitialize();
    [DllImport("magnification.dll")] public static extern bool MagUninitialize();
    [DllImport("magnification.dll")] public static extern bool MagSetWindowSource(IntPtr h, RECT r);
    [DllImport("magnification.dll")] public static extern bool MagSetWindowTransform(IntPtr h, ref MAGTRANSFORM t);
    [DllImport("magnification.dll")] public static extern bool MagSetWindowFilterList(IntPtr h, uint mode, int count, IntPtr[] list);

    public const uint WS_POPUP = 0x80000000, WS_VISIBLE = 0x10000000, WS_CHILD = 0x40000000;
    public const uint EX_LAYERED = 0x80000, EX_TOOL = 0x80, EX_NOACTIVATE = 0x08000000, EX_TRANSPARENT = 0x20, EX_NOREDIRECTIONBITMAP = 0x200000;
    public const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOACTIVATE = 0x10, SWP_ASYNCWINDOWPOS = 0x4000;

    public const string LayerClass = "ShftGlassLayer";
    static readonly WndProc Proc = DefWindowProc; // kept alive for the class
    static bool _registered;
    static readonly object RegisterGate = new();

    public static IntPtr Instance => Marshal.GetHINSTANCE(typeof(GlassNative).Module);

    /// <summary>A plain click-through, never-activated popup for the glass and the magnifier host.</summary>
    public static IntPtr CreateLayer(uint extraEx, int x, int y, int w, int h)
    {
        lock (RegisterGate)
        {
            if (!_registered)
            {
                var wc = new WNDCLASSEX { cbSize = Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Proc, hInstance = Instance, lpszClassName = LayerClass };
                RegisterClassEx(ref wc);
                _registered = true;
            }
        }
        return CreateWindowEx(EX_TOOL | EX_NOACTIVATE | EX_TRANSPARENT | extraEx, LayerClass, null, WS_POPUP, x, y, w, h, IntPtr.Zero, IntPtr.Zero, Instance, IntPtr.Zero);
    }

    static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    public static GraphicsCaptureItem CaptureForMonitor(IntPtr monitor) => Item(i => i.CreateForMonitor(monitor, GraphicsCaptureItemIid));
    public static GraphicsCaptureItem CaptureForWindow(IntPtr window) => Item(i => i.CreateForWindow(window, GraphicsCaptureItemIid));

    static GraphicsCaptureItem Item(Func<IGraphicsCaptureItemInterop, IntPtr> create)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var ptr = create(interop);
        try { return GraphicsCaptureItem.FromAbi(ptr); }
        finally { Marshal.Release(ptr); }
    }
}

[ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IGraphicsCaptureItemInterop
{
    IntPtr CreateForWindow([In] IntPtr window, [In] in Guid iid);
    IntPtr CreateForMonitor([In] IntPtr monitor, [In] in Guid iid);
}

[ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDirect3DDxgiInterfaceAccess
{
    IntPtr GetInterface([In] in Guid iid);
}
