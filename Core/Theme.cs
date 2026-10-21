using System.Windows.Media;
using Microsoft.Win32;

namespace ClonaDock.Core;

/// <summary>Colours for the Dock, its menus and popups, resolved from the settings and Windows' colour mode.</summary>
public sealed class Theme
{
    public bool IsDark { get; init; }
    public Color BarFill { get; init; }
    public Color BarBorder { get; init; }
    public Color Indicator { get; init; }
    public Color LabelBack { get; init; }
    public Color LabelText { get; init; }
    public Color MenuBack { get; init; }
    public Color MenuText { get; init; }
    public Color MenuHover { get; init; }
    public Color MenuSeparator { get; init; }
    public Color Accent { get; init; }

    public static bool WindowsUsesDarkTaskbar()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v ? v == 0 : true;
        }
        catch
        {
            return true;
        }
    }

    public static Color AccentColor()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int abgr)
            {
                var u = unchecked((uint)abgr);
                return Color.FromRgb((byte)(u & 0xFF), (byte)((u >> 8) & 0xFF), (byte)((u >> 16) & 0xFF));
            }
        }
        catch { }
        return Color.FromRgb(0x3B, 0x82, 0xF6);
    }

    public static Theme Resolve(AppSettings s)
    {
        bool dark = s.Theme switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => WindowsUsesDarkTaskbar(),
        };
        // Never fully 0: fully transparent pixels of the layered Dock window would let clicks fall through.
        byte fillAlpha = (byte)Math.Max(1, Math.Round(Math.Clamp(s.BarOpacity, 0, 1) * 255));
        var fill = dark ? Color.FromArgb(fillAlpha, 0x1C, 0x1C, 0x20) : Color.FromArgb(fillAlpha, 0xF4, 0xF4, 0xF6);
        if (TryParse(s.Tint, out var tint))
            fill = Color.FromArgb(fillAlpha, tint.R, tint.G, tint.B);

        var accent = AccentColor();
        var indicator = TryParse(s.IndicatorColor, out var ind) ? ind
            : dark ? Color.FromRgb(0xE8, 0xE8, 0xE8) : Color.FromRgb(0x30, 0x30, 0x30);

        return new Theme
        {
            IsDark = dark,
            BarFill = fill,
            BarBorder = dark ? Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x30, 0x00, 0x00, 0x00),
            Indicator = indicator,
            LabelBack = dark ? Color.FromArgb(0xE6, 0x2A, 0x2A, 0x2E) : Color.FromArgb(0xF0, 0xFA, 0xFA, 0xFA),
            LabelText = dark ? Colors.White : Color.FromRgb(0x1A, 0x1A, 0x1A),
            MenuBack = dark ? Color.FromRgb(0x2B, 0x2B, 0x2F) : Color.FromRgb(0xF9, 0xF9, 0xFB),
            MenuText = dark ? Color.FromRgb(0xF2, 0xF2, 0xF2) : Color.FromRgb(0x1A, 0x1A, 0x1A),
            MenuHover = dark ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x18, 0x00, 0x00, 0x00),
            MenuSeparator = dark ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x20, 0x00, 0x00, 0x00),
            Accent = accent,
        };
    }

    public static bool TryParse(string? text, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            color = (Color)ColorConverter.ConvertFromString(text.Trim());
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
