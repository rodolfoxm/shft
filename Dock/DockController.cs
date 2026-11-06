using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Shft.Core;
using Shft.Native;
using Shft.Ui;
using Shft.Widgets;

namespace Shft.Dock;

/// <summary>
/// Decides what the Dock shows (pinned items, open apps, folders, widgets) and
/// what happens when you click, right-click, drag or drop on it.
/// </summary>
public sealed class DockController : IDisposable
{
    readonly DockWindow _window;
    readonly DockView _view;
    readonly WindowTracker _tracker = new();
    readonly BadgeWatcher _badges = new();
    string? _loggedOpenApps;
    readonly Dictionary<string, DockTile> _tiles = new();
    readonly List<string> _runningOrder = new();
    int _menusOpen;

    public DockController(DockWindow window, DockView view)
    {
        _window = window;
        _view = view;
        _view.TileClicked += OnTileClicked;
        _view.TileMenu += OnTileMenu;
        _view.BarMenu += OnBarMenu;
        _view.TileMoved += OnTileMoved;
        _view.TileRemoved += OnTileRemoved;
        _tracker.Changed += OnWindowsChanged;
        _tracker.Changed += ActivateLaunchedWindows;
        Instance = this;
        _badges.Changed += () => UpdateBadges(_view.Tiles);
    }

    void UpdateBadges(IEnumerable<DockTile> tiles)
    {
        var counts = _badges.Counts;
        foreach (var t in tiles)
            t.Badge = t.MatchKey != null && counts.TryGetValue(t.MatchKey, out var c) ? c : 0;
    }

    /// <summary>A menu or a flyout is open, so auto-hide must wait.</summary>
    public bool IsBusy => _menusOpen > 0 || FlyoutWindow.OpenCount > 0;

    public void Start()
    {
        _tracker.Start();
        ApplySettings();
    }

    public void ApplySettings()
    {
        var s = SettingsStore.Current;
        var theme = Theme.Resolve(s);
        App.ApplyTheme(theme);
        _view.Configure(s, theme);
        foreach (var w in _tiles.Values.Select(t => t.Child).OfType<WidgetBase>()) w.ApplyTheme(theme.IsDark);
        if (s.ShowBadges) _badges.Start();
        else _badges.Stop();
        Rebuild();
        _window.ApplyLayout();
    }

    // ---------------------------------------------------------------- building the Dock

    DockTile Tile(string key, EntryKind kind)
    {
        if (_tiles.TryGetValue(key, out var t) && t.Kind == kind) return t;
        t = new DockTile(key, kind);
        _tiles[key] = t;
        return t;
    }

