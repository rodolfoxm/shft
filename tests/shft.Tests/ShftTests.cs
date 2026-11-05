using System.IO;
using System.Text;
using Shft.Bar;
using Shft.Core;
using Shft.Native;
using Shft.Ui;
using Xunit;

namespace Shft.Tests;

public class AdaptiveContrastTests
{
    static double[] Same(double l, int n = 40) => Enumerable.Repeat(l, n).ToArray();

    [Fact] public void White_background_gets_dark_text() => Assert.True(AdaptiveContrast.Decide(Same(1), null).DarkText);
    [Fact] public void Black_background_gets_light_text() => Assert.False(AdaptiveContrast.Decide(Same(0), null).DarkText);

    [Fact]
    public void Plain_backgrounds_need_no_scrim()
    {
        Assert.False(AdaptiveContrast.Decide(Same(0), null).Scrim);
        Assert.False(AdaptiveContrast.Decide(Same(1), null).Scrim);
    }

    [Fact]
    public void Half_black_half_white_asks_for_the_scrim()
    {
        var mixed = Same(0, 20).Concat(Same(1, 20)).ToArray();
        Assert.True(AdaptiveContrast.Decide(mixed, null).Scrim);
    }

    [Fact]
    public void Near_the_tie_the_current_colour_stays()
    {
        const double tie = 0.179; // equal contrast with black and white
        Assert.True(AdaptiveContrast.Decide(Same(tie - 0.01), true).DarkText);
        Assert.False(AdaptiveContrast.Decide(Same(tie + 0.01), false).DarkText);
    }

    [Fact] public void Empty_sample_is_safe() => Assert.True(AdaptiveContrast.Decide(Array.Empty<double>(), null).Scrim);

    [Fact]
    public void Luminance_endpoints()
    {
        Assert.Equal(0, AdaptiveContrast.Luminance(0, 0, 0));
        Assert.Equal(1, AdaptiveContrast.Luminance(255, 255, 255), 3);
    }
}

public class AppSearchTests
{
    static readonly AppEntry Code = new("Visual Studio Code", "a"), Chrome = new("Google Chrome", "b"), Calc = new("Calculadora", "c");

    [Fact]
    public void Prefix_beats_word_start_beats_substring_beats_scattered()
    {
        Assert.True(AppSearch.Score("Chrome", "chr") > AppSearch.Score("Google Chrome", "chr"));
        Assert.True(AppSearch.Score("Google Chrome", "chr") > AppSearch.Score("Synchronize", "chr"));
        Assert.True(AppSearch.Score("Synchronize", "chr") > AppSearch.Score("Calculadora Home Remote", "chr"));
    }

    [Fact] public void No_match_scores_zero() => Assert.Equal(0, AppSearch.Score("Chrome", "xyz"));
    [Fact] public void Search_ignores_case() => Assert.Equal(1000, AppSearch.Score("CHROME", "chrome"));

    [Fact]
    public void Recent_app_wins_a_tie()
    {
        var ranked = AppSearch.Rank(new[] { Calc, new AppEntry("Calendário", "d") }, "cal", new[] { "d" }, Array.Empty<string>());
        Assert.Equal("d", ranked[0].Path);
    }

    [Fact]
    public void A_better_match_outranks_recency()
    {
        var ranked = AppSearch.Rank(new[] { Chrome, new AppEntry("Chrome", "x") }, "chrome", new[] { "b" }, Array.Empty<string>());
        Assert.Equal("x", ranked[0].Path);
    }

    [Fact] public void Non_matches_are_left_out() => Assert.Single(AppSearch.Rank(new[] { Code, Chrome }, "visual", Array.Empty<string>(), Array.Empty<string>()));

    [Fact] public void Url_runs_directly() => Assert.Equal("https://a.com", AppSearch.Direct("https://a.com")!.Path);

    [Fact]
    public void Program_on_path_runs_directly() =>
        Assert.Equal(@"C:\t\cmd.exe", AppSearch.Direct("cmd", f => f == @"C:\t\cmd.exe", @"C:\x;C:\t")!.Path);

    [Fact] public void Unknown_word_is_not_a_program() => Assert.Null(AppSearch.Direct("banana", _ => false, @"C:\t"));
    [Fact] public void Bad_characters_are_refused() => Assert.Null(AppSearch.Direct("a<b|c", _ => true, @"C:\t"));
}

