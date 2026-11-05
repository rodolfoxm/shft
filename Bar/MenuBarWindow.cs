using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Shft.Core;
using Shft.Dock;
using Shft.Native;
using Shft.Ui;

namespace Shft.Bar;

/// <summary>The top bar of one monitor. Shared data and timers live in <see cref="MenuBars"/>.</summary>
public sealed class MenuBarWindow : Window
{
    /// <summary>One end of the bar. Each end has its own text colour, so a bar over a half-dark wallpaper stays readable.</summary>
    sealed class Zone
    {
        public readonly SolidColorBrush Text = new(Colors.White);
        public readonly SolidColorBrush Hover = new(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
        public readonly StackPanel Items = new() { Orientation = Orientation.Horizontal };
        public readonly Border Host = new() { CornerRadius = new CornerRadius(10), Padding = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, BorderThickness = new Thickness(0) };
        public readonly SolidColorBrush Scrim = new(Colors.Transparent);
        public bool? Dark;

        public Zone()
        {
            Host.Child = Items;
            Host.Background = Scrim;
            Items.Resources["MenuHoverBrush"] = Hover;
        }

        public void Paint(bool darkText, bool scrim, bool animate)
        {
            var c = darkText ? Color.FromRgb(0x10, 0x10, 0x14) : Colors.White;
            var back = darkText ? Colors.White : Colors.Black;
            var hover = Color.FromArgb(0x28, c.R, c.G, c.B);
            var layer = scrim ? Color.FromArgb(0x48, back.R, back.G, back.B) : Colors.Transparent;
            if (animate)
            {
                Motion.Tint(Text, c);
                Motion.Tint(Scrim, layer);
            }
            else
            {
                Text.BeginAnimation(SolidColorBrush.ColorProperty, null);
                Scrim.BeginAnimation(SolidColorBrush.ColorProperty, null);
                Text.Color = c;
                Scrim.Color = layer;
            }
            Hover.Color = hover;
            Dark = darkText;
        }
    }

    readonly MenuBars _hub;
    readonly MonitorInfo _monitor;
    readonly Grid _root = new();
    readonly Border _fill = new();
    readonly Zone _left = new(), _right = new();
    readonly BackdropWindow _backdrop = new();
    AppBar? _appBar;
    IntPtr _hwnd;
    RECT _rect;

    TextBlock _user = null!, _app = null!, _memory = null!, _clock = null!, _bell = null!, _volume = null!, _net = null!, _bt = null!;
    Border _bellDot = null!;
    Button _userButton = null!, _appButton = null!, _memoryButton = null!, _btButton = null!;
    StackPanel _appMenus = null!, _tray = null!;
    FrameworkElement _logo = null!;
    Button _controls = null!;

    public MonitorInfo Monitor => _monitor;
    public IntPtr Handle => _hwnd;

    public MenuBarWindow(MenuBars hub, MonitorInfo monitor)
    {
        _hub = hub;
        _monitor = monitor;
        Title = "shft. bar";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Left = -32000;
        Top = -32000;
        Width = 10;
        Height = 10;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 13;
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);

        _left.Host.HorizontalAlignment = HorizontalAlignment.Left;
        _left.Host.Margin = new Thickness(6, 0, 0, 0);
        _right.Host.HorizontalAlignment = HorizontalAlignment.Right;
        _right.Host.Margin = new Thickness(0, 0, 6, 0);
        BuildLeft();
        BuildRight();
        _root.Children.Add(_fill);
        _root.Children.Add(_left.Host);
        _root.Children.Add(_right.Host);
        Content = _root;
        MouseRightButtonUp += (_, e) =>
        {
            if (e.OriginalSource == _fill || e.OriginalSource == _root) OpenBarMenu();
        };
    }

    Button Item(Zone zone, object content, string name, Action click)
    {
        var b = FlyoutWindow.FlatButton(content, click, 7);
        b.Focusable = false;
        b.Foreground = zone.Text;
        b.Padding = new Thickness(7, 3, 7, 3);
        b.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(b, name);
        zone.Items.Children.Add(b);
        return b;
    }

