using System.IO;

namespace ClonaDock.Core;

/// <summary>Small rolling log in the data folder; diagnostics only.</summary>
public static class Log
{
    static readonly object Gate = new();

    static string FilePath => Path.Combine(SettingsStore.DataFolder, "clonadock.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(SettingsStore.DataFolder);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 512 * 1024)
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the Dock down.
        }
    }
}
