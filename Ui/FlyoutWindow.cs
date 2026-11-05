using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Shft.Core;
using Shft.Dock;
using Shft.Native;

namespace Shft.Ui;

/// <summary>
/// Every shft. panel: a rounded glass card that opens with the shared motion and closes when you click elsewhere.
/// The glass is a blurred snapshot of the screen under the panel, not a DWM backdrop: DWM only rounds its blur at
/// 8 px and can't fade or scale it, while a picture in the panel's own window can do both, at any radius.
/// </summary>
public class FlyoutWindow : Window
{
    public static int OpenCount { get; private set; }

    static WeakReference<FrameworkElement>? _lastAnchor;
    static DateTime _lastClosed;
    bool _counted;
    FrameworkElement? _anchor;

    protected readonly Border Frame;
    readonly Grid _layers;
    readonly Border _glass = new(); // a brush, not an Image: a stretched Image would grow a size-to-content window to the screen
    readonly GlassBar _surface = new() { IsHitTestVisible = false };
    readonly Border _content = new();
    bool _closing;
    Point _origin = new(0.5, 0);
    Color? _paper;

    public FlyoutWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 13;
        Left = -32000;
        Top = -32000;

        _layers = new Grid();
        _layers.Children.Add(_glass);
        _layers.Children.Add(_surface);
        _layers.Children.Add(_content);
        _layers.SizeChanged += (_, e) => _layers.Clip = new RectangleGeometry(new Rect(e.NewSize), Radius, Radius);

        Frame = new Border
        {
            Margin = new Thickness(18),
            Effect = new DropShadowEffect { BlurRadius = 26, ShadowDepth = 6, Direction = 270, Opacity = 0.32, RenderingBias = RenderingBias.Performance },
            Child = _layers,
        };
        Inner = new Thickness(12);
        Radius = 18;
        SetResourceReference(ForegroundProperty, "TextBrush");
        base.Content = Frame;

