using Shft.Native.Glass;
using Xunit;

namespace Shft.Tests;

public class GlassWatchdogTests
{
    const long Tps = 1000;
    long _now;
    GlassWatchdog New() => new(() => _now, Tps);

    [Fact]
    public void Stall_2s_goes_to_fallback()
    {
        var w = New();
        _now = 1900; Assert.Equal(GlassHealth.Live, w.State);
        _now = 2100; Assert.Equal(GlassHealth.Fallback, w.State);
    }

    [Fact]
    public void Frames_keep_it_live()
    {
        var w = New();
        for (_now = 0; _now < 10_000; _now += 500) w.Frame();
        Assert.Equal(GlassHealth.Live, w.State);
    }

    [Fact]
    public void Retry_after_30s()
    {
        var w = New();
        w.Failed("d3d");
        _now = 29_000; Assert.False(w.ShouldRetry);
        _now = 30_000; Assert.True(w.ShouldRetry);
        Assert.Equal(GlassHealth.Live, w.State);
    }

    [Fact]
    public void Three_failures_turn_it_off()
    {
        var w = New();
        for (int i = 0; i < 3; i++) { w.Failed("mag"); _now += 30_000; w.ShouldRetry.ToString(); }
        Assert.Equal(GlassHealth.Off, w.State);
        _now += 100_000;
        Assert.False(w.ShouldRetry);
    }

    [Fact]
    public void Stall_while_locked_is_not_a_failure()
    {
        var w = New();
        w.Locked(true);
        _now = 60_000; Assert.Equal(GlassHealth.Live, w.State);
        w.Locked(false);
        _now = 61_000; Assert.Equal(GlassHealth.Live, w.State);
    }

    [Fact]
    public void MonitorGone_goes_to_fallback()
    {
        var w = New();
        w.Failed("monitor");
        Assert.Equal(GlassHealth.Fallback, w.State);
    }
}
