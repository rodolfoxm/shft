using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Box = Vortice.Mathematics.Box;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;
using Rect = System.Windows.Rect;

namespace GlassBench;

[StructLayout(LayoutKind.Sequential)]
unsafe struct Params
{
    public float SizeX, SizeY, Radius, Height;
    public float Amount, BlurRadius, Mode, Dark;
    public float OffX, OffY, SrcW, SrcH;
    public fixed float Masks[24 * 4];
    public float MaskCount, P1, P2, P3;
}

/// <summary>One D3D11 device for every surface; the immediate context is used under one lock.</summary>
static class Gpu
{
    public static readonly object Gate = new();
    public static ID3D11Device Device = null!;
    public static ID3D11DeviceContext Ctx = null!;
    public static ID3D11VertexShader Vs = null!;
    public static ID3D11PixelShader Ps = null!, PsCopy = null!;
    public static ID3D11Buffer Cb = null!;
    public static ID3D11SamplerState Sampler = null!;
    public static IDirect3DDevice WinRtDevice = null!;

    public static void Init()
    {
        ID3D11Device dev; ID3D11DeviceContext ctx;
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, new[] { FeatureLevel.Level_11_0 }, out dev, out ctx).CheckError();
        Device = dev; Ctx = ctx;
        var hlsl = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Glass.hlsl"));
        Vs = Device.CreateVertexShader(Compiler.Compile(hlsl, "VS", "Glass.hlsl", "vs_5_0").Span);
        Ps = Device.CreatePixelShader(Compiler.Compile(hlsl, "PS", "Glass.hlsl", "ps_5_0").Span);
        PsCopy = Device.CreatePixelShader(Compiler.Compile(hlsl, "PSCopy", "Glass.hlsl", "ps_5_0").Span);
        Cb = Device.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<Params>(), BindFlags.ConstantBuffer));
        Sampler = Device.CreateSamplerState(SamplerDescription.LinearClamp);
        using var dxgi = Device.QueryInterface<IDXGIDevice>();
        Native.CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var insp);
        WinRtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(insp);
        Marshal.Release(insp);
    }
}

/// <summary>Per-phase counters of one surface.</summary>
sealed class Stats
{
    public int Arrived, Rendered, Skipped;
    public readonly List<double> CaptureLatency = new(), RefreshLatency = new(), RenderMs = new();
    public void Reset() { lock (this) { Arrived = Rendered = Skipped = 0; CaptureLatency.Clear(); RefreshLatency.Clear(); RenderMs.Clear(); } }
}

/// <summary>A glass surface under a fake Dock: its own window, swap chain and source (monitor capture or magnifier).</summary>
sealed class Surface
{
    public const int Margin = 48;
    readonly string _source;
    readonly int _fps;
    readonly bool _skip;
    readonly string _hostPos;
    public readonly Native.RECT Monitor;
    public readonly int GlassX, GlassY, SrcW, SrcH;
    public Window DockWindow = null!;
    public IntPtr DockHwnd, GlassHwnd, HostHwnd, MagHwnd;
    public readonly Stats Stats = new();
    public string Name;
    public volatile bool Paused;
    public volatile string DumpName;
    public string Out;
    float[] _masks = Array.Empty<float>();

    IDXGISwapChain1 _swap;
    ID3D11RenderTargetView _rtv;
    ID3D11Texture2D _srcTex, _small, _smallStaging;
    ID3D11ShaderResourceView _srv;
    ID3D11RenderTargetView _smallRtv;
    byte[] _prev;
    IDCompositionDevice _dcomp;
    IDCompositionTarget _target;
    IDCompositionVisual _visual;
    GraphicsCaptureSession _session;
    Direct3D11CaptureFramePool _pool;
    Thread _magThread;
    readonly AutoResetEvent _magWake = new(false);
    long _lastRefreshTicks, _lastChangeTicks;
    public bool Adaptive;
    public bool EventDriven;
    public int IdleSignalMs = 100;
    public readonly System.Collections.Concurrent.ConcurrentQueue<long> RenderTimes = new();
    bool _signalFast;
    public int SignalFrames, SignalHits;
    GraphicsCaptureSession _signal;
    Direct3D11CaptureFramePool _signalPool;
    volatile bool _quit;

