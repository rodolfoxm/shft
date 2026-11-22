using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using Shft.Ui;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using WinRT;
using Box = Vortice.Mathematics.Box;
using Color = System.Windows.Media.Color;

namespace Shft.Native.Glass;

/// <summary>
/// One live glass: a glass window (DirectComposition swap chain, no redirection bitmap) just below its owner, a
/// magnifier host (alpha 1) just below the glass, a capture of that host window, and the shader. No window is ever
/// hidden from capture: the magnifier leaves our windows out for itself only.
/// </summary>
public sealed class GlassSurface : IGlassSource, IDisposable
{
    const int Margin = 48;

    /// <summary>Every shft. window the magnifier must leave out (Dock, bars, open panels). Set once by the app.</summary>
    public static Func<IReadOnlyList<IntPtr>> OurWindows { get; set; } = GlassNative.OwnWindows;

    static readonly List<GlassSurface> Live = new();
    static readonly Dictionary<IntPtr, (ChangeSignal? Signal, int Count)> Signals = new();
    static readonly object StaticGate = new();

    readonly IntPtr _owner;
    readonly System.Windows.Threading.Dispatcher _ui = System.Windows.Threading.Dispatcher.CurrentDispatcher;
    static bool _displayHooked;
    readonly GlassDevice _dev;
    readonly GlassPacing _pacing = new(Stopwatch.GetTimestamp, Stopwatch.Frequency);
    readonly GlassWatchdog _watch = new(Stopwatch.GetTimestamp, Stopwatch.Frequency);
    readonly IntPtr _glassHwnd;
    internal IntPtr GlassHwnd => _glassHwnd;
    IntPtr _hostHwnd, _magHwnd, _monitor;
    Int32Rect _glass, _src, _monitorPx;
    float _radius;
    long _lastRefresh;
    volatile bool _paused = true, _built, _disposed;
    byte[]? _previous;
    GlassHealth _health = GlassHealth.Live;

    IDXGISwapChain1? _swap;
    ID3D11RenderTargetView? _rtv;
    ID3D11Texture2D? _srcTex;
    ID3D11ShaderResourceView? _srv;
    Direct3D11CaptureFramePool? _pool;
    GraphicsCaptureSession? _session;
    readonly IDCompositionDevice _dcomp;
    readonly IDCompositionTarget _target;

    public IDCompositionVisual Visual { get; }
    public IDCompositionDevice Composition => _dcomp;
    public event Action<GlassHealth>? HealthChanged;

    public GlassStyle Style { get; set; } = GlassStyle.Regular;
    public bool Dark { get; set; } = true;
    public float Blur { get; set; } = 14;
    public Color? Tint { get; set; }

    /// <summary>Test hook: the next source image as BGRA bytes (width = source width).</summary>
    internal TaskCompletionSource<(byte[] Pixels, int Width, int Height)>? NextSource;

