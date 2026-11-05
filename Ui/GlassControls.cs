using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Shft.Core;

namespace Shft.Ui;

/// <summary>Icon glyph codes from Segoe Fluent Icons, the one icon set shft. draws its own controls with.</summary>
public static class Glyph
{
    public const string Bluetooth = "", Wifi = "", Ethernet = "", Offline = "", Bell = "", Volume = "", Mute = "",
        Mic = "", MicOff = "", Sun = "", Moon = "", Settings = "", Power = "", Chevron = "", Back = "",
        Up = "", Down = "", Controls = "", Memory = "", Hdr = "", Eye = "", Search = "", Pin = "", Unpin = "",
        Play = "", Pause = "", Prev = "", Next = "", Lock = "", Restart = "", SignOut = "", Today = "",
        Check = "", Battery = "", Charging = "", Close = "", Apps = "";

    public static readonly FontFamily Font = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public static GlyphView Text(string code, double size = 15) => new(code, size)
    {
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
    };
}

/// <summary>The fat capsule slider of the Control Center and, when thin, of Settings. Mouse wheel and arrow keys work too.</summary>
public sealed class CapsuleSlider : Grid
{
    readonly Border _fill;
    readonly Border? _knob;
    readonly GlyphView? _icon;
    readonly bool _thin;
    double _value;
    bool _dragging;

    public event Action<double>? ValueChanged;

    public CapsuleSlider(string name, string? glyph = null, double height = 28, bool thin = false)
    {
        _thin = thin;
        Height = thin ? 18 : height;
        Focusable = true;
        Cursor = Cursors.Hand;
        Background = Brushes.Transparent;
        AutomationProperties.SetName(this, name);

        var track = new Border { CornerRadius = new CornerRadius(thin ? 3 : height / 2), Height = thin ? 6 : double.NaN, VerticalAlignment = thin ? VerticalAlignment.Center : VerticalAlignment.Stretch };
        track.SetResourceReference(Border.BackgroundProperty, "TileBrush");
        Children.Add(track);

        _fill = new Border
        {
            CornerRadius = new CornerRadius(thin ? 3 : height / 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Height = track.Height,
            VerticalAlignment = track.VerticalAlignment,
            MinWidth = thin ? 6 : height,
        };
        _fill.SetResourceReference(Border.BackgroundProperty, "TileOnBrush");
        Children.Add(_fill);

        if (thin)
        {
            _knob = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(7), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            _knob.SetResourceReference(Border.BackgroundProperty, "TextBrush");
            Children.Add(_knob);
        }

        if (glyph != null)
        {
            _icon = Glyph.Text(glyph, 13);
            _icon.HorizontalAlignment = HorizontalAlignment.Left;
            _icon.Margin = new Thickness((height - _icon.Width) / 2, 0, 0, 0);
            _icon.IsHitTestVisible = false;
            _icon.SetResourceReference(TextBlock.ForegroundProperty, "TileOnTextBrush");
            Children.Add(_icon);
        }

        SizeChanged += (_, _) => Layout(false);
        MouseEnter += (_, _) => track.SetResourceReference(Border.BackgroundProperty, "TileHoverBrush");
        MouseLeave += (_, _) => track.SetResourceReference(Border.BackgroundProperty, "TileBrush");
        IsEnabledChanged += (_, e) => Motion.Fade(this, (bool)e.NewValue ? 1 : 0.4);
    }

    public string? Icon { set { if (_icon != null && value != null) _icon.Text = value; } }

    public double Value
    {
        get => _value;
        set
        {
            if (_dragging || Math.Abs(value - _value) < 0.001) return; // the user's hand wins over system echoes
            _value = Math.Clamp(value, 0, 1);
            Layout(true);
        }
    }

    void Layout(bool animate)
    {
        double min = _thin ? 6 : Height;
        double w = Math.Max(min, min + (ActualWidth - min) * _value);
        if (_knob != null) _knob.Margin = new Thickness(Math.Max(0, (ActualWidth - 14) * _value), 0, 0, 0);
        if (animate && IsLoaded) _fill.BeginAnimation(WidthProperty, new DoubleAnimation(w, Motion.Time(140)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        else
        {
            _fill.BeginAnimation(WidthProperty, null);
            _fill.Width = w;
        }
    }

    void Set(double v)
    {
        v = Math.Clamp(v, 0, 1);
        if (Math.Abs(v - _value) < 0.0005) return;
        _value = v;
        Layout(false);
        ValueChanged?.Invoke(v);
    }

    double At(MouseEventArgs e)
    {
        double min = _thin ? 6 : Height;
        return (e.GetPosition(this).X - min / 2) / Math.Max(1, ActualWidth - min);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        _dragging = true;
        CaptureMouse();
        Set(At(e));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging) Set(At(e));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragging = false;
        ReleaseMouseCapture();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        Set(_value + Math.Sign(e.Delta) * 0.05);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Down) Set(_value - 0.05);
        else if (e.Key is Key.Right or Key.Up) Set(_value + 0.05);
        else return;
        e.Handled = true;
    }
}

/// <summary>A round icon switch with a caption: off, on, disabled and busy, each visibly different.</summary>
public sealed class ControlTile : Button
{
    readonly Border _circle = new() { CornerRadius = new CornerRadius(100) };
    readonly GlyphView _icon;
    readonly TextBlock _title, _detail;
    bool _on, _busy;

