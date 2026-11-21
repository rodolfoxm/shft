using System.Net;
using System.Net.Http;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;
using Shft.Hme;
using Xunit;

namespace Shft.Tests;

/// <summary>Answers Positivo's app API by its "a" (action) parameter.</summary>
sealed class FakePositivo : HttpMessageHandler
{
    public readonly List<string> Actions = new();
    public Func<string, string?> Answer = _ => """{"success":true,"result":{}}""";
    public int FailNetwork;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (FailNetwork-- > 0) throw new HttpRequestException("rede caiu");
        var action = HttpUtility.ParseQueryString(request.RequestUri!.Query)["a"]!;
        Actions.Add(action);
        var body = Answer(action) ?? """{"success":true,"result":{}}""";
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}

public sealed class HmePositivoTests
{
    static readonly Func<TimeSpan, Task> NoWait = _ => Task.CompletedTask;

    static string Login(string action) => action switch
    {
        "tuya.m.user.email.token.create" => """{"success":true,"result":{"publicKey":"3233","exponent":"17","token":"T"}}""",
        "tuya.m.user.email.password.login" => """{"success":true,"result":{"sid":"NEW"}}""",
        _ => null!,
    };

    [Fact]
    public void Post_hash() => Assert.Equal("8df46529bb6cb5c6a366f2d841caf652", PositivoClient.PostHash("{\"a\":1}"));

    [Fact]
    public void Sign_matches_the_app() =>
        Assert.Equal("54bdd3c458e72bd9c241c6b8f77eed34703cde2c0cb2220252367a66eabbae6e", PositivoClient.Sign(new Dictionary<string, string>
        {
            ["a"] = "tuya.m.location.list", ["v"] = "1.0", ["clientId"] = "qk4c93xsttfjsm4ktr94", ["deviceId"] = "DEV",
            ["time"] = "1700000000", ["requestId"] = "RID", ["sid"] = "SID", ["postData"] = "{\"a\":1}", ["lang"] = "pt",
            ["channel"] = "oem", ["et"] = "0.0.1",
        }));

    [Fact]
    public void Colour_v2()
    {
        var c = PositivoClient.ColourFromRaw("colour_data_v2", JsonValue.Create("00b403e803e8")!);
        Assert.Equal((180, 1000, 1000), ((int)c["h"]!, (int)c["s"]!, (int)c["v"]!));
        Assert.Equal("00b403e803e8", PositivoClient.ColourToRaw("colour_data_v2", c));
    }

    [Fact]
    public void Colour_v1()
    {
        Assert.Equal("0080000078ff80", PositivoClient.ColourToRaw("colour_data", new JsonObject { ["h"] = 120, ["s"] = 255, ["v"] = 128 }));
        var c = PositivoClient.ColourFromRaw("colour_data", JsonValue.Create("ff000000b4ffff")!);
        Assert.Equal((180, 255, 255), ((int)c["h"]!, (int)c["s"]!, (int)c["v"]!));
    }

    [Fact]
    public void Password_rsa() => Assert.Equal(new string('0', 64) + "04eb", PositivoClient.EncryptPassword("pw", 3233, 17));

    [Fact]
    public void Device_id_keeps_the_fixed_prefix()
    {
        var id = PositivoClient.NewDeviceId();
        Assert.StartsWith("b2f7827604d5585b9658eb86c0a079b3", id);
        Assert.Equal(44, id.Length);
    }

    [Fact]
    public async Task Session_expired_relogs_once()
    {
        string? saved = null;
        int lists = 0;
        var http = new FakePositivo
        {
            Answer = a => Login(a) ?? (a == "tuya.m.location.list"
                ? ++lists == 1 ? """{"success":false,"errorCode":"USER_SESSION_INVALID","errorMsg":"x"}""" : """{"success":true,"result":[]}"""
                : null),
        };
        var p = new PositivoClient("a@b.c", "pw", "DEV", "OLD", s => saved = s, http, NoWait);
        Assert.Empty(await p.Devices(force: true, ttlSeconds: 60));
        Assert.Equal("NEW", saved);
        Assert.Equal(2, lists);
        Assert.Single(http.Actions, "tuya.m.user.email.password.login");
    }

