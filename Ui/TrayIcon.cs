using System.Windows;
using System.Windows.Forms;

namespace Shft.Ui;

/// <summary>The icon by the clock: settings and quit, one click away.</summary>
public sealed class TrayIcon : IDisposable
{
    readonly NotifyIcon _icon;

    public TrayIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Configurações…", null, (_, _) => App.ShowSettings());
        menu.Items.Add("Adicionar um app…", null, (_, _) => App.ShowSettings("dock"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Encerrar shft.", null, (_, _) => System.Windows.Application.Current.Shutdown());

        _icon = new NotifyIcon
        {
            Text = "shft.",
            Icon = LoadIcon(),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) App.ShowSettings();
        };
    }

    static System.Drawing.Icon LoadIcon()
    {
        try
        {
            var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/shft.ico"))?.Stream;
            if (stream != null) return new System.Drawing.Icon(stream, 16, 16);
        }
        catch { }
        return System.Drawing.SystemIcons.Application;
    }

    public void Notify(string title, string text) => _icon.ShowBalloonTip(3000, title, text, ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
