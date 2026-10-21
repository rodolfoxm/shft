using System.Diagnostics;
using System.IO;
using System.Windows.Threading;

namespace ClonaDock.Native;

public sealed class AppWindow
{
    public IntPtr Handle { get; init; }
    public string Title { get; set; } = "";
    public string ExePath { get; init; } = "";

    /// <summary>AppUserModelID of a packaged (Microsoft Store) app; null for ordinary programs.</summary>
    public string? AppId { get; init; }

    /// <summary>What to launch and where the icon comes from: the exe, or the app's Start menu entry.</summary>
    public string LaunchPath => AppId != null ? AppsFolder.PathFor(AppId) : ExePath;

    public string Key => LaunchPath.ToLowerInvariant();
}

/// <summary>Packaged apps can't be started from their exe; their Start menu entry can.</summary>
public static class AppsFolder
{
    public const string Prefix = @"shell:AppsFolder\";

    public static string PathFor(string appId) => Prefix + appId;

    public static bool IsAppsFolder(string path) => path.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern int GetApplicationUserModelId(IntPtr process, ref int length, System.Text.StringBuilder? id);

    /// <summary>The AppUserModelID of a packaged process, or null.</summary>
    public static string? AppIdOf(uint pid)
    {
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        var h = Win32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            int len = 0;
            if (GetApplicationUserModelId(h, ref len, null) != 122 /* ERROR_INSUFFICIENT_BUFFER */ || len <= 0) return null;
            var sb = new System.Text.StringBuilder(len);
            return GetApplicationUserModelId(h, ref len, sb) == 0 ? sb.ToString() : null;
        }
        finally
        {
            Win32.CloseHandle(h);
        }
    }
}

/// <summary>
/// Keeps the list of top-level app windows (the ones Alt+Tab would show) and
/// raises <see cref="Changed"/> shortly after Windows reports a change.
/// </summary>
public sealed class WindowTracker : IDisposable
{
    readonly List<IntPtr> _hooks = new();
    readonly Win32.WinEventProc _proc;
    readonly DispatcherTimer _debounce;
    readonly DispatcherTimer _safety;
    readonly uint _ownPid = (uint)Environment.ProcessId;
    readonly Dictionary<uint, string?> _pathCache = new();
    readonly Dictionary<uint, string?> _appIdCache = new();

    public IReadOnlyList<AppWindow> Windows { get; private set; } = Array.Empty<AppWindow>();

    /// <summary>The last foreground window that is not ours; clicks on the Dock do not change it.</summary>
    public IntPtr LastForeground { get; private set; }

    public event Action? Changed;