    void Rebuild()
    {
        var s = SettingsStore.Current;
        var list = new List<DockTile>();
        var windows = _tracker.Windows;
        var pinnedKeys = new HashSet<string>();

        if (s.ShowLauncher)
        {
            var t = Tile("launcher", EntryKind.Launcher);
            t.Title = "Applications";
            t.Section = Section.Leading;
            if (t.Tag as string != s.LauncherIcon || t.Child == null)
            {
                t.Child = LauncherIcon(s.LauncherIcon);
                t.Tag = s.LauncherIcon;
            }
            list.Add(t);
        }

        foreach (var item in s.Items.Where(i => !i.IsTrailing))
        {
            switch (item.Kind)
            {
                case DockItemKind.App:
                {
                    var t = Tile(item.Id, EntryKind.App);
                    t.Item = item;
                    t.Section = Section.Leading;
                    t.LaunchPath = item.Path;
                    t.MatchKey = ShortcutResolver.MatchKey(item.Path);
                    t.Title = !string.IsNullOrWhiteSpace(item.Name) ? item.Name!
                        : File.Exists(item.Path) && !item.Path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
                            ? WindowTracker.DisplayName(item.Path) : Path.GetFileNameWithoutExtension(item.Path);
                    t.Child ??= IconImage(item.Path);
                    if (t.MatchKey != null) pinnedKeys.Add(t.MatchKey);
                    list.Add(t);
                    break;
                }
                case DockItemKind.Spacer:
                {
                    var t = Tile(item.Id, EntryKind.Spacer);
                    t.Item = item;
                    t.Section = Section.Leading;
                    t.LengthUnits = item.LargeSpacer ? 1 : 0.45;
                    t.Title = "Espaçador · botão direito para remover";
                    list.Add(t);
                    break;
                }
                case DockItemKind.Widget:
                {
                    var t = Tile(item.Id, EntryKind.Widget);
                    t.Item = item;
                    t.Section = Section.Leading;
                    if (t.Child is not WidgetBase)
                    {
                        var w = WidgetFactory.Create(item);
                        w.ApplyTheme(Theme.Resolve(s).IsDark);
                        t.Child = w;
                    }
                    var widget = (WidgetBase)t.Child;
                    t.Title = widget.Title;
                    t.LengthUnits = widget.WidthUnits;
                    list.Add(t);
                    break;
                }
            }
        }

        if (s.ShowOpenApps)
        {
            foreach (var key in windows.Select(w => w.Key).Distinct())
                if (!pinnedKeys.Contains(key) && !IsHidden(key) && !_runningOrder.Contains(key)) _runningOrder.Add(key);
            _runningOrder.RemoveAll(k => pinnedKeys.Contains(k) || IsHidden(k) || windows.All(w => w.Key != k));

            if (_runningOrder.Count > 0)
            {
                var sep = Tile("sep-running", EntryKind.Separator);
                sep.LengthUnits = 0.3;
                sep.Section = Section.Running;
                list.Add(sep);
                foreach (var key in _runningOrder)
                {
                    var first = windows.First(w => w.Key == key);
                    var t = Tile("run:" + key, EntryKind.App);
                    t.Item = null;
                    t.Section = Section.Running;
                    t.LaunchPath = first.LaunchPath;
                    t.MatchKey = key;
                    t.Title = WindowTracker.DisplayName(first.ExePath);
                    t.Child ??= IconImage(first.LaunchPath);
                    list.Add(t);
                }
            }
        }

        var trailing = new List<DockTile>();
        foreach (var item in s.Items.Where(i => i.IsTrailing))
        {
            var kind = item.Kind == DockItemKind.Folder ? EntryKind.Folder : EntryKind.File;
            var t = Tile(item.Id, kind);
            t.Item = item;
            t.Section = Section.Trailing;
            t.LaunchPath = item.Path;
            t.Title = !string.IsNullOrWhiteSpace(item.Name) ? item.Name! : Path.GetFileName(item.Path.TrimEnd('\\')) is { Length: > 0 } n ? n : item.Path;
            t.Child ??= IconImage(item.Path);
            trailing.Add(t);
        }
        if (s.ShowDownloads)
        {
            var t = Tile("downloads", EntryKind.Downloads);
            t.Title = "Downloads";
            t.Section = Section.Trailing;
            t.LaunchPath = KnownFolders.Downloads;
            t.Child ??= IconImage(KnownFolders.Downloads);
            trailing.Add(t);
        }
        if (s.ShowRecycleBin)
        {
            var t = Tile("trash", EntryKind.RecycleBin);
            t.Title = "Lixeira";
            t.Section = Section.Trailing;
            t.Child = IconImage(IconLoader.RecycleBin, refresh: true);
            trailing.Add(t);
        }
        if (trailing.Count > 0)
        {
            var sep = Tile("sep-trailing", EntryKind.Separator);
            sep.LengthUnits = 0.3;
            sep.Section = Section.Trailing;
            list.Add(sep);
            list.AddRange(trailing);
        }

        // Forget tiles that are gone so their widgets stop their timers.
        var keep = list.Select(t => t.Key).ToHashSet();
        foreach (var gone in _tiles.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            if (_tiles[gone].Child is WidgetBase w) w.Stop();
            _tiles.Remove(gone);
        }

        var openApps = string.Join(", ", list.Where(t => t.Kind == EntryKind.App && t.Section == Section.Running).Select(t => t.Title));
        if (openApps != _loggedOpenApps)
        {
            _loggedOpenApps = openApps;
            Log.Write("open apps in the Dock: " + openApps);
        }
        UpdateRunning(list);
        UpdateBadges(list);
        _view.SetTiles(list);
    }

    static readonly string[] PictureExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" };