    [Fact]
    public async Task Three_tries_on_network_error()
    {
        var http = new FakePositivo { FailNetwork = 2, Answer = a => a == "tuya.m.location.list" ? """{"success":true,"result":[]}""" : null };
        var p = new PositivoClient("a@b.c", "pw", "DEV", "SID", null, http, NoWait);
        Assert.Empty(await p.Devices(true, 60));

        http.FailNetwork = 3;
        var ex = await Assert.ThrowsAsync<PositivoException>(() => p.Devices(true, 60));
        Assert.StartsWith("Sem conexão com a nuvem da Positivo", ex.Message);
    }

    [Fact]
    public async Task Wrong_password_is_explained()
    {
        var http = new FakePositivo
        {
            Answer = a => a == "tuya.m.user.email.password.login"
                ? """{"success":false,"errorCode":"USER_PASSWD_WRONG","errorMsg":"x"}""" : Login(a),
        };
        var p = new PositivoClient("a@b.c", "pw", "DEV", null, null, http, NoWait);
        var ex = await Assert.ThrowsAsync<PositivoException>(() => p.Login());
        Assert.Equal("E-mail ou senha da Positivo incorretos.", ex.Message);
    }

    static FakePositivo Home(string name, string schema, string dps)
    {
        var list = new JsonObject { ["success"] = true, ["result"] = new JsonArray(new JsonObject { ["devId"] = "d1", ["isOnline"] = true, ["dps"] = JsonNode.Parse(dps) }) };
        var info = new JsonObject { ["success"] = true, ["result"] = new JsonObject { ["name"] = name, ["productId"] = "zz", ["category"] = "", ["devId"] = "d1", ["schema"] = schema } };
        return new FakePositivo
        {
            Answer = a => a switch
            {
                "tuya.m.location.list" => """{"success":true,"result":[{"groupId":7}]}""",
                "tuya.m.my.group.device.list" => list.ToJsonString(),
                "tuya.m.device.get" => info.ToJsonString(),
                _ => null,
            },
        };
    }

    [Fact]
    public async Task Normalize_guesses_category_and_maps_codes()
    {
        var p = new PositivoClient("a@b.c", "pw", "DEV", "SID", null, Home("Lâmpada", """[{"id":1,"code":"switch_led"},{"id":2,"code":"bright_value_v2"}]""", """{"1":true,"2":500}"""), NoWait);
        var d = Assert.Single(await p.Devices(true, 60));
        Assert.Equal("pos:d1", (string)d["id"]!);
        Assert.Equal("dj", (string)d["category"]!);
        Assert.True((bool)p.Status("pos:d1")["switch_led"]!);

        p = new PositivoClient("a@b.c", "pw", "DEV", "SID", null, Home("Tomada da Sala", """[{"id":1,"code":"switch_1"}]""", """{"1":false}"""), NoWait);
        Assert.Equal("cz", (string)Assert.Single(await p.Devices(true, 60))["category"]!);
    }

    [Fact]
    public async Task Send_maps_codes_to_dps_and_updates_cache()
    {
        var http = Home("Lâmpada", """[{"id":1,"code":"switch_led"}]""", """{"1":true}""");
        var p = new PositivoClient("a@b.c", "pw", "DEV", "SID", null, http, NoWait);
        await p.Devices(true, 60);
        await p.Send("pos:d1", [new JsonObject { ["code"] = "switch_led", ["value"] = false }]);
        Assert.Contains("tuya.m.device.dp.publish", http.Actions);
        Assert.False((bool)p.Status("pos:d1")["switch_led"]!);
        await Assert.ThrowsAsync<PositivoException>(() => p.Send("pos:d1", [new JsonObject { ["code"] = "nada", ["value"] = 1 }]));
    }
}