    public Surface(string name, Native.RECT monitor, string source, int fps, bool skip, string hostPos, string outDir)
    {
        Name = name; Monitor = monitor; _source = source; _fps = fps; _skip = skip; _hostPos = hostPos; Out = outDir;
        int dockX = monitor.L + (monitor.R - monitor.L - Scene.DockW) / 2, dockY = monitor.B - Scene.DockH - 6;
        GlassX = dockX + Scene.BarX; GlassY = dockY + Scene.BarY;
        SrcW = Scene.BarW + 2 * Margin; SrcH = Scene.BarH + 2 * Margin;
        DockWindow = Scene.Dock(out var tiles);
        var list = new List<float>();
        foreach (var r in tiles) list.AddRange(new[] { (float)(r.X - Scene.BarX), (float)(r.Y - Scene.BarY), (float)r.Width, (float)r.Height });
        _masks = list.ToArray();
        DockWindow.Show();
        DockHwnd = new WindowInteropHelper(DockWindow).Handle;
        Native.SetWindowPos(DockHwnd, Native.HWND_TOPMOST, dockX, dockY, Scene.DockW, Scene.DockH, Native.SWP_NOACTIVATE);
        GlassHwnd = CreateGlassWindow(GlassX, GlassY, Scene.BarW, Scene.BarH);
        if (_source == "wgc") Program.Log($"{Name}: glass excluded from capture: {Native.SetWindowDisplayAffinity(GlassHwnd, 0x11)}");
        Native.SetWindowPos(GlassHwnd, DockHwnd, 0, 0, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOMOVE | Native.SWP_NOACTIVATE);
        InitGraphics();
    }

    static Native.WndProc _proc;
    static bool _registered;

    public static IntPtr CreateLayerWindow(int x, int y, int w, int h) => CreateGlassWindow(x, y, w, h);

    static IntPtr CreateGlassWindow(int x, int y, int w, int h)
    {
        if (!_registered)
        {
            _proc = (hh, m, wp, lp) => Native.DefWindowProc(hh, m, wp, lp);
            var cls = new Native.WNDCLASSEX { cbSize = Marshal.SizeOf<Native.WNDCLASSEX>(), lpfnWndProc = _proc, cls = "GlassSpike2Layer", hInst = Marshal.GetHINSTANCE(typeof(Surface).Module) };
            Native.RegisterClassEx(ref cls);
            _registered = true;
        }
        const uint WS_POPUP = 0x80000000, EX_NOREDIR = 0x00200000, EX_TOOL = 0x80, EX_TOPMOST = 0x8, EX_NOACT = 0x08000000, EX_TRANSP = 0x20, EX_LAYERED = 0x80000;
        var hwnd = Native.CreateWindowEx(EX_NOREDIR | EX_TOOL | EX_TOPMOST | EX_NOACT | EX_TRANSP | EX_LAYERED, "GlassSpike2Layer", "glass", WS_POPUP, x, y, w, h, IntPtr.Zero, IntPtr.Zero, Marshal.GetHINSTANCE(typeof(Surface).Module), IntPtr.Zero);
        Native.ShowWindow(hwnd, 4);
        return hwnd;
    }

