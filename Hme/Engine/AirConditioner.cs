using System.Text.Json.Nodes;
using Shft.Core;

namespace Shft.Hme;

public sealed record AcState(bool Power, string Mode, int Temp, string Fan, bool Turbo, bool Available, string Error,
    IReadOnlyList<string> Modes, int TempMin, int TempMax, string Model);

public sealed record AcChange(bool? Power = null, string? Mode = null, int? Temp = null, string? Fan = null, bool? Turbo = null);

/// <summary>Quick taps on the AC become one command (one beep), as Controle Casa's panel did: shown at once, sent once.</summary>
public static class AcChanges
{
    public static AcChange Merge(AcChange? pending, AcChange next) => pending == null ? next : new AcChange(
        next.Power ?? pending.Power, next.Mode ?? pending.Mode, next.Temp ?? pending.Temp, next.Fan ?? pending.Fan, next.Turbo ?? pending.Turbo);

    public static AcState Apply(AcState s, AcChange c) => s with
    {
        Power = c.Power ?? s.Power, Mode = c.Mode ?? s.Mode, Fan = c.Fan ?? s.Fan, Turbo = c.Turbo ?? s.Turbo,
        Temp = Math.Clamp(c.Temp ?? s.Temp, s.TempMin, s.TempMax),
    };
}

/// <summary>
/// core.py's air conditioner: Wi-Fi ACs by their status codes, IR ones through the hub. Each change is one IR shot,
/// and once a way of sending works only that way is used, so the AC never beeps twice.
/// </summary>
public sealed class AirConditioner
{
    public static readonly string[] Modes = ["cool", "heat", "auto", "fan", "dry"];
    public static readonly string[] Fans = ["auto", "low", "mid", "high"];
    static readonly string[] IrAcCategories = ["infrared_ac", "hwkt", "ktkzq"];
    static readonly string[] TempCodes = ["va_temperature", "temp_current", "temp_indoor"];

    readonly ICloud _cloud;
    readonly HmeConfig _cfg;
    readonly Func<Task<IReadOnlyList<JsonObject>>> _devices;
    readonly Func<string, Task<IReadOnlyDictionary<string, JsonNode?>>> _status;
    readonly Func<string, List<JsonObject>, Task> _send;
    (bool Power, string Mode, int Temp, string Fan) _lastIr = (true, "cool", 24, "auto");
    bool _turbo, _statusOk = true;
    string _method;

    public AirConditioner(ICloud cloud, HmeConfig cfg, Func<Task<IReadOnlyList<JsonObject>>> devices,
        Func<string, Task<IReadOnlyDictionary<string, JsonNode?>>> status, Func<string, List<JsonObject>, Task> send)
    {
        (_cloud, _cfg, _devices, _status, _send) = (cloud, cfg, devices, status, send);
        Type = HmeConfig.Str(cfg.Ac["type"]) is { Length: > 0 } t ? t : "auto";
        HubId = HmeConfig.Str(cfg.Ac["infrared_id"]);
        RemoteId = HmeConfig.Str(cfg.Ac["remote_id"]);
        WifiId = HmeConfig.Str(cfg.Ac["device_id"]);
        _method = HmeConfig.Str(cfg.Ac["method"]);
    }

    public string Type { get; private set; }
    public string HubId { get; private set; }
    public string RemoteId { get; private set; }
    public string WifiId { get; private set; }
    public string Error { get; private set; } = "";
    public string Method => _method;

    /// <summary>The devices that are the AC itself; the panel lists them as the AC card, not as switches.</summary>
    public IEnumerable<string> OwnIds => new[] { HubId, RemoteId, WifiId }.Where(i => i.Length > 0);

    AcState Profile(bool power, string mode, int temp, string fan, bool turbo, bool available = true) =>
        new(power, mode, temp, fan, turbo, available, Error, _cfg.AcModes, _cfg.TempMin, _cfg.TempMax, HmeConfig.Str(_cfg.Ac["model"]));

    async Task<IReadOnlyList<JsonObject>> Remotes(string hub) =>
        (await _cloud.Request($"/v2.0/infrareds/{hub}/remotes"))["result"] is JsonArray a ? a.OfType<JsonObject>().ToList() : [];

    static string Cat(JsonObject d) => HmeConfig.Str(d["category"]);

    static string CategoryId(JsonObject r) => r["category_id"] is JsonValue v ? v.ToJsonString().Trim('"') : "";

