using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Shft.Bar;

/// <summary>The order of the bar's items. Pure, so it can be tested.</summary>
public static class BarOrder
{
    public static readonly string[] Default = { "memory", "tray", "network", "bluetooth", "volume", "bell", "controls", "clock" };

    /// <summary>
    /// The saved order, ignoring keys that no longer exist. An item the saved order doesn't know yet, from a newer
    /// version, goes right after the item that precedes it by default.
    /// </summary>
    public static List<string> Arrange(IReadOnlyList<string> defaults, IEnumerable<string> saved)
    {
        var result = saved.Where(defaults.Contains).Distinct().ToList();
        for (int i = 0; i < defaults.Count; i++)
        {
            if (result.Contains(defaults[i])) continue;
            result.Insert(i == 0 ? 0 : result.IndexOf(defaults[i - 1]) + 1, defaults[i]);
        }
        return result;
    }

    /// <summary>The default order with the pinned tray icons, which come right before the tray arrow.</summary>
    public static List<string> WithPins(IEnumerable<string> defaults, IEnumerable<string> pins)
    {
        var list = defaults.ToList();
        list.InsertRange(list.IndexOf("tray"), pins);
        return list;
    }

    /// <summary>Where a dragged item lands: how many of the other items' centres lie left of the pointer.</summary>
    public static int Target(IEnumerable<double> centers, double x) => centers.Count(c => c < x);
}

/// <summary>Lets the items of one panel be reordered by dragging. A press that doesn't move stays a normal click.</summary>
public sealed class BarDrag
{
    const double Threshold = 6;

    readonly Panel _host;
    FrameworkElement? _item;
    Point _start;
    bool _active;

    /// <summary>A drag really started: the press will not become a click.</summary>
    public event Action? Started;

    /// <summary>The item was dropped in a new place.</summary>
    public event Action? Moved;

    /// <summary>The drag is over, moved or not.</summary>
    public event Action? Ended;

    public BarDrag(Panel host)
    {
        _host = host;
        TakeMouse = host.CaptureMouse;
        host.MouseMove += (_, e) =>
        {
            if (!_active) return;
            if (e.LeftButton == MouseButtonState.Pressed) Follow(e.GetPosition(_host).X);
            else
            {
                _host.ReleaseMouseCapture();
                End();
            }
        };
        host.MouseLeftButtonUp += (_, _) => host.ReleaseMouseCapture();
        host.LostMouseCapture += (_, e) =>
        {
            // The event bubbles: the button handing its capture to this panel, which starts the drag, arrives here too.
            if (e.OriginalSource == _host) End();
        };
    }

    /// <summary><paramref name="handle"/> is what the pointer grabs; <paramref name="item"/> is the direct child that moves.</summary>
    public void Attach(FrameworkElement handle, FrameworkElement item)
    {
        handle.PreviewMouseLeftButtonDown += (_, e) =>
        {
            End(); // a new press always starts clean
            _item = item;
            _start = e.GetPosition(_host);
        };
        handle.PreviewMouseLeftButtonUp += (_, _) =>
        {
            if (!_active) _item = null;
        };
        handle.PreviewMouseMove += (_, e) =>
        {
            if (_item != item || _active || e.LeftButton != MouseButtonState.Pressed) return;
            if (Math.Abs(e.GetPosition(_host).X - _start.X) < Threshold) return;
            Begin(item);
        };
    }

    internal bool IsDragging => _active;

    internal Func<bool> TakeMouse { get; set; }

    internal void Begin(FrameworkElement item)
    {
        _item = item;
        _active = true;
        _moved = false;
        item.Opacity = 0.55;
        Started?.Invoke();
        // Taking the capture from the button is what cancels its click. A quick flick can release the button first:
        // without the capture nothing would ever report the end, and the bar would stay stuck in a drag.
        if (!TakeMouse()) End();
    }

    bool _moved;

    void Follow(double x)
    {
        if (_item == null) return;
        var others = _host.Children.OfType<FrameworkElement>().Where(c => c != _item).ToList();
        int target = BarOrder.Target(others.Select(c => c.TranslatePoint(new Point(0, 0), _host).X + c.ActualWidth / 2), x);
        if (_host.Children.IndexOf(_item) == target) return;
        _host.Children.Remove(_item);
        _host.Children.Insert(target, _item);
        _moved = true;
    }

    void End()
    {
        if (!_active) return;
        _active = false;
        if (_item != null) _item.Opacity = 1;
        _item = null;
        if (_moved) Moved?.Invoke();
        Ended?.Invoke();
    }
}
