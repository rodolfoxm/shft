using System.IO;
using System.Text.Json.Nodes;
using Shft.Hme;
using Xunit;

namespace Shft.Tests;

/// <summary>A cloud that answers by path and records every call.</summary>
sealed class FakeCloud : ICloud
{
    public readonly List<(string Method, string Path, string? Body)> Calls = new();
    public Func<string, string, JsonNode?, JsonObject> Answer = (_, _, _) => new JsonObject { ["success"] = true, ["result"] = true };

    public Task<JsonObject> Request(string path, string method = "GET", JsonNode? body = null, IReadOnlyDictionary<string, string>? query = null)
    {
        lock (Calls) Calls.Add((method, path, body?.ToJsonString()));
        return Task.FromResult(Answer(method, path, body));
    }

    public static JsonObject Ok(JsonNode? result = null) => new() { ["success"] = true, ["result"] = result ?? true };

    public static JsonObject Fail(string msg = "no", int code = 2008) => new() { ["success"] = false, ["code"] = code, ["msg"] = msg };
}

public sealed class HmeAirTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "hme-air-" + Guid.NewGuid().ToString("N"));
    readonly HmeConfig _cfg;
    readonly FakeCloud _cloud = new();
    readonly List<(string Id, List<JsonObject> Cmds)> _sent = new();
    IReadOnlyList<JsonObject> _devices = [];
    Dictionary<string, JsonNode?> _status = new();

    public HmeAirTests()
    {
        Directory.CreateDirectory(_dir);
        _cfg = HmeConfig.Load(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, true);

    AirConditioner Ir(string method = "")
    {
        _cfg.Ac["type"] = "ir";
        _cfg.Ac["infrared_id"] = "H";
        _cfg.Ac["remote_id"] = "R";
        _cfg.Ac["method"] = method;
        _cloud.Answer = (m, p, _) => p.EndsWith("/ac/status", StringComparison.Ordinal)
            ? FakeCloud.Ok(new JsonObject { ["power"] = "1", ["mode"] = 0, ["temp"] = 24, ["wind"] = 0 })
            : FakeCloud.Ok();
        return Make();
    }

    AirConditioner Make() => new(_cloud, _cfg, () => Task.FromResult(_devices),
        id => Task.FromResult<IReadOnlyDictionary<string, JsonNode?>>(_status),
        (id, cmds) => { _sent.Add((id, cmds)); return Task.CompletedTask; });

    List<(string Method, string Path, string? Body)> Posts => _cloud.Calls.Where(c => c.Method == "POST").ToList();

    [Fact]
    public async Task Ir_power_change_sends_only_power()
    {
        var ac = Ir("cena (v2)");
        await ac.Set(new AcChange(Power: false));
        var post = Assert.Single(Posts);
        Assert.Equal("/v2.0/infrareds/H/air-conditioners/R/scenes", post.Path);
        Assert.Contains("\"power\":0", post.Body);
    }

    [Fact]
    public async Task Ir_temp_change_on_single_key_method()
    {
        var ac = Ir("tecla por tecla (v2)");
        await ac.Set(new AcChange(Temp: 22));
        var post = Assert.Single(Posts);
        Assert.Equal("/v2.0/infrareds/H/air-conditioners/R/command", post.Path);
        Assert.Equal("""{"code":"temp","value":22}""", post.Body);
    }

    [Fact]
    public async Task Ir_first_success_is_remembered_and_saved()
    {
        var ac = Ir();
        _cloud.Answer = (m, p, _) => p.EndsWith("/scenes", StringComparison.Ordinal) && p.StartsWith("/v2.0", StringComparison.Ordinal)
            ? FakeCloud.Fail() : FakeCloud.Ok();
        await ac.Set(new AcChange(Power: true));
        Assert.Equal("tecla por tecla (v2)", HmeConfig.Str(_cfg.Ac["method"]));
        Assert.Contains("tecla por tecla (v2)", File.ReadAllText(Path.Combine(_dir, "config.json")));
    }

    [Fact]
    public async Task Ir_no_fallback_after_remembered_method_fails()
    {
        var ac = Ir("cena (v2)");
        _cloud.Answer = (m, _, _) => m == "POST" ? FakeCloud.Fail("recusado", 9) : FakeCloud.Ok(new JsonObject());
        var ex = await Assert.ThrowsAsync<HmeCloudException>(() => ac.Set(new AcChange(Power: true)));
        Assert.StartsWith("Ar-condicionado: ", ex.Message);
        Assert.Single(Posts);
    }

    [Fact]
    public async Task Turbo_sends_high_fan()
    {
        var ac = Ir("tecla por tecla (v2)");
        await ac.Set(new AcChange(Turbo: true));
        Assert.Equal("""{"code":"wind","value":3}""", Assert.Single(Posts).Body);
    }

    [Fact]
    public async Task Wifi_commands_use_code_maps()
    {
        _cfg.Ac["type"] = "wifi";
        _cfg.Ac["device_id"] = "W";
        _cfg.WifiAcCodes["temp_scale"] = 10;
        _status = new() { ["switch"] = true, ["temp_set"] = 240, ["mode"] = "cold", ["windspeed"] = "auto" };
        var ac = Make();
        await ac.Set(new AcChange(Mode: "heat", Temp: 23));
        var (id, cmds) = Assert.Single(_sent);
        Assert.Equal("W", id);
        Assert.Equal(new[] { "temp_set", "mode" }, cmds.Select(c => (string)c["code"]!));
        Assert.Equal(230, (int)cmds[0]["value"]!);
        Assert.Equal("hot", (string)cmds[1]["value"]!);
    }

    [Fact]
    public async Task Wifi_state_reads_scaled_temp_and_maps_back()
    {
        _cfg.Ac["type"] = "wifi";
        _cfg.Ac["device_id"] = "W";
        _cfg.WifiAcCodes["temp_scale"] = 10;
        _status = new() { ["switch"] = true, ["temp_set"] = 235, ["mode"] = "wet", ["windspeed"] = "middle" };
        var s = await Make().State();
        Assert.True(s.Power);
        Assert.Equal(24, s.Temp);
        Assert.Equal("dry", s.Mode);
        Assert.Equal("mid", s.Fan);
    }

    [Fact]
    public async Task Temp_is_clamped()
    {
        var ac = Ir("tecla por tecla (v2)");
        await ac.Set(new AcChange(Temp: 40));
        Assert.Equal("""{"code":"temp","value":30}""", Assert.Single(Posts).Body);
    }

    [Fact]
    public async Task Detect_prefers_wifi_kt_then_ir_hub_then_virtual()
    {
        JsonObject D(string id, string cat) => new() { ["id"] = id, ["category"] = cat, ["status"] = new JsonArray() };
        _cloud.Answer = (_, p, _) => p == "/v2.0/infrareds/HUB/remotes"
            ? FakeCloud.Ok(new JsonArray(new JsonObject { ["remote_id"] = "RR", ["category_id"] = 5 }))
            : FakeCloud.Ok(new JsonArray());

        var ac = Make();
        await ac.Detect([D("HUB", "wnykq"), D("K", "kt")]);
        Assert.Equal(("wifi", "K"), (ac.Type, ac.WifiId));

        ac = Make();
        await ac.Detect([D("HUB", "wnykq"), D("V", "infrared_ac")]);
        Assert.Equal(("ir", "HUB", "RR"), (ac.Type, ac.HubId, ac.RemoteId));

        ac = Make();
        await ac.Detect([D("V", "infrared_ac")]);
        Assert.Equal(("irdev", "V"), (ac.Type, ac.RemoteId));
    }

    [Fact]
    public async Task Room_temp_divides_by_ten_above_60()
    {
        _cfg.RoomSensorId = "S";
        _status = new() { ["va_temperature"] = 245 };
        Assert.Equal(24, await Make().RoomTemp());
        _status = new() { ["va_temperature"] = 23 };
        Assert.Equal(23, await Make().RoomTemp());
    }

    [Fact]
    public async Task Ir_status_failure_falls_back_to_last_sent()
    {
        var ac = Ir("tecla por tecla (v2)");
        _cloud.Answer = (m, p, _) => p.EndsWith("/ac/status", StringComparison.Ordinal) ? FakeCloud.Fail() : FakeCloud.Ok();
        await ac.Set(new AcChange(Temp: 21));
        var s = await ac.State();
        Assert.Equal(21, s.Temp);
        Assert.Equal(1, _cloud.Calls.Count(c => c.Path.EndsWith("/ac/status", StringComparison.Ordinal)));
    }
}
