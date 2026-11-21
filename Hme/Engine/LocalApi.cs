using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shft.Core;

namespace Shft.Hme;

/// <summary>
/// Controle Casa's local API (shortcuts, Stream Deck), same routes, port and rules: only this PC, and /api/* only
/// with the X-Controle-Casa header, which a web page cannot send here.
/// </summary>
public static class LocalApi
{
    const string Header = "X-Controle-Casa";
    const int MaxBody = 64 * 1024;

    public static HttpListener? Start(int port, EngineHost host)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Prefixes.Add($"http://localhost:{port}/");
        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            Log.Write($"hme: port {port} busy, no local API: {ex.Message}");
            listener.Close();
            return null;
        }
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
                _ = Task.Run(() => Handle(ctx, host));
            }
        });
        return listener;
    }

    /// <summary>Asks whoever owns the engine to show its panel. False when nobody answers within a second.</summary>
    public static async Task<bool> AskShow(int port)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        using var msg = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/api/show");
        msg.Headers.Add(Header, "1");
        try
        {
            using var r = await http.SendAsync(msg);
            return r.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary>Controle Casa (Python) answers /api/version with a number; hme. answers with text.</summary>
    public static async Task<bool> IsOldControleCasa(int port)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        try
        {
            var v = JsonNode.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/api/version"))?["version"];
            return v is JsonValue n && n.TryGetValue<int>(out _);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return false;
        }
    }

    public const string OldAppMessage = "O Controle Casa antigo está aberto. Feche-o pela bandeja (botão direito → Sair) e abra o hme. de novo: ele usa a mesma configuração, e os dois juntos dobrariam as consultas à nuvem.";

    sealed class BadRequest(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }

    static async Task Handle(HttpListenerContext ctx, EngineHost host)
    {
        var req = ctx.Request;
        try
        {
            var hostName = (req.UserHostName ?? "").Split(':')[0].Trim('[', ']').ToLowerInvariant();
            if (hostName is not ("127.0.0.1" or "localhost")) { Reply(ctx, 403, Error("acesso negado")); return; }
            var path = req.Url!.AbsolutePath;
            bool fromApp = req.Headers[Header] == "1";
            if (req.HttpMethod == "GET")
            {
                if (path == "/api/version") { Reply(ctx, 200, new JsonObject { ["version"] = HmeEngine.Version }); return; }
                if (!fromApp) { Reply(ctx, 403, Error("acesso negado")); return; }
                await Get(ctx, host, path, req);
                return;
            }
            if (req.HttpMethod != "POST" || !fromApp) { Reply(ctx, 403, Error("acesso negado")); return; }
            await Post(ctx, host, path, await Body(req));
        }
        catch (BadRequest ex)
        {
            Reply(ctx, ex.Code, Error(ex.Message));
        }
        catch (Exception ex) // like core.py: any failure answers 500 instead of leaving the caller waiting
        {
            try { Reply(ctx, 500, Error(ex.Message)); }
            catch (Exception inner) when (inner is HttpListenerException or ObjectDisposedException or InvalidOperationException) { }
        }
    }

    static async Task<JsonObject> Body(HttpListenerRequest req)
    {
        if (req.ContentLength64 > MaxBody) throw new BadRequest(413, "Pedido grande demais.");
        var buffer = new byte[MaxBody + 1];
        int total = 0, read;
        while ((read = await req.InputStream.ReadAsync(buffer.AsMemory(total, buffer.Length - total))) > 0)
        {
            total += read;
            if (total > MaxBody) throw new BadRequest(413, "Pedido grande demais.");
        }
        if (total == 0) return new JsonObject();
        try
        {
            return JsonNode.Parse(Encoding.UTF8.GetString(buffer, 0, total)) as JsonObject ?? throw new BadRequest(400, "JSON inválido.");
        }
        catch (JsonException)
        {
            throw new BadRequest(400, "JSON inválido.");
        }
    }

    static JsonObject Error(string message) => new() { ["error"] = message };

    static void Reply(HttpListenerContext ctx, int code, JsonNode body)
    {
        var bytes = Encoding.UTF8.GetBytes(body.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        ctx.Response.StatusCode = code;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.Headers["Cache-Control"] = "no-store";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes);
        ctx.Response.Close();
    }

    static IHome Need(EngineHost host) => host.Home ?? throw new BadRequest(400, host.BootError ?? "Configure o hme. primeiro.");

    static async Task Get(HttpListenerContext ctx, EngineHost host, string path, HttpListenerRequest req)
    {
        switch (path)
        {
            case "/api/state":
                if (host.Home == null) { Reply(ctx, 200, new JsonObject { ["needs_setup"] = true, ["error"] = host.BootError }); return; }
                if (req.Headers["X-CC-Ativo"] == "1") host.Home.MarkActivity();
                Reply(ctx, 200, ToJson(await host.Home.State()));
                return;
            case "/api/diag":
                Reply(ctx, 200, new JsonObject { ["text"] = host.Home == null ? "Nenhuma conta conectada." : await host.Home.Diagnose() });
                return;
            case "/api/show":
                host.Show();
                Reply(ctx, 200, new JsonObject { ["ok"] = true });
                return;
            case "/api/debug":
                Reply(ctx, 200, new JsonObject { ["demo"] = host.Home is DemoHome, ["configured"] = host.Home != null, ["version"] = HmeEngine.Version });
                return;
            case "/api/quit":
                Reply(ctx, 405, Error("use POST"));
                return;
            default:
                Reply(ctx, 404, Error("not found"));
                return;
        }
    }

    static bool? B(JsonNode? n) => Devices.Bool(n);

    static int? I(JsonNode? n) => Devices.Number(n) is double d ? (int)Math.Round(d) : null;

    static async Task Post(HttpListenerContext ctx, EngineHost host, string path, JsonObject b)
    {
        if (path == "/api/quit")
        {
            Reply(ctx, 200, new JsonObject { ["ok"] = true });
            _ = Task.Delay(200).ContinueWith(_ => host.Quit(), TaskScheduler.Default);
            return;
        }
        if (path == "/api/ac/test")
        {
            Reply(ctx, 200, new JsonObject { ["text"] = host.Home == null ? "Nenhuma conta conectada." : await host.Home.TestAc() });
            return;
        }
        var home = Need(host);
        if (path == "/api/ac")
        {
            if (!host.Config.HasAc) throw new BadRequest(400, "Sem ar-condicionado nos ajustes do hme.");
            Reply(ctx, 200, ToJson(await home.SetAc(new AcChange(B(b["power"]), HmeConfig.Str(b["mode"]) is { Length: > 0 } m ? m : null,
                I(b["temp"]), HmeConfig.Str(b["fan"]) is { Length: > 0 } f ? f : null, B(b["turbo"])))));
            return;
        }
        if (path == "/api/lights-off")
        {
            Reply(ctx, 200, ToJson(await home.LightsOff(HmeConfig.Str(b["room"]) is { Length: > 0 } r ? r : null)));
            return;
        }
        if (path.StartsWith("/api/device/", StringComparison.Ordinal))
        {
            var id = Uri.UnescapeDataString(path["/api/device/".Length..]);
            (int, int)? color = b["color"] is JsonObject c ? (I(c["h"]) ?? 0, I(c["s"]) ?? 0) : null;
            int? brightness = I(b["brightness"]), temp = I(b["temp"]);
            var light = color != null || brightness != null || temp != null ? new LightChange(B(b["on"]), brightness, temp, color) : null;
            Reply(ctx, 200, ToJson(await home.SetDevice(id, light, B(b["on"]), HmeConfig.Str(b["channel"]) is { Length: > 0 } ch ? ch : null)));
            return;
        }
        Reply(ctx, 404, Error("not found"));
    }

    /// <summary>The same JSON Controle Casa answered with, so existing shortcuts keep working.</summary>
    public static JsonObject ToJson(HomeState s)
    {
        var devices = new JsonArray();
        foreach (var d in s.Devices)
        {
            var o = new JsonObject
            {
                ["id"] = d.Id, ["name"] = d.Name, ["original_name"] = d.OriginalName, ["online"] = d.Online, ["category"] = d.Category,
                ["kind"] = d.Kind.ToString().ToLowerInvariant(), ["on"] = d.On, ["room"] = d.Room,
            };
            if (d.Light is { } l)
                o["light"] = new JsonObject
                {
                    ["has_bright"] = l.HasBright, ["has_color"] = l.HasColor, ["has_temp"] = l.HasTemp, ["mode"] = l.Mode,
                    ["brightness"] = l.Brightness, ["temp"] = l.Temp, ["h"] = l.H, ["s"] = l.S,
                };
            if (d.Kind == DeviceKind.Multi)
                o["channels"] = new JsonArray(d.Channels.Select(c => (JsonNode)new JsonObject { ["code"] = c.Code, ["name"] = c.Name, ["on"] = c.On }).ToArray());
            devices.Add(o);
        }
        JsonObject ac = s.Ac is { } a
            ? new JsonObject
            {
                ["power"] = a.Power, ["mode"] = a.Mode, ["temp"] = a.Temp, ["fan"] = a.Fan, ["turbo"] = a.Turbo, ["available"] = a.Available,
                ["error"] = a.Error, ["modes"] = new JsonArray(a.Modes.Select(m => (JsonNode)JsonValue.Create(m)!).ToArray()),
                ["temp_min"] = a.TempMin, ["temp_max"] = a.TempMax, ["model"] = a.Model,
            }
            : new JsonObject { ["available"] = false };
        return new JsonObject
        {
            ["demo"] = s.Demo, ["room"] = new JsonObject { ["name"] = s.RoomName, ["temp"] = s.RoomTemp }, ["ac"] = ac, ["devices"] = devices,
            ["rooms"] = new JsonArray(s.Rooms.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()), ["notice"] = s.Notice,
        };
    }
}
