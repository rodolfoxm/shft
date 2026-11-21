using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Shft.Hme;

public enum DeviceKind { Light, Switch, Plug, Multi }

public sealed record Channel(string Code, string Name, bool On);

public sealed record LightState(bool HasBright, bool HasColor, bool HasTemp, string Mode, int Brightness, int Temp, int H, int S);

public sealed record Device(string Id, string Name, string OriginalName, bool Online, string Category, DeviceKind Kind, bool On,
    LightState? Light, IReadOnlyList<Channel> Channels, string Room = "");

public sealed record LightChange(bool? On = null, int? Brightness = null, int? Temp = null, (int H, int S)? Color = null);

/// <summary>core.py's device model: Tuya status codes turned into what the panel draws, and back into commands.</summary>
public static partial class Devices
{
    public const string LightOn = "switch_led";

    static readonly Dictionary<string, (int Lo, int Hi)> Scales = new()
    {
        ["bright_value_v2"] = (10, 1000), ["bright_value"] = (25, 255),
        ["temp_value_v2"] = (0, 1000), ["temp_value"] = (0, 255),
    };

    [GeneratedRegex(@"^switch(_\d+)?$")]
    private static partial Regex SwitchCode();

    public static bool IsSwitchCode(string code) => SwitchCode().IsMatch(code);

    static int Round(double v) => (int)Math.Round(v, MidpointRounding.ToEven); // Python's round()

    public static int ToPct(double v, string code)
    {
        var (lo, hi) = Scales.GetValueOrDefault(code, (10, 1000));
        return Math.Clamp(Round((v - lo) / (hi - lo) * 100), 0, 100);
    }

    public static int FromPct(double p, string code)
    {
        var (lo, hi) = Scales.GetValueOrDefault(code, (10, 1000));
        return Round(lo + (hi - lo) * Math.Clamp(p, 0, 100) / 100);
    }

    public static IReadOnlyDictionary<string, JsonNode?> StatusMap(JsonObject raw)
    {
        var map = new Dictionary<string, JsonNode?>();
        if (raw["status"] is JsonArray arr)
            foreach (var s in arr.OfType<JsonObject>())
                if (HmeConfig.Str(s["code"]) is { Length: > 0 } code) map[code] = s["value"];
        return map;
    }

