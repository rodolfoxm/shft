using System.Numerics;
using System.Windows;
using System.Windows.Media;
using Shft.Core;
using Shft.Native.Glass;
using Vortice.DirectComposition;

namespace Shft.Ui;

/// <summary>
/// Copies the card's open/close animation (scale, slide, fade) to the glass visual every frame, two frames late:
/// WPF presents about two frames after DirectComposition commits (measured: 99.4–99.6% of frames exact; a few fade-edge
/// frames off by 8–21 px — known limitation). Runs only while something is moving.
/// </summary>
sealed class PanelGlassSync
{
    const int DelayFrames = 2, StillFrames = 6;

    readonly FrameworkElement _frame;
    readonly GlassSurface _glass;
    readonly Point _origin;
    readonly double _dpiX, _dpiY;
    readonly IDCompositionEffectGroup _effect;
    readonly Queue<(double, double, double, double)> _queue = new();
    (double, double, double, double) _last = (-1, -1, -1, -1);
    int _still;
    bool _running;

    public PanelGlassSync(FrameworkElement frame, GlassSurface glass, Point origin, double dpiX, double dpiY)
    {
        _frame = frame; _glass = glass; _origin = origin; _dpiX = dpiX; _dpiY = dpiY;
        glass.Composition.CreateEffectGroup(out _effect).CheckError();
        glass.Visual.SetEffect(_effect).CheckError();
    }

    (double, double, double, double) Now()
    {
        double sx = 1, sy = 1, ty = 0;
        if (_frame.RenderTransform is TransformGroup { Children: [ScaleTransform s, TranslateTransform t] }) { sx = s.ScaleX; sy = s.ScaleY; ty = t.Y; }
        return GlassRuntime.Follow(sx, sy, ty, _frame.Opacity, Motion.Reduced);
    }

    public void Run()
    {
        _still = 0;
        if (_running) return;
        _running = true;
        CompositionTarget.Rendering += OnRendering;
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    /// <summary>Starts hidden, like the card before its fade-in.</summary>
    public void Hidden() { var n = Now(); Set((n.Item1, n.Item2, n.Item3, 0)); }

    void OnRendering(object? sender, EventArgs e)
    {
        var now = Now();
        // the card appearing from nothing: apply the jump at once, the delay would show glass without its card
        if (_queue.Count > 0 && _queue.Last().Item4 <= 0.001 && now.Item4 > 0.001)
        {
            _queue.Clear();
            for (int k = 0; k < DelayFrames; k++) _queue.Enqueue(now);
        }
        _queue.Enqueue(now);
        if (_queue.Count <= DelayFrames) return;
        var v = _queue.Dequeue();
        if (v == _last) { if (++_still >= StillFrames && _queue.All(q => q == v)) Stop(); return; }
        _still = 0;
        Set(v);
    }

    void Set((double Sx, double Sy, double Ty, double O) v)
    {
        _last = v;
        float w = (float)(_frame.ActualWidth * _dpiX), h = (float)(_frame.ActualHeight * _dpiY);
        float ox = (float)_origin.X * w, oy = (float)_origin.Y * h;
        var m = Matrix3x2.CreateTranslation(-ox, -oy) * Matrix3x2.CreateScale((float)v.Sx, (float)v.Sy)
              * Matrix3x2.CreateTranslation(ox, oy + (float)(v.Ty * _dpiY));
        try
        {
            _glass.Visual.SetTransform(m);
            _effect.SetOpacity((float)v.O);
            _glass.Composition.Commit();
        }
        catch (Exception ex) { Log.Write($"panel glass sync: {ex.Message}"); Stop(); }
    }

    public void Dispose() { Stop(); _effect.Dispose(); }
}
