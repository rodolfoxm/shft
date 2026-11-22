using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using WinRT;
using Color = System.Windows.Media.Color;

namespace GlassBench;

/// <summary>
/// Proof that a glass window can follow a WPF panel's open/close animation frame by frame. The card has a magenta
/// edge and an opaque dark body over a black backdrop; the glass under it is solid cyan, clipped to the same rounded
/// shape and moved per frame (DirectComposition transform and opacity) from the card's animated values. Every
/// captured frame is scanned: any cyan outside the magenta edge is misalignment, measured in pixels.
/// </summary>
static class PanelSync
{
    const int FrameW = 340, FrameH = 420, Pad = 30, Radius = 26;

    public static async Task Run(string outDir, int cycles, int delayFrames = 0)
    {
        var mon = Native.MonitorAt(10, 10);
        int winW = FrameW + 2 * Pad, winH = FrameH + 2 * Pad;
        int wx = mon.L + (mon.R - mon.L - winW) / 2, wy = mon.T + (mon.B - mon.T - winH) / 2;

        var black = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Topmost = true, ShowInTaskbar = false, ShowActivated = false, Background = Brushes.Black };
        black.Show();
        Native.SetWindowPos(new WindowInteropHelper(black).Handle, Native.HWND_TOPMOST, wx - 60, wy - 60, winW + 120, winH + 120, Native.SWP_NOACTIVATE);

