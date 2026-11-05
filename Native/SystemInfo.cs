using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Shft.Native;

/// <summary>CPU, memory and network meters shared by the widgets.</summary>
public static class SystemMeters
{
    static ulong _lastIdle, _lastTotal;
    static long _lastRx, _lastTx;
    static DateTime _lastNet = DateTime.MinValue;

    /// <summary>CPU use (0..1) since the previous call.</summary>
    public static double Cpu()
    {
        if (!Win32.GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        ulong total = kernel.Value + user.Value; // kernel time includes idle time
        ulong dIdle = idle.Value - _lastIdle, dTotal = total - _lastTotal;
        bool first = _lastTotal == 0;
        _lastIdle = idle.Value;
        _lastTotal = total;
        if (first || dTotal == 0) return 0;
        return Math.Clamp(1.0 - (double)dIdle / dTotal, 0, 1);
    }

    /// <summary>Memory in use (0..1), and totals in bytes.</summary>
    public static (double Load, ulong Used, ulong Total) Memory()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!Win32.GlobalMemoryStatusEx(ref m)) return (0, 0, 0);
        return (m.dwMemoryLoad / 100.0, m.ullTotalPhys - m.ullAvailPhys, m.ullTotalPhys);
    }

    /// <summary>Bytes per second received and sent across all active interfaces since the previous call.</summary>
    public static (double Down, double Up) Network()
    {
        long rx = 0, tx = 0;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var stats = nic.GetIPStatistics();
                rx += stats.BytesReceived;
                tx += stats.BytesSent;
            }
        }
        catch
        {
            return (0, 0);
        }

        var now = DateTime.UtcNow;
        double seconds = (now - _lastNet).TotalSeconds;
        bool first = _lastNet == DateTime.MinValue;
        var result = first || seconds <= 0 ? (0d, 0d) : (Math.Max(0, (rx - _lastRx) / seconds), Math.Max(0, (tx - _lastTx) / seconds));
        _lastRx = rx;
        _lastTx = tx;
        _lastNet = now;
        return result;
    }

    public static bool IsOnline()
    {
        try { return NetworkInterface.GetIsNetworkAvailable(); }
        catch { return false; }
    }

    public static string Bytes(double value, bool perSecond = false)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int u = 0;
        while (value >= 1024 && u < units.Length - 1) { value /= 1024; u++; }
        var text = value >= 100 || u == 0 ? $"{value:0}" : $"{value:0.#}";
        return $"{text} {units[u]}{(perSecond ? "/s" : "")}";
    }
}

/// <summary>"Open shft. when Windows starts" through the per-user Run key.</summary>
public static class StartWithWindows
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "shft", OldName = "ClonaDock";

    public static void Set(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null) return;
            if (on && Environment.ProcessPath is string exe)
            {
                key.SetValue(Name, $"\"{exe}\"");
                key.DeleteValue(OldName, throwOnMissingValue: false); // shft. replaces the old Dock at sign-in
            }
            else key.DeleteValue(Name, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Core.Log.Write("start with Windows not changed: " + ex.Message);
        }
    }
}

public static class KnownFolders
{
    [DllImport("shell32.dll")]
    static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    public static string Downloads
    {
        get
        {
            if (SHGetKnownFolderPath(DownloadsId, 0, IntPtr.Zero, out var ptr) == 0)
            {
                try { return Marshal.PtrToStringUni(ptr) ?? Fallback; }
                finally { Marshal.FreeCoTaskMem(ptr); }
            }
            return Fallback;
        }
    }

    static string Fallback => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
}
