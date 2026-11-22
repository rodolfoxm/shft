using System.Windows;
using Shft.Native.Glass;
using Xunit;

namespace Shft.Tests;

public class GlassOpticsTests
{
    [Fact]
    public void Displacement_is_full_at_the_edge_and_gone_at_the_band_depth()
    {
        Assert.Equal(14, GlassOptics.Displacement(0, 18, 14), 6);
        Assert.Equal(0, GlassOptics.Displacement(-18, 18, 14), 6);
        Assert.Equal(14 * (1 - Math.Sqrt(0.75)), GlassOptics.Displacement(-9, 18, 14), 6);
        Assert.Equal(0, GlassOptics.Displacement(-40, 18, 14), 6); // deep inside: no bending
    }

    [Fact]
    public void Rounded_sdf_inside_edge_outside_and_normals()
    {
        var size = new Size(200, 100);
        Assert.True(GlassOptics.RoundedSdf(new Point(100, 50), size, 18, out _) < 0);
        Assert.Equal(0, GlassOptics.RoundedSdf(new Point(100, 0), size, 18, out var top), 6);
        Assert.Equal(new Vector(0, -1), top);
        Assert.True(GlassOptics.RoundedSdf(new Point(100, -5), size, 18, out _) > 0);
        GlassOptics.RoundedSdf(new Point(2, 2), size, 18, out var corner);
        Assert.Equal(1, corner.Length, 6);
        Assert.True(corner.X < 0 && corner.Y < 0);
    }

    [Fact]
    public void Source_rect_stays_inside_the_monitor_without_shrinking()
    {
        var monitor = new Int32Rect(0, 0, 1920, 1080);
        var glass = new Int32Rect(600, 1000, 720, 66); // touching the bottom of the screen
        var src = GlassGeometry.SourceRect(glass, 48, monitor);
        Assert.Equal(720 + 96, src.Width);
        Assert.Equal(66 + 96, src.Height);
        Assert.Equal(1080, src.Y + src.Height);
        Assert.Equal(600 - 48, src.X);
    }

    [Fact]
    public void Source_rect_on_a_monitor_left_of_the_main_one()
    {
        var monitor = new Int32Rect(-1080, -394, 1080, 1920);
        var src = GlassGeometry.SourceRect(new Int32Rect(-1060, 1400, 300, 66), 48, monitor);
        Assert.Equal(-1080, src.X);
        Assert.True(src.Y + src.Height <= 1526);
    }

    [Fact]
    public void Scale_150_maps_dips_to_physical_pixels() =>
        Assert.Equal(new Int32Rect(15, 30, 150, 75), GlassGeometry.ToPhysical(new Rect(10, 20, 100, 50), 1.5));
}