    void BuildLeft()
    {
        _user = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        _logo = new Border();
        var who = new StackPanel { Orientation = Orientation.Horizontal };
        who.Children.Add(_logo);
        who.Children.Add(_user);
        _userButton = Item(_left, who, "Menu do usuário", OpenUserMenu);
        _userButton.MouseRightButtonUp += (_, e) => { e.Handled = true; LogoPicker.Choose(); };
        _userButton.ToolTip = "Botão direito: trocar a logo";

        _app = new TextBlock { FontWeight = FontWeights.SemiBold, MaxWidth = 260, TextTrimming = TextTrimming.CharacterEllipsis };
        _appButton = Item(_left, _app, "App em foco", OpenAppMenu);
        _appMenus = new StackPanel { Orientation = Orientation.Horizontal };
        _left.Items.Children.Add(_appMenus);
    }

    void BuildRight()
    {
        _memory = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5, FontWeight = FontWeights.SemiBold };
        var ram = new StackPanel { Orientation = Orientation.Horizontal };
        ram.Children.Add(new TextBlock { Text = Glyph.Memory, FontFamily = Glyph.Font, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
        ram.Children.Add(_memory);
        _memoryButton = Item(_right, ram, "Memória", () => _ = _hub.PurgeMemory());
        _memoryButton.ToolTip = "Clique para liberar a RAM ociosa";

        _tray = new StackPanel { Orientation = Orientation.Horizontal };
        _right.Items.Children.Add(_tray);

        _bt = Glyph.Text(Glyph.Bluetooth, 14);
        _btButton = Item(_right, _bt, "Bluetooth", () => Panel("bluetooth", () => new ControlCenterWindow("bluetooth"), _controls));
        _net = Glyph.Text(Glyph.Ethernet, 14);
        Item(_right, _net, "Rede", () => Panel("network", () => new ControlCenterWindow("network"), _controls));
        _volume = Glyph.Text(Glyph.Volume, 14);
        var volume = Item(_right, _volume, "Volume", () => Panel("output", () => new ControlCenterWindow("output"), _controls));
        volume.MouseWheel += (_, e) =>
        {
            var audio = AudioService.Instance;
            audio.SetLevel(AudioFlow.Output, audio.Level(AudioFlow.Output) + Math.Sign(e.Delta) * 0.04);
        };
        volume.ToolTip = "A roda do mouse muda o volume";

        _bell = Glyph.Text(Glyph.Bell, 14);
        _bellDot = new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -1, -2, 0), Visibility = Visibility.Collapsed };
        var bell = new Grid();
        bell.Children.Add(_bell);
        bell.Children.Add(_bellDot);
        Button? bellButton = null;
        bellButton = Item(_right, bell, "Notificações", () => Panel("bell", () => new NotificationsPanel(), bellButton!));
        Button? trayButton = null;
        trayButton = Item(_right, Glyph.Text(Glyph.Up, 12), "Bandeja do sistema", () => Panel("tray", () => new TrayPanel(), trayButton!));
        _controls = Item(_right, Glyph.Text(Glyph.Controls, 14), "Central de Controle", ToggleControlCenter);
        _clock = new TextBlock { FontWeight = FontWeights.SemiBold, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
        Button? clock = null;
        clock = Item(_right, _clock, "Calendário", () => Panel("clock", () => new CalendarPanel(), clock!));
    }

    FlyoutWindow? _open;
    string? _openKey;

    /// <summary>Opens a panel under the bar; the same button again closes it.</summary>
    void Panel(string key, Func<FlyoutWindow> create, FrameworkElement anchor)
    {
        if (_open != null)
        {
            bool same = _openKey == key;
            _open.SafeClose();
            _open = null;
            if (same) return;
        }
        _open = create();
        _openKey = key;
        var panel = _open;
        panel.Closed += (_, _) => { if (_open == panel) _open = null; };
        panel.ShowNear(anchor, DockEdge.Top);
    }

    public void ToggleControlCenter() => Panel("controls", () => new ControlCenterWindow(), _controls);

    void OpenUserMenu()
    {
        var menu = Menu(_userButton);
        menu.Items.Add(DockController.Item("Configurações do shft.", () => App.ShowSettings()));
        menu.Items.Add(DockController.Item("Configurações do Windows", () => DockController.Open("ms-settings:")));
        menu.Items.Add(DockController.Item("Gerenciador de Tarefas", () => DockController.Open("taskmgr.exe")));
        menu.Items.Add(new Separator());
        menu.Items.Add(DockController.Item("Bloquear", SystemActions.Lock));
        menu.Items.Add(DockController.Item("Suspender", SystemActions.Sleep));
        menu.Items.Add(DockController.Item("Sair da conta…", () => Confirm("Sair da conta agora?", SystemActions.SignOut)));
        menu.Items.Add(DockController.Item("Reiniciar…", () => Confirm("Reiniciar o computador agora?", SystemActions.Restart)));
        menu.Items.Add(DockController.Item("Desligar…", () => Confirm("Desligar o computador agora?", SystemActions.PowerOff)));
        menu.IsOpen = true;
    }

