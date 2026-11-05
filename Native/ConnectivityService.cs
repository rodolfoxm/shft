using System.Net.NetworkInformation;
using System.Windows.Threading;
using Shft.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using Windows.Devices.WiFi;

namespace Shft.Native;

public sealed record BluetoothEntry(string Name, bool Connected);

public sealed record WifiEntry(WiFiAvailableNetwork Network, WiFiAdapter Adapter, string Name, double Signal, bool Connected);

/// <summary>Bluetooth and Wi-Fi radios plus connection state, from Windows' events rather than polling.</summary>
public sealed class ConnectivityService
{
    public static ConnectivityService Instance { get; } = new();

    readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    Radio? _bluetooth, _wifi;

    public event Action? Changed;

    public bool BluetoothAvailable => _bluetooth != null;
    public bool BluetoothOn => _bluetooth?.State == RadioState.On;
    public bool WifiAvailable => _wifi != null;
    public bool WifiOn => _wifi?.State == RadioState.On;
    public bool Loaded { get; private set; }

    /// <summary>"Ethernet", "Wi-Fi" or null when offline.</summary>
    public string? Connection { get; private set; }

    public async Task StartAsync()
    {
        NetworkChange.NetworkAddressChanged += (_, _) => _ui.BeginInvoke(RefreshConnection);
        RefreshConnection();
        try
        {
            await Radio.RequestAccessAsync();
            foreach (var radio in await Radio.GetRadiosAsync())
            {
                if (radio.Kind == RadioKind.Bluetooth) _bluetooth = radio;
                else if (radio.Kind == RadioKind.WiFi) _wifi = radio;
                else continue;
                radio.StateChanged += (_, _) => _ui.BeginInvoke(Raise);
            }
        }
        catch (Exception ex)
        {
            Log.Write("radios: " + ex.Message);
        }
        Loaded = true;
        Raise();
    }

    void Raise() => Changed?.Invoke();

    void RefreshConnection()
    {
        string? now = null;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.GetIPProperties().GatewayAddresses.Count == 0) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) { now = "Wi-Fi"; break; }
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet) now = "Ethernet";
            }
        }
        catch (NetworkInformationException ex)
        {
            Log.Write("network: " + ex.Message);
        }
        if (now == Connection) return;
        Connection = now;
        Raise();
    }

    /// <summary>False when Windows refused; the caller shows the real state either way.</summary>
    public static async Task<bool> SetRadio(Radio? radio, bool on)
    {
        if (radio == null) return false;
        try
        {
            return await radio.SetStateAsync(on ? RadioState.On : RadioState.Off) == RadioAccessStatus.Allowed;
        }
        catch (Exception ex)
        {
            Log.Write("radio not changed: " + ex.Message);
            return false;
        }
    }

    public Task<bool> SetBluetooth(bool on) => SetRadio(_bluetooth, on);

    public Task<bool> SetWifi(bool on) => SetRadio(_wifi, on);

    public static async Task<List<BluetoothEntry>> PairedDevicesAsync()
    {
        var list = new List<BluetoothEntry>();
        try
        {
            foreach (var info in await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true)))
            {
                using var device = await BluetoothDevice.FromIdAsync(info.Id);
                list.Add(new BluetoothEntry(info.Name, device?.ConnectionStatus == BluetoothConnectionStatus.Connected));
            }
        }
        catch (Exception ex)
        {
            Log.Write("bluetooth devices: " + ex.Message);
        }
        return list.OrderByDescending(d => d.Connected).ThenBy(d => d.Name).ToList();
    }

    /// <summary>Networks in range; empty when there is no Wi-Fi hardware or Windows denies access.</summary>
    public static async Task<List<WifiEntry>> ScanWifiAsync()
    {
        var list = new List<WifiEntry>();
        try
        {
            if (await WiFiAdapter.RequestAccessAsync() != WiFiAccessStatus.Allowed) return list;
            foreach (var adapter in await WiFiAdapter.FindAllAdaptersAsync())
            {
                await adapter.ScanAsync();
                var profile = await adapter.NetworkAdapter.GetConnectedProfileAsync();
                foreach (var n in adapter.NetworkReport.AvailableNetworks.Where(n => n.Ssid.Length > 0))
                {
                    if (list.Any(e => e.Name == n.Ssid)) continue;
                    list.Add(new WifiEntry(n, adapter, n.Ssid, n.SignalBars / 4.0, profile?.WlanConnectionProfileDetails?.GetConnectedSsid() == n.Ssid));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write("wifi scan: " + ex.Message);
        }
        return list.OrderByDescending(e => e.Connected).ThenByDescending(e => e.Signal).ToList();
    }

    /// <summary>Connects with Windows' saved password; a network it doesn't know goes to Windows' own picker.</summary>
    public static async Task<bool> ConnectAsync(WifiEntry entry)
    {
        try
        {
            var result = await entry.Adapter.ConnectAsync(entry.Network, WiFiReconnectionKind.Automatic);
            return result.ConnectionStatus == WiFiConnectionStatus.Success;
        }
        catch (Exception ex)
        {
            Log.Write("wifi connect: " + ex.Message);
            return false;
        }
    }
}
