using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Glide.App;
using Glide.App.Controls;
using Glide.App.Services;
using Glide.App.Settings;
using Glide.App.Updates;
using Glide.Core.Commands;
using Glide.Core.Updates;
using Glide.Core.Windows;
using static Glide.Core.Tests.UpdateTestSupport;

namespace Glide.Core.Tests;

/// <summary>
/// Headless contract of the update prompt window and of Settings > General &amp; startup > Updates: layout, buttons, checkbox, Esc,
/// focus, result flags, status states, Check now with a fake feed, no "N changed" badge from the status line, search, the registry
/// action, and PNG renders (set GLIDE_SAVE_RENDERS to a folder) to compare with the approved mockups.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class UpdateUiTests : IDisposable
{
    public UpdateUiTests(AvaloniaHeadlessFixture fixture) => _ = fixture;

    private static readonly List<IStyle> InstalledStyles = new();

    private static void EnsureAppStyles()
    {
        if (InstalledStyles.Count > 0) return;
        var app = Application.Current!;
        app.RequestedThemeVariant = ThemeVariant.Dark;
        InstalledStyles.Add(new FluentTheme());
        InstalledStyles.Add(new StyleInclude(new Uri("avares://Glide/")) { Source = new Uri("avares://Glide/Styles/GlideTheme.axaml") });
        for (var i = 0; i < InstalledStyles.Count; i++) app.Styles.Insert(i, InstalledStyles[i]);
    }

    public void Dispose()
    {
        if (InstalledStyles.Count == 0) return;
        AvaloniaHeadlessFixture.RunOnUIThread(() =>
        {
            foreach (var style in InstalledStyles) Application.Current!.Styles.Remove(style);
            InstalledStyles.Clear();
        });
    }

    private static T Named<T>(Visual root, string name) where T : Visual =>
        root.GetVisualDescendants().OfType<T>().Single(c => (c as StyledElement)?.Name == name);

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static string? SaveDirectory => Environment.GetEnvironmentVariable("GLIDE_SAVE_RENDERS");

    private static void Save(Visual visual, string name)
    {
        if (string.IsNullOrWhiteSpace(SaveDirectory)) return;
        Directory.CreateDirectory(SaveDirectory);
        var size = new PixelSize((int)Math.Ceiling(visual.Bounds.Width), (int)Math.Ceiling(visual.Bounds.Height));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
        bitmap.Render(visual);
        bitmap.Save(Path.Combine(SaveDirectory, name));
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static UpdatePromptModel Model(string current = "5.0.0", string latest = "5.0.1", string released = "Released 2 days ago on GitHub") =>
        new(current, latest, released, "https://github.com/cobhc95/Glide/releases/tag/v5.0.1");

    // ================================================================ the prompt window ========

    private static UpdatePromptWindow OpenPrompt(UpdatePromptModel? model = null)
    {
        EnsureAppStyles();
        var window = new UpdatePromptWindow(model ?? Model());
        window.Show();
        Settle(window);
        return window;
    }

    [Fact]
    public async Task The_prompt_shows_the_mockup_content()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var window = OpenPrompt();
            try
            {
                Assert.Equal("A new version of Glide is available", window.HeadingText.Text);
                Assert.Equal("Released 2 days ago on GitHub", window.ReleasedText.Text);
                Assert.Equal("5.0.0", window.CurrentVersionText.Text);
                Assert.Equal("5.0.1", window.LatestVersionText.Text);
                Assert.Equal("Not now", window.NotNowButton.Content);
                Assert.Equal("View on GitHub", window.ViewButton.Content);
                Assert.Equal("Don't remind me again", window.DontRemindCheck.Content);
                Assert.False(window.DontRemindCheck.IsChecked);
                Assert.Contains("never downloads or installs anything by itself", window.NoteBlock.Text);
                Assert.Contains("opens the release page in your browser", window.NoteBlock.Text);
                Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "New");
                Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "You have ");
                Assert.Contains(window.GetVisualDescendants().OfType<GlideIconView>(), i => i.Kind == "Download");
                Assert.Contains(window.GetVisualDescendants().OfType<GlideIconView>(), i => i.Kind == "Forward");
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task The_prompt_is_a_fixed_470_wide_modal_without_a_taskbar_button()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var window = OpenPrompt();
            try
            {
                Assert.Equal(470, window.Width);
                Assert.Equal(470, UpdatePromptWindow.DialogWidth);
                Assert.Equal(470, Math.Round(window.Bounds.Width));
                Assert.False(window.CanResize);
                Assert.False(window.ShowInTaskbar);
                Assert.Equal(SizeToContent.Height, window.SizeToContent);
                Assert.True(window.Bounds.Height > 150 && window.Bounds.Height < 400, "content-sized height: " + window.Bounds.Height);
                // Everything fits: nothing is clipped at the window's right edge.
                foreach (var visual in window.GetVisualDescendants().OfType<Control>().Where(c => c.IsVisible && c.Bounds.Width > 0))
                {
                    var right = visual.TranslatePoint(new Point(visual.Bounds.Width, 0), window)!.Value.X;
                    Assert.True(right <= window.Bounds.Width + 0.5, $"{visual.GetType().Name} sticks out at {right}");
                }
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Not_now_has_the_initial_focus_and_is_the_cancel_button_while_view_is_not_a_default()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var window = OpenPrompt();
            try
            {
                Assert.True(window.NotNowButton.IsFocused, "focus: " + TopLevel.GetTopLevel(window)?.FocusManager?.GetFocusedElement());
                Assert.True(window.NotNowButton.IsCancel);
                Assert.False(window.ViewButton.IsDefault);
                Assert.False(window.NotNowButton.IsDefault);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Pressing_Enter_on_the_opening_focus_means_Not_now_never_the_browser()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var window = OpenPrompt();
            try
            {
                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                Settle(window);
                Assert.NotNull(window.Outcome);
                Assert.Equal(UpdatePromptAction.NotNow, window.Outcome!.Action);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task The_buttons_set_the_result()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var notNow = OpenPrompt();
            Click(notNow.NotNowButton);
            Assert.Equal(new UpdatePromptResult(UpdatePromptAction.NotNow, false), notNow.Outcome);

            var view = OpenPrompt();
            Click(view.ViewButton);
            Assert.Equal(new UpdatePromptResult(UpdatePromptAction.ViewOnGitHub, false), view.Outcome);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task The_checkbox_is_carried_by_either_button()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var a = OpenPrompt();
            a.DontRemindCheck.IsChecked = true;
            Click(a.NotNowButton);
            Assert.Equal(new UpdatePromptResult(UpdatePromptAction.NotNow, true), a.Outcome);

            var b = OpenPrompt();
            b.DontRemindCheck.IsChecked = true;
            Click(b.ViewButton);
            Assert.Equal(new UpdatePromptResult(UpdatePromptAction.ViewOnGitHub, true), b.Outcome);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Escape_and_the_close_button_are_Not_now()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var esc = OpenPrompt();
            esc.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Settle(esc);
            Assert.Equal(new UpdatePromptResult(UpdatePromptAction.NotNow, false), esc.Outcome);

            var escChecked = OpenPrompt();
            escChecked.DontRemindCheck.IsChecked = true;
            escChecked.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Settle(escChecked);
            Assert.Equal(new UpdatePromptResult(UpdatePromptAction.NotNow, true), escChecked.Outcome);

            var x = OpenPrompt();
            x.Close();                                         // the window's own close button
            Assert.Equal(new UpdatePromptResult(UpdatePromptAction.NotNow, false), x.Outcome);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task The_window_reports_it_was_shown_and_has_no_result_until_used()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            EnsureAppStyles();
            var window = new UpdatePromptWindow(Model());
            Assert.False(window.WasShown);
            Assert.Null(window.Outcome);
            window.Show();
            Settle(window);
            Assert.True(window.WasShown);
            Assert.Null(window.Outcome);
            window.Close();
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task The_prompt_is_a_centred_dialog_in_the_placement_service()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            EnsureAppStyles();
            var window = new UpdatePromptWindow(Model());
            try
            {
                Assert.Equal("update-prompt", WindowPlacementService.KeyOf(window));
                Assert.Equal(WindowPlacementMode.PositionOnly, WindowPlacementService.ModeOf(window));
                Assert.NotNull(WindowPlacementCatalog.Find("update-prompt"));
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Render_the_prompt()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var window = OpenPrompt();
            try
            {
                Save(window, "prompt-default.png");
                window.DontRemindCheck.IsChecked = true;
                Settle(window);
                Save(window, "prompt-dont-remind-ticked.png");
            }
            finally { window.Close(); }
            var released = OpenPrompt(Model("5.0.1", "5.0.2", "Released today on GitHub"));
            try { Save(released, "prompt-5.0.1-to-5.0.2.png"); }
            finally { released.Close(); }
            return Task.CompletedTask;
        });
    }

    // ================================================================ the Settings card ========

    private static SettingsWindow OpenSettings(UpdateRig? rig, GlideSettingsState? state = null, double width = 930, double height = 630, bool expand = false)
    {
        EnsureAppStyles();
        var window = new SettingsWindow(state ?? new GlideSettingsState(), (_, _) => { });
        if (rig is not null) window.Updates = rig.Service;
        window.Show();
        typeof(SettingsWindow).GetMethod("ShowCategory", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, new object?[] { "General", true, false, null });
        window.Width = width;
        window.Height = height;
        Settle(window);
        var card = Card(window);
        card.IsExpanded = expand;
        Settle(window);
        return window;
    }

    private static SettingsCard Card(SettingsWindow window) =>
        window.GetLogicalDescendants().OfType<SettingsCard>().Single(c => c.CardId == "general-updates");

    private static string StatusText(SettingsWindow window) =>
        (Named<TextBlock>(window, "UpdateLastCheckedText").IsVisible ? Named<TextBlock>(window, "UpdateLastCheckedText").Text + " " : string.Empty) +
        Named<TextBlock>(window, "UpdateResultText").Text;

    private static async Task<UpdateRig> RigWithAnswer(string? json, bool fail = false, string running = "5.0.1")
    {
        var rig = new UpdateRig(running);
        if (fail) rig.Feed.ThenFail();
        else if (json is not null) rig.Feed.ThenJson(json);
        if (json is not null || fail) await rig.Service.CheckNowAsync();
        return rig;
    }

    [Fact]
    public async Task The_Updates_card_sits_between_History_and_Settings_window_collapsed_with_its_mockup_text()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var window = OpenSettings(null);
            try
            {
                var panel = Named<StackPanel>(window, "GeneralPanel");
                var ids = panel.Children.OfType<SettingsCard>().Select(c => c.CardId).ToList();
                var index = ids.IndexOf("general-updates");
                Assert.True(index > 0);
                Assert.Equal("general-history", ids[index - 1]);
                Assert.Equal("general-settings-window", ids[index + 1]);
                var card = Card(window);
                Assert.Equal("Updates", card.Title);
                Assert.Equal("Check GitHub for new versions", card.Subtitle);
                Assert.False(card.IsExpanded);
                Assert.Equal(0, card.ChangedCount);
                Assert.Contains("updates", Named<TextBlock>(window, "PageSubtitle").Text, StringComparison.OrdinalIgnoreCase);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task The_card_holds_the_two_settings_a_status_line_two_buttons_and_the_privacy_note()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var window = OpenSettings(null, expand: true);
            try
            {
                var daily = Named<CheckBox>(window, "UpdateCheckDailyCheck");
                var dontRemind = Named<CheckBox>(window, "UpdateDontRemindCheck");
                Assert.Equal("Check for updates once a day", daily.Content);
                Assert.True(daily.IsChecked);                                   // default: on
                Assert.False(dontRemind.IsChecked);                             // default: off
                var texts = dontRemind.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
                Assert.Contains("Don't remind me about updates", texts);
                Assert.Contains("(turns the prompt off)", texts);
                Assert.Equal("Check now", Named<Button>(window, "UpdateCheckNowButton").Content);
                Assert.Equal("View on GitHub", Named<Button>(window, "UpdateViewButton").Content);
                Assert.Equal("Only a plain request to github.com is sent: no personal data, no telemetry. Nothing is downloaded or installed automatically.",
                    Named<TextBlock>(window, "UpdatePrivacyNote").Text);
                var card = Card(window);
                foreach (var name in new[] { "UpdateCheckDailyCheck", "UpdateDontRemindCheck", "UpdateCheckNowButton", "UpdateViewButton", "UpdateResultText", "UpdatePrivacyNote" })
                    Assert.Contains(card.Body.GetLogicalDescendants().Prepend(card.Body), e => (e as StyledElement)?.Name == name);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task The_checkboxes_load_and_write_the_two_settings()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            GlideSettingsState? applied = null;
            EnsureAppStyles();
            var window = new SettingsWindow(new GlideSettingsState { UpdateCheckDaily = false, UpdateDontRemind = true }, (s, _) => applied = s);
            window.Show();
            Settle(window);
            try
            {
                Assert.False(Named<CheckBox>(window, "UpdateCheckDailyCheck").IsChecked);
                Assert.True(Named<CheckBox>(window, "UpdateDontRemindCheck").IsChecked);
                var read = (GlideSettingsState)typeof(SettingsWindow).GetMethod("ReadControls", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null)!;
                Assert.False(read.UpdateCheckDaily);
                Assert.True(read.UpdateDontRemind);
                Named<CheckBox>(window, "UpdateCheckDailyCheck").IsChecked = true;
                Named<CheckBox>(window, "UpdateDontRemindCheck").IsChecked = false;
                read = (GlideSettingsState)typeof(SettingsWindow).GetMethod("ReadControls", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null)!;
                Assert.True(read.UpdateCheckDaily);
                Assert.False(read.UpdateDontRemind);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Without_a_service_the_card_says_not_checked_yet_and_check_now_is_disabled()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var window = OpenSettings(null, expand: true);
            try
            {
                Assert.Equal("Not checked yet", StatusText(window));
                Assert.False(Named<Button>(window, "UpdateCheckNowButton").IsEnabled);
                Assert.False(Named<Button>(window, "UpdateViewButton").IsVisible);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task The_status_line_shows_not_checked_up_to_date_available_and_could_not_reach()
    {
        var notChecked = new UpdateRig();
        var upToDate = await RigWithAnswer(ReleaseJson("v5.0.1"));
        var available = await RigWithAnswer(ReleaseJson("v5.0.2"));
        var failed = await RigWithAnswer(null, fail: true);
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            void Case(UpdateRig rig, string expectedResult, bool viewVisible, string? expectedLastChecked)
            {
                var window = OpenSettings(rig, expand: true);
                try
                {
                    Assert.Equal(expectedResult, Named<TextBlock>(window, "UpdateResultText").Text);
                    Assert.Equal(viewVisible, Named<Button>(window, "UpdateViewButton").IsVisible);
                    var last = Named<TextBlock>(window, "UpdateLastCheckedText");
                    if (expectedLastChecked is null) Assert.False(last.IsVisible);
                    else
                    {
                        Assert.True(last.IsVisible);
                        Assert.StartsWith(expectedLastChecked, last.Text);
                        Assert.EndsWith(".", last.Text);
                    }
                    Assert.True(Named<Button>(window, "UpdateCheckNowButton").IsEnabled);
                    Assert.Equal("Check now", Named<Button>(window, "UpdateCheckNowButton").Content);
                    Assert.Equal(0, Card(window).ChangedCount);        // the status is never a changed setting
                }
                finally { window.Close(); }
            }
            Case(notChecked, "Not checked yet", false, null);
            Case(upToDate, "You're up to date (5.0.1)", false, "Last checked: ");
            Case(available, "Glide 5.0.2 is available", true, "Last checked: ");
            Case(failed, "Couldn't reach GitHub", false, null);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Check_now_runs_the_check_shows_checking_then_the_result()
    {
        var rig = new UpdateRig();
        rig.Feed.Hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Feed.ThenJson(ReleaseJson("v5.0.2"));
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
        {
            var window = OpenSettings(rig, expand: true);
            try
            {
                var button = Named<Button>(window, "UpdateCheckNowButton");
                Assert.Equal("Not checked yet", StatusText(window));
                Click(button);
                Settle(window);
                Assert.Equal("Checking...", button.Content);
                Assert.False(button.IsEnabled);
                Assert.Equal("Checking...", Named<TextBlock>(window, "UpdateResultText").Text);

                rig.Feed.Hold.SetResult(true);
                for (var i = 0; i < 100 && rig.Service.IsChecking; i++) { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }
                Settle(window);
                Assert.Equal(1, rig.Feed.Calls);
                Assert.Equal("Check now", button.Content);
                Assert.True(button.IsEnabled);
                Assert.Equal("Glide 5.0.2 is available", Named<TextBlock>(window, "UpdateResultText").Text);
                Assert.True(Named<Button>(window, "UpdateViewButton").IsVisible);
                Assert.Equal(0, Card(window).ChangedCount);
                Assert.Equal("Up to date", Named<TextBlock>(window, "DirtyText").Text);     // the status never makes Settings "dirty"
                Assert.False(Named<Button>(window, "ApplyButton").IsEnabled);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task Check_now_works_when_daily_checks_are_off_and_reports_a_failure()
    {
        var rig = new UpdateRig();
        rig.Feed.ThenFail();
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
        {
            var window = OpenSettings(rig, new GlideSettingsState { UpdateCheckDaily = false }, expand: true);
            try
            {
                Assert.False(Named<CheckBox>(window, "UpdateCheckDailyCheck").IsChecked);
                Click(Named<Button>(window, "UpdateCheckNowButton"));
                for (var i = 0; i < 100 && rig.Feed.Calls == 0; i++) { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }
                for (var i = 0; i < 100 && rig.Service.IsChecking; i++) { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }
                Settle(window);
                Assert.Equal(1, rig.Feed.Calls);
                Assert.Equal("Couldn't reach GitHub", Named<TextBlock>(window, "UpdateResultText").Text);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task View_on_GitHub_in_Settings_opens_only_the_validated_release_page()
    {
        var rig = await RigWithAnswer(ReleaseJson("v5.0.2", "https://evil.com/cobhc95/Glide/releases/tag/v5.0.2"));
        var started = new List<System.Diagnostics.ProcessStartInfo>();
        UpdateBrowserLauncher.StartOverrideForTests = started.Add;
        try
        {
            await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
            {
                var window = OpenSettings(rig, expand: true);
                try
                {
                    Click(Named<Button>(window, "UpdateViewButton"));
                    var info = Assert.Single(started);
                    Assert.Equal(UpdateUrlPolicy.FallbackReleaseUrl, info.FileName);
                    Assert.True(info.UseShellExecute);
                    Assert.True(string.IsNullOrEmpty(info.Arguments));
                }
                finally { window.Close(); }
                return Task.CompletedTask;
            });
        }
        finally { UpdateBrowserLauncher.StartOverrideForTests = null; }
    }

    [Fact]
    public async Task A_check_finishing_while_Settings_is_open_leaves_every_dirty_indicator_alone()
    {
        var rig = new UpdateRig();
        rig.Feed.ThenJson(ReleaseJson("v5.0.2"));
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
        {
            var window = OpenSettings(rig, expand: true);
            try
            {
                var before = (GlideSettingsState)typeof(SettingsWindow).GetMethod("ReadControls", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null)!;
                await rig.Service.CheckNowAsync();
                Settle(window);
                var after = (GlideSettingsState)typeof(SettingsWindow).GetMethod("ReadControls", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null)!;
                // The window's snapshot has none of the new memory, and a state that does is still "no change".
                var withMemory = after.CloneState();
                rig.Service.ApplyTo(withMemory);
                Assert.NotNull(withMemory.UpdateLastCheckedUtc);
                Assert.Empty(SettingsChangeSet.Compute(before, withMemory));
                Assert.Empty(SettingsChangeSet.Compute(withMemory, before));
                Assert.Equal("Up to date", Named<TextBlock>(window, "DirtyText").Text);
                Assert.Equal(0, Card(window).ChangedCount);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task Changing_a_real_setting_of_the_card_counts_as_a_change_but_the_status_does_not()
    {
        var rig = await RigWithAnswer(ReleaseJson("v5.0.2"));
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var window = OpenSettings(rig, expand: true);
            try
            {
                var card = Card(window);
                Assert.Equal(0, card.ChangedCount);
                Named<CheckBox>(window, "UpdateCheckDailyCheck").IsChecked = false;
                Named<CheckBox>(window, "UpdateDontRemindCheck").IsChecked = true;
                Settle(window);
                Assert.Equal(2, card.ChangedCount);
                Assert.Contains("Check for updates once a day", card.ChangedTip);
                Assert.Contains("Don't remind me about updates", card.ChangedTip);
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Closing_Settings_detaches_from_the_service()
    {
        var rig = new UpdateRig();
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var window = OpenSettings(rig, expand: true);
            window.Close();
            Settle(window);
            Assert.Null(window.Updates);
            var field = typeof(UpdateService).GetField("StatusChanged", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(field);
            Assert.Null(field!.GetValue(rig.Service));          // no handler left behind
            return Task.CompletedTask;
        });
    }

    // ================================================================ search and registry ========

    [Fact]
    public async Task Searching_for_update_finds_both_settings_and_the_check_action()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var window = OpenSettings(null);
            try
            {
                var panel = Named<StackPanel>(window, "SearchPanel");
                var runRebuild = typeof(SettingsWindow).GetMethod("RunSearchRebuild", BindingFlags.NonPublic | BindingFlags.Instance)!;
                foreach (var query in new[] { "update", "updates", "check for updates", "new version", "upgrade", "latest", "release", "github", "notify", "remind", "reminder", "auto update", "version" })
                {
                    runRebuild.Invoke(window, new object[] { query });
                    var texts = panel.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
                    Assert.True(texts.Contains("Check for updates once a day"), $"'{query}' did not find 'Check for updates once a day'");
                    Assert.True(texts.Contains("Don't remind me about updates"), $"'{query}' did not find 'Don't remind me about updates'");
                }
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void The_two_settings_are_catalogued_in_General_and_startup_with_the_requested_defaults_and_terms()
    {
        var daily = Assert.Single(Glide.Core.Settings.SettingsCatalog.All, s => s.Id == "updates.checkDaily");
        var dont = Assert.Single(Glide.Core.Settings.SettingsCatalog.All, s => s.Id == "updates.dontRemind");
        Assert.Equal("General & startup", daily.Category);
        Assert.Equal("General & startup", dont.Category);
        Assert.Equal(true, daily.DefaultValue);
        Assert.Equal(false, dont.DefaultValue);
        Assert.Equal("Check for updates once a day", daily.Label);
        Assert.Equal("Don't remind me about updates", dont.Label);
        foreach (var term in new[] { "update", "updates", "check for updates", "new version", "upgrade", "latest", "release", "github", "notify", "remind", "reminder", "auto update", "version" })
        {
            Assert.Contains(term, daily.SearchTerms);
            Assert.Contains(term, dont.SearchTerms);
        }
        // The state defaults match the catalogue.
        var state = new GlideSettingsState();
        Assert.True(state.UpdateCheckDaily);
        Assert.False(state.UpdateDontRemind);
        // Both are real toggles: hotkey / gesture bindable with a typed accessor.
        Assert.NotNull(AppActionRegistry.ForSetting("updates.checkDaily"));
        Assert.NotNull(AppActionRegistry.ForSetting("updates.dontRemind"));
        Assert.NotNull(SettingAccessors.Find("updates.checkDaily"));
        Assert.NotNull(SettingAccessors.Find("updates.dontRemind"));
    }

    [Fact]
    public void Check_for_updates_now_is_a_registered_searchable_action_with_no_default_key()
    {
        var action = AppActionRegistry.Find("app.checkUpdates")!;
        Assert.NotNull(action);
        Assert.Equal("Check for updates now", action.Label);
        Assert.Equal(GlideCommand.CheckForUpdates, action.Command);
        Assert.Equal(AppActionKind.Command, action.Kind);
        Assert.Equal(AppActionContexts.Everywhere, action.Contexts);
        Assert.Contains("update", action.SearchTerms);
        Assert.False(action.Destructive);
        Assert.Same(action, AppActionRegistry.ForCommand(GlideCommand.CheckForUpdates));
        var row = HotkeyCatalog.Find("app.checkUpdates")!;
        Assert.NotNull(row);
        Assert.Empty(row.DefaultShortcuts);                       // no default key
        Assert.Contains("update", (row.Label + " " + string.Join(" ", action.SearchTerms)).ToLowerInvariant());
        Assert.Empty(AppActionRegistry.Validate());
    }

    [Fact]
    public async Task The_action_is_mapped_in_the_production_command_dispatcher()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var window = new MainWindow();
            try { Assert.Contains(GlideCommand.CheckForUpdates, window.ProductionMappedCommandsForTests()); }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Check_for_updates_now_without_a_service_does_nothing_and_never_throws()
    {
        var before = UpdateService.Current;
        UpdateService.Install(null);
        try
        {
            await AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
            {
                var window = new MainWindow();
                try { await window.ExecuteActionAsync(AppActionRegistry.Find("app.checkUpdates")!, "test"); }
                finally { window.Close(); }
            });
        }
        finally { UpdateService.Install(before); }
    }

    // ================================================================ renders ========

    [Fact]
    public async Task Render_the_card_in_every_state()
    {
        var available = await RigWithAnswer(ReleaseJson("v5.0.1"), running: "5.0.0");
        var upToDate = await RigWithAnswer(ReleaseJson("v5.0.1"));
        var failed = await RigWithAnswer(null, fail: true);
        var notChecked = new UpdateRig();
        var checking = new UpdateRig();
        checking.Feed.Hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        checking.Feed.ThenJson(ReleaseJson("v5.0.1"));
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
        {
            async Task Render(UpdateRig rig, string name, bool startCheck = false)
            {
                var window = OpenSettings(rig, expand: true, width: 930, height: 560);
                try
                {
                    if (startCheck)
                    {
                        _ = rig.Service.CheckNowAsync();
                        await rig.Feed.Entered.Task;
                        Settle(window);
                    }
                    Save(Card(window), name + "-card.png");
                    Save(window, name + "-window.png");
                }
                finally { window.Close(); }
            }
            await Render(available, "settings-available");
            await Render(upToDate, "settings-up-to-date");
            await Render(failed, "settings-error");
            await Render(notChecked, "settings-not-checked");
            await Render(checking, "settings-checking", startCheck: true);
            checking.Feed.Hold!.SetResult(true);
        });
    }
}
