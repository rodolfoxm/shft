using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Shft.Native;
using Shft.Ui;

namespace Shft.Hme;

/// <summary>
/// hme.'s setup and settings, in the solid style of shft.'s settings: the Tuya account (with the steps), devices,
/// the air conditioner (or none), Positivo and diagnostics.
/// </summary>
public sealed class HmeSettingsWindow : Window
{
    static HmeSettingsWindow? _open;

    readonly EngineHost _host;
    readonly ListBox _nav;
    readonly ContentControl _page = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    Discovery? _discovery;
    string? _discoveryError;

    static readonly (string Key, string Label, string Glyph)[] NavItems =
    [
        ("account", "Conta Tuya", ""), ("devices", "Aparelhos", ""), ("ac", "Ar-condicionado", ""),
        ("positivo", "Positivo", ""), ("diag", "Diagnóstico", ""),
    ];

    HmeConfig Cfg => _host.Config;

    public static void ShowFor(EngineHost host, string? page = null)
    {
        if (_open == null || !_open.IsLoaded)
        {
            _open = new HmeSettingsWindow(host);
            _open.Closed += (_, _) => _open = null;
            _open.Show();
        }
        if (page != null) _open.Select(page);
        else if (host.Home == null) _open.Select("account");
        _open.Activate();
    }

    HmeSettingsWindow(EngineHost host)
    {
        _host = host;
        Resources.MergedDictionaries.Add(SettingsKit.Theme);
        Title = "hme. — Ajustes";
        Width = 880;
        Height = 640;
        MinWidth = 700;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(FontFamilyProperty, "SettingsFont");
        FontSize = 13;
        SetResourceReference(BackgroundProperty, "MainBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        UseLayoutRounding = true;

        _nav = new ListBox { BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(12, 0, 12, 12), ItemContainerStyle = (Style)SettingsKit.Theme["NavItem"] };
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

        var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20) };
        var house = Glyph.Text(Glyph.Home, 20);
        house.SetResourceReference(GlyphView.ForegroundProperty, "TextBrush");
        brand.Children.Add(house);
        var word = new TextBlock { Text = "hme.", FontSize = 19, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        word.SetResourceReference(TextBlock.FontFamilyProperty, "SettingsDisplayFont");
        brand.Children.Add(word);
        var side = new DockPanel { Width = 220 };
        side.SetResourceReference(Panel.BackgroundProperty, "SidebarBrush");
        DockPanel.SetDock(brand, System.Windows.Controls.Dock.Top);
        side.Children.Add(brand);
        side.Children.Add(_nav);
        var line = new Border { Width = 1 };
        line.SetResourceReference(Border.BackgroundProperty, "DividerBrush");
        var root = new DockPanel();
        DockPanel.SetDock(side, System.Windows.Controls.Dock.Left);
        DockPanel.SetDock(line, System.Windows.Controls.Dock.Left);
        root.Children.Add(side);
        root.Children.Add(line);
        root.Children.Add(new ScrollViewer { Content = _page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(32, 24, 32, 24), Focusable = false });
        Content = root;
        _nav.SelectedIndex = 0;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        int dark = 1, caption = 0x14110F, text = 0xEAE7E6; // the same solid title bar as shft.'s settings
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
        var key = (_nav.SelectedItem as ListBoxItem)?.Tag as string ?? "account";
        _page.Content = key switch
        {
            "devices" => DevicesPage(),
            "ac" => AcPage(),
            "positivo" => PositivoPage(),
            "diag" => DiagPage(),
            _ => AccountPage(),
        };
    }

    static StackPanel Page(string key, string title, string description) =>
        SettingsKit.Page(NavItems.First(n => n.Key == key).Glyph, title, description);

    void Save(bool reload = true)
    {
        try
        {
            Cfg.Save();
        }
        catch (Exception ex) when (ex is HmeSecretException or System.IO.IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "Não consegui salvar os ajustes do hme.\n\n" + ex.Message, "hme.", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (reload) _ = _host.Reload();
    }

    static TextBlock Status() => new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };

    // ---------------------------------------------------------------- account

