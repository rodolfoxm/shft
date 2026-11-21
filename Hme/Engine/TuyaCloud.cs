using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shft.Core;

namespace Shft.Hme;

public sealed class HmeCloudException(string message) : Exception(message);

/// <summary>A cloud the engine talks to; the fake in tests records what was asked.</summary>
public interface ICloud
{
    Task<JsonObject> Request(string path, string method = "GET", JsonNode? body = null, IReadOnlyDictionary<string, string>? query = null);
}

/// <summary>
/// Tuya's OpenAPI with the user's own project keys, signed the way tinytuya 1.20 does it (HMAC-SHA256 over key,
/// token, time and the request). Every call is counted against the monthly quota.
/// </summary>
public sealed class TuyaCloud : ICloud, IDisposable
{
    const int QuotaCode = 28841004; // the free plan ran out

    readonly HttpClient _http;
    readonly string _host, _key, _secret;
    readonly Usage _usage;
    readonly SemaphoreSlim _gate = new(1, 1);
    string? _token;

    public TuyaCloud(string region, string key, string secret, Usage usage, HttpMessageHandler? handler = null)
    {
        _host = HostFor(region);
        (_key, _secret, _usage) = (key, secret, usage);
        _http = handler == null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    public static string HostFor(string region) => region switch
    {
        "cn" => "openapi.tuyacn.com",
        "us-e" => "openapi-ueaz.tuyaus.com",
        "eu" => "openapi.tuyaeu.com",
        "eu-w" => "openapi-weaz.tuyaeu.com",
        "in" => "openapi.tuyain.com",
        "sg" => "openapi-sg.iotbing.com",
        _ => "openapi.tuyaus.com",
    };

    public static string Sign(string key, string secret, string? token, long t, string method, string bodyJson, string? contentType, string pathAndQuery)
    {
        var headers = contentType != null ? $"Content-type:{contentType}\n" : "";
        var payload = key + (token ?? "") + t.ToString(CultureInfo.InvariantCulture) + method + "\n" +
                      Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bodyJson))).ToLowerInvariant() + "\n" +
                      headers + "\n" + pathAndQuery;
        return Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload)));
    }

    /// <summary>Gets the access token; bad keys or region fail here with a readable message.</summary>
    public async Task Connect()
    {
        await _gate.WaitAsync();
        try { await FetchToken(); }
        finally { _gate.Release(); }
    }

    async Task FetchToken()
    {
        _token = null;
        var r = await Send("/v1.0/token?grant_type=1", "GET", "", null);
        if (!Ok(r)) throw new HmeCloudException(ErrorText(r));
        _token = HmeConfig.Str(r["result"]?["access_token"]);
    }

    public async Task<JsonObject> Request(string path, string method = "GET", JsonNode? body = null, IReadOnlyDictionary<string, string>? query = null)
    {
        if (_usage.QuotaOut && method == "GET")
            return new JsonObject { ["success"] = false, ["code"] = QuotaCode, ["msg"] = _usage.QuotaMessage.Length > 0 ? _usage.QuotaMessage : "cota esgotada" };
        _usage.Count();

        string signed = path, sent = path;
        if (query is { Count: > 0 })
        {
            var sorted = query.OrderBy(q => q.Key, StringComparer.Ordinal).ToList();
            signed += "?" + string.Join("&", sorted.Select(q => q.Key + "=" + q.Value));
            sent += "?" + string.Join("&", sorted.Select(q => Uri.EscapeDataString(q.Key) + "=" + Uri.EscapeDataString(q.Value)));
        }
        var json = body?.ToJsonString() ?? "";

        JsonObject r;
        await _gate.WaitAsync();
        try
        {
            if (_token == null) await FetchToken();
            r = await Send(sent, method, json, signed);
            if (r["__token_invalid"] != null)
            {
                await FetchToken();
                r = await Send(sent, method, json, signed);
            }
        }
        catch (HmeCloudException ex)
        {
            r = new JsonObject { ["success"] = false, ["msg"] = ex.Message };
        }
        finally
        {
            _gate.Release();
        }
        r.Remove("__token_invalid");

        if (CodeOf(r) == "28841004")
            _usage.MarkQuota(ErrorText(r));
        else if (r["success"] is not JsonValue s || !s.TryGetValue<bool>(out var ok) || ok)
            _usage.ClearQuota();
        if (!Ok(r)) Log.Write($"hme: {method} {path} -> {ErrorText(r)}");
        return r;
    }

    async Task<JsonObject> Send(string pathAndQuery, string method, string body, string? signedPath)
    {
        long t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bool post = method != "GET";
        using var msg = new HttpRequestMessage(new HttpMethod(method), $"https://{_host}{pathAndQuery}");
        if (post)
        {
            msg.Content = new StringContent(body, new UTF8Encoding(false), "application/json");
            msg.Content.Headers.ContentType!.CharSet = null;
            msg.Headers.TryAddWithoutValidation("Signature-Headers", "Content-type");
        }
        if (_token == null) msg.Headers.TryAddWithoutValidation("secret", _secret);
        else msg.Headers.TryAddWithoutValidation("access_token", _token);
        msg.Headers.TryAddWithoutValidation("client_id", _key);
        msg.Headers.TryAddWithoutValidation("sign", Sign(_key, _secret, _token, t, method, body, post ? "application/json" : null, signedPath ?? pathAndQuery));
        msg.Headers.TryAddWithoutValidation("t", t.ToString(CultureInfo.InvariantCulture));
        msg.Headers.TryAddWithoutValidation("sign_method", "HMAC-SHA256");
        msg.Headers.TryAddWithoutValidation("mode", "cors");
        string text;
        try
        {
            using var res = await _http.SendAsync(msg);
            text = await res.Content.ReadAsStringAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new JsonObject { ["success"] = false, ["msg"] = "Sem conexão com a nuvem da Tuya: " + ex.Message };
        }
        if (text.Contains("token invalid", StringComparison.Ordinal) && _token != null)
            return new JsonObject { ["__token_invalid"] = true };
        try
        {
            return JsonNode.Parse(text) as JsonObject ?? new JsonObject { ["success"] = false, ["msg"] = text };
        }
        catch (JsonException)
        {
            return new JsonObject { ["success"] = false, ["msg"] = text };
        }
    }

    public static bool Ok(JsonObject? r) =>
        r != null && r["success"] is JsonValue s && s.TryGetValue<bool>(out var ok) && ok &&
        !(r["result"] is JsonValue res && res.TryGetValue<bool>(out var b) && !b);

    static string CodeOf(JsonObject r)
    {
        var n = r["code"] ?? r["Errorcode"];
        return n == null ? "" : n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n.ToJsonString();
    }

    public static string ErrorText(JsonObject r)
    {
        var code = CodeOf(r);
        var msg = HmeConfig.Str(r["msg"]) is { Length: > 0 } m ? m : HmeConfig.Str(r["Error"]) is { Length: > 0 } e ? e : HmeConfig.Str(r["Payload"]);
        return code switch
        {
            "1004" => "Access Secret incorreto.",
            "1005" => "Access ID incorreto.",
            "2009" => "Projeto sem permissão — confira se a API 'IoT Core' está autorizada.",
            "1106" => "Sem permissão — confira se o app Smart Life está vinculado e se o serviço necessário está autorizado no projeto.",
            "28841101" => "Serviço não autorizado no projeto — adicione o 'IR Control Hub Open Service' em Service API.",
            "2008" => "Comando não suportado por este aparelho.",
            "2017" => "Esse aparelho não aceita o formato de comando padrão.",
            "28841004" => "A cota grátis de chamadas da Tuya deste mês acabou. Ela renova no próximo ciclo; veja Ajustes → Diagnóstico.",
            "28841105" or "28841002" => "O trial grátis do IoT Core expirou — renove em Cloud Services → IoT Core.",
            _ => $"{msg} (código {code})".Trim(),
        };
    }

    public static string AcHint(string msg)
    {
        var low = msg.ToLowerInvariant();
        if (low.Contains("permiss", StringComparison.Ordinal) || low.Contains("1106", StringComparison.Ordinal) ||
            low.Contains("28841101", StringComparison.Ordinal) || low.Contains("não autorizado", StringComparison.Ordinal))
            return msg + " → No projeto da Tuya: Service API → Go to Authorize → adicione 'IR Control Hub Open Service' e confira se o trial dele está ativo.";
        return msg;
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
