using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Shft.Ui;

/// <summary>The Settings window's building blocks, so every page shares the same sizes, spacing and states.</summary>
public static class SettingsKit
{
    public static ResourceDictionary Theme { get; } = new() { Source = new Uri("pack://application:,,,/shft;component/Ui/SettingsTheme.xaml") };

    static Style Style(string key) => (Style)Theme[key];

    /// <summary>The two-bar symbol, drawn from the same geometry as branding/shft-symbol-*.svg.</summary>
    public static FrameworkElement Symbol(double size)
    {
        var bars = new Path
        {
            Data = Geometry.Parse("M20,74 L42,26 M58,74 L80,26"),
            StrokeThickness = 20,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        bars.SetResourceReference(Shape.StrokeProperty, "TextBrush");
        var canvas = new Canvas { Width = 100, Height = 100 };
        canvas.Children.Add(bars);
        return new Viewbox { Width = size, Height = size, Child = canvas, VerticalAlignment = VerticalAlignment.Center };
    }

    public static TextBlock Icon(string glyph, double size = 16)
    {
        var t = new TextBlock { Text = glyph, FontSize = size, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        t.SetResourceReference(TextBlock.FontFamilyProperty, "SettingsIconFont");
        return t;
    }

    public static TextBlock Muted(string text, double size = 12)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextSubtleBrush");
        return t;
    }

    public static StackPanel Page(string glyph, string title, string description)
    {
        var heading = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        heading.ColumnDefinitions.Add(new ColumnDefinition());
        var icon = Icon(glyph, 26);
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        var text = new StackPanel();
        var name = new TextBlock { Text = title, FontSize = 25, FontWeight = FontWeights.SemiBold };
        name.SetResourceReference(TextBlock.FontFamilyProperty, "SettingsDisplayFont");
        text.Children.Add(name);
        text.Children.Add(Muted(description, 13));
        Grid.SetColumn(text, 1);
        heading.Children.Add(icon);
        heading.Children.Add(text);
        var page = new StackPanel { MaxWidth = 760 };
        page.Children.Add(heading);
        return page;
    }

    public static TextBlock Section(string text) => new()
    {
        Text = text,
        FontSize = 15,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 16, 0, 8),
    };

