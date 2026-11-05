using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Shft.Native;

/// <summary>
/// Gives idle memory back to Windows: after startup and after a window (settings, launcher, stacks)
/// closes, the Dock compacts its heap and lets Windows page out what it no longer touches.
/// </summary>
public static class MemoryTrim
{
    [DllImport("kernel32.dll")]
    static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

    static DispatcherTimer? _pending;

    /// <summary>Trims a moment later, once whatever just closed has really been released.</summary>
    public static void Soon(Dispatcher dispatcher, double seconds = 3)
    {
        _pending?.Stop();
        _pending = new DispatcherTimer(TimeSpan.FromSeconds(seconds), DispatcherPriority.ApplicationIdle, (_, _) =>
        {
            _pending?.Stop();
            Now();
        }, dispatcher);
        _pending.Start();
    }

    public static void Now()
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, new IntPtr(-1), new IntPtr(-1));
        }
        catch
        {
            // Purely an optimisation.
        }
    }
}
