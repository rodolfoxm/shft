namespace Shft.Hme;

/// <summary>What the panel draws. Ac is null when the user said there is no air conditioner.</summary>
public sealed record HomeState(bool Demo, string RoomName, int? RoomTemp, AcState? Ac, IReadOnlyList<Device> Devices,
    IReadOnlyList<string> Rooms, string? Notice);

public sealed record DiscoveredDevice(string Id, string Name, string OriginalName, string Kind, bool Online,
    IReadOnlyList<string> Channels, bool Hidden, string Room, bool RoomManual, string? Source = null);

public sealed record DiscoveredAc(string Type, string Name, string InfraredId = "", string RemoteId = "", string DeviceId = "", string Hub = "");

public sealed record Sensor(string Id, string Name);

/// <summary>core.py's discover(): everything in the account, for the settings window.</summary>
public sealed record Discovery(IReadOnlyList<DiscoveredDevice> Devices, IReadOnlyList<DiscoveredAc> Acs,
    IReadOnlyList<Sensor> Sensors, IReadOnlyList<string> Rooms, string IrError);

public sealed record TestResult(bool Ok, string? Region, int Count, bool Linked, string? Error);

public sealed record PositivoResult(bool Ok, int Count, IReadOnlyList<string> Names, string? Error);

/// <summary>A home: the real one (Tuya and Positivo) or the demo.</summary>
public interface IHome
{
    Task<HomeState> State(bool cacheOnly = false);
    Task<HomeState> SetAc(AcChange change);
    Task<HomeState> SetDevice(string id, LightChange? light = null, bool? on = null, string? channel = null);
    Task<HomeState> LightsOff(string? room = null);
    Task<Discovery> Discover();
    Task<string> Diagnose();
    Task<string> TestAc();
    void MarkActivity();
}
