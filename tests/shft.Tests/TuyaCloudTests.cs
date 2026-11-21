using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Shft.Hme;
using Xunit;

namespace Shft.Tests;

/// <summary>Answers each request with the next queued body and keeps what was asked.</summary>
sealed class FakeHttp : HttpMessageHandler
{
    public readonly List<HttpRequestMessage> Requests = new();
    public readonly List<string?> Bodies = new();
    public Func<HttpRequestMessage, string> Reply = _ => """{"success":true,"result":{}}""";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        Bodies.Add(request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Reply(request), Encoding.UTF8, "application/json") };
    }
}

public sealed class TuyaCloudTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "hme-tuya-" + Guid.NewGuid().ToString("N"));

    public TuyaCloudTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    static string TokenOr(HttpRequestMessage r, string other) =>
        r.RequestUri!.AbsolutePath == "/v1.0/token" ? """{"success":true,"result":{"access_token":"TOK"}}""" : other;

    [Fact]
    public void Sign_token_request() =>
        Assert.Equal("D246DCE7BB311A8E7A32E0CF5CFCBB7BD0D4713E5F8D74416F030D0455964F0C",
            TuyaCloud.Sign("KEYKEYKEY", "SECRETSECRET", null, 1700000000123, "GET", "", null, "/v1.0/token?grant_type=1"));

    [Fact]
    public void Sign_get_with_sorted_query() =>
        Assert.Equal("77E26F1D4D5F5DFF0EDFDA0AC8D56A6F6146F07539693C94192143C910C61EAB",
            TuyaCloud.Sign("KEYKEYKEY", "SECRETSECRET", "TOKENABC", 1700000000123, "GET", "", null,
                "/v1.0/iot-01/associated-users/devices?last_row_key=k1&size=100"));

    [Fact]
    public void Sign_post_with_content_type() =>
        Assert.Equal("A0C1B153502B3270C4A737C8373AB8766FF17C9559C5346465FDCB0953B712BD",
            TuyaCloud.Sign("KEYKEYKEY", "SECRETSECRET", "TOKENABC", 1700000000123, "POST",
                """{"commands": [{"code": "switch_1", "value": true}]}""", "application/json", "/v1.0/iot-03/devices/dev1/commands"));

    [Theory]
    [InlineData("us", "openapi.tuyaus.com")]
    [InlineData("us-e", "openapi-ueaz.tuyaus.com")]
    [InlineData("eu", "openapi.tuyaeu.com")]
    [InlineData("eu-w", "openapi-weaz.tuyaeu.com")]
    [InlineData("in", "openapi.tuyain.com")]
    [InlineData("sg", "openapi-sg.iotbing.com")]
    [InlineData("cn", "openapi.tuyacn.com")]
    public void Hosts(string region, string host) => Assert.Equal(host, TuyaCloud.HostFor(region));

    [Fact]
    public async Task Requests_are_signed_with_token_and_sorted_query()
    {
        var http = new FakeHttp { Reply = r => TokenOr(r, """{"success":true,"result":{"devices":[]}}""") };
        var cloud = new TuyaCloud("us", "K", "S", new Usage(_dir), http);
        await cloud.Request("/v1.0/iot-01/associated-users/devices", query: new Dictionary<string, string> { ["size"] = "100", ["last_row_key"] = "a b" });
        var last = http.Requests[^1];
        Assert.Equal("TOK", last.Headers.GetValues("access_token").Single());
        Assert.Equal("HMAC-SHA256", last.Headers.GetValues("sign_method").Single());
        Assert.Equal("?last_row_key=a%20b&size=100", last.RequestUri!.Query);
        Assert.Equal(2, http.Requests.Count);
    }

    [Fact]
    public async Task Post_sends_the_signed_body_with_content_type()
    {
        var http = new FakeHttp { Reply = r => TokenOr(r, """{"success":true,"result":true}""") };
        var cloud = new TuyaCloud("us", "K", "S", new Usage(_dir), http);
        await cloud.Request("/v1.0/iot-03/devices/d/commands", "POST", new JsonObject { ["commands"] = new JsonArray() });
        var last = http.Requests[^1];
        Assert.Equal("Content-type", last.Headers.GetValues("Signature-Headers").Single());
        Assert.Equal("""{"commands":[]}""", http.Bodies[^1]);
        Assert.Equal("application/json", last.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Quota_code_pauses_gets_not_posts()
    {
        var usage = new Usage(_dir);
        var http = new FakeHttp { Reply = r => TokenOr(r, """{"success":false,"code":28841004,"msg":"quota"}""") };
        var cloud = new TuyaCloud("us", "K", "S", usage, http);
        await cloud.Request("/v1.0/x");
        Assert.True(usage.QuotaOut);
        int before = http.Requests.Count;
        var r = await cloud.Request("/v1.0/y");
        Assert.Equal(28841004, (int)r["code"]!);
        Assert.Equal(before, http.Requests.Count);
        await cloud.Request("/v1.0/z", "POST", new JsonObject());
        Assert.Equal(before + 1, http.Requests.Count);
    }

    [Fact]
    public async Task Token_invalid_renews_once()
    {
        int calls = 0;
        var http = new FakeHttp
        {
            Reply = r => r.RequestUri!.AbsolutePath == "/v1.0/token"
                ? """{"success":true,"result":{"access_token":"TOK"}}"""
                : ++calls == 1 ? """{"success":false,"msg":"token invalid"}""" : """{"success":true,"result":1}""",
        };
        var cloud = new TuyaCloud("us", "K", "S", new Usage(_dir), http);
        var res = await cloud.Request("/v1.0/x");
        Assert.True(TuyaCloud.Ok(res));
        Assert.Equal(2, http.Requests.Count(q => q.RequestUri!.AbsolutePath == "/v1.0/token"));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Bad_keys_raise_with_hint()
    {
        var http = new FakeHttp { Reply = _ => """{"success":false,"code":1004,"msg":"sign invalid"}""" };
        var cloud = new TuyaCloud("us", "K", "S", new Usage(_dir), http);
        var ex = await Assert.ThrowsAsync<HmeCloudException>(() => cloud.Connect());
        Assert.Equal("Access Secret incorreto.", ex.Message);
    }

    [Fact]
    public void Usage_rolls_day_and_month()
    {
        var now = new DateTime(2026, 11, 30, 10, 0, 0);
        var u = new Usage(_dir, () => now);
        u.Count();
        u.Count();
        now = new DateTime(2026, 12, 1, 9, 0, 0);
        u.Count();
        Assert.Equal(1, u.Today);
        Assert.Equal(1, u.ThisMonth);
        Assert.Contains("de ~26.000", u.Text());
    }

    [Fact]
    public void Usage_persists_on_flush()
    {
        var u = new Usage(_dir);
        u.Count();
        u.Flush();
        Assert.Equal(1, new Usage(_dir).ThisMonth);
    }

    [Fact]
    public void ErrorText_hints()
    {
        Assert.Equal("Access Secret incorreto.", TuyaCloud.ErrorText(new JsonObject { ["code"] = 1004 }));
        Assert.StartsWith("A cota grátis", TuyaCloud.ErrorText(new JsonObject { ["code"] = 28841004 }));
        Assert.Equal("boom (código 9)", TuyaCloud.ErrorText(new JsonObject { ["code"] = 9, ["msg"] = "boom" }));
        Assert.Contains("IR Control Hub Open Service", TuyaCloud.AcHint("sem permissão 1106"));
    }
}