    public ControlTile(string glyph, string title, string? detail = null, bool wide = false, double circle = 38)
    {
        Template = Plain;
        Cursor = Cursors.Hand;
        Focusable = true;
        AutomationProperties.SetName(this, title);
        _circle.Width = _circle.Height = circle;
        _icon = Glyph.Text(glyph, circle * 0.42);
        _circle.Child = _icon;

        _title = new TextBlock { Text = title, FontSize = wide ? 13 : 11, FontWeight = wide ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis };
        _title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        _detail = new TextBlock { Text = detail ?? "", FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, Visibility = detail == null ? Visibility.Collapsed : Visibility.Visible };
        _detail.SetResourceReference(TextBlock.ForegroundProperty, "TextSubtleBrush");

        if (wide)
        {
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            text.Children.Add(_title);
            text.Children.Add(_detail);
            var row = new DockPanel();
            DockPanel.SetDock(_circle, System.Windows.Controls.Dock.Left);
            row.Children.Add(_circle);
            row.Children.Add(text);
            Content = row;
        }
        else
        {
            _title.TextAlignment = TextAlignment.Center;
            _title.Margin = new Thickness(0, 5, 0, 0);
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            _circle.HorizontalAlignment = HorizontalAlignment.Center;
            stack.Children.Add(_circle);
            stack.Children.Add(_title);
            Content = stack;
        }
        Paint();
        MouseEnter += (_, _) => Paint();
        MouseLeave += (_, _) => Paint();
        GotKeyboardFocus += (_, _) => Paint();
        LostKeyboardFocus += (_, _) => Paint();
        IsEnabledChanged += (_, e) => { Motion.Fade(this, (bool)e.NewValue ? 1 : 0.4); Paint(); };
        Motion.Press(_circle, 0.9);
    }

    static ControlTemplate Plain { get; } = BuildPlain();

    static ControlTemplate BuildPlain()
    {
        var t = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
        t.Seal();
        return t;
    }

    public bool IsOn { get => _on; set { _on = value; Paint(); AutomationProperties.SetItemStatus(this, value ? "ligado" : "desligado"); } }

    public string Detail
    {
        set
        {
            _detail.Text = value;
            _detail.Visibility = value.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    public string Icon { set => _icon.Text = value; }

    /// <summary>Waiting for Windows: a slow pulse, and the tile can't be pressed twice.</summary>
    public bool IsBusy
    {
        get => _busy;
        set
        {
            if (_busy == value) return;
            _busy = value;
            IsHitTestVisible = !value;
            _circle.BeginAnimation(OpacityProperty, value
                ? new DoubleAnimation(1, 0.45, new Duration(TimeSpan.FromMilliseconds(600))) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever }
                : null);
        }
    }

    void Paint()
    {
        bool hot = IsMouseOver || IsKeyboardFocused;
        _circle.SetResourceReference(Border.BackgroundProperty, _on ? "TileOnBrush" : hot ? "TileHoverBrush" : "TileBrush");
        _icon.SetResourceReference(TextBlock.ForegroundProperty, _on ? "TileOnTextBrush" : "TextBrush");
    }
}

/// <summary>An on/off switch for Settings.</summary>
public sealed class SwitchToggle : ToggleButton
{
    readonly Border _track = new() { Width = 38, Height = 22, CornerRadius = new CornerRadius(11) };
    readonly Border _knob = new() { Width = 16, Height = 16, CornerRadius = new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Left, Background = Brushes.White };
    readonly TranslateTransform _move = new(3, 0);

