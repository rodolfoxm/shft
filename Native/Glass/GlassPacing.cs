using System.Windows;

namespace Shft.Native.Glass;

/// <summary>When to ask the magnifier for a new image: 30 FPS for 400 ms after a relevant change, otherwise every 2 s.</summary>
public sealed class GlassPacing(Func<long> now, long ticksPerSecond)
{
    const int Fps = 30;
    const double ActiveSeconds = 0.4, IdleSeconds = 2;

    long _lastChange = long.MinValue;

    /// <summary>Something relevant moved behind the glass. Safe from any thread.</summary>
    public void Changed() => Interlocked.Exchange(ref _lastChange, now());

    public bool Active
    {
        get
        {
            long last = Interlocked.Read(ref _lastChange);
            return last != long.MinValue && now() - last < (long)(ActiveSeconds * ticksPerSecond);
        }
    }

    public long NextRefresh(long lastRefresh) =>
        lastRefresh + (Active ? ticksPerSecond / Fps : (long)(IdleSeconds * ticksPerSecond));

    /// <summary>A dirty region matters if it touches the glass area and is not wholly inside one of our own windows (that was the self-wake loop).</summary>
    public static bool Relevant(Int32Rect dirty, Int32Rect area, IReadOnlyList<Int32Rect> ours)
    {
        foreach (var o in ours)
            if (dirty.X >= o.X && dirty.Y >= o.Y && dirty.X + dirty.Width <= o.X + o.Width && dirty.Y + dirty.Height <= o.Y + o.Height)
                return false;
        return dirty.X < area.X + area.Width && area.X < dirty.X + dirty.Width
            && dirty.Y < area.Y + area.Height && area.Y < dirty.Y + dirty.Height;
    }
}
