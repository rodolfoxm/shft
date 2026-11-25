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

public class BarDragTests
{
    static void Sta(Action body)
    {
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (failure != null) throw failure;
    }

    static void LoseCapture(System.Windows.UIElement element) =>
        element.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0) { RoutedEvent = System.Windows.Input.Mouse.LostMouseCaptureEvent });

    [Fact]
    public void The_button_losing_the_mouse_to_the_bar_does_not_end_the_drag() => Sta(() =>
    {
        var host = new System.Windows.Controls.StackPanel();
        var button = new System.Windows.Controls.Button();
        host.Children.Add(button);
        var drag = new BarDrag(host) { TakeMouse = () => true };

        drag.Begin(button);
        LoseCapture(button);
        Assert.True(drag.IsDragging);

        LoseCapture(host);
        Assert.False(drag.IsDragging);
        Assert.Equal(1, button.Opacity);
    });
}

public class BarDragStuckTests
{
    [Fact]
    public void A_drag_that_cannot_take_the_mouse_never_stays_on()
    {
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try
            {
                var host = new System.Windows.Controls.StackPanel();
                var button = new System.Windows.Controls.Button();
                host.Children.Add(button);
                var drag = new BarDrag(host) { TakeMouse = () => false };
                drag.Begin(button);
                Assert.False(drag.IsDragging);
                Assert.Equal(1, button.Opacity);
            }
            catch (Exception ex) { failure = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (failure != null) throw failure;
    }

    [Fact]
    public void Tray_icons_join_the_bar_before_the_arrow() =>
        Assert.Equal(new[] { "memory", "pin:a", "pin:b", "tray", "network" }, BarOrder.WithPins(new[] { "memory", "tray", "network" }, new[] { "pin:a", "pin:b" }));

    [Fact]
    public void A_tray_icon_new_to_the_saved_order_stays_by_the_memory()
    {
        var defaults = BarOrder.WithPins(BarOrder.Default, new[] { "pin:a" });
        Assert.Equal(new[] { "clock", "memory", "pin:a", "tray" }, BarOrder.Arrange(defaults, new[] { "clock", "memory", "tray" }).Take(4));
    }
}

public class StartupTaskTests
{
    [Fact]
    public void Task_is_valid_xml_and_escapes_paths()
    {
        var xml = StartWithWindows.TaskXml(@"PC\Ana & Bia", @"C:\Apps <x>\shft.exe");
        var doc = System.Xml.Linq.XDocument.Parse(xml);
        System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        Assert.Equal(@"C:\Apps <x>\shft.exe", doc.Descendants(ns + "Command").Single().Value);
        Assert.Equal("--startup", doc.Descendants(ns + "Arguments").Single().Value);
        Assert.Equal(@"PC\Ana & Bia", doc.Descendants(ns + "LogonTrigger").Single().Element(ns + "UserId")!.Value);
    }

    [Fact]
    public void Task_runs_at_normal_priority_without_elevation()
    {
        var doc = System.Xml.Linq.XDocument.Parse(StartWithWindows.TaskXml("u", "x.exe"));
        System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        Assert.Equal("4", doc.Descendants(ns + "Priority").Single().Value);
        Assert.Equal("LeastPrivilege", doc.Descendants(ns + "RunLevel").Single().Value);
        Assert.Equal("PT0S", doc.Descendants(ns + "ExecutionTimeLimit").Single().Value);
    }
}

public class OverlayTests
{
    static readonly RECT Screen = new(0, 0, 1920, 1080);

    [Fact] public void Steam_popup_over_a_game_is_an_overlay() => Assert.True(Monitors.IsOverlay("SDL_app", new RECT(1338, 125, 1725, 910), Screen));
    [Fact] public void Task_switcher_is_an_overlay() => Assert.True(Monitors.IsOverlay("XamlExplorerHostIslandWindow", new RECT(0, 32, 1920, 1080), Screen));
    [Fact] public void A_maximized_app_is_a_real_switch() => Assert.False(Monitors.IsOverlay("Chrome_WidgetWin_1", new RECT(-8, 24, 1928, 1088), Screen));
    [Fact] public void Big_window_is_a_real_switch() => Assert.False(Monitors.IsOverlay("Notepad", new RECT(0, 0, 1600, 900), Screen));
    [Fact] public void Empty_monitor_is_safe() => Assert.False(Monitors.IsOverlay("x", new RECT(0, 0, 10, 10), new RECT(0, 0, 0, 0)));
}

public class DeviceBatteryTests
{
    // The real reply of this PC's Arctis Nova Pro base (12E5) with the headset off.
    static readonly byte[] ArctisOff = Convert.FromHexString("06B00000010000080700020A0400040100000000");

