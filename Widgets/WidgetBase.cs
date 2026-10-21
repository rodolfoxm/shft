using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using ClonaDock.Core;

namespace ClonaDock.Widgets;

/// <summary>
/// A live tile in the Dock. Content is laid out on a fixed design canvas (100 units tall)
/// inside a Viewbox, so it scales smoothly with the icon size and magnification.
/// </summary>
public abstract class WidgetBase : Viewbox
{
    public const double DesignHeight = 100;

    readonly DispatcherTimer _timer;
    protected readonly Border Card;
    protected readonly Grid Body;
    bool _dark = true;

    protected WidgetBase(DockItem item)
    {
        Item = item;
        Stretch = Stretch.Uniform;
        Body = new Grid { ClipToBounds = true };
        Card = new Border
        {
            Width = DesignHeight * WidthUnits,
            Height = DesignHeight,
            CornerRadius = new CornerRadius(22),
            Child = Body,
            BorderThickness = new Thickness(1),
        };
        Child = Card;
        TextElement.SetFontFamily(Card, new FontFamily("Segoe UI Variable Display, Segoe UI"));

        _timer = new DispatcherTimer { Interval = Interval };
        _timer.Tick += (_, _) => SafeRefresh();
        Loaded += (_, _) => { SafeRefresh(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    public DockItem Item { get; }

    /// <summary>Name shown in the hover label.</summary>
    public abstract string Title { get; }

    /// <summary>Width along the Dock, in icon sizes.</summary>
    public virtual double WidthUnits => Math.Clamp(SettingsStore.Current.TileWidth, 1.2, 3);

    protected virtual TimeSpan Interval => TimeSpan.FromSeconds(1);

    protected bool Dark => _dark;

    protected Brush Fore => Dark ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));

    protected Brush Muted => new SolidColorBrush(Dark ? Color.FromArgb(0xA8, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x99, 0, 0, 0));

    protected Brush Track => new SolidColorBrush(Dark ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1C, 0, 0, 0));

    public void ApplyTheme(bool dark)
    {
        _dark = dark;
        Card.Width = DesignHeight * WidthUnits;
        Card.Background = CardBackground(dark);
        Card.BorderBrush = new SolidColorBrush(dark ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x24, 0, 0, 0));
        Body.Children.Clear();
        Build();
        SafeRefresh();
    }

    protected virtual Brush CardBackground(bool dark) => dark
        ? new LinearGradientBrush(Color.FromRgb(0x34, 0x34, 0x3A), Color.FromRgb(0x1E, 0x1E, 0x23), 90)
        : new LinearGradientBrush(Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0xEE, 0xEE, 0xF2), 90);

    /// <summary>Creates the tile's elements; called again when the theme changes.</summary>
    protected abstract void Build();

    /// <summary>Updates the tile's values.</summary>
    protected abstract void Refresh();

    public virtual void OnClick(FrameworkElement anchor) { }

    public virtual void AddMenuItems(ContextMenu menu) { }

    public void Stop() => _timer.Stop();

    void SafeRefresh()
    {
        try { Refresh(); }
        catch (Exception ex) { Log.Write($"widget {GetType().Name}: {ex.Message}"); }
    }

    protected void Save() => SettingsStore.Save(notify: false);

    protected TextBlock Text(double size, FontWeight? weight = null, Brush? brush = null, HorizontalAlignment align = HorizontalAlignment.Center)
    {
        return new TextBlock
        {
            FontSize = size,
            FontWeight = weight ?? FontWeights.Normal,
            Foreground = brush ?? Fore,
            HorizontalAlignment = align,
            TextAlignment = align == HorizontalAlignment.Left ? TextAlignment.Left : align == HorizontalAlignment.Right ? TextAlignment.Right : TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
    }

    protected static StackPanel Stack(params UIElement[] children)
    {
        var s = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var c in children) s.Children.Add(c);
        return s;
    }

    protected static MenuItem MenuItem(string header, Action action, bool isChecked = false) =>
        Dock.DockController.Item(header, action, isChecked);
}
