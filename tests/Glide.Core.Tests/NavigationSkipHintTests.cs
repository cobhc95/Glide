using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Glide.App;
using Glide.App.Settings;
using Glide.Core.Navigation;
using Glide.Core.Workspace;

namespace Glide.Core.Tests;

/// <summary>
/// 5.0.6: the sticky / bouncing key detector (pure) and the "Skipping several pictures at once?" toast (navigation.skipHint).
/// </summary>
public sealed class NavigationSkipDetectorTests
{
    private const int Right = 1;
    private const int Left = 2;

    [Fact]
    public void A_bouncing_key_fires_on_the_second_extra_navigation()
    {
        var d = new NavigationSkipDetector();
        Assert.False(d.OnKeyDown(Right, 0, navigated: true));      // the real press
        Assert.False(d.OnKeyUp(Right, 30));
        Assert.False(d.OnKeyDown(Right, 90, navigated: true));     // bounce 1 (accepted after the 80 ms limit)
        Assert.False(d.OnKeyUp(Right, 100));
        Assert.True(d.OnKeyDown(Right, 180, navigated: true));     // bounce 2
    }

    [Fact]
    public void A_bounce_without_any_limit_fires_too()
    {
        var d = new NavigationSkipDetector();
        d.OnKeyDown(Right, 0, true); d.OnKeyUp(Right, 6);
        d.OnKeyDown(Right, 12, true); d.OnKeyUp(Right, 18);
        Assert.True(d.OnKeyDown(Right, 24, true));
    }

    [Fact]
    public void A_sticky_key_that_repeats_faster_than_auto_repeat_fires_on_key_up()
    {
        var d = new NavigationSkipDetector();
        Assert.False(d.OnKeyDown(Right, 0, true));
        Assert.False(d.OnKeyDown(Right, 60, true));   // no key-up in between: looks like a repeat ...
        Assert.False(d.OnKeyDown(Right, 130, true));  // ... but Windows never repeats this early
        Assert.True(d.OnKeyUp(Right, 200));
    }

    [Fact]
    public void Normal_auto_repeat_of_a_held_key_never_fires()
    {
        var d = new NavigationSkipDetector();
        Assert.False(d.OnKeyDown(Right, 0, true));
        // Windows' repeat delay (here 500 ms), then ~30 repeats a second, every one accepted.
        for (var t = 500.0; t < 1500; t += 33)
            Assert.False(d.OnKeyDown(Right, t, true));
        Assert.False(d.OnKeyUp(Right, 1500));
        // The shortest repeat delay Windows offers (250 ms) does not fire either.
        Assert.False(d.OnKeyDown(Right, 3000, true));
        Assert.False(d.OnKeyDown(Right, 3250, true));
        Assert.False(d.OnKeyDown(Right, 3283, true));
        Assert.False(d.OnKeyUp(Right, 3300));
    }

    [Fact]
    public void A_single_press_never_fires()
    {
        var d = new NavigationSkipDetector();
        Assert.False(d.OnKeyDown(Right, 0, true));
        Assert.False(d.OnKeyUp(Right, 90));
    }

    [Fact]
    public void Slow_and_deliberate_presses_never_fire()
    {
        var d = new NavigationSkipDetector();
        for (var t = 0.0; t < 3000; t += 200)                       // five presses a second
        {
            Assert.False(d.OnKeyDown(Right, t, true));
            Assert.False(d.OnKeyUp(Right, t + 70));
        }
        // A quick deliberate double tap (one extra) does not fire either.
        Assert.False(d.OnKeyDown(Right, 5000, true)); d.OnKeyUp(Right, 5040);
        Assert.False(d.OnKeyDown(Right, 5120, true)); d.OnKeyUp(Right, 5160);
        Assert.False(d.OnKeyDown(Right, 5600, true));
    }

    [Fact]
    public void Key_downs_blocked_by_the_limit_are_not_extra_navigations()
    {
        var d = new NavigationSkipDetector();
        d.OnKeyDown(Right, 0, true); d.OnKeyUp(Right, 10);
        Assert.False(d.OnKeyDown(Right, 40, navigated: false)); d.OnKeyUp(Right, 50);
        Assert.False(d.OnKeyDown(Right, 80, navigated: false)); d.OnKeyUp(Right, 90);
        Assert.False(d.OnKeyDown(Right, 300, true));
    }

