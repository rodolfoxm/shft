using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Shft.Core;
using Shft.Dock;
using Shft.Native;

namespace Shft.Ui;

/// <summary>Windows' notifications: read, dismiss one, clear all.</summary>
public sealed class NotificationsPanel : FlyoutWindow
{
    readonly StackPanel _list = new();

    public NotificationsPanel()
    {
        Inner = new Thickness(14);
        var header = new DockPanel { Margin = new Thickness(2, 0, 0, 8) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var quiet = FlatButton(Glyph.Text(Glyph.Moon, 13), () => { SafeClose(); DockController.Open("ms-settings:quietmomentshome"); }, 7);
        AutomationProperties.SetName(quiet, "Não perturbe");
        quiet.ToolTip = "Não perturbe";
        actions.Children.Add(quiet);
        actions.Children.Add(FlatButton(new TextBlock { Text = "Limpar tudo", FontSize = 12 }, () => _ = NotificationService.Instance.ClearAsync(), 8));
        DockPanel.SetDock(actions, System.Windows.Controls.Dock.Right);
        header.Children.Add(actions);
        header.Children.Add(new TextBlock { Text = "Notificações", FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });

        var root = new DockPanel { Width = 340 };
        DockPanel.SetDock(header, System.Windows.Controls.Dock.Top);
        root.Children.Add(header);
        var more = FlatButton(new TextBlock { Text = "Mais configurações de notificação", FontSize = 12 }, () => { SafeClose(); DockController.Open("ms-settings:notifications"); });
        DockPanel.SetDock(more, System.Windows.Controls.Dock.Bottom);
        root.Children.Add(more);
        root.Children.Add(new ScrollViewer { Content = _list, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Body = root;

        var service = NotificationService.Instance;
        service.Changed += Fill;
        Closed += (_, _) => service.Changed -= Fill;
        Fill();
        _ = service.RefreshAsync();
    }

    void Fill()
    {
        _list.Children.Clear();
        var service = NotificationService.Instance;
        if (service.Items.Count == 0)
        {
            var empty = Subtle(service.Available ? "Nenhuma notificação" : "O Windows não liberou o acesso às notificações.");
            empty.HorizontalAlignment = HorizontalAlignment.Center;
            empty.Margin = new Thickness(0, 46, 0, 46);
            _list.Children.Add(empty);
            return;
        }
        foreach (var n in service.Items)
        {
            var close = FlatButton(Glyph.Text(Glyph.Close, 9), () => _ = service.RemoveAsync(n.Id), 6);
            AutomationProperties.SetName(close, "Dispensar");
            DockPanel.SetDock(close, System.Windows.Controls.Dock.Right);
            close.VerticalAlignment = VerticalAlignment.Top;
            var text = new StackPanel();
            var head = Subtle($"{n.App} · {n.When.LocalDateTime:HH:mm}");
            head.FontSize = 11;
            text.Children.Add(head);
            if (n.Title.Length > 0) text.Children.Add(new TextBlock { Text = n.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            if (n.Body.Length > 0) text.Children.Add(new TextBlock { Text = n.Body, TextWrapping = TextWrapping.Wrap, MaxHeight = 56, TextTrimming = TextTrimming.CharacterEllipsis });
            var row = new DockPanel();
            row.Children.Add(close);
            row.Children.Add(text);
            var card = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 8, 6, 8), Margin = new Thickness(0, 0, 0, 6), Child = row };
            card.SetResourceReference(Border.BackgroundProperty, "TileBrush");
            _list.Children.Add(card);
        }
    }
}

/// <summary>Every tray icon, with a pin that keeps it on the bar. Left and right click go to the app as usual.</summary>
public sealed class TrayPanel : FlyoutWindow
{
    readonly StackPanel _list = new() { Width = 260 };

    public TrayPanel()
    {
        Inner = new Thickness(8);
        Body = new ScrollViewer { Content = _list, MaxHeight = 560, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        TrayHost.Instance.Changed += Fill;
        Closed += (_, _) => TrayHost.Instance.Changed -= Fill;
        Fill();
    }

    void Fill()
    {
        _list.Children.Clear();
        var pins = SettingsStore.Current.PinnedTray;
        var items = TrayHost.Instance.Items;
        if (items.Count == 0)
        {
            var empty = Subtle("Os ícones aparecem aqui conforme os apps os anunciam.");
            empty.Margin = new Thickness(10);
            _list.Children.Add(empty);
        }
        foreach (var e in items)
        {
            var row = new DockPanel { Margin = new Thickness(2, 0, 0, 0) };
            bool pinned = pins.Contains(e.PinKey);
            var pin = FlatButton(Glyph.Text(pinned ? Glyph.Unpin : Glyph.Pin, 12), () =>
            {
                if (pinned) pins.Remove(e.PinKey);
                else pins.Add(e.PinKey);
                SettingsStore.Save();
                Fill();
            }, 7);
            AutomationProperties.SetName(pin, pinned ? "Tirar da barra" : "Manter na barra");
            DockPanel.SetDock(pin, System.Windows.Controls.Dock.Right);
            row.Children.Add(pin);
            var name = new StackPanel { Orientation = Orientation.Horizontal };
            name.Children.Add(new Image { Source = e.Icon, Width = 18, Height = 18, Margin = new Thickness(0, 0, 12, 0) });
            name.Children.Add(new TextBlock { Text = e.Name, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 170, TextTrimming = TextTrimming.CharacterEllipsis });
            var open = FlatButton(name, () => { SafeClose(); TrayHost.Instance.Click(e, right: false); }, 8);
            open.MouseRightButtonUp += (_, ev) => { ev.Handled = true; SafeClose(); TrayHost.Instance.Click(e, right: true); };
            open.ToolTip = e.Tip.Length > 0 ? e.Tip : null;
            row.Children.Add(open);
            _list.Children.Add(row);
        }
    }
}

/// <summary>Square-crops a picture for the bar's logo.</summary>
public static class LogoPicker
{
    public static void Choose()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Escolha a imagem da logo", Filter = "Imagens|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.ico|Todos os arquivos|*.*" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var target = System.IO.Path.Combine(SettingsStore.DataFolder, $"logo-{DateTime.Now.Ticks}.png");
            Save(dlg.FileName, target);
            SettingsStore.Current.TopBarLogo = target;
            SettingsStore.Save();
        }
        catch (Exception ex)
        {
            Log.Write("logo not changed: " + ex.Message);
            MessageBox.Show("Não foi possível usar essa imagem.\n\n" + ex.Message, "shft.");
        }
    }

    internal static void Save(string source, string target, int size = 128)
    {
        var image = new System.Windows.Media.Imaging.BitmapImage();
        image.BeginInit();
        image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(source);
        image.EndInit();
        int side = Math.Min(image.PixelWidth, image.PixelHeight);
        var square = new System.Windows.Media.Imaging.CroppedBitmap(image, new Int32Rect((image.PixelWidth - side) / 2, (image.PixelHeight - side) / 2, side, side));
        double scale = (double)size / side;
        var scaled = new System.Windows.Media.Imaging.TransformedBitmap(square, new ScaleTransform(scale, scale));
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(scaled));
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        using var file = System.IO.File.Create(target);
        encoder.Save(file);
    }
}
