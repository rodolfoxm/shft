using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows;

namespace Shft.Native.Glass;

/// <summary>Something the magnifier thread refreshes on its own schedule (one per glass surface).</summary>
public interface IGlassSource
{
    Int32Rect ScreenArea { get; }
    bool Paused { get; }
    long NextRefresh { get; }
    void Refresh();
    void Poke();
}

/// <summary>
/// One thread owns every magnifier of the process: MagInitialize once (with two threads the filter list failed on the
/// second monitor and the glass saw itself). It sleeps on a one-shot high-resolution timer set for the next surface
/// that is due; with nothing active it sleeps until woken.
/// </summary>
public static class MagnifierHost
{
    static Thread? _thread;
    static readonly ConcurrentQueue<Action> Queue = new();
    static readonly AutoResetEvent WakeEvent = new(false);
    static readonly List<IGlassSource> Sources = new();
    static readonly object Gate = new();
    static volatile bool _quit;

    /// <summary>False when MagInitialize failed: every caller falls back to the plain glass.</summary>
    public static bool Available { get; private set; } = true;

    /// <summary>Runs on the magnifier thread and waits for it; exceptions come back to the caller.</summary>
    public static void Invoke(Action action)
    {
        Ensure();
        if (Thread.CurrentThread == _thread) { action(); return; }
        using var done = new ManualResetEventSlim();
        Exception? error = null;
        Queue.Enqueue(() => { try { action(); } catch (Exception ex) { error = ex; } finally { done.Set(); } });
        WakeEvent.Set();
        if (!done.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("magnifier thread did not answer");
        if (error != null) throw error;
    }

    public static void Add(IGlassSource s) { lock (Gate) Sources.Add(s); Wake(); }

    public static void Remove(IGlassSource s) { lock (Gate) Sources.Remove(s); }

    public static void Wake() => WakeEvent.Set();

    /// <summary>Runs on the magnifier thread without waiting (moving hosts, filter lists): the UI never blocks on it.</summary>
    public static void Post(Action action)
    {
        Ensure();
        Queue.Enqueue(() => { try { action(); } catch (Exception ex) { Core.Log.Write("real glass: " + ex.Message); } });
        WakeEvent.Set();
    }

    public static void Shutdown()
    {
        lock (Gate) { if (_thread == null) return; _quit = true; }
        WakeEvent.Set();
        _thread.Join(2000);
    }

    static void Ensure()
    {
        lock (Gate)
        {
            if (_thread != null) return;
            _quit = false;
            _thread = new Thread(Loop) { IsBackground = true, Name = "glass magnifiers" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }
    }

    static void Loop()
    {
        // Initialized only while a magnifier exists: an initialized magnifier left full-screen games (CS2 in 4:3)
        // with the mouse off, so with no glass on screen (a game in front) it is shut down completely.
        Available = GlassNative.MagInitialize();
        if (!Available) Core.Log.Write("real glass: MagInitialize failed");
        else { GlassNative.MagUninitialize(); }
        var timer = GlassNative.CreateWaitableTimerEx(IntPtr.Zero, null, 0x2 /* high resolution */, 0x1F0003);
        var handles = new[] { timer, WakeEvent.SafeWaitHandle.DangerousGetHandle() };
        var list = new List<IGlassSource>();
        while (!_quit)
        {
            while (Queue.TryDequeue(out var a)) a();
            long now = Stopwatch.GetTimestamp(), nextDue = long.MaxValue;
            list.Clear();
            lock (Gate) list.AddRange(Sources);
            foreach (var s in list)
            {
                if (s.Paused) continue;
                try { if (now >= s.NextRefresh) s.Refresh(); }
                catch (Exception ex) { Core.Log.Write($"real glass: refresh failed: {ex.Message}"); }
                nextDue = Math.Min(nextDue, s.NextRefresh);
            }
            if (nextDue != long.MaxValue)
            {
                long due = -Math.Max(1, (nextDue - Stopwatch.GetTimestamp()) * 10_000_000 / Stopwatch.Frequency);
                GlassNative.SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false);
            }
            GlassNative.MsgWaitForMultipleObjects(2, handles, false, 0xFFFFFFFF, 0x04FF /* QS_ALLINPUT */);
            while (GlassNative.PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1))
            {
                GlassNative.TranslateMessage(ref msg);
                GlassNative.DispatchMessage(ref msg);
            }
        }
        while (Queue.TryDequeue(out var a)) a();
        GlassNative.CloseHandle(timer);
        if (_initialized) GlassNative.MagUninitialize();
        _initialized = false;
        lock (Gate) _thread = null;
    }

    /// <summary>
    /// On the magnifier thread: a host window (alpha 1, invisible on screen but on-screen so it keeps updating) with a
    /// magnifier control at 1:1 that leaves out <paramref name="exclude"/> and the host itself.
    /// </summary>
    static bool _initialized; // magnifier thread only
    static int _hosts;

    internal static (IntPtr Host, IntPtr Mag) CreateMagnifier(Int32Rect at, IReadOnlyList<IntPtr> exclude)
    {
        if (!_initialized)
        {
            if (!GlassNative.MagInitialize()) throw new InvalidOperationException("MagInitialize");
            _initialized = true;
        }
        _hosts++;
        var host = GlassNative.CreateLayer(GlassNative.EX_LAYERED, at.X, at.Y, at.Width, at.Height);
        if (host == IntPtr.Zero) throw new InvalidOperationException("magnifier host window");
        GlassNative.SetLayeredWindowAttributes(host, 0, 1, 2 /* LWA_ALPHA */);
        var mag = GlassNative.CreateWindowEx(0, "Magnifier", null, GlassNative.WS_CHILD | GlassNative.WS_VISIBLE,
            0, 0, at.Width, at.Height, host, IntPtr.Zero, GlassNative.Instance, IntPtr.Zero);
        if (mag == IntPtr.Zero) { GlassNative.DestroyWindow(host); throw new InvalidOperationException("magnifier control"); }
        var identity = new GlassNative.MAGTRANSFORM { a = 1, e = 1, i = 1 };
        GlassNative.MagSetWindowTransform(mag, ref identity);
        if (!SetExclusions(mag, host, exclude)) { GlassNative.DestroyWindow(host); throw new InvalidOperationException("magnifier filter list"); }
        return (host, mag);
    }

    /// <summary>On the magnifier thread: destroys a host made by <see cref="CreateMagnifier"/>; the last one shuts the magnifier down.</summary>
    internal static void DestroyMagnifier(IntPtr host)
    {
        GlassNative.DestroyWindow(host);
        if (--_hosts > 0 || !_initialized) return;
        GlassNative.MagUninitialize();
        _initialized = false;
    }

    internal static bool SetExclusions(IntPtr mag, IntPtr host, IReadOnlyList<IntPtr> exclude)
    {
        var list = exclude.Where(h => h != IntPtr.Zero).Append(host).Distinct().ToArray();
        return GlassNative.MagSetWindowFilterList(mag, 0 /* MW_FILTERMODE_EXCLUDE */, list.Length, list);
    }
}