public class BarOrderTests
{
    static readonly string[] Defaults = { "a", "b", "c", "d" };

    [Fact] public void Empty_is_the_default_order() => Assert.Equal(Defaults, BarOrder.Arrange(Defaults, Array.Empty<string>()));
    [Fact] public void Saved_order_wins() => Assert.Equal(new[] { "d", "a", "c", "b" }, BarOrder.Arrange(Defaults, new[] { "d", "a", "c", "b" }));
    [Fact] public void Unknown_keys_are_dropped() => Assert.Equal(Defaults, BarOrder.Arrange(Defaults, new[] { "a", "old", "b", "c", "d" }));
    [Fact] public void Repeated_keys_count_once() => Assert.Equal(Defaults, BarOrder.Arrange(Defaults, new[] { "a", "a", "b", "c", "d" }));
    [Fact] public void A_new_item_lands_after_its_default_neighbour() => Assert.Equal(new[] { "d", "a", "b", "c" }, BarOrder.Arrange(Defaults, new[] { "d", "a", "c" }));
    [Fact] public void A_new_first_item_lands_first() => Assert.Equal(new[] { "a", "c", "b", "d" }, BarOrder.Arrange(Defaults, new[] { "c", "b", "d" }));
    [Fact] public void Every_default_key_is_kept() => Assert.Equal(BarOrder.Default.Length, BarOrder.Arrange(BarOrder.Default, new[] { "clock" }).Count);

    [Fact] public void Pointer_left_of_everything_is_first() => Assert.Equal(0, BarOrder.Target(new double[] { 10, 30, 50 }, 5));
    [Fact] public void Pointer_between_centres() => Assert.Equal(2, BarOrder.Target(new double[] { 10, 30, 50 }, 40));
    [Fact] public void Pointer_right_of_everything_is_last() => Assert.Equal(3, BarOrder.Target(new double[] { 10, 30, 50 }, 99));
}

public class WinTapTests
{
    const int Win = WinTap.LeftWin, E = 0x45;

    static bool Tap(WinTap t, params (int Key, bool Down)[] events) => events.Select(e => t.Key(e.Key, e.Down)).ToArray().Last();

    [Fact] public void Alone_is_a_tap() => Assert.True(Tap(new WinTap(), (Win, true), (Win, false)));
    [Fact] public void Right_key_too() => Assert.True(Tap(new WinTap(), (WinTap.RightWin, true), (WinTap.RightWin, false)));
    [Fact] public void Holding_with_auto_repeat_is_still_a_tap() => Assert.True(Tap(new WinTap(), (Win, true), (Win, true), (Win, true), (Win, false)));
    [Fact] public void A_shortcut_is_not_a_tap() => Assert.False(Tap(new WinTap(), (Win, true), (E, true), (E, false), (Win, false)));
    [Fact] public void Auto_repeat_keeps_the_shortcut() => Assert.False(Tap(new WinTap(), (Win, true), (E, true), (Win, true), (Win, false)));
    [Fact] public void Other_keys_alone_do_nothing() => Assert.False(Tap(new WinTap(), (E, true), (E, false)));
    [Fact] public void A_key_released_after_win_went_down_does_not_block_the_tap() => Assert.True(Tap(new WinTap(), (Win, true), (E, false), (Win, false)));
    [Fact] public void Next_press_starts_clean() => Assert.True(Tap(new WinTap(), (Win, true), (E, true), (Win, false), (Win, true), (Win, false)));
    [Fact] public void Release_without_press_is_ignored() => Assert.False(Tap(new WinTap(), (Win, false)));
}

public class HotkeyTests
{
    [Fact] public void Parses_alt_space() => Assert.Equal((1u, 0x20u), GlobalHotkey.Parse("Alt+Space"));
    [Fact] public void Parses_any_order() => Assert.Equal((6u, 0x4Bu), GlobalHotkey.Parse("Shift+Ctrl+K"));
    [Fact] public void Needs_a_modifier() => Assert.Null(GlobalHotkey.Parse("K"));
    [Fact] public void Needs_a_key() => Assert.Null(GlobalHotkey.Parse("Ctrl+Alt"));
    [Fact] public void Rejects_nonsense() => Assert.Null(GlobalHotkey.Parse("Ctrl+Banana"));
    [Fact] public void Empty_is_no_hotkey() => Assert.Null(GlobalHotkey.Parse(""));
}

