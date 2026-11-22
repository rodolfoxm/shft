using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace GlassBench;

static class Program
{
    static readonly List<string> Report = new();
    static string _out = ".";
    public static void Log(string s)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {s}";
        lock (Report)
        {
            Report.Add(line);
            try { File.AppendAllText(Path.Combine(_out, "live.log"), line + Environment.NewLine); } catch { }
        }
    }

    static readonly List<Process> _children = new();

    [STAThread]
    static int Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int I(int i) => int.Parse(args[i]);
        switch (args[0])
        {
            case "bg": app.Startup += (_, _) => Scene.Background(I(1), I(2), I(3), I(4), args[5]); break;
            case "blink": app.Startup += (_, _) => Scene.Blink(I(1), I(2), args[3]); break;
            case "fs": app.Startup += (_, _) => Scene.FullScreen(I(1), I(2), I(3), I(4), I(5)); break;
            case "panel":
                _out = args[1];
                Directory.CreateDirectory(_out);
                app.Startup += async (_, _) =>
                {
                    try { await PanelSync.Run(_out, I(2), args.Length > 3 ? I(3) : 0); }
                    catch (Exception ex) { Log("FATAL " + ex); }
                    finally { File.WriteAllLines(Path.Combine(_out, "report.txt"), Report); app.Shutdown(); }
                };
                break;
            case "run":
                _out = args[6];
                Directory.CreateDirectory(_out);
                app.Startup += async (_, _) =>
                {
                    // safety: a hung test must never stay on the screen
                    new Thread(() =>
                    {
                        Thread.Sleep(150_000);
                        Log("WATCHDOG: test did not finish in 150 s, killing it");
                        lock (Report) File.WriteAllLines(Path.Combine(_out, "report.txt"), Report);
                        lock (_children) foreach (var c in _children) try { c.Kill(); } catch { }
                        Environment.Exit(3);
                    }) { IsBackground = true }.Start();
                    try { await Run(args[1], I(2), args[3] == "1", I(4), args[5], args[7]); }
                    catch (Exception ex) { Log("FATAL " + ex); }
                    finally
                    {
                        File.WriteAllLines(Path.Combine(_out, "report.txt"), Report);
                        app.Shutdown();
                    }
                };
                break;
        }
        app.Run();
        return 0;
    }

    static Process Spawn(params object[] a)
    {
        var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, string.Join(" ", a)) { UseShellExecute = false })!;
        lock (_children) _children.Add(p);
        return p;
    }

    static void Signal(string tag, string name)
    {
        try { using var ev = EventWaitHandle.OpenExisting($"GlassBench.{tag}.{name}"); ev.Set(); } catch (WaitHandleCannotBeOpenedException) { }
    }

    sealed class Gpu3D : IDisposable
    {
        readonly List<PerformanceCounter> _c = new();
        public Gpu3D(int pid)
        {
            try
            {
                var cat = new PerformanceCounterCategory("GPU Engine");
                foreach (var n in cat.GetInstanceNames().Where(n => n.StartsWith($"pid_{pid}_", StringComparison.Ordinal) && n.EndsWith("engtype_3D", StringComparison.Ordinal)))
                {
                    var pc = new PerformanceCounter("GPU Engine", "Utilization Percentage", n, true);
                    pc.NextValue();
                    _c.Add(pc);
                }
            }
            catch (Exception ex) { Log("gpu counter: " + ex.Message); }
        }
        public double Read() => _c.Sum(c => { try { return c.NextValue(); } catch { return 0; } });
        public void Dispose() { foreach (var c in _c) c.Dispose(); }
    }

    static async Task Run(string source, int fps, bool skip, int monitors, string hostPos, string tag)
    {
        Log($"run: source={source} fps={fps} skip={skip} monitors={monitors} host={hostPos}");
        var m1 = Native.MonitorAt(10, 10);
        var mons = new List<Native.RECT> { m1 };
        if (monitors > 1) mons.Add(Native.MonitorAt(-500, 500));
        var bgs = new List<Process>();
        for (int i = 0; i < mons.Count; i++)
        {
            var m = mons[i];
            int w = Math.Min(1100, m.R - m.L), h = 420;
            bgs.Add(Spawn("bg", m.L + (m.R - m.L - w) / 2, m.B - h, w, h, $"{tag}{i}"));
        }
        await Task.Delay(1500);
        Gpu.Init();
        MagHost.Hooks = hostPos.Contains("-hooks", StringComparison.Ordinal);
        var surfaces = source == "none" ? new List<Surface>() : mons.Select((m, i) => new Surface($"m{i + 1}", m, source, fps, skip, hostPos, _out)).ToList();
        var ours = surfaces.SelectMany(s => new[] { s.DockHwnd, s.GlassHwnd }).ToArray();
        foreach (var s in surfaces) { s.Adaptive = hostPos.EndsWith("-adaptive", StringComparison.Ordinal); s.EventDriven = hostPos.Contains("-event", StringComparison.Ordinal); s.IdleSignalMs = hostPos.Contains("-idle33", StringComparison.Ordinal) ? 33 : 100; s.Start(ours); }

        // the same full-screen rule shft. uses: a foreground window covering the monitor pauses that monitor's glass
        var watch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        watch.Tick += (_, _) =>
        {
            var fg = Native.GetForegroundWindow();
            Native.GetWindowRect(fg, out var r);
            foreach (var s in surfaces)
            {
                bool full = !ours.Contains(fg) && fg != s.HostHwnd && r.L <= s.Monitor.L && r.T <= s.Monitor.T && r.R >= s.Monitor.R && r.B >= s.Monitor.B;
                if (full) s.Pause(); else s.Resume();
            }
        };
        watch.Start();

        var self = Process.GetCurrentProcess();
        Process dwm = Process.GetProcessesByName("dwm").FirstOrDefault();
        async Task Phase(string name, int ms)
        {
            foreach (var s in surfaces) s.Stats.Reset();
            self.Refresh();
            var cpu0 = self.TotalProcessorTime;
            TimeSpan? dwm0 = null;
            try { dwm0 = dwm?.TotalProcessorTime; } catch { }
            PerformanceCounter dwmPc = null;
            try { dwmPc = new PerformanceCounter("Process", "% Processor Time", "dwm", true); dwmPc.NextValue(); } catch { }
            using var g = new Gpu3D(self.Id);
            using var gd = dwm != null ? new Gpu3D(dwm.Id) : null;
            await Task.Delay(ms);
            self.Refresh();
            double cpu = (self.TotalProcessorTime - cpu0).TotalMilliseconds;
            string dwmCpu = "n/a";
            try { if (dwm0 != null) { dwm.Refresh(); dwmCpu = $"{(dwm.TotalProcessorTime - dwm0.Value).TotalMilliseconds / ms * 100:0.00}%"; } } catch { }
            try { if (dwmPc != null) dwmCpu = $"{dwmPc.NextValue():0.00}%"; dwmPc?.Dispose(); } catch { }
            double gpu = g.Read(), gpuDwm = gd?.Read() ?? 0;
            var line = $"PHASE {name} ({ms} ms) | glass process CPU {cpu / ms * 100:0.00}% of a core | GPU3D glass {gpu:0.0}% | DWM CPU {dwmCpu}, GPU3D {gpuDwm:0.0}% | private {self.PrivateMemorySize64 / 1048576} MB, working set {self.WorkingSet64 / 1048576} MB";
            foreach (var s in surfaces)
            {
                lock (s.Stats)
                {
                    var st = s.Stats;
                    string P(List<double> l) => l.Count == 0 ? "-" : $"{l.Average():0.0}/{l.OrderBy(x => x).ElementAt((int)(l.Count * 0.95)):0.0}";
                    line += $" || {s.Name}: signal frames {Interlocked.Exchange(ref s.SignalFrames, 0)}, hits {Interlocked.Exchange(ref s.SignalHits, 0)}, arrived {st.Arrived}, rendered {st.Rendered}, skipped {st.Skipped}, capture latency avg/p95 {P(st.CaptureLatency)} ms, refresh->frame {P(st.RefreshLatency)} ms, render {P(st.RenderMs)} ms{(s.Paused ? ", PAUSED" : "")}";
                }
            }
            Log(line);
        }

        await Phase("warmup", 3000);
        foreach (var s in surfaces) s.DumpName = "live";
        await Task.Delay(500);
        foreach (var s in surfaces)
        {
            int x = s.GlassX - 60, y = s.GlassY - 70;
            using var bmp = new System.Drawing.Bitmap(Scene.BarW + 120, Scene.BarH + 90);
            using (var gr = System.Drawing.Graphics.FromImage(bmp)) gr.CopyFromScreen(x, y, 0, 0, bmp.Size);
            bmp.Save(Path.Combine(_out, $"{s.Name}-printscreen.png"));
        }
        await Phase("animated", 10000);
        for (int i = 0; i < mons.Count; i++) Signal($"{tag}{i}", "Stop");
        await Task.Delay(700);
        await Phase("static", 10000);
        var blink = Spawn("blink", m1.L + 80, m1.T + 80, tag);
        await Task.Delay(1000);
        await Phase("animation-far-from-glass", 8000);
        Signal(tag, "BlinkQuit");
        blink.WaitForExit(3000);
        for (int i = 0; i < mons.Count; i++) Signal($"{tag}{i}", "Start");
        await Task.Delay(700);
        await Phase("animated-again", 5000);
        // wake-up latency: background idle for 2 s, then it starts moving; time from its first moved frame to the
        // first glass frame drawn after it
        if (surfaces.Count > 0)
        {
            using var mmf = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateOrOpen($"GlassBench.{tag}0.Moved", 8);
            using var view = mmf.CreateViewAccessor();
            var lat = new List<double>();
            for (int k = 0; k < 10; k++)
            {
                Signal($"{tag}0", "Stop");
                await Task.Delay(2000);
                view.Write(0, 0L);
                Signal($"{tag}0", "Start");
                await Task.Delay(700);
                long moved = view.ReadInt64(0);
                long first = surfaces[0].RenderTimes.Where(t => t > moved).DefaultIfEmpty(0).Min();
                if (moved > 0 && first > 0) lat.Add((first - moved) * 1000.0 / Stopwatch.Frequency);
            }
            lat.Sort();
            Log(lat.Count == 0 ? "WAKE latency: no samples" : $"WAKE latency (movement starts behind the glass -> glass shows it), {lat.Count} cycles: median {lat[lat.Count / 2]:0} ms, p90 {lat[(int)(lat.Count * 0.9)]:0} ms, max {lat[^1]:0} ms | all: {string.Join(" ", lat.Select(x => x.ToString("0")))}");
            Log($"window events seen {MagHost.HookEvents}, woke the glass {MagHost.HookWakes}");
        }
        var fs = Spawn("fs", m1.L, m1.T, m1.R - m1.L, m1.B - m1.T, 9);
        await Task.Delay(1200);
        await Phase("fullscreen-on-monitor-1", 7000);
        fs.WaitForExit(5000);
        await Task.Delay(1500);
        await Phase("after-fullscreen", 3000);
        watch.Stop();
        for (int i = 0; i < mons.Count; i++) Signal($"{tag}{i}", "Quit");
        foreach (var s in surfaces) s.Stop();
        MagHost.Quit();
        foreach (var b in bgs) b.WaitForExit(3000);
    }
}
