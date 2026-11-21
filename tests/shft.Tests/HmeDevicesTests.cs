using System.IO;
using System.Text.Json.Nodes;
using Shft.Hme;
using Xunit;

namespace Shft.Tests;

public sealed class HmeDevicesTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "hme-dev-" + Guid.NewGuid().ToString("N"));
    readonly HmeConfig _cfg;

    public HmeDevicesTests()
    {
        Directory.CreateDirectory(_dir);
        _cfg = HmeConfig.Load(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, true);

    static JsonObject Raw(string id, string name, string category, params (string Code, JsonNode? Value)[] status)
    {
        var st = new JsonArray();
        foreach (var (code, value) in status) st.Add(new JsonObject { ["code"] = code, ["value"] = value });
        return new JsonObject { ["id"] = id, ["name"] = name, ["category"] = category, ["online"] = true, ["status"] = st };
    }

    static Dictionary<string, JsonNode?> St(params (string Code, JsonNode? Value)[] status) => status.ToDictionary(s => s.Code, s => s.Value);

    [Fact]
    public void Scales()
    {
        Assert.Equal(100, Devices.ToPct(1000, "bright_value_v2"));
        Assert.Equal(0, Devices.ToPct(10, "bright_value_v2"));
        Assert.Equal(50, Devices.ToPct(140, "bright_value"));
        Assert.Equal(500, Devices.FromPct(50, "temp_value_v2"));
        Assert.Equal(255, Devices.FromPct(150, "bright_value"));
    }

    [Fact]
    public void Parse_light_colour_mode()
    {
        var d = Devices.Parse(Raw("l1", "Fita", "dj", ("switch_led", true), ("work_mode", "colour"),
            ("colour_data_v2", new JsonObject { ["h"] = 120, ["s"] = 500, ["v"] = 800 }), ("bright_value_v2", 1000)), _cfg)!;
        Assert.Equal(DeviceKind.Light, d.Kind);
        Assert.Equal(120, d.Light!.H);
        Assert.Equal(50, d.Light.S);
        Assert.Equal(80, d.Light.Brightness);
        Assert.True(d.Light.HasColor && d.Light.HasBright && !d.Light.HasTemp);
    }

    [Fact]
    public void Parse_colour_given_as_json_text()
    {
        var d = Devices.Parse(Raw("l1", "Fita", "dj", ("switch_led", true), ("work_mode", "colour"),
            ("colour_data", """{"h":30,"s":255,"v":255}""")), _cfg)!;
        Assert.Equal(30, d.Light!.H);
        Assert.Equal(100, d.Light.S);
    }

    [Fact]
    public void Parse_multi_switch_names_channels()
    {
        _cfg.ChannelNames["m1"] = new JsonArray("Teto", "");
        var d = Devices.Parse(Raw("m1", "Interruptor", "kg", ("switch_2", false), ("switch_1", true)), _cfg)!;
        Assert.Equal(DeviceKind.Multi, d.Kind);
        Assert.Equal(new[] { "Teto", "Tecla 2" }, d.Channels.Select(c => c.Name));
        Assert.Equal(new[] { "switch_1", "switch_2" }, d.Channels.Select(c => c.Code));
        Assert.True(d.On);
    }

    [Fact]
    public void Parse_plug_by_category_and_custom_name()
    {
        _cfg.Names["p1"] = "Tomada da TV";
        var p = Devices.Parse(Raw("p1", "Smart Plug", "cz", ("switch", true)), _cfg)!;
        Assert.Equal(DeviceKind.Plug, p.Kind);
        Assert.Equal("Tomada da TV", p.Name);
        Assert.Equal("Smart Plug", p.OriginalName);
        Assert.Equal(DeviceKind.Switch, Devices.Parse(Raw("s1", "Luz", "kg", ("switch_1", true)), _cfg)!.Kind);
        Assert.Null(Devices.Parse(Raw("x", "Sensor", "wsdcg", ("va_temperature", 250)), _cfg));
    }

    [Fact]
    public void Light_commands_colour_keeps_brightness()
    {
        var cmds = Devices.LightCommands(St(("switch_led", true), ("work_mode", "white"), ("bright_value_v2", 550),
            ("colour_data_v2", new JsonObject { ["h"] = 0, ["s"] = 0, ["v"] = 1000 })), new LightChange(Color: (200, 100)));
        Assert.Equal(2, cmds.Count);
        Assert.Equal("work_mode", (string)cmds[0]["code"]!);
        Assert.Equal("colour", (string)cmds[0]["value"]!);
        Assert.Equal("""{"h":200,"s":1000,"v":550}""", cmds[1]["value"]!.ToJsonString());
    }

    [Fact]
    public void Light_commands_turn_on_first()
    {
        var cmds = Devices.LightCommands(St(("switch_led", false), ("bright_value_v2", 500)), new LightChange(Brightness: 40));
        Assert.Equal("switch_led", (string)cmds[0]["code"]!);
        Assert.True((bool)cmds[0]["value"]!);
        Assert.Equal(406, (int)cmds[1]["value"]!);
    }

    [Fact]
    public void Light_commands_v1_colour_is_json_text()
    {
        var cmds = Devices.LightCommands(St(("switch_led", true), ("work_mode", "colour"),
            ("colour_data", """{"h":10,"s":255,"v":255}""")), new LightChange(Brightness: 50));
        Assert.Single(cmds);
        Assert.Equal("""{"h": 10, "s": 255, "v": 128}""", (string)cmds[0]["value"]!); // json.dumps spacing, as Python sent it
    }

    [Fact]
    public void Light_commands_temp_sets_white_and_brightness()
    {
        var cmds = Devices.LightCommands(St(("switch_led", true), ("temp_value_v2", 0), ("bright_value_v2", 10)),
            new LightChange(Temp: 30, Brightness: 100));
        Assert.Equal(new[] { "work_mode", "temp_value_v2", "bright_value_v2" }, cmds.Select(c => (string)c["code"]!));
        Assert.Equal(300, (int)cmds[1]["value"]!);
        Assert.Equal(1000, (int)cmds[2]["value"]!);
    }

    [Fact]
    public void Room_match()
    {
        Assert.Equal("Cozinha", Rooms.Match("Luz Cozinha 1", []));
        Assert.Equal("Suíte", Rooms.Match("Abajur da Suíte", []));
        Assert.Equal("Banheiro", Rooms.Match("lavabo", []));
        Assert.Equal("Estúdio", Rooms.Match("Luz do Estudio", ["Estúdio"]));
        Assert.Equal("Sala Gamer", Rooms.Match("Luz da Sala Gamer", ["Sala Gamer"]));
        Assert.Null(Rooms.Match("Fita LED", []));
    }

    static Device Dev(string id, string name) => new(id, name, name, true, "kg", DeviceKind.Switch, false, null, []);

    [Fact]
    public void Room_order_appends_unknown_and_other_last()
    {
        _cfg.Root["rooms"] = new JsonArray("Sala");
        var devs = new[] { Dev("a", "x") with { Room = "Varanda" }, Dev("b", "y") with { Room = Rooms.Other }, Dev("c", "z") with { Room = "Sala" } };
        Assert.Equal(new[] { "Sala", "Varanda", "Outros" }, Rooms.Order(devs, _cfg));
    }

    [Fact]
    public void Device_room_override_wins()
    {
        var none = new Dictionary<string, string>();
        _cfg.DeviceRoom["a"] = "Quarto";
        Assert.Equal("Quarto", Rooms.Of(Dev("a", "Luz da Cozinha"), _cfg, none));
        _cfg.DeviceRoom["a"] = "";
        Assert.Equal("Outros", Rooms.Of(Dev("a", "Luz da Cozinha"), _cfg, none));
        Assert.Equal("Sala", Rooms.Of(Dev("b", "Fita"), _cfg, new Dictionary<string, string> { ["b"] = "Sala de estar" }));
    }

    [Fact]
    public void Visible_hides_by_id_or_original_name_and_sets_room()
    {
        _cfg.Hide.Add("a");
        _cfg.Hide.Add("Luz velha");
        var shown = Devices.Visible([Dev("a", "x"), Dev("b", "Luz velha"), Dev("c", "Luz da Sala")], _cfg, new Dictionary<string, string>());
        Assert.Equal("c", Assert.Single(shown).Id);
        Assert.Equal("Sala", shown[0].Room);
    }
}
