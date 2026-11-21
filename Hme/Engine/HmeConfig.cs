using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shft.Core;

namespace Shft.Hme;

/// <summary>
/// Controle Casa's config.json, read and written as JSON so fields hme. does not know survive. Same folder, same
/// defaults and same merge as core.py; secrets open in memory and stay DPAPI-protected on disk.
/// </summary>
public sealed class HmeConfig
{
    public static readonly string DefaultFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ControleCasa");

    public static readonly string[] AllModes = ["cool", "heat", "auto", "fan", "dry"];

    const string Defaults = """
    {
      "configured": false, "demo": false, "api_region": "us", "access_id": "", "access_secret": "",
      "ac": {"room_name": "Quarto", "type": "auto", "infrared_id": "", "remote_id": "", "device_id": "",
             "method": "", "model": "", "modes": ["cool", "heat", "auto", "fan", "dry"], "temp_min": 17, "temp_max": 30},
      "room_sensor_id": "", "names": {}, "channel_names": {}, "hide": [],
      "wifi_ac_codes": {
        "power": "switch", "temp": "temp_set", "temp_scale": 1, "mode": "mode",
        "mode_map": {"cool": "cold", "heat": "hot", "auto": "auto", "fan": "wind", "dry": "wet"},
        "fan": "windspeed", "fan_map": {"auto": "auto", "low": "low", "mid": "middle", "high": "high"}, "turbo": ""
      },
      "ui": {"theme": "auto", "tint": 12, "backdrop": "black"},
      "positivo": {"email": "", "password": "", "session": "", "device_id": ""},
      "rooms": ["Sala", "Cozinha", "Quarto", "Banheiro", "Escritório"],
      "device_room": {}, "port": 8765
    }
    """;

    string _secret = "";

    HmeConfig(string folder, JsonObject root)
    {
        Folder = folder;
        Root = root;
    }

    public string Folder { get; }
    public JsonObject Root { get; }
    public string? SecretError { get; private set; }
    string FilePath => Path.Combine(Folder, "config.json");

    public static HmeConfig Load(string folder)
    {
        JsonObject saved = new();
        var path = Path.Combine(folder, "config.json");
        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject o) saved = o;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Log.Write("hme: config not read, using defaults: " + ex.Message);
        }

        bool hadRooms = saved.ContainsKey("rooms");
        var root = Merge((JsonObject)JsonNode.Parse(Defaults)!, saved);
        if (!hadRooms && Bool(root["configured"])) // who used v10 keeps that version's list
            root["rooms"] = new JsonArray("Sala", "Cozinha", "Quarto", "Suíte", "Estúdio");

        var cfg = new HmeConfig(folder, root);
        bool legacy = false;
        var raw = Str(root["access_secret"]);
        if (raw.Length > 0)
        {
            try
            {
                cfg._secret = Secrets.Unprotect(raw);
                legacy |= !Secrets.IsProtected(raw);
            }
            catch (HmeSecretException ex)
            {
                cfg.SecretError = ex.Message;
            }
        }
        var pw = Str(cfg.Positivo["password"]);
        legacy |= pw.Length > 0 && !Secrets.IsProtected(pw);
        if (legacy)
        {
            try { cfg.Save(); }
            catch (Exception ex) when (ex is HmeSecretException or IOException or UnauthorizedAccessException)
            {
                Log.Write("hme: old config not re-protected: " + ex.Message);
            }
        }
        return cfg;
    }

    /// <summary>core.py's _merge; a default object also replaces a saved value of another type.</summary>
    static JsonObject Merge(JsonObject baseObj, JsonObject extra)
    {
        foreach (var (key, value) in extra.ToList())
        {
            if (baseObj[key] is JsonObject b)
            {
                if (value is JsonObject e) Merge(b, (JsonObject)e.DeepClone());
                continue;
            }
            baseObj[key] = value?.DeepClone();
        }
        return baseObj;
    }

    /// <summary>Atomic write; secrets always protected. If protecting fails, nothing is written.</summary>
    public void Save()
    {
        var output = (JsonObject)Root.DeepClone();
        output["access_secret"] = Secrets.Protect(_secret);
        var pos = (JsonObject)output["positivo"]!;
        foreach (var key in new[] { "password", "session" })
        {
            var v = Str(pos[key]);
            if (v.Length > 0 && !Secrets.IsProtected(v))
            {
                v = Secrets.Protect(Secrets.Unprotect(v));
                pos[key] = v;
                Positivo[key] = v;
            }
        }
        Directory.CreateDirectory(Folder);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, output.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        File.Move(tmp, FilePath, overwrite: true);
        Root["access_secret"] = output["access_secret"]!.DeepClone();
    }

    internal static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    internal static bool Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    internal static int Int(JsonNode? n, int fallback)
    {
        if (n is not JsonValue v) return fallback;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d)) return (int)Math.Round(d);
        return fallback;
    }

    internal static double Num(JsonNode? n, double fallback)
    {
        if (n is not JsonValue v) return fallback;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<int>(out var i)) return i;
        return fallback;
    }

    public string Region { get => Str(Root["api_region"]) is { Length: > 0 } r ? r : "us"; set => Root["api_region"] = value; }
    public string AccessId { get => Str(Root["access_id"]); set => Root["access_id"] = value; }
    public string AccessSecret { get => _secret; set { _secret = value; SecretError = null; } }
    public bool Configured { get => Bool(Root["configured"]); set => Root["configured"] = value; }
    public bool Demo { get => Bool(Root["demo"]); set => Root["demo"] = value; }
    public int Port => Int(Root["port"], 8765);
    public JsonObject Ac => (JsonObject)Root["ac"]!;
    public JsonObject WifiAcCodes => (JsonObject)Root["wifi_ac_codes"]!;
    public JsonObject Names => (JsonObject)Root["names"]!;
    public JsonObject ChannelNames => (JsonObject)Root["channel_names"]!;
    public JsonObject DeviceRoom => (JsonObject)Root["device_room"]!;
    public JsonObject Positivo => (JsonObject)Root["positivo"]!;
    public JsonArray Hide => Root["hide"] as JsonArray ?? (JsonArray)(Root["hide"] = new JsonArray());
    public JsonArray Rooms => Root["rooms"] as JsonArray ?? (JsonArray)(Root["rooms"] = new JsonArray());
    public string RoomSensorId { get => Str(Root["room_sensor_id"]); set => Root["room_sensor_id"] = value; }

    public int TempMin => Int(Ac["temp_min"], 17);
    public int TempMax => Int(Ac["temp_max"], 30);
    public double WifiTempScale => Num(WifiAcCodes["temp_scale"], 1) is var s && s > 0 ? s : 1;

    public IReadOnlyList<string> AcModes
    {
        get
        {
            var modes = (Ac["modes"] as JsonArray)?.Select(Str).Where(m => AllModes.Contains(m)).ToList();
            return modes is { Count: > 0 } ? modes : AllModes;
        }
    }

    /// <summary>New in hme.: whether the AC card and shortcuts show. Unset: true when an AC was already chosen.</summary>
    public bool HasAc
    {
        get
        {
            if (Root["has_ac"] is JsonValue v && v.TryGetValue<bool>(out var b)) return b;
            return Str(Ac["remote_id"]).Length > 0 || Str(Ac["device_id"]).Length > 0 || Str(Ac["infrared_id"]).Length > 0;
        }
        set => Root["has_ac"] = value;
    }
}
