using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Shft.Native;

namespace Shft.Dock;

/// <summary>
/// A borderless window that sits exactly behind the bar and blurs what is under it.
/// It is separate from the Dock window because the Dock window is much larger than the bar
/// (magnified icons and labels need room) and the blur must only cover the bar.
/// </summary>
public sealed class BackdropWindow : Window
{
    IntPtr _hwnd;
    RECT _last;
    int _lastRadius = -1;
    bool _shown;

    public BackdropWindow()
    {
        Title = "shft. backdrop";
        WindowStyle = WindowStyle.None;
        // Not a layered window: Windows ignores the rounded window region on layered windows,
        // which left the blur as a square-cornered rectangle behind the bar.
        AllowsTransparency = false;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Width = 1;
        Height = 1;
        Left = -32000;
        Top = -32000;
    }

    public IntPtr Handle => _hwnd;

    /// <summary>Windows 11 rounds the window (and its blur) with this radius, in device-independent pixels.</summary>
    public const double SystemCornerRadius = 8;

    /// <summary>True on Windows 11, where DWM rounds the blur's corners.</summary>
    public static bool RoundedBySystem { get; private set; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        const long WS_EX_TRANSPARENT = 0x20;
        Win32.AddExStyle(_hwnd, Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE | WS_EX_TRANSPARENT);
        if (HwndSource.FromHwnd(_hwnd)?.CompositionTarget is HwndTarget target)
            target.BackgroundColor = Colors.Transparent;
        // DWM ignores window regions for its blur, but its own corner rounding clips the blur too,
        // with smooth (antialiased) edges. Its radius is fixed, so the bar matches it (SystemCornerRadius).
        int round = 2; // DWMWCP_ROUND
        RoundedBySystem = Win32.DwmSetWindowAttribute(_hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int)) == 0;
        int noBorder = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE: no Windows 11 window outline
        Win32.DwmSetWindowAttribute(_hwnd, Win32.DWMWA_BORDER_COLOR, ref noBorder, sizeof(int));
        SetStyle(Core.SettingsStore.Current.Acrylic, Core.Theme.Resolve(Core.SettingsStore.Current).IsDark);
    }

    (bool Acrylic, bool Dark)? _style;

    /// <summary>Acrylic (Windows' tinted, grainy material) or a plain soft blur with no tint.</summary>
    public void SetStyle(bool acrylic, bool dark)
    {
        if (_hwnd == IntPtr.Zero || _style == (acrylic, dark)) return;
        _style = (acrylic, dark);
        if (acrylic)
        {
            Win32.EnableBlur(_hwnd, false);
            if (!Win32.EnableSystemBackdrop(_hwnd, dark))
                Win32.EnableBlur(_hwnd, true, acrylic: true); // Windows 10
        }
        else
        {
            Win32.DisableSystemBackdrop(_hwnd);
            Win32.EnableBlur(_hwnd, true, acrylic: false);
        }
    }

    /// <summary>Moves the blur to <paramref name="rect"/> (physical pixels) with rounded corners.</summary>
    public void Place(RECT rect, int radius)
    {
        if (_hwnd == IntPtr.Zero) return;
        if (rect.Width <= 2 || rect.Height <= 2)
        {
            Conceal();
            return;
        }
        if (_shown && rect.Equals(_last) && radius == _lastRadius) return;

        bool resized = rect.Width != _last.Width || rect.Height != _last.Height || radius != _lastRadius;
        Win32.SetWindowPos(_hwnd, IntPtr.Zero, rect.Left, rect.Top, rect.Width, rect.Height,
            Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
        if (resized && !RoundedBySystem)
        {
            // Windows 10: a hard-edged region, the best available there.
            var rgn = Win32.CreateRoundRectRgn(0, 0, rect.Width + 1, rect.Height + 1, radius * 2, radius * 2);
            Win32.SetWindowRgn(_hwnd, rgn, true); // the system owns the region now
        }
        _last = rect;
        _lastRadius = radius;
        _shown = true;
    }

    public void Conceal()
    {
        if (!_shown || _hwnd == IntPtr.Zero) return;
        Win32.ShowWindow(_hwnd, Win32.SW_HIDE);
        _shown = false;
    }
}
