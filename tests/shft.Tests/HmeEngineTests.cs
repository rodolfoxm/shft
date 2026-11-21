using System.IO;
using System.Text.Json.Nodes;
using Shft.Hme;
using Xunit;

namespace Shft.Tests;

public sealed class HmeEngineTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "hme-eng-" + Guid.NewGuid().ToString("N"));
    readonly HmeConfig _cfg;
    readonly FakeCloud _cloud = new();
    DateTime _now = new(2026, 11, 21, 10, 0, 0);
    JsonArray _list = new();
    int _listCalls;

    public HmeEngineTests()
    {
        Directory.CreateDirectory(_dir);
        _cfg = HmeConfig.Load(_dir);
        _cfg.HasAc = false;
        _cloud.Answer = (m, p, b) =>
        {
            if (p == "/v1.0/iot-01/associated-users/devices")
            {
                _listCalls++;
                return FakeCloud.Ok(new JsonObject { ["devices"] = _list.DeepClone(), ["has_more"] = false });
            }
            return FakeCloud.Ok();
        };
    }

    public void Dispose() => Directory.Delete(_dir, true);

    static JsonObject Switch(string id, string name, bool on, string owner = "") => new()
    {
        ["id"] = id, ["name"] = name, ["category"] = "kg", ["online"] = true, ["owner_id"] = owner, ["ip"] = "10.0.0.9", ["local_key"] = "secret",
        ["status"] = new JsonArray(new JsonObject { ["code"] = "switch_1", ["value"] = on }),
    };

    HmeEngine Engine(PositivoClient? positivo = null) => new(_cfg, _cloud, new Usage(_dir, () => _now), new SavedList(_dir), positivo, () => _now);

    [Fact]
    public async Task Device_list_ttl_60s_active_600s_idle()
    {
        _list = new JsonArray(Switch("a", "Luz da Sala", true));
        var e = Engine();
        e.MarkActivity();
        await e.State();
        _now = _now.AddSeconds(30);
        await e.State();
        Assert.Equal(1, _listCalls);
        _now = _now.AddSeconds(31);
        await e.State();
        Assert.Equal(2, _listCalls);
        _now = _now.AddSeconds(400); // idle now
        await e.State();
        Assert.Equal(2, _listCalls);
        _now = _now.AddSeconds(201);
        await e.State();
        Assert.Equal(3, _listCalls);
    }

    [Fact]
    public async Task Cache_only_never_calls_cloud()
    {
        var e = Engine();
        await e.State(cacheOnly: true);
        Assert.Empty(_cloud.Calls);
    }

    [Fact]
    public async Task Command_updates_cache_without_rereading()
    {
        _list = new JsonArray(Switch("a", "Luz", true));
        var e = Engine();
        e.MarkActivity();
        await e.State();
        var s = await e.SetDevice("a", on: false);
        Assert.False(s.Devices.Single().On);
        Assert.Equal(1, _listCalls);
        Assert.Contains(_cloud.Calls, c => c.Method == "POST" && c.Path == "/v1.0/iot-03/devices/a/commands" && c.Body!.Contains("\"switch_1\""));
    }

    [Fact]
    public async Task Command_route_fallback_and_memory()
    {
        _list = new JsonArray(Switch("a", "Luz", true));
        var answer = _cloud.Answer;
        _cloud.Answer = (m, p, b) => p.StartsWith("/v1.0/iot-03/devices/a/commands", StringComparison.Ordinal) ? FakeCloud.Fail("formato", 2017) : answer(m, p, b);
        var e = Engine();
        await e.SetDevice("a", on: false);
        int before = _cloud.Calls.Count;
        await e.SetDevice("a", on: true);
        Assert.Equal("/v2.0/cloud/thing/a/shadow/properties/issue", _cloud.Calls.Skip(before).First(c => c.Method == "POST").Path);
    }

    [Fact]
    public async Task Saved_list_when_cloud_fails()
    {
        new SavedList(_dir).Put("tuya", [Switch("a", "Luz da Sala", true), Switch("b", "Luz da Cozinha", false)]);
        _cloud.Answer = (_, _, _) => FakeCloud.Fail("sem internet", 0);
        var s = await Engine().State();
        Assert.Equal(2, s.Devices.Count);
        Assert.NotNull(s.Notice);
    }

    [Fact]
    public void Saved_list_strips_fields()
    {
        new SavedList(_dir).Put("tuya", [Switch("a", "Luz", true)]);
        var text = File.ReadAllText(Path.Combine(_dir, "aparelhos.json"));
        Assert.DoesNotContain("local_key", text);
        Assert.DoesNotContain("10.0.0.9", text);
        var d = (JsonObject)new SavedList(_dir).Get("tuya").Single();
        Assert.Equal(new[] { "id", "name", "category", "online", "status", "owner_id" }, d.Select(kv => kv.Key));
    }

    [Fact]
    public async Task Positivo_empty_keeps_previous_and_waits_60s()
    {
        int lists = 0;
        var http = new FakePositivo
        {
            Answer = a => a switch
            {
                "tuya.m.location.list" => ++lists == 1 ? """{"success":true,"result":[{"groupId":1}]}""" : """{"success":true,"result":[]}""",
                "tuya.m.my.group.device.list" => """{"success":true,"result":[{"devId":"p1","isOnline":true,"dps":{"1":true}}]}""",
                "tuya.m.device.get" => """{"success":true,"result":{"name":"Tomada","productId":"","category":"cz","devId":"p1","schema":"[{\"id\":1,\"code\":\"switch_1\"}]"}}""",
                _ => null,
            },
        };
        var e = Engine(new PositivoClient("a@b.c", "pw", "DEV", "SID", null, http, _ => Task.CompletedTask));
        Assert.Single((await e.State()).Devices);
        _now = _now.AddSeconds(700);
        Assert.Single((await e.State()).Devices); // came back empty: the panel keeps the last list
        http.FailNetwork = 9;
        _now = _now.AddSeconds(700);
        var s = await e.State();
        Assert.Single(s.Devices);
        Assert.NotNull(s.Notice);
        int actions = http.Actions.Count;
        _now = _now.AddSeconds(30);
        await e.State();
        Assert.Equal(actions, http.Actions.Count);
    }

    [Fact]
    public async Task Rooms_refresh_once_a_day()
    {
        _list = new JsonArray(Switch("a", "Fita", true, owner: "H1"));
        var answer = _cloud.Answer;
        _cloud.Answer = (m, p, b) => p switch
        {
            "/v1.0/homes/H1/rooms" => FakeCloud.Ok(new JsonObject { ["rooms"] = new JsonArray(new JsonObject { ["room_id"] = 5, ["name"] = "Varanda" }) }),
            "/v1.0/homes/H1/rooms/5/devices" => FakeCloud.Ok(new JsonArray(new JsonObject { ["id"] = "a" })),
            _ => answer(m, p, b),
        };
        var e = Engine();
        await e.State();
        _now = _now.AddHours(1);
        var s = await e.State();
        Assert.Equal("Varanda", s.Devices.Single().Room);
        Assert.Equal(1, _cloud.Calls.Count(c => c.Path == "/v1.0/homes/H1/rooms"));
    }

    [Fact]
    public async Task No_ac_hides_ac_and_skips_its_calls()
    {
        _cfg.Ac["type"] = "ir";
        _cfg.Ac["infrared_id"] = "H";
        _cfg.Ac["remote_id"] = "R";
        _cfg.HasAc = false;
        var s = await Engine().State();
        Assert.Null(s.Ac);
        Assert.DoesNotContain(_cloud.Calls, c => c.Path.Contains("infrareds", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ac_devices_are_not_listed_as_switches()
    {
        _cfg.HasAc = true;
        _cfg.Ac["type"] = "wifi";
        _cfg.Ac["device_id"] = "W";
        _list = new JsonArray(Switch("W", "Ar", true), Switch("b", "Luz", true));
        var s = await Engine().State();
        Assert.Equal("b", s.Devices.Single().Id);
        Assert.NotNull(s.Ac);
    }

    [Fact]
    public async Task Lights_off_only_on_lights_switches_multi_online_and_on()
    {
        var plug = Switch("p", "Tomada da Sala", true);
        plug["category"] = "cz";
        var offline = Switch("o", "Luz da Sala 2", true);
        offline["online"] = false;
        _list = new JsonArray(Switch("a", "Luz da Sala", true), Switch("k", "Luz da Cozinha", true), plug, offline, Switch("z", "Luz do Quarto", false));
        var e = Engine();
        await e.LightsOff("Sala");
        var posts = _cloud.Calls.Where(c => c.Method == "POST").Select(c => c.Path).ToList();
        Assert.Equal(new[] { "/v1.0/iot-03/devices/a/commands" }, posts);
        await e.LightsOff();
        Assert.Contains("/v1.0/iot-03/devices/k/commands", _cloud.Calls.Select(c => c.Path));
        Assert.DoesNotContain("/v1.0/iot-03/devices/p/commands", _cloud.Calls.Select(c => c.Path));
    }

    [Fact]
    public async Task Commands_are_serialized()
    {
        _list = new JsonArray(Switch("a", "Luz", true));
        int inFlight = 0, maxInFlight = 0;
        var slow = new SlowCloud(async () =>
        {
            maxInFlight = Math.Max(maxInFlight, Interlocked.Increment(ref inFlight));
            await Task.Delay(30);
            Interlocked.Decrement(ref inFlight);
        }, _cloud);
        var e = new HmeEngine(_cfg, slow, new Usage(_dir, () => _now), new SavedList(_dir), null, () => _now);
        await Task.WhenAll(e.SetDevice("a", on: false), e.SetDevice("a", on: true));
        Assert.Equal(1, maxInFlight);
        var bodies = _cloud.Calls.Where(c => c.Method == "POST").Select(c => c.Body).ToList();
        Assert.Contains("false", bodies[0]);
        Assert.Contains("true", bodies[1]);
    }

    sealed class SlowCloud(Func<Task> gate, ICloud inner) : ICloud
    {
        public async Task<JsonObject> Request(string path, string method = "GET", JsonNode? body = null, IReadOnlyDictionary<string, string>? query = null)
        {
            if (method == "POST") await gate();
            return await inner.Request(path, method, body, query);
        }
    }

    [Fact]
    public async Task Demo_state_matches_python_demo()
    {
        _cfg.HasAc = true;
        var d = new DemoHome(_cfg);
        var s = await d.State();
        Assert.True(s.Demo);
        Assert.Equal(9, s.Devices.Count);
        Assert.True(s.Ac!.Power);
        Assert.Equal(18, s.Ac.Temp);
        Assert.Equal("cool", s.Ac.Mode);
        Assert.Contains("Escritório", s.Rooms);
        s = await d.LightsOff("Sala");
        Assert.DoesNotContain(s.Devices, x => x.Room == "Sala" && x.Kind != DeviceKind.Plug && x.On);
        Assert.Contains(s.Devices, x => x.Id == "d3" && x.On); // the TV plug stays on
    }

    [Fact]
    public async Task Demo_without_ac_has_no_ac()
    {
        _cfg.HasAc = false;
        Assert.Null((await new DemoHome(_cfg).State()).Ac);
    }

    [Fact]
    public async Task Factory_reports_secret_error_and_demo()
    {
        _cfg.Demo = true;
        var (home, error) = await HmeEngineFactory.Create(_cfg, new Usage(_dir));
        Assert.IsType<DemoHome>(home);
        Assert.Null(error);
        _cfg.Demo = false;
        (home, error) = await HmeEngineFactory.Create(_cfg, new Usage(_dir));
        Assert.Null(home);
        Assert.Null(error); // not configured yet: the panel shows the setup
    }
}
