using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Shft.Core;

namespace Shft.Native;

/// <summary>Reads what is on screen, for the glass panels and the adaptive top bar.</summary>
public static class ScreenSampler
{
    const uint WDA_NONE = 0, WDA_EXCLUDEFROMCAPTURE = 0x11;
    const int HALFTONE = 4;
    const uint SRCCOPY = 0x00CC0020;

    /// <summary>The screen rectangle shrunk to <paramref name="w"/>×<paramref name="h"/>, as BGRA bytes. Null on failure.</summary>
    public static byte[]? Capture(RECT r, int w, int h)
    {
        var screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return null;
        var dc = CreateCompatibleDC(screen);
        var bits = IntPtr.Zero;
        var info = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var dib = CreateDIBSection(dc, ref info, 0, out bits, IntPtr.Zero, 0);
        try
        {
            if (dib == IntPtr.Zero) return null;
            var old = SelectObject(dc, dib);
            SetStretchBltMode(dc, HALFTONE);
            bool ok = StretchBlt(dc, 0, 0, w, h, screen, r.Left, r.Top, r.Width, r.Height, SRCCOPY);
            SelectObject(dc, old);
            if (!ok) return null;
            var pixels = new byte[w * h * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            return pixels;
        }
        finally
        {
            if (dib != IntPtr.Zero) DeleteObject(dib);
            DeleteDC(dc);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>
    /// What lies under our own windows. Display affinity makes Windows leave them out of screen reads for a moment;
    /// they stay on the monitor as they are. Null on failure (the caller falls back).
    /// </summary>
    public static async Task<byte[]?> CaptureBehind(IReadOnlyList<IntPtr> windows, RECT r, int w, int h)
    {
        try
        {
            foreach (var hwnd in windows) SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
            await Task.Delay(30); // one composed frame; measured at 13 ms on this PC
            return Capture(r, w, h);
        }
        finally
        {
            foreach (var hwnd in windows) SetWindowDisplayAffinity(hwnd, WDA_NONE);
        }
    }

    /// <summary>A blurred copy of a screen area for a panel's glass. Done in bytes: a tenth of the size, a few box passes.</summary>
    public static BitmapSource? GlassOf(RECT r, int softness)
    {
        int s = Math.Clamp(softness, 2, 24);
        int w = Math.Max(4, r.Width / s), h = Math.Max(4, r.Height / s);
        var px = Capture(r, w, h);
        if (px == null) return null;
        Saturate(px, 1.35);
        for (int i = 0; i < 3; i++) BoxBlur(px, w, h, 2);
        for (int i = 3; i < px.Length; i += 4) px[i] = 255;
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        bmp.Freeze();
        return bmp;
    }

    internal static void Saturate(byte[] px, double amount)
    {
        for (int i = 0; i + 2 < px.Length; i += 4)
        {
            double b = px[i], g = px[i + 1], r = px[i + 2];
            double grey = 0.114 * b + 0.587 * g + 0.299 * r;
            px[i] = (byte)Math.Clamp(grey + (b - grey) * amount, 0, 255);
            px[i + 1] = (byte)Math.Clamp(grey + (g - grey) * amount, 0, 255);
            px[i + 2] = (byte)Math.Clamp(grey + (r - grey) * amount, 0, 255);
        }
    }

    /// <summary>One box pass over the colour channels, horizontal then vertical, clamped at the edges.</summary>
    internal static void BoxBlur(byte[] px, int w, int h, int radius)
    {
        var copy = new byte[px.Length];
        Pass(px, copy, w, h, radius, 4, w * 4);
        Pass(copy, px, h, w, radius, w * 4, 4);
    }

    static void Pass(byte[] src, byte[] dst, int length, int lines, int radius, int step, int lineStep)
    {
        int window = radius * 2 + 1;
        for (int line = 0; line < lines; line++)
        {
            int origin = line * lineStep;
            for (int c = 0; c < 4; c++)
            {
                int sum = 0;
                for (int k = -radius; k <= radius; k++) sum += src[origin + Math.Clamp(k, 0, length - 1) * step + c];
                for (int i = 0; i < length; i++)
                {
                    dst[origin + i * step + c] = (byte)(sum / window);
                    sum += src[origin + Math.Min(i + radius + 1, length - 1) * step + c] - src[origin + Math.Max(i - radius, 0) * step + c];
                }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] static extern bool StretchBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int sw, int sh, uint op);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);
}

/// <summary>Text colour decision for the adaptive top bar. Pure, so it can be tested on its own.</summary>
public static class AdaptiveContrast
{
    public readonly record struct Result(bool DarkText, bool Scrim);

    /// <summary>Relative luminance (WCAG) of one sRGB colour, 0..1.</summary>
    public static double Luminance(byte r, byte g, byte b)
    {
        static double Linear(byte v)
        {
            double c = v / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(r) + 0.7152 * Linear(g) + 0.0722 * Linear(b);
    }

    /// <summary>Luminance of each pixel in a BGRA run.</summary>
    public static double[] Luminances(byte[] px, int from, int count)
    {
        var l = new double[count];
        for (int i = 0; i < count; i++)
        {
            int o = (from + i) * 4;
            l[i] = Luminance(px[o + 2], px[o + 1], px[o]);
        }
        return l;
    }

    static double Contrast(double a, double b) => (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);

    /// <summary>
    /// Chooses the text colour with the better worst-case contrast. The current choice is kept near the
    /// tie point so a slowly changing background doesn't flip it; a mixed background asks for the scrim.
    /// </summary>
    public static Result Decide(double[] luminance, bool? previousDark)
    {
        if (luminance.Length == 0) return new Result(previousDark ?? false, true);
        const double black = 0.0, white = 1.0, readable = 3.0;
        double mean = luminance.Average();
        bool dark = Contrast(mean, black) > Contrast(mean, white);
        if (previousDark is bool was && was != dark)
        {
            double gain = Math.Abs(Contrast(mean, black) - Contrast(mean, white));
            if (gain < 1.2) dark = was;
        }
        double text = dark ? black : white;
        double failing = luminance.Count(l => Contrast(l, text) < readable) / (double)luminance.Length;
        return new Result(dark, failing > 0.12);
    }
}
