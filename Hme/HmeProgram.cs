using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using Shft.Core;

namespace Shft.Hme;

/// <summary>
/// hme.exe: hme. without shft., in the notification area. One engine per user: if shft. or another hme.exe already
/// has it, this one asks it to show its panel and leaves.
/// </summary>
public static class HmeProgram
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Log and the shared UI's settings live in hme.'s own folder, not in shft.'s.
        Environment.SetEnvironmentVariable("SHFT_DATA", Path.Combine(HmeConfig.DefaultFolder, "hme"));
        bool quiet = args.Contains("--bandeja");
        var cfg = HmeConfig.Load(HmeConfig.DefaultFolder);

        if (ProbeVersion(cfg.Port) is JsonValue v && v.TryGetValue<int>(out _))
        {
            // Controle Casa (Python) answers with a number; two apps on one account would double the cloud calls.
            if (!quiet)
                MessageBox.Show("O Controle Casa antigo está aberto. Feche-o pela bandeja (botão direito → Sair) e abra o hme. de novo: o hme. usa a mesma configuração.",
                    "hme.", MessageBoxButton.OK, MessageBoxImage.Information);
            return 1;
        }

        HmeApp? app = null;
        var host = EngineHost.TryAcquire(cfg, () => app?.Dispatcher.BeginInvoke(() => app.ShowPanel(atCursor: false)), () => app?.Dispatcher.BeginInvoke(() => app.Quit()));
        if (host == null)
        {
            bool shown = LocalApi.AskShow(cfg.Port).GetAwaiter().GetResult();
            if (!shown && !quiet)
                MessageBox.Show("O hme. já está aberto (na barra do shft. ou na bandeja).", "hme.", MessageBoxButton.OK, MessageBoxImage.Information);
            return 0;
        }

        if (Environment.GetEnvironmentVariable("HME_DATA") is not { Length: > 0 }) MigrateStart(); // a test run leaves Windows' start entries alone

        app = new HmeApp(host, showPanel: !quiet);
        return app.Run();
    }

    static void MigrateStart()
    {
        try
        {
            using var run = HmeStartup.OpenRun();
            if (run != null && Environment.ProcessPath is { } exe && HmeStartup.MigrateRunKey(run, exe))
                Log.Write("hme: Controle Casa's start entry now starts hme.exe");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Log.Write("hme: start entry not migrated: " + ex.Message);
        }
    }

    /// <summary>What answers /api/version on hme.'s port, or null when nothing does.</summary>
    static JsonNode? ProbeVersion(int port)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
            var text = http.GetStringAsync($"http://127.0.0.1:{port}/api/version").GetAwaiter().GetResult();
            return JsonNode.Parse(text)?["version"];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }
}
