using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using ClonaDock.Core;
using ClonaDock.Native;

namespace ClonaDock.Ui;

/// <summary>
/// A themed popup window that opens next to a Dock tile and closes when you click elsewhere.
/// Stacks, the launcher and widget panels are flyouts.
/// </summary>
public class FlyoutWindow : Window
{
    public static int OpenCount { get; private set; }

    protected readonly Border Frame;
    bool _closing;

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

        Frame = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12),
            Margin = new Thickness(14),
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270, Opacity = 0.35 },
        };
        Frame.SetResourceReference(Border.BackgroundProperty, "MenuBackBrush");
        Frame.SetResourceReference(Border.BorderBrushProperty, "MenuBorderBrush");
        SetResourceReference(ForegroundProperty, "MenuTextBrush");
        base.Content = Frame;

        Deactivated += (_, _) => SafeClose();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape) SafeClose();
        };
        Loaded += (_, _) => OpenCount++;
        Closed += (_, _) =>
        {
            OpenCount = Math.Max(0, OpenCount - 1);
            MemoryTrim.Soon(Dispatcher);
        };
    }

    protected UIElement Body
    {
        set => Frame.Child = value;
    }

    public void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        Dispatcher.BeginInvoke(Close);
    }

    /// <summary>Opens beside <paramref name="anchor"/>, on the side away from the Dock's screen edge.</summary>
    public void ShowNear(FrameworkElement anchor)
    {
        Opacity = 0;
        Show();
        UpdateLayout();
        PlaceNear(anchor);
        Opacity = 1;
        Activate();
    }

    void PlaceNear(FrameworkElement anchor)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (PresentationSource.FromVisual(anchor) == null) return;
        var a0 = anchor.PointToScreen(new Point(0, 0));
        var a1 = anchor.PointToScreen(new Point(anchor.ActualWidth, anchor.ActualHeight));
        var dpi = VisualTreeHelper.GetDpi(this);
        int w = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX);
        int h = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);
        var monitor = Monitors.All().FirstOrDefault(m =>
            a0.X >= m.Bounds.Left && a0.X < m.Bounds.Right && a0.Y >= m.Bounds.Top && a0.Y < m.Bounds.Bottom) ?? Monitors.Pick(-1);
        var work = monitor.Bounds;
        double cx = (a0.X + a1.X) / 2, cy = (a0.Y + a1.Y) / 2;
        int gap = (int)(4 * monitor.Scale);

        (double x, double y) = SettingsStore.Current.Edge switch
        {
            DockEdge.Bottom => (cx - w / 2.0, a0.Y - h - gap),
            DockEdge.Top => (cx - w / 2.0, a1.Y + gap),
            DockEdge.Left => (a1.X + gap, cy - h / 2.0),
            _ => (a0.X - w - gap, cy - h / 2.0),
        };
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - w));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - h));
        Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, (int)x, (int)y, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
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
        t.SetResourceReference(TextBlock.ForegroundProperty, "MenuSubtleBrush");
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
        b.SetResourceReference(ForegroundProperty, "MenuTextBrush");
        b.Click += (_, _) => click();
        return b;
    }

    static ControlTemplate? _flat;

    static ControlTemplate FlatTemplate()
    {
        if (_flat != null) return _flat;
        var border = new FrameworkElementFactory(typeof(Border), "Bd");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
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
        template.Seal();
        _flat = template;
        return template;
    }
}
