using System.Runtime.InteropServices;
using System.Windows;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace Shft.Native.Glass;

/// <summary>
/// A monitor capture used only as a change signal: Windows reports which rectangles changed and the pixels are never
/// read. Regions wholly inside our own windows are dropped (that was the self-wake loop); the rest go to the caller
/// in screen pixels. 30 per second at most, on any refresh rate.
/// </summary>
public sealed class ChangeSignal : IDisposable
{
    readonly Direct3D11CaptureFramePool _pool;
    readonly GraphicsCaptureSession _session;
    readonly Func<IReadOnlyList<Int32Rect>> _ours;
    readonly Action<Int32Rect> _onRelevant;
    readonly int _originX, _originY;
    volatile bool _disposed;

    public ChangeSignal(IntPtr monitor, IDirect3DDevice device, Func<IReadOnlyList<Int32Rect>> ours, Action<Int32Rect> onRelevant)
    {
        _ours = ours; _onRelevant = onRelevant;
        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        Win32.GetMonitorInfo(monitor, ref info);
        _originX = info.rcMonitor.Left; _originY = info.rcMonitor.Top;
        var item = GlassNative.CaptureForMonitor(monitor);
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, item.Size);
        _pool.FrameArrived += OnFrame;
        _session = _pool.CreateCaptureSession(item);
        _session.IsCursorCaptureEnabled = false;
        _session.IsBorderRequired = false;
        _session.DirtyRegionMode = GraphicsCaptureDirtyRegionMode.ReportOnly;
        _session.MinUpdateInterval = TimeSpan.FromMilliseconds(33);
        _session.StartCapture();
    }

    void OnFrame(Direct3D11CaptureFramePool pool, object _)
    {
        using var f = pool.TryGetNextFrame();
        if (f == null || _disposed) return;
        var ours = _ours();
        foreach (var r in f.DirtyRegions)
        {
            var screen = new Int32Rect(r.X + _originX, r.Y + _originY, r.Width, r.Height);
            bool inside = false;
            foreach (var o in ours)
                if (screen.X >= o.X - 1 && screen.Y >= o.Y - 1 && screen.X + screen.Width <= o.X + o.Width + 1 && screen.Y + screen.Height <= o.Y + o.Height + 1)
                { inside = true; break; }
            if (!inside) _onRelevant(screen);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pool.FrameArrived -= OnFrame;
        _session.Dispose();
        _pool.Dispose();
    }
}