    [Fact]
    public void The_mouse_wheel_never_fires_and_breaks_a_chain()
    {
        var d = new NavigationSkipDetector();
        for (var t = 0.0; t < 300; t += 10) Assert.False(d.OnWheelNavigation(t));
        d.OnKeyDown(Right, 400, true); d.OnKeyUp(Right, 410);
        d.OnKeyDown(Right, 450, true); d.OnKeyUp(Right, 460);
        d.OnWheelNavigation(470);
        Assert.False(d.OnKeyDown(Right, 500, true));
    }

    [Fact]
    public void Alternating_keys_are_not_one_bouncing_key()
    {
        var d = new NavigationSkipDetector();
        d.OnKeyDown(Right, 0, true); d.OnKeyUp(Right, 20);
        d.OnKeyDown(Left, 60, true); d.OnKeyUp(Left, 80);
        Assert.False(d.OnKeyDown(Right, 120, true));
    }

    [Fact]
    public void Reset_forgets_a_key_whose_key_up_was_lost()
    {
        var d = new NavigationSkipDetector();
        d.OnKeyDown(Right, 0, true);
        d.Reset();                                                   // window deactivated: the key-up never arrives
        d.OnKeyDown(Right, 20, true); d.OnKeyUp(Right, 30);          // a fresh press, not a repeat
        d.OnKeyDown(Right, 60, true); d.OnKeyUp(Right, 70);
        Assert.True(d.OnKeyDown(Right, 100, true));
    }
}

