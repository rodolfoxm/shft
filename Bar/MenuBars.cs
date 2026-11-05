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
        foreach (var m in wanted.Where(m => !_bars.Any(b => Same(m, b.Monitor))))
        {
            var bar = new MenuBarWindow(this, m);
            bar.Show();
            _bars.Add(bar);
        }
        ForEach(b => b.Apply());
        UpdateFocus(Win32.GetForegroundWindow());
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
        bool anyVisible = false;
        foreach (var b in _bars)
        {
            bool full = SettingsStore.Current.HideOnFullScreen && Monitors.IsFullScreen(fg, b.Monitor);
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
            NotificationService.Instance.Pause(true);
        }
        else
        {
            _memory.Start();
            if (SettingsStore.Current.TopBarStyle == TopBarStyle.Adaptive) _heartbeat.Start();
            NotificationService.Instance.Pause(false);
        }
    }

    public void ToggleControlCenter() => BarUnderPointer()?.ToggleControlCenter();

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
        foreach (var b in _bars.ToList())
            try { b.Close(); } catch (InvalidOperationException) { }
        _bars.Clear();
    }
}