    public static void Confirm(string question, Action action)
    {
        if (MessageBox.Show(question, "shft.", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK) action();
    }

    void OpenAppMenu()
    {
        var fg = _hub.Foreground;
        if (fg == IntPtr.Zero) return;
        var menu = Menu(_appButton);
        menu.Items.Add(DockController.Item("Minimizar", () => Win32.ShowWindowAsync(fg, Win32.SW_MINIMIZE)));
        menu.Items.Add(DockController.Item("Fechar janela", () => Win32.RequestClose(fg)));
        menu.IsOpen = true;
    }

    /// <summary>The focused app's own menus, for the apps that still have a classic menu bar.</summary>
    void FillAppMenus(IntPtr window)
    {
        _appMenus.Children.Clear();
        foreach (var entry in AppMenu.TopLevel(window).Where(e => e.Text.Length > 0).Take(9))
        {
            var e = entry;
            Button? b = null;
            b = FlyoutWindow.FlatButton(new TextBlock { Text = e.Text }, () =>
            {
                var menu = Menu(b!);
                AddEntries(menu.Items, AppMenu.Items(e.SubMenu), window);
                if (menu.Items.Count > 0) menu.IsOpen = true;
            }, 7);
            b.Focusable = false;
            b.Foreground = _left.Text;
            b.Padding = new Thickness(7, 3, 7, 3);
            b.VerticalAlignment = VerticalAlignment.Center;
            _appMenus.Children.Add(b);
        }
    }

    static void AddEntries(ItemCollection target, IReadOnlyList<AppMenu.Entry> entries, IntPtr window)
    {
        foreach (var e in entries)
        {
            if (e.Separator)
            {
                target.Add(new Separator());
                continue;
            }
            if (e.SubMenu != IntPtr.Zero)
            {
                var parent = new MenuItem { Header = e.Text, IsEnabled = e.Enabled };
                AddEntries(parent.Items, AppMenu.Items(e.SubMenu), window);
                target.Add(parent);
                continue;
            }
            var id = e.Id;
            var item = DockController.Item(e.Text, () => AppMenu.Invoke(window, id));
            item.IsEnabled = e.Enabled;
            target.Add(item);
        }
    }

    ContextMenu Menu(UIElement target) => new() { PlacementTarget = target, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };

    void OpenBarMenu()
    {
        var menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        menu.Items.Add(DockController.Item("Configurações da barra…", () => App.ShowSettings("bar")));
        menu.Items.Add(new Separator());
        menu.Items.Add(DockController.Item("Encerrar shft.", () => Application.Current.Shutdown()));
        menu.IsOpen = true;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        var source = HwndSource.FromHwnd(_hwnd);
        source.AddHook(WndProc);
        Win32.AddExStyle(_hwnd, Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE);
        _appBar = new AppBar(source);
        _appBar.FullScreenChanged += _ => _hub.CheckFullScreen();
        _appBar.NeedsLayout += () => Dispatcher.BeginInvoke(Place, DispatcherPriority.Background);
        _backdrop.Show();
        Win32.SetWindowLongPtr(_hwnd, Win32.GWLP_HWNDPARENT, _backdrop.Handle);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_MOUSEACTIVATE = 0x0021, MA_NOACTIVATE = 3;
        if (msg == WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(MA_NOACTIVATE);
        }
        return IntPtr.Zero;
    }

    /// <summary>Applies size, position, background and content settings.</summary>
    public void Apply()
    {
        if (_hwnd == IntPtr.Zero) return;
        var s = SettingsStore.Current;
        var theme = Theme.Resolve(s);
        Resources["TextBrush"] = _right.Text; // panels aside, anything hosted here follows the zone colour

        byte alpha = (byte)Math.Round(Math.Clamp(s.TopBarOpacity, 0, 1) * 255);
        var fill = s.TopBarStyle switch
        {
            TopBarStyle.Solid => Color.FromRgb(theme.MenuBack.R, theme.MenuBack.G, theme.MenuBack.B),
            TopBarStyle.Translucent => Color.FromArgb(Math.Max((byte)1, alpha), theme.BarFill.R, theme.BarFill.G, theme.BarFill.B),
            _ => Color.FromArgb(1, 0, 0, 0), // not zero: fully clear pixels of a layered window let clicks through
        };
        _fill.Background = Theme.Brush(fill);
        if (s.TopBarStyle != TopBarStyle.Adaptive)
        {
            _left.Paint(!theme.IsDark, false, false);
            _right.Paint(!theme.IsDark, false, false);
        }
        _left.Dark = _right.Dark = s.TopBarStyle == TopBarStyle.Adaptive ? _left.Dark : null;

        _user.Text = s.TopBarUserName.Length > 0 ? s.TopBarUserName : SystemActions.DisplayName();
        _user.Visibility = s.TopBarShowUser ? Visibility.Visible : Visibility.Collapsed;
        _appButton.Visibility = s.TopBarShowApp ? Visibility.Visible : Visibility.Collapsed;
        _appMenus.Visibility = _appButton.Visibility;
        _memoryButton.Visibility = s.TopBarShowMemory ? Visibility.Visible : Visibility.Collapsed;
        foreach (var t in new[] { _user, _app, _memory, _clock }) t.Foreground = t == _user || t == _app ? _left.Text : _right.Text;
        SetLogo(s);
        Place();
        OnData();
        OnClock();
        OnTray();
        OnMemory();
    }

    void SetLogo(AppSettings s)
    {
        var parent = (StackPanel)_logo.Parent;
        int index = parent.Children.IndexOf(_logo);
        parent.Children.Remove(_logo);
        _logo = BuildLogo(s);
        parent.Children.Insert(index, _logo);
    }

    FrameworkElement BuildLogo(AppSettings s)
    {
        const double size = 17;
        if (File.Exists(s.TopBarLogo))
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(s.TopBarLogo);
                bmp.DecodePixelWidth = 64;
                bmp.EndInit();
                bmp.Freeze();
                if (!s.TopBarLogoTint) return new Image { Source = bmp, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
                return new Border { Width = size, Height = size, Background = _left.Text, OpacityMask = new ImageBrush(bmp) { Stretch = Stretch.Uniform }, VerticalAlignment = VerticalAlignment.Center };
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException)
            {
                Log.Write("logo not read: " + ex.Message);
            }
        }
        return new TextBlock { Text = "s.", FontSize = 15, FontWeight = FontWeights.Bold, Foreground = _left.Text, VerticalAlignment = VerticalAlignment.Center };
    }