[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class NavigationSkipHintWindowTests
{
    public NavigationSkipHintWindowTests(AvaloniaHeadlessFixture fixture) { _ = fixture; }

    [Fact]
    public void The_toast_may_show_while_the_setting_is_on_and_at_most_once_every_five_minutes()
    {
        const double cooldown = MainWindow.NavigationSkipHintCooldownMs;
        Assert.Equal(5 * 60 * 1000, cooldown);
        var on = new GlideSettingsState();
        Assert.True(MainWindow.ShouldShowNavigationSkipHint(on, null, 0));
        Assert.False(MainWindow.ShouldShowNavigationSkipHint(on, 1000, 1000 + cooldown - 1));
        Assert.True(MainWindow.ShouldShowNavigationSkipHint(on, 1000, 1000 + cooldown));
        // The interval no longer matters: a limit already raised above the default still gets the toast.
        Assert.True(MainWindow.ShouldShowNavigationSkipHint(new GlideSettingsState { NavigationRateLimitMs = 400 }, null, 0));
        Assert.False(MainWindow.ShouldShowNavigationSkipHint(new GlideSettingsState { NavigationSkipHint = false }, null, 0));
    }

    [Fact]
    public async Task The_toast_shows_again_after_the_cooldown_but_not_within_it()
    {
        const double cooldown = MainWindow.NavigationSkipHintCooldownMs;
        await OnWindow(async window =>
        {
            var hint = window.FindControl<Border>("NavigationSkipHint")!;
            Assert.False(hint.IsVisible);
            Bounce(window, 0);
            Assert.True(hint.IsVisible);
            Assert.Equal(Avalonia.Layout.HorizontalAlignment.Center, hint.HorizontalAlignment);
            Assert.Equal(Avalonia.Layout.VerticalAlignment.Bottom, hint.VerticalAlignment);
            // Non-modal and never steals focus: its buttons cannot take focus.
            var buttons = hint.GetLogicalDescendants().OfType<Button>().ToList();
            Assert.Equal(3, buttons.Count);
            foreach (var button in buttons)
                Assert.False(button.Focusable, button.Name);
            var texts = hint.GetLogicalDescendants().OfType<TextBlock>().ToList();
            Assert.Contains(texts, t => t.Text == "Skipping several pictures at once?");
            Assert.Contains(texts, t => t.Text == "Adjust navigation speed →");
            var dontRemind = Assert.Single(texts, t => t.Text == "Don't remind me again");
            Assert.Equal(Avalonia.Media.TextDecorations.Underline, dontRemind.TextDecorations);
            Assert.True(Settings(window).NavigationSkipHint, "showing the toast changes no setting");

            // The x only closes it; the setting stays on.
            window.FindControl<Button>("NavigationSkipHintClose")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(hint.IsVisible);
            Assert.True(Settings(window).NavigationSkipHint);

            Bounce(window, cooldown / 2);                            // within the 5-minute cooldown
            Assert.False(hint.IsVisible);
            Bounce(window, cooldown - 1000);                         // still within it
            Assert.False(hint.IsVisible);
            Bounce(window, cooldown + 1000);                         // after it
            Assert.True(hint.IsVisible);
            await Settle();
        });
    }

    [Fact]
    public async Task Dont_remind_me_again_turns_the_setting_off_and_stops_the_toast_until_it_is_turned_back_on()
    {
        const double cooldown = MainWindow.NavigationSkipHintCooldownMs;
        await OnWindow(async window =>
        {
            var hint = window.FindControl<Border>("NavigationSkipHint")!;
            Bounce(window, 0);
            Assert.True(hint.IsVisible);
            window.FindControl<Button>("NavigationSkipHintDontRemind")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(hint.IsVisible);
            Assert.False(Settings(window).NavigationSkipHint);
            // Saved like a Settings change: the setting round-trips through the settings file.
            SettingsStore.Save(Settings(window));
            Assert.False(SettingsStore.Load().NavigationSkipHint);

            Bounce(window, cooldown + 1000);                         // off: no toast, even after the cooldown
            Assert.False(hint.IsVisible);
            Bounce(window, 3 * cooldown);
            Assert.False(hint.IsVisible);

            // Turned back on through its registry toggle action (the hotkey path), the toast shows again.
            var action = Glide.Core.Commands.AppActionRegistry.All.Single(a =>
                a.SettingId == "navigation.skipHint" && a.Kind == Glide.Core.Commands.AppActionKind.SettingToggle);
            Invoke(window, "ChangeSetting", action);
            await Settle();
            Assert.True(Settings(window).NavigationSkipHint);
            Bounce(window, 4 * cooldown);
            Assert.True(hint.IsVisible);
            await Settle();
        });
    }

    [Fact]
    public async Task With_the_setting_off_no_toast_shows()
    {
        await OnWindow(async window =>
        {
            Settings(window).NavigationSkipHint = false;
            Bounce(window, 0);
            Assert.False(window.FindControl<Border>("NavigationSkipHint")!.IsVisible);
            await Settle();
        });
    }

    [Fact]
    public async Task A_limit_already_raised_above_the_default_still_gets_the_toast()
    {
        await OnWindow(async window =>
        {
            Settings(window).NavigationRateLimitMs = 250;
            Bounce(window, 0);
            Assert.True(window.FindControl<Border>("NavigationSkipHint")!.IsVisible);
            await Settle();
        });
    }

    [Fact]
    public async Task The_toast_fades_after_about_six_seconds()
    {
        Assert.InRange(MainWindow.NavigationSkipHintVisibleMs, 5000, 7000);
        await OnWindow(async window =>
        {
            Bounce(window, 0);
            var hint = window.FindControl<Border>("NavigationSkipHint")!;
            Assert.True(hint.IsVisible);
            var deadline = DateTime.UtcNow.AddMilliseconds(MainWindow.NavigationSkipHintVisibleMs + 3000);
            while (hint.IsVisible && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); await Task.Delay(50); }
            Assert.False(hint.IsVisible);
        });
    }

    [Fact]
    public async Task The_link_opens_Settings_and_reveals_and_highlights_the_navigation_minimum_interval()
    {
        await OnWindow(async window =>
        {
            Bounce(window, 0);
            var hint = window.FindControl<Border>("NavigationSkipHint")!;
            Assert.True(hint.IsVisible);
            window.FindControl<Button>("NavigationSkipHintLink")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(hint.IsVisible);

            SettingsWindow? settings = null;
            Control? highlighted = null;
            for (var i = 0; i < 200; i++)
            {
                Dispatcher.UIThread.RunJobs();
                settings = GetField(window, "_openSettingsWindow") as SettingsWindow;
                highlighted = settings is null ? null : SettingsField<Control?>(settings, "_revealHighlightTarget");
                if (highlighted is not null) break;
                await Task.Delay(20);
            }
            Assert.NotNull(settings);
            try
            {
                Assert.NotNull(highlighted);
                var card = SettingsField<Dictionary<string, Glide.App.Controls.SettingsCard>>(settings!, "_settingCard")["navigation.rateLimitMs"];
                Assert.Equal("window-rate-limit", card.CardId);
                Assert.True(card.IsExpanded, "the Navigation speed limit card is expanded");
                var page = SettingsField<Dictionary<Glide.App.Controls.SettingsCard, string>>(settings!, "_pageOfCard")[card];
                Assert.Equal(page, SettingsField<string>(settings!, "_activeCategory"));
                var box = settings!.FindControl<NumericUpDown>("NavigationRateLimitMsBox")!;
                Assert.True(ReferenceEquals(highlighted, box) || highlighted!.GetLogicalDescendants().Contains(box), "the highlighted row holds the interval box");
                // Hint B: the help line sits in the same card.
                var help = settings.FindControl<TextBlock>("NavigationRateLimitHelpText")!;
                Assert.Equal("Raise this if one key press skips several pictures (common on laptop keyboards). Lower it for faster browsing. 0 = no limit.", help.Text);
                Assert.Contains("phaseNote", help.Classes);
                Assert.Contains(help, card.GetLogicalDescendants());
            }
            finally { settings!.Close(); Dispatcher.UIThread.RunJobs(); }
        });
    }

    [Fact]
    public async Task Real_key_presses_a_bouncing_Page_Down_shows_the_toast_but_a_held_arrow_does_not()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Glide.SkipHintTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        foreach (var name in new[] { "a.png", "b.png", "c.png", "d.png", "e.png", "f.png", "g.png", "h.png" })
            File.WriteAllBytes(Path.Combine(folder, name), NavigationHistoryWindowTests.Png(24, 16));
        try
        {
            await OnWindow(async window =>
            {
                Settings(window).NavigationRateLimitMs = 0;          // no limit: every key-down navigates
                await (Task)Invoke(window, "OpenAsImageTabAsync", Path.Combine(folder, "a.png"), false, false)!;
                await Settle();
                var hint = window.FindControl<Border>("NavigationSkipHint")!;

                // A held arrow: auto-repeat key-downs while the key is down start nothing new, and the release is quick.
                var before = (int)GetField(window, "_acceptedKeyboardNavigations")!;
                window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
                for (var i = 0; i < 4; i++) window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
                window.KeyRelease(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
                await Settle();
                Assert.Equal(before + 1, (int)GetField(window, "_acceptedKeyboardNavigations")!);
                Assert.False(hint.IsVisible);
                await Task.Delay(400);                                // break any chain

                // A bouncing Page Down: three separate key-downs within a few milliseconds.
                for (var i = 0; i < 3; i++)
                {
                    window.KeyPress(Key.PageDown, RawInputModifiers.None, PhysicalKey.PageDown, null);
                    window.KeyRelease(Key.PageDown, RawInputModifiers.None, PhysicalKey.PageDown, null);
                }
                Assert.True(hint.IsVisible);
                await Settle();
            });
        }
        finally { try { Directory.Delete(folder, true); } catch { } }
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    /// <summary>A bouncing Right arrow fed to the window's detector with synthetic times (fires on the third key-down).</summary>
    private static void Bounce(MainWindow window, double start)
    {
        const int key = (int)Key.Right;
        window.NoteNavigationKeyDown(key, start, true); window.NoteNavigationKeyUp(key, start + 10);
        window.NoteNavigationKeyDown(key, start + 60, true); window.NoteNavigationKeyUp(key, start + 70);
        window.NoteNavigationKeyDown(key, start + 120, true); window.NoteNavigationKeyUp(key, start + 130);
    }

    private static async Task OnWindow(Func<MainWindow, Task> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "Glide.Settings.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
        {
            var previousStore = SettingsStore.SettingsDirectoryOverrideForTests.Value;
            SettingsStore.SettingsDirectoryOverrideForTests.Value = root;
            var window = new MainWindow();
            try
            {
                // Work on a pristine in-memory state; never the user's settings.
                SetField(window, "_settings", new GlideSettingsState());
                window.Show();
                await Settle();
                ((WorkspaceState)GetField(window, "_workspace")!).Reset(new TabState[] { new HomeTabState(Guid.NewGuid()) }, 0);
                await body(window);
            }
            finally
            {
                window.Close();
                SettingsStore.SettingsDirectoryOverrideForTests.Value = previousStore;
            }
        });
        try { Directory.Delete(root, true); } catch { }
    }

    private static async Task Settle()
    {
        for (var i = 0; i < 6; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(8); }
    }

    private static GlideSettingsState Settings(MainWindow window) => (GlideSettingsState)GetField(window, "_settings")!;

    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

    private static object? Invoke(MainWindow window, string method, params object?[] args)
    {
        var info = typeof(MainWindow).GetMethod(method, Flags);
        Assert.NotNull(info);
        return info!.Invoke(window, args);
    }

    private static object? GetField(MainWindow window, string name)
    {
        var field = typeof(MainWindow).GetField(name, Flags);
        Assert.NotNull(field);
        return field!.GetValue(window);
    }

    private static void SetField(MainWindow window, string name, object? value)
    {
        var field = typeof(MainWindow).GetField(name, Flags);
        Assert.NotNull(field);
        field!.SetValue(window, value);
    }

    private static T SettingsField<T>(SettingsWindow settings, string name)
    {
        var field = typeof(SettingsWindow).GetField(name, Flags);
        Assert.NotNull(field);
        return (T)field!.GetValue(settings)!;
    }
}
