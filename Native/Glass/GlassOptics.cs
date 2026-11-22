using System.Windows;

namespace Shft.Native.Glass;

/// <summary>Glass math shared by the shader and the tests (curve from Glasmio / AndroidLiquidGlass, Apache-2.0).</summary>
public static class GlassOptics
{
    /// <summary>How far the background is pushed along the normal, at a signed distance from the edge (negative = inside).</summary>
    public static double Displacement(double signedDistance, double height, double amount)
    {
        var u = Math.Clamp(-signedDistance / height, 0, 1);
        return amount * (1 - Math.Sqrt(u * (2 - u)));
    }

    /// <summary>Signed distance from p to a rounded rect at (0,0) of the given size; normal points outward.</summary>
    public static double RoundedSdf(Point p, Size size, double radius, out Vector normal)
    {
        double hx = size.Width / 2, hy = size.Height / 2;
        double dx = p.X - hx, dy = p.Y - hy;
        double qx = Math.Abs(dx) - (hx - radius), qy = Math.Abs(dy) - (hy - radius);
        double ox = Math.Max(qx, 0), oy = Math.Max(qy, 0);
        double outside = Math.Sqrt(ox * ox + oy * oy);
        double sx = dx < 0 ? -1 : 1, sy = dy < 0 ? -1 : 1;
        if (outside > 0) normal = new Vector(sx * ox / outside, sy * oy / outside);
        else normal = qx > qy ? new Vector(sx, 0) : new Vector(0, sy);
        return outside + Math.Min(Math.Max(qx, qy), 0) - radius;
    }
}

public static class GlassGeometry
{
    /// <summary>Area the magnifier reads: glass plus margin, slid to stay inside the monitor (past the edge reads black), never cut.</summary>
    public static Int32Rect SourceRect(Int32Rect glassPx, int margin, Int32Rect monitorPx)
    {
        int w = Math.Min(glassPx.Width + 2 * margin, monitorPx.Width);
        int h = Math.Min(glassPx.Height + 2 * margin, monitorPx.Height);
        int x = Math.Clamp(glassPx.X - margin, monitorPx.X, monitorPx.X + monitorPx.Width - w);
        int y = Math.Clamp(glassPx.Y - margin, monitorPx.Y, monitorPx.Y + monitorPx.Height - h);
        return new Int32Rect(x, y, w, h);
    }

    public static Int32Rect ToPhysical(Rect dip, double scale) => new(
        (int)Math.Round(dip.X * scale), (int)Math.Round(dip.Y * scale),
        (int)Math.Round(dip.Width * scale), (int)Math.Round(dip.Height * scale));
}