    public static StackPanel Card(Panel page, string? header = null)
    {
        if (header != null) page.Children.Add(Section(header));
        var inner = new StackPanel();
        var card = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(16, 0, 16, 0), Child = inner };
        card.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        page.Children.Add(card);
        return inner;
    }

    /// <summary>One setting: icon, name, description, and its control aligned to the right.</summary>
    public static Grid Row(Panel card, string label, string? help, UIElement control, string? glyph = null)
    {
        var grid = new Grid { MinHeight = 56 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 120 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (glyph != null)
        {
            var icon = Icon(glyph, 16);
            icon.HorizontalAlignment = HorizontalAlignment.Left;
            grid.Children.Add(icon);
        }
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 8, 16, 8) };
        text.Children.Add(new TextBlock { Text = label, FontSize = 13.5, FontWeight = FontWeights.Medium, TextWrapping = TextWrapping.Wrap });
        if (help != null) text.Children.Add(Muted(help));
        Grid.SetColumn(text, glyph == null ? 0 : 1);
        Grid.SetColumnSpan(text, glyph == null ? 2 : 1);
        Grid.SetColumn(control, 2);
        if (control is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
        // Buttons space themselves to the right; the row's last one has to reach the edge.
        if (control is Button single) single.Margin = new Thickness(0);
        if (control is StackPanel { Children.Count: > 0 } group && group.Children[^1] is Button last) last.Margin = new Thickness(0);
        AutomationProperties.SetName(control, label);
        grid.Children.Add(text);
        grid.Children.Add(control);
        if (card.Children.Count > 0)
        {
            var line = new Border { Height = 1 };
            line.SetResourceReference(Border.BackgroundProperty, "DividerBrush");
            card.Children.Add(line);
        }
        card.Children.Add(grid);
        return grid;
    }

    /// <summary>Turns a row off and says why, in place of its description.</summary>
    public static void Unavailable(Grid row, string why)
    {
        foreach (UIElement child in row.Children)
        {
            if (child is StackPanel text && Grid.GetColumn(child) < 2)
            {
                while (text.Children.Count > 1) text.Children.RemoveAt(1);
                text.Children.Add(Muted(why));
                text.Children[0].Opacity = 0.55;
            }
            else if (Grid.GetColumn(child) == 2)
            {
                child.IsEnabled = false;
                child.Opacity = 0.45;
            }
        }
    }

    /// <summary>Shows a problem under a row's name.</summary>
    public static TextBlock Problem(Grid row)
    {
        var note = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        note.SetResourceReference(TextBlock.ForegroundProperty, "ErrorBrush");
        foreach (UIElement child in row.Children)
            if (child is StackPanel text && Grid.GetColumn(child) < 2) text.Children.Add(note);
        return note;
    }

    public static Button Button(string text, Action click)
    {
        var b = new Button { Content = text, Style = Style("SettingsButton"), Margin = new Thickness(0, 0, 8, 0) };
        b.Click += (_, _) => click();
        return b;
    }

    public static SwitchToggle Toggle(bool value, Action<bool> set)
    {
        var c = new SwitchToggle("", value) { FocusVisualStyle = Style("FocusRing") };
        // State events, not Click: assistive tools toggle without clicking.
        c.Checked += (_, _) => set(true);
        c.Unchecked += (_, _) => set(false);
        return c;
    }

    public static FrameworkElement Slider(double min, double max, double value, double step, Func<double, string> format, Action<double> set)
    {
        var label = new TextBlock { Text = format(value), FontSize = 12.5, FontWeight = FontWeights.Medium, Width = 56, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var slider = new CapsuleSlider("", thin: true) { Width = 176, VerticalAlignment = VerticalAlignment.Center, Value = (value - min) / (max - min), FocusVisualStyle = Style("FocusRing") };
        slider.ValueChanged += v =>
        {
            double snapped = Math.Round((min + v * (max - min)) / step) * step;
            label.Text = format(snapped);
            set(snapped);
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(slider);
        panel.Children.Add(label);
        return panel;
    }

    public static ComboBox Choice<T>(IEnumerable<(T Value, string Label)> options, T current, Action<T> set)
    {
        var box = new ComboBox { Width = 176 };
        foreach (var (value, label) in options)
        {
            var item = new ComboBoxItem { Content = label, Tag = value };
            box.Items.Add(item);
            if (Equals(value, current)) box.SelectedItem = item;
        }
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is ComboBoxItem { Tag: T v }) set(v);
        };
        return box;
    }

    public static TextBox Field(string value, Action<string> set, double width = 176)
    {
        var box = new TextBox { Text = value, Width = width };
        box.TextChanged += (_, _) => set(box.Text.Trim());
        return box;
    }

    /// <summary>Records a shortcut: focus the box and press the keys. Backspace clears it.</summary>
    public static TextBox HotkeyBox(string value, Action<string> set)
    {
        var box = new TextBox { Text = value, Width = 176, IsReadOnly = true, TextAlignment = TextAlignment.Center };
        box.PreviewKeyDown += (_, e) =>
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is Key.Tab or Key.Escape) return; // keyboard users must be able to leave the box
            e.Handled = true;
            if (key is Key.Back or Key.Delete)
            {
                box.Text = "";
                set("");
                return;
            }
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
            var mods = Keyboard.Modifiers;
            if (mods == ModifierKeys.None) return;
            var text = string.Join("+", new[]
            {
                mods.HasFlag(ModifierKeys.Control) ? "Ctrl" : null, mods.HasFlag(ModifierKeys.Alt) ? "Alt" : null,
                mods.HasFlag(ModifierKeys.Shift) ? "Shift" : null, mods.HasFlag(ModifierKeys.Windows) ? "Win" : null, key.ToString(),
            }.Where(p => p != null));
            box.Text = text;
            set(text);
        };
        return box;
    }
}
