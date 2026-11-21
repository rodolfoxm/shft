using System.Text.Json.Nodes;
using Shft.Core;

namespace Shft.Hme;

/// <summary>
/// core.py's Tuya backend plus the Positivo account: device list cached by how busy the panel is, commands that
/// update the cache instead of reading the cloud again, and the last good list when the cloud fails.
/// </summary>
public sealed class HmeEngine : IHome
{
    public const string Version = "2.0.0";
    const int TtlActive = 60, TtlIdle = 600, ActiveWindow = 300, RoomsEvery = 86400, PositivoBackoff = 60;

    readonly HmeConfig _cfg;
    readonly ICloud _cloud;
    readonly Usage _usage;
    readonly SavedList _saved;
    readonly Func<DateTime> _now;
    readonly AirConditioner _ac;
    readonly SemaphoreSlim _commands = new(1, 1);
    readonly SemaphoreSlim _listGate = new(1, 1);
    readonly Dictionary<string, string> _routes = new(); // device -> command route that worked
    readonly List<string> _errors = new();
    List<JsonObject> _cache;
    DateTime _cacheAt = DateTime.MinValue, _activity = DateTime.MinValue, _roomsAt = DateTime.MinValue;
    DateTime _positivoAt = DateTime.MinValue, _positivoFailedAt = DateTime.MinValue;
    Dictionary<string, string> _smartLifeRooms = new();
    bool _detected;
    string? _listError;

    public HmeEngine(HmeConfig cfg, ICloud cloud, Usage usage, SavedList saved, PositivoClient? positivo, Func<DateTime>? now = null)
    {
        (_cfg, _cloud, _usage, _saved, Positivo) = (cfg, cloud, usage, saved, positivo);
        _now = now ?? (() => DateTime.Now);
        _cache = saved.Get("tuya"); // shows the last list until the cloud answers
        _ac = new AirConditioner(cloud, cfg, async () => await DeviceList(), Status, Send);
    }

    public PositivoClient? Positivo { get; private set; }
    public string PositivoError { get; private set; } = "";

    public void MarkActivity() => _activity = _now();

    int Ttl => (_now() - _activity).TotalSeconds < ActiveWindow ? TtlActive : TtlIdle;

    void Remember(string line)
    {
        lock (_errors)
        {
            _errors.Add(_now().ToString("HH:mm:ss ") + line);
            if (_errors.Count > 15) _errors.RemoveAt(0);
        }
    }

    async Task<List<JsonObject>> DeviceList(bool force = false, bool cacheOnly = false, bool raiseErrors = false)
    {
        if (cacheOnly || (!force && (_now() - _cacheAt).TotalSeconds < Ttl)) return _cache;
        if (!force && _usage.QuotaOut) return _cache;
        await _listGate.WaitAsync();
        try
        {
            if (!force && (_now() - _cacheAt).TotalSeconds < Ttl) return _cache; // someone else just read it
            var output = new List<JsonObject>();
            string last = "";
            for (int page = 0; page < 20; page++)
            {
                var q = new Dictionary<string, string> { ["size"] = "100" };
                if (last.Length > 0) q["last_row_key"] = last;
                var r = await _cloud.Request("/v1.0/iot-01/associated-users/devices", query: q);
                if (!TuyaCloud.Ok(r))
                {
                    var error = TuyaCloud.ErrorText(r);
                    Remember("GET lista de aparelhos -> " + error);
                    if (raiseErrors) throw new HmeCloudException(error);
                    _cacheAt = _now(); // failed: keep the last good list, without insisting
                    _listError = error;
                    return _cache;
                }
                var res = r["result"] as JsonObject ?? new JsonObject();
                output.AddRange((res["devices"] as JsonArray ?? new()).OfType<JsonObject>().Select(d => (JsonObject)d.DeepClone()));
                if (Devices.Bool(res["has_more"]) != true) break;
                last = HmeConfig.Str(res["last_row_key"]);
            }
            (_cache, _cacheAt, _listError) = (output, _now(), null);
            _saved.Put("tuya", output);
            return output;
        }
        finally
        {
            _listGate.Release();
        }
    }

