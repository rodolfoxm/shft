using Shft.Core;

namespace Shft.Native.Glass;

/// <summary>The one GPU device for real glass, made on first use; null (photo fallback) when it cannot be made.</summary>
public static class GlassRuntime
{
    static GlassDevice? _device;
    static bool _failed;

    /// <summary>Real glass wanted and possible here: setting on, Windows 11, not high contrast, not a remote session.</summary>
    public static bool Wanted(AppSettings s) =>
        s.RealGlass && Environment.OSVersion.Version.Build >= 22000
        && !System.Windows.SystemParameters.HighContrast && !System.Windows.Forms.SystemInformation.TerminalServerSession;

    public static GlassDevice? Device
    {
        get
        {
            if (_device != null || _failed) return _device;
            _device = GlassDevice.TryCreate(out var why);
            if (_device == null) { _failed = true; Log.Write($"real glass off: {why}"); }
            return _device;
        }
    }

    /// <summary>Glass follows the panel's animation; with reduced motion the panel only fades, so the glass does too.</summary>
    public static (double ScaleX, double ScaleY, double MoveY, double Opacity) Follow(double sx, double sy, double ty, double opacity, bool reduced) =>
        reduced ? (1, 1, 0, opacity) : (sx, sy, ty, opacity);
}
