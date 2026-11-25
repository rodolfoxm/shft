using System.Windows;
using Shft.Native.Glass;
using Xunit;

namespace Shft.Tests;

public class MagnifierHostTests
{
    [Fact]
    public void Filter_list_works_for_two_controls()
    {
        if (!Environment.UserInteractive) return; // needs an interactive desktop
        if (Environment.GetEnvironmentVariable("SHFT_DESKTOP_TESTS") != "1") return; // opens windows on the desktop: opt-in (AGENTS.md: tests are pure logic)
        IntPtr h1 = default, h2 = default;
        try
        {
            MagnifierHost.Invoke(() =>
            {
                // created at once on the magnifier thread: each has its own filter list (true or it throws)
                (h1, _) = MagnifierHost.CreateMagnifier(new Int32Rect(0, 0, 64, 32), Array.Empty<IntPtr>());
                (h2, _) = MagnifierHost.CreateMagnifier(new Int32Rect(100, 0, 64, 32), new[] { h1 });
            });
            Assert.True(MagnifierHost.Available);
            Assert.NotEqual(IntPtr.Zero, h1);
            Assert.NotEqual(IntPtr.Zero, h2);
        }
        finally
        {
            MagnifierHost.Invoke(() => { if (h1 != IntPtr.Zero) MagnifierHost.DestroyMagnifier(h1); if (h2 != IntPtr.Zero) MagnifierHost.DestroyMagnifier(h2); });
        }
    }
}

static class GlassNativeForTests
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
    public static void Destroy(IntPtr h) { if (h != IntPtr.Zero) DestroyWindow(h); }
}
