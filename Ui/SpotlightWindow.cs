using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Shft.Core;
using Shft.Dock;
using Shft.Native;

namespace Shft.Ui;

public sealed record AppEntry(string Name, string Path, string? Detail = null);

/// <summary>Every app Windows knows: the same Apps list the Start menu shows, Store apps included.</summary>
public static class AppCatalog
{
    static volatile List<AppEntry> _apps = new();
    static int _scanning;

    public static IReadOnlyList<AppEntry> Apps => _apps;
    public static event Action? Updated;

    /// <summary>Refreshes in the background; the panel opens at once with the last list.</summary>
    public static void Refresh()
    {
        if (Interlocked.Exchange(ref _scanning, 1) == 1) return;
        var ui = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var t = new Thread(() =>
        {
            try
            {
                var fresh = Scan();
                if (fresh.Count > 0) _apps = fresh;
                ui.BeginInvoke(() => Updated?.Invoke());
            }
            catch (Exception ex)
            {
                Log.Write("apps not listed: " + ex.Message);
            }
            finally
            {
                _scanning = 0;
            }
        }) { IsBackground = true, Name = "app catalog" };
        t.SetApartmentState(ApartmentState.STA); // Shell.Application is an apartment object
        t.Start();
    }

    static List<AppEntry> Scan()
    {
        var list = new List<AppEntry>();
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        dynamic folder = shell.NameSpace("shell:AppsFolder");
        foreach (dynamic item in folder.Items())
        {
            string name = item.Name, path = item.Path;
            if (IsNoise(name)) continue;
            list.Add(new AppEntry(name, AppsFolder.PathFor(path)));
        }
        // A name can appear twice (per-user and per-machine shortcuts of the same app); one row is enough.
        return list.GroupBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).Select(g => g.First())
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    static bool IsNoise(string name)
    {
        var l = name.ToLowerInvariant();
        return l.Contains("uninstall") || l.Contains("desinstalar") || l.StartsWith("readme", StringComparison.Ordinal);
    }
}

/// <summary>Search ranking, kept pure so it can be tested.</summary>
public static class AppSearch
{
    /// <summary>Higher is better; 0 means no match. Prefix beats word start beats substring beats scattered letters.</summary>
    public static int Score(string name, string query)
    {
        if (query.Length == 0) return 1;
        var n = name.ToLowerInvariant();
        var q = query.ToLowerInvariant();
        if (n == q) return 1000;
        if (n.StartsWith(q, StringComparison.Ordinal)) return 800;
        int at = n.IndexOf(q, StringComparison.Ordinal);
        if (at > 0) return n[at - 1] is ' ' or '-' or '.' or '_' ? 600 : 400;
        int i = 0;
        foreach (char c in n)
            if (i < q.Length && c == q[i]) i++;
        return i == q.Length ? 150 : 0;
    }