    /// <summary>core.py's _detect: a Wi-Fi AC first, then an AC remote on an IR hub, then a virtual IR AC.</summary>
    public async Task Detect(IReadOnlyList<JsonObject> devs)
    {
        if ((Type == "ir" && HubId.Length > 0 && RemoteId.Length > 0) || (Type == "wifi" && WifiId.Length > 0) || (Type == "irdev" && RemoteId.Length > 0))
            return;
        if (devs.FirstOrDefault(d => Cat(d) == "kt") is { } kt && Type is "auto" or "wifi")
        {
            (WifiId, Type) = (HmeConfig.Str(kt["id"]), "wifi");
            return;
        }
        foreach (var hub in devs.Where(d => Cat(d) == "wnykq"))
        {
            if ((await Remotes(HmeConfig.Str(hub["id"]))).FirstOrDefault(r => CategoryId(r) == "5") is { } remote)
            {
                (HubId, RemoteId, Type) = (HmeConfig.Str(hub["id"]), HmeConfig.Str(remote["remote_id"]), "ir");
                return;
            }
        }
        if (devs.FirstOrDefault(d => IrAcCategories.Contains(Cat(d))) is { } virt)
            (RemoteId, Type) = (HmeConfig.Str(virt["id"]), "irdev");
    }

    public async Task<AcState> State()
    {
        if (Type == "ir" && HubId.Length > 0 && RemoteId.Length > 0)
        {
            if (_statusOk)
            {
                var resp = await _cloud.Request($"/v2.0/infrareds/{HubId}/remotes/{RemoteId}/ac/status");
                if (resp["result"] is JsonObject r && r.Count > 0)
                {
                    int mode = HmeConfig.Int(r["mode"], 0), wind = HmeConfig.Int(r["wind"], 0);
                    _lastIr = (Str(r["power"], "1") == "1", Modes[((mode % 5) + 5) % 5], HmeConfig.Int(r["temp"], 24), Fans[((wind % 4) + 4) % 4]);
                }
                else if (!TuyaCloud.Ok(resp) && resp["result"] is not JsonObject)
                {
                    _statusOk = false; // no state reading: use what was sent last
                }
            }
            return Profile(_lastIr.Power, _lastIr.Mode, _lastIr.Temp, _lastIr.Fan, _turbo);
        }
        if (Type == "irdev" && RemoteId.Length > 0) return Profile(_lastIr.Power, _lastIr.Mode, _lastIr.Temp, _lastIr.Fan, _turbo);
        if (Type == "wifi" && WifiId.Length > 0)
        {
            var st = await _status(WifiId);
            var w = _cfg.WifiAcCodes;
            string Code(string k) => HmeConfig.Str(w[k]);
            var modeRev = (w["mode_map"] as JsonObject ?? new()).ToDictionary(kv => HmeConfig.Str(kv.Value), kv => kv.Key);
            var fanRev = (w["fan_map"] as JsonObject ?? new()).ToDictionary(kv => HmeConfig.Str(kv.Value), kv => kv.Key);
            int temp = Devices.Number(st.GetValueOrDefault(Code("temp"))) is double td
                ? (int)Math.Round(td / _cfg.WifiTempScale, MidpointRounding.ToEven) : 24;
            bool turbo = Code("turbo").Length > 0 ? Devices.Bool(st.GetValueOrDefault(Code("turbo"))) == true : _turbo;
            return Profile(Devices.Bool(st.GetValueOrDefault(Code("power"))) == true,
                modeRev.GetValueOrDefault(HmeConfig.Str(st.GetValueOrDefault(Code("mode"))), "cool"), temp,
                fanRev.GetValueOrDefault(HmeConfig.Str(st.GetValueOrDefault(Code("fan"))), "auto"), turbo);
        }
        return Profile(false, "cool", 24, "auto", false, available: false);
    }

    static string Str(JsonNode? n, string fallback) => n is JsonValue v ? v.ToJsonString().Trim('"') : fallback;

