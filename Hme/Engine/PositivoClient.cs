using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Shft.Hme;

public class PositivoException(string message) : Exception(message);

public sealed class PositivoSessionExpired(string message) : PositivoException(message);

/// <summary>
/// Positivo Casa Inteligente runs on Tuya's cloud, on an island apart from Smart Life. This signs in the way the
/// phone app does (e-mail and password), lists devices and sends commands; the devices stay in Positivo's app, so
/// Alexa keeps working. Not an official API: if Positivo changes its app, this may stop working.
/// </summary>
public sealed class PositivoClient : IDisposable
{
    public const string Prefix = "pos:";
    const string Endpoint = "https://a1.tuyaus.com/api.json";
    const string ClientId = "qk4c93xsttfjsm4ktr94";
    const string AppKey = "A_9v5dh8yngmhtxdjen7t7qqx5ggmdf5su_w7vm9gr7meys387fpj7y7yrvx8w554cd";
    const string Country = "55";

    static readonly HashSet<string> SignKeys =
    [
        "a", "v", "lat", "lon", "lang", "deviceId", "imei", "imsi", "appVersion", "ttid", "isH5", "h5Token", "os",
        "clientId", "postData", "time", "requestId", "n4h5", "sid", "sp", "et",
    ];

    static readonly Dictionary<string, string> ProductHint = new() { ["pq860vo9ib50jhud"] = "cz", ["lwpag3bu0faaowlj"] = "wnykq" };

    readonly string _email, _password, _deviceId;
    readonly HttpClient _http;
    readonly Func<TimeSpan, Task> _delay;
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly Dictionary<string, Dictionary<string, string>> _schemas = new(); // devId -> dp id -> code
    readonly Dictionary<string, Dictionary<string, string>> _codes = new();   // devId -> code -> dp id
    readonly Dictionary<string, (string Name, string ProductId, string Category, string Gw)> _meta = new();
    List<JsonObject> _cache = new();
    DateTime _cacheAt = DateTime.MinValue;

    public PositivoClient(string email, string password, string deviceId, string? session, Action<string>? onSession,
        HttpMessageHandler? handler = null, Func<TimeSpan, Task>? delay = null)
    {
        (_email, _password, _deviceId) = (email.Trim(), password, deviceId);
        Sid = string.IsNullOrEmpty(session) ? null : session;
        OnSession = onSession;
        _http = handler == null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(12);
        _delay = delay ?? (t => Task.Delay(t));
    }

    public string? Sid { get; private set; }
    public Action<string>? OnSession { get; set; }
    public IReadOnlyList<JsonObject> Cache => _cache;

    public static string NewDeviceId() => "b2f7827604d5585b9658eb86c0a079b3" + Guid.NewGuid().ToString("N")[..12]; // uuid5(DNS, "controlecasa.positivo")

