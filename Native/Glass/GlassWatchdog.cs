namespace Shft.Native.Glass;

public enum GlassHealth { Live, Fallback, Off }

/// <summary>
/// Per-surface guard: no frame for 2 s while live, a lost device or a failed magnifier call → fallback glass,
/// retry after 30 s; three failures in a row without a frame in between → off until restart.
/// </summary>
public sealed class GlassWatchdog(Func<long> now, long tps)
{
    const int StallSeconds = 2, RetrySeconds = 30, MaxFailures = 3;

    readonly object _gate = new();
    long _lastFrame = now(), _failedAt;
    int _failures;
    bool _locked;
    GlassHealth _state = GlassHealth.Live;

    public string? LastReason { get; private set; }

    public void Frame()
    {
        lock (_gate) { _lastFrame = now(); if (_state == GlassHealth.Live) _failures = 0; }
    }

    public void Failed(string why)
    {
        lock (_gate)
        {
            if (_state != GlassHealth.Live) return;
            LastReason = why;
            _failedAt = now();
            _state = ++_failures >= MaxFailures ? GlassHealth.Off : GlassHealth.Fallback;
        }
    }

    /// <summary>Locked session or secure desktop: capture stops by design, not a failure.</summary>
    public void Locked(bool locked)
    {
        lock (_gate) { _locked = locked; _lastFrame = now(); }
    }

    public GlassHealth State
    {
        get
        {
            lock (_gate)
            {
                if (_state == GlassHealth.Live && !_locked && now() - _lastFrame > StallSeconds * tps) Failed("stall");
                return _state;
            }
        }
    }

    /// <summary>True once, 30 s after a fallback: the caller rebuilds the surface and the guard is live again.</summary>
    public bool ShouldRetry
    {
        get
        {
            lock (_gate)
            {
                if (_state != GlassHealth.Fallback || now() - _failedAt < RetrySeconds * tps) return false;
                _state = GlassHealth.Live;
                _lastFrame = now();
                return true;
            }
        }
    }
}
