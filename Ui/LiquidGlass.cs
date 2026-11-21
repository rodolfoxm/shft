using System.Windows;
using System.Windows.Media;

namespace Shft.Ui;

/// <summary>The two glass materials of Apple's Liquid Glass: regular (frosted, readable) and clear (lets more through).</summary>
public enum GlassStyle { Regular, Clear }

/// <summary>
/// shft.'s Liquid Glass, for the surfaces Apple gives it (the Dock, panels that float over content, round controls):
/// a specular rim lit from the top left with a faint reflection on the far edge, a sheen on top, a little depth at the
/// bottom and, where the picture behind is known, a lens band at the edge. No colour fringe: Apple's glass has none.
/// Everything is frozen and built once per look, since the Dock redraws while it magnifies.
/// </summary>
public static class LiquidGlass
{
    static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    static Color White(byte a) => Color.FromArgb(a, 0xFF, 0xFF, 0xFF);

    static Color Black(byte a) => Color.FromArgb(a, 0, 0, 0);

    static LinearGradientBrush Gradient(Point from, Point to, params (Color Color, double Offset)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = from, EndPoint = to };
        foreach (var (c, o) in stops) brush.GradientStops.Add(new GradientStop(c, o));
        return Freeze(brush);
    }

    /// <summary>The body colour: clear keeps about a third of the fill, a tint mixes in by its own alpha.</summary>
    public static Color Fill(Color fill, GlassStyle style, Color? tint = null)
    {
        if (tint is { } t)
        {
            double k = t.A / 255.0;
            byte Mix(byte a, byte b) => (byte)Math.Round(a + (b - a) * k);
            fill = Color.FromArgb(fill.A, Mix(fill.R, t.R), Mix(fill.G, t.G), Mix(fill.B, t.B));
        }
        return style == GlassStyle.Clear ? Color.FromArgb((byte)(fill.A * 0.35), fill.R, fill.G, fill.B) : fill;
    }

    /// <summary>The specular edge: brightest at the top-left corner, almost gone across the middle, a soft return at the bottom right.</summary>
    public static LinearGradientBrush Rim(bool dark, double strength = 1)
    {
        byte A(double a) => (byte)Math.Clamp(Math.Round(a * strength), 0, 255);
        return Gradient(new Point(0, 0), new Point(1, 1),
            (White(A(dark ? 0x96 : 0xB8)), 0),
            (White(A(dark ? 0x2C : 0x44)), 0.3),
            (White(A(dark ? 0x0A : 0x14)), 0.6),
            (White(A(dark ? 0x40 : 0x60)), 1));
    }

    static readonly LinearGradientBrush RimDark = Rim(true), RimLight = Rim(false), HotDark = Rim(true, 1.5), HotLight = Rim(false, 1.3);

    /// <summary>The rim for a round control; brighter under the pointer, like glass catching more light.</summary>
    public static Brush ControlRim(bool dark, bool hot) => dark ? hot ? HotDark : RimDark : hot ? HotLight : RimLight;

    /// <summary>A band <paramref name="band"/> wide just inside the rounded edge, where the lens bends what is behind.</summary>
    public static Geometry Ring(Size size, double radius, double band)
    {
        var outer = new RectangleGeometry(new Rect(size), radius, radius);
        var innerRect = new Rect(band, band, Math.Max(0, size.Width - band * 2), Math.Max(0, size.Height - band * 2));
        var inner = new RectangleGeometry(innerRect, Math.Max(0, radius - band), Math.Max(0, radius - band));
        return Freeze(new CombinedGeometry(GeometryCombineMode.Exclude, outer, inner));
    }

    public sealed record Look(Brush Fill, Brush Sheen, Brush Shade, Pen Rim, Pen Inner);

    public static Look Build(Color fill, bool dark, GlassStyle style = GlassStyle.Regular, Color? tint = null)
    {
        var body = Freeze(new SolidColorBrush(Fill(fill, style, tint)));
        // Light catching the curved top, and a little shade where the glass is thickest at the bottom.
        var sheen = Gradient(new Point(0, 0), new Point(0, 1), (White(dark ? (byte)0x1C : (byte)0x26), 0), (White(0), 0.5));
        var shade = Gradient(new Point(0, 0), new Point(0, 1), (Black(0), 0.7), (Black(dark ? (byte)0x18 : (byte)0x0C), 1));
        double strength = style == GlassStyle.Clear ? 1.25 : 1;
        var rim = Freeze(new Pen(Rim(dark, strength), 1.1));
        var inner = Freeze(new Pen(Freeze(new SolidColorBrush(White(dark ? (byte)0x0E : (byte)0x18))), 1)); // the glass has thickness
        return new Look(body, sheen, style == GlassStyle.Clear ? Freeze(new SolidColorBrush(Colors.Transparent)) : shade, rim, inner);
    }

    public static void Draw(DrawingContext dc, double w, double h, double r, Look look)
    {
        dc.DrawRoundedRectangle(look.Fill, null, new Rect(0, 0, w, h), r, r);
        dc.DrawRoundedRectangle(look.Sheen, null, new Rect(1, 1, w - 2, h - 2), r - 1, r - 1);
        dc.DrawRoundedRectangle(look.Shade, null, new Rect(1, 1, w - 2, h - 2), r - 1, r - 1);
        dc.DrawRoundedRectangle(null, look.Inner, new Rect(1.6, 1.6, w - 3.2, h - 3.2), Math.Max(0, r - 1.5), Math.Max(0, r - 1.5));
        dc.DrawRoundedRectangle(null, look.Rim, new Rect(0.55, 0.55, w - 1.1, h - 1.1), r - 0.5, r - 0.5);
    }
}