    public SwitchToggle(string name, bool value)
    {
        AutomationProperties.SetName(this, name);
        var t = new ControlTemplate(typeof(ToggleButton)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
        Template = t;
        Cursor = Cursors.Hand;
        _knob.RenderTransform = _move;
        _knob.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 3, ShadowDepth = 1, Opacity = 0.3 };
        var grid = new Grid();
        grid.Children.Add(_track);
        grid.Children.Add(_knob);
        Content = grid;
        IsChecked = value;
        Paint(false);
        Checked += (_, _) => Paint(true);
        Unchecked += (_, _) => Paint(true);
    }

    void Paint(bool animate)
    {
        bool on = IsChecked == true;
        _track.SetResourceReference(Border.BackgroundProperty, on ? "AccentBrush" : "TileHoverBrush");
        double x = on ? 19 : 3;
        if (animate) _move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, Motion.Time(220)) { EasingFunction = new SpringEase() });
        else _move.X = x;
    }
}

/// <summary>Month view: the panel behind the clock, and the Dock's calendar widget.</summary>
public sealed class CalendarPanel : FlyoutWindow
{
    readonly TextBlock _title = new() { FontWeight = FontWeights.SemiBold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
    readonly UniformGrid _days = new() { Columns = 7, Rows = 7 };
    DateTime _month = new(DateTime.Today.Year, DateTime.Today.Month, 1);

    public CalendarPanel()
    {
        Inner = new Thickness(14);
        var header = new DockPanel { Margin = new Thickness(4, 0, 0, 8) };
        var nav = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (g, name, go) in new (string, string, Action)[]
                 {
                     (Glyph.Back, "Mês anterior", () => Go(_month.AddMonths(-1))),
                     (Glyph.Today, "Hoje", () => Go(new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1))),
                     (Glyph.Chevron, "Próximo mês", () => Go(_month.AddMonths(1))),
                 })
        {
            var b = FlatButton(Glyph.Text(g, 12), go, 7);
            AutomationProperties.SetName(b, name);
            nav.Children.Add(b);
        }
        DockPanel.SetDock(nav, System.Windows.Controls.Dock.Right);
        header.Children.Add(nav);
        header.Children.Add(_title);
        var root = new StackPanel { Width = 280 };
        root.Children.Add(header);
        root.Children.Add(_days);
        Body = root;
        Go(_month);
    }

    void Go(DateTime month)
    {
        _month = month;
        _title.Text = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(month.ToString("MMMM yyyy", CultureInfo.CurrentCulture));
        _days.Children.Clear();
        var culture = CultureInfo.CurrentCulture.DateTimeFormat;
        for (int i = 0; i < 7; i++)
        {
            var label = culture.AbbreviatedDayNames[(i + 1) % 7].TrimEnd('.');
            var t = new TextBlock { Text = label, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 6) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "TextSubtleBrush");
            _days.Children.Add(t);
        }
        int lead = ((int)month.DayOfWeek + 6) % 7;
        var first = month.AddDays(-lead);
        for (int i = 0; i < 42; i++)
        {
            var day = first.AddDays(i);
            bool today = day == DateTime.Today, inMonth = day.Month == month.Month;
            var cell = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(10), HorizontalAlignment = HorizontalAlignment.Center };
            var text = new TextBlock { Text = day.Day.ToString(CultureInfo.CurrentCulture), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontWeight = today ? FontWeights.SemiBold : FontWeights.Normal };
            text.SetResourceReference(TextBlock.ForegroundProperty, today ? "TileOnTextBrush" : inMonth ? "TextBrush" : "TextSubtleBrush");
            if (!inMonth) text.Opacity = 0.55;
            if (today) cell.SetResourceReference(Border.BackgroundProperty, "TileOnBrush");
            cell.Child = text;
            _days.Children.Add(cell);
        }
    }
}
