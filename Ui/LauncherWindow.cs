using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ClonaDock.Dock;
using ClonaDock.Native;

namespace ClonaDock.Ui;

/// <summary>The app launcher: every Start menu shortcut, searchable, in a grid.</summary>
public sealed class LauncherWindow : FlyoutWindow
{
    static LauncherWindow? _open;

    sealed record AppEntry(string Name, string Path);

    readonly TextBox _search;
    readonly WrapPanel _grid;
    readonly List<AppEntry> _apps;
    readonly DockController _dock;

    public static void Toggle(FrameworkElement anchor, DockController dock)
    {
        if (_open != null)
        {
            _open.SafeClose();
            return;
        }
        _open = new LauncherWindow(dock);
        _open.Closed += (_, _) => _open = null;
        _open.ShowNear(anchor);
        _open._search.Focus();
    }

    LauncherWindow(DockController dock)
    {
        _dock = dock;
        _apps = ScanStartMenu();

        var root = new DockPanel { Width = 640, Height = 480 };
        _search = new TextBox
        {
            FontSize = 14,
            Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(0, 0, 0, 10),
            BorderThickness = new Thickness(1),
        };
        _search.SetResourceReference(TextBox.BackgroundProperty, "MenuHoverBrush");
        _search.SetResourceReference(TextBox.ForegroundProperty, "MenuTextBrush");
        _search.SetResourceReference(TextBox.BorderBrushProperty, "MenuBorderBrush");
        _search.SetResourceReference(TextBox.CaretBrushProperty, "MenuTextBrush");
        _search.TextChanged += (_, _) => Fill();
        _search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Filtered().FirstOrDefault() is AppEntry first) Start(first);
        };

        var hint = Subtle("Buscar apps");
        hint.IsHitTestVisible = false;
        hint.Margin = new Thickness(13, 9, 0, 0);
        var searchHost = new Grid();
        searchHost.Children.Add(_search);
        searchHost.Children.Add(hint);
        _search.TextChanged += (_, _) => hint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        DockPanel.SetDock(searchHost, System.Windows.Controls.Dock.Top);
        root.Children.Add(searchHost);

        _grid = new WrapPanel();
        var scroll = new ScrollViewer { Content = _grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        root.Children.Add(scroll);
        Body = root;
        Fill();
    }

    IEnumerable<AppEntry> Filtered()
    {
        var q = _search.Text.Trim();
        return q.Length == 0 ? _apps : _apps.Where(a => a.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase));
    }

    void Fill()
    {
        _grid.Children.Clear();
        foreach (var app in Filtered())
        {
            var cell = new StackPanel { Width = 96 };
            var icon = new Image { Width = 44, Height = 44, Margin = new Thickness(0, 4, 0, 4) };
            // Icons load after the grid shows, so a long Start menu opens instantly.
            Dispatcher.BeginInvoke(() => icon.Source = IconLoader.Get(app.Path, 64, cache: false), System.Windows.Threading.DispatcherPriority.Background);
            cell.Children.Add(icon);
            cell.Children.Add(new TextBlock
            {
                Text = app.Name,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 34,
                FontSize = 11.5,
            });
            var a = app;
            var button = FlatButton(cell, () => Start(a), 4);
            button.HorizontalContentAlignment = HorizontalAlignment.Center;
            button.ToolTip = app.Name;
            var menu = new ContextMenu();
            menu.Items.Add(DockController.Item("Abrir", () => Start(a)));
            menu.Items.Add(DockController.Item("Adicionar ao Dock", () => _dock.Pin(a.Path, a.Name)));
            menu.Items.Add(DockController.Item("Mostrar no Explorador de Arquivos", () => DockController.ShowInExplorer(a.Path)));
            button.ContextMenu = menu;
            _grid.Children.Add(button);
        }
        if (_grid.Children.Count == 0)
            _grid.Children.Add(Subtle($"Nada chamado “{_search.Text.Trim()}”"));
    }

    void Start(AppEntry app)
    {
        SafeClose();
        DockController.Launch(app.Path);
    }

    static List<AppEntry> ScanStartMenu()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        };
        var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        var list = new List<AppEntry>();
        foreach (var root in roots.Where(Directory.Exists))
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories).ToList(); }
            catch { continue; }
            foreach (var file in files)
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is not (".lnk" or ".url" or ".appref-ms")) continue;
                var name = Path.GetFileNameWithoutExtension(file);
                var lower = name.ToLowerInvariant();
                if (lower.Contains("uninstall") || lower.Contains("desinstalar") || lower.StartsWith("readme")) continue;
                if (seen.Add(name)) list.Add(new AppEntry(name, file));
            }
        }
        return list.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
