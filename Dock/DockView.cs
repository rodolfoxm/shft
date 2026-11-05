using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Shft.Core;
using Shft.Native;

namespace Shft.Dock;

/// <summary>
/// Lays out the Dock: the bar, the tiles with their magnification, the hover label,
/// launch bounces, auto-hide sliding and drag-to-reorder.
/// </summary>
public sealed class DockView : Panel
{
    readonly GlassBar _bar;
    readonly Border _label;
    readonly TextBlock _labelText;
    readonly List<DockTile> _tiles = new();

    // Look and geometry (device-independent pixels).
    public DockEdge Edge { get; private set; } = DockEdge.Bottom;
    public double IconSize { get; private set; } = 48;
    public double Magnification { get; private set; } = 1.7;
    public double Gap { get; private set; } = 6;
    public bool ShowLabels { get; private set; } = true;
    double _corner = 18;

    public double Pad => IconSize * 0.14;
    double Spacing => IconSize * 0.10;
    public double BarThickness => IconSize + 2 * Pad;
    bool Horizontal => Edge is DockEdge.Bottom or DockEdge.Top;

    // Animation state.
    bool _animating;
    DateTime _lastFrame;
    bool _hovering;
    double? _anchor, _anchorTarget;
    DockTile? _hovered;
    double _hide, _hideTarget;

    // Drag state.
    DockTile? _pressed;
    Point _pressPoint, _cursor;
    bool _dragging, _removing;
    int _dropIndex;

    public event Action<DockTile, MouseButton>? TileClicked;
    public event Action<DockTile>? TileMenu;
    public event Action? BarMenu;
    public event Action<DockTile, int>? TileMoved;
    public event Action<DockTile>? TileRemoved;
    public event Action<Rect>? BarRectChanged;

    /// <summary>Length of the bar at rest (no magnification), so the window can size itself to it.</summary>
    public event Action<double>? ContentLengthChanged;
    double _contentLength;

    public Rect BarRect { get; private set; }
    public bool IsDragging => _dragging;
    public bool IsPointerOver => _hovering;
    public bool IsHidden => _hideTarget > 0.5;
    public IReadOnlyList<DockTile> Tiles => _tiles;

