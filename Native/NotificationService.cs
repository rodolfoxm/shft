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
        _listener?.ClearNotifications();
        await RefreshAsync();
    }
}