    [Fact]
    public void Razer_request_has_the_layout_and_checksum()
    {
        var r = Razer.Request(0x1F, 0x07, 0x80, 0x02);
        Assert.Equal(91, r.Length);
        Assert.Equal(0x1F, r[2]);
        Assert.Equal(0x02, r[6]);
        Assert.Equal(0x07, r[7]);
        Assert.Equal(0x80, r[8]);
        Assert.Equal(0x02 ^ 0x07 ^ 0x80, r[89]);
    }

    static byte[] Reply(byte status, byte level)
    {
        var r = Razer.Request(0x1F, 0x07, 0x80, 0x02);
        r[1] = status;
        r[10] = level;
        byte crc = 0;
        for (int i = 3; i < 89; i++) crc ^= r[i];
        r[89] = crc;
        return r;
    }

    [Fact]
    public void Razer_ok_reply_gives_the_level()
    {
        Assert.Equal(Razer.Status.Ok, Razer.Parse(Reply(0x02, 212), 0x1F, 0x07, 0x80, 0x02, out var args));
        Assert.Equal(83, Razer.Percent(args[1]));
    }

    [Fact] public void Razer_full_and_empty() => Assert.Equal((100, 0), (Razer.Percent(255), Razer.Percent(0)));
    [Fact] public void Razer_busy_and_offline() => Assert.Equal((Razer.Status.Busy, Razer.Status.Offline), (Razer.Parse(Reply(0x01, 0), 0x1F, 0x07, 0x80, 0x02, out _), Razer.Parse(Reply(0x04, 0), 0x1F, 0x07, 0x80, 0x02, out _)));

    [Fact]
    public void Razer_reply_to_another_program_is_ignored()
    {
        var r = Reply(0x02, 200);
        r[2] = 0x3F; // Synapse's transaction
        Assert.Equal(Razer.Status.Mismatch, Razer.Parse(r, 0x1F, 0x07, 0x80, 0x02, out _));
    }

    [Fact]
    public void Razer_corrupt_reply_is_ignored()
    {
        var r = Reply(0x02, 200);
        r[50] ^= 0xFF;
        Assert.Equal(Razer.Status.Mismatch, Razer.Parse(r, 0x1F, 0x07, 0x80, 0x02, out _));
    }

    [Fact] public void Razer_names_lose_the_receiver() => Assert.Equal("Razer Viper V4 Pro", Razer.Clean("Razer Viper V4 Pro HyperSpeed Dongle"));
    [Fact] public void Razer_kinds() => Assert.Equal((DeviceKind.Headset, DeviceKind.Keyboard, DeviceKind.Mouse), (Razer.KindOf("Razer BlackShark V2 Pro"), Razer.KindOf("Razer Huntsman V3"), Razer.KindOf("Razer Viper V4 Pro")));

    [Fact] public void Arctis_off() => Assert.Null(NovaPro.Parse(ArctisOff, 0x12E5)!.Percent);

    [Fact]
    public void Arctis_level_and_charging()
    {
        var on = (byte[])ArctisOff.Clone();
        on[6] = 6;
        on[15] = 0x02;
        var b = NovaPro.Parse(on, 0x12E5)!;
        Assert.Equal(75, b.Percent);
        Assert.True(b.Charging);
    }

    [Fact] public void Arctis_short_reply_is_nothing() => Assert.Null(NovaPro.Parse(new byte[8], 0x12E5));

    [Fact]
    public void A_sleeping_device_keeps_its_last_level()
    {
        var before = new List<DeviceBattery> { new("m", "Mouse", DeviceKind.Mouse, 83, true) };
        var now = DeviceBatteries.Merge(before, new List<DeviceBattery> { new("m", "Mouse", DeviceKind.Mouse, null, false) });
        Assert.Equal(83, now.Single().Percent);
        Assert.True(now.Single().Asleep);
        Assert.False(now.Single().Charging);
    }

    [Fact] public void A_device_never_seen_awake_is_not_shown() =>
        Assert.Empty(DeviceBatteries.Merge(Array.Empty<DeviceBattery>(), new List<DeviceBattery> { new("h", "Fone", DeviceKind.Headset, null, false) }));
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

    static uint VersionFrom(uint flags, uint versionField, uint current = 0)
    {
        var p = Payload(1, flags, 1, 1, 0, 0, "");
        BitConverter.TryWriteBytes(p.AsSpan(8 + 800), versionField);
        return TrayHost.VersionAfter(current, TrayHost.Parse(p)!.Value);
    }