    void InitGraphics()
    {
        var d = Gpu.Device;
        using var dxgiDevice = d.QueryInterface<IDXGIDevice>();
        using var factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
        _swap = factory.CreateSwapChainForComposition(d, new SwapChainDescription1
        {
            Width = Scene.BarW, Height = Scene.BarH, Format = Format.B8G8R8A8_UNorm, BufferCount = 2, BufferUsage = Usage.RenderTargetOutput,
            SampleDescription = new SampleDescription(1, 0), SwapEffect = SwapEffect.FlipSequential, AlphaMode = AlphaMode.Premultiplied, Scaling = Scaling.Stretch,
        });
        using (var back = _swap.GetBuffer<ID3D11Texture2D>(0)) _rtv = d.CreateRenderTargetView(back);
        _dcomp = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
        _dcomp.CreateTargetForHwnd(GlassHwnd, true, out _target).CheckError();
        _dcomp.CreateVisual(out _visual).CheckError();
        _visual.SetContent(_swap).CheckError();
        _target.SetRoot(_visual).CheckError();
        _dcomp.Commit().CheckError();
        _srcTex = d.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)SrcW, (uint)SrcH, 1, 1, BindFlags.ShaderResource));
        _srv = d.CreateShaderResourceView(_srcTex);
        _small = d.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)(SrcW / 8), (uint)(SrcH / 8), 1, 1, BindFlags.RenderTarget));
        _smallRtv = d.CreateRenderTargetView(_small);
        _smallStaging = d.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)(SrcW / 8), (uint)(SrcH / 8), 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
    }

    // ------------------------------------------------------------------ sources

    public void Start(IntPtr[] allOurs)
    {
        if (_source == "wgc") StartMonitorCapture();
        else StartMagnifier(allOurs);
    }

    void StartMonitorCapture()
    {
        var hmon = Native.MonitorFromPoint(new System.Drawing.Point(GlassX + 10, GlassY + 10), 2);
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var iid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        var ptr = interop.CreateForMonitor(hmon, ref iid);
        var item = GraphicsCaptureItem.FromAbi(ptr);
        Marshal.Release(ptr);
        OpenSession(item);
    }

    void OpenSession(GraphicsCaptureItem item)
    {
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(Gpu.WinRtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
        _pool.FrameArrived += (p, _) => OnFrame(p);
        _session = _pool.CreateCaptureSession(item);
        _session.IsCursorCaptureEnabled = false;
        _session.IsBorderRequired = false;
        _session.MinUpdateInterval = TimeSpan.FromMilliseconds(1000.0 / _fps);
        _session.StartCapture();
    }

    void StartMagnifier(IntPtr[] allOurs)
    {
        MagHost.Invoke(() =>
        {
            const uint WS_POPUP = 0x80000000, WS_VISIBLE = 0x10000000, WS_CHILD = 0x40000000, EX_LAYERED = 0x80000, EX_TOOL = 0x80, EX_NOACT = 0x08000000, EX_TRANSP = 0x20, EX_TOPMOST = 0x8;
            int hx = SrcLeft, hy = SrcTop;
            HostHwnd = Native.CreateWindowEx(EX_LAYERED | EX_TOOL | EX_NOACT | EX_TRANSP | EX_TOPMOST, "GlassSpike2Layer", "magnifier host", WS_POPUP | WS_VISIBLE, hx, hy, SrcW, SrcH, IntPtr.Zero, IntPtr.Zero, Marshal.GetHINSTANCE(typeof(Surface).Module), IntPtr.Zero);
            Native.SetLayeredWindowAttributes(HostHwnd, 0, (byte)(_hostPos.StartsWith("under1", StringComparison.Ordinal) ? 1 : 255), 2);
            MagHwnd = Native.CreateWindowEx(0, "Magnifier", "mag", WS_CHILD | WS_VISIBLE, 0, 0, SrcW, SrcH, HostHwnd, IntPtr.Zero, Marshal.GetHINSTANCE(typeof(Surface).Module), IntPtr.Zero);
            Program.Log($"{Name}: magnifier control {(MagHwnd != IntPtr.Zero ? "created" : "FAILED " + Marshal.GetLastWin32Error())} (host at {hx},{hy})");
            var tr = new Native.MAGTRANSFORM { a = 1, e = 1, i = 1 };
            Native.MagSetWindowTransform(MagHwnd, ref tr);
            var exclude = allOurs.Concat(new[] { HostHwnd }).ToArray();
            Program.Log($"{Name}: filter list ({exclude.Length} windows): {Native.MagSetWindowFilterList(MagHwnd, 0, exclude.Length, exclude)}");
            Native.SetWindowPos(HostHwnd, GlassHwnd, 0, 0, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOMOVE | Native.SWP_NOACTIVATE);
            Refresh();
        });
        MagHost.Add(this);
        OpenHostCapture();
    }

    /// <summary>When the magnifier should be asked for a new picture: at the frame rate, or 6 times a second after a second with no change.</summary>
    public long NextRefresh
    {
        get
        {
            long last = Interlocked.Read(ref _lastRefreshTicks);
            if (EventDriven)
            {
                // ask only while the region under the glass is changing (250 ms after the last change), else a check every 2 s
                bool active = Stopwatch.GetTimestamp() - Interlocked.Read(ref _signalTicks) < Stopwatch.Frequency * 4 / 10;
                SetSignalFast(active);
                return last + (active ? Stopwatch.Frequency / _fps : Stopwatch.Frequency * 2);
            }
            bool idle = Adaptive && Stopwatch.GetTimestamp() - Interlocked.Read(ref _lastChangeTicks) > Stopwatch.Frequency;
            return last + Stopwatch.Frequency / (idle ? 6 : _fps);
        }
    }

    long _signalTicks;

    /// <summary>
    /// A monitor capture used only as a change signal: Windows reports which rectangles changed (dirty regions) and
    /// the pixels are never touched. A change that touches the area under the glass wakes the magnifier at once.
    /// </summary>
    void StartSignal()
    {
        var hmon = Native.MonitorFromPoint(new System.Drawing.Point(GlassX + 10, GlassY + 10), 2);
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var iid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        var ptr = interop.CreateForMonitor(hmon, ref iid);
        var item = GraphicsCaptureItem.FromAbi(ptr);
        Marshal.Release(ptr);
        _signalPool = Direct3D11CaptureFramePool.CreateFreeThreaded(Gpu.WinRtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, item.Size);
        var area = new Native.RECT(SrcLeft - Monitor.L, SrcTop - Monitor.T, SrcLeft - Monitor.L + SrcW, SrcTop - Monitor.T + SrcH);
        _signalPool.FrameArrived += (p, _) =>
        {
            using var f = p.TryGetNextFrame();
            if (f == null || Paused) return;
            Interlocked.Increment(ref SignalFrames);
            foreach (var r in f.DirtyRegions)
            {
                // our own windows (magnifier host, glass, Dock) repaint inside this area: those are not news
                bool ours = r.X >= area.L - 1 && r.Y >= area.T - 1 && r.X + r.Width <= area.R + 1 && r.Y + r.Height <= area.B + 1;
                if (ours) continue;
                if (r.X < area.R && r.X + r.Width > area.L && r.Y < area.B && r.Y + r.Height > area.T)
                {
                    Interlocked.Increment(ref SignalHits);
                    Poke();
                    break;
                }
            }
        };
        _signal = _signalPool.CreateCaptureSession(item);
        _signal.IsCursorCaptureEnabled = false;
        _signal.IsBorderRequired = false;
        _signal.DirtyRegionMode = GraphicsCaptureDirtyRegionMode.ReportOnly;
        _signal.MinUpdateInterval = TimeSpan.FromMilliseconds(IdleSignalMs);
        _signal.StartCapture();
        Program.Log($"{Name}: change signal on (dirty regions of the monitor)");
    }

    /// <summary>Something relevant changed behind the glass: go to full rate now.</summary>
    public void Poke()
    {
        Interlocked.Exchange(ref _signalTicks, Stopwatch.GetTimestamp());
        MagHost.Wake();
    }

    public Native.RECT ScreenArea => new(SrcLeft, SrcTop, SrcLeft + SrcW, SrcTop + SrcH);

    /// <summary>The change signal itself slows to the idle rate when nothing moves behind the glass.</summary>
    void SetSignalFast(bool fast)
    {
        if (_signal == null || fast == _signalFast) return;
        _signalFast = fast;
        try { _signal.MinUpdateInterval = TimeSpan.FromMilliseconds(fast ? 1000.0 / _fps : IdleSignalMs); }
        catch (Exception ex) { Program.Log("signal rate not changed: " + ex.Message); }
    }

    void OpenHostCapture()
    {
        if (EventDriven && _signal == null) StartSignal();
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var iid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        var ptr = interop.CreateForWindow(HostHwnd, ref iid);
        var item = GraphicsCaptureItem.FromAbi(ptr);
        Marshal.Release(ptr);
        Program.Log($"{Name}: host capture item {item.Size.Width}x{item.Size.Height}");
        OpenSession(item);
    }

    int SrcLeft => Math.Clamp(GlassX - Margin, Monitor.L, Monitor.R - SrcW);
    int SrcTop => Math.Clamp(GlassY - Margin, Monitor.T, Monitor.B - SrcH);

    public void Refresh()
    {
        Native.MagSetWindowSource(MagHwnd, new Native.RECT(SrcLeft, SrcTop, SrcLeft + SrcW, SrcTop + SrcH));
        Native.InvalidateRect(MagHwnd, IntPtr.Zero, true);
        Interlocked.Exchange(ref _lastRefreshTicks, Stopwatch.GetTimestamp());
    }

    // ------------------------------------------------------------------ frames

    static TimeSpan Now => TimeSpan.FromTicks((long)(Stopwatch.GetTimestamp() * (10_000_000.0 / Stopwatch.Frequency)));

    void OnFrame(Direct3D11CaptureFramePool pool)
    {
        using var frame = pool.TryGetNextFrame();
        if (frame == null || Paused) return;
        double captureLatency = (Now - frame.SystemRelativeTime).TotalMilliseconds;
        double refreshLatency = _source == "mag" ? (Stopwatch.GetTimestamp() - Interlocked.Read(ref _lastRefreshTicks)) * 1000.0 / Stopwatch.Frequency : 0;
        var sw = Stopwatch.StartNew();
        var surface = frame.Surface;
        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        var iid = typeof(ID3D11Texture2D).GUID;
        using var tex = new ID3D11Texture2D(access.GetInterface(ref iid));
        Marshal.ReleaseComObject(access);
        (surface as IDisposable)?.Dispose();
        bool changed = true;
        lock (Gpu.Gate)
        {
            var ctx = Gpu.Ctx;
            if (_source == "wgc")
            {
                int left = GlassX - Margin - Monitor.L, top = GlassY - Margin - Monitor.T;
                var box = new Box(Math.Max(0, left), Math.Max(0, top), 0, Math.Min(frame.ContentSize.Width, left + SrcW), Math.Min(frame.ContentSize.Height, top + SrcH), 1);
                ctx.CopySubresourceRegion(_srcTex, 0, (uint)(box.Left - left), (uint)(box.Top - top), 0, tex, 0, box);
            }
            else
            {
                var box = new Box(0, 0, 0, Math.Min(SrcW, frame.ContentSize.Width), Math.Min(SrcH, frame.ContentSize.Height), 1);
                ctx.CopySubresourceRegion(_srcTex, 0, 0, 0, 0, tex, 0, box);
            }
            ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            ctx.VSSetShader(Gpu.Vs);
            ctx.PSSetShaderResource(0, _srv);
            ctx.PSSetSampler(0, Gpu.Sampler);
            if (_skip)
            {
                ctx.OMSetRenderTargets(_smallRtv);
                ctx.RSSetViewport(new Vortice.Mathematics.Viewport(SrcW / 8, SrcH / 8));
                ctx.PSSetShader(Gpu.PsCopy);
                ctx.Draw(3, 0);
                ctx.CopyResource(_smallStaging, _small);
                var map = ctx.Map(_smallStaging, 0, MapMode.Read);
                int rowBytes = SrcW / 8 * 4, rows = SrcH / 8;
                var now = new byte[rowBytes * rows];
                for (int r = 0; r < rows; r++) Marshal.Copy(map.DataPointer + (nint)(r * map.RowPitch), now, r * rowBytes, rowBytes);
                ctx.Unmap(_smallStaging, 0);
                changed = _prev == null || !now.AsSpan().SequenceEqual(_prev) || DumpName != null;
                _prev = now;
            }
            if (changed)
            {
                Render(ctx);
                long ts = Stopwatch.GetTimestamp();
                Interlocked.Exchange(ref _lastChangeTicks, ts);
                RenderTimes.Enqueue(ts);
                while (RenderTimes.Count > 400) RenderTimes.TryDequeue(out _);
            }
        }
        sw.Stop();
        lock (Stats)
        {
            Stats.Arrived++;
            if (changed) { Stats.Rendered++; Stats.RenderMs.Add(sw.Elapsed.TotalMilliseconds); Stats.CaptureLatency.Add(captureLatency); if (_source == "mag") Stats.RefreshLatency.Add(refreshLatency); }
            else Stats.Skipped++;
        }
    }

    unsafe void Render(ID3D11DeviceContext ctx)
    {
        var prm = new Params { SizeX = Scene.BarW, SizeY = Scene.BarH, Radius = Scene.BarR, Height = 18, Amount = 14, BlurRadius = 14, Mode = _source == "wgc" ? 1 : 0, Dark = 1, OffX = _source == "mag" ? GlassX - SrcLeft : Margin, OffY = _source == "mag" ? GlassY - SrcTop : Margin, SrcW = SrcW, SrcH = SrcH };
        for (int i = 0; i < _masks.Length && i < 96; i++) prm.Masks[i] = _masks[i];
        prm.MaskCount = _masks.Length / 4;
        ctx.UpdateSubresource(in prm, Gpu.Cb);
        ctx.OMSetRenderTargets(_rtv);
        ctx.RSSetViewport(new Vortice.Mathematics.Viewport(Scene.BarW, Scene.BarH));
        ctx.PSSetShader(Gpu.Ps);
        ctx.PSSetConstantBuffer(0, Gpu.Cb);
        ctx.Draw(3, 0);
        var name = DumpName;
        if (name != null)
        {
            DumpName = null;
            using (var back = _swap.GetBuffer<ID3D11Texture2D>(0)) Save(ctx, back, Scene.BarW, Scene.BarH, $"{Name}-glass-{name}");
            Save(ctx, _srcTex, SrcW, SrcH, $"{Name}-source-{name}");
        }
        _swap.Present(0, PresentFlags.None);
    }

    void Save(ID3D11DeviceContext ctx, ID3D11Texture2D texture, int w, int h, string name)
    {
        using var staging = Gpu.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)w, (uint)h, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        ctx.CopyResource(staging, texture);
        var map = ctx.Map(staging, 0, MapMode.Read);
        var bytes = new byte[w * h * 4];
        for (int r = 0; r < h; r++) Marshal.Copy(map.DataPointer + (nint)(r * map.RowPitch), bytes, r * w * 4, w * 4);
        ctx.Unmap(staging, 0);
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, bytes, w * 4);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var f = File.Create(Path.Combine(Out, name + ".png"));
        enc.Save(f);
    }

    // ------------------------------------------------------------------ full screen

    public void Pause()
    {
        if (Paused) return;
        Paused = true;
        _session?.Dispose(); _session = null;
        _pool?.Dispose(); _pool = null;
        _signal?.Dispose(); _signal = null;
        _signalPool?.Dispose(); _signalPool = null;
        Native.ShowWindow(GlassHwnd, 0);
        DockWindow.Hide();
        Program.Log($"{Name}: paused (full screen in front): capture closed, glass hidden");
    }

    public void Resume()
    {
        if (!Paused) return;
        Paused = false;
        DockWindow.Show();
        Native.ShowWindow(GlassHwnd, 4);
        Native.SetWindowPos(GlassHwnd, DockHwnd, 0, 0, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOMOVE | Native.SWP_NOACTIVATE);
        _prev = null;
        if (_source == "wgc") StartMonitorCapture();
        else { MagHost.Wake(); OpenHostCapture(); }
        Program.Log($"{Name}: resumed");
    }

    public void Stop()
    {
        _quit = true;
        if (HostHwnd != IntPtr.Zero) MagHost.Invoke(() => Native.DestroyWindow(HostHwnd));
        _session?.Dispose();
        _pool?.Dispose();
        Native.DestroyWindow(GlassHwnd);
        DockWindow.Close();
    }
}