    /// <summary>Best matches first; among equals, recent apps win, then favourites, then the name.</summary>
    public static List<AppEntry> Rank(IEnumerable<AppEntry> apps, string query, IReadOnlyList<string> recent, IReadOnlyList<string> favorites) =>
        apps.Select(a => (App: a, Score: Score(a.Name, query)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score + Boost(x.App.Path, recent, favorites))
            .ThenBy(x => x.App.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(x => x.App)
            .ToList();

    static int Boost(string path, IReadOnlyList<string> recent, IReadOnlyList<string> favorites)
    {
        int r = -1;
        for (int i = 0; i < recent.Count && r < 0; i++)
            if (recent[i].Equals(path, StringComparison.OrdinalIgnoreCase)) r = i;
        return (r >= 0 ? 60 - Math.Min(r, 30) : 0) + (favorites.Contains(path, StringComparer.OrdinalIgnoreCase) ? 20 : 0);
    }

    /// <summary>A typed program, path or address that can run as is: "cmd", "C:\tools\x.exe", "https://…".</summary>
    public static AppEntry? Direct(string text, Func<string, bool>? fileExists = null, string? pathVariable = null)
    {
        var t = text.Trim();
        fileExists ??= File.Exists;
        if (t.Length < 2) return null;
        if (Uri.TryCreate(t, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") return new AppEntry(t, t, "Abrir endereço");
        if (Path.IsPathFullyQualified(t) && (fileExists(t) || Directory.Exists(t))) return new AppEntry(Path.GetFileName(t.TrimEnd('\\')), t, t);
        var exe = t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? t : t + ".exe";
        if (exe.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        foreach (var dir in (pathVariable ?? Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim(), exe);
            if (fileExists(candidate)) return new AppEntry(exe, candidate, "Executar");
        }
        return null;
    }
}

/// <summary>Applications: a Spotlight-style search over every app, driven from the keyboard.</summary>
public sealed class SpotlightWindow : FlyoutWindow
{
    static SpotlightWindow? _open;

    readonly TextBox _search;
    readonly ListBox _list;
    readonly TextBlock _empty;

    public static void Toggle(MonitorInfo? monitor = null)
    {
        if (_open != null)
        {
            _open.SafeClose();
            return;
        }
        AppCatalog.Refresh();
        _open = new SpotlightWindow();
        _open.Closed += (_, _) => _open = null;
        var target = monitor ?? Monitors.ForWindow(Win32.GetForegroundWindow());
        _open.ShowCentered(target);
        Win32.Activate(new System.Windows.Interop.WindowInteropHelper(_open).Handle);
        _open._search.Focus();
    }

    SpotlightWindow()
    {
        Radius = 22;
        Inner = new Thickness(0);
        var root = new DockPanel { Width = 620 };

        var icon = Glyph.Text(Glyph.Search, 20);
        icon.Margin = new Thickness(18, 0, 12, 0);
        icon.Opacity = 0.7;
        _search = new TextBox
        {
            FontSize = 22,
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0, 14, 16, 14),
        };
        _search.SetResourceReference(TextBox.ForegroundProperty, "TextBrush");
        _search.SetResourceReference(TextBox.CaretBrushProperty, "TextBrush");
        System.Windows.Automation.AutomationProperties.SetName(_search, "Buscar apps");
        var hint = new TextBlock { Text = "Buscar apps", FontSize = 22, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.45, FontFamily = _search.FontFamily, Margin = new Thickness(3, 0, 0, 0) };
        var field = new Grid();
        field.Children.Add(_search);
        field.Children.Add(hint);
        var header = new DockPanel();
        header.Children.Add(icon);
        header.Children.Add(field);
        DockPanel.SetDock(header, System.Windows.Controls.Dock.Top);
        root.Children.Add(header);

        _list = new ListBox
        {
            Height = 396,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(8, 4, 8, 8),
            Focusable = false,
            ItemTemplate = new DataTemplate { VisualTree = new FrameworkElementFactory(typeof(AppRow)) },
            ItemContainerStyle = (Style)Application.Current.Resources["AppRowItem"],
        };
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _empty = new TextBlock { Text = "", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "TextSubtleBrush");
        var body = new Grid();
        body.Children.Add(_list);
        body.Children.Add(_empty);
        var line = new Border { Height = 1 };
        line.SetResourceReference(Border.BackgroundProperty, "TileRimBrush");
        DockPanel.SetDock(line, System.Windows.Controls.Dock.Top);
        root.Children.Add(line);
        root.Children.Add(body);
        Body = root;

        _search.TextChanged += (_, _) =>
        {
            hint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            Fill();
        };
        _search.PreviewKeyDown += OnKey;
        _list.MouseDoubleClick += (_, e) => StartSelected();
        _list.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if ((e.OriginalSource as DependencyObject) is { } hit && ItemsControl.ContainerFromElement(_list, hit) is ListBoxItem item)
            {
                _list.SelectedItem = item.Content;
                StartSelected();
            }
        };
        _list.PreviewMouseRightButtonUp += (_, e) =>
        {
            if ((e.OriginalSource as DependencyObject) is { } hit && ItemsControl.ContainerFromElement(_list, hit) is ListBoxItem { Content: AppEntry app })
            {
                e.Handled = true;
                ShowMenu(app);
            }
        };
        AppCatalog.Updated += Fill;
        Closed += (_, _) => AppCatalog.Updated -= Fill;
        Fill();
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down: Step(1); break;
            case Key.Up: Step(-1); break;
            case Key.PageDown: Step(8); break;
            case Key.PageUp: Step(-8); break;
            case Key.Enter: StartSelected(); break;
            default: return;
        }
        e.Handled = true;
    }

    void Step(int delta)
    {
        if (_list.Items.Count == 0) return;
        _list.SelectedIndex = Math.Clamp(_list.SelectedIndex + delta, 0, _list.Items.Count - 1);
        _list.ScrollIntoView(_list.SelectedItem);
    }

    void Fill()
    {
        var s = SettingsStore.Current;
        var q = _search.Text.Trim();
        var apps = AppCatalog.Apps;
        List<object> items;
        if (q.Length == 0)
        {
            // Favourites, then recents, then everything.
            var byPath = apps.GroupBy(a => a.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var top = s.AppsFavorites.Concat(s.AppsRecent).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(p => byPath.GetValueOrDefault(p)).OfType<AppEntry>().ToList();
            items = top.Concat(apps.Where(a => !top.Contains(a))).Cast<object>().ToList();
        }
        else
        {
            items = AppSearch.Rank(apps, q, s.AppsRecent, s.AppsFavorites).Cast<object>().ToList();
            if (AppSearch.Direct(q) is { } direct) items.Insert(items.Count > 0 ? 1 : 0, direct);
        }
        _list.ItemsSource = items;
        if (items.Count > 0) _list.SelectedIndex = 0;
        _list.ScrollIntoView(_list.SelectedItem);
        _empty.Text = apps.Count == 0 ? "Carregando apps…" : $"Nada chamado “{q}”";
        _empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void StartSelected()
    {
        if (_list.SelectedItem is AppEntry app) Start(app);
    }

    void Start(AppEntry app)
    {
        var s = SettingsStore.Current;
        s.AppsRecent.RemoveAll(p => p.Equals(app.Path, StringComparison.OrdinalIgnoreCase));
        s.AppsRecent.Insert(0, app.Path);
        if (s.AppsRecent.Count > 12) s.AppsRecent.RemoveRange(12, s.AppsRecent.Count - 12);
        SettingsStore.Save(notify: false);
        SafeClose();
        DockController.Launch(app.Path);
    }

    void ShowMenu(AppEntry app)
    {
        var s = SettingsStore.Current;
        bool favorite = s.AppsFavorites.Contains(app.Path, StringComparer.OrdinalIgnoreCase);
        var menu = new ContextMenu();
        menu.Items.Add(DockController.Item("Abrir", () => Start(app)));
        menu.Items.Add(DockController.Item(favorite ? "Tirar dos favoritos" : "Adicionar aos favoritos", () =>
        {
            if (favorite) s.AppsFavorites.RemoveAll(p => p.Equals(app.Path, StringComparison.OrdinalIgnoreCase));
            else s.AppsFavorites.Add(app.Path);
            SettingsStore.Save(notify: false);
            Fill();
        }));
        if (App.Dock is { } dock) menu.Items.Add(DockController.Item("Adicionar ao Dock", () => dock.Pin(app.Path, app.Name)));
        menu.IsOpen = true;
    }

    /// <summary>One result. Icons load after the row shows, so scrolling stays smooth.</summary>
    sealed class AppRow : DockPanel
    {
        readonly Image _icon = new() { Width = 32, Height = 32, Margin = new Thickness(0, 0, 12, 0) };
        readonly TextBlock _name = new() { FontSize = 14, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        readonly TextBlock _detail = new() { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) };
        readonly TextBlock _star = Glyph.Text("\uE735", 11);

        public AppRow()
        {
            _detail.SetResourceReference(TextBlock.ForegroundProperty, "TextSubtleBrush");
            _star.Margin = new Thickness(8, 0, 4, 0);
            _star.Opacity = 0.6;
            SetDock(_detail, System.Windows.Controls.Dock.Right);
            SetDock(_star, System.Windows.Controls.Dock.Right);
            Children.Add(_detail);
            Children.Add(_star);
            Children.Add(_icon);
            Children.Add(_name);
            RenderOptions.SetBitmapScalingMode(_icon, BitmapScalingMode.HighQuality);
            DataContextChanged += (_, _) => Show();
        }

        void Show()
        {
            if (DataContext is not AppEntry app) return;
            _name.Text = app.Name;
            _detail.Text = app.Detail ?? "";
            _star.Visibility = SettingsStore.Current.AppsFavorites.Contains(app.Path, StringComparer.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
            _icon.Source = null;
            Dispatcher.BeginInvoke(() =>
            {
                if (ReferenceEquals(DataContext, app)) _icon.Source = IconLoader.Get(app.Path, 64, cache: false);
            }, System.Windows.Threading.DispatcherPriority.Background);
        }
    }
}