    async Task<List<JsonObject>> PositivoDevices(bool force = false, bool cacheOnly = false)
    {
        if (Positivo == null) return [];
        List<JsonObject> Fallback() => Positivo.Cache.Count > 0 ? Positivo.Cache.ToList() : _saved.Get("positivo");
        if (cacheOnly || (!force && (_now() - _positivoFailedAt).TotalSeconds < PositivoBackoff)) return Fallback();
        if (!force && (_now() - _positivoAt).TotalSeconds < Ttl) return Fallback();
        try
        {
            var devs = await Positivo.Devices(force: true, ttlSeconds: 0);
            (_positivoAt, _positivoFailedAt, PositivoError) = (_now(), DateTime.MinValue, "");
            if (devs.Count > 0 || _saved.Get("positivo").Count == 0)
            {
                _saved.Put("positivo", devs);
                return devs;
            }
            return _saved.Get("positivo"); // came back empty all of a sudden: do not wipe the panel
        }
        catch (PositivoException ex)
        {
            (PositivoError, _positivoFailedAt) = (ex.Message, _now());
            Log.Write("hme: Positivo: " + ex.Message);
            return Fallback();
        }
    }

    async Task<IReadOnlyDictionary<string, JsonNode?>> Status(string id)
    {
        if (id.StartsWith(PositivoClient.Prefix, StringComparison.Ordinal))
            return Positivo?.Status(id) ?? new Dictionary<string, JsonNode?>();
        if ((await DeviceList()).FirstOrDefault(d => HmeConfig.Str(d["id"]) == id) is { } known) return Devices.StatusMap(known);
        var r = await _cloud.Request($"/v1.0/iot-03/devices/{id}/status");
        var map = new Dictionary<string, JsonNode?>();
        foreach (var s in (r["result"] as JsonArray ?? new()).OfType<JsonObject>()) map[HmeConfig.Str(s["code"])] = s["value"]?.DeepClone();
        return map;
    }

    /// <summary>Sends commands; if the device refuses the standard format, tries Tuya's other formats and remembers.</summary>
    async Task Send(string id, List<JsonObject> cmds)
    {
        if (cmds.Count == 0) return;
        if (id.StartsWith(PositivoClient.Prefix, StringComparison.Ordinal))
        {
            if (Positivo == null) throw new HmeCloudException("Positivo: conta não conectada.");
            try { await Positivo.Send(id, cmds); }
            catch (PositivoException ex) { throw new HmeCloudException("Positivo: " + ex.Message); }
            return;
        }
        var props = new JsonObject();
        foreach (var c in cmds) props[HmeConfig.Str(c["code"])] = c["value"]?.DeepClone();
        var routes = new (string Name, Func<Task<JsonObject>> Run)[]
        {
            ("padrao", () => _cloud.Request($"/v1.0/iot-03/devices/{id}/commands", "POST", new JsonObject { ["commands"] = Array(cmds) })),
            ("dp", () => _cloud.Request($"/v2.0/cloud/thing/{id}/shadow/properties/issue", "POST", new JsonObject { ["properties"] = props.ToJsonString() })),
            ("v1", () => _cloud.Request($"/v1.0/devices/{id}/commands", "POST", new JsonObject { ["commands"] = Array(cmds) })),
        };
        var preferred = _routes.GetValueOrDefault(id, "padrao");
        string first = "";
        foreach (var (name, run) in routes.OrderBy(r => r.Name != preferred))
        {
            var r = await run();
            if (TuyaCloud.Ok(r))
            {
                _routes[id] = name;
                ApplyLocal(id, cmds);
                return;
            }
            if (first.Length == 0) first = TuyaCloud.ErrorText(r);
        }
        Remember($"POST comando {id} -> {first}");
        throw new HmeCloudException(first.Length > 0 ? first : "O aparelho não respondeu.");
    }