    [Fact] public void Qt_icon_refresh_reveals_version_4() => Assert.Equal(4u, VersionFrom(0x80 | 2, 4));
    [Fact] public void Show_tip_alone_reveals_version_4() => Assert.Equal(4u, VersionFrom(0x80, 0));
    [Fact] public void An_old_icon_stays_old() => Assert.Equal(0u, VersionFrom(2 | 4, 0));
    [Fact] public void A_balloon_timeout_is_not_a_version() => Assert.Equal(0u, VersionFrom(0x10, 4));
    [Fact] public void A_known_version_is_kept() => Assert.Equal(3u, VersionFrom(2, 0, current: 3));

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

public class NotificationTargetTests
{
    [Fact]
    public void ProtocolToastOpensItsUri() =>
        Assert.Equal("windowsdefender://providers", NotificationService.Target("Windows.Defender.SecurityCenter",
            "<toast duration=\"short\" launch=\"windowsdefender://providers\" activationType=\"protocol\"><visual/></toast>"));

    [Fact]
    public void ForegroundToastOpensItsApp() =>
        Assert.Equal(AppsFolder.PathFor("Microsoft.WindowsTerminal_8wekyb3d8bbwe!App"),
            NotificationService.Target("Microsoft.WindowsTerminal_8wekyb3d8bbwe!App", "<toast launch=\"action=x&amp;id=1\"><visual/></toast>"));

    [Fact]
    public void EscapedUriIsDecoded() =>
        Assert.Equal("https://a.example/?x=1&y=2", NotificationService.Target("App", "<toast activationType='x' launch=\"https://a.example/?x=1&amp;y=2\" activationType=\"protocol\">"));

    [Fact]
    public void FileUriAndSystemSenderOpenNothing()
    {
        Assert.Null(NotificationService.Target("Windows.SystemToast.Calling", "<toast launch=\"file:///C:/x.exe\" activationType=\"protocol\">"));
        Assert.Null(NotificationService.Target(null, "<toast>"));
    }
}

public class HmeBarTests
{
    [Fact] public void Hme_sits_right_before_the_control_center() =>
        Assert.Equal(Array.IndexOf(BarOrder.Default, "controls") - 1, Array.IndexOf(BarOrder.Default, "hme"));

    [Fact] public void Old_saved_order_gets_hme_after_the_bell() =>
        Assert.Equal(new[] { "memory", "tray", "network", "bluetooth", "volume", "bell", "hme", "controls", "clock" },
            BarOrder.Arrange(BarOrder.Default, new[] { "memory", "tray", "network", "bluetooth", "volume", "bell", "controls", "clock" }));

    [Fact] public void Hme_is_off_by_default() => Assert.False(new AppSettings().TopBarShowHme);
}

public class LiquidGlassTests
{
    static readonly System.Windows.Media.Color Base = System.Windows.Media.Color.FromArgb(0xC0, 0x20, 0x20, 0x24);

    [Fact]
    public void Clear_is_more_transparent_than_regular()
    {
        Assert.Equal(Base.A, LiquidGlass.Fill(Base, GlassStyle.Regular).A);
        Assert.True(LiquidGlass.Fill(Base, GlassStyle.Clear).A < Base.A / 2 + 10);
    }

    [Fact]
    public void Tint_pulls_the_colour_toward_it()
    {
        var red = LiquidGlass.Fill(Base, GlassStyle.Regular, System.Windows.Media.Color.FromArgb(0x60, 0xFF, 0, 0));
        Assert.True(red.R > Base.R + 20);
        Assert.Equal(Base.A, red.A);
    }

    [Fact]
    public void Light_comes_from_the_top_left_without_a_colour_fringe()
    {
        foreach (var dark in new[] { true, false })
        {
            var rim = LiquidGlass.Rim(dark);
            Assert.True(rim.IsFrozen);
            Assert.Equal(new System.Windows.Point(0, 0), rim.StartPoint);
            Assert.Equal(new System.Windows.Point(1, 1), rim.EndPoint);
            var stops = rim.GradientStops.OrderBy(s => s.Offset).ToList();
            Assert.True(stops[0].Color.A > stops[^1].Color.A, "the lit corner is the brightest");
            Assert.True(stops[^1].Color.A > stops.Min(s => s.Color.A), "the far edge keeps a faint reflection");
            Assert.All(stops, s => Assert.True(s.Color.R == s.Color.G && s.Color.G == s.Color.B));
        }
    }

    [Fact]
    public void Lens_is_a_band_along_the_edge()
    {
        var ring = LiquidGlass.Ring(new System.Windows.Size(200, 100), 18, 8);
        Assert.True(ring.FillContains(new System.Windows.Point(3, 50)));
        Assert.True(ring.FillContains(new System.Windows.Point(100, 96)));
        Assert.False(ring.FillContains(new System.Windows.Point(100, 50)));
        Assert.True(ring.IsFrozen);
    }

