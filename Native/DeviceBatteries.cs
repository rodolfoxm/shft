using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Shft.Core;

namespace Shft.Native;

public enum DeviceKind { Mouse, Headset, Keyboard, Other }

/// <summary>One wireless device's battery, as last read. <see cref="Percent"/> is null while the device is off or asleep.</summary>
public sealed record DeviceBattery(string Key, string Name, DeviceKind Kind, int? Percent, bool Charging, bool Asleep = false);

/// <summary>
/// Battery of wireless devices read straight from their receivers, without the vendor's software. Protocol facts
/// (report layouts and commands) follow OpenRazer and HeadsetControl as documented by the Wireless Battery project;
/// the code is shft.'s own. Every command here only reads.
/// </summary>
public sealed class DeviceBatteries
{
    public static DeviceBatteries Instance { get; } = new();

    readonly System.Windows.Threading.Dispatcher _ui = System.Windows.Threading.Dispatcher.CurrentDispatcher;
    Timer? _timer;
    int _reading;

    public IReadOnlyList<DeviceBattery> Items { get; private set; } = Array.Empty<DeviceBattery>();
    public event Action? Changed;

    /// <summary>Reads now and then once a minute, off the UI thread: a receiver can take a second to answer.</summary>
    public void Start() => _timer ??= new Timer(_ => Refresh(), null, TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(1));

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public void RefreshSoon() => ThreadPool.QueueUserWorkItem(_ => Refresh());

    void Refresh()
    {
        if (Interlocked.Exchange(ref _reading, 1) == 1) return;
        try
        {
            var read = Merge(Items, Read());
            if (read.SequenceEqual(Items)) return;
            Items = read;
            _ui.BeginInvoke(() => Changed?.Invoke());
        }
        catch (Exception ex)
        {
            Log.Write("batteries: " + ex.Message);
        }
        finally
        {
            _reading = 0;
        }
    }

    /// <summary>A device that stops answering (asleep, switched off) keeps its last level, shown as asleep.</summary>
    internal static List<DeviceBattery> Merge(IReadOnlyList<DeviceBattery> before, List<DeviceBattery> now)
    {
        var result = new List<DeviceBattery>();
        foreach (var d in now)
        {
            var old = before.FirstOrDefault(b => b.Key == d.Key);
            if (d.Percent != null) result.Add(d);
            else if (old?.Percent != null) result.Add(old with { Asleep = true, Charging = false });
        }
        return result;
    }

    public static List<DeviceBattery> Read()
    {
        var list = new List<DeviceBattery>();
        var hids = Hid.All();
        foreach (var group in hids.Where(h => h.Vid == Razer.Vid).GroupBy(h => h.Pid))
            if (Razer.Read(group.ToList()) is { } r) list.Add(r);
        foreach (var group in hids.Where(h => h.Vid == NovaPro.Vid && NovaPro.Pids.Contains(h.Pid)).GroupBy(h => h.Pid))
            if (NovaPro.Read(group.ToList()) is { } n) list.Add(n);
        return list;
    }
}

/// <summary>A HID collection: its path, ids, interface number and report sizes.</summary>
internal sealed record HidInfo(string Path, ushort Vid, ushort Pid, int Interface, ushort UsagePage, int InputLength, int OutputLength, int FeatureLength);

internal static class Hid
{
    public static List<HidInfo> All()
    {
        HidD_GetHidGuid(out var guid);
        var list = new List<HidInfo>();
        if (CM_Get_Device_Interface_List_Size(out int size, ref guid, null, 0) != 0 || size <= 1) return list;
        var buffer = new char[size];
        if (CM_Get_Device_Interface_List(ref guid, null, buffer, size, 0) != 0) return list;
        foreach (var path in new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var lower = path.ToLowerInvariant();
            // Only the brands read here are opened at all; nothing else is touched.
            if (!lower.Contains("vid_1532") && !lower.Contains("vid_1038")) continue;
            using var h = Open(path, 0);
            if (h.IsInvalid) continue;
            var attributes = Attributes();
            if (!HidD_GetAttributes(h, ref attributes)) continue;
            if (!HidD_GetPreparsedData(h, out var data)) continue;
            try
            {
                if (HidP_GetCaps(data, out var caps) != 0x110000) continue; // HIDP_STATUS_SUCCESS
                int mi = lower.IndexOf("&mi_", StringComparison.Ordinal);
                int iface = mi >= 0 ? Convert.ToInt32(lower.Substring(mi + 4, 2), 16) : -1;
                list.Add(new HidInfo(path, attributes.VendorID, attributes.ProductID, iface, caps.UsagePage, caps.InputReportByteLength, caps.OutputReportByteLength, caps.FeatureReportByteLength));
            }
            finally
            {
                HidD_FreePreparsedData(data);
            }
        }
        return list;
    }

