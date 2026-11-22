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

public sealed class HmeReviewFixTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "hme-fix-" + Guid.NewGuid().ToString("N"));

    public HmeReviewFixTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    static AcState Ac(int temp = 24, bool power = true, string mode = "cool") =>
        new(power, mode, temp, "auto", false, true, "", HmeConfig.AllModes, 17, 30, "");

    [Fact] // review 1: two quick "+" from 24° end at 26° in one command
    public void Quick_ac_taps_merge_into_one_change()
    {
        var shown = Ac(24);
        AcChange? pending = null;
        foreach (var tap in new[] { new AcChange(Temp: shown.Temp + 1), new AcChange(Temp: 26) })
        {
            pending = AcChanges.Merge(pending, tap);
            shown = AcChanges.Apply(shown, tap);
        }
        Assert.Equal(26, shown.Temp);
        Assert.Equal(new AcChange(Temp: 26), pending);
        Assert.Equal(new AcChange(Power: false, Temp: 26), AcChanges.Merge(pending, new AcChange(Power: false)));
        Assert.Equal(30, AcChanges.Apply(Ac(30), new AcChange(Temp: 31)).Temp);
    }

    [Fact] // review 2 and 3: a configured account never waits on the network to build the engine
    public async Task Factory_builds_the_engine_without_connecting()
    {
        var cfg = HmeConfig.Load(_dir);
        (cfg.AccessId, cfg.AccessSecret, cfg.Configured, cfg.Region) = ("K", "S", true, "us");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var (home, error) = await HmeEngineFactory.Create(cfg, new Usage(_dir));
        Assert.IsType<HmeEngine>(home);
        Assert.Null(error);
        Assert.True(watch.ElapsedMilliseconds < 1000);
    }

    [Fact] // review 4: the Python Controle Casa answers /api/version with a number
    public async Task Old_controle_casa_is_recognised()
    {
        var free = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        free.Start();
        int port = ((System.Net.IPEndPoint)free.LocalEndpoint).Port;
        free.Stop();
        using var python = new System.Net.HttpListener();
        python.Prefixes.Add($"http://127.0.0.1:{port}/");
        python.Start();
        _ = Task.Run(async () =>
        {
            var ctx = await python.GetContextAsync();
            var bytes = System.Text.Encoding.UTF8.GetBytes("{\"version\": 15}");
            ctx.Response.OutputStream.Write(bytes);
            ctx.Response.Close();
        });
        Assert.True(await LocalApi.IsOldControleCasa(port));
        python.Stop();
        Assert.False(await LocalApi.IsOldControleCasa(port));
    }

    [Fact] // review 5: a malformed Positivo reply must not take the Tuya devices down with it
    public async Task Bad_positivo_reply_keeps_the_home_working()
    {
        var cfg = HmeConfig.Load(_dir);
        cfg.HasAc = false;
        var cloud = new FakeCloud
        {
            Answer = (_, p, _) => p == "/v1.0/iot-01/associated-users/devices"
                ? FakeCloud.Ok(new JsonObject { ["devices"] = new JsonArray(new JsonObject
                {
                    ["id"] = "a", ["name"] = "Luz", ["category"] = "kg", ["online"] = true,
                    ["status"] = new JsonArray(new JsonObject { ["code"] = "switch_1", ["value"] = true }),
                }), ["has_more"] = false })
                : FakeCloud.Ok(),
        };
        var http = new FakePositivo
        {
            Answer = a => a switch
            {
                "tuya.m.location.list" => """{"success":true,"result":[{"groupId":1}]}""",
                "tuya.m.my.group.device.list" => """{"success":true,"result":[{"devId":"p1","isOnline":true,"dps":{"1":true}}]}""",
                "tuya.m.device.get" => """{"success":true,"result":{"name":"X","devId":"p1","schema":"not json"}}""",
                _ => null,
            },
        };
        var e = new HmeEngine(cfg, cloud, new Usage(_dir), new SavedList(_dir), new PositivoClient("a@b.c", "pw", "DEV", "SID", null, http, _ => Task.CompletedTask));
        var s = await e.State();
        Assert.Equal("a", s.Devices.Single().Id);
        Assert.NotNull(s.Notice);
    }
}