    public WindowTracker()
    {
        _proc = OnWinEvent;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Refresh(); };
        _safety = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _safety.Tick += (_, _) => Refresh();
    }

    public void Start()
    {
        void Hook(uint min, uint max) =>
            _hooks.Add(Win32.SetWinEventHook(min, max, IntPtr.Zero, _proc, 0, 0,
                Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS));

        Hook(Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND);
        Hook(Win32.EVENT_SYSTEM_MINIMIZESTART, Win32.EVENT_SYSTEM_MINIMIZEEND);
        Hook(Win32.EVENT_OBJECT_DESTROY, Win32.EVENT_OBJECT_HIDE);
        Hook(Win32.EVENT_OBJECT_NAMECHANGE, Win32.EVENT_OBJECT_NAMECHANGE);
        Hook(Win32.EVENT_OBJECT_CLOAKED, Win32.EVENT_OBJECT_UNCLOAKED);
        _safety.Start();
        LastForeground = Win32.GetForegroundWindow();
        Refresh();
    }

    void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero) return;
        if (evt == Win32.EVENT_SYSTEM_FOREGROUND)
        {
            Win32.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != _ownPid) LastForeground = hwnd;
        }
        if (evt == Win32.EVENT_OBJECT_NAMECHANGE && !Windows.Any(w => w.Handle == hwnd)) return;
        if (!_debounce.IsEnabled) _debounce.Start();
    }

    public void Refresh()
    {
        var list = new List<AppWindow>();
        var shell = Win32.GetShellWindow();
        Win32.EnumWindows((hwnd, _) =>
        {
            if (hwnd == shell || !IsAppWindow(hwnd)) return true;
            Win32.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == _ownPid) return true;
            var (path, appPid) = PathFor(hwnd, pid);
            if (string.IsNullOrEmpty(path)) return true;
            list.Add(new AppWindow { Handle = hwnd, Title = Win32.WindowText(hwnd), ExePath = path, AppId = AppIdFor(appPid) });
            return true;
        }, IntPtr.Zero);

        bool same = list.Count == Windows.Count &&
                    list.Zip(Windows).All(p => p.First.Handle == p.Second.Handle && p.First.Title == p.Second.Title);
        Windows = list;
        if (!same) Changed?.Invoke();
    }

    static bool IsAppWindow(IntPtr hwnd)
    {
        if (!Win32.IsWindowVisible(hwnd) || Win32.IsCloaked(hwnd)) return false;
        long ex = Win32.GetExStyle(hwnd);
        long style = Win32.GetStyle(hwnd);
        if ((style & Win32.WS_CHILD) != 0) return false;
        bool appWindow = (ex & Win32.WS_EX_APPWINDOW) != 0;
        if (!appWindow)
        {
            if ((ex & Win32.WS_EX_TOOLWINDOW) != 0) return false;
            if ((ex & Win32.WS_EX_NOACTIVATE) != 0) return false;
            if (Win32.GetWindow(hwnd, Win32.GW_OWNER) != IntPtr.Zero) return false;
        }
        if (Win32.GetWindowTextLength(hwnd) == 0) return false;
        var cls = Win32.ClassName(hwnd);
        return cls is not ("Windows.UI.Core.CoreWindow" or "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd");
    }

    string? CachedPath(uint pid)
    {
        if (!_pathCache.TryGetValue(pid, out var path))
        {
            path = Win32.ProcessPath(pid);
            if (path != null && IsHelper(path) && ParentPath(pid) is string parent) path = parent;
            _pathCache[pid] = path;
            if (_pathCache.Count > 512) _pathCache.Clear();
        }
        return path;
    }

    /// <summary>
    /// Browser-engine helpers that draw an app's windows for it (Steam's steamwebhelper.exe,
    /// "… Helper.exe" of Electron/CEF apps). Their windows belong to the app that started them.
    /// </summary>
    static bool IsHelper(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.EndsWith("helper", StringComparison.OrdinalIgnoreCase) ||
               name.Contains(" Helper", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The parent process's exe, when the parent is still running and is not a shell or service host.</summary>
    static string? ParentPath(uint pid)
    {
        uint parent = ParentPid(pid);
        if (parent == 0) return null;
        var path = Win32.ProcessPath(parent);
        if (path == null) return null;
        var name = Path.GetFileName(path).ToLowerInvariant();
        if (name is "explorer.exe" or "svchost.exe" or "services.exe" or "sihost.exe" or "runtimebroker.exe") return null;
        return IsHelper(path) ? ParentPath(parent) ?? path : path;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    struct PROCESSENTRY32
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32 entry);

    static uint ParentPid(uint pid)
    {
        const uint TH32CS_SNAPPROCESS = 0x2;
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return 0;
        try
        {
            var e = new PROCESSENTRY32 { dwSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32FirstW(snap, ref e)) return 0;
            do
            {
                if (e.th32ProcessID == pid) return e.th32ParentProcessID;
            } while (Process32NextW(snap, ref e));
            return 0;
        }
        finally
        {
            Win32.CloseHandle(snap);
        }
    }

    string? AppIdFor(uint pid)
    {
        if (!_appIdCache.TryGetValue(pid, out var id))
        {
            id = AppsFolder.AppIdOf(pid);
            _appIdCache[pid] = id;
            if (_appIdCache.Count > 512) _appIdCache.Clear();
        }
        return id;
    }

    /// <summary>The app's exe and the process that owns it (for Store apps, not the frame host).</summary>
    (string? Path, uint Pid) PathFor(IntPtr hwnd, uint pid)
    {
        var path = CachedPath(pid);
        if (path != null && Path.GetFileName(path).Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase)
            && HostedAppPid(hwnd, pid) is uint hosted)
            return (CachedPath(hosted) ?? path, hosted);
        return (path, pid);
    }

    /// <summary>Store apps live in a child window owned by another process; that process names the app.</summary>
    static uint? HostedAppPid(IntPtr frame, uint framePid)
    {
        uint? found = null;
        Win32.EnumChildWindows(frame, (child, _) =>
        {
            Win32.GetWindowThreadProcessId(child, out uint pid);
            if (pid != framePid)
            {
                found = pid;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static string DisplayName(string exePath)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exePath);
            if (!string.IsNullOrWhiteSpace(info.FileDescription)) return info.FileDescription.Trim();
        }
        catch { }
        return Path.GetFileNameWithoutExtension(exePath);
    }

    public void Dispose()
    {
        _safety.Stop();
        _debounce.Stop();
        foreach (var h in _hooks) Win32.UnhookWinEvent(h);
        _hooks.Clear();
    }
}
