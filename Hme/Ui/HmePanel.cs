using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Shft.Ui;

namespace Shft.Hme;

/// <summary>
/// hme.'s panel in the Control Center's style: the air conditioner, then the home by room. While it is open the
/// home is read at most once a minute (the engine's cache decides); closed, nothing reads the cloud.
/// </summary>
public sealed class HmePanel : FlyoutWindow
{
    const double PanelWidth = 340;

    static readonly (int H, string Name)[] Colours =
        [(0, "Vermelho"), (30, "Laranja"), (60, "Amarelo"), (120, "Verde"), (180, "Ciano"), (210, "Azul"), (270, "Roxo"), (320, "Rosa")];

    static readonly Dictionary<string, string> ModeNames = new()
    {
        ["cool"] = "Frio", ["heat"] = "Quente", ["auto"] = "Auto", ["fan"] = "Ventilar", ["dry"] = "Secar",
    };

    static readonly (string Id, string Name)[] FanNames = [("auto", "Auto"), ("low", "Baixa"), ("mid", "Média"), ("high", "Alta")];

    readonly EngineHost _host;
    readonly StackPanel _root = new() { Width = PanelWidth };
    readonly ScrollViewer _scroll;
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(60) };
    readonly DispatcherTimer _errorTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    readonly HashSet<string> _expanded = new();
    HomeState? _state;
    string? _room, _error;
    bool _busy;

    public HmePanel(EngineHost host)
    {
        _host = host;
        Radius = 26;
        Inner = new Thickness(12);
        _scroll = new ScrollViewer { Content = _root, MaxHeight = 640, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Body = _scroll;
        _poll.Tick += (_, _) => _ = Load(false);
        _errorTimer.Tick += (_, _) => { _errorTimer.Stop(); _error = null; Draw(); };
        _host.Changed += OnHostChanged;
        Closed += (_, _) =>
        {
            _poll.Stop();
            _errorTimer.Stop();
            _host.Changed -= OnHostChanged;
        };
        Draw();
        _ = Load(true);
        _poll.Start();
    }

    void OnHostChanged() => Dispatcher.BeginInvoke(() => _ = Load(true));

    /// <summary>Draws the cache at once, then reads the home (the engine decides whether that touches the cloud).</summary>
    async Task Load(bool cacheFirst)
    {
        var home = _host.Home;
        if (home == null)
        {
            _state = null;
            Draw();
            return;
        }
        try
        {
            if (cacheFirst)
            {
                _state = await home.State(cacheOnly: true);
                Draw();
            }
            home.MarkActivity();
            _state = await home.State();
        }
        catch (Exception ex) when (ex is HmeCloudException or PositivoException or System.Net.Http.HttpRequestException)
        {
            ShowError(ex.Message);
        }
        Draw();
    }

    async Task Run(Func<IHome, Task<HomeState>> action)
    {
        if (_host.Home is not { } home) return;
        _busy = true;
        try
        {
            _state = await action(home);
        }
        catch (Exception ex) when (ex is HmeCloudException or PositivoException or System.Net.Http.HttpRequestException)
        {
            ShowError(ex.Message);
        }
        finally
        {
            _busy = false;
        }
        Draw();
    }

    void ShowError(string message)
    {
        _error = message;
        _errorTimer.Stop();
        _errorTimer.Start();
    }

    static Border Card(UIElement child, double radius = 18, Thickness? padding = null)
    {
        var b = new Border { CornerRadius = new CornerRadius(radius), Padding = padding ?? new Thickness(12), BorderThickness = new Thickness(1), Child = child, Margin = new Thickness(0, 10, 0, 0) };
        b.SetResourceReference(Border.BackgroundProperty, "TileBrush");
        b.SetResourceReference(Border.BorderBrushProperty, "TileRimBrush");
        return b;
    }

    static Button Chip(string text, bool on, Action click)
    {
        var label = new TextBlock { Text = text, FontSize = 12, FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal };
        var b = FlatButton(label, click, 8);
        b.Margin = new Thickness(0, 0, 6, 6);
        b.SetResourceReference(BackgroundProperty, on ? "TileOnBrush" : "TileBrush");
        if (on) label.SetResourceReference(TextBlock.ForegroundProperty, "TileOnTextBrush");
        AutomationProperties.SetItemStatus(b, on ? "selecionado" : "");
        return b;
    }

    void Draw()
    {
        double scroll = _scroll.VerticalOffset;
        _root.Children.Clear();
        _root.Children.Add(Header());
        if (_host.Home == null)
        {
            _root.Children.Add(Setup());
            return;
        }
        if (_state == null)
        {
            _root.Children.Add(Card(Subtle("Carregando…")));
            return;
        }
        if (_state.Ac is { Available: true } ac) _root.Children.Add(AcCard(ac));
        _root.Children.Add(RoomsBar());
        _root.Children.Add(DeviceList());
        if ((_error ?? _state.Notice) is { } note)
        {
            var t = Subtle(note);
            t.Margin = new Thickness(4, 10, 4, 0);
            if (_error != null) t.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A));
            _root.Children.Add(t);
        }
        _scroll.ScrollToVerticalOffset(scroll);
    }

    UIElement Header()
    {
        var row = new DockPanel { Margin = new Thickness(2, 0, 0, 0) };
        var settings = FlatButton(Glyph.Text(Glyph.Settings, 14), () => CloseThen(() => HmeSettingsWindow.ShowFor(_host)), 7);
        AutomationProperties.SetName(settings, "Ajustes do hme.");
        settings.ToolTip = "Ajustes do hme.";
        DockPanel.SetDock(settings, System.Windows.Controls.Dock.Right);
        row.Children.Add(settings);
        var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new TextBlock { Text = "hme.", FontSize = 15, FontWeight = FontWeights.SemiBold });
        if (_state is { } s)
        {
            var detail = s.RoomTemp is int t ? $"{s.RoomName} · {t}°" : s.Demo ? "demonstração" : "";
            if (s.Demo && s.RoomTemp is int) detail += " · demonstração";
            if (detail.Length > 0)
            {
                var d = Subtle(detail);
                d.Margin = new Thickness(8, 2, 0, 0);
                title.Children.Add(d);
            }
        }
        row.Children.Add(title);
        return row;
    }

    UIElement Setup()
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = _host.BootError != null ? "O hme. não conseguiu conectar" : "Configure o hme.",
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4),
        });
        stack.Children.Add(Subtle(_host.BootError ?? "Conecte sua conta do Smart Life (ou use a demonstração) para controlar ar, luzes e tomadas daqui."));
        var go = FlatButton(new TextBlock { Text = _host.BootError != null ? "Abrir ajustes" : "Configurar", FontWeight = FontWeights.SemiBold }, () => CloseThen(() => HmeSettingsWindow.ShowFor(_host)));
        go.Margin = new Thickness(-8, 8, 0, 0);
        stack.Children.Add(go);
        return Card(stack);
    }

    // ---------------------------------------------------------------- air conditioner

    UIElement AcCard(AcState ac)
    {
        var stack = new StackPanel();
        var top = new DockPanel();
        var power = new ControlTile(Glyph.Power, "Ar-condicionado", ac.Power ? $"Ligado · {ModeNames.GetValueOrDefault(ac.Mode, ac.Mode)}" : "Desligado", wide: true, circle: 34) { IsOn = ac.Power };
        power.Click += (_, _) => _ = Run(h => h.SetAc(new AcChange(Power: !ac.Power)));
        power.IsBusy = _busy;

        var temp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        var minus = FlatButton(new TextBlock { Text = "−", FontSize = 18, Width = 14, TextAlignment = TextAlignment.Center }, () => _ = Run(h => h.SetAc(new AcChange(Temp: ac.Temp - 1))), 6);
        var plus = FlatButton(new TextBlock { Text = "+", FontSize = 18, Width = 14, TextAlignment = TextAlignment.Center }, () => _ = Run(h => h.SetAc(new AcChange(Temp: ac.Temp + 1))), 6);
        AutomationProperties.SetName(minus, "Diminuir temperatura");
        AutomationProperties.SetName(plus, "Aumentar temperatura");
        minus.IsEnabled = ac.Temp > ac.TempMin;
        plus.IsEnabled = ac.Temp < ac.TempMax;
        temp.Children.Add(minus);
        temp.Children.Add(new TextBlock { Text = $"{ac.Temp}°", FontSize = 22, FontWeight = FontWeights.SemiBold, Width = 46, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        temp.Children.Add(plus);
        DockPanel.SetDock(temp, System.Windows.Controls.Dock.Right);
        top.Children.Add(temp);
        top.Children.Add(power);
        stack.Children.Add(top);

        var modes = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var m in ac.Modes)
            modes.Children.Add(Chip(ModeNames.GetValueOrDefault(m, m), ac.Mode == m, () => _ = Run(h => h.SetAc(new AcChange(Mode: m)))));
        stack.Children.Add(modes);
        var fans = new WrapPanel();
        foreach (var (id, name) in FanNames)
            fans.Children.Add(Chip(name, !ac.Turbo && ac.Fan == id, () => _ = Run(h => h.SetAc(new AcChange(Fan: id, Turbo: ac.Turbo ? false : null)))));
        fans.Children.Add(Chip("Turbo", ac.Turbo, () => _ = Run(h => h.SetAc(new AcChange(Turbo: !ac.Turbo)))));
        stack.Children.Add(fans);
        if (ac.Error.Length > 0)
        {
            var e = Subtle(ac.Error);
            e.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A));
            stack.Children.Add(e);
        }
        return Card(stack);
    }

    // ---------------------------------------------------------------- home

    UIElement RoomsBar()
    {
        var rooms = _state!.Rooms;
        if (_room != null && !rooms.Contains(_room)) _room = null;
        var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        bool anyOn = _state.Devices.Any(d => d.Kind != DeviceKind.Plug && d.Online && d.On && (_room == null || d.Room == _room));
        var off = Chip(_room == null ? "Apagar tudo" : "Apagar", false, () => _ = Run(h => h.LightsOff(_room)));
        off.IsEnabled = anyOn && !_busy;
        off.ToolTip = _room == null ? "Apaga luzes e interruptores da casa toda" : $"Apaga luzes e interruptores de {_room}";
        DockPanel.SetDock(off, System.Windows.Controls.Dock.Right);
        row.Children.Add(off);
        var chips = new WrapPanel();
        chips.Children.Add(Chip("Todos", _room == null, () => { _room = null; Draw(); }));
        foreach (var r in rooms) chips.Children.Add(Chip(r, _room == r, () => { _room = r; Draw(); }));
        row.Children.Add(chips);
        return row;
    }

    UIElement DeviceList()
    {
        var list = new StackPanel();
        var devices = _state!.Devices.Where(d => _room == null || d.Room == _room).ToList();
        if (devices.Count == 0)
        {
            var empty = Subtle(_state.Devices.Count == 0 ? "Nenhum aparelho ainda. Veja os Ajustes do hme." : "Nada neste cômodo.");
            empty.Margin = new Thickness(4);
            list.Children.Add(empty);
        }
        foreach (var group in devices.GroupBy(d => d.Room))
        {
            if (_room == null)
            {
                var head = Subtle(group.Key);
                head.FontWeight = FontWeights.SemiBold;
                head.Margin = new Thickness(6, list.Children.Count == 0 ? 2 : 10, 0, 4);
                list.Children.Add(head);
            }
            foreach (var d in group) list.Children.Add(Row(d));
        }
        return Card(list, padding: new Thickness(6));
    }

    UIElement Row(Device d)
    {
        var stack = new StackPanel { Opacity = d.Online ? 1 : 0.45 };
        var row = new DockPanel { Margin = new Thickness(6, 5, 6, 5) };
        var glyph = Glyph.Text(d.Kind switch { DeviceKind.Light => Glyph.Bulb, DeviceKind.Plug => Glyph.Plug, DeviceKind.Multi => Glyph.Controls, _ => Glyph.Power }, 14);
        glyph.Margin = new Thickness(0, 0, 10, 0);
        if (d.On && d.Online) glyph.SetResourceReference(GlyphView.ForegroundProperty, "AccentBrush");
        DockPanel.SetDock(glyph, System.Windows.Controls.Dock.Left);
        row.Children.Add(glyph);

        if (d.Kind != DeviceKind.Multi)
        {
            var toggle = new SwitchToggle(d.Name, d.On) { IsEnabled = d.Online && !_busy, VerticalAlignment = VerticalAlignment.Center };
            toggle.Click += (_, _) => _ = Run(h => h.SetDevice(d.Id, on: toggle.IsChecked == true));
            DockPanel.SetDock(toggle, System.Windows.Controls.Dock.Right);
            row.Children.Add(toggle);
        }
        if (d.Kind == DeviceKind.Light && d.Online && d.Light is { } l && (l.HasBright || l.HasColor || l.HasTemp))
        {
            bool open = _expanded.Contains(d.Id);
            var more = FlatButton(Glyph.Text(open ? Glyph.Up : Glyph.Down, 11), () =>
            {
                if (!_expanded.Remove(d.Id)) _expanded.Add(d.Id);
                Draw();
            }, 5);
            AutomationProperties.SetName(more, open ? "Esconder ajustes da lâmpada" : "Ajustar a lâmpada");
            more.Margin = new Thickness(0, 0, 6, 0);
            DockPanel.SetDock(more, System.Windows.Controls.Dock.Right);
            row.Children.Add(more);
        }
        var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(new TextBlock { Text = d.Name, TextTrimming = TextTrimming.CharacterEllipsis });
        if (!d.Online) name.Children.Add(Subtle("Offline"));
        row.Children.Add(name);
        stack.Children.Add(row);

        if (d.Kind == DeviceKind.Multi)
        {
            foreach (var c in d.Channels)
            {
                var line = new DockPanel { Margin = new Thickness(30, 2, 6, 4) };
                var t = new SwitchToggle($"{d.Name}: {c.Name}", c.On) { IsEnabled = d.Online && !_busy };
                t.Click += (_, _) => _ = Run(h => h.SetDevice(d.Id, on: t.IsChecked == true, channel: c.Code));
                DockPanel.SetDock(t, System.Windows.Controls.Dock.Right);
                line.Children.Add(t);
                line.Children.Add(new TextBlock { Text = c.Name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
                stack.Children.Add(line);
            }
        }
        if (d.Light is { } light && _expanded.Contains(d.Id) && d.Online) stack.Children.Add(LightControls(d, light));
        return stack;
    }

    UIElement LightControls(Device d, LightState l)
    {
        var stack = new StackPanel { Margin = new Thickness(30, 2, 6, 8) };
        if (l.HasBright || l.HasColor)
        {
            var bright = new CapsuleSlider("Brilho de " + d.Name, Glyph.Sun, 26) { Value = l.Brightness / 100.0 };
            Debounce(bright, v => Run(h => h.SetDevice(d.Id, new LightChange(Brightness: Math.Max(1, (int)Math.Round(v * 100))))));
            stack.Children.Add(bright);
        }
        if (l.HasTemp)
        {
            var warm = new CapsuleSlider("Branco quente ou frio de " + d.Name, Glyph.Bulb, 26) { Value = l.Temp / 100.0, Margin = new Thickness(0, 8, 0, 0) };
            warm.ToolTip = "Esquerda: quente · direita: frio";
            Debounce(warm, v => Run(h => h.SetDevice(d.Id, new LightChange(Temp: (int)Math.Round(v * 100)))));
            stack.Children.Add(warm);
        }
        if (l.HasColor)
        {
            var swatches = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (var (h, label) in Colours)
            {
                bool current = l.Mode == "colour" && Math.Abs(l.H - h) < 8 && l.S > 50;
                var dot = new Border
                {
                    Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 8, 0),
                    Background = new SolidColorBrush(FromHue(h)), Cursor = Cursors.Hand, BorderThickness = new Thickness(current ? 2 : 0),
                };
                dot.SetResourceReference(Border.BorderBrushProperty, "TextBrush");
                AutomationProperties.SetName(dot, label);
                dot.ToolTip = label;
                dot.MouseLeftButtonUp += (_, _) => _ = Run(x => x.SetDevice(d.Id, new LightChange(Color: (h, 100))));
                swatches.Children.Add(dot);
            }
            stack.Children.Add(swatches);
        }
        return stack;
    }

    /// <summary>Sends only after the hand stops: a drag would otherwise be dozens of cloud calls.</summary>
    void Debounce(CapsuleSlider slider, Func<double, Task> send)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        double last = slider.Value;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _ = send(last);
        };
        slider.ValueChanged += v =>
        {
            last = v;
            timer.Stop();
            timer.Start();
        };
        Closed += (_, _) => timer.Stop();
    }

    static Color FromHue(int h)
    {
        double x = 1 - Math.Abs(h / 60.0 % 2 - 1);
        var (r, g, b) = (h / 60) switch { 0 => (1.0, x, 0.0), 1 => (x, 1.0, 0.0), 2 => (0.0, 1.0, x), 3 => (0.0, x, 1.0), 4 => (x, 0.0, 1.0), _ => (1.0, 0.0, x) };
        return Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }
}