    public static string PostHash(string data)
    {
        var h = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(data))).ToLowerInvariant();
        return h[8..16] + h[0..8] + h[24..32] + h[16..24];
    }

    public static string Sign(IReadOnlyDictionary<string, string> p)
    {
        var parts = p.Where(kv => SignKeys.Contains(kv.Key) && !string.IsNullOrEmpty(kv.Value))
                     .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                     .Select(kv => kv.Key + "=" + (kv.Key == "postData" ? PostHash(kv.Value) : kv.Value));
        return Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(AppKey), Encoding.UTF8.GetBytes(string.Join("||", parts)))).ToLowerInvariant();
    }

    public static string EncryptPassword(string password, BigInteger n, BigInteger e)
    {
        var md5 = Encoding.ASCII.GetBytes(Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(password))).ToLowerInvariant());
        var enc = BigInteger.ModPow(new BigInteger(md5, isUnsigned: true, isBigEndian: true), e, n);
        var bytes = enc.IsZero ? [] : enc.ToByteArray(isUnsigned: true, isBigEndian: true);
        return new string('0', 64) + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    async Task<JsonNode?> Call(string action, JsonNode? post = null, string version = "1.0", bool withSid = true, IReadOnlyDictionary<string, string>? extra = null)
    {
        var p = new Dictionary<string, string>
        {
            ["a"] = action, ["v"] = version, ["clientId"] = ClientId, ["deviceId"] = _deviceId, ["appVersion"] = "1.1.6",
            ["appRnVersion"] = "5.14", ["channel"] = "oem", ["platform"] = "Linux", ["os"] = "Android", ["osSystem"] = "9",
            ["lang"] = "pt", ["timeZoneId"] = "America/Sao_Paulo", ["ttid"] = "sdk_tuya@" + ClientId, ["et"] = "0.0.1",
            ["sdkVersion"] = "3.10.0", ["requestId"] = Guid.NewGuid().ToString(),
            ["time"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
        };
        foreach (var (k, v) in extra ?? new Dictionary<string, string>()) p[k] = v;
        if (withSid)
        {
            if (Sid == null) await LoginLocked();
            p["sid"] = Sid!;
        }
        string? postData = post?.ToJsonString();
        var signed = new Dictionary<string, string>(p);
        if (postData != null) signed["postData"] = postData;
        p["sign"] = Sign(signed);
        var url = Endpoint + "?" + string.Join("&", p.Select(kv => WebUtility.UrlEncode(kv.Key) + "=" + WebUtility.UrlEncode(kv.Value)));

        JsonObject? res = null;
        for (int attempt = 0; attempt < 3; attempt++) // the connection sometimes lags (antivirus, Wi-Fi): try again
        {
            try
            {
                using var content = new FormUrlEncodedContent(postData == null ? [] : [new KeyValuePair<string, string>("postData", postData)]);
                using var msg = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
                msg.Headers.TryAddWithoutValidation("User-Agent", "TY-UA=APP/Android/1.1.6/SDK/null");
                using var r = await _http.SendAsync(msg);
                if (!r.IsSuccessStatusCode) throw new PositivoException($"A nuvem da Positivo recusou a conexão (HTTP {(int)r.StatusCode}).");
                res = JsonNode.Parse(await r.Content.ReadAsStringAsync()) as JsonObject;
                break;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                if (attempt == 2)
                    throw new PositivoException("Sem conexão com a nuvem da Positivo. Confira a internet do PC." + (ex.Message.Length > 0 ? $" ({ex.Message})" : ""));
                await _delay(TimeSpan.FromSeconds(1 + attempt));
            }
        }
        if (res != null && Hme.Devices.Bool(res["success"]) == true) return res["result"];
        string code = HmeConfig.Str(res?["errorCode"]), msgText = HmeConfig.Str(res?["errorMsg"]);
        if (code is "USER_SESSION_INVALID" or "USER_SESSION_LOSS") throw new PositivoSessionExpired(msgText);
        throw new PositivoException(Explain(code, msgText));
    }

    async Task<JsonNode?> Api(string action, JsonNode? post = null, IReadOnlyDictionary<string, string>? extra = null)
    {
        await _gate.WaitAsync();
        try
        {
            try { return await Call(action, post, extra: extra); }
            catch (PositivoSessionExpired)
            {
                Sid = null;
                return await Call(action, post, extra: extra);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task Login()
    {
        await _gate.WaitAsync();
        try { await LoginLocked(); }
        finally { _gate.Release(); }
    }

    async Task LoginLocked()
    {
        var tok = await Call("tuya.m.user.email.token.create", new JsonObject { ["countryCode"] = Country, ["email"] = _email }, withSid: false);
        var n = BigInteger.Parse(Text(tok?["publicKey"]), CultureInfo.InvariantCulture);
        var e = BigInteger.Parse(Text(tok?["exponent"]), CultureInfo.InvariantCulture);
        var res = await Call("tuya.m.user.email.password.login", new JsonObject
        {
            ["countryCode"] = Country, ["email"] = _email, ["ifencrypt"] = 1, ["options"] = "{\"group\": 1}",
            ["passwd"] = EncryptPassword(_password, n, e), ["token"] = HmeConfig.Str(tok?["token"]),
        }, withSid: false);
        Sid = HmeConfig.Str(res?["sid"]);
        try { OnSession?.Invoke(Sid); }
        catch (Exception ex) when (ex is HmeSecretException or System.IO.IOException) { Core.Log.Write("hme: Positivo session not saved: " + ex.Message); }
    }

    static string Text(JsonNode? n) => n is JsonValue v ? v.ToJsonString().Trim('"') : "";

    async Task<JsonObject> Learn(string devId)
    {
        var info = await Api("tuya.m.device.get", new JsonObject { ["devId"] = devId }) as JsonObject ?? new JsonObject();
        var schemaNode = info["schema"];
        if (schemaNode is JsonValue sv && sv.TryGetValue<string>(out var text)) schemaNode = JsonNode.Parse(text);
        var ids = new Dictionary<string, string>();
        foreach (var s in (schemaNode as JsonArray ?? new()).OfType<JsonObject>())
            if (s["id"] != null && HmeConfig.Str(s["code"]) is { Length: > 0 } code) ids[Text(s["id"])] = code;
        _schemas[devId] = ids;
        _codes[devId] = ids.ToDictionary(kv => kv.Value, kv => kv.Key);
        var gw = HmeConfig.Str(info["devId"]);
        _meta[devId] = (HmeConfig.Str(info["name"]) is { Length: > 0 } nm ? nm : "?", HmeConfig.Str(info["productId"]),
            HmeConfig.Str(info["category"]), gw.Length > 0 ? gw : devId);
        return info;
    }

    JsonObject Normalize(string devId, JsonObject? dps, bool online)
    {
        var ids = _schemas.GetValueOrDefault(devId) ?? new();
        var meta = _meta.TryGetValue(devId, out var known) ? known : (Name: "?", ProductId: "", Category: "", Gw: devId);
        var status = new JsonArray();
        foreach (var (dp, val) in dps ?? new JsonObject())
        {
            if (!ids.TryGetValue(dp, out var code)) continue;
            var value = code.StartsWith("colour_data", StringComparison.Ordinal) && val != null ? ColourFromRaw(code, val) : val?.DeepClone();
            status.Add(new JsonObject { ["code"] = code, ["value"] = value });
        }
        var codes = ids.Values.ToHashSet();
        var cat = meta.Category.Length > 0 ? meta.Category : ProductHint.GetValueOrDefault(meta.ProductId, "");
        if (cat.Length == 0)
        {
            var name = meta.Name.ToLowerInvariant();
            if (codes.Contains("switch_led")) cat = "dj";
            else if (codes.Overlaps(["cur_power", "cur_current", "add_ele"]) || name.Contains("tomada", StringComparison.Ordinal) || name.Contains("plug", StringComparison.Ordinal)) cat = "cz";
            else if (codes.Overlaps(["ir_send", "control", "study_code"])) cat = "wnykq";
            else cat = "kg";
        }
        return new JsonObject
        {
            ["id"] = Prefix + devId, ["name"] = meta.Name, ["online"] = online, ["category"] = cat, ["status"] = status, ["source"] = "positivo",
        };
    }

    public async Task<List<JsonObject>> Devices(bool force, int ttlSeconds)
    {
        if (!force && (DateTime.UtcNow - _cacheAt).TotalSeconds < ttlSeconds) return _cache;
        var output = new List<JsonObject>();
        foreach (var g in (await Api("tuya.m.location.list") as JsonArray ?? new()).OfType<JsonObject>())
        {
            var gid = Text(g["groupId"] ?? g["gid"] ?? g["id"]);
            foreach (var item in (await Api("tuya.m.my.group.device.list", extra: new Dictionary<string, string> { ["gid"] = gid }) as JsonArray ?? new()).OfType<JsonObject>())
            {
                var d = item;
                var did = HmeConfig.Str(d["devId"]);
                if (did.Length == 0) continue;
                if (!_schemas.ContainsKey(did))
                {
                    var merged = (JsonObject)(await Learn(did)).DeepClone();
                    foreach (var (k, v) in d) merged[k] = v?.DeepClone();
                    d = merged;
                }
                var dps = d["dps"] as JsonObject ?? await Api("tuya.m.device.dp.get", new JsonObject { ["devId"] = did }) as JsonObject;
                output.Add(Normalize(did, dps, Hme.Devices.Bool(d["isOnline"] ?? d["online"]) ?? true));
            }
        }
        (_cache, _cacheAt) = (output, DateTime.UtcNow);
        return output;
    }

    static string Raw(string id) => id.StartsWith(Prefix, StringComparison.Ordinal) ? id[Prefix.Length..] : id;

    public IReadOnlyDictionary<string, JsonNode?> Status(string id)
    {
        var d = _cache.FirstOrDefault(x => HmeConfig.Str(x["id"]) == Prefix + Raw(id));
        return d == null ? new Dictionary<string, JsonNode?>() : Hme.Devices.StatusMap(d);
    }

    /// <summary>Official-cloud commands [{code, value}] turned into the device's raw dps.</summary>
    public async Task Send(string id, IReadOnlyList<JsonObject> cmds)
    {
        var raw = Raw(id);
        if (!_codes.ContainsKey(raw)) await Learn(raw);
        var map = _codes[raw];
        var dps = new JsonObject();
        foreach (var c in cmds)
        {
            var code = HmeConfig.Str(c["code"]);
            if (!map.TryGetValue(code, out var dp)) continue;
            dps[dp] = code.StartsWith("colour_data", StringComparison.Ordinal) && c["value"] is JsonNode v
                ? ColourToRaw(code, v is JsonObject o ? o : JsonNode.Parse(HmeConfig.Str(v)) as JsonObject ?? new())
                : c["value"]?.DeepClone();
        }
        if (dps.Count == 0) throw new PositivoException("Esse aparelho não tem essa função.");
        var gw = _meta.TryGetValue(raw, out var m) ? m.Gw : raw;
        await Api("tuya.m.device.dp.publish", new JsonObject { ["devId"] = raw, ["gwId"] = gw, ["dps"] = dps.ToJsonString() });
        var d = _cache.FirstOrDefault(x => HmeConfig.Str(x["id"]) == Prefix + raw);
        if (d?["status"] is not JsonArray st) return; // the saved state changes instead of reading the cloud again
        foreach (var s in st.OfType<JsonObject>())
        {
            var code = HmeConfig.Str(s["code"]);
            if (cmds.FirstOrDefault(c => HmeConfig.Str(c["code"]) == code) is { } cmd) s["value"] = cmd["value"]?.DeepClone();
        }
    }

    static int Hex(string s, int start, int len) => int.Parse(s.AsSpan(start, len), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    /// <summary>'hhhhssssvvvv' (v2) or 'rrggbbhhhhssvv' (v1) into the official cloud's {h, s, v}.</summary>
    public static JsonObject ColourFromRaw(string code, JsonNode raw)
    {
        if (raw is JsonObject o) return (JsonObject)o.DeepClone();
        var s = HmeConfig.Str(raw);
        try
        {
            if (code.EndsWith("v2", StringComparison.Ordinal) && s.Length >= 12)
                return new JsonObject { ["h"] = Hex(s, 0, 4), ["s"] = Hex(s, 4, 4), ["v"] = Hex(s, 8, 4) };
            if (s.Length >= 14)
                return new JsonObject { ["h"] = Hex(s, 6, 4), ["s"] = Hex(s, 10, 2), ["v"] = Hex(s, 12, 2) };
        }
        catch (FormatException) { }
        return new JsonObject { ["h"] = 0, ["s"] = 0, ["v"] = 0 };
    }

    public static string ColourToRaw(string code, JsonObject v)
    {
        int h = HmeConfig.Int(v["h"], 0), s = HmeConfig.Int(v["s"], 0), val = HmeConfig.Int(v["v"], 0);
        if (code.EndsWith("v2", StringComparison.Ordinal)) return $"{h:x4}{s:x4}{val:x4}";
        var (r, g, b) = HsvToRgb((((h % 360) + 360) % 360) / 360.0, s / 255.0, val / 255.0);
        return $"{(int)(r * 255):x2}{(int)(g * 255):x2}{(int)(b * 255):x2}{h:x4}{s:x2}{val:x2}";
    }

    /// <summary>Python's colorsys.hsv_to_rgb.</summary>
    static (double, double, double) HsvToRgb(double h, double s, double v)
    {
        if (s == 0) return (v, v, v);
        int i = (int)(h * 6);
        double f = h * 6 - i, p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
        return (i % 6) switch
        {
            0 => (v, t, p), 1 => (q, v, p), 2 => (p, v, t), 3 => (p, q, v), 4 => (t, p, v), _ => (v, p, q),
        };
    }

    static string Explain(string code, string msg) => code switch
    {
        "USER_PASSWD_WRONG" => "E-mail ou senha da Positivo incorretos.",
        "USER_NAME_IS_NOT_EXIST" => "Não existe conta Positivo com esse e-mail.",
        "ILLEGAL_CLIENT_ID" or "SING_VALIDATE_FALED" or "SIGN_VALIDATE_FAILED" or "API_OR_API_VERSION_WRONG" => "A Positivo mudou o app e essa conexão não funciona mais.",
        "DEVICE_NOT_ONLINE" => "O aparelho está offline.",
        _ => $"{(msg.Length > 0 ? msg : "Erro")} ({code})",
    };

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
