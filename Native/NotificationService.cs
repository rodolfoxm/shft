using System.Windows.Threading;
using Shft.Core;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace Shft.Native;

public sealed record NotificationEntry(uint Id, string App, string Title, string Body, DateTimeOffset When);

/// <summary>Windows' notification list, read and cleared through its own listener.</summary>
public sealed class NotificationService
{
    public static NotificationService Instance { get; } = new();

    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(8) };
    UserNotificationListener? _listener;
    bool _busy, _polling;

    public IReadOnlyList<NotificationEntry> Items { get; private set; } = Array.Empty<NotificationEntry>();
    public bool Available => _listener != null;
    public event Action? Changed;

    public async Task StartAsync()
    {
        try
        {
            var listener = UserNotificationListener.Current;
            if (await listener.RequestAccessAsync() != UserNotificationListenerAccessStatus.Allowed)
            {
                Log.Write("notifications: access denied");
                return;
            }
            _listener = listener;
            try
            {
                listener.NotificationChanged += (_, _) => _timer.Dispatcher.BeginInvoke(() => _ = RefreshAsync());
            }
            catch (Exception ex)
            {
                // Unpackaged apps may not get the event; a slow timer keeps the bell honest.
                Log.Write("notifications: no change event, polling: " + ex.Message);
                _timer.Tick += (_, _) => _ = RefreshAsync();
                _polling = true;
                _timer.Start();
            }
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Log.Write("notifications: " + ex.Message);
        }
    }

    public void Pause(bool paused)
    {
        if (!_polling) return;
        if (paused) _timer.Stop();
        else _timer.Start();
    }

    public async Task RefreshAsync()
    {
        if (_listener == null || _busy) return;
        _busy = true;
        try
        {
            var read = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            var list = new List<NotificationEntry>();
            foreach (var n in read)
            {
                var binding = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
                var text = binding?.GetTextElements().Select(t => t.Text).ToList() ?? new List<string>();
                string app;
                try { app = n.AppInfo?.DisplayInfo?.DisplayName ?? "App"; }
                catch (Exception) { app = "App"; } // some Win32 senders have no display info
                list.Add(new NotificationEntry(n.Id, app, text.FirstOrDefault() ?? "", string.Join("\n", text.Skip(1)), n.CreationTime));
            }
            Items = list.OrderByDescending(n => n.When).ToList();
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Write("notifications not read: " + ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    public async Task RemoveAsync(uint id)
    {
        _listener?.RemoveNotification(id);
        await RefreshAsync();
    }

    public async Task ClearAsync()
    {
        // ClearNotifications() left the list as it was here; one by one is what the dismiss button does, and that works.
        foreach (var n in Items)
        {
            try { _listener?.RemoveNotification(n.Id); }
            catch (Exception ex) { Log.Write($"notification {n.Id} not removed: " + ex.Message); }
        }
        Items = Array.Empty<NotificationEntry>();
        Changed?.Invoke();
        await RefreshAsync();
    }

    /// <summary>Does what clicking the toast does: opens where it points, then dismisses it.</summary>
    public async Task OpenAsync(uint id)
    {
        string? target = null;
        try { target = await Task.Run(() => TargetOf(id)); }
        catch (Exception ex) { Log.Write("notification target not read: " + ex.Message); }
        Log.Write($"notification {id} opens {target ?? "nothing"}");
        if (target != null) Dock.DockController.Open(target);
        await RemoveAsync(id);
    }

    // The listener API hides the toast's launch target; Windows' own database has the toast XML.
    internal static string? TargetOf(uint id)
    {
        var copy = BadgeWatcher.CopyDatabase("toast");
        using var con = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={copy};Pooling=False");
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT h.PrimaryId, n.Payload FROM Notification n JOIN NotificationHandler h ON h.RecordId = n.HandlerId WHERE n.Id = $id";
        cmd.Parameters.AddWithValue("$id", (long)id);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        var app = reader.IsDBNull(0) ? null : reader.GetString(0);
        var payload = reader.GetValue(1) is byte[] bytes ? System.Text.Encoding.UTF8.GetString(bytes) : reader.GetValue(1)?.ToString() ?? "";
        return Target(app, payload);
    }

    /// <summary>A protocol toast opens its launch URI; any other opens its app. System senders have no app to open.</summary>
    public static string? Target(string? app, string payload)
    {
        var toast = System.Text.RegularExpressions.Regex.Match(payload, "<toast\\b[^>]*>", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Value;
        string Attr(string name) => System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Match(toast, "\\b" + name + "\\s*=\\s*\"([^\"]*)\"").Groups[1].Value);
        var launch = Attr("launch");
        if (Attr("activationType").Equals("protocol", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(launch, UriKind.Absolute, out var uri) && !uri.IsFile && !uri.IsUnc)
            return launch;
        if (string.IsNullOrEmpty(app) || app.StartsWith("Windows.SystemToast.", StringComparison.OrdinalIgnoreCase)) return null;
        return AppsFolder.PathFor(app);
    }
}
