using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Shft.Core;

namespace Shft.Native;

/// <summary>
/// Hides or auto-hides the Windows taskbar while the Dock runs and puts it back exactly as it was.
/// A marker file and a watchdog process restore it even if the Dock crashes.
/// </summary>
public static class TaskbarControl
{
    sealed record Saved(int State, bool Hidden);

    static string MarkerPath => Path.Combine(SettingsStore.DataFolder, "taskbar-restore.json");

    static TaskbarMode _mode = TaskbarMode.Keep;
    static int? _originalState;

    public static TaskbarMode Mode => _mode;

    static IntPtr Primary => TrayHost.FindTaskbar(IntPtr.Zero);

    static IEnumerable<IntPtr> AllTaskbars()
    {
        var main = Primary;
        if (main != IntPtr.Zero) yield return main;
        IntPtr secondary = IntPtr.Zero;
        while ((secondary = Win32.FindWindowEx(IntPtr.Zero, secondary, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
            yield return secondary;
    }

    static int GetState()
    {
        var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = Primary };
        return (int)Win32.AppBarMessage(Win32.ABM_GETSTATE, ref data).ToUInt32();
    }

    static void SetState(int state)
    {
        var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = Primary, lParam = new IntPtr(state) };
        Win32.AppBarMessage(Win32.ABM_SETSTATE, ref data);
    }

    public static void Apply(TaskbarMode mode)
    {
        if (Primary == IntPtr.Zero) return;
        if (mode == TaskbarMode.Keep)
        {
            Restore();
            _mode = mode;
            return;
        }

        _originalState ??= GetState();
        WriteMarker(new Saved(_originalState.Value, true));

        // Auto-hide frees the work area; Hide also takes the bar off screen entirely.
        SetState(_originalState.Value | Win32.ABS_AUTOHIDE);
        foreach (var bar in AllTaskbars())
            Win32.ShowWindow(bar, mode == TaskbarMode.Hide ? Win32.SW_HIDE : Win32.SW_SHOWNA);
        _mode = mode;
        Log.Write("taskbar -> " + mode);
    }

    /// <summary>Hide mode: the Start key or Explorer can bring the bar back; hide it again once Start is closed.</summary>
    public static void Enforce()
    {
        if (_mode != TaskbarMode.Hide) return;
        var fg = Win32.GetForegroundWindow();
        var cls = fg == IntPtr.Zero ? "" : Win32.ClassName(fg);
        if (cls is "Windows.UI.Core.CoreWindow" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland" or "XamlExplorerHostIslandWindow")
            return;
        foreach (var bar in AllTaskbars())
            if (Win32.IsWindowVisible(bar)) Win32.ShowWindow(bar, Win32.SW_HIDE);
        // Explorer can drop auto-hide when the display wakes or it reloads; without it the work area shrinks.
        if (_originalState is int original && (GetState() & Win32.ABS_AUTOHIDE) == 0)
            SetState(original | Win32.ABS_AUTOHIDE);
    }

    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);

    static Win32.WinEventProc? _showHook;
    static IntPtr _showHookHandle;

    /// <summary>
    /// Re-hides the taskbar the moment Windows shows it again (e.g. when you come back to the PC and the
    /// display wakes), instead of waiting for the next one-second check.
    /// </summary>
    public static void WatchForReappearance()
    {
        if (_showHookHandle != IntPtr.Zero) return;
        _showHook = (_, _, hwnd, idObject, idChild, _, _) =>
        {
            if (_mode != TaskbarMode.Hide || idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero) return;
            var cls = Win32.ClassName(hwnd);
            if (cls is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd")) return;
            // The Win key shows the taskbar together with Start: give Start a moment to take the
            // foreground (Enforce leaves the bar alone then) and never act while Win is held.
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            _ = Task.Delay(250).ContinueWith(_ => dispatcher.BeginInvoke(() =>
            {
                if ((GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0) return;
                Enforce();
            }));
        };
        _showHookHandle = Win32.SetWinEventHook(Win32.EVENT_OBJECT_SHOW, Win32.EVENT_OBJECT_SHOW, IntPtr.Zero, _showHook, 0, 0,
            Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS);
    }

    public static void Restore()
    {
        if (_originalState is int state)
        {
            SetState(state);
            foreach (var bar in AllTaskbars()) Win32.ShowWindow(bar, Win32.SW_SHOWNA);
            _originalState = null;
            Log.Write("taskbar restored");
        }
        _mode = TaskbarMode.Keep;
        DeleteMarker();
    }

    /// <summary>On start: a marker left behind means the last run ended without restoring the taskbar.</summary>
    public static void RecoverFromCrash()
    {
        var saved = ReadMarker();
        if (saved == null) return;
        Log.Write("taskbar: restoring after an unclean exit");
        _originalState = saved.State;
        Restore();
    }

    static void WriteMarker(Saved saved)
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.DataFolder);
            File.WriteAllText(MarkerPath, JsonSerializer.Serialize(saved));
        }
        catch { }
    }

    static Saved? ReadMarker()
    {
        try { return File.Exists(MarkerPath) ? JsonSerializer.Deserialize<Saved>(File.ReadAllText(MarkerPath)) : null; }
        catch { return null; }
    }

    static void DeleteMarker()
    {
        try { File.Delete(MarkerPath); } catch { }
    }

    /// <summary>Starts a copy of this exe that waits for the Dock to exit and restores the taskbar if needed.</summary>
    public static void StartWatchdog()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe == null) return;
            Process.Start(new ProcessStartInfo(exe, $"--watchdog {Environment.ProcessId}") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Log.Write("watchdog not started: " + ex.Message);
        }
    }

    /// <summary>Body of the watchdog process.</summary>
    public static void RunWatchdog(int pid)
    {
        try
        {
            using var dock = Process.GetProcessById(pid);
            dock.WaitForExit();
        }
        catch
        {
            // Already gone.
        }
        Thread.Sleep(500);
        if (ReadMarker() != null)
        {
            RecoverFromCrash();
            Log.Write("watchdog: taskbar restored");
        }
    }
}
