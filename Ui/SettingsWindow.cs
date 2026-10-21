using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ClonaDock.Core;
using ClonaDock.Native;
using ClonaDock.Widgets;

namespace ClonaDock.Ui;

/// <summary>All settings in one window; every change applies to the Dock right away.</summary>
public sealed class SettingsWindow : Window
{
    readonly ListBox _nav;
    readonly ContentControl _page;
    readonly DispatcherTimer _debounce;
    readonly Dictionary<string, Func<UIElement>> _pages;

    static readonly Brush Subtle = new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x68));
    static readonly Brush CardBack = Brushes.White;
    static readonly Brush CardBorder = new SolidColorBrush(Color.FromRgb(0xE3, 0xE3, 0xE8));

    AppSettings S => SettingsStore.Current;

    public SettingsWindow()
    {
        Title = "ClonaDock — Configurações";
        Width = 820;
        Height = 640;
        MinWidth = 700;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 13;
        Background = new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF6));
        try { Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/dock.ico")); } catch { }

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); SettingsStore.Save(); };

        _pages = new()
        {
            ["general"] = GeneralPage,
            ["look"] = LookPage,
            ["dock"] = DockPage,
            ["widgets"] = WidgetsPage,
            ["about"] = AboutPage,
        };

        _nav = new ListBox
        {
            Width = 200,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(8, 12, 8, 12),
        };
        foreach (var (key, label, glyph) in new[]
                 {
                     ("general", "Geral", ""), ("look", "Aparência", ""), ("dock", "Itens do Dock", ""),
                     ("widgets", "Widgets", ""), ("about", "Sobre", ""),
                 })
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 7, 6, 7) };
            row.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 15, Width = 28, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = label, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            _nav.Items.Add(new ListBoxItem { Content = row, Tag = key });
        }
        _nav.SelectionChanged += (_, _) => ShowPage();

        _page = new ContentControl();
        var scroll = new ScrollViewer { Content = _page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(24, 20, 24, 20) };

        var root = new DockPanel();
        DockPanel.SetDock(_nav, System.Windows.Controls.Dock.Left);
        root.Children.Add(_nav);
        root.Children.Add(scroll);
        Content = root;
        _nav.SelectedIndex = 0;
        Closed += (_, _) => { if (_debounce.IsEnabled) { _debounce.Stop(); SettingsStore.Save(); } };
    }

    public void Select(string key)
    {
        foreach (ListBoxItem item in _nav.Items)
            if ((string)item.Tag == key) _nav.SelectedItem = item;
    }

    void ShowPage()
    {
        if (_nav.SelectedItem is ListBoxItem { Tag: string key } && _pages.TryGetValue(key, out var build))
            _page.Content = build();
    }

    void Commit(bool now = false)
    {
        if (now) { _debounce.Stop(); SettingsStore.Save(); }
        else { _debounce.Stop(); _debounce.Start(); }
    }

    // ---------------------------------------------------------------- building blocks

    static StackPanel Page(string title, string? intro = null)
    {
        var p = new StackPanel { MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left };
        p.Children.Add(new TextBlock { Text = title, FontSize = 26, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        if (intro != null) p.Children.Add(new TextBlock { Text = intro, Foreground = Subtle, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        return p;
    }

    static StackPanel Card(Panel page, string? header = null)
    {
        if (header != null)
            page.Children.Add(new TextBlock { Text = header, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 14, 0, 6) });
        var inner = new StackPanel();
        page.Children.Add(new Border
        {
            Background = CardBack,
            BorderBrush = CardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 6, 16, 6),
            Child = inner,
        });
        return inner;
    }

    static void Row(Panel card, string label, string? help, UIElement control)
    {
        var grid = new Grid { Margin = new Thickness(0, 8, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        text.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        if (help != null) text.Children.Add(new TextBlock { Text = help, Foreground = Subtle, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(control, 1);
        if (control is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(text);
        grid.Children.Add(control);
        if (card.Children.Count > 0)
            card.Children.Add(new Border { Height = 1, Background = CardBorder });
        card.Children.Add(grid);
    }

    CheckBox Toggle(bool value, Action<bool> set)
    {
        var c = new CheckBox { IsChecked = value };
        c.Click += (_, _) => { set(c.IsChecked == true); Commit(now: true); };
        return c;
    }

    ComboBox Choice<T>(IEnumerable<(T Value, string Label)> options, T current, Action<T> set)
    {
        var box = new ComboBox { Width = 220 };
        foreach (var (value, label) in options)
        {
            var item = new ComboBoxItem { Content = label, Tag = value };
            box.Items.Add(item);
            if (Equals(value, current)) box.SelectedItem = item;
        }
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is ComboBoxItem { Tag: T v }) { set(v); Commit(now: true); }
        };
        return box;
    }

    FrameworkElement Slider(double min, double max, double value, double step, Func<double, string> format, Action<double> set)
    {
        var label = new TextBlock { Width = 56, TextAlignment = TextAlignment.Right, Foreground = Subtle, VerticalAlignment = VerticalAlignment.Center };
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, Width = 200, TickFrequency = step, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
        label.Text = format(value);
        slider.ValueChanged += (_, e) => { label.Text = format(e.NewValue); set(e.NewValue); Commit(); };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(slider);
        panel.Children.Add(label);
        return panel;
    }

    static Button Btn(string text, Action click)
    {
        var b = new Button { Content = text, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0), MinWidth = 90 };
        b.Click += (_, _) => click();
        return b;
    }

    // ---------------------------------------------------------------- pages

    UIElement GeneralPage()
    {
        var page = Page("Geral", "Como o ClonaDock se encaixa no Windows.");

        var place = Card(page, "Posição");
        Row(place, "Borda da tela", "Em qual borda o Dock fica.", Choice(new[]
        {
            (DockEdge.Bottom, "Embaixo"), (DockEdge.Left, "À esquerda"), (DockEdge.Right, "À direita"), (DockEdge.Top, "No topo"),
        }, S.Edge, v => S.Edge = v));

        var monitors = Monitors.All();
        var options = new List<(int, string)> { (-1, "Monitor principal") };
        options.AddRange(monitors.Select((m, i) => (i, Monitors.Describe(m, i))));
        Row(place, "Monitor", null, Choice(options, S.Monitor, v => S.Monitor = v));

        var behaviour = Card(page, "Comportamento");
        Row(behaviour, "Barra de tarefas do Windows", "Ocultá-la dá mais espaço ao Dock. Ela volta como era quando você encerra o ClonaDock.", Choice(new[]
        {
            (TaskbarMode.Keep, "Deixar visível"), (TaskbarMode.AutoHide, "Ocultar automaticamente"), (TaskbarMode.Hide, "Ocultar por completo"),
        }, S.Taskbar, v => S.Taskbar = v));
        Row(behaviour, "Manter as janelas fora do Dock", "Janelas maximizadas param acima dele em vez de ficar por baixo.",
            Toggle(S.ReserveSpace, v => S.ReserveSpace = v));
        Row(behaviour, "Ocultar e mostrar automaticamente", "O Dock aparece quando o ponteiro encosta na borda da tela.",
            Toggle(S.AutoHide, v => S.AutoHide = v));
        Row(behaviour, "Atraso para mostrar", "Quanto tempo o ponteiro fica na borda antes de o Dock aparecer.",
            Slider(0, 1000, S.AutoHideDelayMs, 50, v => $"{v:0} ms", v => S.AutoHideDelayMs = (int)v));
        Row(behaviour, "Esconder com app em tela cheia", "Vídeos e jogos em tela cheia ficam por cima do Dock.",
            Toggle(S.HideOnFullScreen, v => S.HideOnFullScreen = v));
        Row(behaviour, "Abrir o ClonaDock quando o Windows iniciar", null,
            Toggle(S.StartWithWindows, v => S.StartWithWindows = v));
        return page;
    }

    UIElement LookPage()
    {
        var page = Page("Aparência", "Deixe o Dock com a sua cara.");

        var look = Card(page, "Barra");
        Row(look, "Claro ou escuro", "Automático segue o modo de cor do Windows.", Choice(new[]
        {
            (ThemeMode.Auto, "Automático"), (ThemeMode.Dark, "Escuro"), (ThemeMode.Light, "Claro"),
        }, S.Theme, v => S.Theme = v));
        Row(look, "Desfoque atrás da barra", "Leve é um desfoque puro, sem tom. Acrílico é o material do Windows, com tom cinza e granulado.", Choice(new[]
        {
            (0, "Nenhum"), (1, "Leve"), (2, "Acrílico"),
        }, !S.Glass ? 0 : S.Acrylic ? 2 : 1, v => { S.Glass = v > 0; S.Acrylic = v == 2; }));
        Row(look, "Opacidade do fundo", "Quanto da cor da barra aparece. 0% deixa só o vidro.",
            Slider(0, 1, S.BarOpacity, 0.05, v => $"{v * 100:0}%", v => S.BarOpacity = v));
        Row(look, "Borda Liquid Glass", "Brilho no topo e uma franja vermelho/ciano na borda, que imita a aberração cromática.",
            Toggle(S.LiquidGlass, v => S.LiquidGlass = v));
        Row(look, "Arredondamento da barra", "Com o Vidro ligado, o Windows 11 arredonda o desfoque com raio fixo de 8 px e a barra acompanha.", Slider(0, 32, S.CornerRadius, 1, v => $"{v:0} px", v => S.CornerRadius = v));
        Row(look, "Distância entre o Dock e a borda da tela", null, Slider(0, 30, S.EdgeGap, 1, v => $"{v:0} px", v => S.EdgeGap = v));
        Row(look, "Cor do fundo", "Tinge o vidro. Vazio usa a cor do tema.", ColorPicker(S.Tint, v => S.Tint = v));
        Row(look, "Cor do indicador", "A bolinha embaixo dos apps abertos.", ColorPicker(S.IndicatorColor, v => S.IndicatorColor = v));

        var icons = Card(page, "Ícones");
        Row(icons, "Tamanho dos ícones", null, Slider(28, 96, S.IconSize, 2, v => $"{v:0} px", v => S.IconSize = v));
        Row(icons, "Ampliação ao passar o mouse", "Quanto o ícone cresce. 1,0× desliga.", Slider(1, 2.5, S.Magnification, 0.05, v => $"{v:0.00}×", v => S.Magnification = v));
        Row(icons, "Mostrar o nome ao passar o mouse", null, Toggle(S.ShowLabels, v => S.ShowLabels = v));
        Row(icons, "Selos de notificação", "Bolinha vermelha com o número de não lidas, para apps que publicam selo no Windows (como o WhatsApp da Microsoft Store).",
            Toggle(S.ShowBadges, v => S.ShowBadges = v));
        Row(icons, "Animação de abertura", "O ícone pula enquanto o app abre.", Toggle(S.BounceOnLaunch, v => S.BounceOnLaunch = v));
        return page;
    }

    FrameworkElement ColorPicker(string current, Action<string> set)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var box = new TextBox { Text = current, Width = 90, Margin = new Thickness(8, 0, 0, 0), VerticalContentAlignment = VerticalAlignment.Center };
        string[] swatches = { "", "#1C1C20", "#F4F4F6", "#1E3A8A", "#7C3AED", "#BE185D", "#047857", "#B45309" };
        foreach (var hex in swatches)
        {
            var color = Theme.TryParse(hex, out var c) ? c : Colors.Transparent;
            var swatch = new Button
            {
                Width = 20,
                Height = 20,
                Margin = new Thickness(2, 0, 2, 0),
                Background = hex == "" ? Brushes.White : new SolidColorBrush(color),
                Content = hex == "" ? new TextBlock { Text = "A", FontSize = 10 } : null,
                ToolTip = hex == "" ? "Automático" : hex,
                BorderBrush = CardBorder,
            };
            var h = hex;
            swatch.Click += (_, _) => { box.Text = h; };
            panel.Children.Add(swatch);
        }
        box.TextChanged += (_, _) =>
        {
            var t = box.Text.Trim();
            if (t == "" || Theme.TryParse(t, out _)) { set(t); Commit(); }
        };
        panel.Children.Add(box);
        return panel;
    }

    UIElement DockPage()
    {
        var page = Page("Itens do Dock", "Arraste um ícone no Dock para reordenar; puxe-o para fora da barra para remover. Também dá para soltar arquivos e atalhos no Dock.");

        var fixedItems = Card(page, "Itens fixos");
        Row(fixedItems, "Botão de apps", "Primeiro no Dock; abre a lista de todos os apps do menu Iniciar.", Toggle(S.ShowLauncher, v => S.ShowLauncher = v));
        var iconButtons = new StackPanel { Orientation = Orientation.Horizontal };
        iconButtons.Children.Add(Btn("Escolher…", () => App.Dock?.ChooseLauncherIcon()));
        iconButtons.Children.Add(Btn("Original", Dock.DockController.ResetLauncherIcon));
        Row(fixedItems, "Ícone do botão de apps", "PNG, JPG ou ICO, ou um app para usar o ícone dele. Original volta aos 9 pontinhos.", iconButtons);
        Row(fixedItems, "Mostrar apps abertos", "Apps abertos que não estão fixados, depois de um separador.", Toggle(S.ShowOpenApps, v => S.ShowOpenApps = v));
        Row(fixedItems, "Pasta Downloads", null, Toggle(S.ShowDownloads, v => S.ShowDownloads = v));
        Row(fixedItems, "Lixeira no fim do Dock", null, Toggle(S.ShowRecycleBin, v => S.ShowRecycleBin = v));

        page.Children.Add(new TextBlock { Text = "Apps, pastas e arquivos", FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 14, 0, 6) });
        var list = new ListBox { Height = 240, BorderBrush = CardBorder };
        void Fill()
        {
            list.Items.Clear();
            foreach (var item in S.Items.Where(i => i.Kind is DockItemKind.App or DockItemKind.Folder or DockItemKind.File or DockItemKind.Spacer))
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2) };
                if (item.Kind != DockItemKind.Spacer)
                    row.Children.Add(new Image { Source = IconLoader.Get(item.Path, 32, cache: false), Width = 22, Height = 22, Margin = new Thickness(0, 0, 8, 0) });
                var name = item.Kind == DockItemKind.Spacer ? (item.LargeSpacer ? "— Espaçador grande —" : "— Espaçador pequeno —")
                    : item.Name ?? Path.GetFileNameWithoutExtension(item.Path.TrimEnd('\\'));
                row.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
                if (item.Kind != DockItemKind.Spacer)
                    row.Children.Add(new TextBlock { Text = "  " + item.Path, Foreground = Subtle, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
                list.Items.Add(new ListBoxItem { Content = row, Tag = item });
            }
        }
        Fill();
        SettingsStore.Changed += Refill;
        void Refill() => Dispatcher.BeginInvoke(Fill);
        list.Unloaded += (_, _) => SettingsStore.Changed -= Refill;
        page.Children.Add(list);

        DockItem? Selected() => (list.SelectedItem as ListBoxItem)?.Tag as DockItem;
        void Move(int delta)
        {
            if (Selected() is not DockItem item) return;
            int i = S.Items.IndexOf(item), j = i + delta;
            if (j < 0 || j >= S.Items.Count) return;
            (S.Items[i], S.Items[j]) = (S.Items[j], S.Items[i]);
            SettingsStore.Save();
            Dispatcher.BeginInvoke(() =>
            {
                foreach (ListBoxItem li in list.Items) if (li.Tag == item) list.SelectedItem = li;
            });
        }

        var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(Btn("Adicionar app…", () =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Programas e atalhos|*.exe;*.lnk;*.url;*.appref-ms|Todos os arquivos|*.*",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            };
            if (dlg.ShowDialog(this) == true) App.Dock?.AddPath(dlg.FileName);
        }));
        buttons.Children.Add(Btn("Adicionar pasta…", () =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog();
            if (dlg.ShowDialog(this) == true) App.Dock?.AddPath(dlg.FolderName);
        }));
        buttons.Children.Add(Btn("Subir", () => Move(-1)));
        buttons.Children.Add(Btn("Descer", () => Move(+1)));
        buttons.Children.Add(Btn("Remover", () =>
        {
            if (Selected() is not DockItem item) return;
            S.Items.Remove(item);
            SettingsStore.Save();
        }));
        page.Children.Add(buttons);

        var hidden = Card(page, "Apps que nunca aparecem");
        void FillHidden()
        {
            hidden.Children.Clear();
            if (S.HiddenApps.Count == 0)
                hidden.Children.Add(new TextBlock
                {
                    Text = "Nenhum. Clique com o botão direito num app aberto no Dock e escolha “Nunca mostrar no Dock”.",
                    Foreground = Subtle,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 8),
                });
            foreach (var app in S.HiddenApps.ToList())
            {
                var a = app;
                Row(hidden, a.Name, a.Key, Btn("Mostrar de novo", () =>
                {
                    S.HiddenApps.Remove(a);
                    SettingsStore.Save();
                    FillHidden();
                }));
            }
        }
        FillHidden();
        return page;
    }

    UIElement WidgetsPage()
    {
        var page = Page("Widgets", "Tiles vivos, não só ícones. Clique num widget no Dock para usá-lo; o botão direito traz as opções dele.");

        var options = Card(page, "Ajustes");
        Row(options, "Largura dos tiles", "Em tamanhos de ícone.", Slider(1.2, 3, S.TileWidth, 0.1, v => $"{v:0.0}×", v => S.TileWidth = v));
        var cityText = new TextBlock { Text = string.IsNullOrEmpty(S.WeatherCity) ? "Nenhuma" : S.WeatherCity, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        var cityPanel = new StackPanel { Orientation = Orientation.Horizontal };
        cityPanel.Children.Add(cityText);
        cityPanel.Children.Add(Btn("Trocar cidade…", () =>
        {
            var place = CityPicker.Ask();
            if (place == null) return;
            S.WeatherCity = place.Name;
            S.WeatherLatitude = place.Latitude;
            S.WeatherLongitude = place.Longitude;
            cityText.Text = place.Name;
            SettingsStore.Save();
        }));
        Row(options, "Cidade do clima", "Previsão do Open-Meteo; só as coordenadas da cidade são enviadas.", cityPanel);
        Row(options, "Temperatura em Fahrenheit", null, Toggle(S.Fahrenheit, v => S.Fahrenheit = v));

        var current = Card(page, "No seu Dock");
        void FillCurrent()
        {
            current.Children.Clear();
            var widgets = S.Items.Where(i => i.Kind == DockItemKind.Widget).ToList();
            if (widgets.Count == 0)
                current.Children.Add(new TextBlock { Text = "Nenhum widget ainda. Escolha um abaixo.", Foreground = Subtle, Margin = new Thickness(0, 8, 0, 8) });
            foreach (var w in widgets)
            {
                var item = w;
                Row(current, WidgetFactory.Name(w.Widget), null, Btn("Remover", () =>
                {
                    S.Items.Remove(item);
                    SettingsStore.Save();
                    FillCurrent();
                }));
            }
        }
        FillCurrent();

        var available = Card(page, "Adicionar");
        foreach (var kind in Enum.GetValues<WidgetKind>())
        {
            var k = kind;
            Row(available, WidgetFactory.Name(k), Describe(k), Btn("Adicionar", () =>
            {
                App.Dock?.AddItem(new DockItem { Kind = DockItemKind.Widget, Widget = k });
                FillCurrent();
            }));
        }
        return page;
    }

    static string Describe(WidgetKind kind) => kind switch
    {
        WidgetKind.Clock => "Hora local ou de outra cidade, com segundos se quiser.",
        WidgetKind.Calendar => "O dia de hoje; clique para ver o mês.",
        WidgetKind.Weather => "Temperatura agora e previsão de 6 dias.",
        WidgetKind.Note => "Uma nota rápida sempre à vista.",
        WidgetKind.Todo => "Tarefas pendentes, com a próxima em destaque.",
        WidgetKind.FocusTimer => "Pomodoro: sessões de foco e pausas.",
        WidgetKind.SystemActivity => "Uso de CPU e memória.",
        WidgetKind.Network => "Download e upload, com gráfico.",
        WidgetKind.Battery => "Carga e se está na tomada.",
        WidgetKind.Water => "Cada clique soma um copo; zera todo dia.",
        WidgetKind.Countdown => "Dias até uma data.",
        WidgetKind.TimeProgress => "Quanto do dia, da semana e do ano já passou.",
        WidgetKind.NowPlaying => "Spotify, navegadores e players dos controles de mídia do Windows.",
        _ => "",
    };

    UIElement AboutPage()
    {
        var page = Page("Sobre");
        var card = Card(page);
        var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        card.Children.Add(new TextBlock { Text = $"ClonaDock {version}", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4) });
        card.Children.Add(new TextBlock
        {
            Text = "Um Dock no estilo do macOS para Windows, com widgets. Reimplementação independente, " +
                   "feita a partir do comportamento observado do PrimoDock 0.6.0-beta (sem código do original).",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Subtle,
            Margin = new Thickness(0, 0, 0, 10),
        });
        var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        buttons.Children.Add(Btn("Abrir pasta de dados", () =>
        {
            Directory.CreateDirectory(SettingsStore.DataFolder);
            Process.Start(new ProcessStartInfo(SettingsStore.DataFolder) { UseShellExecute = true });
        }));
        buttons.Children.Add(Btn("Encerrar ClonaDock", () => Application.Current.Shutdown()));
        card.Children.Add(buttons);
        return page;
    }
}
