using System.Text.Json.Nodes;

namespace Shft.Hme;

/// <summary>core.py's Demo: a made-up home to try everything without an account.</summary>
public sealed class DemoHome : IHome
{
    readonly HmeConfig _cfg;
    readonly List<Device> _devices;
    AcState _ac;

    public DemoHome(HmeConfig cfg)
    {
        _cfg = cfg;
        static LightState L(string mode = "white", int h = 30, int s = 0, int brightness = 80) => new(true, true, true, mode, brightness, 40, h, s);
        static Device D(string id, string name, DeviceKind kind, bool on, bool online = true, LightState? light = null, IReadOnlyList<Channel>? channels = null) =>
            new(id, name, name, online, kind == DeviceKind.Plug ? "cz" : "kg", kind, on, light, channels ?? []);
        _devices =
        [
            D("d1", "Luz da Sala", DeviceKind.Switch, true),
            D("d2", "Fita LED da Sala", DeviceKind.Light, true, light: L("colour", 270, 90, 70)),
            D("d3", "Tomada da TV", DeviceKind.Plug, true),
            D("d4", "Luz da Cozinha", DeviceKind.Switch, false),
            D("d5", "Cafeteira da Cozinha", DeviceKind.Plug, false),
            D("d6", "Luz do Quarto", DeviceKind.Switch, false),
            D("d7", "Abajur do Quarto", DeviceKind.Light, true, light: L()),
            D("d8", "Interruptor do Escritório", DeviceKind.Multi, true, channels: [new("switch_1", "Tecla 1", true), new("switch_2", "Tecla 2", false)]),
            D("d9", "Luminária do Escritório", DeviceKind.Light, false, online: false, light: L()),
        ];
        _ac = new AcState(true, "cool", 18, "auto", true, true, "", cfg.AcModes, cfg.TempMin, cfg.TempMax, HmeConfig.Str(cfg.Ac["model"]));
    }

    public void MarkActivity() { }

    List<Device> Named() => _devices.Select(d =>
    {
        var name = HmeConfig.Str(_cfg.Names[d.Id]) is { Length: > 0 } n ? n : d.OriginalName;
        var names = _cfg.ChannelNames[d.Id] as JsonArray;
        var channels = d.Channels.Select((c, i) => names != null && i < names.Count && HmeConfig.Str(names[i]) is { Length: > 0 } cn ? c with { Name = cn } : c).ToList();
        return d with { Name = name, Channels = channels };
    }).ToList();

    public Task<HomeState> State(bool cacheOnly = false)
    {
        var v = Devices.Visible(Named(), _cfg, new Dictionary<string, string>());
        var ac = _cfg.HasAc ? _ac with { Modes = _cfg.AcModes, TempMin = _cfg.TempMin, TempMax = _cfg.TempMax } : null;
        var room = HmeConfig.Str(_cfg.Ac["room_name"]) is { Length: > 0 } rn ? rn : "Quarto";
        return Task.FromResult(new HomeState(true, room, _cfg.HasAc ? 26 : null, ac, v, Rooms.Order(v, _cfg), null));
    }

    public Task<HomeState> SetAc(AcChange c)
    {
        _ac = _ac with
        {
            Power = c.Power ?? _ac.Power, Mode = c.Mode ?? _ac.Mode, Fan = c.Fan ?? _ac.Fan, Turbo = c.Turbo ?? _ac.Turbo,
            Temp = Math.Clamp(c.Temp ?? _ac.Temp, _cfg.TempMin, _cfg.TempMax),
        };
        return State();
    }

    public Task<HomeState> SetDevice(string id, LightChange? light = null, bool? on = null, string? channel = null)
    {
        int i = _devices.FindIndex(d => d.Id == id);
        if (i < 0) return State();
        var d = _devices[i];
        if (channel != null)
        {
            var channels = d.Channels.Select(c => c.Code == channel ? c with { On = on == true } : c).ToList();
            d = d with { Channels = channels, On = channels.Any(c => c.On) };
        }
        else if (on is bool value)
        {
            d = d with { On = value, Channels = d.Channels.Select(c => c with { On = value }).ToList() };
        }
        if (d.Light != null && light != null)
        {
            var l = d.Light;
            if (light.Color is { } col) (l, d) = (l with { Mode = "colour", H = col.H, S = col.S }, d with { On = true });
            if (light.Temp is int t) (l, d) = (l with { Mode = "white", Temp = t }, d with { On = true });
            if (light.Brightness is int b) (l, d) = (l with { Brightness = b }, d with { On = true });
            d = d with { Light = l };
        }
        _devices[i] = d;
        return State();
    }

    public async Task<HomeState> LightsOff(string? room = null)
    {
        foreach (var d in (await State()).Devices.Where(d => d.Kind is DeviceKind.Light or DeviceKind.Switch or DeviceKind.Multi && d.Online && (room == null || d.Room == room)))
            await SetDevice(d.Id, on: false);
        return await State();
    }

    public Task<Discovery> Discover()
    {
        var hide = _cfg.Hide.Select(HmeConfig.Str).ToHashSet();
        var devs = Named().Select(d => new DiscoveredDevice(d.Id, d.Name, d.OriginalName, d.Kind.ToString().ToLowerInvariant(), d.Online,
            d.Channels.Select(c => c.Name).ToList(), hide.Contains(d.Id), Rooms.Of(d, _cfg, new Dictionary<string, string>()), _cfg.DeviceRoom.ContainsKey(d.Id))).ToList();
        devs.Add(new DiscoveredDevice("ir1", "Controlador IR", "Controlador IR", "hub", true, [], false, Rooms.Other, false));
        var order = Rooms.Order(devs.Select(d => new Device(d.Id, d.Name, d.OriginalName, true, "", DeviceKind.Switch, false, null, [], d.Room)), _cfg);
        return Task.FromResult(new Discovery(devs, [new DiscoveredAc("ir", "Ar-condicionado", "ir1", "r1", Hub: "Controlador IR")], [], order, ""));
    }

    public Task<string> Diagnose() => Task.FromResult($"hme. {HmeEngine.Version} · modo demonstração (nenhuma conta conectada).");

    public Task<string> TestAc() => Task.FromResult("Modo demonstração: não há ar de verdade para testar.");
}
