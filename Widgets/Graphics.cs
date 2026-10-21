using System.Windows;
using System.Windows.Media;

namespace ClonaDock.Widgets;

/// <summary>A progress ring (0..1).</summary>
public sealed class Ring : FrameworkElement
{
    double _value;
    public double Value { get => _value; set { _value = Math.Clamp(value, 0, 1); InvalidateVisual(); } }
    public double Stroke { get; set; } = 8;
    public Brush Fill { get; set; } = Brushes.White;
    public Brush TrackBrush { get; set; } = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        double r = (size - Stroke) / 2;
        dc.DrawEllipse(null, new Pen(TrackBrush, Stroke), c, r, r);
        if (_value <= 0) return;

        var pen = new Pen(Fill, Stroke) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (_value >= 0.999)
        {
            dc.DrawEllipse(null, pen, c, r, r);
            return;
        }
        double angle = _value * 2 * Math.PI;
        var start = new Point(c.X, c.Y - r);
        var end = new Point(c.X + r * Math.Sin(angle), c.Y - r * Math.Cos(angle));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(r, r), 0, _value > 0.5, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }
}

/// <summary>A horizontal progress bar with rounded ends (0..1).</summary>
public sealed class Bar : FrameworkElement
{
    double _value;
    public double Value { get => _value; set { _value = Math.Clamp(value, 0, 1); InvalidateVisual(); } }
    public Brush Fill { get; set; } = Brushes.White;
    public Brush TrackBrush { get; set; } = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));

    protected override void OnRender(DrawingContext dc)
    {
        double h = ActualHeight, w = ActualWidth;
        if (w <= 0 || h <= 0) return;
        dc.DrawRoundedRectangle(TrackBrush, null, new Rect(0, 0, w, h), h / 2, h / 2);
        if (_value > 0) dc.DrawRoundedRectangle(Fill, null, new Rect(0, 0, Math.Max(h, w * _value), h), h / 2, h / 2);
    }
}

/// <summary>A small line chart of recent values.</summary>
public sealed class Sparkline : FrameworkElement
{
    public List<double> Values { get; } = new();
    public Brush Line { get; set; } = Brushes.White;
    public Brush Area { get; set; } = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));

    public void Push(double v, int max = 40)
    {
        Values.Add(v);
        while (Values.Count > max) Values.RemoveAt(0);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (Values.Count < 2 || w <= 0 || h <= 0) return;
        double top = Math.Max(Values.Max(), 1);
        double step = w / (Values.Count - 1);
        var pts = Values.Select((v, i) => new Point(i * step, h - v / top * (h - 2) - 1)).ToList();

        var area = new StreamGeometry();
        using (var ctx = area.Open())
        {
            ctx.BeginFigure(new Point(0, h), true, true);
            foreach (var p in pts) ctx.LineTo(p, false, false);
            ctx.LineTo(new Point(w, h), false, false);
        }
        area.Freeze();
        dc.DrawGeometry(Area, null, area);

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(pts[0], false, false);
            foreach (var p in pts.Skip(1)) ctx.LineTo(p, true, true);
        }
        line.Freeze();
        dc.DrawGeometry(null, new Pen(Line, 2) { LineJoin = PenLineJoin.Round }, line);
    }
}

/// <summary>Weather symbols drawn with shapes, from WMO weather codes.</summary>
public sealed class WeatherGlyph : FrameworkElement
{
    int _code;
    bool _day = true;
    public int Code { get => _code; set { _code = value; InvalidateVisual(); } }
    public bool IsDay { get => _day; set { _day = value; InvalidateVisual(); } }

    static readonly Brush Sun = new SolidColorBrush(Color.FromRgb(0xFF, 0xC5, 0x2E));
    static readonly Brush Moon = new SolidColorBrush(Color.FromRgb(0xE8, 0xE4, 0xC9));
    static readonly Brush Cloud = new SolidColorBrush(Color.FromRgb(0xEE, 0xF1, 0xF6));
    static readonly Brush DarkCloud = new SolidColorBrush(Color.FromRgb(0xA9, 0xB1, 0xBF));
    static readonly Brush Rain = new SolidColorBrush(Color.FromRgb(0x5A, 0xB0, 0xFF));
    static readonly Brush Bolt = new SolidColorBrush(Color.FromRgb(0xFF, 0xD3, 0x3A));