    static HIDD_ATTRIBUTES Attributes() => new() { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };

    public static SafeFileHandle Open(string path, uint access, bool overlapped = false) =>
        CreateFile(path, access, 3 /* share read | write */, IntPtr.Zero, 3 /* OPEN_EXISTING */, overlapped ? 0x40000000u : 0, IntPtr.Zero);

    public static string Product(SafeFileHandle h)
    {
        var buffer = new byte[256];
        return HidD_GetProductString(h, buffer, buffer.Length) ? System.Text.Encoding.Unicode.GetString(buffer).TrimEnd('\0') : "";
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID, ProductID, VersionNumber; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices, NumberOutputButtonCaps,
            NumberOutputValueCaps, NumberOutputDataIndices, NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);
    [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr data, out HIDP_CAPS caps);
    [DllImport("hid.dll")] static extern bool HidD_GetProductString(SafeFileHandle h, byte[] buffer, int length);
    [DllImport("hid.dll")] public static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buffer, int length);
    [DllImport("hid.dll")] public static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buffer, int length);
    [DllImport("hid.dll")] public static extern bool HidD_FlushQueue(SafeFileHandle h);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_Interface_List_SizeW")]
    static extern int CM_Get_Device_Interface_List_Size(out int size, ref Guid guid, string? device, int flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_Interface_ListW")]
    static extern int CM_Get_Device_Interface_List(ref Guid guid, string? device, char[] buffer, int length, int flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFileW", SetLastError = true)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint mode, uint flags, IntPtr template);
}

/// <summary>
/// Razer: a 90-byte feature report. [0] status, [1] transaction id, [5] data size, [6] command class, [7] command id,
/// [8..87] arguments, [88] XOR of bytes 2..87. Battery is class 07 id 80 (args[1] = 0..255), charging 07 84.
/// </summary>
internal static class Razer
{
    public const ushort Vid = 0x1532;
    const int Length = 91; // report id 0, then the 90-byte report
    public enum Status { Ok, Busy, Offline, Unsupported, Mismatch }

    public static byte[] Request(byte transaction, byte cls, byte id, byte size)
    {
        var buffer = new byte[Length];
        buffer[2] = transaction;
        buffer[6] = size;
        buffer[7] = cls;
        buffer[8] = id;
        buffer[89] = Checksum(buffer);
        return buffer;
    }

    static byte Checksum(byte[] withReportId)
    {
        byte crc = 0;
        for (int i = 3; i < 89; i++) crc ^= withReportId[i];
        return crc;
    }

    /// <summary>Reads a reply to a request; another program (Synapse) shares the mailbox, so a foreign reply is a mismatch.</summary>
    public static Status Parse(byte[] reply, byte transaction, byte cls, byte id, byte size, out byte[] args)
    {
        args = Array.Empty<byte>();
        if (reply.Length < Length) return Status.Mismatch;
        if (reply[2] != transaction || reply[3] != 0 || reply[4] != 0 || reply[5] != 0 || reply[6] != size || reply[7] != cls || reply[8] != id || reply[89] != Checksum(reply))
            return Status.Mismatch;
        switch (reply[1])
        {
            case 0x02: args = reply[9..89]; return Status.Ok;
            case 0x01: return Status.Busy;
            case 0x04: return Status.Offline;
            default: return Status.Unsupported;
        }
    }

    public static int Percent(byte raw) => Math.Clamp((raw * 100 + 127) / 255, 0, 100);

    static Status Command(SafeFileHandle h, byte transaction, byte cls, byte id, out byte[] args)
    {
        args = Array.Empty<byte>();
        var request = Request(transaction, cls, id, 0x02);
        var status = Status.Mismatch;
        bool send = true;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            if (send && !Hid.HidD_SetFeature(h, request, request.Length)) return Status.Mismatch;
            Thread.Sleep(attempt == 0 ? 15 : 40);
            send = true;
            var reply = new byte[Length];
            if (!Hid.HidD_GetFeature(h, reply, reply.Length)) continue;
            status = Parse(reply, transaction, cls, id, 0x02, out args);
            if (status == Status.Ok || status == Status.Unsupported) return status;
            if (status == Status.Busy) send = false; // let this request finish
        }
        return status;
    }

    public static DeviceBattery? Read(List<HidInfo> collections)
    {
        foreach (var c in collections.Where(c => c.FeatureLength == Length))
        {
            using var h = Hid.Open(c.Path, 0);
            if (h.IsInvalid) continue;
            foreach (byte transaction in new byte[] { 0x1F, 0x3F, 0xFF, 0x08, 0x9F })
            {
                var status = Command(h, transaction, 0x07, 0x80, out var level);
                if (status == Status.Unsupported) break;
                if (status == Status.Offline) return new DeviceBattery(Key(c), Name(h), KindOf(Name(h)), null, false);
                if (status != Status.Ok) continue;
                bool charging = Command(h, transaction, 0x07, 0x84, out var ch) == Status.Ok && ch.Length > 1 && ch[1] == 1;
                var name = Name(h);
                return new DeviceBattery(Key(c), name, KindOf(name), Percent(level[1]), charging);
            }
        }
        return null;
    }

    static string Key(HidInfo c) => $"razer:{c.Pid:X4}";

    internal static string Clean(string name)
    {
        foreach (var suffix in new[] { " HyperSpeed Dongle", " Dongle", " (Wired)", " (Wireless)", " Wireless Receiver", " Receiver" })
            if (name.EndsWith(suffix, StringComparison.Ordinal)) return name[..^suffix.Length];
        return name.Length > 0 ? name : "Razer";
    }

    static string Name(SafeFileHandle h) => Clean(Hid.Product(h));

    internal static DeviceKind KindOf(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("blackshark") || n.Contains("barracuda") || n.Contains("kraken") || n.Contains("headset")) return DeviceKind.Headset;
        if (n.Contains("huntsman") || n.Contains("blackwidow") || n.Contains("deathstalker") || n.Contains("ornata") || n.Contains("keyboard")) return DeviceKind.Keyboard;
        return DeviceKind.Mouse;
    }
}

