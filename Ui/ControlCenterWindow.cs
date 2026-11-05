using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Shft.Bar;
using Shft.Core;
using Shft.Dock;
using Shft.Native;
using Windows.Media.Control;
using Forms = System.Windows.Forms;

namespace Shft.Ui;

/// <summary>The Control Center: every control reads Windows and writes to it, and shows when it can't.</summary>
public sealed class ControlCenterWindow : FlyoutWindow
{
    const double Width_ = 340;

    readonly Border _pageHost = new();
    readonly DispatcherTimer _media = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly AudioService _audio = AudioService.Instance;
    readonly ConnectivityService _net = ConnectivityService.Instance;
    readonly List<Action> _refresh = new();

    public ControlCenterWindow(string? page = null)
    {
        Radius = 26;
        Inner = new Thickness(12);
        _pageHost.Width = Width_;
        Body = _pageHost;
        ShowHome();
        if (page != null) Open(page);

        _audio.Changed += Refresh;
        _net.Changed += Refresh;
        _media.Tick += (_, _) => _ = RefreshMedia();
        _media.Start();
        Closed += (_, _) =>
        {
            _audio.Changed -= Refresh;
            _net.Changed -= Refresh;
            _media.Stop();
        };
    }

    void Refresh()
    {
        foreach (var r in _refresh) r();
    }

    /// <summary>A module: the rounded translucent card everything in here sits on.</summary>
    static Border Card(UIElement child, double radius = 18, Thickness? padding = null)
    {
        var b = new Border { CornerRadius = new CornerRadius(radius), Padding = padding ?? new Thickness(12), BorderThickness = new Thickness(1), Child = child };
        b.SetResourceReference(Border.BackgroundProperty, "TileBrush");
        b.SetResourceReference(Border.BorderBrushProperty, "TileRimBrush");
        return b;
    }