    static JsonArray Array(List<JsonObject> cmds) => new(cmds.Select(c => (JsonNode)c.DeepClone()).ToArray());

    /// <summary>After an accepted command, the cached state changes instead of reading the cloud again.</summary>
    void ApplyLocal(string id, List<JsonObject> cmds)
    {
        var values = cmds.ToDictionary(c => HmeConfig.Str(c["code"]), c => c["value"]);
        foreach (var d in _cache.Where(d => HmeConfig.Str(d["id"]) == id))
            foreach (var s in (d["status"] as JsonArray ?? new()).OfType<JsonObject>())
                if (values.TryGetValue(HmeConfig.Str(s["code"]), out var v)) s["value"] = v?.DeepClone();
    }

    /// <summary>Smart Life's own rooms, once a day; without permission it is just skipped.</summary>
    async Task RefreshRooms(bool now = false)
    {
        if (!now && (_now() - _roomsAt).TotalSeconds < RoomsEvery) return;
        _roomsAt = _now();
        try
        {
            var map = new Dictionary<string, string>();
            foreach (var home in (await DeviceList(cacheOnly: !now)).Select(d => HmeConfig.Str(d["owner_id"])).Where(h => h.Length > 0).Distinct())
            {
                var r = await _cloud.Request($"/v1.0/homes/{home}/rooms");
                if (!TuyaCloud.Ok(r)) continue;
                foreach (var room in (r["result"]?["rooms"] as JsonArray ?? new()).OfType<JsonObject>())
                {
                    var rd = await _cloud.Request($"/v1.0/homes/{home}/rooms/{room["room_id"]?.ToJsonString().Trim('"')}/devices");
                    if (!TuyaCloud.Ok(rd)) continue;
                    foreach (var dev in (rd["result"] as JsonArray ?? new()).OfType<JsonObject>())
                        if (HmeConfig.Str(dev["id"]) is { Length: > 0 } did) map[did] = HmeConfig.Str(room["name"]);
                }
            }
            _smartLifeRooms = map;
        }
        catch (HmeCloudException ex)
        {
            Log.Write("hme: rooms: " + ex.Message);
        }
    }

    public async Task<HomeState> State(bool cacheOnly = false) => await Build(null, cacheOnly);

    async Task<HomeState> Build(AcState? acOverride, bool cacheOnly)
    {
        var tuya = await DeviceList(cacheOnly: cacheOnly);
        if (!cacheOnly) await RefreshRooms();
        if (_cfg.HasAc && !_detected && !cacheOnly)
        {
            _detected = true;
            await _ac.Detect(tuya);
        }
        var skip = _cfg.HasAc ? _ac.OwnIds.ToHashSet() : [];
        var raw = tuya.Concat(await PositivoDevices(cacheOnly: cacheOnly)).Where(d => !skip.Contains(HmeConfig.Str(d["id"])));
        var devs = Devices.Visible(raw.Select(d => Devices.Parse(d, _cfg)).OfType<Device>(), _cfg, _smartLifeRooms);
        AcState? ac = null;
        int? temp = null;
        if (_cfg.HasAc)
        {
            ac = acOverride ?? (cacheOnly ? null : await _ac.State());
            temp = cacheOnly ? null : await _ac.RoomTemp();
        }
        return new HomeState(false, HmeConfig.Str(_cfg.Ac["room_name"]) is { Length: > 0 } rn ? rn : "Quarto", temp, ac, devs, Rooms.Order(devs, _cfg), Notice());
    }

    string? Notice()
    {
        if (_usage.QuotaOut) return _usage.QuotaMessage;
        if (_listError != null) return "Mostrando a última lista que deu certo. " + _listError;
        if (PositivoError.Length > 0) return "Positivo: " + PositivoError;
        return null;
    }

    public async Task<HomeState> SetAc(AcChange change)
    {
        MarkActivity();
        await _commands.WaitAsync();
        try { return await Build(await _ac.Set(change), cacheOnly: false); }
        finally { _commands.Release(); }
    }

    public async Task<HomeState> SetDevice(string id, LightChange? light = null, bool? on = null, string? channel = null)
    {
        MarkActivity();
        await _commands.WaitAsync();
        try
        {
            await SetDeviceCore(id, light, on, channel);
            return await State();
        }
        finally
        {
            _commands.Release();
        }
    }

    async Task SetDeviceCore(string id, LightChange? light, bool? on, string? channel)
    {
        var st = await Status(id);
        var cmds = new List<JsonObject>();
        bool isLight = Devices.Bool(st.GetValueOrDefault(Devices.LightOn)) != null;
        if (channel != null)
        {
            cmds.Add(new JsonObject { ["code"] = channel, ["value"] = on == true });
        }
        else if (on is bool value)
        {
            if (isLight) cmds.Add(new JsonObject { ["code"] = Devices.LightOn, ["value"] = value });
            else
            {
                var codes = st.Where(kv => Devices.IsSwitchCode(kv.Key) && Devices.Bool(kv.Value) != null).Select(kv => kv.Key).ToList();
                foreach (var code in codes.Count > 0 ? codes : ["switch_1"]) cmds.Add(new JsonObject { ["code"] = code, ["value"] = value });
            }
        }
        if (isLight && light != null) cmds.AddRange(Devices.LightCommands(st, light with { On = on ?? light.On }));
        await Send(id, cmds);
    }

    public async Task<HomeState> LightsOff(string? room = null)
    {
        MarkActivity();
        await _commands.WaitAsync();
        try
        {
            foreach (var d in (await State()).Devices.Where(d => d.Kind is DeviceKind.Light or DeviceKind.Switch or DeviceKind.Multi && d.Online && d.On && (room == null || d.Room == room)))
                await SetDeviceCore(d.Id, null, false, null);
            return await State();
        }
        finally
        {
            _commands.Release();
        }
    }

    public async Task<Discovery> Discover()
    {
        var devs = await DeviceList(force: true, raiseErrors: true);
        var acs = new List<DiscoveredAc>();
        var sensors = new List<Sensor>();
        var output = new List<DiscoveredDevice>();
        var remoteIds = new HashSet<string>();
        string irError = "";
        string[] tempCodes = ["va_temperature", "temp_current", "temp_indoor"];
        string[] irCats = ["infrared_ac", "hwkt", "ktkzq"];
        foreach (var d in devs)
        {
            string id = HmeConfig.Str(d["id"]), cat = HmeConfig.Str(d["category"]), name = HmeConfig.Str(d["name"]);
            var st = Devices.StatusMap(d);
            if (cat == "kt") acs.Add(new DiscoveredAc("wifi", name.Length > 0 ? name : "Ar-condicionado", DeviceId: id));
            if (cat == "wnykq")
            {
                var rr = await _cloud.Request($"/v2.0/infrareds/{id}/remotes");
                if (!TuyaCloud.Ok(rr)) irError = TuyaCloud.AcHint(TuyaCloud.ErrorText(rr));
                foreach (var r in (rr["result"] as JsonArray ?? new()).OfType<JsonObject>())
                {
                    var rid = HmeConfig.Str(r["remote_id"]);
                    remoteIds.Add(rid);
                    if (r["category_id"]?.ToJsonString().Trim('"') == "5")
                        acs.Add(new DiscoveredAc("ir", HmeConfig.Str(r["remote_name"]) is { Length: > 0 } rn ? rn : "Ar-condicionado", id, rid, Hub: name));
                }
            }
            if (irCats.Contains(cat)) acs.Add(new DiscoveredAc("irdev", name.Length > 0 ? name : "Ar-condicionado", RemoteId: id, DeviceId: id, Hub: "controle virtual"));
            if (tempCodes.Any(c => Devices.Number(st.GetValueOrDefault(c)) != null)) sensors.Add(new Sensor(id, name.Length > 0 ? name : "?"));
        }
        DiscoveredDevice Describe(JsonObject d, string? source)
        {
            var p = Devices.Parse(d, _cfg);
            string id = HmeConfig.Str(d["id"]), cat = HmeConfig.Str(d["category"]), original = HmeConfig.Str(d["name"]) is { Length: > 0 } n ? n : "?";
            var kind = p != null ? p.Kind.ToString().ToLowerInvariant() : cat == "wnykq" ? "hub" : cat == "kt" ? "ac" : "other";
            return new DiscoveredDevice(id, HmeConfig.Str(_cfg.Names[id]) is { Length: > 0 } custom ? custom : original, original, kind,
                Devices.Bool(d["online"]) == true, p?.Channels.Select(c => c.Name).ToList() ?? [], _cfg.Hide.Select(HmeConfig.Str).Contains(id), "", false, source);
        }
        foreach (var d in await PositivoDevices(force: true)) output.Add(Describe(d, "Positivo"));
        var known = acs.Where(a => a.Type == "ir").Select(a => a.RemoteId).ToHashSet();
        acs = acs.Where(a => !(a.Type == "irdev" && known.Contains(a.RemoteId))).ToList();
        foreach (var d in devs.Where(d => !remoteIds.Contains(HmeConfig.Str(d["id"])) && !irCats.Contains(HmeConfig.Str(d["category"]))))
            output.Add(Describe(d, null));
        await RefreshRooms(now: true);
        output = output.Select(d =>
        {
            var probe = new Device(d.Id, d.Name, d.OriginalName, d.Online, "", DeviceKind.Switch, false, null, []);
            return d with { Room = Rooms.Of(probe, _cfg, _smartLifeRooms), RoomManual = _cfg.DeviceRoom.ContainsKey(d.Id) };
        }).ToList();
        var order = Rooms.Order(output.Select(d => new Device(d.Id, d.Name, d.OriginalName, d.Online, "", DeviceKind.Switch, false, null, [], d.Room)), _cfg);
        return new Discovery(output, acs, sensors, order, irError);
    }

    public async Task<string> TestAc() => await _ac.Test();

    public async Task<string> Diagnose()
    {
        var lines = new List<string> { $"hme. {Version} · região {_cfg.Region}", _usage.Text() };
        if (_usage.QuotaOut) lines.Add("⚠ Cota da Tuya esgotada: leituras automáticas pausadas por 1 h de cada vez.");
        var devs = await DeviceList(force: true);
        lines.Add($"Aparelhos na conta: {devs.Count}");
        foreach (var d in devs)
        {
            var codes = string.Join(",", Devices.StatusMap(d).Keys);
            var f = await _cloud.Request($"/v1.0/iot-03/devices/{HmeConfig.Str(d["id"])}/functions");
            var fn = TuyaCloud.Ok(f) ? "funções OK" : "funções: " + TuyaCloud.ErrorText(f);
            lines.Add($"- {HmeConfig.Str(d["name"])} [{HmeConfig.Str(d["category"])}] {(Devices.Bool(d["online"]) == true ? "online" : "OFFLINE")} | {fn} | {codes}");
        }
        lines.Add("");
        lines.Add(_cfg.HasAc
            ? $"Ar: tipo={_ac.Type} hub={Or(_ac.HubId)} controle={Or(_ac.RemoteId)} wifi={Or(_ac.WifiId)} método={Or(_ac.Method)}"
            : "Ar: desligado nos ajustes (sem ar-condicionado).");
        foreach (var hub in devs.Where(d => HmeConfig.Str(d["category"]) == "wnykq"))
        {
            var r = await _cloud.Request($"/v2.0/infrareds/{HmeConfig.Str(hub["id"])}/remotes");
            lines.Add(TuyaCloud.Ok(r)
                ? $"Hub {HmeConfig.Str(hub["name"])}: controles = " + (string.Join(", ", (r["result"] as JsonArray ?? new()).OfType<JsonObject>()
                    .Select(x => $"{HmeConfig.Str(x["remote_name"])}(cat {x["category_id"]?.ToJsonString().Trim('"')})")) is { Length: > 0 } list ? list : "nenhum")
                : $"Hub {HmeConfig.Str(hub["name"])}: lista de controles falhou -> {TuyaCloud.ErrorText(r)}");
        }
        if (_cfg.HasAc && _ac.HubId.Length > 0 && _ac.RemoteId.Length > 0)
        {
            var r = await _cloud.Request($"/v2.0/infrareds/{_ac.HubId}/remotes/{_ac.RemoteId}/ac/status");
            lines.Add("Status do ar: " + (TuyaCloud.Ok(r) ? r["result"]?.ToJsonString() : TuyaCloud.ErrorText(r)));
        }
        if (_ac.Error.Length > 0) lines.Add("Último erro do ar: " + _ac.Error);
        lines.Add("");
        if (Positivo != null)
        {
            try
            {
                var pd = await Positivo.Devices(true, 0);
                lines.Add($"Conta Positivo ({HmeConfig.Str(_cfg.Positivo["email"])}): {pd.Count} aparelho(s)");
                foreach (var d in pd)
                    lines.Add($"- {HmeConfig.Str(d["name"])} [{HmeConfig.Str(d["category"])}] {(Devices.Bool(d["online"]) == true ? "online" : "OFFLINE")} | {string.Join(",", Devices.StatusMap(d).Keys)}");
            }
            catch (PositivoException ex)
            {
                lines.Add("Conta Positivo: erro -> " + ex.Message);
            }
        }
        else
        {
            lines.Add("Conta Positivo: não conectada");
        }
        if (PositivoError.Length > 0) lines.Add("Último erro da Positivo: " + PositivoError);
        lock (_errors)
        {
            if (_errors.Count > 0)
            {
                lines.Add("");
                lines.Add("Últimos erros:");
                lines.AddRange(_errors);
            }
        }
        return string.Join("\n", lines);
    }

    static string Or(string s) => s.Length > 0 ? s : "-";

    /// <summary>core.py's connect_positivo: signs in, lists devices, then keeps the account (password and session protected).</summary>
    public async Task<PositivoResult> ConnectPositivo(string email, string password)
    {
        try
        {
            if (password.Length == 0 && HmeConfig.Str(_cfg.Positivo["email"]) == email)
                password = Secrets.Unprotect(HmeConfig.Str(_cfg.Positivo["password"]));
            var client = new PositivoClient(email, password, HmeEngineFactory.PositivoDeviceId(_cfg), null, null);
            await client.Login();
            var devs = await client.Devices(true, 0);
            _cfg.Positivo["email"] = email.Trim();
            _cfg.Positivo["password"] = Secrets.Protect(password);
            _cfg.Positivo["session"] = Secrets.Protect(client.Sid ?? "");
            _cfg.Save();
            client.OnSession = sid => HmeEngineFactory.SaveSession(_cfg, sid);
            (Positivo, PositivoError, _positivoAt) = (client, "", DateTime.MinValue);
            return new PositivoResult(true, devs.Count, devs.Select(d => HmeConfig.Str(d["name"])).ToList(), null);
        }
        catch (Exception ex) when (ex is PositivoException or HmeSecretException or System.IO.IOException)
        {
            return new PositivoResult(false, 0, [], ex.Message);
        }
    }

    public void DisconnectPositivo()
    {
        foreach (var key in new[] { "email", "password", "session" }) _cfg.Positivo[key] = ""; // keeps the device identity
        _cfg.Save();
        (Positivo, PositivoError) = (null, "");
    }
}
