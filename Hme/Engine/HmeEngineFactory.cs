using Shft.Core;

namespace Shft.Hme;

/// <summary>core.py's make_backend and test_connection.</summary>
public static class HmeEngineFactory
{
    public static readonly (string Id, string Name)[] Regions =
    [
        ("us", "Américas (Oeste) — padrão no Brasil"), ("us-e", "Américas (Leste)"), ("eu", "Europa Central"),
        ("eu-w", "Europa Ocidental"), ("in", "Índia"), ("sg", "Singapura"), ("cn", "China"),
    ];

    /// <summary>The home for this config: demo, the real one, or none yet (setup). A secret or key problem comes back as the error.</summary>
    public static async Task<(IHome? Home, string? Error)> Create(HmeConfig cfg, Usage usage)
    {
        if (cfg.SecretError != null && !cfg.Demo) return (null, cfg.SecretError);
        if (cfg.Demo) return (new DemoHome(cfg), null);
        if (!cfg.Configured || cfg.AccessId.Length == 0 || cfg.AccessSecret.Length == 0) return (null, null);
        var cloud = new TuyaCloud(cfg.Region, cfg.AccessId, cfg.AccessSecret, usage);
        try
        {
            await cloud.Connect();
        }
        catch (HmeCloudException ex) when (!ex.Message.StartsWith("Sem conexão", StringComparison.Ordinal))
        {
            cloud.Dispose();
            return (null, ex.Message);
        }
        catch (HmeCloudException ex)
        {
            Log.Write("hme: offline at start, showing the saved list: " + ex.Message); // the token comes with the first request
        }
        return (new HmeEngine(cfg, cloud, usage, new SavedList(cfg.Folder), StartPositivo(cfg)), null);
    }

    static PositivoClient? StartPositivo(HmeConfig cfg)
    {
        var email = HmeConfig.Str(cfg.Positivo["email"]);
        var password = HmeConfig.Str(cfg.Positivo["password"]);
        if (email.Length == 0 || password.Length == 0) return null;
        try
        {
            string session;
            try { session = Secrets.Unprotect(HmeConfig.Str(cfg.Positivo["session"])); }
            catch (HmeSecretException) { session = ""; } // unreadable session: sign in again
            return new PositivoClient(email, Secrets.Unprotect(password), PositivoDeviceId(cfg), session, sid => SaveSession(cfg, sid));
        }
        catch (HmeSecretException ex)
        {
            Log.Write("hme: Positivo not started: " + ex.Message);
            return null;
        }
    }

    /// <summary>The same identity always, so Positivo does not warn of a new sign-in each time.</summary>
    public static string PositivoDeviceId(HmeConfig cfg)
    {
        if (HmeConfig.Str(cfg.Positivo["device_id"]) is { Length: > 0 } id) return id;
        id = PositivoClient.NewDeviceId();
        cfg.Positivo["device_id"] = id;
        TrySave(cfg);
        return id;
    }

    public static void SaveSession(HmeConfig cfg, string sid)
    {
        cfg.Positivo["session"] = Secrets.Protect(sid);
        TrySave(cfg);
    }

    static void TrySave(HmeConfig cfg)
    {
        try { cfg.Save(); }
        catch (Exception ex) when (ex is HmeSecretException or System.IO.IOException or UnauthorizedAccessException)
        {
            Log.Write("hme: config not saved: " + ex.Message);
        }
    }

    /// <summary>Tests the keys; with "auto" it tries every data center and prefers one that has devices.</summary>
    public static async Task<TestResult> TestConnection(string key, string secret, string region, Usage usage)
    {
        var order = region == "auto" ? Regions.Select(r => r.Id).ToList() : [region];
        string? firstOk = null;
        string lastError = "Não foi possível conectar.";
        foreach (var r in order)
        {
            using var cloud = new TuyaCloud(r, key, secret, usage);
            try
            {
                await cloud.Connect();
            }
            catch (HmeCloudException ex)
            {
                lastError = ex.Message;
                continue;
            }
            var res = await cloud.Request("/v1.0/iot-01/associated-users/devices", query: new Dictionary<string, string> { ["size"] = "100" });
            if (!TuyaCloud.Ok(res))
            {
                lastError = TuyaCloud.ErrorText(res);
                firstOk ??= r;
                continue;
            }
            int n = (res["result"]?["devices"] as System.Text.Json.Nodes.JsonArray)?.Count ?? 0;
            if (n > 0) return new TestResult(true, r, n, true, null);
            firstOk ??= r;
        }
        return firstOk != null ? new TestResult(true, firstOk, 0, false, null) : new TestResult(false, null, 0, false, lastError);
    }
}
