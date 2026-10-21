using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using ClonaDock.Core;
using Microsoft.Data.Sqlite;

namespace ClonaDock.Native;

/// <summary>
/// Reads the badges apps publish to Windows (the unread count on a Store app's taskbar icon, e.g. WhatsApp)
/// from the notification platform's local database. Only badge rows are read; toast contents are not.
/// The database is copied first so the notification service's own file is never locked.
/// </summary>
public sealed class BadgeWatcher : IDisposable
{
    /// <summary>A glyph badge (no number) such as "newMessage" or "attention".</summary>
    public const int Dot = -1;

    static readonly string Source = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\Notifications\wpndatabase.db");
    static readonly string CopyFolder = Path.Combine(Path.GetTempPath(), "ClonaDock-badges");
    static readonly Regex Value = new("value\\s*=\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);

    readonly DispatcherTimer _timer;
    DateTime _seenDb, _seenWal;
    bool _busy;

    /// <summary>Badge per app, keyed like the Dock's match keys (lower-case shell:AppsFolder entry).</summary>
    public IReadOnlyDictionary<string, int> Counts { get; private set; } = new Dictionary<string, int>();

    public event Action? Changed;

    public BadgeWatcher()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) => Poll();
    }

    public void Start()
    {
        _timer.Start();
        Poll();
    }

    public void Stop()
    {
        _timer.Stop();
        if (Counts.Count > 0)
        {
            Counts = new Dictionary<string, int>();
            Changed?.Invoke();
        }
    }

    async void Poll()
    {
        if (_busy || !File.Exists(Source)) return;
        var db = File.GetLastWriteTimeUtc(Source);
        var wal = File.Exists(Source + "-wal") ? File.GetLastWriteTimeUtc(Source + "-wal") : DateTime.MinValue;
        if (db == _seenDb && wal == _seenWal) return;

        _busy = true;
        try
        {
            var counts = await Task.Run(Read);
            _seenDb = db;
            _seenWal = wal;
            bool same = counts.Count == Counts.Count && counts.All(kv => Counts.TryGetValue(kv.Key, out var v) && v == kv.Value);
            if (!same)
            {
                Counts = counts;
                Log.Write($"badges: {string.Join(", ", counts.Select(kv => $"{kv.Key[AppsFolder.Prefix.Length..]}={kv.Value}"))}");
                Changed?.Invoke();
            }
        }
        catch (Exception ex)
        {
            Log.Write("badges: " + ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    static Dictionary<string, int> Read()
    {
        Directory.CreateDirectory(CopyFolder);
        var copy = Path.Combine(CopyFolder, "wpn.db");
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var from = Source + suffix;
            var to = copy + suffix;
            if (File.Exists(from)) File.Copy(from, to, overwrite: true);
            else if (File.Exists(to)) File.Delete(to);
        }

        var result = new Dictionary<string, int>();
        using (var con = new SqliteConnection($"Data Source={copy};Pooling=False"))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT h.PrimaryId, n.Payload FROM Notification n " +
                              "JOIN NotificationHandler h ON h.RecordId = n.HandlerId WHERE n.Type = 'badge'";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var app = reader.IsDBNull(0) ? null : reader.GetString(0);
                if (string.IsNullOrEmpty(app) || reader.IsDBNull(1)) continue;
                var payload = reader.GetValue(1) is byte[] bytes ? System.Text.Encoding.UTF8.GetString(bytes) : reader.GetValue(1)?.ToString() ?? "";
                var match = Value.Match(payload);
                if (!match.Success) continue;
                var value = match.Groups[1].Value.Trim();
                int count = int.TryParse(value, out var n) ? n : value is "none" or "" ? 0 : Dot;
                if (count != 0) result[AppsFolder.PathFor(app).ToLowerInvariant()] = count;
            }
        }
        return result;
    }

    public void Dispose() => _timer.Stop();
}