    static TextBlock Label(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 7), TextTrimming = TextTrimming.CharacterEllipsis };
        return t;
    }

    void Swap(UIElement page)
    {
        _refresh.Clear();
        _pageHost.Child = page;
        _pageHost.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, Motion.Time(160)));
    }

    void Open(string page)
    {
        switch (page)
        {
            case "bluetooth": ShowBluetooth(); break;
            case "network": ShowNetwork(); break;
            case "output": ShowDevices(AudioFlow.Output); break;
            case "input": ShowDevices(AudioFlow.Input); break;
        }
    }

    // ---------------------------------------------------------------- home

    void ShowHome()
    {
        var root = new StackPanel();
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        top.ColumnDefinitions.Add(new ColumnDefinition());
        var connectivity = Connectivity();
        var media = Media();
        Grid.SetColumn(media, 2);
        top.Children.Add(connectivity);
        top.Children.Add(media);
        Swap(root);
        root.Children.Add(top);
        root.Children.Add(Spaced(Toggles()));
        foreach (var screen in Screens()) root.Children.Add(Spaced(screen));
        root.Children.Add(Spaced(Sound(AudioFlow.Output, "Som", Glyph.Volume, Glyph.Mute)));
        if (_audio.Available(AudioFlow.Input)) root.Children.Add(Spaced(Sound(AudioFlow.Input, "Microfone", Glyph.Mic, Glyph.MicOff)));
        if (Battery() is { } battery) root.Children.Add(Spaced(battery));
        root.Children.Add(Spaced(Footer()));
    }

    static T Spaced<T>(T el) where T : FrameworkElement
    {
        el.Margin = new Thickness(0, 10, 0, 0);
        return el;
    }

    UIElement Connectivity()
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var network = new ControlTile(Glyph.Ethernet, "Rede", "…", wide: true, circle: 34);
        network.Click += (_, _) => ShowNetwork();
        var bluetooth = new ControlTile(Glyph.Bluetooth, "Bluetooth", "…", wide: true, circle: 34) { Margin = new Thickness(0, 10, 0, 0) };
        bluetooth.Click += async (_, _) =>
        {
            bluetooth.IsBusy = true;
            await _net.SetBluetooth(!_net.BluetoothOn);
            bluetooth.IsBusy = false;
            Refresh();
        };
        bluetooth.MouseRightButtonUp += (_, e) => { e.Handled = true; ShowBluetooth(); };
        bluetooth.ToolTip = "Clique liga e desliga; botão direito mostra os dispositivos";
        stack.Children.Add(network);
        stack.Children.Add(bluetooth);

        void Update()
        {
            network.IsOn = _net.Connection != null;
            network.Icon = _net.Connection == "Wi-Fi" ? Glyph.Wifi : _net.Connection == null ? Glyph.Offline : Glyph.Ethernet;
            network.Detail = _net.Connection ?? "Sem conexão";
            bluetooth.IsEnabled = _net.BluetoothAvailable;
            bluetooth.IsOn = _net.BluetoothOn;
            bluetooth.Detail = !_net.Loaded ? "Carregando…" : !_net.BluetoothAvailable ? "Indisponível" : _net.BluetoothOn ? "Ligado" : "Desligado";
        }
        _refresh.Add(Update);
        Update();
        return Card(stack);
    }

    // ---------------------------------------------------------------- media

    static GlobalSystemMediaTransportControlsSessionManager? _manager;

    Image _art = null!;
    TextBlock _track = null!, _artist = null!;
    GlyphView _playGlyph = null!;
    GlobalSystemMediaTransportControlsSession? _session;
    string? _shownTrack;

    FrameworkElement Media()
    {
        _art = new Image { Stretch = Stretch.UniformToFill };
        var art = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(10), ClipToBounds = true, HorizontalAlignment = HorizontalAlignment.Left, Child = _art, Clip = new RectangleGeometry(new Rect(0, 0, 44, 44), 10, 10) };
        art.SetResourceReference(Border.BackgroundProperty, "TileHoverBrush");
        _track = new TextBlock { Text = "Nada tocando", FontWeight = FontWeights.SemiBold, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 6, 0, 0) };
        _artist = new TextBlock { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
        _artist.SetResourceReference(TextBlock.ForegroundProperty, "TextSubtleBrush");
        _playGlyph = Glyph.Text(Glyph.Play, 14);
        var controls = new UniformGrid { Columns = 3, Margin = new Thickness(0, 2, 0, 0) };
        foreach (var (content, name, act) in new (object, string, Func<GlobalSystemMediaTransportControlsSession, Task>)[]
                 {
                     (Glyph.Text(Glyph.Prev, 12), "Anterior", async s => await s.TrySkipPreviousAsync()),
                     (_playGlyph, "Tocar ou pausar", async s => await s.TryTogglePlayPauseAsync()),
                     (Glyph.Text(Glyph.Next, 12), "Próxima", async s => await s.TrySkipNextAsync()),
                 })
        {
            var b = FlatButton(content, async () =>
            {
                if (_session == null) return;
                try { await act(_session); }
                catch (Exception ex) { Log.Write("media: " + ex.Message); }
            }, 5);
            b.HorizontalContentAlignment = HorizontalAlignment.Center;
            AutomationProperties.SetName(b, name);
            controls.Children.Add(b);
        }
        var stack = new StackPanel();
        stack.Children.Add(art);
        stack.Children.Add(_track);
        stack.Children.Add(_artist);
        stack.Children.Add(controls);
        _ = RefreshMedia();
        return Card(stack);
    }

    async Task RefreshMedia()
    {
        try
        {
            _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _session = _manager.GetCurrentSession();
            if (_session == null)
            {
                _track.Text = "Nada tocando";
                _artist.Text = "";
                _art.Source = null;
                _shownTrack = null;
                return;
            }
            var info = _session.GetPlaybackInfo();
            _playGlyph.Text = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing ? Glyph.Pause : Glyph.Play;
            var props = await _session.TryGetMediaPropertiesAsync();
            var key = props.Title + "|" + props.Artist;
            if (key == _shownTrack) return;
            _shownTrack = key;
            _track.Text = props.Title;
            _artist.Text = props.Artist;
            _art.Source = props.Thumbnail == null ? null : await Thumbnail(props.Thumbnail);
        }
        catch (Exception ex)
        {
            Log.Write("media: " + ex.Message);
        }
    }

    static async Task<BitmapImage> Thumbnail(global::Windows.Storage.Streams.IRandomAccessStreamReference reference)
    {
        using var stream = await reference.OpenReadAsync();
        using var copy = new MemoryStream();
        await stream.AsStreamForRead().CopyToAsync(copy);
        copy.Position = 0;
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.DecodePixelWidth = 88;
        bmp.StreamSource = copy;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    // ---------------------------------------------------------------- toggles

    FrameworkElement Toggles()
    {
        var grid = new UniformGrid { Columns = 4 };
        var dark = new ControlTile(Glyph.Moon, "Modo escuro") { IsOn = DisplayService.DarkMode() };
        dark.Click += (_, _) =>
        {
            DisplayService.SetDarkMode(!dark.IsOn);
            dark.IsOn = DisplayService.DarkMode();
        };

        var hdrState = DisplayService.HdrEnabled();
        var hdr = new ControlTile(Glyph.Hdr, "HDR") { IsEnabled = hdrState != null, IsOn = hdrState == true };
        hdr.Click += async (_, _) =>
        {
            bool want = !hdr.IsOn;
            hdr.IsBusy = true;
            await Task.Run(() => DisplayService.SetHdr(want));
            await Task.Delay(400); // the mode switch blanks the screen for a moment; read back after
            hdr.IsBusy = false;
            hdr.IsOn = DisplayService.HdrEnabled() == true;
        };

        var nightState = DisplayService.NightLight();
        var night = new ControlTile(Glyph.Eye, "Luz noturna") { IsEnabled = nightState != null, IsOn = nightState == true };
        night.Click += (_, _) =>
        {
            DisplayService.SetNightLight(!night.IsOn);
            night.IsOn = DisplayService.NightLight() == true;
        };

        var ram = new ControlTile(Glyph.Memory, "Limpar RAM");
        ram.Click += async (_, _) =>
        {
            ram.IsBusy = true;
            await App.Bars!.PurgeMemory();
            ram.IsBusy = false;
        };
        ram.IsEnabled = App.Bars != null;

        grid.Children.Add(dark);
        grid.Children.Add(hdr);
        grid.Children.Add(night);
        grid.Children.Add(ram);
        return Card(grid, padding: new Thickness(6, 12, 6, 10));
    }

    // ---------------------------------------------------------------- display

    static List<ScreenInfo>? _screens;

    IEnumerable<FrameworkElement> Screens()
    {
        var host = new StackPanel();
        var hint = new TextBlock { Text = "Lendo os monitores…", FontSize = 12 };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextSubtleBrush");
        host.Children.Add(hint);

        async void Load()
        {
            // DDC/CI talks to the monitor over the cable: tens of ms per call, so never on the UI thread.
            var screens = await Task.Run(() =>
            {
                _screens ??= DisplayService.ReadScreens();
                return _screens;
            });
            host.Children.Clear();
            foreach (var screen in screens)
            {
                if (host.Children.Count > 0) host.Children.Add(new Border { Height = 8 });
                host.Children.Add(Label(screen.Name));
                var slider = new CapsuleSlider("Brilho de " + screen.Name, Glyph.Sun);
                if (screen.Brightness is int level) slider.Value = (double)(level - screen.Min) / Math.Max(1, screen.Max - screen.Min);
                else
                {
                    slider.IsEnabled = false;
                    slider.ToolTip = "Este monitor não aceita ajuste por DDC/CI";
                }
                var s = screen;
                int? pending = null;
                bool running = false;
                slider.ValueChanged += v =>
                {
                    pending = s.Min + (int)Math.Round(v * (s.Max - s.Min));
                    s.Brightness = pending;
                    if (running) return;
                    running = true;
                    // Only the latest value reaches the monitor: it can't keep up with a dragging hand.
                    _ = Task.Run(() =>
                    {
                        while (pending is int value)
                        {
                            pending = null;
                            DisplayService.SetBrightness(s, value);
                        }
                        running = false;
                    });
                };
                host.Children.Add(slider);
            }
            if (host.Children.Count == 0) hint.Text = "Nenhum monitor com ajuste de brilho";
        }
        Load();
        yield return Card(host);
    }

    /// <summary>Monitor handles die with the display setup; the next open reads them again.</summary>
    public static void ForgetScreens() => _screens = null;

    // ---------------------------------------------------------------- sound

    FrameworkElement Sound(AudioFlow flow, string title, string on, string off)
    {
        var header = new DockPanel();
        var more = FlatButton(Glyph.Text(Glyph.Chevron, 10), () => ShowDevices(flow), 5);
        AutomationProperties.SetName(more, "Escolher dispositivo de " + title);
        DockPanel.SetDock(more, System.Windows.Controls.Dock.Right);
        var mute = FlatButton(Glyph.Text(on, 12), () => _audio.SetMuted(flow, !_audio.Muted(flow)), 5);
        var muteIcon = (GlyphView)mute.Content;
        AutomationProperties.SetName(mute, "Silenciar " + title);
        DockPanel.SetDock(mute, System.Windows.Controls.Dock.Right);
        var name = Label(title);
        name.VerticalAlignment = VerticalAlignment.Center;
        name.Margin = new Thickness(2, 0, 0, 0);
        header.Children.Add(more);
        header.Children.Add(mute);
        header.Children.Add(name);

        var slider = new CapsuleSlider(title, on);
        slider.ValueChanged += v => _audio.SetLevel(flow, v);

        var stack = new StackPanel();
        stack.Children.Add(header);
        slider.Margin = new Thickness(0, 4, 0, 0);
        stack.Children.Add(slider);

        void Update()
        {
            bool muted = _audio.Muted(flow);
            slider.IsEnabled = _audio.Available(flow);
            slider.Value = muted ? 0 : _audio.Level(flow);
            slider.Icon = muted ? off : on;
            muteIcon.Text = muted ? off : on;
            var id = _audio.DefaultId(flow);
            name.Text = _audio.Devices(flow).FirstOrDefault(d => d.Id == id)?.Name ?? title;
        }
        _refresh.Add(Update);
        Update();
        return Card(stack, padding: new Thickness(12, 8, 12, 12));
    }

    // ---------------------------------------------------------------- system

    static FrameworkElement? Battery()
    {
        var p = Forms.SystemInformation.PowerStatus;
        if (p.BatteryChargeStatus.HasFlag(Forms.BatteryChargeStatus.NoSystemBattery) || p.BatteryLifePercent > 1) return null;
        bool plugged = p.PowerLineStatus == Forms.PowerLineStatus.Online;
        var row = new DockPanel();
        var icon = Glyph.Text(plugged ? Glyph.Charging : Glyph.Battery, 16);
        icon.Margin = new Thickness(0, 0, 10, 0);
        row.Children.Add(icon);
        row.Children.Add(new TextBlock { Text = $"{p.BatteryLifePercent * 100:0}%{(plugged ? " · na tomada" : "")}", VerticalAlignment = VerticalAlignment.Center });
        return Card(row);
    }

    FrameworkElement Footer()
    {
        var row = new DockPanel();
        var power = FlatButton(Glyph.Text(Glyph.Power, 15), () => { }, 8);
        AutomationProperties.SetName(power, "Energia");
        power.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = power, Placement = PlacementMode.Top };
            menu.Items.Add(DockController.Item("Bloquear", SystemActions.Lock));
            menu.Items.Add(DockController.Item("Suspender", SystemActions.Sleep));
            menu.Items.Add(DockController.Item("Reiniciar…", () => MenuBarWindow.Confirm("Reiniciar o computador agora?", SystemActions.Restart)));
            menu.Items.Add(DockController.Item("Desligar…", () => MenuBarWindow.Confirm("Desligar o computador agora?", SystemActions.PowerOff)));
            menu.IsOpen = true;
        };
        DockPanel.SetDock(power, System.Windows.Controls.Dock.Right);
        var settings = FlatButton(Glyph.Text(Glyph.Settings, 15), () => { SafeClose(); App.ShowSettings("panel"); }, 8);
        AutomationProperties.SetName(settings, "Configurações");
        DockPanel.SetDock(settings, System.Windows.Controls.Dock.Right);
        row.Children.Add(power);
        row.Children.Add(settings);
        row.Children.Add(new TextBlock { Text = "shft.", FontWeight = FontWeights.Bold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Opacity = 0.7 });
        return row;
    }

    // ---------------------------------------------------------------- detail pages

    StackPanel Sub(string title, UIElement? headerRight = null)
    {
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var back = FlatButton(Glyph.Text(Glyph.Back, 12), ShowHome, 7);
        AutomationProperties.SetName(back, "Voltar");
        DockPanel.SetDock(back, System.Windows.Controls.Dock.Left);
        header.Children.Add(back);
        if (headerRight != null)
        {
            DockPanel.SetDock(headerRight, System.Windows.Controls.Dock.Right);
            header.Children.Add(headerRight);
        }
        header.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) });
        var page = new StackPanel { MinHeight = 200 };
        page.Children.Add(header);
        Swap(page);
        return page;
    }

    static Button Row(string glyph, string text, string? detail, bool selected, Action click)
    {
        var row = new DockPanel();
        if (selected)
        {
            var check = Glyph.Text(Glyph.Check, 12);
            DockPanel.SetDock(check, System.Windows.Controls.Dock.Right);
            row.Children.Add(check);
        }
        var icon = Glyph.Text(glyph, 14);
        icon.Margin = new Thickness(0, 0, 10, 0);
        row.Children.Add(icon);
        var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis });
        if (detail != null) label.Children.Add(Subtle(detail));
        row.Children.Add(label);
        var b = FlatButton(row, click, 8);
        b.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        return b;
    }

    void ShowDevices(AudioFlow flow)
    {
        var page = Sub(flow == AudioFlow.Output ? "Saída de som" : "Entrada de som");
        var list = new StackPanel();
        page.Children.Add(Card(list, padding: new Thickness(6)));
        page.Children.Add(FlatButtonFull("Configurações de som…", () => { SafeClose(); DockController.Open("ms-settings:sound"); }));
        void Fill()
        {
            list.Children.Clear();
            var current = _audio.DefaultId(flow);
            foreach (var d in _audio.Devices(flow))
                list.Children.Add(Row(flow == AudioFlow.Output ? Glyph.Volume : Glyph.Mic, d.Name, null, d.Id == current, () => _audio.SetDefault(d.Id)));
            if (list.Children.Count == 0) list.Children.Add(Subtle("Nenhum dispositivo"));
        }
        _refresh.Add(Fill);
        Fill();
    }

    static Button FlatButtonFull(string text, Action click)
    {
        var b = FlatButton(new TextBlock { Text = text, FontSize = 12 }, click);
        b.Margin = new Thickness(0, 8, 0, 0);
        return b;
    }

    async void ShowBluetooth()
    {
        var list = new StackPanel();
        var toggle = new SwitchToggle("Bluetooth", _net.BluetoothOn) { IsEnabled = _net.BluetoothAvailable };
        toggle.Click += async (_, _) =>
        {
            toggle.IsEnabled = false;
            await _net.SetBluetooth(toggle.IsChecked == true);
            toggle.IsChecked = _net.BluetoothOn;
            toggle.IsEnabled = true;
            await Fill();
        };
        var page = Sub("Bluetooth", toggle);
        page.Children.Add(Card(list, padding: new Thickness(10)));
        page.Children.Add(FlatButtonFull("Adicionar ou conectar dispositivos…", () => { SafeClose(); DockController.Open("ms-settings:bluetooth"); }));

        async Task Fill()
        {
            list.Children.Clear();
            list.Children.Add(Subtle("Procurando…"));
            var devices = await ConnectivityService.PairedDevicesAsync();
            list.Children.Clear();
            foreach (var d in devices)
            {
                var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
                var icon = Glyph.Text(Glyph.Bluetooth, 14);
                icon.Margin = new Thickness(0, 0, 10, 0);
                row.Children.Add(icon);
                var label = new StackPanel();
                label.Children.Add(new TextBlock { Text = d.Name, TextTrimming = TextTrimming.CharacterEllipsis });
                label.Children.Add(Subtle(d.Connected ? "Conectado" : "Pareado"));
                row.Children.Add(label);
                list.Children.Add(row);
            }
            if (devices.Count == 0) list.Children.Add(Subtle("Nenhum dispositivo pareado"));
        }
        await Fill();
    }

    async void ShowNetwork()
    {
        var page = Sub("Rede");
        var state = new StackPanel();
        page.Children.Add(Card(state, padding: new Thickness(10)));
        var name = new TextBlock { FontWeight = FontWeights.SemiBold };
        state.Children.Add(name);
        state.Children.Add(Subtle("Conexão ativa"));
        void Update() => name.Text = _net.Connection ?? "Sem conexão";
        _refresh.Add(Update);
        Update();

        if (_net.WifiAvailable)
        {
            var toggle = new SwitchToggle("Wi-Fi", _net.WifiOn);
            toggle.Click += async (_, _) =>
            {
                await _net.SetWifi(toggle.IsChecked == true);
                toggle.IsChecked = _net.WifiOn;
            };
            var header = new DockPanel { Margin = new Thickness(2, 12, 2, 6) };
            DockPanel.SetDock(toggle, System.Windows.Controls.Dock.Right);
            header.Children.Add(toggle);
            header.Children.Add(new TextBlock { Text = "Wi-Fi", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            page.Children.Add(header);
            var list = new StackPanel();
            list.Children.Add(Subtle("Procurando redes…"));
            page.Children.Add(Card(list, padding: new Thickness(6)));
            var networks = await ConnectivityService.ScanWifiAsync();
            list.Children.Clear();
            foreach (var n in networks.Take(10))
            {
                var entry = n;
                list.Children.Add(Row(Glyph.Wifi, n.Name, $"{n.Signal * 100:0}%", n.Connected, async () =>
                {
                    if (!await ConnectivityService.ConnectAsync(entry)) DockController.Open("ms-availablenetworks:");
                }));
            }
            if (networks.Count == 0) list.Children.Add(Subtle("Nenhuma rede encontrada"));
        }
        page.Children.Add(FlatButtonFull("Configurações de rede…", () => { SafeClose(); DockController.Open("ms-settings:network"); }));
    }
}