        Deactivated += (_, _) => SafeClose();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape) SafeClose();
        };
        Loaded += (_, _) =>
        {
            OpenCount++;
            _counted = true;
        };
        Closed += (_, _) =>
        {
            if (_counted) OpenCount = Math.Max(0, OpenCount - 1);
            if (_anchor != null)
            {
                _lastAnchor = new WeakReference<FrameworkElement>(_anchor);
                _lastClosed = DateTime.UtcNow;
            }
            _glass.Background = null;
            MemoryTrim.Soon(Dispatcher);
        };
    }

    protected double Radius { get => _surface.Radius; set => _surface.Radius = value; }

    protected Thickness Inner { set => _content.Padding = value; }

    /// <summary>An opaque card instead of glass, for the sticky note.</summary>
    protected Color Paper { set => _paper = value; }

    protected UIElement Body
    {
        set => _content.Child = value;
    }

    public void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        Motion.Close(Frame, () => Dispatcher.BeginInvoke(Close));
    }

    /// <summary>Opens beside <paramref name="anchor"/>; <paramref name="edge"/> is the screen edge it hangs from (the Dock's by default).</summary>
    public void ShowNear(FrameworkElement anchor, DockEdge? edge = null)
    {
        // A Dock tile clicked with its panel open: the press already closed it, so the click must not reopen it.
        if (edge == null && _lastAnchor?.TryGetTarget(out var last) == true && last == anchor && (DateTime.UtcNow - _lastClosed).TotalMilliseconds < 350)
        {
            Close();
            return;
        }
        _anchor = anchor;
        var side = edge ?? SettingsStore.Current.Edge;
        _origin = side switch
        {
            DockEdge.Bottom => new Point(0.5, 1),
            DockEdge.Left => new Point(0, 0.5),
            DockEdge.Right => new Point(1, 0.5),
            _ => new Point(0.5, 0),
        };
        Reveal(() => PlaceNear(anchor, side), side == DockEdge.Bottom ? 8 : -8);
    }

    /// <summary>Opens centred across a monitor, a bit above the middle, like a search box.</summary>
    public void ShowCentered(MonitorInfo monitor, double heightFraction = 0.2)
    {
        _origin = new Point(0.5, 0.2);
        Reveal(() =>
        {
            var size = PixelSize();
            var b = monitor.Bounds;
            Move(b.Left + (b.Width - size.W) / 2, b.Top + (int)(b.Height * heightFraction));
        }, -6);
    }

    void Reveal(Action place, double fromY)
    {
        Opacity = 0;
        Show();
        UpdateLayout();
        place();
        Dress();
        Frame.Opacity = 0;
        Opacity = 1;
        Motion.Open(Frame, _origin, fromY);
        Activate();
    }

    /// <summary>Takes the snapshot under the card while the window is still invisible, and sets its look.</summary>
    void Dress()
    {
        var s = SettingsStore.Current;
        var theme = Theme.Resolve(s);
        if (_paper is Color paper)
        {
            _surface.SetLook(paper, theme.TileRim, false, theme.IsDark);
            return;
        }
        _surface.SetLook(theme.PanelTint, theme.TileRim, s.PanelRim, theme.IsDark);
        var hwnd = new WindowInteropHelper(this).Handle;
        if (Win32.GetWindowRect(hwnd, out var w))
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            int mx = (int)(Frame.Margin.Left * dpi.DpiScaleX), my = (int)(Frame.Margin.Top * dpi.DpiScaleY);
            if (ScreenSampler.GlassOf(new RECT(w.Left + mx, w.Top + my, w.Right - mx, w.Bottom - my), s.PanelBlur) is { } picture)
            {
                var brush = new ImageBrush(picture) { Stretch = Stretch.Fill };
                RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
                _glass.Background = brush;
            }
        }
        if (_glass.Background == null)
        {
            // No picture (protected content, locked desktop): stay readable on a plain card.
            _surface.SetLook(Color.FromArgb(0xF2, theme.PanelTint.R, theme.PanelTint.G, theme.PanelTint.B), theme.TileRim, false, theme.IsDark);
        }
    }

    (int W, int H) PixelSize()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return ((int)Math.Ceiling(ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY));
    }

    void Move(double x, double y) =>
        Win32.SetWindowPos(new WindowInteropHelper(this).Handle, Win32.HWND_TOPMOST, (int)x, (int)y, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);

    void PlaceNear(FrameworkElement anchor, DockEdge edge)
    {
        if (PresentationSource.FromVisual(anchor) == null) return;
        var a0 = anchor.PointToScreen(new Point(0, 0));
        var a1 = anchor.PointToScreen(new Point(anchor.ActualWidth, anchor.ActualHeight));
        var (w, h) = PixelSize();
        var monitor = Monitors.All().FirstOrDefault(m =>
            a0.X >= m.Bounds.Left && a0.X < m.Bounds.Right && a0.Y >= m.Bounds.Top && a0.Y < m.Bounds.Bottom) ?? Monitors.Pick(-1);
        var b = monitor.Bounds;
        double cx = (a0.X + a1.X) / 2, cy = (a0.Y + a1.Y) / 2;
        double shadow = Frame.Margin.Left * monitor.Scale, gap = 6 * monitor.Scale;
        (double x, double y) = edge switch
        {
            DockEdge.Bottom => (cx - w / 2.0, a0.Y - h + shadow - gap),
            DockEdge.Top => (cx - w / 2.0, a1.Y - shadow + gap),
            DockEdge.Left => (a1.X - shadow + gap, cy - h / 2.0),
            _ => (a0.X - w + shadow - gap, cy - h / 2.0),
        };
        // The card, not its invisible shadow margin, stays inside the screen.
        double min = shadow - gap;
        x = Math.Clamp(x, b.Left - min, Math.Max(b.Left - min, b.Right - w + min));
        y = Math.Clamp(y, b.Top - min, Math.Max(b.Top - min, b.Bottom - h + min));
        Move(x, y);
    }

    protected static TextBlock Heading(string text) => new()
    {
        Text = text,
        FontSize = 15,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(2, 0, 2, 8),
    };

    protected static TextBlock Subtle(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextSubtleBrush");
        return t;
    }

    /// <summary>A flat button that matches the menus.</summary>
    public static Button FlatButton(object content, Action click, double padding = 8)
    {
        var b = new Button
        {
            Content = content,
            Padding = new Thickness(padding, padding * 0.6, padding, padding * 0.6),
            Cursor = System.Windows.Input.Cursors.Hand,
            Template = FlatTemplate(),
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        b.SetResourceReference(ForegroundProperty, "TextBrush");
        b.Click += (_, _) => click();
        Motion.Press(b, 0.97);
        return b;
    }

    static ControlTemplate? _flat;

    static ControlTemplate FlatTemplate()
    {
        if (_flat != null) return _flat;
        var border = new FrameworkElementFactory(typeof(Border), "Bd");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetBinding(ContentPresenter.HorizontalAlignmentProperty,
            new System.Windows.Data.Binding("HorizontalContentAlignment") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("MenuHoverBrush"), "Bd"));
        template.Triggers.Add(hover);
        var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("MenuHoverBrush"), "Bd"));
        template.Triggers.Add(focus);
        template.Seal();
        _flat = template;
        return template;
    }
}
