using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;

namespace GlassBench;

/// <summary>Test scenes. They run in their own processes, so the glass process is measured alone.</summary>
static class Scene
{
    public const int DockW = 760, DockH = 120, BarX = 20, BarY = 46, BarW = 720, BarH = 66, BarR = 22;

    static void Place(Window w, int x, int y, int width, int height)
    {
        w.Show();
        var h = new WindowInteropHelper(w).Handle;
        Native.SetWindowPos(h, Native.HWND_TOPMOST, x, y, width, height, Native.SWP_NOACTIVATE);
    }

    /// <summary>Stripes, big text and a moving disc; named events stop and start the motion.</summary>
    public static void Background(int x, int y, int w, int h, string tag)
    {
        var win = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Topmost = true, ShowInTaskbar = false, ShowActivated = false };
        var canvas = new Canvas { Background = new LinearGradientBrush(Color.FromRgb(0x1E, 0x3A, 0x8A), Color.FromRgb(0xC0, 0x26, 0x6B), 0) };
        var stripes = new Canvas();
        for (int i = 0; i < 60; i++)
        {
            var c = (i % 3) switch { 0 => Colors.Gold, 1 => Colors.White, _ => Colors.LimeGreen };
            stripes.Children.Add(new Rectangle { Width = 14, Height = h, Fill = new SolidColorBrush(c), RenderTransform = new TranslateTransform(i * 40, 0), Opacity = 0.85 });
        }
        var move = new TranslateTransform();
        stripes.RenderTransform = move;
        canvas.Children.Add(stripes);
        var text = new TextBlock { Text = "shft. vidro 0123456789", FontSize = 64, FontWeight = FontWeights.Bold, Foreground = Brushes.Black };
        Canvas.SetLeft(text, 60); Canvas.SetTop(text, h - 170);
        canvas.Children.Add(text);
        var disc = new Ellipse { Width = 90, Height = 90, Fill = Brushes.White, Stroke = Brushes.Black, StrokeThickness = 6 };
        var discMove = new TranslateTransform(0, h - 120);
        disc.RenderTransform = discMove;
        canvas.Children.Add(disc);
        win.Content = canvas;
        bool on = true, justStarted = false;
        var start = DateTime.UtcNow;
        var mmf = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateOrOpen($"GlassBench.{tag}.Moved", 8);
        var view = mmf.CreateViewAccessor();
        void Tick(object s, EventArgs e)
        {
            if (justStarted) { justStarted = false; view.Write(0, System.Diagnostics.Stopwatch.GetTimestamp()); }
            double t = (DateTime.UtcNow - start).TotalSeconds;
            move.X = (t * 60) % 40 - 40;
            discMove.X = 100 + (Math.Sin(t * 1.3) + 1) * (w - 300) / 2;
        }
        CompositionTarget.Rendering += Tick;
        void Listen(string name, Action act) => new Thread(() =>
        {
            using var ev = new EventWaitHandle(false, EventResetMode.AutoReset, $"GlassBench.{tag}.{name}");
            while (ev.WaitOne()) win.Dispatcher.Invoke(act);
        }) { IsBackground = true }.Start();
        Listen("Stop", () => { if (on) { on = false; CompositionTarget.Rendering -= Tick; } });
        Listen("Start", () => { if (!on) { on = true; justStarted = true; CompositionTarget.Rendering += Tick; } });
        Listen("Quit", () => Application.Current.Shutdown());
        Place(win, x, y, w, h);
    }

    /// <summary>A small animated window far from the glass (a clock, a chat, a video elsewhere on the screen).</summary>
    public static void Blink(int x, int y, string tag)
    {
        var win = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Topmost = true, ShowInTaskbar = false, ShowActivated = false, Background = Brushes.Black };
        var dot = new Ellipse { Width = 60, Height = 60, Fill = Brushes.Cyan };
        var move = new TranslateTransform();
        dot.RenderTransform = move;
        win.Content = new Canvas { Children = { dot } };
        var start = DateTime.UtcNow;
        CompositionTarget.Rendering += (_, _) => move.X = 70 + Math.Sin((DateTime.UtcNow - start).TotalSeconds * 4) * 60;
        new Thread(() =>
        {
            using var ev = new EventWaitHandle(false, EventResetMode.AutoReset, $"GlassBench.{tag}.BlinkQuit");
            ev.WaitOne();
            win.Dispatcher.Invoke(() => Application.Current.Shutdown());
        }) { IsBackground = true }.Start();
        Place(win, x, y, 200, 120);
    }

    /// <summary>A borderless window covering a whole monitor, animating like a game, for the given seconds.</summary>
    public static void FullScreen(int x, int y, int w, int h, int seconds)
    {
        var win = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Topmost = true, Background = Brushes.Black };
        var bar = new Rectangle { Width = 200, Height = h, Fill = Brushes.OrangeRed };
        var move = new TranslateTransform();
        bar.RenderTransform = move;
        win.Content = new Canvas { Children = { bar } };
        var start = DateTime.UtcNow;
        CompositionTarget.Rendering += (_, _) => move.X = ((DateTime.UtcNow - start).TotalSeconds * 900) % w;
        win.Show();
        Native.SetWindowPos(new WindowInteropHelper(win).Handle, Native.HWND_TOPMOST, x, y, w, h, 0);
        win.Activate();
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        t.Tick += (_, _) => Application.Current.Shutdown();
        t.Start();
    }

    /// <summary>The fake Dock: icons, one magnified past the bar, a label and an indicator dot. No background of its own.</summary>
    public static Window Dock(out List<Rect> tiles)
    {
        var w = new Window { WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize, Topmost = true, ShowInTaskbar = false, ShowActivated = false, Width = DockW, Height = DockH };
        var canvas = new Canvas();
        tiles = new List<Rect>();
        Color[] colors = { Colors.DodgerBlue, Colors.OrangeRed, Colors.MediumSeaGreen, Colors.MediumPurple, Colors.Goldenrod, Colors.DeepPink, Colors.Teal, Colors.SlateGray };
        double x = BarX + 14;
        for (int i = 0; i < 8; i++)
        {
            bool big = i == 3;
            double s = big ? 78 : 52;
            double top = big ? BarY + 54 - s : BarY + 7;
            var tile = new Border { Width = s, Height = s, CornerRadius = new CornerRadius(s * 0.23), Background = new LinearGradientBrush(colors[i], Colors.White, 90) };
            tile.Child = new TextBlock { Text = ((char)('A' + i)).ToString(), FontSize = s * 0.5, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            Canvas.SetLeft(tile, x); Canvas.SetTop(tile, top);
            canvas.Children.Add(tile);
            tiles.Add(new Rect(x, top, s, s));
            if (i == 1)
            {
                var dot = new Ellipse { Width = 5, Height = 5, Fill = Brushes.White };
                Canvas.SetLeft(dot, x + s / 2 - 2.5); Canvas.SetTop(dot, BarY + BarH - 7);
                canvas.Children.Add(dot);
                tiles.Add(new Rect(x + s / 2 - 2.5, BarY + BarH - 7, 5, 5));
            }
            if (big)
            {
                var label = new Border { CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x20, 0x20, 0x24)), Padding = new Thickness(8, 2, 8, 2), Child = new TextBlock { Text = "Finder", Foreground = Brushes.White, FontSize = 13 } };
                Canvas.SetLeft(label, x + 12); Canvas.SetTop(label, 0);
                canvas.Children.Add(label);
            }
            x += s + 30;
        }
        w.Content = canvas;
        return w;
    }
}
