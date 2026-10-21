using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClonaDock.Core;

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON under %APPDATA%\ClonaDock.</summary>
public static class SettingsStore
{
    public static string DataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClonaDock");

    static string FilePath => Path.Combine(DataFolder, "settings.json");

    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Current { get; private set; } = new();

    /// <summary>Raised after <see cref="Save"/> so open windows can re-apply what changed.</summary>
    public static event Action? Changed;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options);
                if (loaded != null)
                {
                    Current = loaded;
                    return Current;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write("settings not read: " + ex.Message);
            TryBackupBrokenFile();
        }

        Current = Defaults();
        Save(notify: false);
        return Current;
    }

    public static void Save(bool notify = true)
    {
        try
        {
            Directory.CreateDirectory(DataFolder);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Options));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Write("settings not saved: " + ex.Message);
        }
        if (notify) Changed?.Invoke();
    }

    static void TryBackupBrokenFile()
    {
        try { File.Copy(FilePath, FilePath + ".broken", overwrite: true); } catch { }
    }

    static AppSettings Defaults()
    {
        var s = new AppSettings();
        void App(string path, string? name = null)
        {
            if (File.Exists(path)) s.Items.Add(new DockItem { Kind = DockItemKind.App, Path = path, Name = name });
        }

        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        App(Path.Combine(win, "explorer.exe"), "Explorador de Arquivos");
        App(Path.Combine(pf86, @"Microsoft\Edge\Application\msedge.exe"), "Microsoft Edge");
        App(Path.Combine(pf, @"Google\Chrome\Application\chrome.exe"), "Google Chrome");
        App(Path.Combine(win, @"System32\notepad.exe"), "Bloco de Notas");
        App(Path.Combine(win, @"System32\Taskmgr.exe"), "Gerenciador de Tarefas");

        s.Items.Add(new DockItem { Kind = DockItemKind.Widget, Widget = WidgetKind.Clock });
        s.Items.Add(new DockItem { Kind = DockItemKind.Widget, Widget = WidgetKind.Calendar });
        s.Items.Add(new DockItem { Kind = DockItemKind.Widget, Widget = WidgetKind.SystemActivity });
        return s;
    }
}
