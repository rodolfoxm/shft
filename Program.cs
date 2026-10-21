using System.Threading;
using ClonaDock.Native;

namespace ClonaDock;

/// <summary>
/// Entry point. Helper runs (the taskbar watchdog, <c>--quit</c>, a second launch) finish here
/// without starting WPF, so they cost a few MB instead of a full UI process.
/// </summary>
public static class Program
{
    public const string QuitEvent = "ClonaDock.Quit";
    public const string ShowSettingsEvent = "ClonaDock.ShowSettings";

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

        if (args.Contains("--quit"))
        {
            using var quit = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEvent);
            quit.Set();
            return 0;
        }

        SingleInstance = new Mutex(true, "ClonaDock.SingleInstance", out bool first);
        if (!first)
        {
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
