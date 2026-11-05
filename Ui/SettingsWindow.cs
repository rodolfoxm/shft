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

/// <summary>All of shft.'s settings in one window; every change applies right away. Its look lives in SettingsTheme.xaml and SettingsKit.</summary>
public sealed class SettingsWindow : Window
{
    readonly ListBox _nav;
    readonly ContentControl _page;
    readonly ScrollViewer _scroll;
    readonly DispatcherTimer _debounce;
    readonly Dictionary<string, Func<UIElement>> _pages;

    AppSettings S => SettingsStore.Current;

    public SettingsWindow()
    {
        Resources.MergedDictionaries.Add(SettingsKit.Theme);
        Title = "shft. — Configurações";
        Width = 960;
        Height = 680;
        MinWidth = 760;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(FontFamilyProperty, "SettingsFont");
        FontSize = 13;
        SetResourceReference(BackgroundProperty, "MainBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        UseLayoutRounding = true;
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
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(12, 0, 12, 12),
            ItemContainerStyle = (Style)SettingsKit.Theme["NavItem"],
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_nav, ScrollBarVisibility.Disabled);
        foreach (var (key, label, glyph) in NavItems)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var icon = SettingsKit.Icon(glyph, 15);
            icon.Width = 28;
            icon.TextAlignment = TextAlignment.Left;
            row.Children.Add(icon);
            row.Children.Add(new TextBlock { Text = label, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            var item = new ListBoxItem { Content = row, Tag = key };
            AutomationProperties.SetName(item, label);
            _nav.Items.Add(item);
        }
        _nav.SelectionChanged += (_, _) => ShowPage();

        var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 20, 20, 20) };
        brand.Children.Add(SettingsKit.Symbol(24));
        var word = new TextBlock { Text = "shft.", FontSize = 19, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        word.SetResourceReference(TextBlock.FontFamilyProperty, "SettingsDisplayFont");
        brand.Children.Add(word);

        var side = new DockPanel { Width = 232 };
        side.SetResourceReference(Panel.BackgroundProperty, "SidebarBrush");
        DockPanel.SetDock(brand, System.Windows.Controls.Dock.Top);
        side.Children.Add(brand);
        side.Children.Add(_nav);

        var line = new Border { Width = 1 };
        line.SetResourceReference(Border.BackgroundProperty, "DividerBrush");

        _page = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        _scroll = new ScrollViewer { Content = _page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(32, 24, 32, 24), Focusable = false };

        var root = new DockPanel();
        DockPanel.SetDock(side, System.Windows.Controls.Dock.Left);
        DockPanel.SetDock(line, System.Windows.Controls.Dock.Left);
        root.Children.Add(side);
        root.Children.Add(line);
        root.Children.Add(_scroll);
        Content = root;
        _nav.SelectedIndex = 0;
        Closed += (_, _) => { if (_debounce.IsEnabled) { _debounce.Stop(); SettingsStore.Save(); } };
    }

    static readonly (string Key, string Label, string Glyph)[] NavItems =
    {
        ("look", "Aparência", "\uE790"), ("bar", "Barra superior", "\uE7F4"), ("dock", "Dock", "\uE75B"), ("items", "Itens do Dock", "\uE8FD"),
        ("panel", "Central de Controle", "\uE9E9"), ("apps", "Applications", "\uE721"), ("motion", "Animações", "\uE916"),
        ("behaviour", "Comportamento", "\uE7C4"), ("system", "Inicialização e sistema", "\uE7E8"), ("widgets", "Widgets", "\uF0E2"), ("about", "Sobre", "\uE946"),
    };

