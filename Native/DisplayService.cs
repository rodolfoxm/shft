using System.Runtime.InteropServices;
using Microsoft.Win32;
using Shft.Core;

namespace Shft.Native;

/// <summary>One screen as the Control Center sees it. <see cref="Brightness"/> is null when the monitor refuses DDC/CI.</summary>
public sealed class ScreenInfo
{
    public required IntPtr Physical { get; init; }
    public required string Name { get; init; }
    public int? Brightness { get; set; }
    public int Min { get; init; }
    public int Max { get; init; } = 100;
}

/// <summary>Brightness over DDC/CI, HDR, dark mode and night light: the real Windows switches.</summary>
public static class DisplayService
{
    // ---------------------------------------------------------------- brightness (DDC/CI, slow: call off the UI thread)

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PhysicalMonitor
    {
        public IntPtr Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
    }

    [DllImport("dxva2.dll")] static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);
    [DllImport("dxva2.dll")] static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count, [Out] PhysicalMonitor[] monitors);
    [DllImport("dxva2.dll")] static extern bool GetMonitorBrightness(IntPtr physical, out uint min, out uint current, out uint max);
    [DllImport("dxva2.dll")] static extern bool SetMonitorBrightness(IntPtr physical, uint value);

    /// <summary>Handles stay valid for the app's life; a display change rebuilds the list.</summary>
    public static List<ScreenInfo> ReadScreens()
    {
        var names = TargetNames();
        var list = new List<ScreenInfo>();
        foreach (var m in Monitors.All())
        {
            try
            {
                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(m.Handle, out uint count) || count == 0) continue;
                var physical = new PhysicalMonitor[count];
                if (!GetPhysicalMonitorsFromHMONITOR(m.Handle, count, physical)) continue;
                foreach (var p in physical)
                {
                    bool ok = GetMonitorBrightness(p.Handle, out uint min, out uint current, out uint max) && max > min;
                    list.Add(new ScreenInfo
                    {
                        Physical = p.Handle,
                        Name = names.TryGetValue(m.Device, out var n) && n.Length > 0 ? n : p.Description,
                        Brightness = ok ? (int)current : null,
                        Min = ok ? (int)min : 0,
                        Max = ok ? (int)max : 100,
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Write("brightness: " + ex.Message);
            }
        }
        return list;
    }

    public static void SetBrightness(ScreenInfo screen, int value)
    {
        if (!SetMonitorBrightness(screen.Physical, (uint)Math.Clamp(value, screen.Min, screen.Max)))
            Log.Write("brightness: the monitor refused the change");
    }

    // ---------------------------------------------------------------- HDR and monitor names (display config)

    [StructLayout(LayoutKind.Sequential)]
    struct Luid { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    struct PathSource { public Luid Adapter; public uint Id, ModeIndex, Flags; }

    [StructLayout(LayoutKind.Sequential)]
    struct PathTarget
    {
        public Luid Adapter;
        public uint Id, ModeIndex, Tech, Rotation, Scaling;
        public uint RefreshNum, RefreshDen, ScanLine, Available, Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PathInfo { public PathSource Source; public PathTarget Target; public uint Flags; }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    struct ModeInfo { [FieldOffset(0)] public uint Type; }

    [StructLayout(LayoutKind.Sequential)]
    struct InfoHeader { public int Type; public uint Size; public Luid Adapter; public uint Id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SourceName
    {
        public InfoHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Gdi;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct TargetName
    {
        public InfoHeader Header;
        public uint Flags, Tech;
        public ushort Mfg, Product;
        public uint Instance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Friendly;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Path;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ColorInfo { public InfoHeader Header; public uint Value, Encoding, Bits; }

    [StructLayout(LayoutKind.Sequential)]
    struct ColorState { public InfoHeader Header; public uint Enable; }

    const uint QdcOnlyActive = 2;
    const int GetSourceName = 1, GetTargetName = 2, GetAdvancedColor = 9, SetAdvancedColor = 10;

    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] PathInfo[] paths, ref uint modeCount, [Out] ModeInfo[] modes, IntPtr topology);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] static extern int GetInfo(ref SourceName info);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] static extern int GetInfo(ref TargetName info);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] static extern int GetInfo(ref ColorInfo info);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigSetDeviceInfo")] static extern int SetInfo(ref ColorState info);

    static PathInfo[] ActivePaths()
    {
        if (GetDisplayConfigBufferSizes(QdcOnlyActive, out uint paths, out uint modes) != 0) return Array.Empty<PathInfo>();
        var list = new PathInfo[paths];
        var modeList = new ModeInfo[modes];
        return QueryDisplayConfig(QdcOnlyActive, ref paths, list, ref modes, modeList, IntPtr.Zero) == 0
            ? list.Take((int)paths).ToArray() : Array.Empty<PathInfo>();
    }

    static InfoHeader Header<T>(int type, Luid adapter, uint id) => new()
    {
        Type = type,
        Size = (uint)Marshal.SizeOf<T>(),
        Adapter = adapter,
        Id = id,
    };

    /// <summary>GDI device name (\\.\DISPLAY1) to the monitor's own name.</summary>
    static Dictionary<string, string> TargetNames()
    {
        var map = new Dictionary<string, string>();
        foreach (var p in ActivePaths())
        {
            var source = new SourceName { Header = Header<SourceName>(GetSourceName, p.Source.Adapter, p.Source.Id) };
            var target = new TargetName { Header = Header<TargetName>(GetTargetName, p.Target.Adapter, p.Target.Id) };
            if (GetInfo(ref source) == 0 && GetInfo(ref target) == 0) map[source.Gdi] = target.Friendly;
        }
        return map;
    }

    /// <summary>Null when no connected monitor can do HDR.</summary>
    public static bool? HdrEnabled()
    {
        bool? result = null;
        foreach (var p in ActivePaths())
        {
            var info = new ColorInfo { Header = Header<ColorInfo>(GetAdvancedColor, p.Target.Adapter, p.Target.Id) };
            if (GetInfo(ref info) != 0 || (info.Value & 1) == 0) continue; // bit 0: supported
            result = (result ?? false) || (info.Value & 2) != 0; // bit 1: enabled
        }
        return result;
    }

    public static void SetHdr(bool on)
    {
        foreach (var p in ActivePaths())
        {
            var info = new ColorInfo { Header = Header<ColorInfo>(GetAdvancedColor, p.Target.Adapter, p.Target.Id) };
            if (GetInfo(ref info) != 0 || (info.Value & 1) == 0) continue;
            var state = new ColorState { Header = Header<ColorState>(SetAdvancedColor, p.Target.Adapter, p.Target.Id), Enable = on ? 1u : 0u };
            int error = SetInfo(ref state);
            if (error != 0) Log.Write("HDR not changed: error " + error);
        }
    }

    // ---------------------------------------------------------------- dark mode

    const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static bool DarkMode()
    {
        using var key = Registry.CurrentUser.OpenSubKey(Personalize);
        return key?.GetValue("AppsUseLightTheme") is 0;
    }

    public static void SetDarkMode(bool dark)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(Personalize))
        {
            key.SetValue("AppsUseLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
            key.SetValue("SystemUsesLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
        }
        const uint WM_SETTINGCHANGE = 0x1A, SMTO_ABORTIFHUNG = 2;
        SendMessageTimeout(new IntPtr(0xFFFF), WM_SETTINGCHANGE, IntPtr.Zero, "ImmersiveColorSet", SMTO_ABORTIFHUNG, 200, out _);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW")]
    static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);

    // ---------------------------------------------------------------- night light

    const string NightLightKey = @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\default$windows.data.bluelightreduction.bluelightreductionstate\windows.data.bluelightreduction.bluelightreductionstate";

    // shortcut: Windows has no public night light API, so this edits its state blob; the format matched this
    // PC's build (26300). If a build changes it, ToggleNightLight returns null and nothing is written.
    const int StateIndex = 18, FlagIndex = 23;
    const byte StateOff = 0x13, StateOn = 0x15;

    internal static bool? NightLightOn(byte[]? blob) => blob == null || blob.Length <= FlagIndex ? null : blob[StateIndex] switch
    {
        StateOn => true,
        StateOff => false,
        _ => null,
    };

    /// <summary>The blob with night light switched; on adds a two-byte field, off removes it, and a timestamp byte moves so Windows notices.</summary>
    internal static byte[]? ToggleNightLight(byte[] blob, bool on)
    {
        if (NightLightOn(blob) is not bool current) return null;
        if (current == on) return blob;
        var data = blob.ToList();
        if (on) data.InsertRange(FlagIndex, new byte[] { 0x10, 0x00 });
        else data.RemoveRange(FlagIndex, 2);
        data[StateIndex] = on ? StateOn : StateOff;
        for (int i = 10; i < 15; i++)
        {
            if (data[i] == 0xFF) continue;
            data[i]++;
            break;
        }
        return data.ToArray();
    }

    public static bool? NightLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(NightLightKey);
        return NightLightOn(key?.GetValue("Data") as byte[]);
    }

    public static void SetNightLight(bool on)
    {
        using var key = Registry.CurrentUser.OpenSubKey(NightLightKey, writable: true);
        if (key?.GetValue("Data") is byte[] blob && ToggleNightLight(blob, on) is { } updated)
            key.SetValue("Data", updated, RegistryValueKind.Binary);
    }
}
