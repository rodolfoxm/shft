using System.Windows.Forms;
using Shft.Core;

namespace Shft.Hme;

/// <summary>
/// The house by the clock: a click opens the panel, the right button turns the AC on or off and the lights out.
/// A blue dot while the AC is on. It reads only what the panel last saw, so it never calls the cloud itself.
/// </summary>
public sealed class HmeTray : IDisposable
{
    readonly NotifyIcon _icon;
    readonly EngineHost _host;
    readonly HmeApp _app;
    readonly System.Drawing.Icon _plain, _dot;

    public HmeTray(EngineHost host, HmeApp app)
    {
        (_host, _app) = (host, app);
        _plain = LoadIcon();
        _dot = WithDot(_plain);
        _icon = new NotifyIcon { Text = "hme.", Icon = _plain, Visible = true, ContextMenuStrip = new ContextMenuStrip() };
        _icon.ContextMenuStrip.Opening += (_, _) => FillMenu();
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) _app.TogglePanel();
        };
        _host.Updated += OnState;
        _host.Changed += () => _app.Dispatcher.BeginInvoke(() => OnState(_host.Last));
    }

    void OnState(HomeState? s)
    {
        bool acOn = s?.Ac is { Power: true, Available: true } && _host.Config.HasAc;
        _icon.Icon = acOn ? _dot : _plain;
        _icon.Text = acOn ? $"hme. · ar ligado, {s!.Ac!.Temp}°" : "hme.";
    }

    void FillMenu()
    {
        var menu = _icon.ContextMenuStrip!;
        menu.Items.Clear();
        var home = _host.Home;
        if (home != null && _host.Config.HasAc && _host.Last?.Ac is { Available: true } ac)
            menu.Items.Add(ac.Power ? "Desligar o ar" : "Ligar o ar", null, async (_, _) => await Act(h => h.SetAc(new AcChange(Power: !ac.Power))));
        if (home != null)
        {
            menu.Items.Add("Apagar as luzes", null, async (_, _) => await Act(h => h.LightsOff()));
            menu.Items.Add(new ToolStripSeparator());
        }
        menu.Items.Add("Ajustes do hme.", null, (_, _) => HmeSettingsWindow.ShowFor(_host));
        bool startup = false;
        try
        {
            using var run = HmeStartup.OpenRun();
            startup = run != null && HmeStartup.IsOn(run);
        }
        catch (System.Security.SecurityException) { }
        var start = new ToolStripMenuItem("Iniciar com o Windows") { Checked = startup };
        start.Click += (_, _) =>
        {
            try
            {
                using var run = HmeStartup.OpenRun();
                if (run != null && Environment.ProcessPath is { } exe) HmeStartup.Set(run, !startup, exe);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
            {
                Log.Write("hme: start entry not changed: " + ex.Message);
            }
        };
        menu.Items.Add(start);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sair do hme.", null, (_, _) => _app.Quit());
    }

    async Task Act(Func<IHome, Task<HomeState>> action)
    {
        if (_host.Home is not { } home) return;
        try
        {
            _host.Report(await action(home));
        }
        catch (Exception ex) when (ex is HmeCloudException or PositivoException)
        {
            _icon.ShowBalloonTip(4000, "hme.", ex.Message, ToolTipIcon.Warning);
        }
    }

    static System.Drawing.Icon LoadIcon()
    {
        try
        {
            var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/hme.ico"))?.Stream;
            if (stream != null) return new System.Drawing.Icon(stream, 32, 32);
        }
        catch (Exception ex) when (ex is System.IO.IOException or ArgumentException)
        {
            Log.Write("hme: tray icon not loaded: " + ex.Message);
        }
        return System.Drawing.SystemIcons.Application;
    }

    /// <summary>The house with Controle Casa's blue dot in the corner.</summary>
    static System.Drawing.Icon WithDot(System.Drawing.Icon icon)
    {
        using var bmp = icon.ToBitmap();
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int d = bmp.Width * 3 / 8;
            using var brush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0x3B, 0x82, 0xF6));
            g.FillEllipse(brush, bmp.Width - d, bmp.Height - d, d - 1, d - 1);
        }
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _plain.Dispose();
        _dot.Dispose();
    }
}
