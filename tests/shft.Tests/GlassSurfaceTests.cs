using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Shft.Native.Glass;
using Xunit;

namespace Shft.Tests;

public class GlassSurfaceTests
{
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern IntPtr FindWindow(string? cls, string title);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetTopWindow(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);

    [Fact]
    public void Shape_keeps_source_inside_monitor()
    {
        // Dock at the very bottom of the portrait monitor left of the main one (user's layout)
        var monitor = new Int32Rect(-1080, -394, 1080, 1920);
        var dock = new Int32Rect(-900, 1526 - 70, 720, 66);
        var src = GlassGeometry.SourceRect(dock, 48, monitor);
        Assert.True(src.X >= monitor.X && src.Y >= monitor.Y);
        Assert.True(src.X + src.Width <= monitor.X + monitor.Width && src.Y + src.Height <= monitor.Y + monitor.Height);
        Assert.Equal(66 + 96, src.Height);
    }

    [Fact]
    public void Live_frame_has_no_owner_pixels_and_no_glass()
    {
        if (!Environment.UserInteractive) return; // needs an interactive desktop
        using var dev = GlassDevice.TryCreate(out _);
        if (dev == null) return; // no GPU

        // the background is another process, like every window the glass refracts in real use
        // (the magnifier does not show this process's own WPF windows at all)
        using var background = Process.Start(new ProcessStartInfo("powershell.exe",
            "-NoProfile -Command \"Add-Type -A System.Windows.Forms; $f=New-Object Windows.Forms.Form; $f.FormBorderStyle='None'; " +
            "$f.StartPosition='Manual'; $f.Location='200,200'; $f.Size='400,200'; $f.BackColor='Blue'; $f.TopMost=$true; " +
            "$f.ShowInTaskbar=$false; $f.Text='shft-glass-test-bg'; [Windows.Forms.Application]::Run($f)\"") { UseShellExecute = false, CreateNoWindow = true })!;
        Dispatcher? ui = null;
        IntPtr owner = IntPtr.Zero;
        var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            var red = new Window
            {
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false,
                Topmost = true, Background = Brushes.Red, Left = 250, Top = 250, Width = 300, Height = 100,
            };
            red.Show();
            owner = new WindowInteropHelper(red).Handle;
            ui = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try
        {
            Assert.True(ready.Wait(5000));
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (FindWindow(null, "shft-glass-test-bg") == IntPtr.Zero && DateTime.UtcNow < deadline) Thread.Sleep(100);
            Assert.NotEqual(IntPtr.Zero, FindWindow(null, "shft-glass-test-bg"));
            Thread.Sleep(300); // first paint

            // like the app: the surface is made and driven from the owner's UI thread
            string? why = null;
            var surface = ui!.Invoke(() => GlassSurface.TryCreate(owner, dev, out why));
            Assert.True(surface != null, why);
            try
            {
                // the red owner sits at 250,250 (100% DPI assumed for the test windows)
                ui.Invoke(() => { surface!.SetShape(new Int32Rect(250, 250, 300, 100), 20); surface.Show(); });
                // the first frames can arrive before the magnifier painted: wait for the background, then keep
                // looking — if the glass saw itself the area under it would turn grey and darken frame after frame
                // the glass must really be on screen above the background, or this proves nothing
                Thread.Sleep(200);
                var bg = FindWindow(null, "shft-glass-test-bg");
                int glassAt = -1, bgAt = -1, k = 0;
                for (var hw = GetTopWindow(IntPtr.Zero); hw != IntPtr.Zero; hw = GetWindow(hw, 2 /* GW_HWNDNEXT */), k++)
                {
                    if (hw == surface!.GlassHwnd) glassAt = k;
                    if (hw == bg) bgAt = k;
                }
                Assert.True(glassAt >= 0 && glassAt < bgAt, $"glass is not above the background (glass {glassAt}, background {bgAt})");
                int good = 0;
                string seen = "";
                for (int attempt = 0; attempt < 30 && good < 6; attempt++)
                {
                    var next = new TaskCompletionSource<(byte[] Pixels, int Width, int Height)>(TaskCreationOptions.RunContinuationsAsynchronously);
                    surface.NextSource = next;
                    surface.Poke();
                    Assert.True(next.Task.Wait(5000), "no frame from the magnifier");
                    var (px, w, h) = next.Task.Result;
                    int red = 0, blueAll = 0, blueUnder = 0, under = 0;
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                        {
                            int i = (y * w + x) * 4;
                            byte b = px[i], g = px[i + 1], r = px[i + 2];
                            bool isBlue = b > 200 && r < 60 && g < 60;
                            if (r > 200 && g < 60 && b < 60) red++;
                            if (isBlue) blueAll++;
                            if (x >= 48 && x < 348 && y >= 48 && y < 148) { under++; if (isBlue) blueUnder++; }
                        }
                    Assert.Equal(0, red);
                    seen += $" {blueUnder * 100 / under}%";
                    if (good == 0 && blueAll == 0) continue; // magnifier not painted yet
                    Assert.True(blueUnder > under * 95 / 100, $"glass or something else under the glass ({seen})");
                    good++;
                }
                Assert.True(good >= 6, $"background never showed ({seen})");
            }
            finally { ui.Invoke(() => surface!.Dispose()); }
        }
        finally
        {
            ui?.InvokeShutdown();
            thread.Join(2000);
            if (!background.HasExited) background.Kill();
        }
    }
}