public sealed class HmeAuditFixTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "hme-audit-" + Guid.NewGuid().ToString("N"));
    DateTime _now = new(2026, 11, 21, 10, 0, 0);

    public HmeAuditFixTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact] // audit 1: a refused connection takes ~2 s on Windows; nobody listening must answer at once
    public async Task Probe_is_fast_when_nothing_listens()
    {
        var free = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        free.Start();
        int port = ((System.Net.IPEndPoint)free.LocalEndpoint).Port;
        free.Stop();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(await LocalApi.IsOldControleCasa(port));
        Assert.False(await LocalApi.AskShow(port));
        Assert.True(watch.ElapsedMilliseconds < 500, $"took {watch.ElapsedMilliseconds} ms");
    }

    HmeConfig IrConfig()
    {
        var cfg = HmeConfig.Load(_dir);
        cfg.Ac["type"] = "ir";
        cfg.Ac["infrared_id"] = "H";
        cfg.Ac["remote_id"] = "R";
        cfg.Ac["method"] = "tecla por tecla (v2)";
        cfg.HasAc = true;
        return cfg;
    }

    static FakeCloud Cloud() => new()
    {
        Answer = (_, p, _) => p.EndsWith("/ac/status", StringComparison.Ordinal)
            ? FakeCloud.Ok(new JsonObject { ["power"] = "1", ["mode"] = 0, ["temp"] = 24, ["wind"] = 0 })
            : p == "/v1.0/iot-01/associated-users/devices" ? FakeCloud.Ok(new JsonObject { ["devices"] = new JsonArray(), ["has_more"] = false }) : FakeCloud.Ok(),
    };

    [Fact] // audit 2: the IR status follows the list's TTL instead of costing a call per read
    public async Task Ac_status_is_cached_like_the_list()
    {
        var cloud = Cloud();
        var e = new HmeEngine(IrConfig(), cloud, new Usage(_dir, () => _now), new SavedList(_dir), null, () => _now);
        e.MarkActivity();
        await e.State();
        await e.State();
        _now = _now.AddSeconds(20);
        await e.SetAc(new AcChange(Temp: 22));
        Assert.Equal(1, cloud.Calls.Count(c => c.Path.EndsWith("/ac/status", StringComparison.Ordinal)));
        var s = await e.State();
        Assert.Equal(22, s.Ac!.Temp); // what was sent, not a stale read
        _now = _now.AddSeconds(61);
        await e.State();
        Assert.Equal(2, cloud.Calls.Count(c => c.Path.EndsWith("/ac/status", StringComparison.Ordinal)));
    }

    [Fact] // audit 2: saving the settings must not cost a fresh list and the daily rooms again
    public async Task Reloaded_engine_keeps_the_cache()
    {
        var cloud = Cloud();
        var cfg = IrConfig();
        var first = new HmeEngine(cfg, cloud, new Usage(_dir, () => _now), new SavedList(_dir), null, () => _now);
        await first.State();
        int calls = cloud.Calls.Count;
        var second = new HmeEngine(cfg, cloud, new Usage(_dir, () => _now), new SavedList(_dir), null, () => _now);
        second.Adopt(first);
        await second.State();
        Assert.Equal(calls, cloud.Calls.Count);
    }

    [Fact] // audit 3: a config that cannot be read right now is never overwritten with defaults
    public void Locked_config_is_not_overwritten()
    {
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, """{"access_id":"KEEP"}""");
        HmeConfig cfg;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            cfg = HmeConfig.Load(_dir);
        Assert.True(cfg.ReadFailed);
        Assert.Throws<IOException>(() => cfg.Save());
        Assert.Contains("KEEP", File.ReadAllText(path));
    }

    [Fact] // audit 15: token requests count against the quota too
    public async Task Token_requests_are_counted()
    {
        var usage = new Usage(_dir);
        var http = new FakeHttp { Reply = r => """{"success":true,"result":{"access_token":"TOK"}}""" };
        using var cloud = new TuyaCloud("us", "K", "S", usage, http);
        await cloud.Connect();
        Assert.Equal(1, usage.Today);
    }
}
