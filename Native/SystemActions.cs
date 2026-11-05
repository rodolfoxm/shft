using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using Shft.Core;

namespace Shft.Native;

/// <summary>The session and memory actions the bar and the Control Center offer.</summary>
public static class SystemActions
{
    [DllImport("user32.dll")] static extern bool LockWorkStation();
    [DllImport("user32.dll")] static extern bool ExitWindowsEx(uint flags, uint reason);
    [DllImport("secur32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetUserNameExW")] static extern bool GetUserNameEx(int format, StringBuilder name, ref int size);

    public static void Lock() => LockWorkStation();
    public static void SignOut() => ExitWindowsEx(0, 0);
    public static void Sleep() => System.Windows.Forms.Application.SetSuspendState(System.Windows.Forms.PowerState.Suspend, false, false);
    public static void Restart() => Shutdown("/r /t 0");
    public static void PowerOff() => Shutdown("/s /t 0");

    static void Shutdown(string args) =>
        Process.Start(new ProcessStartInfo("shutdown.exe", args) { UseShellExecute = false, CreateNoWindow = true });

    /// <summary>The name Windows shows on the lock screen, or the account name.</summary>
    public static string DisplayName()
    {
        var sb = new StringBuilder(256);
        int size = sb.Capacity;
        return GetUserNameEx(3 /* NameDisplay */, sb, ref size) && sb.Length > 0 ? sb.ToString() : Environment.UserName;
    }

    // ---------------------------------------------------------------- freeing idle memory

    const int SystemMemoryListInformation = 80, EmptyWorkingSets = 2, FlushModifiedList = 3, PurgeStandbyList = 4;

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct TokenPrivileges { public int Count; public long Luid; public int Attributes; }

    [DllImport("ntdll.dll")] static extern int NtSetSystemInformation(int infoClass, ref int info, int length);
    [DllImport("advapi32.dll")] static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "LookupPrivilegeValueW")] static extern bool LookupPrivilegeValue(string? system, string name, out long luid);
    [DllImport("advapi32.dll")] static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges state, int length, IntPtr prev, IntPtr ret);

    /// <summary>The elevated half (<c>shft.exe --purge</c>): empties working sets, flushes and purges the standby list.</summary>
    public static int PurgeElevated()
    {
        const int AdjustAndQuery = 0x0020 | 0x0008, Enabled = 2;
        foreach (var name in new[] { "SeProfileSingleProcessPrivilege", "SeIncreaseQuotaPrivilege" })
        {
            if (!OpenProcessToken(Process.GetCurrentProcess().Handle, AdjustAndQuery, out var token)) continue;
            var state = new TokenPrivileges { Count = 1, Attributes = Enabled };
            if (LookupPrivilegeValue(null, name, out state.Luid))
                AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero);
            Win32.CloseHandle(token);
        }
        int failures = 0;
        foreach (var command in new[] { EmptyWorkingSets, FlushModifiedList, PurgeStandbyList })
        {
            int c = command;
            if (NtSetSystemInformation(SystemMemoryListInformation, ref c, sizeof(int)) != 0) failures++;
        }
        return failures;
    }

    /// <summary>Asks for elevation (Windows shows UAC) and reports how many bytes became available; null if cancelled.</summary>
    public static async Task<double?> PurgeMemoryAsync()
    {
        var before = SystemMeters.Memory();
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--purge") { UseShellExecute = true, Verb = "runas" });
            if (p != null) await p.WaitForExitAsync();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return null; // the user said no to UAC
        }
        catch (Win32Exception ex)
        {
            Log.Write("memory purge: " + ex.Message);
            return null;
        }
        var after = SystemMeters.Memory();
        return Math.Max(0, (double)before.Used - after.Used);
    }
}

/// <summary>One system-wide shortcut. The window's thread receives WM_HOTKEY.</summary>
public sealed class GlobalHotkey : IDisposable
{
    const int WM_HOTKEY = 0x0312;
    static int _nextId = 0x5F00;

    readonly HwndSource _source;
    readonly int _id = _nextId++;
    readonly Action _action;
    bool _registered;