    GlassSurface(IntPtr owner, GlassDevice dev)
    {
        _owner = owner; _dev = dev;
        // Created on the caller's (owner's UI) thread: the magnifier cannot leave out windows of its own thread.
        // Every later call on it is async, so no thread ever waits on another.
        _glassHwnd = GlassNative.CreateLayer(GlassNative.EX_NOREDIRECTIONBITMAP | GlassNative.EX_LAYERED, 0, 0, 1, 1);
        if (_glassHwnd == IntPtr.Zero) throw new InvalidOperationException("glass window");
        using var dxgi = dev.Device.QueryInterface<IDXGIDevice>();
        _dcomp = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgi);
        _dcomp.CreateTargetForHwnd(_glassHwnd, true, out _target).CheckError();
        _dcomp.CreateVisual(out var visual).CheckError();
        Visual = visual;
        _target.SetRoot(Visual).CheckError();
        _dcomp.Commit().CheckError();
    }

    /// <summary>A live glass for this owner window, or null and why (no magnifier, no capture, driver error).</summary>
    public static GlassSurface? TryCreate(IntPtr ownerHwnd, GlassDevice dev, out string? why)
    {
        why = null;
        try
        {
            if (!GraphicsCaptureSession.IsSupported()) { why = "Windows Graphics Capture unavailable"; return null; }
            MagnifierHost.Invoke(() => { });
            if (!MagnifierHost.Available) { why = "magnifier unavailable"; return null; }
            var s = new GlassSurface(ownerHwnd, dev);
            lock (StaticGate)
            {
                if (Live.Count == 0) WindowEvents.Start(OnWindowChange);
                Live.Add(s);
                if (!_displayHooked) { _displayHooked = true; Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => DisplayChanged(); }
            }
            return s;
        }
        catch (Exception e) { why = e.Message; return null; }
    }

    // ------------------------------------------------------------------ shape and visibility

    /// <summary>The glass rect in screen pixels and its corner radius. Moving is cheap; a new size rebuilds the buffers.</summary>
    public void SetShape(Int32Rect glassPx, double radiusPx)
    {
        if (glassPx.Width <= 0 || glassPx.Height <= 0) return;
        bool resized = glassPx.Width != _glass.Width || glassPx.Height != _glass.Height;
        var mon = GlassNative.MonitorFromPoint(new POINT { X = glassPx.X + glassPx.Width / 2, Y = glassPx.Y + glassPx.Height / 2 }, Win32.MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        Win32.GetMonitorInfo(mon, ref info);
        var monPx = new Int32Rect(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Width, info.rcMonitor.Height);
        var src = GlassGeometry.SourceRect(glassPx, Margin, monPx);
        bool srcResized = src.Width != _src.Width || src.Height != _src.Height;
        bool monitorChanged = mon != _monitor;
        _glass = glassPx; _radius = (float)radiusPx; _src = src; _monitorPx = monPx;
        if (monitorChanged) { ReleaseSignal(); _monitor = mon; if (!_paused) AcquireSignal(); }
        if (!_built) return;
        if (resized || srcResized) { Release(); Build(); return; }
        Place();
        Poke();
    }

    public void Show()
    {
        if (_disposed || !_paused || _glass.Width == 0) return;
        _paused = false;
        AcquireSignal();
        if (_watch.State == GlassHealth.Live) Build();
    }

    public void Hide()
    {
        if (_paused) return;
        _paused = true;
        Release();
        ReleaseSignal();
    }

    /// <summary>Full screen in front: nothing runs and nothing is drawn until <see cref="Resume"/>.</summary>
    public void Pause() => Hide();

    public void Resume() => Show();

    // ------------------------------------------------------------------ IGlassSource (magnifier thread)

    public Int32Rect ScreenArea => _src;
    public bool Paused => _paused || !_built;
    public long NextRefresh => _pacing.NextRefresh(Interlocked.Read(ref _lastRefresh));

    public void Refresh()
    {
        Interlocked.Exchange(ref _lastRefresh, Stopwatch.GetTimestamp());
        if (!_built || _magHwnd == IntPtr.Zero) return;
        _watch.Requested();
        if (!GlassNative.MagSetWindowSource(_magHwnd, new RECT(_src.X, _src.Y, _src.X + _src.Width, _src.Y + _src.Height)))
        {
            Fail("magnifier source");
            return;
        }
        GlassNative.InvalidateRect(_magHwnd, IntPtr.Zero, true);
        CheckHealth();
    }

    public void Poke()
    {
        _pacing.Changed();
        MagnifierHost.Wake();
    }

    // ------------------------------------------------------------------ build / release

    void Build()
    {
        if (_built || _disposed) return;
        try
        {
            var d = _dev.Device;
            using (var factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false))
                _swap = factory.CreateSwapChainForComposition(d, new SwapChainDescription1
                {
                    Width = (uint)_glass.Width, Height = (uint)_glass.Height, Format = Format.B8G8R8A8_UNorm, BufferCount = 2,
                    BufferUsage = Usage.RenderTargetOutput, SampleDescription = new SampleDescription(1, 0),
                    SwapEffect = SwapEffect.FlipSequential, AlphaMode = AlphaMode.Premultiplied, Scaling = Scaling.Stretch,
                });
            using (var back = _swap.GetBuffer<ID3D11Texture2D>(0)) _rtv = d.CreateRenderTargetView(back);
            Visual.SetContent(_swap).CheckError();
            _dcomp.Commit().CheckError();
            _srcTex = d.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)_src.Width, (uint)_src.Height, 1, 1, BindFlags.ShaderResource));
            _srv = d.CreateShaderResourceView(_srcTex);
            _previous = null;
            // the magnifier ignores windows that are hidden when its filter list is set: show the glass first
            GlassNative.ShowWindowAsync(_glassHwnd, 4 /* SW_SHOWNOACTIVATE */);
            var exclude = Exclusions();
            MagnifierHost.Invoke(() => (_hostHwnd, _magHwnd) = MagnifierHost.CreateMagnifier(_src, exclude));
            Place();
            MagnifierHost.Invoke(() => GlassNative.ShowWindow(_hostHwnd, 4));
            var item = GlassNative.CaptureForWindow(_hostHwnd);
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_dev.WinRt, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
            _pool.FrameArrived += OnFrame;
            _session = _pool.CreateCaptureSession(item);
            _session.IsCursorCaptureEnabled = false;
            _session.IsBorderRequired = false;
            _session.MinUpdateInterval = TimeSpan.FromMilliseconds(33);
            _session.StartCapture();
            _built = true;
            MagnifierHost.Add(this);
            Poke();
        }
        catch (Exception e)
        {
            Release();
            Fail("build: " + e.Message);
        }
    }

    void Release()
    {
        _built = false;
        MagnifierHost.Remove(this);
        if (_pool != null) _pool.FrameArrived -= OnFrame;
        lock (_dev.Gate)
        {
            _session?.Dispose(); _session = null;
            _pool?.Dispose(); _pool = null;
            _srv?.Dispose(); _srv = null;
            _srcTex?.Dispose(); _srcTex = null;
            _rtv?.Dispose(); _rtv = null;
            if (_swap != null)
            {
                Visual.SetContent(null);
                _dcomp.Commit();
                _swap.Dispose(); _swap = null;
            }
        }
        var host = _hostHwnd;
        _hostHwnd = _magHwnd = IntPtr.Zero;
        GlassNative.ShowWindowAsync(_glassHwnd, 0);
        try
        {
            if (host != IntPtr.Zero) MagnifierHost.Invoke(() => GlassNative.DestroyWindow(host));
        }
        catch (TimeoutException) { Core.Log.Write("real glass: magnifier thread busy on release"); }
    }

    /// <summary>Glass just below the owner, magnifier host just below the glass.</summary>
    void Place()
    {
        var (glass, src, host, owner) = (_glass, _src, _hostHwnd, _owner);
        // inserting after a topmost owner does not make a window topmost: join the owner's band first
        var band = (Win32.GetExStyle(owner) & 0x8 /* WS_EX_TOPMOST */) != 0 ? new IntPtr(-1) : new IntPtr(-2);
        const uint Keep = GlassNative.SWP_NOMOVE | GlassNative.SWP_NOSIZE | GlassNative.SWP_NOACTIVATE;
        GlassNative.SetWindowPos(_glassHwnd, band, 0, 0, 0, 0, Keep | GlassNative.SWP_ASYNCWINDOWPOS);
        GlassNative.SetWindowPos(_glassHwnd, owner, glass.X, glass.Y, glass.Width, glass.Height, GlassNative.SWP_NOACTIVATE | GlassNative.SWP_ASYNCWINDOWPOS);
        if (host != IntPtr.Zero)
            MagnifierHost.Invoke(() =>
            {
                GlassNative.SetWindowPos(host, band, 0, 0, 0, 0, Keep);
                GlassNative.SetWindowPos(host, _glassHwnd, src.X, src.Y, src.Width, src.Height, GlassNative.SWP_NOACTIVATE);
            });
    }

    IReadOnlyList<IntPtr> Exclusions()
    {
        var list = new List<IntPtr>(OurWindows()) { _owner };
        lock (StaticGate) foreach (var s in Live) list.Add(s._glassHwnd);
        return list;
    }

    /// <summary>Panels opened or closed: every magnifier gets the new list of our windows.</summary>
    public static void ExclusionsChanged()
    {
        List<GlassSurface> all;
        lock (StaticGate) all = Live.ToList();
        foreach (var s in all)
        {
            if (!s._built) continue;
            var ex = s.Exclusions();
            MagnifierHost.Invoke(() => { if (s._magHwnd != IntPtr.Zero) MagnifierHost.SetExclusions(s._magHwnd, s._hostHwnd, ex); });
        }
    }

    // ------------------------------------------------------------------ frames (capture thread)

    void OnFrame(Direct3D11CaptureFramePool pool, object _)
    {
        using var frame = pool.TryGetNextFrame();
        if (frame == null || !_built) return;
        try
        {
            var access = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
            using var tex = new ID3D11Texture2D(access.GetInterface(typeof(ID3D11Texture2D).GUID));
            Marshal.ReleaseComObject(access);
            lock (_dev.Gate)
            {
                if (!_built || _srcTex == null || _srv == null || _rtv == null || _swap == null) return;
                var ctx = _dev.Device.ImmediateContext;
                var box = new Box(0, 0, 0, Math.Min(_src.Width, frame.ContentSize.Width), Math.Min(_src.Height, frame.ContentSize.Height), 1);
                ctx.CopySubresourceRegion(_srcTex, 0, 0, 0, 0, tex, 0, box);
                _watch.Frame();
                if (NextSource is { } wanted) { NextSource = null; wanted.TrySetResult(Read(ctx, _srcTex, _src.Width, _src.Height)); }
                if (_dev.SameAsLast(_srv, _src.Width, _src.Height, ref _previous)) return;
                _dev.Draw(new GlassDraw(_rtv, _srv, _glass.Width, _glass.Height, _radius, _glass.X - _src.X, _glass.Y - _src.Y,
                    _src.Width, _src.Height, Style, Dark, Blur, Tint));
                _swap.Present(0, PresentFlags.None);
            }
        }
        catch (Exception e) { Fail("frame: " + e.Message); }
    }

    (byte[], int, int) Read(ID3D11DeviceContext ctx, ID3D11Texture2D texture, int w, int h)
    {
        using var staging = _dev.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)w, (uint)h, 1, 1,
            BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        ctx.CopyResource(staging, texture);
        var map = ctx.Map(staging, 0, MapMode.Read);
        var bytes = new byte[w * h * 4];
        for (int r = 0; r < h; r++) Marshal.Copy(map.DataPointer + (nint)(r * map.RowPitch), bytes, r * w * 4, w * 4);
        ctx.Unmap(staging, 0);
        return (bytes, w, h);
    }

    // ------------------------------------------------------------------ health

    void Fail(string why)
    {
        Core.Log.Write($"real glass: {why}");
        _watch.Failed(why);
        CheckHealth();
    }

    void CheckHealth()
    {
        var now = _watch.State;
        if (now == GlassHealth.Fallback && _watch.ShouldRetry)
        {
            Core.Log.Write("real glass: retrying");
            ThreadPool.QueueUserWorkItem(_ => { if (!_paused) { Release(); Build(); } });
            now = GlassHealth.Live;
        }
        if (now == _health) return;
        _health = now;
        if (now != GlassHealth.Live) ThreadPool.QueueUserWorkItem(_ => Release());
        HealthChanged?.Invoke(now);
    }

    /// <summary>Called by the owner's own timer while in fallback, so a retry can happen without any frames.</summary>
    public void Tick() => CheckHealth();

    /// <summary>Monitors added, removed or rearranged: every shown glass rebuilds its magnifier, capture and signal.</summary>
    static void DisplayChanged()
    {
        List<GlassSurface> all;
        lock (StaticGate) all = Live.ToList();
        foreach (var s in all)
            s._ui.BeginInvoke(() =>
            {
                if (s._disposed || s._paused) return;
                s.Hide();
                s._monitor = IntPtr.Zero; // the old handle may be gone
                s.SetShape(s._glass, s._radius);
                s.Show();
            });
    }

    // ------------------------------------------------------------------ change wake-ups

    static void OnWindowChange(Int32Rect? rect)
    {
        List<GlassSurface> all;
        lock (StaticGate) all = Live.ToList();
        foreach (var s in all)
            if (!s.Paused && (rect is not { } r || GlassPacing.Relevant(r, s._src, Array.Empty<Int32Rect>())))
                s.Poke();
    }

    void AcquireSignal()
    {
        if (_monitor == IntPtr.Zero) return;
        lock (StaticGate)
        {
            if (Signals.TryGetValue(_monitor, out var e)) { Signals[_monitor] = (e.Signal, e.Count + 1); return; }
            ChangeSignal? signal = null;
            var mon = _monitor;
            try { signal = new ChangeSignal(mon, _dev.WinRt, OurRects, r => OnDirty(mon, r)); }
            catch (Exception ex) { Core.Log.Write($"real glass: change signal unavailable ({ex.Message}); window events only"); }
            Signals[_monitor] = (signal, 1);
        }
    }

    void ReleaseSignal()
    {
        if (_monitor == IntPtr.Zero) return;
        lock (StaticGate)
        {
            if (!Signals.TryGetValue(_monitor, out var e)) return;
            if (e.Count > 1) { Signals[_monitor] = (e.Signal, e.Count - 1); return; }
            e.Signal?.Dispose();
            Signals.Remove(_monitor);
        }
    }

    static void OnDirty(IntPtr monitor, Int32Rect r)
    {
        List<GlassSurface> all;
        lock (StaticGate) all = Live.ToList();
        foreach (var s in all)
            if (s._monitor == monitor && !s.Paused && GlassPacing.Relevant(r, s._src, Array.Empty<Int32Rect>()))
                s.Poke();
    }

    /// <summary>Rects of all our windows (owners, glass, magnifier hosts): their repaints are not news.</summary>
    static IReadOnlyList<Int32Rect> OurRects()
    {
        var list = new List<Int32Rect>();
        List<GlassSurface> all;
        lock (StaticGate) all = Live.ToList();
        foreach (var s in all) { list.Add(s._src); list.Add(s._glass); }
        foreach (var h in OurWindows())
            if (Win32.GetWindowRect(h, out var r)) list.Add(new Int32Rect(r.Left, r.Top, Math.Max(0, r.Width), Math.Max(0, r.Height)));
        return list;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Hide();
        _disposed = true;
        Release();
        lock (StaticGate)
        {
            Live.Remove(this);
            if (Live.Count == 0) WindowEvents.Stop();
        }
        _target.Dispose(); Visual.Dispose(); _dcomp.Dispose();
        // DestroyWindow only works on the creating thread; from anywhere else ask that thread to close it
        if (!GlassNative.DestroyWindow(_glassHwnd)) GlassNative.PostMessage(_glassHwnd, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
    }
}