    void Place()
    {
        if (_appBar == null) return;
        var s = SettingsStore.Current;
        int height = (int)Math.Ceiling(Math.Clamp(s.TopBarHeight, 24, 48) * _monitor.Scale);
        var top = _monitor.Bounds.Top;
        if (s.TopBarReserve) top = _appBar.Reserve(DockEdge.Top, _monitor.Bounds, height).Top;
        else _appBar.Release();
        _rect = new RECT(_monitor.Bounds.Left, top, _monitor.Bounds.Right, top + height);
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, _rect.Left, _rect.Top, _rect.Width, _rect.Height, Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
        _backdrop.SetStyle(acrylic: false, Theme.Resolve(s).IsDark);
        if (s.TopBarStyle == TopBarStyle.Translucent && s.TopBarBlur && IsVisible) _backdrop.Place(_rect, 0);
        else _backdrop.Conceal();
    }

    /// <summary>
    /// Explorer can drop a reservation (an Explorer restart, a display waking). If windows may run under the
    /// bar again, register it anew.
    /// </summary>
    public void EnsureReserved()
    {
        if (_appBar == null || !SettingsStore.Current.TopBarReserve || !IsVisible) return;
        var info = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
        if (!Win32.GetMonitorInfo(_monitor.Handle, ref info) || info.rcWork.Top >= _rect.Bottom) return;
        Log.Write($"top bar: work area starts at {info.rcWork.Top}, reserving again");
        _appBar.Release();
        Place();
    }

    public void SetHiddenForFullScreen(bool hidden)
    {
        if (hidden)
        {
            Hide();
            _backdrop.Conceal();
        }
        else
        {
            Show();
            Place();
        }
    }

    // ---------------------------------------------------------------- live data, pushed in by MenuBars