    public async Task<AcState> Set(AcChange c)
    {
        var cur = await State();
        bool power = c.Power ?? cur.Power, turbo = c.Turbo ?? cur.Turbo;
        string mode = c.Mode != null && Modes.Contains(c.Mode) ? c.Mode : cur.Mode;
        string fan = c.Fan != null && Fans.Contains(c.Fan) ? c.Fan : cur.Fan;
        int temp = Math.Clamp(c.Temp ?? cur.Temp, _cfg.TempMin, _cfg.TempMax);

        if (Type is "ir" or "irdev")
        {
            var changed = new HashSet<string>();
            if (c.Power != null) changed.Add("power");
            if (c.Mode != null) changed.Add("mode");
            if (c.Temp != null) changed.Add("temp");
            if (c.Fan != null) changed.Add("fan");
            if (c.Turbo != null) changed.Add("turbo");
            if (changed.Count == 0) return Profile(power, mode, temp, fan, turbo);
            _turbo = turbo;
            await IrSend(power, mode, temp, _turbo ? "high" : fan, changed);
            _lastIr = (power, mode, temp, fan);
        }
        else if (Type == "wifi")
        {
            var w = _cfg.WifiAcCodes;
            string Code(string k) => HmeConfig.Str(w[k]);
            string Map(string map, string key) => HmeConfig.Str(w[map]?[key]);
            var cmds = new List<JsonObject>();
            if (c.Power != null) cmds.Add(Cmd(Code("power"), power));
            if (c.Temp != null) cmds.Add(Cmd(Code("temp"), (int)(temp * _cfg.WifiTempScale)));
            if (c.Mode != null) cmds.Add(Cmd(Code("mode"), Map("mode_map", mode)));
            if (c.Fan != null) cmds.Add(Cmd(Code("fan"), Map("fan_map", fan)));
            if (c.Turbo != null)
            {
                _turbo = turbo;
                cmds.Add(Code("turbo").Length > 0 ? Cmd(Code("turbo"), _turbo) : Cmd(Code("fan"), Map("fan_map", _turbo ? "high" : fan)));
            }
            if (cmds.Count > 0) await _send(WifiId, cmds);
        }
        return Profile(power, mode, temp, fan, _turbo);
    }

    static JsonObject Cmd(string code, JsonNode? value) => new() { ["code"] = code, ["value"] = value };

    async Task FindHub()
    {
        if (HubId.Length > 0 || RemoteId.Length == 0) return;
        foreach (var hub in (await _devices()).Where(d => Cat(d) == "wnykq"))
        {
            if ((await Remotes(HmeConfig.Str(hub["id"]))).Any(r => HmeConfig.Str(r["remote_id"]) == RemoteId))
            {
                HubId = HmeConfig.Str(hub["id"]);
                return;
            }
        }
    }

    /// <summary>The ways Tuya takes an IR AC command; each one is a single shot.</summary>
    List<(string Name, Func<Task<JsonObject>> Run)> Methods(bool power, string mode, int temp, string fan, ISet<string> changed)
    {
        var body = new JsonObject { ["power"] = power ? 1 : 0, ["mode"] = Array.IndexOf(Modes, mode), ["temp"] = temp, ["wind"] = Array.IndexOf(Fans, fan) };
        var keys = new List<(string Code, int Value)>();
        if (changed.Contains("power") || !power)
        {
            keys.Add(("power", power ? 1 : 0)); // power alone: the AC comes back as it was
        }
        else
        {
            if (changed.Contains("mode")) keys.Add(("mode", Array.IndexOf(Modes, mode)));
            if (changed.Contains("temp")) keys.Add(("temp", temp));
            if (changed.Contains("fan") || changed.Contains("turbo")) keys.Add(("wind", Array.IndexOf(Fans, fan)));
        }
        var std = new JsonArray();
        foreach (var (code, value) in keys)
        {
            if (code == "power") std.Add(new JsonObject { ["code"] = value == 1 ? "PowerOn" : "PowerOff", ["value"] = value == 1 ? "PowerOn" : "PowerOff" });
            else std.Add(new JsonObject { ["code"] = code switch { "mode" => "M", "temp" => "T", _ => "F" }, ["value"] = value });
        }
        var baseUrl = $"/v2.0/infrareds/{HubId}/air-conditioners/{RemoteId}";

        async Task<JsonObject> Single()
        {
            JsonObject r = new();
            for (int i = 0; i < keys.Count; i++)
            {
                if (i > 0) await Task.Delay(400);
                r = await _cloud.Request(baseUrl + "/command", "POST", new JsonObject { ["code"] = keys[i].Code, ["value"] = keys[i].Value });
                if (!TuyaCloud.Ok(r)) return r;
            }
            return r;
        }

        var list = new List<(string, Func<Task<JsonObject>>)>();
        if (HubId.Length > 0)
        {
            list.Add(("cena (v2)", () => _cloud.Request(baseUrl + "/scenes", "POST", body.DeepClone())));
            list.Add(("tecla por tecla (v2)", Single));
            list.Add(("cena (v1)", () => _cloud.Request($"/v1.0/infrareds/{HubId}/air-conditioners/{RemoteId}/scenes", "POST", body.DeepClone())));
        }
        list.Add(("comando padrão", () => _cloud.Request($"/v1.0/iot-03/devices/{RemoteId}/commands", "POST", new JsonObject { ["commands"] = std })));
        return list;
    }