public class NightLightBlobTests
{
    // The real blob read from this PC (off).
    static readonly byte[] Off = Convert.FromHexString("434201000A0201002A068C9882D5062A2B0E1343420100D00A02C6148FA48BFA95FCCFEE0100000000");

    [Fact] public void Reads_off() => Assert.False(DisplayService.NightLightOn(Off));

    [Fact]
    public void On_then_off_restores_everything_but_the_timestamp()
    {
        var on = DisplayService.ToggleNightLight(Off, true)!;
        Assert.True(DisplayService.NightLightOn(on));
        Assert.Equal(Off.Length + 2, on.Length);
        Assert.Equal(new byte[] { 0x10, 0x00 }, on[23..25]);
        var back = DisplayService.ToggleNightLight(on, false)!;
        Assert.False(DisplayService.NightLightOn(back));
        Assert.Equal(Off.Length, back.Length);
        Assert.Equal(Off[15..], back[15..]);
    }

    [Fact] public void Same_state_changes_nothing() => Assert.Same(Off, DisplayService.ToggleNightLight(Off, false));

    [Fact]
    public void Unknown_format_is_never_written()
    {
        var other = (byte[])Off.Clone();
        other[18] = 0x99;
        Assert.Null(DisplayService.NightLightOn(other));
        Assert.Null(DisplayService.ToggleNightLight(other, true));
    }

    [Fact] public void Short_blob_is_unknown() => Assert.Null(DisplayService.NightLightOn(new byte[4]));
}

public class TrayMessageTests
{
    static byte[] Payload(uint action, uint flags, int hwnd, uint id, uint callback, int icon, string tip, uint state = 0, uint mask = 0, int size = 964)
    {
        var p = new byte[size];
        void U(int at, uint v) => BitConverter.TryWriteBytes(p.AsSpan(at), v);
        U(0, 0x34753423);
        U(4, action);
        const int nid = 8;
        U(nid + 4, (uint)hwnd);
        U(nid + 8, id);
        U(nid + 12, flags);
        U(nid + 16, callback);
        U(nid + 20, (uint)icon);
        Encoding.Unicode.GetBytes(tip).CopyTo(p, nid + 24);
        U(nid + 280, state);
        U(nid + 284, mask);
        return p;
    }

    [Fact]
    public void Reads_an_add()
    {
        var m = TrayHost.Parse(Payload(0, 1 | 2 | 4, 0x1234, 7, 0x8001, 0x5678, "Olá"))!.Value;
        Assert.Equal(0u, m.Action);
        Assert.Equal(new IntPtr(0x1234), m.Owner);
        Assert.Equal(7u, m.Id);
        Assert.Equal(0x8001u, m.Callback);
        Assert.Equal(new IntPtr(0x5678), m.Icon);
        Assert.Equal("Olá", m.Tip);
    }

    [Fact]
    public void Reads_the_hidden_state()
    {
        var m = TrayHost.Parse(Payload(1, 8, 1, 1, 0, 0, "", state: 1, mask: 1))!.Value;
        Assert.Equal(1u, m.State & m.StateMask);
    }

    [Fact] public void Older_short_messages_still_parse() => Assert.NotNull(TrayHost.Parse(Payload(0, 1, 1, 1, 1, 0, "", size: TrayHost.MinPayload)));

    [Fact]
    public void Wrong_signature_is_ignored()
    {
        var p = Payload(0, 0, 1, 1, 0, 0, "");
        p[0] = 0;
        Assert.Null(TrayHost.Parse(p));
    }

    [Fact] public void Truncated_message_is_ignored() => Assert.Null(TrayHost.Parse(new byte[20]));
}

public class GlassTests
{
    [Fact]
    public void Blur_keeps_a_flat_colour_flat()
    {
        var px = Enumerable.Repeat((byte)80, 16 * 16 * 4).ToArray();
        ScreenSampler.BoxBlur(px, 16, 16, 2);
        Assert.All(px, b => Assert.InRange(b, (byte)79, (byte)80));
    }

    [Fact]
    public void Blur_softens_an_edge()
    {
        var px = new byte[16 * 4 * 4];
        for (int y = 0; y < 4; y++)
            for (int x = 8; x < 16; x++)
                px[(y * 16 + x) * 4] = 255;
        ScreenSampler.BoxBlur(px, 16, 4, 2);
        Assert.InRange(px[7 * 4], (byte)60, (byte)200);
        Assert.Equal(0, px[0]);
    }

