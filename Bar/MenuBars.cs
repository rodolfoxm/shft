using System.Windows;
using System.Windows.Threading;
using Shft.Core;
using Shft.Native;

namespace Shft.Bar;

/// <summary>
/// The bars of all monitors and what they share. Foreground and audio come from Windows' events; the few timers
/// (clock, memory, full-screen check) stop while a game or video owns the screen.
/// </summary>
public sealed class MenuBars : IDisposable
{
    readonly List<MenuBarWindow> _bars = new();
    readonly DispatcherTimer _clock = new();
    readonly DispatcherTimer _memory = new() { Interval = TimeSpan.FromSeconds(5) };
    readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _sample = new() { Interval = TimeSpan.FromMilliseconds(180) };
    readonly DispatcherTimer _heartbeat = new() { Interval = TimeSpan.FromSeconds(3) };
    readonly List<IntPtr> _hooks = new();
    Win32.WinEventProc? _proc;
    bool _full, _sampling;
    bool _purging;
    Hme.EngineHost? _hme;
    Task<Hme.EngineHost?>? _hmeStarting;

    public IntPtr Foreground { get; private set; }

    public MenuBars()
    {
        _clock.Tick += (_, _) => { ForEach(b => b.OnClock()); AlignClock(); };
        _memory.Tick += (_, _) => ForEach(b => b.OnMemory());
        _watch.Tick += (_, _) => CheckFullScreen();
        _sample.Tick += (_, _) => { _sample.Stop(); _ = Sample(); };
        _heartbeat.Tick += (_, _) => RequestSample();
        AudioService.Instance.Changed += OnData;
        ConnectivityService.Instance.Changed += OnData;
        NotificationService.Instance.Changed += OnData;
        TrayHost.Instance.Changed += () => ForEach(b => b.OnTray());
        DeviceBatteries.Instance.Changed += () => ForEach(b => b.OnBatteries());
    }

    void OnData() => ForEach(b => b.OnData());

    void ForEach(Action<MenuBarWindow> act)
    {
        foreach (var b in _bars) act(b);
    }

    public void Start()
    {
        _proc = OnWinEvent;
        foreach (var (from, to) in new[] { (Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND), (EVENT_SYSTEM_MOVESIZEEND, EVENT_SYSTEM_MOVESIZEEND), (Win32.EVENT_SYSTEM_MINIMIZESTART, Win32.EVENT_SYSTEM_MINIMIZEEND) })
            _hooks.Add(Win32.SetWinEventHook(from, to, IntPtr.Zero, _proc, 0, 0, Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS));
        _memory.Start();
        _watch.Start();
        AlignClock();
        _clock.Start();
        Apply();
    }

    const uint EVENT_SYSTEM_MOVESIZEEND = 0x000B;