    static string GlyphOf(string key) => NavItems.First(n => n.Key == key).Glyph;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // A solid title bar in the main colour; Windows 10 ignores the colours and keeps it dark.
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        int dark = 1, caption = 0x14110F, text = 0xEAE7E6; // COLORREF is 0x00BBGGRR
        Win32.DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        Win32.DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));
        Win32.DwmSetWindowAttribute(hwnd, 36, ref text, sizeof(int));
    }

    public void Select(string key)
    {
        foreach (ListBoxItem item in _nav.Items)
            if ((string)item.Tag == key) _nav.SelectedItem = item;
    }

    void ShowPage()
    {
        if (_nav.SelectedItem is ListBoxItem { Tag: string key } && _pages.TryGetValue(key, out var build))
        {
            _page.Content = build();
            _scroll.ScrollToTop();
        }
    }

    void Commit(bool now = false)
    {
        if (now) { _debounce.Stop(); SettingsStore.Save(); }
        else { _debounce.Stop(); _debounce.Start(); }
    }

    /// <summary>Saves and redraws the page, for choices that make other rows available or not.</summary>
    void CommitAndRefresh()
    {
        Commit(now: true);
        Dispatcher.BeginInvoke(ShowPage, DispatcherPriority.Background);
    }

    // ---------------------------------------------------------------- the kit, wired to saving

    static StackPanel Page(string key, string title, string description) => SettingsKit.Page(GlyphOf(key), title, description);
    static StackPanel Card(Panel page, string? header = null) => SettingsKit.Card(page, header);
    static Grid Row(Panel card, string label, string? help, UIElement control, string? glyph = null) => SettingsKit.Row(card, label, help, control, glyph);
    static Button Btn(string text, Action click) => SettingsKit.Button(text, click);
    static TextBlock Muted(string text, double size = 12) => SettingsKit.Muted(text, size);

    SwitchToggle Toggle(bool value, Action<bool> set) => SettingsKit.Toggle(value, v => { set(v); Commit(now: true); });

    ComboBox Choice<T>(IEnumerable<(T Value, string Label)> options, T current, Action<T> set) =>
        SettingsKit.Choice(options, current, v => { set(v); Commit(now: true); });

    FrameworkElement Slider(double min, double max, double value, double step, Func<double, string> format, Action<double> set) =>
        SettingsKit.Slider(min, max, value, step, format, v => { set(v); Commit(); });

    TextBox Field(string value, Action<string> set) => SettingsKit.Field(value, v => { set(v); Commit(); });

    /// <summary>A shortcut row that says so when another app already holds the combination.</summary>
    void HotkeyRow(Panel card, string current, Action<string> set, Func<bool> free)
    {
        TextBlock? note = null;
        void Check()
        {
            note!.Text = "Outro app já usa esse atalho. Escolha outro.";
            note.Visibility = free() ? Visibility.Collapsed : Visibility.Visible;
        }
        var row = Row(card, "Atalho para abrir", "Clique e pressione as teclas. Backspace limpa.", SettingsKit.HotkeyBox(current, v =>
        {
            set(v);
            Commit(now: true);
            Check();
        }), Glyph.Search);
        note = SettingsKit.Problem(row);
        Check();
    }

    // ---------------------------------------------------------------- pages

    UIElement LookPage()
    {
        var page = Page("look", "Aparência", "A identidade da barra, do Dock e dos painéis.");
        var look = Card(page, "Tema");
        Row(look, "Claro ou escuro", "Vale para a barra, o Dock e os painéis. Automático segue o Windows.", Choice(new[]
        {
            (ThemeMode.Auto, "Automático"), (ThemeMode.Dark, "Escuro"), (ThemeMode.Light, "Claro"),
        }, S.Theme, v => S.Theme = v), Glyph.Moon);

        var glass = Card(page, "Vidro dos painéis");
        Row(glass, "Transparência", "Quanto o fundo aparece atrás dos painéis.",
            Slider(0.1, 0.95, S.PanelOpacity, 0.05, v => $"{(1 - v) * 100:0}%", v => S.PanelOpacity = v), Glyph.Sun);
        Row(glass, "Intensidade do desfoque", null, Slider(4, 24, S.PanelBlur, 1, v => $"{v:0}", v => S.PanelBlur = (int)v), "\uE9E9");
        Row(glass, "Borda Liquid Glass", "Brilho no contorno, como luz numa superfície curva.", Toggle(S.PanelRim, v => S.PanelRim = v), "\uE81E");
        return page;
    }

    UIElement BarPage()
    {
        var page = Page("bar", "Barra superior", "Logo, app em foco, indicadores do sistema e relógio.");
        var main = Card(page);
        Row(main, "Mostrar a barra", null, Toggle(S.TopBarEnabled, v => S.TopBarEnabled = v), "\uE7B3");
        Row(main, "Em todos os monitores", null, Toggle(S.TopBarAllMonitors, v => S.TopBarAllMonitors = v), "\uE7F4");
        Row(main, "Manter as janelas abaixo da barra", "Janelas maximizadas param abaixo dela.", Toggle(S.TopBarReserve, v => S.TopBarReserve = v), "\uE8A0");
        Row(main, "Altura", null, Slider(24, 48, S.TopBarHeight, 1, v => $"{v:0} px", v => S.TopBarHeight = v), "\uE8CB");

        var look = Card(page, "Fundo");
        Row(look, "Modo", "Sólido usa o tema. Translúcido mostra o fundo. Adaptativo fica transparente e ajusta a cor do texto.", SettingsKit.Choice(new[]
        {
            (TopBarStyle.Solid, "Sólido"), (TopBarStyle.Translucent, "Translúcido"), (TopBarStyle.Adaptive, "Adaptativo"),
        }, S.TopBarStyle, v => { S.TopBarStyle = v; CommitAndRefresh(); }), "\uE790");
        var opacity = Row(look, "Opacidade", null, Slider(0, 1, S.TopBarOpacity, 0.05, v => $"{v * 100:0}%", v => S.TopBarOpacity = v), Glyph.Sun);
        var blur = Row(look, "Desfoque", null, Toggle(S.TopBarBlur, v => S.TopBarBlur = v), "\uE81E");
        var scrim = Row(look, "Contraste adaptativo", "Camada suave atrás do conteúdo quando o fundo é misto.", Toggle(S.TopBarScrim, v => S.TopBarScrim = v), Glyph.Eye);
        if (S.TopBarStyle != TopBarStyle.Translucent)
        {
            SettingsKit.Unavailable(opacity, "Disponível no modo Translúcido.");
            SettingsKit.Unavailable(blur, "Disponível no modo Translúcido.");
        }
        if (S.TopBarStyle != TopBarStyle.Adaptive) SettingsKit.Unavailable(scrim, "Disponível no modo Adaptativo.");

        var content = Card(page, "Conteúdo");
        var logo = new StackPanel { Orientation = Orientation.Horizontal };
        logo.Children.Add(Btn("Escolher…", LogoPicker.Choose));
        logo.Children.Add(Btn("Original", () => { S.TopBarLogo = ""; Commit(now: true); }));
        Row(content, "Logo", "Também dá para trocar com o botão direito na logo.", logo, "\uEB9F");
        Row(content, "Pintar a logo com a cor do texto", "Para logos de uma cor só, como a da Apple.", Toggle(S.TopBarLogoTint, v => S.TopBarLogoTint = v), "\uE790");
        Row(content, "Nome", "Vazio usa o nome do Windows.", Field(S.TopBarUserName, v => S.TopBarUserName = v), "\uE77B");
        Row(content, "Ordem dos ícones", "Arraste os ícones na própria barra para reorganizar.", Btn("Restaurar", () =>
        {
            S.TopBarOrder.Clear();
            Commit(now: true);
        }), "\uE8CB");
        Row(content, "Mostrar o nome", null, Toggle(S.TopBarShowUser, v => S.TopBarShowUser = v), "\uE77B");
        Row(content, "Mostrar o app em foco", null, Toggle(S.TopBarShowApp, v => S.TopBarShowApp = v), "\uE737");
        Row(content, "Mostrar a memória", "Clicar nela libera a RAM ociosa; o Windows pede confirmação.", Toggle(S.TopBarShowMemory, v => S.TopBarShowMemory = v), Glyph.Memory);
        Row(content, "Formato do relógio", "ddd = dia, MMM = mês, HH = hora, mm = minutos, tt = AM/PM.", Field(S.TopBarClockFormat, v =>
        {
            try
            {
                _ = DateTime.Now.ToString(v, System.Globalization.CultureInfo.CurrentCulture);
                if (v.Length > 0) S.TopBarClockFormat = v;
            }
            catch (FormatException) { } // half-typed format: keep the last good one
        }), "\uE823");
        return page;
    }

    UIElement DockPage()
    {
        var page = Page("dock", "Dock", "Posição, aparência e ícones do Dock.");
        var place = Card(page, "Posição");
        Row(place, "Borda da tela", "Em qual borda o Dock fica.", Choice(new[]
        {
            (DockEdge.Bottom, "Embaixo"), (DockEdge.Left, "À esquerda"), (DockEdge.Right, "À direita"), (DockEdge.Top, "No topo"),
        }, S.Edge, v => S.Edge = v), "\uE75B");
        var monitors = Monitors.All();
        var options = new List<(int, string)> { (-1, "Monitor principal") };
        options.AddRange(monitors.Select((m, i) => (i, Monitors.Describe(m, i))));
        Row(place, "Monitor", null, Choice(options, S.Monitor, v => S.Monitor = v), "\uE7F4");
        Row(place, "Distância da borda da tela", null, Slider(0, 30, S.EdgeGap, 1, v => $"{v:0} px", v => S.EdgeGap = v), "\uE7C2");

        var look = Card(page, "Barra");
        Row(look, "Desfoque atrás da barra", "Leve não tem tom. Acrílico é o material do Windows, com tom e granulado.", Choice(new[]
        {
            (0, "Nenhum"), (1, "Leve"), (2, "Acrílico"),
        }, !S.Glass ? 0 : S.Acrylic ? 2 : 1, v => { S.Glass = v > 0; S.Acrylic = v == 2; }), "\uE81E");
        Row(look, "Opacidade do fundo", "0% deixa só o vidro.", Slider(0, 1, S.BarOpacity, 0.05, v => $"{v * 100:0}%", v => S.BarOpacity = v), Glyph.Sun);
        Row(look, "Borda Liquid Glass", "Brilho no topo e uma franja de cor discreta.", Toggle(S.LiquidGlass, v => S.LiquidGlass = v), "\uE81E");
        Row(look, "Arredondamento", "Com desfoque, o Windows 11 fixa o raio em 8 px.", Slider(0, 32, S.CornerRadius, 1, v => $"{v:0} px", v => S.CornerRadius = v), "\uE8A3");
        Row(look, "Cor do fundo", "Tinge o vidro. Vazio usa a cor do tema.", ColorPicker(S.Tint, v => S.Tint = v), "\uE790");
        Row(look, "Cor do indicador", "A bolinha embaixo dos apps abertos.", ColorPicker(S.IndicatorColor, v => S.IndicatorColor = v), "\uE91F");

        var icons = Card(page, "Ícones");
        Row(icons, "Tamanho dos ícones", null, Slider(28, 96, S.IconSize, 2, v => $"{v:0} px", v => S.IconSize = v), "\uE740");
        Row(icons, "Ampliação ao passar o mouse", "1,0× desliga.", Slider(1, 2.5, S.Magnification, 0.05, v => $"{v:0.00}×", v => S.Magnification = v), Glyph.Search);
        Row(icons, "Mostrar o nome ao passar o mouse", null, Toggle(S.ShowLabels, v => S.ShowLabels = v), "\uE8D2");
        Row(icons, "Selos de notificação", "Número de não lidas, para apps que publicam selo no Windows.", Toggle(S.ShowBadges, v => S.ShowBadges = v), Glyph.Bell);
        Row(icons, "Animação de abertura", "O ícone pula enquanto o app abre.", Toggle(S.BounceOnLaunch, v => S.BounceOnLaunch = v), "\uE7C9");
        return page;
    }

    FrameworkElement ColorPicker(string current, Action<string> set)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var box = new TextBox { Text = current, Width = 84, Margin = new Thickness(8, 0, 0, 0) };
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
        var page = Page("items", "Itens do Dock", "Arraste no Dock para reordenar; puxe para fora para remover. Solte arquivos e atalhos para adicionar.");

        var fixedItems = Card(page, "Itens fixos");
        Row(fixedItems, "Botão de Applications", "Primeiro no Dock; abre a busca de apps.", Toggle(S.ShowLauncher, v => S.ShowLauncher = v), Glyph.Search);
        var iconButtons = new StackPanel { Orientation = Orientation.Horizontal };
        iconButtons.Children.Add(Btn("Escolher…", () => App.Dock?.ChooseLauncherIcon()));
        iconButtons.Children.Add(Btn("Original", Dock.DockController.ResetLauncherIcon));
        Row(fixedItems, "Ícone do botão", "Uma imagem, ou um app para usar o ícone dele.", iconButtons, "\uEB9F");
        Row(fixedItems, "Mostrar apps abertos", "Apps abertos que não estão fixados, depois de um separador.", Toggle(S.ShowOpenApps, v => S.ShowOpenApps = v), "\uE737");
        Row(fixedItems, "Pasta Downloads", null, Toggle(S.ShowDownloads, v => S.ShowDownloads = v), "\uE896");
        Row(fixedItems, "Lixeira no fim do Dock", null, Toggle(S.ShowRecycleBin, v => S.ShowRecycleBin = v), "\uE74D");

        page.Children.Add(SettingsKit.Section("Apps, pastas e arquivos"));
        var list = new ListBox { Height = 240, BorderThickness = new Thickness(0), Padding = new Thickness(6), ItemContainerStyle = (Style)SettingsKit.Theme["ListRow"] };
        list.SetResourceReference(BackgroundProperty, "SurfaceBrush");
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
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

        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
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
        var page = Page("panel", "Central de Controle", "Rede, Bluetooth, brilho, som e energia num painel só.");
        var card = Card(page);
        HotkeyRow(card, S.ControlCenterHotkey, v => S.ControlCenterHotkey = v, () => App.ControlsHotkeyFree);
        Row(card, "Ícones da bandeja fixados na barra", "Os demais ficam na lista da seta da barra.", Btn("Limpar fixados", () => { S.PinnedTray.Clear(); Commit(now: true); }), Glyph.Pin);
        return page;
    }

    UIElement AppsPage()
    {
        var page = Page("apps", "Applications", "Busca rápida de todos os apps do Windows.");
        var card = Card(page);
        Row(card, "Abrir com a tecla Windows", "Tocada sozinha, substitui o Menu Iniciar. Win+E e outros atalhos seguem iguais.", Toggle(S.AppsWinKey, v => S.AppsWinKey = v), "\uE782");
        HotkeyRow(card, S.AppsHotkey, v => S.AppsHotkey = v, () => App.AppsHotkeyFree);
        Row(card, "Favoritos", "Botão direito num app da lista para favoritar.", Btn("Limpar", () => { S.AppsFavorites.Clear(); Commit(now: true); }), "\uE734");
        Row(card, "Recentes", "Sobem na busca e aparecem primeiro na lista.", Btn("Limpar", () => { S.AppsRecent.Clear(); Commit(now: true); }), "\uE81C");
        return page;
    }

    UIElement MotionPage()
    {
        var page = Page("motion", "Animações", "Como a barra, os painéis e o Dock se movem.");
        var card = Card(page);
        Row(card, "Movimento", "Automático segue o Windows. Reduzido usa transições curtas.", Choice(new[]
        {
            (MotionMode.Auto, "Automático"), (MotionMode.Full, "Completo"), (MotionMode.Reduced, "Reduzido"),
        }, S.Motion, v => S.Motion = v), "\uE916");
        Row(card, "Velocidade", null, Slider(0.5, 2, S.MotionSpeed, 0.1, v => $"{v:0.0}×", v => S.MotionSpeed = v), "\uEC4A");
        return page;
    }

    UIElement BehaviourPage()
    {
        var page = Page("behaviour", "Comportamento", "Como o shft. convive com as janelas.");
        var behaviour = Card(page);
        Row(behaviour, "Manter as janelas fora do Dock", "Janelas maximizadas param acima dele.", Toggle(S.ReserveSpace, v => S.ReserveSpace = v), "\uE8A0");
        Row(behaviour, "Ocultar o Dock automaticamente", "Ele aparece quando o ponteiro encosta na borda da tela.", SettingsKit.Toggle(S.AutoHide, v => { S.AutoHide = v; CommitAndRefresh(); }), "\uE7C4");
        var delay = Row(behaviour, "Atraso para mostrar", null, Slider(0, 1000, S.AutoHideDelayMs, 50, v => $"{v:0} ms", v => S.AutoHideDelayMs = (int)v), "\uE823");
        if (!S.AutoHide) SettingsKit.Unavailable(delay, "Disponível com o ocultar automático ligado.");
        Row(behaviour, "Esconder com app em tela cheia", "Jogos e vídeos ficam sozinhos na tela.", Toggle(S.HideOnFullScreen, v => S.HideOnFullScreen = v), "\uE740");
        return page;
    }

    UIElement SystemPage()
    {
        var page = Page("system", "Inicialização e sistema", "O que o shft. muda no Windows e desfaz ao sair.");
        var card = Card(page);
        Row(card, "Abrir o shft. quando o Windows iniciar", null, Toggle(S.StartWithWindows, v => S.StartWithWindows = v), Glyph.Power);
        Row(card, "Barra de tarefas do Windows", "Ela volta como era quando você encerra o shft.", Choice(new[]
        {
            (TaskbarMode.Keep, "Deixar visível"), (TaskbarMode.AutoHide, "Ocultar automaticamente"), (TaskbarMode.Hide, "Ocultar por completo"),
        }, S.Taskbar, v => S.Taskbar = v), "\uE75B");
        return page;
    }

    UIElement WidgetsPage()
    {
        var page = Page("widgets", "Widgets", "Tiles vivos no Dock. Clique para usar; o botão direito traz as opções.");

        var options = Card(page, "Ajustes");
        Row(options, "Largura dos tiles", "Em tamanhos de ícone.", Slider(1.2, 3, S.TileWidth, 0.1, v => $"{v:0.0}×", v => S.TileWidth = v), "\uE7C2");
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
        Row(options, "Cidade do clima", "Previsão do Open-Meteo; só as coordenadas são enviadas.", cityPanel, "\uE753");
        Row(options, "Temperatura em Fahrenheit", null, Toggle(S.Fahrenheit, v => S.Fahrenheit = v), "\uE9CA");

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
        var page = Page("about", "Sobre", "Versão, créditos e dados do app.");
        var card = Card(page);
        var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 8) };
        brand.Children.Add(SettingsKit.Symbol(28));
        brand.Children.Add(new TextBlock { Text = $"shft. {version}", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        card.Children.Add(brand);
        var about = Muted("Barra, Dock, Central de Controle e Applications num só app nativo. O Dock é uma reimplementação independente, feita a partir do comportamento observado do PrimoDock 0.6.0-beta. " +
                          "A experiência da barra se inspira no Seelen UI; nenhum código dos dois foi copiado.", 13);
        about.Margin = new Thickness(0, 0, 0, 12);
        card.Children.Add(about);
        var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 16) };
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