    void Remember(string name)
    {
        _method = name;
        if (HmeConfig.Str(_cfg.Ac["method"]) == name) return;
        _cfg.Ac["method"] = name;
        try { _cfg.Save(); }
        catch (Exception ex) when (ex is HmeSecretException or System.IO.IOException or UnauthorizedAccessException)
        {
            Log.Write("hme: AC method not saved: " + ex.Message);
        }
    }

    /// <summary>Only the way that already worked; no fallback after that, it could fire the IR twice.</summary>
    async Task IrSend(bool power, string mode, int temp, string fan, ISet<string> changed)
    {
        await FindHub();
        var tries = Methods(power, mode, temp, fan, changed);
        if (_method.Length > 0 && tries.Where(t => t.Name == _method).ToList() is { Count: > 0 } known) tries = known;
        string firstError = "";
        foreach (var (name, run) in tries)
        {
            var r = await run();
            if (TuyaCloud.Ok(r))
            {
                Remember(name);
                Error = "";
                return;
            }
            if (firstError.Length == 0) firstError = TuyaCloud.ErrorText(r);
        }
        Error = TuyaCloud.AcHint(firstError.Length > 0 ? firstError : "O ar não respondeu.");
        throw new HmeCloudException("Ar-condicionado: " + Error);
    }

    /// <summary>core.py's test_ac: tries each way on its own (turns the AC on at 24 °C) and reports.</summary>
    public async Task<string> Test()
    {
        await FindHub();
        var lines = new List<string> { $"Ar escolhido: tipo={Type} hub={Or(HubId)} controle={Or(RemoteId.Length > 0 ? RemoteId : WifiId)}" };
        if (Type == "wifi")
        {
            try
            {
                await Set(new AcChange(Power: true, Temp: 24));
                lines.Add("Ar Wi-Fi: comando aceito.");
            }
            catch (HmeCloudException ex)
            {
                lines.Add("Ar Wi-Fi: falhou -> " + ex.Message);
            }
            return string.Join("\n", lines);
        }
        if (RemoteId.Length == 0) return string.Join("\n", lines.Append("Nenhum ar escolhido. Abra os Ajustes do hme. → Ar-condicionado."));
        if (HubId.Length > 0)
        {
            var r = await _cloud.Request($"/v2.0/infrareds/{HubId}/remotes/{RemoteId}/keys");
            if (r["result"] is JsonObject k)
            {
                lines.Add($"Controle: categoria {Str(k["category_id"], "")} · marca {Str(k["brand_id"], "")} · {Str(k["remote_index"], "")}");
                var names = (k["key_list"] as JsonArray ?? new()).OfType<JsonObject>().Take(12)
                    .Select(x => HmeConfig.Str(x["key_name"]) is { Length: > 0 } n ? n : Str(x["key"], ""));
                if (names.Any()) lines.Add("Teclas: " + string.Join(", ", names));
            }
            else
            {
                lines.Add("Teclas do controle: " + TuyaCloud.ErrorText(r));
            }
        }
        bool any = false;
        string first = "";
        foreach (var (name, run) in Methods(true, "cool", 24, "auto", new HashSet<string> { "power" }))
        {
            var r = await run();
            if (TuyaCloud.Ok(r))
            {
                lines.Add($"✔ {name}: aceito — o hme. vai usar só esta forma daqui pra frente");
                Remember(name);
                Error = "";
                any = true;
                break;
            }
            if (first.Length == 0) first = TuyaCloud.ErrorText(r);
            lines.Add($"✘ {name}: {TuyaCloud.ErrorText(r)}");
        }
        lines.Add("");
        lines.Add("Resultado: " + (any ? "pelo menos uma forma funcionou. O ar ligou?" : "nenhuma forma foi aceita. " + TuyaCloud.AcHint(first)));
        return string.Join("\n", lines);
    }

    static string Or(string s) => s.Length > 0 ? s : "-";

    public async Task<int?> RoomTemp()
    {
        var sensor = _cfg.RoomSensorId.Length > 0 ? _cfg.RoomSensorId : Type == "wifi" ? WifiId : "";
        if (sensor.Length == 0) return null;
        var st = await _status(sensor);
        foreach (var code in TempCodes)
        {
            if (Devices.Number(st.GetValueOrDefault(code)) is double d)
                return d > 60 ? (int)Math.Round(d / 10, MidpointRounding.ToEven) : (int)Math.Round(d, MidpointRounding.ToEven);
        }
        return null;
    }
}
