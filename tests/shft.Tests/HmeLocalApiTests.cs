using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Shft.Hme;
using Xunit;

namespace Shft.Tests;

public sealed class HmeLocalApiTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "hme-api-" + Guid.NewGuid().ToString("N"));
    readonly int _port;
    readonly EngineHost _host;
    readonly HttpClient _http = new();
    int _shown;

    public HmeLocalApiTests()
    {
        Directory.CreateDirectory(_dir);
        var free = new TcpListener(IPAddress.Loopback, 0);
        free.Start();
        _port = ((IPEndPoint)free.LocalEndpoint).Port;
        free.Stop();
        File.WriteAllText(Path.Combine(_dir, "config.json"), $$"""{"demo":true,"has_ac":true,"port":{{_port}}}""");
        _host = EngineHost.TryAcquire(HmeConfig.Load(_dir), () => _shown++, () => { }, "Local\\hme.tests." + Guid.NewGuid().ToString("N"))!;
        _host.Start().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _host.Dispose();
        _http.Dispose();
        Directory.Delete(_dir, true);
    }

    HttpRequestMessage Req(HttpMethod method, string path, string? body = null, bool header = true, string? host = null)
    {
        var m = new HttpRequestMessage(method, $"http://127.0.0.1:{_port}{path}");
        if (header) m.Headers.Add("X-Controle-Casa", "1");
        if (host != null) m.Headers.Host = host;
        if (body != null) m.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return m;
    }

    async Task<(HttpStatusCode Code, JsonNode? Json)> Send(HttpRequestMessage m)
    {
        using var r = await _http.SendAsync(m);
        var text = await r.Content.ReadAsStringAsync();
        JsonNode? json = null;
        try { json = JsonNode.Parse(text); } catch (System.Text.Json.JsonException) { }
        return (r.StatusCode, json);
    }

    [Fact]
    public async Task Rejects_foreign_host()
    {
        var (code, _) = await Send(Req(HttpMethod.Get, "/api/state", host: "evil.com"));
        Assert.Contains(code, new[] { HttpStatusCode.BadRequest, HttpStatusCode.Forbidden });
    }

    [Fact]
    public async Task Localhost_name_is_accepted()
    {
        var m = new HttpRequestMessage(HttpMethod.Get, $"http://localhost:{_port}/api/version");
        Assert.Equal(HttpStatusCode.OK, (await Send(m)).Code);
    }

    [Fact]
    public async Task Api_needs_header_except_version()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(Req(HttpMethod.Get, "/api/state", header: false))).Code);
        var (code, json) = await Send(Req(HttpMethod.Get, "/api/version", header: false));
        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Equal(HmeEngine.Version, (string)json!["version"]!);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(Req(HttpMethod.Post, "/api/lights-off", "{}", header: false))).Code);
    }

    [Fact]
    public async Task Post_needs_small_valid_body()
    {
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await Send(Req(HttpMethod.Post, "/api/ac", "{\"x\":\"" + new string('a', 70 * 1024) + "\"}"))).Code);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(Req(HttpMethod.Post, "/api/ac", "{nope"))).Code);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(Req(HttpMethod.Post, "/api/ac", "[1]"))).Code);
    }

    [Fact]
    public async Task State_ac_device_lights_off_round_trip()
    {
        var (code, s) = await Send(Req(HttpMethod.Post, "/api/ac", """{"power":false,"temp":22}"""));
        Assert.Equal(HttpStatusCode.OK, code);
        Assert.False((bool)s!["ac"]!["power"]!);
        Assert.Equal(22, (int)s["ac"]!["temp"]!);
        (_, s) = await Send(Req(HttpMethod.Get, "/api/state"));
        Assert.True((bool)s!["demo"]!);
        Assert.Equal(26, (int)s["room"]!["temp"]!);
        var light = s["devices"]!.AsArray().First(d => (string)d!["id"]! == "d2")!;
        Assert.Equal("light", (string)light["kind"]!);
        Assert.Equal(270, (int)light["light"]!["h"]!);
        Assert.True(light["light"]!["has_color"]!.GetValue<bool>());
        Assert.Equal("Sala", (string)light["room"]!);
        var multi = s["devices"]!.AsArray().First(d => (string)d!["id"]! == "d8")!;
        Assert.Equal("switch_2", (string)multi["channels"]![1]!["code"]!);

        (_, s) = await Send(Req(HttpMethod.Post, "/api/device/d8", """{"channel":"switch_2","on":true}"""));
        Assert.True((bool)s!["devices"]!.AsArray().First(d => (string)d!["id"]! == "d8")!["channels"]![1]!["on"]!);
        (_, s) = await Send(Req(HttpMethod.Post, "/api/device/d7", """{"color":{"h":120,"s":100}}"""));
        Assert.Equal("colour", (string)s!["devices"]!.AsArray().First(d => (string)d!["id"]! == "d7")!["light"]!["mode"]!);
        (_, s) = await Send(Req(HttpMethod.Post, "/api/lights-off", """{"room":"Sala"}"""));
        Assert.False((bool)s!["devices"]!.AsArray().First(d => (string)d!["id"]! == "d1")!["on"]!);
    }

    [Fact]
    public async Task Show_calls_show_action()
    {
        Assert.True(await LocalApi.AskShow(_port));
        Assert.Equal(1, _shown);
    }

    [Fact]
    public async Task AskShow_is_false_when_nobody_listens()
    {
        var free = new TcpListener(IPAddress.Loopback, 0);
        free.Start();
        int port = ((IPEndPoint)free.LocalEndpoint).Port;
        free.Stop();
        Assert.False(await LocalApi.AskShow(port));
    }

    [Fact]
    public void Second_acquire_fails()
    {
        var name = "Local\\hme.tests." + Guid.NewGuid().ToString("N");
        using var first = EngineHost.TryAcquire(HmeConfig.Load(_dir), () => { }, () => { }, name, startApi: false);
        Assert.NotNull(first);
        EngineHost? second = null;
        var t = new Thread(() => second = EngineHost.TryAcquire(HmeConfig.Load(_dir), () => { }, () => { }, name, startApi: false));
        t.Start();
        t.Join();
        Assert.Null(second);
    }

    [Fact]
    public async Task No_ac_answers_unavailable()
    {
        _host.Config.HasAc = false;
        var (_, s) = await Send(Req(HttpMethod.Get, "/api/state"));
        Assert.False((bool)s!["ac"]!["available"]!);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(Req(HttpMethod.Post, "/api/ac", """{"power":true}"""))).Code);
    }

    [Fact]
    public async Task Unknown_route_is_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Send(Req(HttpMethod.Get, "/api/nada"))).Code);
    }
}