    /// <summary>The launcher's look: the dot grid, a picture, or another app's icon.</summary>
    static UIElement LauncherIcon(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !(File.Exists(path) || AppsFolder.IsAppsFolder(path)))
            return new LauncherGlyph();
        if (PictureExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
        {
            try
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; // don't lock the file
                bmp.UriSource = new Uri(path);
                bmp.DecodePixelWidth = 256;
                bmp.EndInit();
                bmp.Freeze();
                var image = new Image { Source = bmp, Stretch = Stretch.Uniform };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                return image;
            }
            catch (Exception ex)
            {
                Log.Write("launcher picture not read: " + ex.Message);
                return new LauncherGlyph();
            }
        }
        return IconImage(path); // .ico, .exe, .lnk: the shell's icon
    }

    public void ChooseLauncherIcon()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Escolha uma imagem ou um app para usar o ícone dele",
            Filter = "Imagens e apps|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.ico;*.exe;*.lnk|Todos os arquivos|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        SettingsStore.Current.LauncherIcon = dlg.FileName;
        SettingsStore.Save();
    }

    public static void ResetLauncherIcon()
    {
        SettingsStore.Current.LauncherIcon = "";
        SettingsStore.Save();
    }

    static Image IconImage(string path, bool refresh = false)
    {
        if (refresh) IconLoader.Forget(path);
        var s = SettingsStore.Current;
        var image = new Image
        {
            Source = IconLoader.Get(path, IconLoader.DockSize(s.IconSize, s.Magnification, Monitors.Pick(s.Monitor).Scale)),
            Stretch = Stretch.Uniform,
            SnapsToDevicePixels = false,
        };
        // 256 px icons shown at ~48 px alias badly with the default filter.
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        return image;
    }

    void UpdateRunning(IEnumerable<DockTile> tiles)
    {
        var windows = _tracker.Windows;
        var fg = Win32.GetForegroundWindow();
        foreach (var t in tiles)
        {
            if (t.Kind != EntryKind.App || t.MatchKey == null)
            {
                t.Windows = new List<AppWindow>();
                t.IsRunning = false;
                continue;
            }
            t.Windows = windows.Where(w => w.Key == t.MatchKey).ToList();
            t.IsRunning = t.Windows.Count > 0;
            t.IsActive = t.Windows.Any(w => w.Handle == fg);
        }
    }

    void OnWindowsChanged()
    {
        var running = _tracker.Windows.Select(w => w.Key).ToHashSet();
        var shown = _tiles.Values.Where(t => t.Section == Section.Running && t.MatchKey != null).Select(t => t.MatchKey!).ToHashSet();
        var pinned = _tiles.Values.Where(t => t.Section == Section.Leading && t.MatchKey != null).Select(t => t.MatchKey!).ToHashSet();
        bool needsRebuild = SettingsStore.Current.ShowOpenApps && !running.Where(k => !pinned.Contains(k) && !IsHidden(k)).ToHashSet().SetEquals(shown);
        if (needsRebuild) Rebuild();
        else UpdateRunning(_view.Tiles);
    }

    // ---------------------------------------------------------------- clicks

    void OnTileClicked(DockTile tile, MouseButton button)
    {
        try
        {
            switch (tile.Kind)
            {
                case EntryKind.Launcher:
                    SpotlightWindow.Toggle(_window.Monitor);
                    break;
                case EntryKind.App:
                    if (button == MouseButton.Middle || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) Launch(tile);
                    else ActivateOrLaunch(tile);
                    break;
                case EntryKind.Folder:
                case EntryKind.Downloads:
                    if (button == MouseButton.Middle) Open(tile.LaunchPath!);
                    else new StackFlyout(tile.LaunchPath!, tile.Title).ShowNear(tile);
                    break;
                case EntryKind.File:
                    Open(tile.LaunchPath!);
                    break;
                case EntryKind.RecycleBin:
                    Open("shell:RecycleBinFolder");
                    break;
                case EntryKind.Widget when tile.Child is WidgetBase w:
                    w.OnClick(tile);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"click on {tile.Title} failed: {ex.Message}");
        }
    }

    void ActivateOrLaunch(DockTile tile)
    {
        var wins = tile.Windows.Where(w => Win32.IsWindow(w.Handle)).ToList();
        if (wins.Count == 0)
        {
            Launch(tile);
            return;
        }

        var fg = Win32.GetForegroundWindow();
        int current = wins.FindIndex(w => w.Handle == fg);
        if (wins.Count == 1)
        {
            var h = wins[0].Handle;
            if (current == 0 && !Win32.IsIconic(h)) Win32.ShowWindowAsync(h, Win32.SW_MINIMIZE);
            else Bring(h, tile.Title);
            return;
        }

        if (current >= 0)
        {
            // Already in front: step to the app's next window.
            Bring(wins[(current + 1) % wins.Count].Handle, tile.Title);
            return;
        }

        // Bring every visible window of the app forward, keeping their order.
        var visible = wins.Where(w => !Win32.IsIconic(w.Handle)).ToList();
        if (visible.Count == 0) Bring(wins[0].Handle, tile.Title);
        else
        {
            for (int i = visible.Count - 1; i >= 0; i--) Bring(visible[i].Handle, tile.Title);
        }
    }

    static void Bring(IntPtr h, string app)
    {
        if (!Win32.Activate(h))
            Log.Write($"Windows refused to bring {app} forward (focus-stealing protection)");
    }

    /// <summary>Closes the windows; apps running as administrator can't be closed from here, so say so.</summary>
    static void CloseWindows(IEnumerable<AppWindow> windows, string app)
    {
        bool blocked = false;
        foreach (var w in windows)
            if (!Win32.RequestClose(w.Handle)) blocked = true;
        if (!blocked) return;
        Log.Write($"close blocked for {app} (runs as administrator)");
        MessageBox.Show(
            $"O {app} está rodando como administrador, e o Windows não deixa outros apps fecharem as janelas dele.\n\nFeche pelo próprio {app}.",
            "shft.", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public void Launch(DockTile tile)
    {
        if (string.IsNullOrEmpty(tile.LaunchPath)) return;
        if (Launch(tile.LaunchPath, tile.Item?.Arguments) && SettingsStore.Current.BounceOnLaunch && !Motion.Reduced)
        {
            tile.Bounce();
            _view.Wake();
        }
    }

    public static bool Launch(string path, string? args = null)
    {
        try
        {
            var psi = new ProcessStartInfo(path) { UseShellExecute = true };
            if (!string.IsNullOrWhiteSpace(args)) psi.Arguments = args;
            if (File.Exists(path)) psi.WorkingDirectory = Path.GetDirectoryName(path) ?? "";
            // The Dock never takes focus, so Windows may not let what it starts come to the front.
            // Hand our foreground right over to whatever process gets launched...
            AllowSetForegroundWindow(-1 /* ASFW_ANY */);
            Process.Start(psi);
            // ...and, if its window still opens behind, bring it forward when it appears.
            Instance?.ExpectWindow(path);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"could not open {path}: {ex.Message}");
            MessageBox.Show($"Não foi possível abrir:\n{path}\n\n{ex.Message}", "shft.", MessageBoxButton.OK, MessageBoxImage.Warning, MessageBoxResult.OK, MessageBoxOptions.DefaultDesktopOnly); // Applications is closing
            return false;
        }
    }

    public static void Open(string path) => Launch(path);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int processId);

    static DockController? Instance;

    sealed record PendingLaunch(string? Key, HashSet<IntPtr> Existing, DateTime Until);

    readonly List<PendingLaunch> _pendingLaunches = new();

    /// <summary>Remembers a launch so its first new window can be brought forward.</summary>
    void ExpectWindow(string path)
    {
        var key = Directory.Exists(path) ? null : ShortcutResolver.MatchKey(path);
        _pendingLaunches.Add(new PendingLaunch(key, _tracker.Windows.Select(w => w.Handle).ToHashSet(), DateTime.UtcNow.AddSeconds(15)));
    }

    /// <summary>A window from a recent launch appeared behind other windows: bring it to the front once.</summary>
    void ActivateLaunchedWindows()
    {
        _pendingLaunches.RemoveAll(p => p.Until < DateTime.UtcNow);
        if (_pendingLaunches.Count == 0) return;
        var fg = Win32.GetForegroundWindow();
        foreach (var pending in _pendingLaunches.ToList())
        {
            var fresh = _tracker.Windows.Where(w => !pending.Existing.Contains(w.Handle)).ToList();
            // A known target matches by app; shortcuts without one take the first new window.
            var window = pending.Key != null ? fresh.FirstOrDefault(w => w.Key == pending.Key) : fresh.FirstOrDefault();
            if (window == null) continue;
            _pendingLaunches.Remove(pending);
            if (window.Handle != fg) Bring(window.Handle, Path.GetFileNameWithoutExtension(window.ExePath));
        }
    }

    public static void ShowInExplorer(string path)
    {
        try
        {
            var target = ShortcutResolver.TargetOf(path) ?? path;
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{target}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write("show in explorer failed: " + ex.Message);
        }
    }

    // ---------------------------------------------------------------- menus

    void OnTileMenu(DockTile tile)
    {
        var menu = NewMenu(tile);
        switch (tile.Kind)
        {
            case EntryKind.App:
                BuildAppMenu(menu, tile);
                break;
            case EntryKind.Widget when tile.Child is WidgetBase w:
                w.AddMenuItems(menu);
                if (menu.Items.Count > 0) menu.Items.Add(new Separator());
                menu.Items.Add(Item("Apagar widget", () => RemoveItem(tile.Item!)));
                break;
            case EntryKind.Spacer:
                menu.Items.Add(Item("Remover espaçador", () => RemoveItem(tile.Item!)));
                break;
            case EntryKind.Folder:
            case EntryKind.File:
            case EntryKind.Downloads:
                menu.Items.Add(Item("Abrir", () => Open(tile.LaunchPath!)));
                menu.Items.Add(Item("Mostrar no Explorador de Arquivos", () => ShowInExplorer(tile.LaunchPath!)));
                if (tile.Item != null)
                {
                    menu.Items.Add(new Separator());
                    menu.Items.Add(Item("Remover do Dock", () => RemoveItem(tile.Item)));
                }
                break;
            case EntryKind.RecycleBin:
                menu.Items.Add(Item("Abrir", () => Open("shell:RecycleBinFolder")));
                menu.Items.Add(Item("Esvaziar Lixeira", () =>
                {
                    Win32.SHEmptyRecycleBin(IntPtr.Zero, null, 0);
                    Rebuild();
                }));
                break;
            case EntryKind.Launcher:
                menu.Items.Add(Item("Abrir Applications", () => SpotlightWindow.Toggle(_window.Monitor)));
                menu.Items.Add(Item("Configurações do Windows", () => Open("ms-settings:")));
                menu.Items.Add(Item("Gerenciador de Tarefas", () => Open("taskmgr.exe")));
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Mudar ícone…", ChooseLauncherIcon));
                if (!string.IsNullOrEmpty(SettingsStore.Current.LauncherIcon))
                    menu.Items.Add(Item("Voltar ao ícone original", ResetLauncherIcon));
                menu.Items.Add(new Separator());
                AddDockItems(menu);
                break;
            default:
                AddDockItems(menu);
                break;
        }
        menu.IsOpen = true;
    }

    void BuildAppMenu(ContextMenu menu, DockTile tile)
    {
        var wins = tile.Windows.Where(w => Win32.IsWindow(w.Handle)).ToList();
        foreach (var w in wins.Take(12))
        {
            var title = string.IsNullOrWhiteSpace(w.Title) ? tile.Title : w.Title;
            if (title.Length > 60) title = title[..57] + "…";
            var h = w.Handle;
            menu.Items.Add(Item(title, () => Bring(h, tile.Title)));
        }
        if (wins.Count > 0) menu.Items.Add(new Separator());

        if (!string.IsNullOrEmpty(tile.LaunchPath))
            menu.Items.Add(Item(wins.Count > 0 ? "Nova janela" : "Abrir", () => Launch(tile)));

        if (tile.Item == null && tile.LaunchPath != null)
        {
            menu.Items.Add(Item("Manter no Dock", () => Pin(tile.LaunchPath, tile.Title)));
            if (tile.MatchKey != null)
                menu.Items.Add(Item("Nunca mostrar no Dock", () => Hide(tile.MatchKey, tile.Title)));
        }
        else if (tile.Item != null)
            menu.Items.Add(Item("Remover do Dock", () => RemoveItem(tile.Item)));

        if (!string.IsNullOrEmpty(tile.LaunchPath) && !AppsFolder.IsAppsFolder(tile.LaunchPath))
            menu.Items.Add(Item("Mostrar no Explorador de Arquivos", () => ShowInExplorer(tile.LaunchPath)));

        if (tile.Item != null)
            menu.Items.Add(Item("Renomear…", () => Rename(tile.Item, tile.Title)));

        if (wins.Count > 0)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(wins.Count == 1 ? "Fechar janela" : $"Fechar {wins.Count} janelas", () =>
            {
                CloseWindows(wins, tile.Title);
            }));
        }
    }

    void OnBarMenu()
    {
        var menu = NewMenu(_view);
        AddDockItems(menu);
        menu.IsOpen = true;
    }

    void AddDockItems(ContextMenu menu)
    {
        var s = SettingsStore.Current;
        menu.Items.Add(Item("Configurações…", () => App.ShowSettings()));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Adicionar um app…", AddAppFromDialog));
        menu.Items.Add(Item("Adicionar uma pasta…", AddFolderFromDialog));
        menu.Items.Add(Item("Adicionar espaçador pequeno", () => AddItem(new DockItem { Kind = DockItemKind.Spacer })));
        menu.Items.Add(Item("Adicionar espaçador grande", () => AddItem(new DockItem { Kind = DockItemKind.Spacer, LargeSpacer = true })));

        var widgets = new MenuItem { Header = "Adicionar widget" };
        foreach (var kind in Enum.GetValues<WidgetKind>())
        {
            var k = kind;
            widgets.Items.Add(Item(WidgetFactory.Name(k), () => AddItem(new DockItem { Kind = DockItemKind.Widget, Widget = k })));
        }
        menu.Items.Add(widgets);
        menu.Items.Add(new Separator());

        var position = new MenuItem { Header = "Posição na tela" };
        foreach (var (edge, name) in new[] { (DockEdge.Bottom, "Embaixo"), (DockEdge.Left, "À esquerda"), (DockEdge.Right, "À direita"), (DockEdge.Top, "No topo") })
        {
            var e = edge;
            position.Items.Add(Item(name, () => { s.Edge = e; SettingsStore.Save(); }, s.Edge == e));
        }
        menu.Items.Add(position);

        var taskbar = new MenuItem { Header = "Barra de tarefas do Windows" };
        foreach (var (mode, name) in new[] { (TaskbarMode.Keep, "Deixar visível"), (TaskbarMode.AutoHide, "Ocultar automaticamente"), (TaskbarMode.Hide, "Ocultar por completo") })
        {
            var m = mode;
            taskbar.Items.Add(Item(name, () => { s.Taskbar = m; SettingsStore.Save(); }, s.Taskbar == m));
        }
        menu.Items.Add(taskbar);

        menu.Items.Add(Item("Ocultar automaticamente", () => { s.AutoHide = !s.AutoHide; SettingsStore.Save(); }, s.AutoHide));
        menu.Items.Add(Item("Mostrar apps abertos", () => { s.ShowOpenApps = !s.ShowOpenApps; SettingsStore.Save(); }, s.ShowOpenApps));        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Encerrar shft.", () => Application.Current.Shutdown()));
    }

    ContextMenu NewMenu(UIElement target)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = target,
            Placement = SettingsStore.Current.Edge switch
            {
                DockEdge.Bottom => PlacementMode.Top,
                DockEdge.Top => PlacementMode.Bottom,
                DockEdge.Left => PlacementMode.Right,
                _ => PlacementMode.Left,
            },
        };
        if (target == _view) menu.Placement = PlacementMode.MousePoint;
        menu.Opened += (_, _) => _menusOpen++;
        menu.Closed += (_, _) => _menusOpen = Math.Max(0, _menusOpen - 1);
        return menu;
    }

    public static MenuItem Item(string header, Action action, bool isChecked = false)
    {
        var item = new MenuItem { Header = header, IsChecked = isChecked };
        item.Click += (_, _) =>
        {
            try { action(); }
            catch (Exception ex) { Log.Write($"menu '{header}' failed: {ex.Message}"); }
        };
        return item;
    }

    // ---------------------------------------------------------------- editing the Dock

    public void Pin(string path, string? name = null)
    {
        var s = SettingsStore.Current;
        var key = ShortcutResolver.MatchKey(path);
        if (s.Items.Any(i => i.Kind == DockItemKind.App && ShortcutResolver.MatchKey(i.Path) == key)) return;
        AddItem(new DockItem { Kind = DockItemKind.App, Path = path, Name = name });
    }

    static bool IsHidden(string key) =>
        SettingsStore.Current.HiddenApps.Any(h => h.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Stops showing an open app in the Dock; Settings › Itens do Dock can bring it back.</summary>
    void Hide(string key, string name)
    {
        var s = SettingsStore.Current;
        if (!IsHidden(key)) s.HiddenApps.Add(new HiddenApp { Key = key, Name = name });
        SettingsStore.Save();
    }

    public void AddItem(DockItem item)
    {
        var s = SettingsStore.Current;
        if (item.IsTrailing) s.Items.Add(item);
        else
        {
            // After the last app or widget, so a spacer at the end does not end up as a gap before it.
            int anchor = item.Kind == DockItemKind.Spacer
                ? s.Items.FindLastIndex(i => !i.IsTrailing)
                : s.Items.FindLastIndex(i => i.Kind is DockItemKind.App or DockItemKind.Widget);
            s.Items.Insert(anchor + 1, item);
        }
        SettingsStore.Save();
    }

    void RemoveItem(DockItem item)
    {
        SettingsStore.Current.Items.Remove(item);
        SettingsStore.Save();
    }

    void Rename(DockItem item, string current)
    {
        var name = InputDialog.Ask("Renomear", "O nome embaixo deste ícone no Dock. Deixe vazio para usar o do próprio app.", current);
        if (name == null) return;
        item.Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        SettingsStore.Save();
    }

    void AddAppFromDialog()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Escolha um programa ou atalho",
            Filter = "Programas e atalhos|*.exe;*.lnk;*.url;*.appref-ms|Todos os arquivos|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
        };
        if (dlg.ShowDialog() == true) AddPath(dlg.FileName);
    }

    void AddFolderFromDialog()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Escolha uma pasta" };
        if (dlg.ShowDialog() == true) AddPath(dlg.FolderName);
    }

    public void AddPath(string path)
    {
        if (Directory.Exists(path))
        {
            AddItem(new DockItem { Kind = DockItemKind.Folder, Path = path });
            return;
        }
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".exe" or ".lnk" or ".url" or ".appref-ms") Pin(path);
        else AddItem(new DockItem { Kind = DockItemKind.File, Path = path });
    }

    public void HandleDrop(DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            foreach (var p in paths) AddPath(p);
        e.Handled = true;
    }

    void OnTileMoved(DockTile tile, int index)
    {
        if (tile.Item == null) return;
        var s = SettingsStore.Current;
        bool trailing = tile.Item.IsTrailing;
        var sectionItems = s.Items.Where(i => i.IsTrailing == trailing).ToList();
        sectionItems.Remove(tile.Item);
        sectionItems.Insert(Math.Clamp(index, 0, sectionItems.Count), tile.Item);
        var others = s.Items.Where(i => i.IsTrailing != trailing).ToList();
        s.Items = trailing ? others.Concat(sectionItems).ToList() : sectionItems.Concat(others).ToList();
        SettingsStore.Save();
    }

    void OnTileRemoved(DockTile tile)
    {
        if (tile.Item != null) RemoveItem(tile.Item);
    }

    public void Dispose()
    {
        _tracker.Dispose();
        _badges.Dispose();
        foreach (var w in _tiles.Values.Select(t => t.Child).OfType<WidgetBase>()) w.Stop();
    }
}

/// <summary>The grid-of-dots glyph for the app launcher tile.</summary>
sealed class LauncherGlyph : FrameworkElement
{
    protected override void OnRender(DrawingContext dc)
    {
        double s = Math.Min(ActualWidth, ActualHeight);
        if (s <= 0) return;
        var rect = new Rect((ActualWidth - s) / 2, (ActualHeight - s) / 2, s, s);
        var bg = new LinearGradientBrush(Color.FromRgb(0x5B, 0x8C, 0xFF), Color.FromRgb(0x8E, 0x4D, 0xE8), 90);
        dc.DrawRoundedRectangle(bg, null, rect, s * 0.22, s * 0.22);
        var dot = new SolidColorBrush(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF));
        double step = s / 4.2, r = s * 0.065;
        for (int y = 0; y < 3; y++)
            for (int x = 0; x < 3; x++)
                dc.DrawEllipse(dot, null, new Point(rect.Left + s / 2 + (x - 1) * step, rect.Top + s / 2 + (y - 1) * step), r, r);
    }
}
