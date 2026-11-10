using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Shft.Core;

namespace Shft.Native;

public sealed class TrayEntry
{
    public required IntPtr Owner { get; init; }
    public required uint Id { get; init; }
    public string? Guid { get; init; }
    public uint CallbackMessage { get; set; }
    public uint Version { get; set; }
    public string Tip { get; set; } = "";
    public ImageSource? Icon { get; set; }
    public bool Hidden { get; set; }
    public string ExePath { get; set; } = "";

    /// <summary>What the panel shows: the app's tip, or its exe name.</summary>
    public string Name => Tip.Split('\n')[0] is { Length: > 0 } first ? first : System.IO.Path.GetFileNameWithoutExtension(ExePath);
    public string PinKey => ExePath.ToLowerInvariant();
}

/// <summary>
/// Sees the icons of the Windows tray. Apps post their icons to the top-level window of class Shell_TrayWnd,
/// so this creates one of its own above Explorer's, reads each message and passes it on unchanged. Explorer keeps
/// working as if nothing happened, and when this process goes away apps reach Explorer directly again.
/// It runs on its own thread because apps wait for the reply: a busy UI must never stall them.
/// </summary>
public sealed class TrayHost : IDisposable
{
    public static TrayHost Instance { get; } = new();

    const int WM_COPYDATA = 0x004A, WM_TIMER = 0x0113;
    const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_STATE = 8, NIF_INFO = 0x10, NIF_GUID = 0x20, NIF_SHOWTIP = 0x80;
    const uint NIS_HIDDEN = 1;
    const int TrayData = 1;
    const uint WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205, WM_CONTEXTMENU = 0x7B, NIN_SELECT = 0x400;
    internal const int MinPayload = 8 + 288;

    readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    readonly List<TrayEntry> _items = new();
    readonly object _gate = new();
    WndProc? _proc;
    IntPtr _hwnd;
    uint _threadId;
    uint _taskbarCreated;
    volatile bool _aside;

    public event Action? Changed;

    public IReadOnlyList<TrayEntry> Items
    {
        get
        {
            List<TrayEntry> shown;
            // Explorer's own icons that never said how to reach them can't be clicked, nor opened another way.
            lock (_gate) shown = _items.Where(i => !i.Hidden && !(i.CallbackMessage == 0 && IsExplorer(i.ExePath))).ToList();
            // An icon whose picture never arrived (it was sent before shft. was listening) still opens its app.
            foreach (var i in shown.Where(i => i.Icon == null && i.ExePath.Length > 0)) i.Icon = IconLoader.Get(i.ExePath, 32);
            return shown.Where(i => i.Icon != null).ToList();
        }
    }

    public void Start()
    {
        if (_hwnd != IntPtr.Zero) return;
        new Thread(Run) { IsBackground = true, Name = "tray host" }.Start();
    }

    void Run()
    {
        _threadId = GetCurrentThreadId();
        _proc = Proc;
        var cls = new WNDCLASS { lpfnWndProc = _proc, lpszClassName = "Shell_TrayWnd", hInstance = GetModuleHandle(null) };
        RegisterClass(ref cls);
        _hwnd = CreateWindowEx(0x80 /* WS_EX_TOOLWINDOW */ | 0x8 /* WS_EX_TOPMOST */, "Shell_TrayWnd", "", 0x80000000 /* WS_POPUP */, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, cls.hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            Log.Write("tray host: window not created " + Marshal.GetLastWin32Error());
            return;
        }
        _taskbarCreated = Win32.RegisterWindowMessage("TaskbarCreated");
        // Apps below our integrity level (and the taskbar message itself) must still get through.
        ChangeWindowMessageFilterEx(_hwnd, WM_COPYDATA, 1, IntPtr.Zero);
        ChangeWindowMessageFilterEx(_hwnd, _taskbarCreated, 1, IntPtr.Zero);
        StayFirst();
        SetTimer(_hwnd, 1, 2000, IntPtr.Zero);
        // Everyone re-announces their icons, exactly as after an Explorer restart; they arrive here first.
        SendNotifyMessage(new IntPtr(0xFFFF), _taskbarCreated, IntPtr.Zero, IntPtr.Zero);
        Log.Write("tray host started");

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        DestroyWindow(_hwnd);
    }

