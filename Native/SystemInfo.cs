using System.Diagnostics;
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

/// <summary>
/// "Open shft. when Windows starts". A logon task, because Windows holds Run-key apps back for up to a minute after
/// sign-in (measured: 60 s here, against 2 s for a task). The Run key stays as the fallback when the task can't be made.
/// </summary>
public static class StartWithWindows
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "shft", OldName = "ClonaDock", TaskName = "shft";

    /// <summary>Runs only for this user, without elevation, at normal priority: the scheduler's default (7) would slow the bar for the whole session.</summary>
    internal static string TaskXml(string user, string exe)
    {
        string u = System.Security.SecurityElement.Escape(user), e = System.Security.SecurityElement.Escape(exe);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Abre o shft. ao entrar no Windows.</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{u}</UserId></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{u}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>{e}</Command><Arguments>--startup</Arguments></Exec></Actions>
            </Task>
            """;
    }

    static bool Schtasks(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        if (p == null) return false;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        return p.WaitForExit(15000) && p.ExitCode == 0;
    }

    static bool CreateTask(string exe)
    {
        var file = Path.Combine(Path.GetTempPath(), $"shft-task-{Environment.ProcessId}.xml");
        try
        {
            File.WriteAllText(file, TaskXml(System.Security.Principal.WindowsIdentity.GetCurrent().Name, exe), System.Text.Encoding.Unicode);
            return Schtasks($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F");
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>Off the UI thread: schtasks takes a moment, and startup must not wait for it.</summary>
    public static void Set(bool on) => Task.Run(() =>
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null) return;
            key.DeleteValue(OldName, throwOnMissingValue: false); // shft. replaces the old Dock at sign-in
            string how;
            if (on && Environment.ProcessPath is string exe)
            {
                bool task = CreateTask(exe);
                if (task) key.DeleteValue(Name, throwOnMissingValue: false);
                else key.SetValue(Name, $"\"{exe}\" --startup");
                how = task ? "logon task" : "Run key (task refused)";
            }
            else
            {
                Schtasks($"/Delete /TN \"{TaskName}\" /F");
                key.DeleteValue(Name, throwOnMissingValue: false);
                how = "off";
            }
            Core.Log.Write($"start with Windows -> {how}");
        }
        catch (Exception ex)
        {
            Core.Log.Write("start with Windows not changed: " + ex.Message);
        }
    });
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
