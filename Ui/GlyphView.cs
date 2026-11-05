using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Shft.Ui;

/// <summary>
/// One icon of shft.'s own vector set: rounded, solid and drawn for this app on a 24-unit grid. Not derived from
/// SF Symbols, whose license keeps them on Apple platforms. Codes outside the set fall back to the system icon font.
/// </summary>
public sealed class GlyphView : FrameworkElement
{
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(GlyphView),
        new FrameworkPropertyMetadata(SystemColors.ControlTextBrush, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    sealed record Shape(Geometry? Fill, Geometry? Line);

    const double Weight = 2;

    static Shape S(string? fill, string? line = null, bool mirror = false)
    {
        Geometry? Parse(string? data)
        {
            if (data == null) return null;
            var g = Geometry.Parse(data);
            if (mirror)
            {
                g = g.Clone();
                g.Transform = new ScaleTransform(-1, 1, 12, 0);
            }
            g.Freeze();
            return g;
        }
        return new Shape(Parse(fill), Parse(line));
    }

    const string Speaker = "M4 9.3h3.2l4.6-4a.7.7 0 0 1 1.2.5v12.4a.7.7 0 0 1-1.2.5l-4.6-4H4a1 1 0 0 1-1-1V10.3a1 1 0 0 1 1-1z";
    const string Mic = "M12 3a3 3 0 0 1 3 3v5a3 3 0 0 1-6 0V6a3 3 0 0 1 3-3z";
    const string Skip = "M3 6.2v11.6a.8.8 0 0 0 1.2.7L12 13.4v4.4a.8.8 0 0 0 1.2.7l8.6-5.8a.8.8 0 0 0 0-1.4l-8.6-5.8a.8.8 0 0 0-1.2.7v4.4L4.2 5.5A.8.8 0 0 0 3 6.2z";
    const string WifiArcs = "M3 9.6a13 13 0 0 1 18 0M6.2 13a8.4 8.4 0 0 1 11.6 0M9.4 16.3a4 4 0 0 1 5.2 0M12 19.6l.01 0";
    const string Pin = "M9 3.5h6M10 3.5v6.2L7 13.5h10l-3-3.8V3.5M12 13.5V21";

    static string Gear()
    {
        var sb = new System.Text.StringBuilder("F0 ");
        const int teeth = 8;
        for (int i = 0; i < teeth * 4; i++)
        {
            double r = i % 4 is 0 or 1 ? 10.2 : 7.6;
            double a = (i - 0.5) * Math.PI / (teeth * 2);
            sb.Append(i == 0 ? 'M' : 'L').Append((12 + r * Math.Cos(a)).ToString("0.00", CultureInfo.InvariantCulture)).Append(' ')
              .Append((12 + r * Math.Sin(a)).ToString("0.00", CultureInfo.InvariantCulture)).Append(' ');
        }
        return sb.Append("zM15 12a3 3 0 1 0-6 0a3 3 0 1 0 6 0z").ToString();
    }

    static readonly Dictionary<string, Shape> Set = new()
    {
        // two stacked switches: the top one on, the bottom one off
        [Glyph.Controls] = S("F0 M6.5 4h11a3.5 3.5 0 0 1 0 7h-11a3.5 3.5 0 0 1 0-7zM15 7.5a2 2 0 1 0 4 0a2 2 0 1 0-4 0zM6.5 13h11a3.5 3.5 0 0 1 0 7h-11a3.5 3.5 0 0 1 0-7zM6.5 14.5a2 2 0 0 0 0 4h11a2 2 0 0 0 0-4zM5 16.5a2 2 0 1 0 4 0a2 2 0 1 0-4 0z"),
        [Glyph.Down] = S(null, "M6 9.5l6 6 6-6"),
        [Glyph.Up] = S(null, "M6 14.5l6-6 6 6"),
        [Glyph.Chevron] = S(null, "M9.5 6l6 6-6 6"),
        [Glyph.Back] = S(null, "M14.5 6l-6 6 6 6"),
        [Glyph.Wifi] = S(null, WifiArcs),
        [Glyph.Offline] = S(null, WifiArcs + "M4 4l16 16"),
        [Glyph.Ethernet] = S(null, "M8 7l-5 5 5 5M16 7l5 5-5 5M9 12l.01 0M12 12l.01 0M15 12l.01 0"),
        [Glyph.Bluetooth] = S(null, "M6.5 7.5L17 16.5 12 21V3l5 4.5L6.5 16.5"),
        [Glyph.Volume] = S(Speaker, "M15.8 9a4.4 4.4 0 0 1 0 6M18.4 6.4a8 8 0 0 1 0 11.2"),
        [Glyph.Mute] = S(Speaker, "M16 9.5l5 5M21 9.5l-5 5"),
        [Glyph.Mic] = S(Mic, "M6 11a6 6 0 0 0 12 0M12 17v4"),
        [Glyph.MicOff] = S(Mic, "M6 11a6 6 0 0 0 12 0M12 17v4M4 4l16 16"),
        [Glyph.Bell] = S("M12 3a6 6 0 0 0-6 6v3.5l-1.7 3.1a1 1 0 0 0 .9 1.4h13.6a1 1 0 0 0 .9-1.4L18 12.5V9a6 6 0 0 0-6-6z", "M10 20.2a2.3 2.3 0 0 0 4 0"),
        [Glyph.Memory] = S("F0 M8 6h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2zM10 10v4h4v-4z", "M9.5 3v2M14.5 3v2M9.5 19v2M14.5 19v2M3 9.5h2M3 14.5h2M19 9.5h2M19 14.5h2"),
        [Glyph.Sun] = S("M8 12a4 4 0 1 0 8 0a4 4 0 1 0-8 0z", "M12 3v2M12 19v2M3 12h2M19 12h2M5.6 5.6l1.4 1.4M17 17l1.4 1.4M5.6 18.4L7 17M17 7l1.4-1.4"),
        [Glyph.Moon] = S("M20.5 14.4A8.8 8.8 0 1 1 9.6 3.5a7.2 7.2 0 0 0 10.9 10.9z"),
        [Glyph.Settings] = S(Gear()),
        [Glyph.Power] = S(null, "M12 3v9M7 5.8a8 8 0 1 0 10 0"),
        [Glyph.Hdr] = S("F0 M5 4h14a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2zM5 6v9h14V6z", "M8.5 20.5h7"),
        [Glyph.Eye] = S("M9 12a3 3 0 1 0 6 0a3 3 0 1 0-6 0z", "M2.5 12s3.5-6.5 9.5-6.5 9.5 6.5 9.5 6.5-3.5 6.5-9.5 6.5S2.5 12 2.5 12z"),
        [Glyph.Search] = S(null, "M4 10.5a6.5 6.5 0 1 0 13 0a6.5 6.5 0 1 0-13 0M15.5 15.5L21 21"),
        [Glyph.Pin] = S(null, Pin),
        [Glyph.Unpin] = S(null, Pin + "M4 4l16 16"),
        [Glyph.Play] = S("M7 5.2v13.6a1 1 0 0 0 1.5.9l11-6.8a1 1 0 0 0 0-1.8l-11-6.8A1 1 0 0 0 7 5.2z"),
        [Glyph.Pause] = S("M7 4.5h2.5a1 1 0 0 1 1 1v13a1 1 0 0 1-1 1H7a1 1 0 0 1-1-1v-13a1 1 0 0 1 1-1zM14.5 4.5H17a1 1 0 0 1 1 1v13a1 1 0 0 1-1 1h-2.5a1 1 0 0 1-1-1v-13a1 1 0 0 1 1-1z"),
        [Glyph.Next] = S(Skip),
        [Glyph.Prev] = S(Skip, mirror: true),
        [Glyph.Check] = S(null, "M5 12.5l4.5 4.5L19 7.5"),
        [Glyph.Close] = S(null, "M6 6l12 12M18 6L6 18"),
        [Glyph.Today] = S(null, "M4 11.5L12 4l8 7.5M6.5 9.5V20h11V9.5"),
        [Glyph.Battery] = S("F0 M4 7h14a2 2 0 0 1 2 2v6a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V9a2 2 0 0 1 2-2zM4 9v6h14V9zM5.5 10.5h9v3h-9z", "M22 10.5v3"),
        [Glyph.Charging] = S("M13.5 3L5 13.5h6L10 21l9-11h-6.5z"),
        [""] = S("M12 3.5l2.6 5.4 5.9.8-4.3 4.1 1 5.9-5.2-2.8-5.2 2.8 1-5.9-4.3-4.1 5.9-.8z"),
    };

    string _text;
    readonly double _size;

    public GlyphView(string code, double size)
    {
        _text = code;
        _size = size * 1.2; // the grid's 24 units carry more air than a font glyph of the same size
        Width = Height = _size;
        IsHitTestVisible = false;
    }

    public string Text
    {
        get => _text;
        set
        {
            if (_text == value) return;
            _text = value;
            InvalidateVisual();
        }
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (!Set.TryGetValue(_text, out var shape))
        {
            var text = new FormattedText(_text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(Glyph.Font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                _size / 1.2, Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point((_size - text.Width) / 2, (_size - text.Height) / 2));
            return;
        }
        double scale = _size / 24;
        dc.PushTransform(new ScaleTransform(scale, scale));
        if (shape.Fill != null) dc.DrawGeometry(Foreground, null, shape.Fill);
        if (shape.Line != null)
        {
            dc.DrawGeometry(null, new Pen(Foreground, Weight) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, shape.Line);
        }
        dc.Pop();
    }
}
