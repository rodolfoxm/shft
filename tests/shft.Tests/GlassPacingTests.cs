using System.Windows;
using Shft.Native.Glass;
using Xunit;

namespace Shft.Tests;

public class GlassPacingTests
{
    const long Tps = 10_000_000;
    long _now;

    [Fact]
    public void Thirty_fps_for_400ms_after_a_change_then_every_2s()
    {
        var p = new GlassPacing(() => _now, Tps);
        Assert.False(p.Active);
        Assert.Equal(2 * Tps, p.NextRefresh(0));
        p.Changed();
        Assert.True(p.Active);
        Assert.Equal(Tps / 30, p.NextRefresh(0));
        _now = Tps * 39 / 100;
        Assert.Equal(_now + Tps / 30, p.NextRefresh(_now));
        _now = Tps * 41 / 100;
        Assert.False(p.Active);
        Assert.Equal(_now + 2 * Tps, p.NextRefresh(_now));
    }

    [Fact]
    public void Relevant_ignores_our_own_windows_and_far_away_changes()
    {
        var area = new Int32Rect(600, 1000, 720, 66);
        var ours = new[] { new Int32Rect(552, 952, 816, 162) }; // the magnifier host under the glass
        Assert.False(GlassPacing.Relevant(new Int32Rect(700, 1010, 50, 20), area, ours));
        Assert.True(GlassPacing.Relevant(new Int32Rect(400, 900, 600, 300), area, ours));
        Assert.False(GlassPacing.Relevant(new Int32Rect(0, 0, 100, 100), area, ours));
    }
}