        var scale = new ScaleTransform(1, 1);
        var move = new TranslateTransform();
        var frame = new Border
        {
            Width = FrameW, Height = FrameH, Margin = new Thickness(Pad), CornerRadius = new CornerRadius(Radius),
            Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x24)), BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x00, 0xFF)),
            BorderThickness = new Thickness(2), RenderTransformOrigin = new Point(0.5, 0), RenderTransform = new TransformGroup { Children = { scale, move } }, Opacity = 0,
        };
        var card = new Window { WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize, Topmost = true, ShowInTaskbar = false, ShowActivated = false, Content = frame };
        card.Show();
        var cardHwnd = new WindowInteropHelper(card).Handle;
        Native.SetWindowPos(cardHwnd, Native.HWND_TOPMOST, wx, wy, winW, winH, Native.SWP_NOACTIVATE);

        // the glass: a window exactly under the card's frame, DirectComposition visual with a rounded clip
        Gpu.Init();
        var glass = Surface.CreateLayerWindow(wx + Pad, wy + Pad, FrameW, FrameH);
        Native.SetWindowPos(glass, cardHwnd, 0, 0, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOMOVE | Native.SWP_NOACTIVATE);
        using var dxgiDevice = Gpu.Device.QueryInterface<IDXGIDevice>();
        using var factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
        using var swap = factory.CreateSwapChainForComposition(Gpu.Device, new SwapChainDescription1
        {
            Width = FrameW, Height = FrameH, Format = Format.B8G8R8A8_UNorm, BufferCount = 2, BufferUsage = Usage.RenderTargetOutput,
            SampleDescription = new SampleDescription(1, 0), SwapEffect = SwapEffect.FlipSequential, AlphaMode = AlphaMode.Premultiplied, Scaling = Scaling.Stretch,
        });
        using (var back = swap.GetBuffer<ID3D11Texture2D>(0))
        using (var rtv = Gpu.Device.CreateRenderTargetView(back))
        {
            lock (Gpu.Gate) Gpu.Ctx.ClearRenderTargetView(rtv, new Vortice.Mathematics.Color4(0, 1, 1, 1));
        }
        swap.Present(0, PresentFlags.None);
        var dcomp = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
        dcomp.CreateTargetForHwnd(glass, true, out var target).CheckError();
        dcomp.CreateVisual(out var visual).CheckError();
        visual.SetContent(swap).CheckError();
        dcomp.CreateRectangleClip(out var clip).CheckError();
        clip.SetLeft(0); clip.SetTop(0); clip.SetRight(FrameW); clip.SetBottom(FrameH);
        clip.SetTopLeftRadiusX(Radius); clip.SetTopLeftRadiusY(Radius); clip.SetTopRightRadiusX(Radius); clip.SetTopRightRadiusY(Radius);
        clip.SetBottomLeftRadiusX(Radius); clip.SetBottomLeftRadiusY(Radius); clip.SetBottomRightRadiusX(Radius); clip.SetBottomRightRadiusY(Radius);
        visual.SetClip(clip).CheckError();
        dcomp.CreateEffectGroup(out var effect).CheckError();
        effect.SetOpacity(0).CheckError();
        visual.SetEffect(effect).CheckError();
        target.SetRoot(visual).CheckError();
        dcomp.Commit().CheckError();

        // per frame: the card's animated values become the glass's transform and opacity
        int syncs = 0;
        double cardOpacity = 0;
        var queue = new Queue<(double, double, double, double)>();
        void Sync(object s, EventArgs e)
        {
            // WPF hands its frame to the compositor later than this commit: apply the values of N frames ago
            var now = (scale.ScaleX, scale.ScaleY, move.Y, frame.Opacity);
            // an animation starting from hidden is a jump, not motion: the glass takes it at once, then keeps the delay
            if (queue.Count > 0 && queue.Last().Item4 <= 0.001 && now.Item4 > 0.001) { queue.Clear(); for (int k = 0; k < delayFrames; k++) queue.Enqueue(now); }
            queue.Enqueue(now);
            if (queue.Count <= delayFrames) return;
            var (sx, sy, ty, o) = queue.Dequeue();
            cardOpacity = frame.Opacity;
            float ox = FrameW * 0.5f;
            var m = Matrix3x2.CreateTranslation(-ox, 0) * Matrix3x2.CreateScale((float)sx, (float)sy) * Matrix3x2.CreateTranslation(ox, (float)ty);
            visual.SetTransform(m);
            effect.SetOpacity((float)o);
            dcomp.Commit();
            syncs++;
        }
        CompositionTarget.Rendering += Sync;

        // capture the screen around the card and scan every frame
        int frames = 0, analysed = 0, bad = 0, fading = 0;
        double worst = 0;
        var hist = new int[8];
        int saved = 0;
        var hmon = Native.MonitorFromPoint(new System.Drawing.Point(wx + 5, wy + 5), 2);
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var iid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        var ptr = interop.CreateForMonitor(hmon, ref iid);
        var item = GraphicsCaptureItem.FromAbi(ptr);
        Marshal.Release(ptr);
        var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(Gpu.WinRtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
        using var staging = Gpu.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)winW, (uint)winH, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        var px = new byte[winW * winH * 4];
        pool.FrameArrived += (p, _) =>
        {
            using var f = p.TryGetNextFrame();
            if (f == null) return;
            Interlocked.Increment(ref frames);
            var surface = f.Surface;
            var access = surface.As<IDirect3DDxgiInterfaceAccess>();
            var tiid = typeof(ID3D11Texture2D).GUID;
            using var tex = new ID3D11Texture2D(access.GetInterface(ref tiid));
            Marshal.ReleaseComObject(access);
            lock (Gpu.Gate)
            {
                Gpu.Ctx.CopySubresourceRegion(staging, 0, 0, 0, 0, tex, 0, new Vortice.Mathematics.Box(wx - mon.L, wy - mon.T, 0, wx - mon.L + winW, wy - mon.T + winH, 1));
                var map = Gpu.Ctx.Map(staging, 0, MapMode.Read);
                for (int r = 0; r < winH; r++) Marshal.Copy(map.DataPointer + (nint)(r * map.RowPitch), px, r * winW * 4, winW * 4);
                Gpu.Ctx.Unmap(staging, 0);
            }
            // magenta edge box (any brightness: it fades with the card)
            int l = int.MaxValue, t = int.MaxValue, rr = -1, b = -1;
            for (int y = 0; y < winH; y++)
                for (int x = 0; x < winW; x++)
                {
                    int i = (y * winW + x) * 4;
                    byte B = px[i], G = px[i + 1], R = px[i + 2];
                    if (R > 40 && B > 40 && R > G * 3 && B > G * 3) { l = Math.Min(l, x); t = Math.Min(t, y); rr = Math.Max(rr, x); b = Math.Max(b, y); }
                }
            if (rr < 0 || b - t < 300) { Interlocked.Increment(ref fading); return; } // edge not fully visible yet (fading): not measurable
            double frameWorst = 0;
            for (int y = 0; y < winH; y++)
                for (int x = 0; x < winW; x++)
                {
                    if (x >= l && x <= rr && y >= t && y <= b) continue;
                    int i = (y * winW + x) * 4;
                    byte B = px[i], G = px[i + 1], R = px[i + 2];
                    if (G > 40 && B > 40 && G > R * 3 && B > R * 3)
                    {
                        double dx = Math.Max(0, Math.Max(l - x, x - rr)), dy = Math.Max(0, Math.Max(t - y, y - b));
                        frameWorst = Math.Max(frameWorst, Math.Max(dx, dy));
                    }
                }
            Interlocked.Increment(ref analysed);
            lock (hist)
            {
                worst = Math.Max(worst, frameWorst);
                hist[Math.Min(7, (int)Math.Ceiling(frameWorst))]++;
                if (frameWorst > 1) bad++;
                if (frameWorst > 6 && saved < 4)
                {
                    saved++;
                    int cyanCount = 0, magCount = 0;
                    for (int i = 0; i < px.Length; i += 4) { if (px[i + 1] > 40 && px[i] > 40 && px[i + 1] > px[i + 2] * 3) cyanCount++; if (px[i + 2] > 40 && px[i] > 40 && px[i + 2] > px[i + 1] * 3) magCount++; }
                    Program.Log($"outlier frame {analysed}: worst {frameWorst:0} px, magenta box {l},{t}-{rr},{b}, cyan px {cyanCount}, magenta px {magCount}, card opacity now {cardOpacity:0.00}");
                    var copy = (byte[])px.Clone();
                    for (int i = 3; i < copy.Length; i += 4) copy[i] = 255;
                    var bmp = System.Windows.Media.Imaging.BitmapSource.Create(winW, winH, 96, 96, PixelFormats.Bgra32, null, copy, winW * 4);
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                    using var fs = File.Create(Path.Combine(outDir, $"outlier-{saved}.png"));
                    enc.Save(fs);
                }
            }
        };
        var session = pool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = false;
        session.IsBorderRequired = false;
        session.StartCapture();

        var spring = new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut }; // overshoot, like shft.'s spring
        var outEase = new CubicEase { EasingMode = EasingMode.EaseOut };
        for (int c = 0; c < cycles; c++)
        {
            frame.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(130)) { EasingFunction = outEase });
            var grow = new DoubleAnimation(0.95, 1, TimeSpan.FromMilliseconds(300)) { EasingFunction = spring };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-8, 0, TimeSpan.FromMilliseconds(300)) { EasingFunction = spring });
            await Task.Delay(550);
            var shrink = new DoubleAnimation(0.97, TimeSpan.FromMilliseconds(100)) { EasingFunction = outEase };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
            frame.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(100)) { EasingFunction = outEase });
            await Task.Delay(350);
        }
        session.Dispose();
        pool.Dispose();
        CompositionTarget.Rendering -= Sync;
        await Task.Delay(200);
        Program.Log($"PANEL SYNC (delay {delayFrames} frames): {cycles} open/close cycles, {frames} captured frames, {analysed} measurable, {fading} mid-fade (edge not fully visible), {syncs} glass updates | worst misalignment {worst:0.0} px | frames over 1 px: {bad} | histogram px 0..7+: {string.Join(" ", hist)}");
        Native.DestroyWindow(glass);
        card.Close();
        black.Close();
    }
}