    public void OnClock() =>
        _clock.Text = FormatClock(DateTime.Now, SettingsStore.Current.TopBarClockFormat, CultureInfo.CurrentCulture);

    internal static string FormatClock(DateTime now, string format, CultureInfo culture)
    {
        var c = (CultureInfo)culture.Clone();
        if (c.DateTimeFormat.AMDesignator.Length == 0) c.DateTimeFormat.AMDesignator = "AM";
        if (c.DateTimeFormat.PMDesignator.Length == 0) c.DateTimeFormat.PMDesignator = "PM";
        return CapitalizeDay(now.ToString(format, c));
    }

    internal static string CapitalizeDay(string text) =>
        text.Length == 0 ? text : char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..].Replace(".", "");

    public void OnMemory()
    {
        var m = SystemMeters.Memory();
        const double gb = 1024 * 1024 * 1024;
        _memory.Text = $"{m.Used / gb:0}GB / {Math.Round(m.Total / gb):0}GB";
    }

    public void OnFocus(string app, IntPtr window)
    {
        _app.Text = app;
        FillAppMenus(window);
    }

    public void OnData()
    {
        var c = ConnectivityService.Instance;
        _btButton.Visibility = c.BluetoothAvailable ? Visibility.Visible : Visibility.Collapsed;
        _bt.Opacity = c.BluetoothOn ? 1 : 0.45;
        _net.Text = c.Connection switch { "Wi-Fi" => Glyph.Wifi, "Ethernet" => Glyph.Ethernet, _ => Glyph.Offline };
        var audio = AudioService.Instance;
        _volume.Text = audio.Muted(AudioFlow.Output) ? Glyph.Mute : Glyph.Volume;
        _bellDot.Visibility = NotificationService.Instance.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void OnTray()
    {
        _tray.Children.Clear();
        var pins = SettingsStore.Current.PinnedTray;
        foreach (var e in TrayHost.Instance.Items.Where(i => pins.Contains(i.PinKey)))
        {
            var entry = e;
            var image = new Image { Source = e.Icon, Width = 16, Height = 16 };
            var b = FlyoutWindow.FlatButton(image, () => TrayHost.Instance.Click(entry, right: false), 6);
            b.Focusable = false;
            b.Padding = new Thickness(6, 3, 6, 3);
            b.VerticalAlignment = VerticalAlignment.Center;
            b.ToolTip = e.Tip.Length > 0 ? e.Tip : e.Name;
            AutomationProperties.SetName(b, e.Name);
            b.MouseRightButtonUp += (_, ev) => { ev.Handled = true; TrayHost.Instance.Click(entry, right: true); };
            _tray.Children.Add(b);
        }
    }

    // ---------------------------------------------------------------- adaptive colour

    /// <summary>Reads what is behind the bar and picks each end's colours. The read leaves this window on screen.</summary>
    public async Task SampleAsync()
    {
        var s = SettingsStore.Current;
        if (s.TopBarStyle != TopBarStyle.Adaptive || !IsVisible || _rect.Width <= 0) return;
        int w = Math.Min(320, _rect.Width / 6);
        var px = await ScreenSampler.CaptureBehind(new[] { _hwnd }, _rect, w, 2);
        if (px == null)
        {
            // Nothing readable (secure desktop, protected content): the scrim keeps text legible.
            bool dark = !Theme.Resolve(s).IsDark;
            _left.Paint(dark, true, true);
            _right.Paint(dark, true, true);
            return;
        }
        double perPixel = w / Math.Max(1, ActualWidth);
        int leftEnd = Math.Clamp((int)((_left.Host.ActualWidth + 12) * perPixel), 1, w);
        int rightStart = Math.Clamp(w - (int)((_right.Host.ActualWidth + 12) * perPixel), 0, w - 1);
        var l = AdaptiveContrast.Decide(AdaptiveContrast.Luminances(px, 0, leftEnd), _left.Dark);
        var r = AdaptiveContrast.Decide(AdaptiveContrast.Luminances(px, rightStart, w - rightStart), _right.Dark);
        _left.Paint(l.DarkText, l.Scrim && s.TopBarScrim, true);
        _right.Paint(r.DarkText, r.Scrim && s.TopBarScrim, true);
    }

    protected override void OnClosed(EventArgs e)
    {
        _appBar?.Dispose();
        try { _backdrop.Close(); } catch (InvalidOperationException) { }
        base.OnClosed(e);
    }
}
