using System.Runtime.InteropServices;
using System.Windows.Threading;
using Shft.Core;

namespace Shft.Native;

public enum AudioFlow { Output = 0, Input = 1 }

public sealed record AudioDevice(string Id, string Name);

/// <summary>
/// Windows' volume, mute and default devices through Core Audio. Everything is event driven: Windows calls
/// back on its own threads, this class hops to the UI thread and raises <see cref="Changed"/>.
/// </summary>
public sealed class AudioService : IDisposable
{
    const int RoleConsole = 0, RoleMultimedia = 1, RoleCommunications = 2;
    const int DeviceActive = 1;
    const int ClsCtxAll = 23;
    static readonly Guid VolumeIid = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    /// <summary>After <see cref="VolumeIid"/>: statics initialise in file order and the constructor needs it.</summary>
    public static AudioService Instance { get; } = new();

    readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    readonly IMMDeviceEnumerator _devices = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
    readonly NotificationClient _client;
    readonly VolumeCallback _callback;
    readonly IAudioEndpointVolume?[] _volume = new IAudioEndpointVolume?[2];
    Guid _context = Guid.NewGuid();

    public event Action? Changed;

    AudioService()
    {
        _client = new NotificationClient(Reattach);
        _callback = new VolumeCallback(() => _ui.BeginInvoke(() => Changed?.Invoke()));
        _devices.RegisterEndpointNotificationCallback(_client);
        Attach();
    }

    void Reattach() => _ui.BeginInvoke(() =>
    {
        Attach();
        Changed?.Invoke();
    });

    void Attach()
    {
        foreach (var flow in new[] { AudioFlow.Output, AudioFlow.Input })
        {
            int i = (int)flow;
            try { _volume[i]?.UnregisterControlChangeNotify(_callback); } catch (COMException) { }
            _volume[i] = null;
            if (_devices.GetDefaultAudioEndpoint((int)flow, RoleMultimedia, out var device) != 0 || device == null) continue;
            var iid = VolumeIid;
            int hr = device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out var obj);
            if (hr != 0)
            {
                Log.Write($"audio: volume of {flow} not reachable: 0x{hr:X8}");
                continue;
            }
            var volume = (IAudioEndpointVolume)obj;
            Marshal.ThrowExceptionForHR(volume.RegisterControlChangeNotify(_callback));
            _volume[i] = volume;
        }
    }

    public bool Available(AudioFlow flow) => _volume[(int)flow] != null;

    public double Level(AudioFlow flow)
    {
        if (_volume[(int)flow] is not { } v) return 0;
        v.GetMasterVolumeLevelScalar(out float level);
        return level;
    }

    public bool Muted(AudioFlow flow)
    {
        if (_volume[(int)flow] is not { } v) return false;
        v.GetMute(out bool muted);
        return muted;
    }

    public void SetLevel(AudioFlow flow, double level) =>
        _volume[(int)flow]?.SetMasterVolumeLevelScalar((float)Math.Clamp(level, 0, 1), ref _context);

    public void SetMuted(AudioFlow flow, bool muted) =>
        _volume[(int)flow]?.SetMute(muted, ref _context);

    public string? DefaultId(AudioFlow flow)
    {
        if (_devices.GetDefaultAudioEndpoint((int)flow, RoleMultimedia, out var d) != 0 || d == null) return null;
        d.GetId(out var id);
        return id;
    }

    public IReadOnlyList<AudioDevice> Devices(AudioFlow flow)
    {
        var list = new List<AudioDevice>();
        if (_devices.EnumAudioEndpoints((int)flow, DeviceActive, out var all) != 0 || all == null) return list;
        all.GetCount(out uint count);
        for (uint i = 0; i < count; i++)
        {
            all.Item(i, out var d);
            d.GetId(out var id);
            list.Add(new AudioDevice(id, FriendlyName(d) ?? id));
        }
        return list;
    }

    static string? FriendlyName(IMMDevice device)
    {
        if (device.OpenPropertyStore(0, out var store) != 0) return null;
        var key = new PropertyKey { Format = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), Id = 14 };
        if (store.GetValue(ref key, out var value) != 0) return null;
        try { return value.Vt == 31 ? Marshal.PtrToStringUni(value.Pointer) : null; }
        finally { PropVariantClear(ref value); }
    }

    /// <summary>Makes a device the default for every role, as Windows' own sound panel does.</summary>
    public void SetDefault(string id)
    {
        try
        {
            // shortcut: IPolicyConfig is undocumented but stable since Vista; replace it if Windows ever offers a public call.
            var policy = (IPolicyConfig)new PolicyConfigCo();
            foreach (var role in new[] { RoleConsole, RoleMultimedia, RoleCommunications })
                Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(id, role));
        }
        catch (Exception ex)
        {
            Log.Write("audio: default device not changed: " + ex.Message);
        }
    }

    public void Dispose()
    {
        try
        {
            _devices.UnregisterEndpointNotificationCallback(_client);
            foreach (var v in _volume) v?.UnregisterControlChangeNotify(_callback);
        }
        catch (COMException) { }
    }

    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant value);

    sealed class VolumeCallback : IAudioEndpointVolumeCallback
    {
        readonly Action _changed;
        public VolumeCallback(Action changed) => _changed = changed;
        public int OnNotify(IntPtr data)
        {
            _changed();
            return 0;
        }
    }

    sealed class NotificationClient : IMMNotificationClient
    {
        readonly Action _changed;
        public NotificationClient(Action changed) => _changed = changed;
        public int OnDeviceStateChanged(string id, int state) { _changed(); return 0; }
        public int OnDeviceAdded(string id) { _changed(); return 0; }
        public int OnDeviceRemoved(string id) { _changed(); return 0; }
        public int OnDefaultDeviceChanged(int flow, int role, string? id) { _changed(); return 0; }
        public int OnPropertyValueChanged(string id, PropertyKey key) => 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PropertyKey { public Guid Format; public int Id; }

    [StructLayout(LayoutKind.Sequential)]
    struct PropVariant
    {
        public ushort Vt;
        public ushort R1, R2, R3;
        public IntPtr Pointer;
        public IntPtr Extra;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCo { }

    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    class PolicyConfigCo { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, int mask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object result);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback callback);
        [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback callback);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float db, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float db);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float db, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float db);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolumeCallback
    {
        [PreserveSig] int OnNotify(IntPtr data);
    }

    [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, int state);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? id);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PropertyKey key);
    }

    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, int isDefault, IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr format, IntPtr mix);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, int isDefault, IntPtr def, IntPtr min);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
    }
}
