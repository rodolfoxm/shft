using System.Windows;
using System.Windows.Media;
using Shft.Ui;

namespace Shft.Dock;

/// <summary>
/// The surface of the Dock and of the panels: a tinted fill with a plain hairline border, or Liquid Glass
/// (see <see cref="LiquidGlass"/>). Brushes are built once per look, not per frame, since the Dock re-renders while it magnifies.
/// </summary>
public sealed class GlassBar : FrameworkElement
{
    double _radius = 18;
    Brush _fill = Freeze(new SolidColorBrush(Color.FromArgb(0x70, 0x1C, 0x1C, 0x20)));
    Pen _hair = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF))), 1));
    LiquidGlass.Look? _glass;

    static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    public void SetLook(Color fill, Color border, bool liquid, bool dark, GlassStyle style = GlassStyle.Regular)
    {
        _fill = Freeze(new SolidColorBrush(fill));
        _hair = Freeze(new Pen(Freeze(new SolidColorBrush(border)), 1));
        _glass = liquid ? LiquidGlass.Build(fill, dark, style) : null;
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
        if (_glass != null)
        {
            LiquidGlass.Draw(dc, w, h, r, _glass);
            return;
        }
        dc.DrawRoundedRectangle(_fill, null, new Rect(0, 0, w, h), r, r);
        dc.DrawRoundedRectangle(null, _hair, new Rect(0.5, 0.5, w - 1, h - 1), r, r);
    }
}