    internal static bool? Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    internal static double? Number(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<double>(out var d) ? d : n is JsonValue w && w.TryGetValue<int>(out var i) ? i : null;

    static (string? Bright, string? Colour, string? Temp) Codes(IReadOnlyDictionary<string, JsonNode?> st) => (
        new[] { "bright_value_v2", "bright_value" }.FirstOrDefault(st.ContainsKey),
        new[] { "colour_data_v2", "colour_data" }.FirstOrDefault(st.ContainsKey),
        new[] { "temp_value_v2", "temp_value" }.FirstOrDefault(st.ContainsKey));

    static JsonObject Colour(IReadOnlyDictionary<string, JsonNode?> st, string code)
    {
        var v = st.GetValueOrDefault(code);
        if (v is JsonValue text && text.TryGetValue<string>(out var s))
        {
            try { v = JsonNode.Parse(s); }
            catch (JsonException) { v = null; }
        }
        return v as JsonObject ?? new JsonObject();
    }

    static double Part(JsonObject colour, string key, double fallback) => Number(colour[key]) ?? fallback;

    static int SwitchNumber(string code) => code.LastIndexOf('_') is var i and >= 0 && int.TryParse(code[(i + 1)..], out var n) ? n : 0;

    /// <summary>A Tuya device as the panel shows it; null when it has nothing to switch.</summary>
    public static Device? Parse(JsonObject raw, HmeConfig cfg)
    {
        var st = StatusMap(raw);
        var id = HmeConfig.Str(raw["id"]);
        var original = HmeConfig.Str(raw["name"]) is { Length: > 0 } n ? n : "?";
        var name = HmeConfig.Str(cfg.Names[id]) is { Length: > 0 } custom ? custom : original;
        bool online = Bool(raw["online"]) ?? false;
        var category = HmeConfig.Str(raw["category"]);

        if (Bool(st.GetValueOrDefault(LightOn)) is bool lightOn)
        {
            var (bc, cc, tc) = Codes(st);
            var mode = HmeConfig.Str(st.GetValueOrDefault("work_mode")) is { Length: > 0 } m ? m : "white";
            int brightness = 100, temp = 50, h = 30, s = 0;
            if (bc != null && Number(st[bc]) is double b) brightness = ToPct(b, bc);
            if (tc != null && Number(st[tc]) is double t) temp = ToPct(t, tc);
            if (cc != null)
            {
                var col = Colour(st, cc);
                double smax = cc.EndsWith("v2", StringComparison.Ordinal) ? 1000 : 255;
                h = (int)Part(col, "h", 0);
                s = Round(Part(col, "s", 0) / smax * 100);
                if (mode == "colour") brightness = Round(Part(col, "v", smax) / smax * 100);
            }
            return new Device(id, name, original, online, category, DeviceKind.Light, lightOn,
                new LightState(bc != null, cc != null, tc != null, mode, brightness, temp, h, s), []);
        }

        var switches = st.Where(kv => IsSwitchCode(kv.Key) && Bool(kv.Value) != null).Select(kv => kv.Key).OrderBy(SwitchNumber).ToList();
        if (switches.Count == 0) return null;
        if (switches.Count > 1)
        {
            var names = (cfg.ChannelNames[id] ?? cfg.ChannelNames[original]) as JsonArray;
            var channels = switches.Select((code, i) =>
            {
                var label = names != null && i < names.Count ? HmeConfig.Str(names[i]) : "";
                return new Channel(code, label.Length > 0 ? label : $"Tecla {i + 1}", Bool(st[code]) == true);
            }).ToList();
            return new Device(id, name, original, online, category, DeviceKind.Multi, channels.Any(c => c.On), null, channels);
        }
        var kind = category is "cz" or "pc" ? DeviceKind.Plug : DeviceKind.Switch;
        return new Device(id, name, original, online, category, kind, Bool(st[switches[0]]) == true, null, []);
    }

    static JsonObject Cmd(string code, JsonNode? value) => new() { ["code"] = code, ["value"] = value };

    /// <summary>core.py's light_commands: colour keeps the brightness, white keeps the colour out, off lights turn on first.</summary>
    public static List<JsonObject> LightCommands(IReadOnlyDictionary<string, JsonNode?> st, LightChange change)
    {
        var (bc, cc, tc) = Codes(st);
        var cmds = new List<JsonObject>();
        bool v2 = (cc ?? "").EndsWith("v2", StringComparison.Ordinal);
        double smax = v2 ? 1000 : 255;
        bool colourMode = HmeConfig.Str(st.GetValueOrDefault("work_mode")) == "colour";

        JsonNode ColourValue(double h, double s, double v)
        {
            int hh = (int)h % 360, ss = Round(s / 100 * smax), vv = (int)Math.Max(Round(v / 100 * smax), smax * 0.01);
            if (v2) return new JsonObject { ["h"] = hh, ["s"] = ss, ["v"] = vv };
            return JsonValue.Create(string.Create(CultureInfo.InvariantCulture, $"{{\"h\": {hh}, \"s\": {ss}, \"v\": {vv}}}"))!;
        }

        if (change.Color is { } color && cc != null)
        {
            double current = change.Brightness ?? (colourMode
                ? Part(Colour(st, cc), "v", smax) / smax * 100
                : bc != null && Number(st[bc]) is double b ? ToPct(b, bc) : 100);
            cmds.Add(Cmd("work_mode", "colour"));
            cmds.Add(Cmd(cc, ColourValue(color.H, color.S, current)));
        }
        else if (change.Temp is int temp && tc != null)
        {
            cmds.Add(Cmd("work_mode", "white"));
            cmds.Add(Cmd(tc, FromPct(temp, tc)));
            if (change.Brightness is int br && bc != null) cmds.Add(Cmd(bc, FromPct(br, bc)));
        }
        else if (change.Brightness is int br)
        {
            if (colourMode && cc != null)
            {
                var col = Colour(st, cc);
                cmds.Add(Cmd(cc, ColourValue(Part(col, "h", 0), Part(col, "s", smax) / smax * 100, br)));
            }
            else if (bc != null)
            {
                cmds.Add(Cmd(bc, FromPct(br, bc)));
            }
        }
        if (cmds.Count > 0 && change.On == null && Bool(st.GetValueOrDefault(LightOn)) != true)
            cmds.Insert(0, Cmd(LightOn, true));
        return cmds;
    }

    /// <summary>core.py's visible: hidden ones out (by id or original name), the rest placed in a room.</summary>
    public static List<Device> Visible(IEnumerable<Device> devs, HmeConfig cfg, IReadOnlyDictionary<string, string> smartLifeRooms)
    {
        var hide = cfg.Hide.Select(HmeConfig.Str).ToHashSet();
        return devs.Where(d => !hide.Contains(d.Id) && !hide.Contains(d.OriginalName))
                   .Select(d => d with { Room = Rooms.Of(d, cfg, smartLifeRooms) }).ToList();
    }
}
