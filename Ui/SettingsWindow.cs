using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Shft.Core;
using Shft.Native;
using Shft.Widgets;

namespace Shft.Ui;

/// <summary>All of shft.'s settings in one window; every change applies right away.</summary>
public sealed class SettingsWindow : Window
{
    readonly ListBox _nav;
    readonly ContentControl _page;
    readonly DispatcherTimer _debounce;
    readonly Dictionary<string, Func<UIElement>> _pages;

    AppSettings S => SettingsStore.Current;

    public SettingsWindow()
    {
        Title = "shft. — Configurações";
        Width = 860;
        Height = 660;
        MinWidth = 720;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 13;
        SetResourceReference(BackgroundProperty, "MenuBackBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        try { Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/shft.ico")); } catch { }

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); SettingsStore.Save(); };

        _pages = new()
        {
            ["look"] = LookPage,
            ["bar"] = BarPage,
            ["dock"] = DockPage,
            ["items"] = ItemsPage,
            ["panel"] = PanelPage,
            ["apps"] = AppsPage,
            ["motion"] = MotionPage,
            ["behaviour"] = BehaviourPage,
            ["system"] = SystemPage,
            ["widgets"] = WidgetsPage,
            ["about"] = AboutPage,
        };

        _nav = new ListBox
        {
            Width = 216,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(10, 14, 10, 12),
            ItemContainerStyle = (Style)Application.Current.Resources["AppRowItem"],
        };
        foreach (var (key, label, glyph) in new[]
                 {
                     ("look", "Aparência", ""), ("bar", "Barra superior", ""), ("dock", "Dock", ""), ("items", "Itens do Dock", ""),
                     ("panel", "Central de Controle", Glyph.Controls), ("apps", "Applications", Glyph.Search), ("motion", "Animações", ""),
                     ("behaviour", "Comportamento", ""), ("system", "Inicialização e sistema", Glyph.Power), ("widgets", "Widgets", ""), ("about", "Sobre", ""),
                 })
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = glyph, FontFamily = Glyph.Font, FontSize = 15, Width = 28, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = label, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center });
            var item = new ListBoxItem { Content = row, Tag = key, Focusable = true };
            AutomationProperties.SetName(item, label);
            _nav.Items.Add(item);
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

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Title bar in the same light or dark as the rest.
        int dark = Theme.Resolve(S).IsDark ? 1 : 0;
        Win32.DwmSetWindowAttribute(new System.Windows.Interop.WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
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

    static TextBlock Muted(string text, double size = 12)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextSubtleBrush");
        return t;
    }

    static StackPanel Page(string title, string? intro = null)
    {
        var p = new StackPanel { MaxWidth = 620, HorizontalAlignment = HorizontalAlignment.Left };
        p.Children.Add(new TextBlock { Text = title, FontSize = 26, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"), Margin = new Thickness(0, 0, 0, 4) });
        if (intro != null)
        {
            var t = Muted(intro, 13);
            t.Margin = new Thickness(0, 0, 0, 12);
            p.Children.Add(t);
        }
        return p;
    }

    static StackPanel Card(Panel page, string? header = null)
    {
        if (header != null)
            page.Children.Add(new TextBlock { Text = header, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 14, 0, 6) });
        var inner = new StackPanel();
        var card = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 6, 16, 6), Child = inner };
        card.SetResourceReference(Border.BackgroundProperty, "TileBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "TileRimBrush");
        page.Children.Add(card);
        return inner;
    }

    static void Row(Panel card, string label, string? help, UIElement control)
    {
        var grid = new Grid { Margin = new Thickness(0, 8, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        text.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        if (help != null) text.Children.Add(Muted(help));
        Grid.SetColumn(control, 1);
        if (control is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(control, label);
        grid.Children.Add(text);
        grid.Children.Add(control);
        if (card.Children.Count > 0)
        {
            var line = new Border { Height = 1 };
            line.SetResourceReference(Border.BackgroundProperty, "TileRimBrush");
            card.Children.Add(line);
        }
        card.Children.Add(grid);
    }

    SwitchToggle Toggle(bool value, Action<bool> set)
    {
        var c = new SwitchToggle("", value);
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
        var label = Muted(format(value), 13);
        label.Width = 60;
        label.TextAlignment = TextAlignment.Right;
        label.VerticalAlignment = VerticalAlignment.Center;
        var slider = new CapsuleSlider("", thin: true) { Width = 200, VerticalAlignment = VerticalAlignment.Center, Value = (value - min) / (max - min) };
        slider.ValueChanged += v =>
        {
            double snapped = Math.Round((min + v * (max - min)) / step) * step;
            label.Text = format(snapped);
            set(snapped);
            Commit();
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(slider);
        panel.Children.Add(label);
        return panel;
    }

    static Button Btn(string text, Action click)
    {
        var b = FlyoutWindow.FlatButton(new TextBlock { Text = text }, click, 10);
        b.Margin = new Thickness(0, 0, 6, 0);
        return b;
    }

    TextBox Field(string value, Action<string> set, double width = 220)
    {
        var box = new TextBox { Text = value, Width = width, Padding = new Thickness(6, 4, 6, 4), BorderThickness = new Thickness(1), Background = Brushes.Transparent };
        box.SetResourceReference(ForegroundProperty, "TextBrush");
        box.SetResourceReference(TextBox.CaretBrushProperty, "TextBrush");
        box.SetResourceReference(BorderBrushProperty, "TileRimBrush");
        box.TextChanged += (_, _) => { set(box.Text.Trim()); Commit(); };
        return box;
    }

    /// <summary>Records a shortcut: focus the box and press the keys. Backspace clears it.</summary>
    FrameworkElement HotkeyBox(string value, Action<string> set)
    {
        var box = new TextBox { Text = value, Width = 180, IsReadOnly = true, Padding = new Thickness(6, 4, 6, 4), Background = Brushes.Transparent, TextAlignment = TextAlignment.Center, BorderThickness = new Thickness(1) };
        box.SetResourceReference(ForegroundProperty, "TextBrush");
        box.SetResourceReference(BorderBrushProperty, "TileRimBrush");
        box.PreviewKeyDown += (_, e) =>
        {
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is Key.Back or Key.Delete)
            {
                box.Text = "";
                set("");
                Commit(now: true);
                return;
            }
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.Tab or Key.Escape) return;
            var mods = Keyboard.Modifiers;
            if (mods == ModifierKeys.None) return;
            var text = string.Join("+", new[]
            {
                mods.HasFlag(ModifierKeys.Control) ? "Ctrl" : null, mods.HasFlag(ModifierKeys.Alt) ? "Alt" : null,
                mods.HasFlag(ModifierKeys.Shift) ? "Shift" : null, mods.HasFlag(ModifierKeys.Windows) ? "Win" : null, key.ToString(),
            }.Where(p => p != null));
            box.Text = text;
            set(text);
            Commit(now: true);
        };
        return box;
    }

    // ---------------------------------------------------------------- pages

    UIElement LookPage()
    {
        var page = Page("Aparência", "A mesma identidade na barra, no Dock e nos painéis.");
        var look = Card(page, "Tema");
        Row(look, "Claro ou escuro", "Automático segue o modo de cor do Windows.", Choice(new[]
        {
            (ThemeMode.Auto, "Automático"), (ThemeMode.Dark, "Escuro"), (ThemeMode.Light, "Claro"),
        }, S.Theme, v => S.Theme = v));

        var glass = Card(page, "Vidro dos painéis");
        Row(glass, "Transparência", "Quanto o fundo aparece atrás da Central de Controle, de Applications e dos outros painéis.",
            Slider(0.1, 0.95, S.PanelOpacity, 0.05, v => $"{(1 - v) * 100:0}%", v => S.PanelOpacity = v));
        Row(glass, "Intensidade do desfoque", null, Slider(4, 24, S.PanelBlur, 1, v => $"{v:0}", v => S.PanelBlur = (int)v));
        Row(glass, "Borda Liquid Glass", "Brilho no contorno, como luz numa superfície curva.", Toggle(S.PanelRim, v => S.PanelRim = v));
        return page;
    }

    UIElement BarPage()
    {
        var page = Page("Barra superior", "Logo, app em foco, indicadores do sistema e relógio.");
        var main = Card(page);
        Row(main, "Mostrar a barra", null, Toggle(S.TopBarEnabled, v => S.TopBarEnabled = v));
        Row(main, "Em todos os monitores", null, Toggle(S.TopBarAllMonitors, v => S.TopBarAllMonitors = v));
        Row(main, "Manter as janelas abaixo da barra", "Janelas maximizadas param abaixo dela.", Toggle(S.TopBarReserve, v => S.TopBarReserve = v));
        Row(main, "Altura", null, Slider(24, 48, S.TopBarHeight, 1, v => $"{v:0} px", v => S.TopBarHeight = v));

        var look = Card(page, "Fundo");
        Row(look, "Modo", "Sólido usa a cor do tema. Translúcido deixa o fundo aparecer. Adaptativo fica transparente e troca a cor dos textos e ícones conforme o que está atrás.", Choice(new[]
        {
            (TopBarStyle.Solid, "Sólido"), (TopBarStyle.Translucent, "Translúcido"), (TopBarStyle.Adaptive, "Adaptativo"),
        }, S.TopBarStyle, v => S.TopBarStyle = v));
        Row(look, "Opacidade no modo translúcido", null, Slider(0, 1, S.TopBarOpacity, 0.05, v => $"{v * 100:0}%", v => S.TopBarOpacity = v));
        Row(look, "Desfoque no modo translúcido", null, Toggle(S.TopBarBlur, v => S.TopBarBlur = v));
        Row(look, "Contraste adaptativo", "No modo adaptativo, põe uma camada suave atrás do conteúdo quando o fundo é misto.", Toggle(S.TopBarScrim, v => S.TopBarScrim = v));

        var content = Card(page, "Conteúdo");
        var logo = new StackPanel { Orientation = Orientation.Horizontal };
        logo.Children.Add(Btn("Escolher…", LogoPicker.Choose));
        logo.Children.Add(Btn("Original", () => { S.TopBarLogo = ""; Commit(now: true); }));
        Row(content, "Logo", "Também dá para trocar com o botão direito na própria logo.", logo);
        Row(content, "Pintar a logo com a cor do texto", "Para logos de uma cor só, como a da Apple.", Toggle(S.TopBarLogoTint, v => S.TopBarLogoTint = v));
        Row(content, "Nome", "Vazio usa o nome do Windows.", Field(S.TopBarUserName, v => S.TopBarUserName = v));
        Row(content, "Mostrar o nome", null, Toggle(S.TopBarShowUser, v => S.TopBarShowUser = v));
        Row(content, "Mostrar o app em foco", null, Toggle(S.TopBarShowApp, v => S.TopBarShowApp = v));
        Row(content, "Mostrar a memória", "Clicar nela libera a RAM ociosa; o Windows pede confirmação.", Toggle(S.TopBarShowMemory, v => S.TopBarShowMemory = v));
        Row(content, "Formato do relógio", "ddd = dia, MMM = mês, HH = hora, mm = minutos, tt = AM/PM.", Field(S.TopBarClockFormat, v =>
        {
            try
            {
                _ = DateTime.Now.ToString(v, System.Globalization.CultureInfo.CurrentCulture);
                if (v.Length > 0) S.TopBarClockFormat = v;
            }
            catch (FormatException) { } // half-typed format: keep the last good one
        }));
        return page;
    }

    UIElement DockPage()
    {
        var page = Page("Dock", "Posição, tamanho e o vidro do Dock.");
        var place = Card(page, "Posição");
        Row(place, "Borda da tela", "Em qual borda o Dock fica.", Choice(new[]
        {
            (DockEdge.Bottom, "Embaixo"), (DockEdge.Left, "À esquerda"), (DockEdge.Right, "À direita"), (DockEdge.Top, "No topo"),
        }, S.Edge, v => S.Edge = v));
        var monitors = Monitors.All();
        var options = new List<(int, string)> { (-1, "Monitor principal") };
        options.AddRange(monitors.Select((m, i) => (i, Monitors.Describe(m, i))));
        Row(place, "Monitor", null, Choice(options, S.Monitor, v => S.Monitor = v));
        Row(place, "Distância da borda da tela", null, Slider(0, 30, S.EdgeGap, 1, v => $"{v:0} px", v => S.EdgeGap = v));

        var look = Card(page, "Barra");
        Row(look, "Desfoque atrás da barra", "Leve é um desfoque puro, sem tom. Acrílico é o material do Windows, com tom e granulado.", Choice(new[]
        {
            (0, "Nenhum"), (1, "Leve"), (2, "Acrílico"),
        }, !S.Glass ? 0 : S.Acrylic ? 2 : 1, v => { S.Glass = v > 0; S.Acrylic = v == 2; }));
        Row(look, "Opacidade do fundo", "0% deixa só o vidro.", Slider(0, 1, S.BarOpacity, 0.05, v => $"{v * 100:0}%", v => S.BarOpacity = v));
        Row(look, "Borda Liquid Glass", "Brilho no topo e uma franja de cor discreta.", Toggle(S.LiquidGlass, v => S.LiquidGlass = v));
        Row(look, "Arredondamento", "Com o desfoque ligado, o Windows 11 usa raio fixo de 8 px e a barra acompanha.", Slider(0, 32, S.CornerRadius, 1, v => $"{v:0} px", v => S.CornerRadius = v));
        Row(look, "Cor do fundo", "Tinge o vidro. Vazio usa a cor do tema.", ColorPicker(S.Tint, v => S.Tint = v));
        Row(look, "Cor do indicador", "A bolinha embaixo dos apps abertos.", ColorPicker(S.IndicatorColor, v => S.IndicatorColor = v));

        var icons = Card(page, "Ícones");
        Row(icons, "Tamanho dos ícones", null, Slider(28, 96, S.IconSize, 2, v => $"{v:0} px", v => S.IconSize = v));
        Row(icons, "Ampliação ao passar o mouse", "1,0× desliga.", Slider(1, 2.5, S.Magnification, 0.05, v => $"{v:0.00}×", v => S.Magnification = v));
        Row(icons, "Mostrar o nome ao passar o mouse", null, Toggle(S.ShowLabels, v => S.ShowLabels = v));
        Row(icons, "Selos de notificação", "Número de não lidas, para apps que publicam selo no Windows.", Toggle(S.ShowBadges, v => S.ShowBadges = v));
        Row(icons, "Animação de abertura", "O ícone pula enquanto o app abre.", Toggle(S.BounceOnLaunch, v => S.BounceOnLaunch = v));
        return page;
    }

    FrameworkElement ColorPicker(string current, Action<string> set)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var box = new TextBox { Text = current, Width = 80, Margin = new Thickness(8, 0, 0, 0), VerticalContentAlignment = VerticalAlignment.Center, Background = Brushes.Transparent };
        box.SetResourceReference(ForegroundProperty, "TextBrush");
        box.SetResourceReference(TextBox.CaretBrushProperty, "TextBrush");
        box.SetResourceReference(BorderBrushProperty, "TileRimBrush");
        string[] swatches = { "", "#1C1C20", "#F4F4F6", "#1E3A8A", "#7C3AED", "#BE185D", "#047857", "#B45309" };
        foreach (var hex in swatches)
        {
            var color = Theme.TryParse(hex, out var c) ? c : Colors.Transparent;
            var h = hex;
            var swatch = new Button
            {
                Width = 20,
                Height = 20,
                Margin = new Thickness(2, 0, 2, 0),
                Background = hex == "" ? Brushes.White : new SolidColorBrush(color),
                Content = hex == "" ? new TextBlock { Text = "A", FontSize = 10, Foreground = Brushes.Black } : null,
                ToolTip = hex == "" ? "Automático" : hex,
            };
            AutomationProperties.SetName(swatch, hex == "" ? "Automático" : hex);
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

    UIElement ItemsPage()
    {
        var page = Page("Itens do Dock", "Arraste um ícone no Dock para reordenar; puxe-o para fora da barra para remover. Também dá para soltar arquivos e atalhos no Dock.");

        var fixedItems = Card(page, "Itens fixos");
        Row(fixedItems, "Botão de Applications", "Primeiro no Dock; abre a busca de apps.", Toggle(S.ShowLauncher, v => S.ShowLauncher = v));
        var iconButtons = new StackPanel { Orientation = Orientation.Horizontal };
        iconButtons.Children.Add(Btn("Escolher…", () => App.Dock?.ChooseLauncherIcon()));
        iconButtons.Children.Add(Btn("Original", Dock.DockController.ResetLauncherIcon));
        Row(fixedItems, "Ícone do botão", "PNG, JPG ou ICO, ou um app para usar o ícone dele.", iconButtons);
        Row(fixedItems, "Mostrar apps abertos", "Apps abertos que não estão fixados, depois de um separador.", Toggle(S.ShowOpenApps, v => S.ShowOpenApps = v));
        Row(fixedItems, "Pasta Downloads", null, Toggle(S.ShowDownloads, v => S.ShowDownloads = v));
        Row(fixedItems, "Lixeira no fim do Dock", null, Toggle(S.ShowRecycleBin, v => S.ShowRecycleBin = v));

        page.Children.Add(new TextBlock { Text = "Apps, pastas e arquivos", FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 14, 0, 6) });
        var list = new ListBox { Height = 240, Background = Brushes.Transparent, ItemContainerStyle = (Style)Application.Current.Resources["AppRowItem"] };
        list.SetResourceReference(BorderBrushProperty, "TileRimBrush");
        void Fill()
        {
            list.Items.Clear();
            foreach (var item in S.Items.Where(i => i.Kind is DockItemKind.App or DockItemKind.Folder or DockItemKind.File or DockItemKind.Spacer))
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                if (item.Kind != DockItemKind.Spacer)
                    row.Children.Add(new Image { Source = IconLoader.Get(item.Path, 32, cache: false), Width = 22, Height = 22, Margin = new Thickness(0, 0, 8, 0) });
                var name = item.Kind == DockItemKind.Spacer ? (item.LargeSpacer ? "— Espaçador grande —" : "— Espaçador pequeno —")
                    : item.Name ?? Path.GetFileNameWithoutExtension(item.Path.TrimEnd('\\'));
                row.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
                if (item.Kind != DockItemKind.Spacer)
                {
                    var path = Muted("  " + item.Path, 11);
                    path.VerticalAlignment = VerticalAlignment.Center;
                    row.Children.Add(path);
                }
                list.Items.Add(new ListBoxItem { Content = row, Tag = item, Focusable = true });
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
            {
                var none = Muted("Nenhum. Clique com o botão direito num app aberto no Dock e escolha “Nunca mostrar no Dock”.");
                none.Margin = new Thickness(0, 8, 0, 8);
                hidden.Children.Add(none);
            }
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

    UIElement PanelPage()
    {
        var page = Page("Central de Controle", "Rede, Bluetooth, brilho, som e energia num painel só.");
        var card = Card(page);
        Row(card, "Atalho para abrir", "Clique e pressione as teclas. Backspace limpa.", HotkeyBox(S.ControlCenterHotkey, v => S.ControlCenterHotkey = v));
        Row(card, "Bandeja: ícones na barra", "Os demais ficam na lista da seta da barra, com um alfinete para fixar.", Btn("Limpar fixados", () => { S.PinnedTray.Clear(); Commit(now: true); }));
        return page;
    }

    UIElement AppsPage()
    {
        var page = Page("Applications", "Busca rápida de todos os apps do Windows.");
        var card = Card(page);
        Row(card, "Atalho para abrir", "Clique e pressione as teclas. Backspace limpa.", HotkeyBox(S.AppsHotkey, v => S.AppsHotkey = v));
        Row(card, "Favoritos", "Clique com o botão direito num app da lista para favoritar.", Btn("Limpar", () => { S.AppsFavorites.Clear(); Commit(now: true); }));
        Row(card, "Recentes", "Sobem na busca e aparecem primeiro na lista.", Btn("Limpar", () => { S.AppsRecent.Clear(); Commit(now: true); }));
        return page;
    }

    UIElement MotionPage()
    {
        var page = Page("Animações", "Como a barra, os painéis e o Dock se movem.");
        var card = Card(page);
        Row(card, "Movimento", "Automático segue “Efeitos de animação” do Windows. Reduzido troca os movimentos por transições curtas.", Choice(new[]
        {
            (MotionMode.Auto, "Automático"), (MotionMode.Full, "Completo"), (MotionMode.Reduced, "Reduzido"),
        }, S.Motion, v => S.Motion = v));
        Row(card, "Velocidade", null, Slider(0.5, 2, S.MotionSpeed, 0.1, v => $"{v:0.0}×", v => S.MotionSpeed = v));
        return page;
    }

    UIElement BehaviourPage()
    {
        var page = Page("Comportamento", "Como o shft. convive com as janelas.");
        var behaviour = Card(page);
        Row(behaviour, "Manter as janelas fora do Dock", "Janelas maximizadas param acima dele.", Toggle(S.ReserveSpace, v => S.ReserveSpace = v));
        Row(behaviour, "Ocultar o Dock automaticamente", "Ele aparece quando o ponteiro encosta na borda da tela.", Toggle(S.AutoHide, v => S.AutoHide = v));
        Row(behaviour, "Atraso para mostrar", null, Slider(0, 1000, S.AutoHideDelayMs, 50, v => $"{v:0} ms", v => S.AutoHideDelayMs = (int)v));
        Row(behaviour, "Esconder com app em tela cheia", "Jogos e vídeos ficam sozinhos na tela; os temporizadores da barra descansam.", Toggle(S.HideOnFullScreen, v => S.HideOnFullScreen = v));
        return page;
    }

    UIElement SystemPage()
    {
        var page = Page("Inicialização e sistema", "O que o shft. muda no Windows e desfaz ao sair.");
        var card = Card(page);
        Row(card, "Abrir o shft. quando o Windows iniciar", null, Toggle(S.StartWithWindows, v => S.StartWithWindows = v));
        Row(card, "Barra de tarefas do Windows", "Ela volta como era quando você encerra o shft.", Choice(new[]
        {
            (TaskbarMode.Keep, "Deixar visível"), (TaskbarMode.AutoHide, "Ocultar automaticamente"), (TaskbarMode.Hide, "Ocultar por completo"),
        }, S.Taskbar, v => S.Taskbar = v));
        return page;
    }

    UIElement WidgetsPage()
    {
        var page = Page("Widgets", "Tiles vivos no Dock. Clique num widget para usá-lo; o botão direito traz as opções dele.");

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
            {
                var none = Muted("Nenhum widget ainda. Escolha um abaixo.");
                none.Margin = new Thickness(0, 8, 0, 8);
                current.Children.Add(none);
            }
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
        var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        card.Children.Add(new TextBlock { Text = $"shft. {version}", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4) });
        var about = Muted("Barra, Dock, Central de Controle e Applications num só app nativo. O Dock é uma reimplementação independente, feita a partir do comportamento observado do PrimoDock 0.6.0-beta. " +
                          "A experiência da barra se inspira no Seelen UI; nenhum código dos dois foi copiado.");
        about.Margin = new Thickness(0, 0, 0, 10);
        card.Children.Add(about);
        var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        buttons.Children.Add(Btn("Abrir pasta de dados", () =>
        {
            Directory.CreateDirectory(SettingsStore.DataFolder);
            Process.Start(new ProcessStartInfo(SettingsStore.DataFolder) { UseShellExecute = true });
        }));
        buttons.Children.Add(Btn("Encerrar shft.", () => Application.Current.Shutdown()));
        card.Children.Add(buttons);
        return page;
    }
}
