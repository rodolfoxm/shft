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
