using System.Threading;
using Shft.Native;

namespace Shft;

/// <summary>
/// Entry point. Helper runs (the taskbar watchdog, <c>--quit</c>, a second launch) finish here
/// without starting WPF, so they cost a few MB instead of a full UI process.
/// </summary>
public static class Program
{
    public const string QuitEvent = "shft.Quit";
    public const string ShowSettingsEvent = "shft.ShowSettings";
    public const string ToggleAppsEvent = "shft.ToggleApps";
    public const string ToggleControlsEvent = "shft.ToggleControls";

    /// <summary>Held by the running Dock; a second launch only signals it.</summary>
    internal static Mutex? SingleInstance;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--watchdog" && int.TryParse(args[1], out int pid))
        {
            TaskbarControl.RunWatchdog(pid);
            return 0;
        }

        if (args.Contains("--purge")) return SystemActions.PurgeElevated();

        if (args.Contains("--batteries"))
        {
            // Diagnostic: what the bar would show, printed for scripts and bug reports.
            foreach (var h in Hid.All()) Console.WriteLine($"hid {h.Vid:X4}:{h.Pid:X4} mi={h.Interface} page={h.UsagePage:X4} in={h.InputLength} out={h.OutputLength} feat={h.FeatureLength}");
            foreach (var b in DeviceBatteries.Read()) Console.WriteLine($"{b.Name} ({b.Kind}): {(b.Percent is int p ? p + "%" : "desligado")}{(b.Charging ? " carregando" : "")}");
            return 0;
        }

        if (args.Contains("--notifications"))
        {
            // Diagnostic: each notification and where clicking it would go. Opens nothing.
            var svc = NotificationService.Instance;
            svc.StartAsync().GetAwaiter().GetResult();
            foreach (var n in svc.Items) Console.WriteLine($"{n.Id} {n.App}: {n.Title} -> {NotificationService.TargetOf(n.Id) ?? "nada"}");
            return 0;
        }

        // Signals to the running instance, handy for Stream Deck buttons and scripts.
        foreach (var (flag, name) in new[] { ("--quit", QuitEvent), ("--toggle-apps", ToggleAppsEvent), ("--toggle-controls", ToggleControlsEvent) })
        {
            if (!args.Contains(flag)) continue;
            using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, name);
            signal.Set();
            return 0;
        }

        SingleInstance = new Mutex(true, "shft.SingleInstance", out bool first);
        if (!first)
        {
            if (args.Contains("--startup")) return 0; // signed in again, or the task and the Run key both fired
            // Opening the app again while it runs brings up its settings.
            using var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSettingsEvent);
            show.Set();
            return 0;
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
