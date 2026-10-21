using System.Runtime.InteropServices;
using System.Windows.Interop;
using ClonaDock.Core;

namespace ClonaDock.Native;

/// <summary>
/// Reserves a strip along a screen edge (SHAppBarMessage), so maximized windows
/// stop at the Dock instead of sliding under it.
/// </summary>
public sealed class AppBar : IDisposable
{
    readonly IntPtr _hwnd;
    readonly uint _callback;
    readonly uint _taskbarCreated;
    bool _registered;
    uint _edge;
    RECT _wanted;

    /// <summary>Raised with true when an app goes full screen on this monitor, false when it leaves.</summary>
    public event Action<bool>? FullScreenChanged;

    /// <summary>Raised when Explorer restarts or the reserved strip moved; the owner re-applies its layout.</summary>
    public event Action? NeedsLayout;

    public RECT Reserved { get; private set; }
    public bool IsRegistered => _registered;

    public AppBar(HwndSource source)
    {
        _hwnd = source.Handle;
        _callback = Win32.RegisterWindowMessage("ClonaDock.AppBarMessage");
        _taskbarCreated = Win32.RegisterWindowMessage("TaskbarCreated");
        source.AddHook(WndProc);
    }

    APPBARDATA Data() => new() { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = _hwnd };

    /// <summary>Reserves <paramref name="thickness"/> physical pixels along <paramref name="edge"/> of the monitor.</summary>
    public RECT Reserve(DockEdge edge, RECT monitor, int thickness)
    {
        if (!_registered)
        {
            var data = Data();
            data.uCallbackMessage = _callback;
            _registered = Win32.SHAppBarMessage(Win32.ABM_NEW, ref data) != UIntPtr.Zero;
            Log.Write("app bar: registered -> " + _registered);
        }

        _edge = edge switch { DockEdge.Left => Win32.ABE_LEFT, DockEdge.Top => Win32.ABE_TOP, DockEdge.Right => Win32.ABE_RIGHT, _ => Win32.ABE_BOTTOM };
        _wanted = monitor;
        var rc = monitor;
        switch (edge)
        {
            case DockEdge.Bottom: rc.Top = rc.Bottom - thickness; break;
            case DockEdge.Top: rc.Bottom = rc.Top + thickness; break;
            case DockEdge.Left: rc.Right = rc.Left + thickness; break;
            case DockEdge.Right: rc.Left = rc.Right - thickness; break;
        }

        var abd = Data();
        abd.uEdge = _edge;
        abd.rc = rc;
        Win32.SHAppBarMessage(Win32.ABM_QUERYPOS, ref abd);

        // The shell may move the strip off other app bars (a visible taskbar); keep our thickness.
        switch (edge)
        {
            case DockEdge.Bottom: abd.rc.Top = abd.rc.Bottom - thickness; break;
            case DockEdge.Top: abd.rc.Bottom = abd.rc.Top + thickness; break;
            case DockEdge.Left: abd.rc.Right = abd.rc.Left + thickness; break;
            case DockEdge.Right: abd.rc.Left = abd.rc.Right - thickness; break;
        }
        Win32.SHAppBarMessage(Win32.ABM_SETPOS, ref abd);
        Reserved = abd.rc;
        return abd.rc;
    }

    public void Release()
    {
        if (!_registered) return;
        var data = Data();
        Win32.SHAppBarMessage(Win32.ABM_REMOVE, ref data);
        _registered = false;
        Reserved = default;
        Log.Write("app bar: released");
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _callback && _registered)
        {
            switch (wParam.ToInt32())
            {
                case Win32.ABN_POSCHANGED:
                    NeedsLayout?.Invoke();
                    break;
                case Win32.ABN_FULLSCREENAPP:
                    FullScreenChanged?.Invoke(lParam != IntPtr.Zero);
                    break;
            }
            handled = true;
        }
        else if (msg == _taskbarCreated)
        {
            Log.Write("app bar: Explorer restarted, registering again");
            _registered = false;
            NeedsLayout?.Invoke();
        }
        return IntPtr.Zero;
    }

    public void Dispose() => Release();
}
