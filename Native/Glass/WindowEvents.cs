using System.Windows;

namespace Shft.Native.Glass;

/// <summary>
/// Out-of-context window event hooks (other processes only) on the magnifier thread, which pumps their messages.
/// Costs nothing while nothing moves. The callback gets the window's rect, or null when it is gone or hidden.
/// </summary>
public static class WindowEvents
{
    static readonly List<IntPtr> Hooks = new();
    static Win32.WinEventProc? _proc;
    static Action<Int32Rect?>? _onChange;

    public static void Start(Action<Int32Rect?> onWindowChange) => MagnifierHost.Invoke(() =>
    {
        _onChange = onWindowChange;
        if (Hooks.Count > 0) return;
        _proc = OnEvent;
        const uint OutOfContext = 0x0000, SkipOwnProcess = 0x0002;
        foreach (var (min, max) in new (uint, uint)[]
                 {
                     (0x8001, 0x8003), // destroy, show, hide
                     (0x800B, 0x800B), // location change
                     (0x0003, 0x0003), // foreground
                     (0x0016, 0x0017), // minimize start/end
                 })
        {
            var h = Win32.SetWinEventHook(min, max, IntPtr.Zero, _proc, 0, 0, OutOfContext | SkipOwnProcess);
            if (h != IntPtr.Zero) Hooks.Add(h);
        }
    });

    public static void Stop() => MagnifierHost.Invoke(() =>
    {
        foreach (var h in Hooks) Win32.UnhookWinEvent(h);
        Hooks.Clear();
        _onChange = null;
    });

    static void OnEvent(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time)
    {
        if (obj != 0 || child != 0 || hwnd == IntPtr.Zero) return; // OBJID_WINDOW only: no cursor, caret or child objects
        var cb = _onChange;
        if (cb == null) return;
        // a destroyed or hidden window has no useful rect any more: report "somewhere"
        if (ev is 0x8001 or 0x8003 || !Win32.GetWindowRect(hwnd, out var r)) { cb(null); return; }
        cb(new Int32Rect(r.Left, r.Top, Math.Max(0, r.Width), Math.Max(0, r.Height)));
    }
}
