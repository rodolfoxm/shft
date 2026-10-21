using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClonaDock.Ui;

/// <summary>A small modal prompt for one line of text.</summary>
public static class InputDialog
{
    public static string? Ask(string title, string message, string initial = "")
    {
        var window = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true,
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 13,
            ShowInTaskbar = false,
        };
        var box = new TextBox { Text = initial, Width = 340, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 10, 0, 14) };
        var ok = new Button { Content = "OK", Width = 90, IsDefault = true, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(0, 4, 0, 4) };
        var cancel = new Button { Content = "Cancelar", Width = 90, IsCancel = true, Padding = new Thickness(0, 4, 0, 4) };
        string? result = null;
        ok.Click += (_, _) => { result = box.Text; window.DialogResult = true; };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 340 });
        root.Children.Add(box);
        root.Children.Add(buttons);
        window.Content = root;
        window.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return window.ShowDialog() == true ? result : null;
    }
}
