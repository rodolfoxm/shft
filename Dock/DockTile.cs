using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Shft.Core;
using Shft.Native;

namespace Shft.Dock;

public enum EntryKind { Launcher, App, Folder, File, Spacer, Separator, Widget, Downloads, RecycleBin }

public enum Section { Leading, Running, Trailing }

/// <summary>One slot in the Dock and the element that draws it.</summary>
public sealed class DockTile : Decorator
{
    public DockTile(string key, EntryKind kind)
    {
        Key = key;
        Kind = kind;
        ClipToBounds = false;
        SnapsToDevicePixels = false;
        AddVisualChild(_badgeVisual);
    }

    // ---- notification badge, drawn above the icon

    readonly DrawingVisual _badgeVisual = new();
    int _badge;

    /// <summary>Unread count from the app's Windows badge: 0 none, <see cref="Native.BadgeWatcher.Dot"/> for a plain dot.</summary>
    public int Badge
    {
        get => _badge;
        set
        {
            if (_badge == value) return;
            _badge = value;
            DrawBadge();
        }
    }

    protected override int VisualChildrenCount => base.VisualChildrenCount + 1;

    protected override Visual GetVisualChild(int index) =>
        index < base.VisualChildrenCount ? base.GetVisualChild(index) : _badgeVisual;

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        PlaceBadge();
    }

    /// <summary>The badge is drawn once on a 100×100 design square and scaled with the icon.</summary>
    void DrawBadge()
    {
        using (var dc = _badgeVisual.RenderOpen())
        {
            if (_badge == 0) return;
            var red = new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30));
            var ring = new Pen(new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF)), 2.5);
            if (_badge < 0)
            {
                dc.DrawEllipse(red, ring, new Point(88, 12), 11, 11);
                return;
            }
            var text = new FormattedText(_badge > 99 ? "99+" : _badge.ToString(),
                System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                24, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            const double h = 34;
            double w = Math.Max(h, text.Width + 16);
            var pill = new Rect(100 - w + 6, -6, w, h);
            dc.DrawRoundedRectangle(red, ring, pill, h / 2, h / 2);
            dc.DrawText(text, new Point(pill.Left + (w - text.Width) / 2, pill.Top + (h - text.Height) / 2));
        }
        PlaceBadge();
    }

    void PlaceBadge()
    {
        double s = Math.Min(ActualWidth, ActualHeight) / 100;
        if (s <= 0) return;
        var t = new TransformGroup();
        t.Children.Add(new ScaleTransform(s, s));
        t.Children.Add(new TranslateTransform(ActualWidth - 100 * s, 0));
        _badgeVisual.Transform = t;
    }

    public string Key { get; }
    public EntryKind Kind { get; }
    public DockItem? Item { get; set; }
    public Section Section { get; set; }
    public string Title { get; set; } = "";
    public string? LaunchPath { get; set; }
    public string? MatchKey { get; set; }
    public List<AppWindow> Windows { get; set; } = new();

    /// <summary>Length along the Dock, in icon sizes.</summary>
    public double LengthUnits { get; set; } = 1;

    /// <summary>Spacers and separators do not magnify; they only keep their gap.</summary>
    public bool Magnifies => Kind is not (EntryKind.Separator);

    public bool IsDraggable => Item != null;

    // Animation state, driven by DockView.
    internal double Scale = 1;
    internal double TargetScale = 1;
    internal double BaseCenter;
    internal DateTime? BounceStart;
    internal Rect Slot;

    bool _running, _active;
    DockEdge _edge;
    Color _indicator = Colors.White;
    Brush _dotBrush = Brushes.White;
    Pen _separatorPen = new(Brushes.Gray, 1);

    public bool IsRunning
    {
        get => _running;
        set { if (_running != value) { _running = value; InvalidateVisual(); } }
    }

    public bool IsActive
    {
        get => _active;
        set { if (_active != value) { _active = value; InvalidateVisual(); } }
    }

    internal void SetLook(DockEdge edge, Color indicator)
    {
        _edge = edge;
        _indicator = indicator;
        // Built once per look: tiles re-render on every magnification frame.
        _dotBrush = new SolidColorBrush(indicator);
        _dotBrush.Freeze();
        _separatorPen = new Pen(new SolidColorBrush(Color.FromArgb(0x70, indicator.R, indicator.G, indicator.B)), 1);
        _separatorPen.Freeze();
        InvalidateVisual();
    }

    public void Bounce() => BounceStart = DateTime.UtcNow;

    /// <summary>Bounce height as a fraction of the icon size; zero when not bouncing.</summary>
    internal double BounceOffset()
    {
        if (BounceStart is not DateTime t0) return 0;
        double t = (DateTime.UtcNow - t0).TotalSeconds;
        const double duration = 1.5;
        if (t > duration)
        {
            BounceStart = null;
            return 0;
        }
        return Math.Abs(Math.Sin(t * Math.PI * 2.0)) * Math.Exp(-t * 1.4) * 0.5;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var w = ActualWidth;
        var h = ActualHeight;

        if (Kind == EntryKind.Separator)
        {
            var pen = _separatorPen;
            if (_edge is DockEdge.Bottom or DockEdge.Top)
                dc.DrawLine(pen, new Point(w / 2, h * 0.1), new Point(w / 2, h * 0.9));
            else
                dc.DrawLine(pen, new Point(w * 0.1, h / 2), new Point(w * 0.9, h / 2));
            return;
        }

        if (!_running) return;

        // The running dot sits between the icon and the screen edge.
        double r = Math.Max(1.6, Math.Min(w, h) * 0.045);
        if (_active) r *= 1.25;
        double gap = Math.Min(w, h) * 0.09 + r;
        var center = _edge switch
        {
            DockEdge.Bottom => new Point(w / 2, h + gap),
            DockEdge.Top => new Point(w / 2, -gap),
            DockEdge.Left => new Point(-gap, h / 2),
            _ => new Point(w + gap, h / 2),
        };
        dc.DrawEllipse(_dotBrush, null, center, r, r);
    }
}