    UIElement AccountPage()
    {
        var page = Page("account", "Conta Tuya", "O Smart Life não tem login para programas; o caminho oficial é um projeto grátis na Tuya. Você faz isso uma vez só.");
        var steps = SettingsKit.Card(page, "Passo a passo");
        string[] lines =
        [
            "1. Crie uma conta em platform.tuya.com.",
            "2. Vá em Cloud → Development → Create Cloud Project. Industry: Smart Home · Development Method: Smart Home · Data Center: Western America (o padrão no Brasil).",
            "3. Na tela de serviços, mantenha os marcados e adicione o IR Control Hub Open Service (necessário para ar por controle IR).",
            "4. Na aba Overview do projeto, copie o Access ID e o Access Secret e cole abaixo.",
            "5. Na aba Devices → Link App Account → Add App Account aparece um QR Code. No celular, abra Smart Life → Eu → leitor (canto superior) e escaneie.",
            "6. Teste a conexão. Depois, em Aparelhos, renomeie, escolha os cômodos e esconda o que não quiser no painel.",
        ];
        var text = new StackPanel { Margin = new Thickness(0, 12, 0, 12) };
        foreach (var l in lines) text.Children.Add(new TextBlock { Text = l, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
        text.Children.Add(SettingsKit.Muted("O IoT Core da Tuya é grátis, mas o teste expira de tempos em tempos: se aparecer um aviso, renove em Cloud → Cloud Services → IoT Core."));
        var open = SettingsKit.Button("Abrir platform.tuya.com", () => Shft.Dock.DockController.Open("https://platform.tuya.com"));
        open.HorizontalAlignment = HorizontalAlignment.Left;
        open.Margin = new Thickness(0, 10, 0, 0);
        text.Children.Add(open);
        steps.Children.Add(text);

        var card = SettingsKit.Card(page, "Chaves do projeto");
        string id = Cfg.AccessId, secret = "", region = "auto";
        SettingsKit.Row(card, "Access ID", null, SettingsKit.Field(id, v => id = v, 260));
        var secretBox = new PasswordBox { Width = 260 };
        secretBox.PasswordChanged += (_, _) => secret = secretBox.Password.Trim();
        SettingsKit.Row(card, "Access Secret", Cfg.AccessSecret.Length > 0 ? "Deixe vazio para manter o que está salvo." : null, secretBox);
        var regions = new List<(string, string)> { ("auto", "Automática") };
        regions.AddRange(HmeEngineFactory.Regions);
        SettingsKit.Row(card, "Região", "Automática testa todas e fica com a que tem seus aparelhos.", SettingsKit.Choice(regions, region, v => region = v));
        if (Cfg.SecretError != null) SettingsKit.Problem(SettingsKit.Row(card, "Segredo salvo", Cfg.SecretError, new TextBlock())).Text = Cfg.SecretError;

        var status = Status();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        Button? test = null;
        test = SettingsKit.Button("Testar e conectar", async () =>
        {
            var key = id.Trim();
            var sec = secret.Length > 0 ? secret : Cfg.AccessSecret;
            if (key.Length == 0 || sec.Length == 0)
            {
                status.Text = "Preencha o Access ID e o Access Secret.";
                return;
            }
            test!.IsEnabled = false;
            status.Text = "Testando…";
            var r = await HmeEngineFactory.TestConnection(key, sec, region, _host.Usage);
            test.IsEnabled = true;
            if (!r.Ok)
            {
                status.Text = r.Error;
                return;
            }
            (Cfg.AccessId, Cfg.AccessSecret, Cfg.Region, Cfg.Configured, Cfg.Demo) = (key, sec, r.Region!, true, false);
            Save();
            status.Text = r.Linked
                ? $"Conectado: {r.Count} aparelho(s) na região {r.Region}. Veja Aparelhos e Ar-condicionado."
                : $"As chaves funcionam (região {r.Region}), mas nenhum aparelho apareceu: vincule o app Smart Life (passo 5).";
        });
        buttons.Children.Add(test);
        buttons.Children.Add(SettingsKit.Button("Usar demonstração", () =>
        {
            (Cfg.Demo, Cfg.Configured) = (true, true);
            Save();
            status.Text = "Modo demonstração ligado: uma casa de mentira para testar tudo.";
        }));
        if (Cfg.Configured)
        {
            buttons.Children.Add(SettingsKit.Button("Desconectar", () =>
            {
                if (MessageBox.Show(this, "Desconectar a conta? As chaves salvas são apagadas; nomes e cômodos ficam.", "hme.",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                (Cfg.Configured, Cfg.Demo, Cfg.AccessSecret) = (false, false, "");
                Save();
                ShowPage();
            }));
        }
        page.Children.Add(buttons);
        page.Children.Add(status);
        if (Cfg.Demo) status.Text = "Agora: modo demonstração.";
        else if (_host.Home != null) status.Text = $"Agora: conectado na região {Cfg.Region}.";
        return page;
    }

    // ---------------------------------------------------------------- devices

    async Task<Discovery?> Discover(TextBlock status)
    {
        if (_host.Home == null)
        {
            status.Text = "Conecte a conta Tuya (ou a demonstração) primeiro.";
            return null;
        }
        status.Text = "Buscando aparelhos…";
        try
        {
            _discovery = await _host.Home.Discover();
            _discoveryError = null;
        }
        catch (Exception ex) when (ex is HmeCloudException or PositivoException)
        {
            _discoveryError = ex.Message;
        }
        status.Text = _discoveryError ?? "";
        return _discovery;
    }

    UIElement DevicesPage()
    {
        var page = Page("devices", "Aparelhos", "Nomes, cômodos e o que aparece no painel.");
        var status = Status();
        var list = new StackPanel();
        var rooms = SettingsKit.Card(page, "Cômodos");
        SettingsKit.Row(rooms, "Lista de cômodos", "Separados por vírgula, na ordem do painel. Aparelhos com o nome do cômodo entram nele sozinhos.",
            SettingsKit.Field(string.Join(", ", Cfg.Rooms.Select(HmeConfig.Str)), v =>
                Cfg.Root["rooms"] = new JsonArray(v.Split(',').Select(r => r.Trim()).Where(r => r.Length > 0).Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()), 300));

        void Fill()
        {
            list.Children.Clear();
            if (_discovery == null) return;
            var card = SettingsKit.Card(list, $"{_discovery.Devices.Count} aparelho(s)");
            var roomOptions = new List<(string, string)> { ("", "Automático") };
            roomOptions.AddRange(_discovery.Rooms.Where(r => r != Rooms.Other).Concat(Cfg.Rooms.Select(HmeConfig.Str)).Distinct().Select(r => (r, r)));
            roomOptions.Add((Rooms.Other, Rooms.Other));
            foreach (var d in _discovery.Devices.Where(d => d.Kind is "light" or "switch" or "plug" or "multi"))
            {
                var controls = new StackPanel { Orientation = Orientation.Horizontal };
                controls.Children.Add(SettingsKit.Field(HmeConfig.Str(Cfg.Names[d.Id]) is { Length: > 0 } n ? n : d.OriginalName, v =>
                {
                    if (v.Length == 0 || v == d.OriginalName) Cfg.Names.Remove(d.Id);
                    else Cfg.Names[d.Id] = v;
                }, 170));
                var currentRoom = Cfg.DeviceRoom.ContainsKey(d.Id) ? (HmeConfig.Str(Cfg.DeviceRoom[d.Id]) is { Length: > 0 } cr ? cr : Rooms.Other) : "";
                var room = SettingsKit.Choice(roomOptions, currentRoom, v =>
                {
                    if (v.Length == 0) Cfg.DeviceRoom.Remove(d.Id);
                    else Cfg.DeviceRoom[d.Id] = v == Rooms.Other ? "" : v;
                });
                room.Width = 130;
                room.Margin = new Thickness(8, 0, 8, 0);
                controls.Children.Add(room);
                var hidden = Cfg.Hide.Select(HmeConfig.Str).Contains(d.Id);
                var show = SettingsKit.Toggle(!hidden, on =>
                {
                    foreach (var node in Cfg.Hide.Where(x => HmeConfig.Str(x) == d.Id).ToList()) Cfg.Hide.Remove(node);
                    if (!on) Cfg.Hide.Add(d.Id);
                });
                AutomationProperties.SetName(show, "Mostrar no painel");
                show.ToolTip = "Mostrar no painel";
                controls.Children.Add(show);
                var help = (d.Online ? "" : "Offline · ") + (d.Source ?? "Smart Life") + (d.Room.Length > 0 && !Cfg.DeviceRoom.ContainsKey(d.Id) ? $" · {d.Room} (automático)" : "");
                SettingsKit.Row(card, d.OriginalName, help, controls);
                if (d.Kind == "multi")
                {
                    for (int i = 0; i < d.Channels.Count; i++)
                    {
                        int index = i;
                        var names = Cfg.ChannelNames[d.Id] as JsonArray;
                        var value = names != null && index < names.Count ? HmeConfig.Str(names[index]) : "";
                        SettingsKit.Row(card, $"   Tecla {index + 1}", null, SettingsKit.Field(value, v =>
                        {
                            var arr = Cfg.ChannelNames[d.Id] as JsonArray ?? new JsonArray();
                            while (arr.Count < d.Channels.Count) arr.Add("");
                            arr[index] = v;
                            Cfg.ChannelNames[d.Id] = arr.DeepClone();
                        }, 170));
                    }
                }
            }
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(SettingsKit.Button("Buscar aparelhos", async () =>
        {
            await Discover(status);
            Fill();
        }));
        buttons.Children.Add(SettingsKit.Button("Salvar", () =>
        {
            Save();
            status.Text = "Salvo.";
        }));
        page.Children.Add(buttons);
        page.Children.Add(status);
        page.Children.Add(list);
        if (_discovery != null) Fill();
        else status.Text = _discoveryError ?? "Clique em Buscar aparelhos.";
        return page;
    }

    // ---------------------------------------------------------------- air conditioner

    UIElement AcPage()
    {
        var page = Page("ac", "Ar-condicionado", "Ar Wi-Fi ou por controle IR do Smart Life. Cada ajuste vira um único comando.");
        var card = SettingsKit.Card(page);
        var details = new StackPanel { Visibility = Cfg.HasAc ? Visibility.Visible : Visibility.Collapsed };
        SettingsKit.Row(card, "Tenho ar-condicionado", "Desligado, o hme. esconde o ar do painel e dos atalhos e não consulta nada dele.",
            SettingsKit.Toggle(Cfg.HasAc, on =>
            {
                Cfg.HasAc = on;
                details.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                Save();
            }), "");
        page.Children.Add(details);

        var status = Status();
        var choose = SettingsKit.Card(details, "Qual ar");
        var acs = new StackPanel();
        choose.Children.Add(acs);

        void FillAcs()
        {
            acs.Children.Clear();
            var current = (HmeConfig.Str(Cfg.Ac["type"]), HmeConfig.Str(Cfg.Ac["remote_id"]), HmeConfig.Str(Cfg.Ac["device_id"]));
            if (_discovery == null)
            {
                SettingsKit.Row(acs, current.Item2.Length + current.Item3.Length > 0 ? "Ar escolhido" : "Nenhum ar escolhido",
                    current.Item2.Length + current.Item3.Length > 0 ? $"Tipo {current.Item1}" : "Busque os aparelhos para escolher.", new TextBlock());
                return;
            }
            if (_discovery.Acs.Count == 0)
            {
                SettingsKit.Row(acs, "Nenhum ar encontrado", _discovery.IrError.Length > 0 ? _discovery.IrError : "A conta não tem ar Wi-Fi nem controle de ar no hub IR.", new TextBlock());
                return;
            }
            var options = _discovery.Acs.Select((a, i) => (i, $"{a.Name} ({(a.Type == "wifi" ? "Wi-Fi" : a.Hub)})")).ToList();
            int selected = _discovery.Acs.ToList().FindIndex(a => a.Type == current.Item1 && (a.RemoteId == current.Item2 || (a.Type == "wifi" && a.DeviceId == current.Item3)));
            SettingsKit.Row(acs, "Ar-condicionado", null, SettingsKit.Choice(options, selected, i =>
            {
                var a = _discovery.Acs[i];
                Cfg.Ac["type"] = a.Type;
                Cfg.Ac["infrared_id"] = a.InfraredId;
                Cfg.Ac["remote_id"] = a.RemoteId;
                Cfg.Ac["device_id"] = a.DeviceId;
                Cfg.Ac["method"] = ""; // a different AC: find its sending method again
            }));
            if (_discovery.Sensors.Count > 0)
            {
                var sensors = new List<(string, string)> { ("", "Nenhum") };
                sensors.AddRange(_discovery.Sensors.Select(s => (s.Id, s.Name)));
                SettingsKit.Row(acs, "Sensor de temperatura", "Mostra a temperatura do cômodo no painel.", SettingsKit.Choice(sensors, Cfg.RoomSensorId, v => Cfg.RoomSensorId = v));
            }
        }

        var settings = SettingsKit.Card(details, "Ajustes do ar");
        SettingsKit.Row(settings, "Nome do cômodo", null, SettingsKit.Field(HmeConfig.Str(Cfg.Ac["room_name"]), v => Cfg.Ac["room_name"] = v));
        SettingsKit.Row(settings, "Temperatura mínima", null, SettingsKit.Slider(16, 30, Cfg.TempMin, 1, v => $"{v:0}°", v => Cfg.Ac["temp_min"] = (int)v));
        SettingsKit.Row(settings, "Temperatura máxima", null, SettingsKit.Slider(16, 32, Cfg.TempMax, 1, v => $"{v:0}°", v => Cfg.Ac["temp_max"] = (int)v));
        var modes = new StackPanel { Orientation = Orientation.Horizontal };
        var names = new Dictionary<string, string> { ["cool"] = "Frio", ["heat"] = "Quente", ["auto"] = "Auto", ["fan"] = "Ventilar", ["dry"] = "Secar" };
        var chosen = Cfg.AcModes.ToHashSet();
        foreach (var m in HmeConfig.AllModes)
        {
            var box = new CheckBox { Content = names[m], IsChecked = chosen.Contains(m), Margin = new Thickness(0, 0, 12, 0) };
            box.Click += (_, _) =>
            {
                if (box.IsChecked == true) chosen.Add(m);
                else chosen.Remove(m);
                Cfg.Ac["modes"] = new JsonArray(HmeConfig.AllModes.Where(chosen.Contains).Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());
            };
            modes.Children.Add(box);
        }
        SettingsKit.Row(settings, "Modos do controle", "Só os que o seu ar tem.", modes);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(SettingsKit.Button("Buscar ar", async () =>
        {
            await Discover(status);
            FillAcs();
        }));
        buttons.Children.Add(SettingsKit.Button("Salvar", () =>
        {
            Save();
            status.Text = "Salvo.";
        }));
        buttons.Children.Add(SettingsKit.Button("Testar o ar", async () =>
        {
            if (_host.Home == null)
            {
                status.Text = "Conecte a conta Tuya primeiro.";
                return;
            }
            status.Text = "Testando (o ar vai ligar a 24°)…";
            try { status.Text = await _host.Home.TestAc(); }
            catch (HmeCloudException ex) { status.Text = ex.Message; }
        }));
        details.Children.Add(buttons);
        details.Children.Add(status);
        FillAcs();
        return page;
    }

    // ---------------------------------------------------------------- Positivo

    UIElement PositivoPage()
    {
        var page = Page("positivo", "Positivo Casa Inteligente", "Opcional: lâmpadas e tomadas da Positivo, sem tirar do app deles (a Alexa continua igual). Não é uma conexão oficial: se a Positivo mudar o app, pode parar.");
        var card = SettingsKit.Card(page);
        string email = HmeConfig.Str(Cfg.Positivo["email"]), password = "";
        SettingsKit.Row(card, "E-mail", null, SettingsKit.Field(email, v => email = v, 260));
        var box = new PasswordBox { Width = 260 };
        box.PasswordChanged += (_, _) => password = box.Password;
        SettingsKit.Row(card, "Senha", email.Length > 0 ? "Deixe vazio para manter a salva." : null, box);
        var status = Status();
        var engine = _host.Home as HmeEngine;
        status.Text = engine == null ? "Conecte a conta Tuya primeiro (a Positivo entra junto com ela)."
            : engine.Positivo != null ? $"Conectado como {email}." + (engine.PositivoError.Length > 0 ? " Último erro: " + engine.PositivoError : "") : "";
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        Button? connect = null;
        connect = SettingsKit.Button("Conectar", async () =>
        {
            if (_host.Home is not HmeEngine e) return;
            if (email.Trim().Length == 0)
            {
                status.Text = "Preencha o e-mail.";
                return;
            }
            connect!.IsEnabled = false;
            status.Text = "Conectando…";
            var r = await e.ConnectPositivo(email.Trim(), password);
            connect.IsEnabled = true;
            status.Text = r.Ok ? $"Conectado: {r.Count} aparelho(s) — {string.Join(", ", r.Names)}." : r.Error;
        });
        connect.IsEnabled = engine != null;
        buttons.Children.Add(connect);
        if (engine?.Positivo != null)
        {
            buttons.Children.Add(SettingsKit.Button("Desconectar", () =>
            {
                engine.DisconnectPositivo();
                ShowPage();
            }));
        }
        page.Children.Add(buttons);
        page.Children.Add(status);
        return page;
    }

    // ---------------------------------------------------------------- diagnostics

    UIElement DiagPage()
    {
        var page = Page("diag", "Diagnóstico", "O uso da cota grátis da Tuya e o que cada aparelho responde.");
        var card = SettingsKit.Card(page);
        SettingsKit.Row(card, "Uso da cota", _host.Usage.Text() + (_host.Usage.QuotaOut ? " · esgotada, leituras pausadas" : ""), new TextBlock(), "");
        var output = new TextBox
        {
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MinHeight = 220, Margin = new Thickness(0, 14, 0, 0),
            FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(SettingsKit.Button("Gerar diagnóstico", async () =>
        {
            if (_host.Home == null)
            {
                output.Text = _host.BootError ?? "Nenhuma conta conectada.";
                return;
            }
            output.Text = "Consultando…";
            try { output.Text = await _host.Home.Diagnose(); }
            catch (Exception ex) when (ex is HmeCloudException or PositivoException) { output.Text = ex.Message; }
        }));
        buttons.Children.Add(SettingsKit.Button("Copiar", () => { if (output.Text.Length > 0) Clipboard.SetText(output.Text); }));
        page.Children.Add(buttons);
        page.Children.Add(output);
        return page;
    }
}