/// <summary>Arctis Nova Pro Wireless base station, interface 4: send 06 B0; reply [6] level 0..8, [15] 01 off / 02 charging.</summary>
internal static class NovaPro
{
    public const ushort Vid = 0x1038;
    public static readonly ushort[] Pids = { 0x12E0, 0x12E5, 0x225D };

    public static DeviceBattery? Parse(byte[] reply, ushort pid)
    {
        if (reply.Length < 16) return null;
        bool off = reply[15] == 0x01;
        int? percent = off ? null : Math.Clamp(reply[6] * 100 / 8, 0, 100);
        return new DeviceBattery($"steelseries:{pid:X4}", "Arctis Nova Pro Wireless", DeviceKind.Headset, percent, reply[15] == 0x02);
    }

    public static DeviceBattery? Read(List<HidInfo> collections)
    {
        foreach (var c in collections.Where(c => c.Interface == 4 && c.OutputLength > 0 && c.InputLength > 0))
        {
            using var h = Hid.Open(c.Path, 0xC0000000 /* read | write */, overlapped: true);
            if (h.IsInvalid) continue;
            using var stream = new FileStream(h, FileAccess.ReadWrite, 1, isAsync: true);
            Hid.HidD_FlushQueue(h);
            var request = new byte[c.OutputLength];
            request[0] = 0x06;
            request[1] = 0xB0;
            try
            {
                stream.Write(request, 0, request.Length);
                var reply = new byte[c.InputLength];
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                int n = stream.ReadAsync(reply, 0, reply.Length, timeout.Token).GetAwaiter().GetResult();
                if (n >= 16) return Parse(reply, c.Pid);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                Log.Write("batteries: Arctis did not answer: " + ex.Message);
            }
        }
        return null;
    }
}