    /// <summary>Lets the next FindWindow from this process reach Explorer. Apps that call meanwhile also go straight there, harmlessly.</summary>
    public void StepAside(bool aside)
    {
        if (_hwnd == IntPtr.Zero) return;
        _aside = aside;
        Win32.SetWindowPos(_hwnd, aside ? new IntPtr(1) /* HWND_BOTTOM */ : Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    /// <summary>FindWindow returns the first match in z-order, so this window has to stay above Explorer's.</summary>
    void StayFirst()
    {
        if (_aside || RealTaskbar() == IntPtr.Zero) return;
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    /// <summary>Explorer's own Shell_TrayWnd, skipping ours.</summary>
    public IntPtr RealTaskbar() => FindTaskbar(_hwnd);

    public static IntPtr FindTaskbar(IntPtr skip)
    {
        var h = IntPtr.Zero;
        while ((h = Win32.FindWindowEx(IntPtr.Zero, h, "Shell_TrayWnd", null)) != IntPtr.Zero)
        {
            // Only Explorer's has the notification area inside; ours (and a dying process's) has no children.
            if (h != skip && Win32.FindWindowEx(h, IntPtr.Zero, "TrayNotifyWnd", null) != IntPtr.Zero) return h;
        }
        return IntPtr.Zero;
    }

    IntPtr Proc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_COPYDATA)
        {
            try
            {
                var data = Marshal.PtrToStructure<COPYDATASTRUCT>(lParam);
                if (data.dwData.ToInt64() == TrayData && data.cbData >= MinPayload)
                {
                    var payload = new byte[data.cbData];
                    Marshal.Copy(data.lpData, payload, 0, data.cbData);
                    Handle(payload);
                }
            }
            catch (Exception ex)
            {
                Log.Write("tray: " + ex.Message); // never let a bad message stop the forwarding below
            }
            // Same message to Explorer, same answer back: tray, app bars and every other shell call keep working.
            const uint SMTO_ABORTIFHUNG = 2;
            return SendMessageTimeout(RealTaskbar(), msg, wParam, lParam, SMTO_ABORTIFHUNG, 1000, out var result) != IntPtr.Zero ? result : IntPtr.Zero;
        }
        if (msg == WM_TIMER)
        {
            if (Win32.FindWindow("Shell_TrayWnd", null) != hwnd) StayFirst();
            return IntPtr.Zero;
        }
        if (msg == _taskbarCreated && _taskbarCreated != 0)
        {
            // Explorer restarted: its new window took the top spot and every icon is coming again.
            lock (_gate) _items.Clear();
            StayFirst();
            _ui.BeginInvoke(() => Changed?.Invoke());
            return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>Decoded form of Shell_NotifyIcon's message; handles travel as 32-bit values even on x64.</summary>
    internal readonly record struct TrayMessage(uint Action, uint Flags, IntPtr Owner, uint Id, uint Callback, IntPtr Icon, string Tip, uint State, uint StateMask, uint Version, string? Guid);

    internal static TrayMessage? Parse(byte[] p)
    {
        if (p.Length < MinPayload || BitConverter.ToUInt32(p, 0) != 0x34753423) return null;
        const int nid = 8;
        uint flags = BitConverter.ToUInt32(p, nid + 12);
        string tip = Encoding.Unicode.GetString(p, nid + 24, 256);
        int end = tip.IndexOf('\0');
        if (end >= 0) tip = tip[..end];
        uint state = p.Length >= nid + 284 ? BitConverter.ToUInt32(p, nid + 280) : 0;
        uint mask = p.Length >= nid + 288 ? BitConverter.ToUInt32(p, nid + 284) : 0;
        uint version = p.Length >= nid + 804 ? BitConverter.ToUInt32(p, nid + 800) : 0;
        string? guid = null;
        if ((flags & NIF_GUID) != 0 && p.Length >= nid + 952) guid = new Guid(p.AsSpan(nid + 936, 16)).ToString();
        return new TrayMessage(BitConverter.ToUInt32(p, 4), flags, new IntPtr(BitConverter.ToInt32(p, nid + 4)), BitConverter.ToUInt32(p, nid + 8),
            BitConverter.ToUInt32(p, nid + 16), new IntPtr(BitConverter.ToInt32(p, nid + 20)), tip, state, mask, version, guid);
    }

    /// <summary>
    /// The protocol an icon speaks. Apps announce it once, when they create the icon, which may have been before shft.
    /// started; Qt apps then only refresh it. Their messages still give it away: NIF_SHOWTIP exists only in version 4,
    /// and without a balloon the version field holds what they set. Getting this wrong loses their clicks.
    /// </summary>
    internal static uint VersionAfter(uint current, TrayMessage m) =>
        (m.Flags & NIF_SHOWTIP) != 0 || (m.Version == 4 && (m.Flags & NIF_INFO) == 0) ? 4 : current;

    void Handle(byte[] payload)
    {
        if (Parse(payload) is not { } m) return;
        // The icon must be copied now: its owner may destroy it right after this call.
        ImageSource? icon = (m.Flags & NIF_ICON) != 0 && m.Icon != IntPtr.Zero ? Copy(m.Icon) : null;
        lock (_gate)
        {
            var existing = _items.FirstOrDefault(i => m.Guid != null ? i.Guid == m.Guid : i.Owner == m.Owner && i.Id == m.Id);
            switch (m.Action)
            {
                case NIM_DELETE:
                    if (existing != null) _items.Remove(existing);
                    break;
                case NIM_SETVERSION:
                    if (existing != null) existing.Version = m.Version;
                    return;
                case NIM_ADD or NIM_MODIFY:
                    if (existing == null)
                    {
                        Win32.GetWindowThreadProcessId(m.Owner, out uint pid);
                        existing = new TrayEntry { Owner = m.Owner, Id = m.Id, Guid = m.Guid, ExePath = Win32.ProcessPath(pid) ?? "" };
                        _items.Add(existing);
                    }
                    existing.Version = VersionAfter(existing.Version, m);
                    if ((m.Flags & NIF_MESSAGE) != 0) existing.CallbackMessage = m.Callback;
                    if ((m.Flags & NIF_TIP) != 0) existing.Tip = m.Tip;
                    if (icon != null) existing.Icon = icon;
                    if ((m.Flags & NIF_STATE) != 0 && (m.StateMask & NIS_HIDDEN) != 0) existing.Hidden = (m.State & NIS_HIDDEN) != 0;
                    break;
                default:
                    return;
            }
        }
        _ui.BeginInvoke(() => Changed?.Invoke());
    }

    static ImageSource? Copy(IntPtr icon)
    {
        try
        {
            var image = Imaging.CreateBitmapSourceFromHIcon(icon, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null; // the owner already destroyed it, or its process runs elevated
        }
    }

    /// <summary>Delivers a click the way Explorer does, so left and right click menus behave as usual.</summary>
    static bool IsExplorer(string path) => Path.GetFileName(path).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase);

    public void Click(TrayEntry e, bool right)
    {
        if (!Win32.IsWindow(e.Owner))
        {
            Log.Write($"tray: {e.Name} is gone, removed");
            lock (_gate) _items.Remove(e);
            Changed?.Invoke();
            return;
        }
        if (e.CallbackMessage == 0)
        {
            // Its icon reached shft. without the message to send clicks to (it was announced before shft. listened).
            // Starting the app again is what brings single-instance apps such as Discord to the front.
            Log.Write($"tray: {e.Name} has no click message, starting {Path.GetFileName(e.ExePath)}");
            try { Process.Start(new ProcessStartInfo(e.ExePath) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Write("tray: could not start it: " + ex.Message); }
            return;
        }
        Win32.GetWindowThreadProcessId(e.Owner, out uint pid);
        AllowSetForegroundWindow(pid);
        Win32.GetCursorPos(out var c);
        void Send(uint m)
        {
            bool v4 = e.Version >= 4;
            var wParam = v4 ? new IntPtr((c.X & 0xFFFF) | (c.Y << 16)) : new IntPtr(e.Id);
            var lParam = v4 ? new IntPtr(m | (e.Id << 16)) : new IntPtr(m);
            Win32.PostMessage(e.Owner, e.CallbackMessage, wParam, lParam);
        }
        Send(right ? WM_RBUTTONDOWN : WM_LBUTTONDOWN);
        Send(right ? WM_RBUTTONUP : WM_LBUTTONUP);
        if (e.Version >= 4) Send(right ? WM_CONTEXTMENU : NIN_SELECT);
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
    }

    delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct COPYDATASTRUCT { public IntPtr dwData; public int cbData; public IntPtr lpData; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASS
    {
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterClassW")] static extern ushort RegisterClass(ref WNDCLASS cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW", SetLastError = true)]
    static extern IntPtr CreateWindowEx(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "GetMessageW")] static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr SetTimer(IntPtr hwnd, IntPtr id, uint ms, IntPtr proc);
    [DllImport("user32.dll", EntryPoint = "SendNotifyMessageW")] static extern bool SendNotifyMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")] static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW")] static extern bool PostThreadMessage(uint thread, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint msg, uint action, IntPtr change);
    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(uint pid);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")] static extern IntPtr GetModuleHandle(string? name);
}
