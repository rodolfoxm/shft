using System.Windows;
using System.Windows.Media;

namespace ClonaDock.Dock;

/// <summary>
/// The bar's own surface: a tinted fill plus either a plain hairline border or a
/// "Liquid Glass" rim (specular highlight, top sheen and a red/cyan fringe imitating chromatic aberration).
/// Brushes are built once per look, not per frame, since the bar re-renders while it magnifies.
/// </summary>
public sealed class GlassBar : FrameworkElement
{
    bool _liquid;
    double _radius = 18;
    Brush _fill = Freeze(new SolidColorBrush(Color.FromArgb(0x70, 0x1C, 0x1C, 0x20)));
    Pen _hair = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF))), 1));
    Brush? _sheen;
    Pen? _warm, _cool, _rim;

    static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    static Pen Pen(Brush b, double width) => Freeze(new Pen(b, width));

    static SolidColorBrush Solid(byte a, byte r, byte g, byte b) => Freeze(new SolidColorBrush(Color.FromArgb(a, r, g, b)));

    static LinearGradientBrush Vertical(params (Color Color, double Offset)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        foreach (var (c, o) in stops) brush.GradientStops.Add(new GradientStop(c, o));
        return Freeze(brush);
    }

    public void SetLook(Color fill, Color border, bool liquid, bool dark)
    {
        _fill = Freeze(new SolidColorBrush(fill));
        _hair = Pen(Freeze(new SolidColorBrush(border)), 1);
        _liquid = liquid;
        if (liquid)
        {
            // Soft sheen across the top half, as light catching curved glass.
            _sheen = Vertical((Color.FromArgb(dark ? (byte)0x13 : (byte)0x20, 0xFF, 0xFF, 0xFF), 0), (Color.FromArgb(0, 0xFF, 0xFF, 0xFF), 0.55));
            // Chromatic fringe: warm and cool copies of the rim, nudged apart, like a lens edge splitting light.
            _warm = Pen(Solid(0x38, 0xFF, 0x5A, 0x6E), 1.1);
            _cool = Pen(Solid(0x38, 0x4F, 0xD8, 0xFF), 1.1);
            // Specular rim: bright at the top edge, fading down the sides, a faint return at the bottom.
            _rim = Pen(Vertical(
                (Color.FromArgb(0x68, 0xFF, 0xFF, 0xFF), 0),
                (Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF), 0.45),
                (Color.FromArgb(0x0C, 0xFF, 0xFF, 0xFF), 0.75),
                (Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF), 1)), 1.2);
        }
        InvalidateVisual();
    }

    public double Radius
    {
        get => _radius;
        set { if (_radius != value) { _radius = value; InvalidateVisual(); } }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 2 || h <= 2) return;
        double r = Math.Min(_radius, h / 2);

        dc.DrawRoundedRectangle(_fill, null, new Rect(0, 0, w, h), r, r);

        if (!_liquid || _sheen == null)
        {
            dc.DrawRoundedRectangle(null, _hair, new Rect(0.5, 0.5, w - 1, h - 1), r, r);
            return;
        }

        const double shift = 0.6;
        dc.DrawRoundedRectangle(_sheen, null, new Rect(1, 1, w - 2, h - 2), r - 1, r - 1);
        dc.DrawRoundedRectangle(null, _warm, new Rect(1.2 - shift, 1.2 - shift * 0.6, w - 2.4, h - 2.4), r - 1, r - 1);
        dc.DrawRoundedRectangle(null, _cool, new Rect(1.2 + shift, 1.2 + shift * 0.6, w - 2.4, h - 2.4), r - 1, r - 1);
        dc.DrawRoundedRectangle(null, _rim, new Rect(0.6, 0.6, w - 1.2, h - 1.2), r - 0.5, r - 0.5);
    }
}
