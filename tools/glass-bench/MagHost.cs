using System.Collections.Concurrent;
using System.Diagnostics;

namespace GlassBench;

/// <summary>
/// One thread owns every magnifier of the process: MagInitialize once, all controls created and refreshed here.
/// It sleeps on a one-shot timer set to the next surface that is due; with every surface paused it sleeps until woken.
/// </summary>
static class MagHost
{
    static Thread _thread;
    static readonly ConcurrentQueue<Action> _queue = new();
    static readonly AutoResetEvent _wake = new(false);
    static readonly List<Surface> _surfaces = new();
    static volatile bool _quit;
    public static bool Hooks;
    public static int HookEvents, HookWakes;
    delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr h);
    static WinEventProc _proc;
    static readonly List<IntPtr> _hooks = new();

    static void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time)
    {
        if (obj != 0 || child != 0 || hwnd == IntPtr.Zero) return; // windows only: no cursor, caret or child objects
        HookEvents++;
        if (!Native.GetWindowRect(hwnd, out var r)) return;
        List<Surface> list;
        lock (_surfaces) list = _surfaces.ToList();
        foreach (var s in list)
        {
            var a = s.ScreenArea;
            bool touches = r.L < a.R && r.R > a.L && r.T < a.B && r.B > a.T;
            // a hidden or destroyed window has no useful rect: wake anyway, it may have been behind the glass
            if (touches || ev is 0x8003 or 0x8001) { HookWakes++; s.Poke(); }
        }
    }

    static void InstallHooks()
    {
        _proc = OnWinEvent;
        const uint OUT = 0x0000, SKIPOWN = 0x0002;
        foreach (var (min, max) in new (uint, uint)[] { (0x8001, 0x8003) /* destroy, show, hide */, (0x800B, 0x800B) /* location change */, (0x0003, 0x0003) /* foreground */, (0x0016, 0x0017) /* minimize */ })
            _hooks.Add(SetWinEventHook(min, max, IntPtr.Zero, _proc, 0, 0, OUT | SKIPOWN));
        Program.Log($"window event hooks: {_hooks.Count(h => h != IntPtr.Zero)}");
    }

    public static void Invoke(Action action)
    {
        Ensure();
        using var done = new ManualResetEventSlim();
        Exception error = null;
        _queue.Enqueue(() => { try { action(); } catch (Exception ex) { error = ex; } finally { done.Set(); } });
        _wake.Set();
        done.Wait();
        if (error != null) throw error;
    }

    public static void Add(Surface s) { lock (_surfaces) _surfaces.Add(s); _wake.Set(); }

    public static void Wake() => _wake.Set();

    public static void Quit() { _quit = true; _wake.Set(); }

    static void Ensure()
    {
        if (_thread != null) return;
        _thread = new Thread(Loop) { IsBackground = true, Name = "magnifiers" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    static void Loop()
    {
        Program.Log($"MagInitialize (once): {Native.MagInitialize()}");
        if (Hooks) InstallHooks();
        var timer = Native.CreateWaitableTimerEx(IntPtr.Zero, null, 0x2, 0x1F0003);
        var handles = new[] { timer, _wake.SafeWaitHandle.DangerousGetHandle() };
        while (!_quit)
        {
            while (_queue.TryDequeue(out var a)) a();
            long now = Stopwatch.GetTimestamp();
            long nextDue = long.MaxValue;
            List<Surface> list;
            lock (_surfaces) list = _surfaces.ToList();
            foreach (var s in list)
            {
                if (s.Paused) continue;
                if (now >= s.NextRefresh) s.Refresh();
                nextDue = Math.Min(nextDue, s.NextRefresh);
            }
            if (nextDue != long.MaxValue)
            {
                long due = -Math.Max(1, (nextDue - Stopwatch.GetTimestamp()) * 10_000_000 / Stopwatch.Frequency);
                Native.SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false);
            }
            Native.MsgWaitForMultipleObjects(2, handles, false, 0xFFFFFFFF, 0x04FF);
            while (Native.PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1))
            {
                Native.TranslateMessage(ref msg);
                Native.DispatchMessage(ref msg);
            }
        }
        foreach (var h in _hooks) UnhookWinEvent(h);
        Native.CloseHandle(timer);
        Native.MagUninitialize();
    }
}