    [Fact]
    public void Dock_glass_is_on_by_default() => Assert.True(new AppSettings().LiquidGlass);
}

public class SettingsReadTests
{
    [Fact]
    public async Task A_briefly_locked_file_is_read_after_the_lock()
    {
        var path = Path.Combine(Path.GetTempPath(), "shft-lock-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, "{\"x\":1}");
        var lockStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        // released on its own thread: a busy thread pool (CI) released it too late for the reader's retries
        var release = new Thread(() => { Thread.Sleep(250); lockStream.Dispose(); });
        try
        {
            release.Start();
            Assert.Equal("{\"x\":1}", SettingsStore.ReadLocked(path));
        }
        finally
        {
            release.Join();
            File.Delete(path);
        }
        await Task.CompletedTask;
    }
}

public class RealGlassSettingsTests
{
    [Fact] public void Real_glass_is_off_by_default() => Assert.False(new AppSettings().RealGlass);

    [Fact] public void Glass_is_flat_and_control_center_keeps_its_card_by_default()
    {
        var s = new AppSettings();
        Assert.False(s.GlassGradient);
        Assert.True(s.ControlCenterBackground);
    }

    [Fact] public void Glass_style_defaults_to_regular() => Assert.Equal(GlassStyle.Regular, new AppSettings().GlassStyle);

    [Fact]
    public void Old_settings_load_with_the_defaults()
    {
        var s = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"LiquidGlass\": true}")!;
        Assert.False(s.RealGlass);
        Assert.Equal(GlassStyle.Regular, s.GlassStyle);
    }
}

public class RealGlassPanelTests
{
    [Fact]
    public void Reduced_motion_glass_follows_opacity_only()
    {
        Assert.Equal((1d, 1d, 0d, 0.4), Shft.Native.Glass.GlassRuntime.Follow(0.95, 0.95, -8, 0.4, reduced: true));
        Assert.Equal((0.95, 0.95, -8d, 0.4), Shft.Native.Glass.GlassRuntime.Follow(0.95, 0.95, -8, 0.4, reduced: false));
    }
}

public class DisplacedFullScreenTests
{
    static readonly RECT Screen = new(0, 0, 1280, 960), WorkUnderTopBar = new(0, 32, 1280, 960);
    const long Popup = unchecked((long)0x80000000), Captioned = 0x00C00000;

    [Fact]
    public void Game_restored_under_the_top_bar_is_displaced() =>
        Assert.True(Monitors.Displaced(new RECT(0, 32, 1280, 992), Screen, WorkUnderTopBar, Popup));

    [Fact]
    public void Game_in_place_is_not() =>
        Assert.False(Monitors.Displaced(new RECT(0, 0, 1280, 960), Screen, WorkUnderTopBar, Popup));

    [Fact]
    public void Shift_other_than_the_strip_is_left_alone() =>
        Assert.False(Monitors.Displaced(new RECT(0, 50, 1280, 1010), Screen, WorkUnderTopBar, Popup));

    [Fact]
    public void Smaller_or_captioned_windows_are_left_alone()
    {
        Assert.False(Monitors.Displaced(new RECT(0, 32, 1280, 960), Screen, WorkUnderTopBar, Popup));
        Assert.False(Monitors.Displaced(new RECT(0, 32, 1280, 992), Screen, WorkUnderTopBar, Captioned));
    }

    [Fact]
    public void Left_dock_strip_counts_too() =>
        Assert.True(Monitors.Displaced(new RECT(80, 0, 1360, 960), Screen, new RECT(80, 0, 1280, 960), Popup));
}

public class TrayClickTests
{
    const string Steam = @"C:\Program Files (x86)\Steam\steam.exe";

    [Fact] public void App_window_or_menu_counts_as_answer() => Assert.True(TrayHost.Answered(true, null, Steam));
    [Fact] public void Helper_in_the_app_folder_counts_as_answer() =>
        Assert.True(TrayHost.Answered(false, @"C:\Program Files (x86)\Steam\bin\cef\cef.win64\steamwebhelper.exe", Steam));
    [Fact] public void Previous_app_in_front_means_nothing_showed() =>
        Assert.False(TrayHost.Answered(false, @"C:\Program Files\Google\Chrome\Application\chrome.exe", Steam));
    [Fact] public void Sibling_folder_with_the_same_prefix_does_not_count() =>
        Assert.False(TrayHost.Answered(false, @"C:\Program Files (x86)\SteamTools\x.exe", Steam));
}