    public static string Describe(int code) => code switch
    {
        0 => "Céu limpo",
        1 => "Quase limpo",
        2 => "Parcialmente nublado",
        3 => "Nublado",
        45 or 48 => "Neblina",
        51 or 53 or 55 or 56 or 57 => "Garoa",
        61 or 63 or 65 or 66 or 67 => "Chuva",
        71 or 73 or 75 or 77 => "Neve",
        80 or 81 or 82 => "Pancadas de chuva",
        85 or 86 => "Pancadas de neve",
        95 or 96 or 99 => "Tempestade",
        _ => "—",
    };

    protected override void OnRender(DrawingContext dc)
    {
        double s = Math.Min(ActualWidth, ActualHeight);
        if (s <= 0) return;
        double ox = (ActualWidth - s) / 2, oy = (ActualHeight - s) / 2;
        Point P(double x, double y) => new(ox + x * s, oy + y * s);

        bool clear = _code is 0 or 1;
        bool partly = _code == 2;
        bool fog = _code is 45 or 48;
        bool rain = _code is >= 51 and <= 67 or >= 80 and <= 82;
        bool snow = _code is >= 71 and <= 77 or 85 or 86;
        bool storm = _code >= 95;

        if (clear || partly)
        {
            var c = partly ? P(0.36, 0.36) : P(0.5, 0.5);
            double r = (partly ? 0.2 : 0.24) * s;
            if (_day)
            {
                var pen = new Pen(Sun, s * 0.05) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                for (int i = 0; i < 8; i++)
                {
                    double a = i * Math.PI / 4;
                    dc.DrawLine(pen, new Point(c.X + Math.Cos(a) * r * 1.35, c.Y + Math.Sin(a) * r * 1.35),
                                     new Point(c.X + Math.Cos(a) * r * 1.75, c.Y + Math.Sin(a) * r * 1.75));
                }
                dc.DrawEllipse(Sun, null, c, r, r);
            }
            else
            {
                var moon = new CombinedGeometry(GeometryCombineMode.Exclude,
                    new EllipseGeometry(c, r * 1.2, r * 1.2),
                    new EllipseGeometry(new Point(c.X + r * 0.6, c.Y - r * 0.4), r * 1.05, r * 1.05));
                dc.DrawGeometry(Moon, null, moon);
            }
            if (clear) return;
        }

        var cloudBrush = storm || rain && _code >= 63 ? DarkCloud : Cloud;
        double cy = rain || snow || storm ? 0.42 : 0.55;
        var cloud = new GeometryGroup { FillRule = FillRule.Nonzero };
        cloud.Children.Add(new EllipseGeometry(P(0.40, cy), 0.17 * s, 0.17 * s));
        cloud.Children.Add(new EllipseGeometry(P(0.60, cy - 0.04), 0.20 * s, 0.20 * s));
        cloud.Children.Add(new RectangleGeometry(new Rect(P(0.24, cy), P(0.80, cy + 0.17)), 0.08 * s, 0.08 * s));
        dc.DrawGeometry(cloudBrush, null, cloud);

        if (fog)
        {
            var pen = new Pen(Cloud, s * 0.045) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawLine(pen, P(0.22, 0.80), P(0.78, 0.80));
            dc.DrawLine(pen, P(0.30, 0.90), P(0.70, 0.90));
        }
        if (rain)
        {
            var pen = new Pen(Rain, s * 0.045) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            for (int i = 0; i < 3; i++) dc.DrawLine(pen, P(0.36 + i * 0.14, 0.70), P(0.32 + i * 0.14, 0.84));
        }
        if (snow)
        {
            for (int i = 0; i < 3; i++) dc.DrawEllipse(Cloud, null, P(0.35 + i * 0.15, 0.76 + (i % 2) * 0.08), s * 0.035, s * 0.035);
        }
        if (storm)
        {
            var bolt = new StreamGeometry();
            using (var ctx = bolt.Open())
            {
                ctx.BeginFigure(P(0.54, 0.62), true, true);
                ctx.PolyLineTo(new[] { P(0.42, 0.80), P(0.51, 0.80), P(0.45, 0.96), P(0.62, 0.74), P(0.53, 0.74), P(0.60, 0.62) }, true, true);
            }
            dc.DrawGeometry(Bolt, null, bolt);
        }
    }
}