    public GlobalHotkey(HwndSource source, Action action)
    {
        _source = source;
        _action = action;
        source.AddHook(Hook);
    }

    /// <summary>"Alt+Space", "Ctrl+Shift+K". Modifiers in any order; the key by its WPF name.</summary>
    internal static (uint Mods, uint Key)? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        uint mods = 0;
        var key = System.Windows.Input.Key.None;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "alt": mods |= 1; break;
                case "ctrl": mods |= 2; break;
                case "shift": mods |= 4; break;
                case "win": mods |= 8; break;
                default:
                    if (!Enum.TryParse(part, true, out key)) return null;
                    break;
            }
        }
        return mods == 0 || key == System.Windows.Input.Key.None ? null : (mods, (uint)System.Windows.Input.KeyInterop.VirtualKeyFromKey(key));
    }

    /// <summary>False when another app holds the combination.</summary>
    public bool Set(string? text)
    {
        if (_registered) UnregisterHotKey(_source.Handle, _id);
        _registered = false;
        if (Parse(text) is not { } hotkey) return string.IsNullOrWhiteSpace(text);
        const uint MOD_NOREPEAT = 0x4000;
        _registered = RegisterHotKey(_source.Handle, _id, hotkey.Mods | MOD_NOREPEAT, hotkey.Key);
        if (!_registered) Log.Write($"hotkey {text} is taken by another app");
        return _registered;
    }

    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == _id)
        {
            handled = true;
            _action();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_registered) UnregisterHotKey(_source.Handle, _id);
        _source.RemoveHook(Hook);
    }

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}

/// <summary>The classic menu bar of the focused window, for apps that still have one.</summary>
public static class AppMenu
{
    const uint MF_BYPOSITION = 0x400, MFS_DISABLED = 3, MIIM_STATE = 1, MIIM_ID = 2, MIIM_SUBMENU = 4, MIIM_FTYPE = 0x100, MFT_SEPARATOR = 0x800;
    const uint WM_COMMAND = 0x0111;

    public sealed record Entry(string Text, uint Id, IntPtr SubMenu, bool Enabled, bool Separator);

    [StructLayout(LayoutKind.Sequential)]
    struct MENUITEMINFO
    {
        public uint cbSize, fMask, fType, fState, wID;
        public IntPtr hSubMenu, hbmpChecked, hbmpUnchecked, dwItemData, dwTypeData;
        public uint cch;
        public IntPtr hbmpItem;
    }

    [DllImport("user32.dll")] static extern IntPtr GetMenu(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int GetMenuItemCount(IntPtr menu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMenuStringW")] static extern int GetMenuString(IntPtr menu, uint item, StringBuilder text, int max, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMenuItemInfoW")] static extern bool GetMenuItemInfo(IntPtr menu, uint item, bool byPosition, ref MENUITEMINFO info);

    public static IReadOnlyList<Entry> TopLevel(IntPtr window) => Items(GetMenu(window));

    public static IReadOnlyList<Entry> Items(IntPtr menu)
    {
        var list = new List<Entry>();
        if (menu == IntPtr.Zero) return list;
        int count = GetMenuItemCount(menu);
        for (uint i = 0; i < count; i++)
        {
            var info = new MENUITEMINFO { cbSize = (uint)Marshal.SizeOf<MENUITEMINFO>(), fMask = MIIM_ID | MIIM_STATE | MIIM_SUBMENU | MIIM_FTYPE };
            if (!GetMenuItemInfo(menu, i, true, ref info)) continue;
            var sb = new StringBuilder(256);
            GetMenuString(menu, i, sb, sb.Capacity, MF_BYPOSITION);
            list.Add(new Entry(sb.ToString().Split('\t')[0].Replace("&", ""), info.wID, info.hSubMenu, (info.fState & MFS_DISABLED) == 0, (info.fType & MFT_SEPARATOR) != 0));
        }
        return list;
    }

    public static void Invoke(IntPtr window, uint id) => Win32.PostMessage(window, WM_COMMAND, new IntPtr(id), IntPtr.Zero);
}