    [Fact]
    public void Saturate_leaves_grey_alone()
    {
        var px = new byte[] { 90, 90, 90, 255 };
        ScreenSampler.Saturate(px, 1.5);
        Assert.Equal(new byte[] { 90, 90, 90, 255 }, px);
    }
}

public class BarTests
{
    [Fact] public void Day_is_capitalised_and_loses_its_dots() => Assert.Equal("Qua 4 nov, 20:47", MenuBarWindow.CapitalizeDay("qua. 4 nov., 20:47"));
    [Fact] public void Empty_clock_is_safe() => Assert.Equal("", MenuBarWindow.CapitalizeDay(""));
    [Fact] public void Spring_ends_at_one() => Assert.Equal(1, new SpringEase().Ease(1));

    [Fact]
    public void Spring_overshoots_only_slightly() =>
        Assert.All(Enumerable.Range(0, 100), i => Assert.InRange(new SpringEase().Ease(i / 100.0), 0, 1.2));
}

public class Setup
{
    /// <summary>Runs before anything touches SettingsStore, so tests never see the real data folder.</summary>
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void IsolateData() => Environment.SetEnvironmentVariable("SHFT_DATA", Directory.CreateTempSubdirectory("shft-tests").FullName);
}

public class ClockTests
{
    static readonly System.Globalization.CultureInfo Br = new("pt-BR");

    [Fact]
    public void Twelve_hour_clock_keeps_am_pm_in_portuguese() =>
        Assert.Equal("Qua 4 nov, 08:47 PM", MenuBarWindow.FormatClock(new DateTime(2026, 11, 4, 20, 47, 0), "ddd d MMM, hh:mm tt", Br));

    [Fact]
    public void Default_clock_is_24_hours() =>
        Assert.Equal("Qua 4 nov, 20:47", MenuBarWindow.FormatClock(new DateTime(2026, 11, 4, 20, 47, 0), "ddd d MMM, HH:mm", Br));
}

public class MigrationTests
{
    [Fact]
    public void Dock_settings_and_logo_come_over_and_the_originals_stay()
    {
        var old = Directory.CreateTempSubdirectory("shft-old").FullName;
        var logo = Path.Combine(old, "logo-639282194184608401.png");
        File.WriteAllBytes(logo, new byte[] { 1, 2, 3 });
        var settings = Path.Combine(old, "settings.json");
        File.WriteAllText(settings, "{\"IconSize\": 36, \"Taskbar\": \"Hide\", \"LiquidGlass\": true, \"Items\": [{\"Kind\": \"App\", \"Path\": \"C:\\\\x.exe\"}]}");
        var state = Path.Combine(old, "state.yml");
        File.WriteAllText(state, "template: |-\n  return Button({ content: [ Image({ path: \"" + logo.Replace("\\", "\\\\") + "\" }), ] });\n");

        SettingsStore.Migrate(settings, state);
        var s = SettingsStore.Load();

        Assert.Equal(36, s.IconSize);
        Assert.Equal(TaskbarMode.Hide, s.Taskbar);
        Assert.True(s.LiquidGlass);
        Assert.Single(s.Items);
        Assert.Equal(MotionMode.Full, s.Motion);
        Assert.True(File.Exists(s.TopBarLogo));
        Assert.StartsWith(SettingsStore.DataFolder, s.TopBarLogo);
        Assert.True(File.Exists(settings) && File.Exists(logo));

        // A second run must not overwrite what the user changed since.
        s.IconSize = 50;
        SettingsStore.Save(notify: false);
        SettingsStore.Migrate(settings, state);
        Assert.Equal(50, SettingsStore.Load().IconSize);
    }
}

public class LogoTests
{
    [Fact]
    public void Picture_is_cropped_to_a_square()
    {
        var dir = Directory.CreateTempSubdirectory("shft-logo").FullName;
        var source = Path.Combine(dir, "wide.png");
        var pixels = new byte[300 * 100 * 4];
        var bmp = System.Windows.Media.Imaging.BitmapSource.Create(300, 100, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, 300 * 4);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using (var f = File.Create(source)) encoder.Save(f);

        var target = Path.Combine(dir, "out.png");
        LogoPicker.Save(source, target);

        using var read = File.OpenRead(target);
        var frame = System.Windows.Media.Imaging.BitmapFrame.Create(read, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        Assert.Equal(128, frame.PixelWidth);
        Assert.Equal(128, frame.PixelHeight);
    }
}
