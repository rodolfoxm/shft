using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClonaDock.Dock;
using ClonaDock.Native;

namespace ClonaDock.Ui;

/// <summary>A folder in the Dock opens as a grid of its newest items.</summary>
public sealed class StackFlyout : FlyoutWindow
{
    const int MaxItems = 24;

    public StackFlyout(string folder, string title)
    {
        var root = new StackPanel { Width = 420 };
        root.Children.Add(Heading(title));

        List<FileSystemInfo> entries;
        try
        {
            entries = new DirectoryInfo(folder).EnumerateFileSystemInfos()
                .Where(i => !i.Attributes.HasFlag(FileAttributes.Hidden) && !i.Attributes.HasFlag(FileAttributes.System))
                .OrderByDescending(i => i.LastWriteTimeUtc)
                .Take(MaxItems)
                .ToList();
        }
        catch (Exception ex)
        {
            entries = new List<FileSystemInfo>();
            root.Children.Add(Subtle("Não foi possível ler a pasta: " + ex.Message));
        }

        if (entries.Count == 0 && root.Children.Count == 1)
            root.Children.Add(Subtle("Nada aqui ainda"));

        var grid = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        foreach (var entry in entries)
        {
            var path = entry.FullName;
            var cell = new StackPanel { Width = 92 };
            cell.Children.Add(new Image { Source = IconLoader.Get(path, 64, iconOnly: false, cache: false), Width = 48, Height = 48, Margin = new Thickness(0, 2, 0, 4) });
            cell.Children.Add(new TextBlock
            {
                Text = entry.Name,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 34,
                FontSize = 11.5,
            });
            var button = FlatButton(cell, () =>
            {
                SafeClose();
                DockController.Open(path);
            }, 4);
            button.HorizontalContentAlignment = HorizontalAlignment.Center;
            button.ToolTip = entry.Name;
            var menu = new ContextMenu();
            menu.Items.Add(DockController.Item("Abrir", () => DockController.Open(path)));
            menu.Items.Add(DockController.Item("Mostrar na pasta", () => DockController.ShowInExplorer(path)));
            button.ContextMenu = menu;
            grid.Children.Add(button);
        }
        root.Children.Add(grid);

        var open = FlatButton(new TextBlock { Text = "Abrir no Explorador de Arquivos  ›" }, () =>
        {
            SafeClose();
            DockController.Open(folder);
        });
        open.HorizontalAlignment = HorizontalAlignment.Right;
        root.Children.Add(open);
        Body = root;
    }
}