    void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int obj, int child, uint thread, uint time)
    {
        if (evt == Win32.EVENT_SYSTEM_FOREGROUND)
        {
            UpdateFocus(hwnd);
            CheckFullScreen();
        }
        RequestSample();
    }

    void UpdateFocus(IntPtr hwnd)
    {
        var cls = hwnd == IntPtr.Zero ? "" : Win32.ClassName(hwnd);
        if (cls is "" or "Progman" or "WorkerW" or "Shell_TrayWnd" or "XamlExplorerHostIslandWindow" or "Windows.UI.Core.CoreWindow")
        {
            if (cls is "Progman" or "WorkerW") Publish(IntPtr.Zero, "Área de Trabalho");
            return;
        }
        Win32.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == Environment.ProcessId) return;
        var exe = Win32.ProcessPath(pid);
        Publish(hwnd, exe != null ? WindowTracker.DisplayName(exe) : Win32.WindowText(hwnd));
    }

    void Publish(IntPtr hwnd, string app)
    {
        Foreground = hwnd;
        ForEach(b => b.OnFocus(app, hwnd));
    }

    void AlignClock() => _clock.Interval = TimeSpan.FromSeconds(60 - DateTime.Now.Second);

    /// <summary>Rebuilds the bars for the current settings and monitors.</summary>
    public void Apply()
    {
        var s = SettingsStore.Current;
        var wanted = !s.TopBarEnabled ? new List<MonitorInfo>()
            : s.TopBarAllMonitors ? Monitors.All().ToList()
            : new List<MonitorInfo> { Monitors.Pick(-1) };
        foreach (var gone in _bars.Where(b => !wanted.Any(m => Same(m, b.Monitor))).ToList())
        {
            gone.Close();
            _bars.Remove(gone);
        }
        var fg = Win32.GetForegroundWindow();
        foreach (var m in wanted.Where(m => !_bars.Any(b => Same(m, b.Monitor))))
        {
            var bar = new MenuBarWindow(this, m);
            // A monitor that changed resolution gets a new bar. When that is a game switching modes (CS2 in 4:3 on
            // every Alt+Tab), the new bar appeared over it mid-switch and the game's mouse came back off: it starts
            // hidden, and CheckFullScreen shows it once the game leaves.
            if (s.HideOnFullScreen && Monitors.FullScreenWindow(fg, m) != IntPtr.Zero)
                new System.Windows.Interop.WindowInteropHelper(bar).EnsureHandle();
            else bar.Show();
            _bars.Add(bar);
        }
        ForEach(b => b.Apply());
        UpdateFocus(Win32.GetForegroundWindow());
        if (s.TopBarEnabled && s.TopBarShowBatteries && !_full) DeviceBatteries.Instance.Start();
        else DeviceBatteries.Instance.Stop();
        if (!s.TopBarShowHme && _hme != null)
        {
            _hme.Dispose(); // the house is off: nothing of hme. keeps running
            _hme = null;
        }
        if (s.TopBarStyle == TopBarStyle.Adaptive && s.TopBarEnabled) _heartbeat.Start();
        else _heartbeat.Stop();
        RequestSample();
    }

    static bool Same(MonitorInfo a, MonitorInfo b) => a.Device == b.Device && a.Bounds.Equals(b.Bounds) && Math.Abs(a.Scale - b.Scale) < 0.01;

    public void RequestSample()
    {
        if (SettingsStore.Current.TopBarStyle != TopBarStyle.Adaptive || _full) return;
        _sample.Stop();
        _sample.Start();
    }

    async Task Sample()
    {
        if (_sampling) return;
        _sampling = true;
        try
        {
            foreach (var b in _bars) await b.SampleAsync();
        }
        finally
        {
            _sampling = false;
        }
    }

    /// <summary>Full-screen app on a bar's monitor: that bar leaves. When every monitor is taken, the timers rest too.</summary>
    public void CheckFullScreen()
    {
        var fg = Win32.GetForegroundWindow();
        Monitors.SnapDisplaced(fg);
        bool anyVisible = false;
        foreach (var b in _bars)
        {
            var game = Monitors.FullScreenWindow(fg, b.Monitor);
            bool full = SettingsStore.Current.HideOnFullScreen && game != IntPtr.Zero;
            if (full == b.IsVisible) b.SetHiddenForFullScreen(full);
            anyVisible |= !full;
            b.EnsureReserved();
        }
        bool rest = !anyVisible && _bars.Count > 0;
        if (rest == _full) return;
        _full = rest;
        if (rest)
        {
            _memory.Stop();
            _heartbeat.Stop();
            DeviceBatteries.Instance.Stop(); // no receiver traffic while a game owns the screen
            NotificationService.Instance.Pause(true);
        }
        else
        {
            _memory.Start();
            if (SettingsStore.Current.TopBarStyle == TopBarStyle.Adaptive) _heartbeat.Start();
            NotificationService.Instance.Pause(false);
            if (SettingsStore.Current.TopBarShowBatteries) DeviceBatteries.Instance.Start();
        }
    }

    public void ToggleControlCenter() => BarUnderPointer()?.ToggleControlCenter();

    public void ToggleHme()
    {
        if (SettingsStore.Current.TopBarShowHme) BarUnderPointer()?.ToggleHme();
    }

    /// <summary>
    /// hme.'s engine, created on the first click of the house. When hme.exe already owns it, hme.exe shows its own
    /// panel and this returns null; if that hme.exe is gone, shft. takes the engine over.
    /// </summary>
    public Task<Hme.EngineHost?> HmeHost()
    {
        if (_hme != null) return Task.FromResult<Hme.EngineHost?>(_hme);
        // A second click while the first is still starting waits for the same start, never a second engine.
        return _hmeStarting ??= StartHme();
    }

    async Task<Hme.EngineHost?> StartHme()
    {
        try { return await StartHmeCore(); }
        finally { _hmeStarting = null; }
    }

    async Task<Hme.EngineHost?> StartHmeCore()
    {
        var cfg = Hme.HmeConfig.Load(Hme.HmeConfig.DefaultFolder);
        if (await Hme.LocalApi.IsOldControleCasa(cfg.Port))
        {
            MessageBox.Show(Hme.LocalApi.OldAppMessage, "hme.", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }
        var host = Hme.EngineHost.TryAcquire(cfg, () => Application.Current.Dispatcher.BeginInvoke(() => BarUnderPointer()?.ToggleHme()),
            () => Log.Write("hme: /api/quit ignored inside shft."));
        if (host == null)
        {
            if (await Hme.LocalApi.AskShow(cfg.Port)) return null;
            host = Hme.EngineHost.TryAcquire(cfg, () => Application.Current.Dispatcher.BeginInvoke(() => BarUnderPointer()?.ToggleHme()),
                () => Log.Write("hme: /api/quit ignored inside shft."));
            if (host == null) return null;
        }
        _hme = host;
        await host.Start();
        return host;
    }

    MenuBarWindow? BarUnderPointer()
    {
        Win32.GetCursorPos(out var c);
        return _bars.FirstOrDefault(b => c.X >= b.Monitor.Bounds.Left && c.X < b.Monitor.Bounds.Right && c.Y >= b.Monitor.Bounds.Top && c.Y < b.Monitor.Bounds.Bottom) ?? _bars.FirstOrDefault();
    }

    /// <summary>Asks Windows to elevate (UAC) and frees idle memory; a click while it runs does nothing.</summary>
    public async Task PurgeMemory()
    {
        if (_purging) return;
        _purging = true;
        try
        {
            var freed = await SystemActions.PurgeMemoryAsync();
            if (freed != null) App.Notify("RAM limpa", SystemMeters.Bytes(freed.Value) + " liberados");
            ForEach(b => b.OnMemory());
        }
        finally
        {
            _purging = false;
        }
    }

    public void Dispose()
    {
        foreach (var t in new[] { _clock, _memory, _watch, _sample, _heartbeat }) t.Stop();
        foreach (var h in _hooks) Win32.UnhookWinEvent(h);
        _hme?.Dispose();
        _hme = null;
        foreach (var b in _bars.ToList())
            try { b.Close(); } catch (InvalidOperationException) { }
        _bars.Clear();
    }
}