    public DockView()
    {
        _bar = new GlassBar { IsHitTestVisible = true };
        _labelText = new TextBlock { FontSize = 12.5, FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI") };
        _label = new Border
        {
            Child = _labelText,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(9, 3, 9, 4),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        Children.Add(_bar);
        Children.Add(_label);
        Background = null; // transparent pixels must stay click-through
    }

    public void Configure(AppSettings s, Theme theme)
    {
        Edge = s.Edge;
        IconSize = Math.Clamp(s.IconSize, 24, 128);
        Magnification = Math.Clamp(s.Magnification, 1, 2.5);
        Gap = Math.Clamp(s.EdgeGap, 0, 40);
        ShowLabels = s.ShowLabels;
        // With glass on Windows 11 the blur's corners are rounded by Windows at a fixed radius; match it.
        _corner = s.Glass && BackdropWindow.RoundedBySystem ? BackdropWindow.SystemCornerRadius : s.CornerRadius;

        _bar.SetLook(theme.BarFill, theme.BarBorder, s.LiquidGlass, theme.IsDark);
        _label.Background = Theme.Brush(theme.LabelBack);
        _labelText.Foreground = Theme.Brush(theme.LabelText);
        foreach (var t in _tiles) t.SetLook(Edge, theme.Indicator);
        _indicator = theme.Indicator;
        InvalidateMeasure();
        Wake();
    }

    Color _indicator = Colors.White;

    public void SetTiles(IReadOnlyList<DockTile> tiles)
    {
        Children.Clear();
        _tiles.Clear();
        Children.Add(_bar);
        foreach (var t in tiles)
        {
            // A reused tile may still be parented here from the previous list.
            if (VisualTreeHelper.GetParent(t) is Panel p && p != this) p.Children.Remove(t);
            _tiles.Add(t);
            Children.Add(t);
            t.SetLook(Edge, _indicator);
        }
        Children.Add(_label);
        if (_pressed != null && !_tiles.Contains(_pressed)) CancelDrag();
        InvalidateMeasure();
        Wake();
    }

    public void SetHidden(bool hidden)
    {
        _hideTarget = hidden ? 1 : 0;
        Wake();
    }

    /// <summary>Starts the frame loop; it stops by itself once everything has settled.</summary>
    public void Wake()
    {
        if (_animating) return;
        _animating = true;
        _lastFrame = DateTime.UtcNow;
        CompositionTarget.Rendering += OnFrame;
    }

    void OnFrame(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        double dt = Math.Clamp((now - _lastFrame).TotalSeconds, 0.001, 0.05);
        _lastFrame = now;

        bool wasHovering = _hovering;
        UpdateHover();

        bool busy = _hovering || _dragging;

        // The magnification wave glides after the pointer instead of jumping with it.
        if (_anchorTarget is double target)
        {
            if (_anchor is double current && _hovering && wasHovering)
            {
                double diff = target - current;
                _anchor = Math.Abs(diff) < 0.05 ? target : current + diff * (1 - Math.Exp(-dt * 15));
                if (Math.Abs(diff) >= 0.05) busy = true;
            }
            else _anchor = target;
        }

        // Icons ease in and out of their size (lower rate = softer motion).
        double k = 1 - Math.Exp(-dt * 8);
        foreach (var t in _tiles)
        {
            double diff = t.TargetScale - t.Scale;
            if (Math.Abs(diff) > 0.001) { t.Scale += diff * k; busy = true; }
            else t.Scale = t.TargetScale;
            if (t.BounceStart != null) busy = true;
        }

        double hk = 1 - Math.Exp(-dt * 11);
        double hd = _hideTarget - _hide;
        if (Math.Abs(hd) > 0.002) { _hide += hd * hk; busy = true; }
        else _hide = _hideTarget;

        InvalidateArrange();
        if (!busy)
        {
            CompositionTarget.Rendering -= OnFrame;
            _animating = false;
        }
    }

    double AxisOf(Point p) => Horizontal ? p.X : p.Y;

    double PerpOf(Point p) => Edge switch
    {
        DockEdge.Bottom => ActualHeight - p.Y,
        DockEdge.Top => p.Y,
        DockEdge.Left => p.X,
        _ => ActualWidth - p.X,
    };

    Rect ToRect(double a, double aLen, double p, double pLen) => Edge switch
    {
        DockEdge.Bottom => new Rect(a, ActualHeight - p - pLen, Math.Max(0, aLen), Math.Max(0, pLen)),
        DockEdge.Top => new Rect(a, p, Math.Max(0, aLen), Math.Max(0, pLen)),
        DockEdge.Left => new Rect(p, a, Math.Max(0, pLen), Math.Max(0, aLen)),
        _ => new Rect(ActualWidth - p - pLen, a, Math.Max(0, pLen), Math.Max(0, aLen)),
    };

    bool TryCursor(out Point p)
    {
        p = default;
        if (PresentationSource.FromVisual(this) == null || !Win32.GetCursorPos(out var pt)) return false;
        p = PointFromScreen(new Point(pt.X, pt.Y));
        return true;
    }

    void UpdateHover()
    {
        bool inside = false;
        if (_hideTarget < 0.5 && TryCursor(out var c))
        {
            double perp = PerpOf(c);
            double axis = AxisOf(c);
            double barNear = BarRect.IsEmpty ? 0 : PerpOf(Edge switch
            {
                DockEdge.Bottom => BarRect.BottomLeft,
                DockEdge.Top => BarRect.TopLeft,
                DockEdge.Left => BarRect.TopLeft,
                _ => BarRect.TopRight,
            });
            double barStart = Horizontal ? BarRect.Left : BarRect.Top;
            double barEnd = Horizontal ? BarRect.Right : BarRect.Bottom;
            double reach = _hovering ? BarThickness + IconSize * (Magnification - 1) + 6 : BarThickness;
            inside = !BarRect.IsEmpty && axis >= barStart && axis <= barEnd && perp >= barNear - 2 && perp <= barNear + reach;
            if (inside) _anchorTarget = axis;
        }

        _hovering = inside && !_dragging;
        _hovered = null;
        double radius = IconSize * 2.4;
        foreach (var t in _tiles)
        {
            double target = 1;
            if (_hovering && _anchor is double m && t.Magnifies && Magnification > 1.01)
            {
                double d = Math.Abs(m - t.BaseCenter) / radius;
                if (d < 1) target = 1 + (Magnification - 1) * (Math.Cos(Math.PI * d) + 1) / 2;
            }
            t.TargetScale = target;

            if (_hovering && _anchor is double a && t.Kind is not EntryKind.Separator)
            {
                double s0 = Horizontal ? t.Slot.Left : t.Slot.Top;
                double s1 = Horizontal ? t.Slot.Right : t.Slot.Bottom;
                if (a >= s0 && a <= s1) _hovered = t;
            }
        }
    }

    protected override Size MeasureOverride(Size available)
    {
        // Tiles are measured at their animated slot size in ArrangeOverride.
        _label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _bar.Measure(new Size(0, 0));
        return new Size(double.IsInfinity(available.Width) ? 0 : available.Width,
                        double.IsInfinity(available.Height) ? 0 : available.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double A = Horizontal ? finalSize.Width : finalSize.Height;
        double S = IconSize, pad = Pad, sp = Spacing, bt = BarThickness;

        var order = BuildOrder(out double placeholderLen);
        int n = order.Count;

        // Base (unmagnified) layout: centres drive the magnification curve.
        double total0 = 2 * pad + sp * Math.Max(0, n - 1);
        foreach (var t in order) total0 += t == null ? placeholderLen : t.LengthUnits * S;
        if (!_dragging && Math.Abs(total0 - _contentLength) >= 1)
        {
            _contentLength = total0;
            ContentLengthChanged?.Invoke(total0);
        }
        double start0 = (A - total0) / 2;
        double pos = start0 + pad;
        foreach (var t in order)
        {
            double len0 = t == null ? placeholderLen : t.LengthUnits * S;
            if (t != null) t.BaseCenter = pos + len0 / 2;
            pos += len0 + sp;
        }

        // Magnified layout.
        var lens = new double[n];
        double total = 2 * pad + sp * Math.Max(0, n - 1);
        for (int i = 0; i < n; i++)
        {
            var t = order[i];
            lens[i] = t == null ? placeholderLen : t.LengthUnits * S * (t.Magnifies ? t.Scale : 1);
            total += lens[i];
        }

        // Keep the point under the pointer still while icons grow around it.
        double start = (A - total) / 2;
        if (_anchor is double m && n > 0)
        {
            double u = m - start0, b = pad, d = pad, mapped = double.NaN;
            if (u <= pad) mapped = u;
            else
            {
                for (int i = 0; i < n && double.IsNaN(mapped); i++)
                {
                    var t = order[i];
                    double len0 = t == null ? placeholderLen : t.LengthUnits * S;
                    if (u <= b + len0) { mapped = d + (len0 > 0 ? (u - b) * lens[i] / len0 : 0); break; }
                    b += len0; d += lens[i];
                    if (i < n - 1)
                    {
                        if (u <= b + sp) { mapped = d + (u - b); break; }
                        b += sp; d += sp;
                    }
                }
                if (double.IsNaN(mapped)) mapped = d + (u - b);
            }
            start = m - mapped;
        }
        if (total <= A) start = Math.Clamp(start, 0, A - total);

        double hideShift = _hide * (Gap + bt + 8);
        double pBase = Gap - hideShift;

        pos = start + pad;
        for (int i = 0; i < n; i++)
        {
            var t = order[i];
            if (t != null)
            {
                Rect r;
                if (t.Kind == EntryKind.Separator)
                {
                    r = ToRect(pos, lens[i], pBase + pad * 0.5, bt - pad);
                }
                else
                {
                    double scale = t.Magnifies ? t.Scale : 1;
                    double thick = S * scale;
                    double lift = t.BounceOffset() * S;
                    r = ToRect(pos, lens[i], pBase + pad + lift, thick);
                }
                t.Slot = r;
                t.Opacity = 1;
                Panel.SetZIndex(t, 0);
                // Measured at the slot size: an element arranged smaller than it measured gets clipped.
                t.Measure(r.Size);
                t.Arrange(r);
            }
            pos += lens[i] + sp;
        }

        if (_dragging && _pressed != null)
        {
            double len = _pressed.LengthUnits * S;
            double axis = AxisOf(_cursor);
            double p0 = pBase + pad;
            double follow = Math.Max(p0, PerpOf(_cursor) - S / 2);
            var r = ToRect(axis - len / 2, len, follow, S);
            _pressed.Slot = r;
            _pressed.Opacity = _removing ? 0.45 : 0.9;
            Panel.SetZIndex(_pressed, 10);
            _pressed.Measure(r.Size);
            _pressed.Arrange(r);
        }

        var bar = n == 0 ? Rect.Empty : ToRect(start, total, pBase, bt);
        _bar.Radius = Math.Min(_corner, bt / 2);
        _bar.Visibility = n == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (!bar.IsEmpty) _bar.Arrange(bar);
        if (bar != BarRect)
        {
            BarRect = bar;
            BarRectChanged?.Invoke(bar);
        }

        ArrangeLabel();
        return finalSize;
    }

    List<DockTile?> BuildOrder(out double placeholderLen)
    {
        placeholderLen = 0;
        if (!_dragging || _pressed == null) return _tiles.Cast<DockTile?>().ToList();

        var others = _tiles.Where(t => t != _pressed).Cast<DockTile?>().ToList();
        var section = _tiles.Where(t => t != _pressed && t.IsDraggable && t.Section == _pressed.Section).ToList();
        double axis = AxisOf(_cursor);
        int k = section.Count(t => (Horizontal ? (t.Slot.Left + t.Slot.Right) : (t.Slot.Top + t.Slot.Bottom)) / 2 < axis);
        _dropIndex = k;

        int insertAt;
        if (section.Count == 0) insertAt = Math.Min(_tiles.IndexOf(_pressed), others.Count);
        else if (k == 0) insertAt = others.IndexOf(section[0]);
        else insertAt = others.IndexOf(section[k - 1]) + 1;

        others.Insert(insertAt, null);
        placeholderLen = _removing ? 0 : _pressed.LengthUnits * IconSize;
        return others;
    }

    void ArrangeLabel()
    {
        var t = _hovered;
        if (t == null || !ShowLabels || _dragging || _hide > 0.3 || string.IsNullOrEmpty(t.Title))
        {
            if (_dragging && _removing && _pressed != null)
            {
                _labelText.Text = "Remover";
                t = _pressed;
            }
            else
            {
                _label.Visibility = Visibility.Collapsed;
                return;
            }
        }
        else
        {
            _labelText.Text = t.Title;
        }

        _label.Visibility = Visibility.Visible;
        _label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _label.DesiredSize;
        var s = t.Slot;
        const double gap = 8;
        Point at = Edge switch
        {
            DockEdge.Bottom => new Point(s.Left + s.Width / 2 - size.Width / 2, s.Top - size.Height - gap),
            DockEdge.Top => new Point(s.Left + s.Width / 2 - size.Width / 2, s.Bottom + gap),
            DockEdge.Left => new Point(s.Right + gap, s.Top + s.Height / 2 - size.Height / 2),
            _ => new Point(s.Left - size.Width - gap, s.Top + s.Height / 2 - size.Height / 2),
        };
        at.X = Math.Clamp(at.X, 0, Math.Max(0, ActualWidth - size.Width));
        at.Y = Math.Clamp(at.Y, 0, Math.Max(0, ActualHeight - size.Height));
        _label.Arrange(new Rect(at, size));
    }

    DockTile? HitTile(Point p)
    {
        for (int i = _tiles.Count - 1; i >= 0; i--)
            if (_tiles[i].Slot.Contains(p)) return _tiles[i];
        return null;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        var p = e.GetPosition(this);
        if (e.ChangedButton == MouseButton.Left)
        {
            _pressed = HitTile(p);
            _pressPoint = p;
            _cursor = p;
            if (_pressed != null) CaptureMouse();
            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.Middle && HitTile(p) is DockTile t)
        {
            TileClicked?.Invoke(t, MouseButton.Middle);
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Wake();
        if (_pressed == null || e.LeftButton != MouseButtonState.Pressed) return;
        _cursor = e.GetPosition(this);
        if (!_dragging && _pressed.IsDraggable && (_cursor - _pressPoint).Length > 6)
            _dragging = true;
        if (_dragging)
            _removing = PerpOf(_cursor) > Gap + BarThickness + IconSize * 1.2;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        var p = e.GetPosition(this);
        if (e.ChangedButton == MouseButton.Left)
        {
            var pressed = _pressed;
            bool dragged = _dragging, removing = _removing;
            int index = _dropIndex;
            _pressed = null;
            _dragging = _removing = false;
            if (IsMouseCaptured) ReleaseMouseCapture();
            Wake();
            if (pressed == null) return;
            if (dragged)
            {
                if (removing) TileRemoved?.Invoke(pressed);
                else TileMoved?.Invoke(pressed, index);
            }
            else if (HitTile(p) == pressed)
            {
                TileClicked?.Invoke(pressed, MouseButton.Left);
            }
            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.Right)
        {
            if (HitTile(p) is DockTile t) TileMenu?.Invoke(t);
            else BarMenu?.Invoke();
            e.Handled = true;
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_dragging && Mouse.LeftButton != MouseButtonState.Pressed) CancelDrag();
    }

    void CancelDrag()
    {
        _pressed = null;
        _dragging = _removing = false;
        Wake();
    }
}
