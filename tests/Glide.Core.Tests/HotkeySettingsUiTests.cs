using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Glide.App.Services;
using Glide.App.Settings;
using Glide.Core.Commands;

namespace Glide.Core.Tests;

/// <summary>
/// The Keyboard shortcuts page of the real Settings window (headless): sortable headers, the Replace / Cancel
/// prompt, "Search hotkey" capture, custom presets, and that tooltips cannot swallow the mouse wheel.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class HotkeySettingsUiTests
{
    public HotkeySettingsUiTests(AvaloniaHeadlessFixture fixture) { _ = fixture; }

    // ---- sort headers -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Clicking_headers_sorts_the_list_toggles_direction_and_shows_an_arrow()
    {
        await OnSettings(ctx =>
        {
            var defaultOrder = Rows(ctx.Window).Select(r => r.ActionId).ToList();
            Assert.Equal("Shortcut", Header(ctx.Window, "HotkeySortShortcutButton"));
            Assert.Equal("Action", Header(ctx.Window, "HotkeySortActionButton"));

            Click(ctx.Window, "HotkeySortActionButton");
            var asc = Rows(ctx.Window).Select(r => r.Label).ToList();
            Assert.Equal(asc.OrderBy(x => x, NaturalStringComparer.Instance).ToList(), asc);
            Assert.Equal("Action  ▲", Header(ctx.Window, "HotkeySortActionButton"));
            Assert.Equal("Shortcut", Header(ctx.Window, "HotkeySortShortcutButton"));

            Click(ctx.Window, "HotkeySortActionButton");
            var desc = Rows(ctx.Window).Select(r => r.Label).ToList();
            Assert.Equal(Enumerable.Reverse(asc).ToList(), desc);
            Assert.Equal("Action  ▼", Header(ctx.Window, "HotkeySortActionButton"));

            Click(ctx.Window, "HotkeySortCategoryButton");
            var categories = Rows(ctx.Window).Select(r => r.Category).ToList();
            Assert.Equal(categories.OrderBy(x => x, NaturalStringComparer.Instance).ToList(), categories);
            Assert.Equal("Category  ▲", Header(ctx.Window, "HotkeySortCategoryButton"));
            Assert.Equal("Action", Header(ctx.Window, "HotkeySortActionButton"));      // arrow moved to the new column

            Click(ctx.Window, "HotkeySortShortcutButton");
            var shortcuts = Rows(ctx.Window).Select(r => r.Shortcut).ToList();
            Assert.Equal("(unassigned)", shortcuts[^1]);                                  // unassigned rows go last
            var assigned = shortcuts.Where(s => s != "(unassigned)").ToList();
            Assert.Equal(assigned.OrderBy(x => x, NaturalStringComparer.Instance).ToList(), assigned);
            Assert.Equal(defaultOrder.Count, Rows(ctx.Window).Count);                      // sorting never drops rows
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Filter_matches_the_action_search_terms_so_explore_or_browse_finds_the_containing_folder_action()
    {
        await OnSettings(ctx =>
        {
            Invoke(ctx.Window, "BuildHotkeyRows", "browse");
            var rows = Rows(ctx.Window);
            Assert.Contains(rows, r => r.Label == "Explore/Browse containing folder");
            // The filter matches substrings of the search terms (5.0.x: generated setting actions such as
            // "Cycle: Show in Explorer button opens" carry "browse containing folder" and are listed too).
            Assert.All(rows, r => Assert.True(HotkeyCatalog.Find(r.ActionId)!.SearchTerms?.Any(t => t.Contains("browse", StringComparison.OrdinalIgnoreCase)) == true || r.Label.Contains("browse", StringComparison.OrdinalIgnoreCase) || r.Category.Contains("browse", StringComparison.OrdinalIgnoreCase)));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Global_settings_search_finds_the_renamed_containing_folder_action_by_each_search_term()
    {
        await OnSettings(ctx =>
        {
            var panel = ctx.Window.GetVisualDescendants().OfType<StackPanel>().First(p => p.Name == "SearchPanel");
            foreach (var term in new[] { "explore", "browse", "folder", "reveal", "show in explorer", "containing", "locate" })
            {
                Invoke(ctx.Window, "RunSearchRebuild", term);
                var labels = panel.Children.OfType<Border>().Select(b => b.Child is Grid g ? g.Children.OfType<StackPanel>().FirstOrDefault()?.Children.OfType<TextBlock>().FirstOrDefault()?.Text : null).ToList();
                Assert.True(labels.Contains("Explore/Browse containing folder"), $"search '{term}' did not list the action; got: {string.Join(" | ", labels.Take(12))}");
            }
            Assert.DoesNotContain(HotkeyCatalog.All, a => a.Label == "Explore containing folder");
            return Task.CompletedTask;
        });
    }

    // ---- Replace / Cancel prompt -------------------------------------------------------------------------------

    [Fact]
    public async Task Assigning_a_taken_shortcut_asks_names_the_owner_and_Cancel_changes_nothing()
    {
        await OnSettings(async ctx =>
        {
            var prompts = new List<string>();
            ctx.Window.ConfirmOverride = (title, text) => { prompts.Add(text); return Task.FromResult(false); };
            var before = Snapshot(ctx.Window);

            await Capture(ctx.Window, "view.zoomIn", "+", add: false, "F");     // "F" = Toggle fullscreen

            var prompt = Assert.Single(prompts);
            Assert.Contains("Toggle fullscreen", prompt);
            Assert.Contains("Zoom in", prompt);
            Assert.Equal(before, Snapshot(ctx.Window));
            Assert.Contains("Cancelled", Message(ctx.Window));
            Assert.Empty(ctx.Applied);                                           // nothing was previewed either
            return;
        });
    }

    [Fact]
    public async Task Replace_unassigns_the_old_owner_and_refreshes_the_list()
    {
        await OnSettings(async ctx =>
        {
            ctx.Window.ConfirmOverride = (_, _) => Task.FromResult(true);
            await Capture(ctx.Window, "view.zoomIn", null, add: true, "F");

            var map = Working(ctx.Window);
            Assert.Contains("F", map["view.zoomIn"]);
            Assert.DoesNotContain("F", map["view.fullscreen"]);
            Assert.Contains("removed it from Toggle fullscreen", Message(ctx.Window));
            Assert.DoesNotContain(Rows(ctx.Window), r => r.Shortcut == "F" && r.ActionId == "view.fullscreen");
            Assert.Contains(Rows(ctx.Window), r => r.Shortcut == "F" && r.ActionId == "view.zoomIn");
            Assert.NotEmpty(ctx.Applied);                                        // previewed like any other change
        });
    }

    [Fact]
    public async Task Re_assigning_a_shortcut_the_action_already_owns_does_not_prompt_or_change_anything()
    {
        await OnSettings(async ctx =>
        {
            var prompts = 0;
            ctx.Window.ConfirmOverride = (_, _) => { prompts++; return Task.FromResult(true); };
            var before = Snapshot(ctx.Window);
            await Capture(ctx.Window, "view.fullscreen", null, add: true, "F11");
            await Capture(ctx.Window, "view.fullscreen", "F", add: false, "F");

            Assert.Equal(0, prompts);
            Assert.Equal(before, Snapshot(ctx.Window));
            Assert.Contains("Nothing changed", Message(ctx.Window));
        });
    }

    [Fact]
    public async Task A_free_shortcut_is_assigned_immediately_without_a_prompt()
    {
        await OnSettings(async ctx =>
        {
            var prompts = 0;
            ctx.Window.ConfirmOverride = (_, _) => { prompts++; return Task.FromResult(true); };
            await Capture(ctx.Window, "view.zoomIn", "+", add: false, "Ctrl+Alt+Z");
            Assert.Equal(0, prompts);
            Assert.Equal(new[] { "=", "Ctrl+Alt+Z" }, Working(ctx.Window)["view.zoomIn"]);
        });
    }

    [Fact]
    public async Task Cross_scope_conflicts_say_where_the_old_owner_works()
    {
        await OnSettings(async ctx =>
        {
            var prompts = new List<string>();
            ctx.Window.ConfirmOverride = (_, text) => { prompts.Add(text); return Task.FromResult(false); };
            await Capture(ctx.Window, "view.zoomIn", null, add: true, "Ctrl+T");   // viewer action takes a global key
            await Capture(ctx.Window, "tab.new", null, add: true, "F");            // global action takes a viewer key
            Assert.Contains("every tab", prompts[0]);
            Assert.Contains("only works while viewing", prompts[1]);
        });
    }

    // ---- Search hotkey ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Search_hotkey_shows_the_action_for_the_pressed_combination_then_returns_to_normal()
    {
        await OnSettings(ctx =>
        {
            var full = Rows(ctx.Window).Count;
            Click(ctx.Window, "SearchHotkeyButton");
            Assert.True(ctx.Window.IsHotkeyLookupCapturing);
            Assert.Equal("Press a key...", ButtonText(ctx.Window, "SearchHotkeyButton"));
            Assert.False(ctx.Window.FindControl<TextBox>("HotkeyFilterBox")!.IsEnabled);

            ctx.Window.KeyPress(Key.P, RawInputModifiers.Control, PhysicalKey.P, "p");

            Assert.False(ctx.Window.IsHotkeyLookupCapturing);
            Assert.Equal("Search hotkey", ButtonText(ctx.Window, "SearchHotkeyButton"));
            Assert.True(ctx.Window.FindControl<TextBox>("HotkeyFilterBox")!.IsEnabled);
            var row = Assert.Single(Rows(ctx.Window));
            Assert.Equal("Ctrl+P", row.Shortcut);
            Assert.Equal("Print current image", row.Label);
            Assert.Equal("Ctrl+P is assigned to: Print current image (File).", Message(ctx.Window));
            Assert.True(ctx.Window.FindControl<Button>("ClearHotkeyLookupButton")!.IsVisible);
            Assert.Empty(ctx.Applied);                                                // looking up never changes anything

            Click(ctx.Window, "ClearHotkeyLookupButton");                             // back to the normal list
            Assert.Equal(full, Rows(ctx.Window).Count);
            Assert.False(ctx.Window.FindControl<Button>("ClearHotkeyLookupButton")!.IsVisible);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Search_hotkey_says_clearly_when_the_key_is_not_assigned()
    {
        await OnSettings(ctx =>
        {
            ctx.Window.BeginHotkeyLookupCapture();
            ctx.Window.KeyPress(Key.Q, RawInputModifiers.Control | RawInputModifiers.Alt, PhysicalKey.Q, "q");
            Assert.Empty(Rows(ctx.Window));
            Assert.Equal("Ctrl+Alt+Q is not assigned to any action.", Message(ctx.Window));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Search_hotkey_ignores_lone_modifiers_and_waits_for_the_real_key()
    {
        await OnSettings(ctx =>
        {
            ctx.Window.BeginHotkeyLookupCapture();
            ctx.Window.KeyPress(Key.LeftCtrl, RawInputModifiers.Control, PhysicalKey.ControlLeft, null);
            Assert.True(ctx.Window.IsHotkeyLookupCapturing, "a modifier alone must not end the capture");
            ctx.Window.KeyPress(Key.Left, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.ArrowLeft, null);
            Assert.False(ctx.Window.IsHotkeyLookupCapturing);
            Assert.Contains("Ctrl+Shift+Left", Message(ctx.Window));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Escape_cancels_the_hotkey_search_without_closing_Settings_or_touching_the_list()
    {
        await OnSettings(ctx =>
        {
            var before = Rows(ctx.Window).Count;
            ctx.Window.BeginHotkeyLookupCapture();
            ctx.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);

            Assert.False(ctx.Window.IsHotkeyLookupCapturing);
            Assert.True(ctx.Window.IsVisible, "Esc during capture must cancel the capture only, not close Settings");
            Assert.Equal(before, Rows(ctx.Window).Count);
            Assert.Equal("Hotkey search cancelled.", Message(ctx.Window));
            Assert.True(ctx.Window.FindControl<TextBox>("HotkeyFilterBox")!.IsEnabled);
            Assert.False(ctx.Window.FindControl<Button>("ClearHotkeyLookupButton")!.IsVisible);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Captured_keys_never_trigger_buttons_commands_or_edits()
    {
        await OnSettings(ctx =>
        {
            var before = Snapshot(ctx.Window);
            foreach (var (key, physical) in new[] { (Key.Enter, PhysicalKey.Enter), (Key.Space, PhysicalKey.Space), (Key.Delete, PhysicalKey.Delete), (Key.Tab, PhysicalKey.Tab), (Key.F5, PhysicalKey.F5) })
            {
                ctx.Window.BeginHotkeyLookupCapture();
                ctx.Window.KeyPress(key, RawInputModifiers.None, physical, null);
                Assert.False(ctx.Window.IsHotkeyLookupCapturing, $"{key} should have been captured");
                Assert.True(ctx.Window.IsVisible);
            }
            // Space / Enter did not "click" the Search hotkey button again (it would have restarted the capture).
            Assert.False(ctx.Window.IsHotkeyLookupCapturing);
            Assert.Equal(before, Snapshot(ctx.Window));
            Assert.Empty(ctx.Applied);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Typing_in_the_filter_leaves_the_search_hotkey_result_view()
    {
        await OnSettings(ctx =>
        {
            var full = Rows(ctx.Window).Count;
            ctx.Window.BeginHotkeyLookupCapture();
            ctx.Window.KeyPress(Key.P, RawInputModifiers.Control, PhysicalKey.P, "p");
            Assert.Single(Rows(ctx.Window));

            ctx.Window.FindControl<TextBox>("HotkeyFilterBox")!.Text = "slideshow";
            // TextChanged is dispatched; let it run (it leaves the lookup view) before the debounce tick fires.
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Invoke(ctx.Window, "HotkeyFilterTick", null, EventArgs.Empty);
            var rows = Rows(ctx.Window);
            Assert.True(rows.Count > 1 && rows.Count < full, $"{rows.Count} of {full} rows: {string.Join(" | ", rows.Take(5).Select(r => r.Label))}");
            // A row matches by label, category or search terms (5.0.x: generated setting toggles such as
            // "Toggle: Shuffle" carry the setting's "slideshow" search term).
            Assert.All(rows, r => Assert.True((r.Label + r.Category).Contains("lideshow", StringComparison.Ordinal) ||
                HotkeyCatalog.Find(r.ActionId)?.SearchTerms?.Any(t => t.Contains("slideshow", StringComparison.OrdinalIgnoreCase)) == true, r.Label));
            return Task.CompletedTask;
        });
    }

    // ---- custom presets ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Custom_presets_can_be_saved_applied_renamed_and_deleted_next_to_the_built_ins()
    {
        await OnSettings(async ctx =>
        {
            var w = ctx.Window;
            w.ConfirmOverride = (_, _) => Task.FromResult(true);
            var names = new Queue<string?>(new string?[] { "Travel keys", "Home keys" });
            w.PresetNamePromptOverride = (_, _) => Task.FromResult(names.Dequeue());

            // Edit the mapping, then save it as a preset.
            await Capture(w, "view.zoomIn", null, add: true, "Ctrl+Alt+Z");
            await w.SaveHotkeyPresetAsync();
            Assert.Equal(HotkeyCatalog.PresetNames.Concat(new[] { "Travel keys" }), PresetItems(w));
            Assert.Equal("Travel keys", SelectedPreset(w));
            Assert.Equal("Travel keys", Assert.Single(StagedState(w).CustomHotkeyPresets).Name);       // staged for Apply / profile export
            Assert.True(w.FindControl<Button>("RenameHotkeyPresetButton")!.IsEnabled);

            // Reset to the Glide defaults, then apply the saved preset to get the edit back.
            Invoke(w, "ResetHotkeysClicked", null, new RoutedEventArgs());
            Assert.DoesNotContain("Ctrl+Alt+Z", Working(w)["view.zoomIn"]);
            Invoke(w, "LoadHotkeyPresetClicked", null, new RoutedEventArgs());
            Assert.Contains("Ctrl+Alt+Z", Working(w)["view.zoomIn"]);

            // Rename keeps the contents.
            await w.RenameHotkeyPresetAsync();
            Assert.Equal(HotkeyCatalog.PresetNames.Concat(new[] { "Home keys" }), PresetItems(w));
            Assert.Equal(new[] { "=", "+", "Ctrl+Alt+Z" }.OrderBy(x => x), StagedState(w).CustomHotkeyPresets[0].Hotkeys["view.zoomIn"].OrderBy(x => x));

            // Delete removes it again, the built-ins stay.
            await w.DeleteHotkeyPresetAsync();
            Assert.Equal(HotkeyCatalog.PresetNames, PresetItems(w));
            Assert.Empty(StagedState(w).CustomHotkeyPresets);
            Assert.Equal("Glide default", SelectedPreset(w));
        });
    }

    [Fact]
    public async Task Built_in_presets_cannot_be_renamed_or_deleted_from_the_page()
    {
        await OnSettings(async ctx =>
        {
            var w = ctx.Window;
            var prompted = 0;
            w.ConfirmOverride = (_, _) => { prompted++; return Task.FromResult(true); };
            w.PresetNamePromptOverride = (_, _) => { prompted++; return Task.FromResult<string?>("X"); };
            foreach (var name in HotkeyCatalog.PresetNames)
            {
                w.FindControl<ComboBox>("HotkeyPresetCombo")!.SelectedIndex = HotkeyCatalog.PresetNames.ToList().IndexOf(name);
                Assert.False(w.FindControl<Button>("RenameHotkeyPresetButton")!.IsEnabled, name);
                Assert.False(w.FindControl<Button>("DeleteHotkeyPresetButton")!.IsEnabled, name);
                await w.DeleteHotkeyPresetAsync();
                await w.RenameHotkeyPresetAsync();
                Assert.Equal(HotkeyCatalog.PresetNames.Count, PresetItems(w).Count);
            }
            Assert.Equal(0, prompted);
        });
    }

    [Fact]
    public async Task Saving_over_an_existing_name_asks_before_replacing_and_a_built_in_name_is_refused()
    {
        await OnSettings(async ctx =>
        {
            var w = ctx.Window;
            var confirms = new List<string>();
            var answer = true;
            w.ConfirmOverride = (title, text) => { confirms.Add(title); return Task.FromResult(answer); };
            var next = "Mine";
            w.PresetNamePromptOverride = (_, _) => Task.FromResult<string?>(next);

            await w.SaveHotkeyPresetAsync();                           // creates "Mine"
            Assert.Empty(confirms);
            await Capture(w, "view.zoomIn", null, add: true, "Ctrl+Alt+Z");

            answer = false;
            await w.SaveHotkeyPresetAsync();                           // same name, declined: keeps the old snapshot
            Assert.Single(confirms);
            Assert.DoesNotContain("Ctrl+Alt+Z", StagedState(w).CustomHotkeyPresets[0].Hotkeys["view.zoomIn"]);

            answer = true;
            await w.SaveHotkeyPresetAsync();                           // confirmed: overwritten
            Assert.Contains("Ctrl+Alt+Z", StagedState(w).CustomHotkeyPresets[0].Hotkeys["view.zoomIn"]);

            next = "IrfanView";
            await w.SaveHotkeyPresetAsync();
            Assert.Single(StagedState(w).CustomHotkeyPresets);
            Assert.Contains("built-in", Message(w));
        });
    }

    [Fact]
    public async Task Opening_Settings_with_saved_presets_lists_them_after_the_built_ins()
    {
        var state = new GlideSettingsState();
        HotkeyPresetLibrary.Save(state.CustomHotkeyPresets, "Saved earlier", HotkeyCatalog.CreateDefaultMap(), out _);
        await OnSettings(ctx =>
        {
            Assert.Equal(HotkeyCatalog.PresetNames.Concat(new[] { "Saved earlier" }), PresetItems(ctx.Window));
            return Task.CompletedTask;
        }, state);
    }

    // ---- Settings editors for this feedback batch ----------------------------------------------------------------

    [Fact]
    public async Task Auto_hide_checkbox_round_trips_through_the_settings_window()
    {
        await OnSettings(ctx =>
        {
            var box = ctx.Window.FindControl<CheckBox>("StatusAutoHideCheck")!;
            Assert.False(box.IsChecked);
            box.IsChecked = true;
            Assert.True(StagedState(ctx.Window).StatusAutoHide);
            box.IsChecked = false;
            Assert.False(StagedState(ctx.Window).StatusAutoHide);
            return Task.CompletedTask;
        });
        await OnSettings(ctx =>
        {
            Assert.True(ctx.Window.FindControl<CheckBox>("StatusAutoHideCheck")!.IsChecked);
            return Task.CompletedTask;
        }, new GlideSettingsState { StatusAutoHide = true });
    }

    [Fact]
    public async Task Reveal_area_adjustment_editor_round_trips_and_is_clamped_through_the_settings_window()
    {
        await OnSettings(ctx =>
        {
            var box = ctx.Window.FindControl<NumericUpDown>("StatusRevealZoneExpandBox")!;
            Assert.Equal(0m, box.Value);                                     // default: exactly the status bar's box
            Assert.Equal(-40m, box.Minimum);
            Assert.Equal(300m, box.Maximum);
            Assert.Equal(5m, box.Increment);
            box.Value = 60;
            Assert.Equal(60, StagedState(ctx.Window).StatusRevealZoneExpand);
            box.Value = -25;
            Assert.Equal(-25, StagedState(ctx.Window).StatusRevealZoneExpand);
            // The hover reveal checkbox sits in the same card and is on by default.
            Assert.True(ctx.Window.FindControl<CheckBox>("StatusHoverWhenClosedCheck")!.IsChecked);
            Assert.True(StagedState(ctx.Window).StatusShowOnHoverWhenClosed);
            return Task.CompletedTask;
        });
        await OnSettings(ctx =>
        {
            Assert.Equal(-30m, ctx.Window.FindControl<NumericUpDown>("StatusRevealZoneExpandBox")!.Value);
            Assert.False(ctx.Window.FindControl<CheckBox>("StatusHoverWhenClosedCheck")!.IsChecked);
            return Task.CompletedTask;
        }, new GlideSettingsState { StatusRevealZoneExpand = -30, StatusShowOnHoverWhenClosed = false });
    }

    [Fact]
    public async Task Imported_profile_state_keeps_good_presets_and_drops_unusable_ones()
    {
        await OnSettings(ctx =>
        {
            var state = new GlideSettingsState();
            state.CustomHotkeyPresets.Add(new CustomHotkeyPreset { Name = "Keep me", Hotkeys = new(StringComparer.OrdinalIgnoreCase) { ["view.zoomIn"] = new() { "Z" } } });
            state.CustomHotkeyPresets.Add(new CustomHotkeyPreset { Name = "IrfanView" });      // built-in name: unusable
            state.CustomHotkeyPresets.Add(new CustomHotkeyPreset { Name = "   " });            // blank
            typeof(SettingsWindow).GetMethod("NormalizeImportedState", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { state });
            Assert.Equal(new[] { "Keep me" }, state.CustomHotkeyPresets.Select(p => p.Name));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Keyboard_shortcuts_card_renders_for_a_visual_check()
    {
        var save = Environment.GetEnvironmentVariable("GLIDE_SAVE_RENDERS");
        if (string.IsNullOrWhiteSpace(save)) return;      // only produces PNGs when GLIDE_SAVE_RENDERS is set
        await OnSettings(ctx =>
        {
            var w = ctx.Window;
            ApplyAppStyles(w);
            w.Width = 1100; w.Height = 900;
            Invoke(w, "RevealSetting", "hotkeys.registry");
            Dispatcher.UIThread.RunJobs();
            Click(w, "HotkeySortActionButton");
            Click(w, "HotkeySortActionButton");        // descending: shows the ▼ arrow
            Dispatcher.UIThread.RunJobs();
            w.UpdateLayout();
            void Shot(string name)
            {
                Dispatcher.UIThread.RunJobs();
                w.UpdateLayout();
                var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)w.Bounds.Width, (int)w.Bounds.Height));
                bitmap.Render(w);
                Directory.CreateDirectory(save!);
                bitmap.Save(Path.Combine(save!, name));
            }
            Shot("settings-hotkeys-sorted-by-action-desc.png");
            ctx.Window.BeginHotkeyLookupCapture();
            Shot("settings-hotkeys-search-hotkey-waiting.png");
            ctx.Window.KeyPress(Key.P, RawInputModifiers.Control, PhysicalKey.P, "p");
            Shot("settings-hotkeys-search-hotkey-result.png");
            return Task.CompletedTask;
        });
    }

    // ---- tooltips and the mouse wheel ---------------------------------------------------------------------------

    [Fact]
    public void Hotkey_row_tooltips_open_beside_the_pointer_not_under_it()
    {
        // Rows of a scrolling list (the hotkey list rows are ListBoxItems) open their tooltip beside the pointer, clear of
        // the cursor glyph; ordinary controls keep the anchored placement above / below the control.
        var rowPlacement = ToolTipGuard.Placement(insideScrollingList: true, lowerHalf: true);
        Assert.Equal(PlacementMode.Pointer, rowPlacement.Mode);
        Assert.True(rowPlacement.HorizontalOffset >= 12 && rowPlacement.VerticalOffset >= 24, "the tooltip must clear the cursor glyph");
        Assert.Equal(rowPlacement, ToolTipGuard.Placement(insideScrollingList: true, lowerHalf: false));
        Assert.Equal(PlacementMode.Top, ToolTipGuard.Placement(false, true).Mode);
        Assert.Equal(PlacementMode.Bottom, ToolTipGuard.Placement(false, false).Mode);

        AvaloniaHeadlessFixture.RunOnUIThread(() =>
        {
            var row = new ListBoxItem { Content = "row" };
            var cellInRow = new TextBlock { Text = "x" };
            row.Content = cellInRow;
            Assert.True(ToolTipGuard.IsInsideScrollingList(row));
            Assert.False(ToolTipGuard.IsInsideScrollingList(new Button()));
            ToolTipGuard.CloseOpenToolTips(row);               // nothing open: a harmless no-op
            ToolTipGuard.CloseOpenToolTips(null);
        });
    }

    [Fact]
    public void Tooltip_wheel_guard_is_wired_at_application_start_and_closes_tooltips_before_the_wheel_is_handled()
    {
        var guard = ReadSource("src", "Glide.App", "Services", "ToolTipGuard.cs");
        // Tunnel + handledEventsToo so the tooltip is gone before the list / scroll viewer consumes the tick.
        Assert.Contains("InputElement.PointerWheelChangedEvent.AddClassHandler<Control>((control, _) => CloseOpenToolTips(control)", guard, StringComparison.Ordinal);
        Assert.Contains("Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true", guard, StringComparison.Ordinal);
        Assert.Contains("ToolTip.SetIsOpen(control, false)", guard, StringComparison.Ordinal);
        var app = ReadSource("src", "Glide.App", "App.axaml.cs");
        Assert.Contains("ToolTipGuard.Register();", app, StringComparison.Ordinal);
        Assert.DoesNotContain("RegisterToolTipPlacement", app, StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(segments).ToArray());
            if (File.Exists(candidate)) return SourceText.Read(candidate);
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Could not locate source file.", Path.Combine(segments));
    }

    [Fact]
    public async Task The_tooltip_popup_itself_is_not_hit_testable_with_the_app_styles()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var tip = new ToolTip { Content = "hello" };
            var window = new Window { Width = 200, Height = 100, Content = tip };
            ApplyAppStyles(window);
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                Assert.False(tip.IsHitTestVisible, "ToolTip style in GlideTheme.axaml must make tooltips click- and wheel-through");
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    // ---- helpers ---------------------------------------------------------------------------------------------

    private sealed record Context(SettingsWindow Window, List<GlideSettingsState> Applied);

    private static async Task OnSettings(Func<Context, Task> body, GlideSettingsState? initial = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "Glide.Settings.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
        {
            var previous = SettingsStore.SettingsDirectoryOverrideForTests.Value;
            SettingsStore.SettingsDirectoryOverrideForTests.Value = root;
            var applied = new List<GlideSettingsState>();
            var window = new SettingsWindow(initial ?? new GlideSettingsState(), (state, _) => applied.Add(state));
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                Invoke(window, "BuildHotkeyRows", (object?)null);
                Dispatcher.UIThread.RunJobs();
                applied.Clear();
                await body(new Context(window, applied));
            }
            finally
            {
                SetField(window, "_closeWithoutRollback", true);
                window.Close();
                SettingsStore.SettingsDirectoryOverrideForTests.Value = previous;
            }
        });
        try { Directory.Delete(root, true); } catch { }
    }

    private static void ApplyAppStyles(Window window)
    {
        window.RequestedThemeVariant = ThemeVariant.Dark;
        window.Styles.Insert(0, new FluentTheme());
        window.Styles.Insert(1, new StyleInclude(new Uri("avares://Glide/")) { Source = new Uri("avares://Glide/Styles/GlideTheme.axaml") });
    }

    private sealed record Row(string ActionId, string Shortcut, string Label, string Category);

    private static List<Row> Rows(SettingsWindow window)
    {
        var list = window.FindControl<ListBox>("HotkeyList")!;
        var rows = new List<Row>();
        foreach (var item in (IEnumerable<ListBoxItem>)list.ItemsSource!)
        {
            var grid = (Grid)item.Content!;
            var texts = grid.Children.OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
            var tag = item.Tag!;
            var id = (string)tag.GetType().GetProperty("ActionId")!.GetValue(tag)!;
            rows.Add(new Row(id, texts[0], texts[1], texts[2]));
        }
        return rows;
    }

    private static string Header(SettingsWindow window, string buttonName) => ButtonText(window, buttonName);
    private static string ButtonText(SettingsWindow window, string name) => window.FindControl<Button>(name)!.Content?.ToString() ?? "";
    private static string Message(SettingsWindow window) => window.FindControl<TextBlock>("HotkeyCaptureText")!.Text ?? "";

    private static void Click(SettingsWindow window, string buttonName) =>
        window.FindControl<Button>(buttonName)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static Dictionary<string, List<string>> Working(SettingsWindow window) =>
        (Dictionary<string, List<string>>)GetField(window, "_workingHotkeys")!;

    private static string Snapshot(SettingsWindow window) =>
        string.Join(";", Working(window).OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + string.Join(",", kv.Value)));

    private static GlideSettingsState StagedState(SettingsWindow window) => (GlideSettingsState)Invoke(window, "ReadControls")!;

    private static List<string> PresetItems(SettingsWindow window) =>
        window.FindControl<ComboBox>("HotkeyPresetCombo")!.Items.OfType<ComboBoxItem>().Select(i => i.Content?.ToString() ?? "").ToList();

    private static string SelectedPreset(SettingsWindow window) =>
        (window.FindControl<ComboBox>("HotkeyPresetCombo")!.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";

    /// <summary>Starts a capture for the action exactly like double-click / Change / Add, then delivers the captured shortcut.</summary>
    private static async Task Capture(SettingsWindow window, string actionId, string? oldShortcut, bool add, string shortcut)
    {
        Invoke(window, "StartHotkeyCaptureFor", actionId, oldShortcut, add, false, null);
        await window.AssignCapturedShortcutAsync(shortcut);
    }

    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

    private static object? Invoke(SettingsWindow window, string method, params object?[] args)
    {
        var info = typeof(SettingsWindow).GetMethod(method, Flags);
        Assert.NotNull(info);
        return info!.Invoke(window, args);
    }

    private static object? GetField(SettingsWindow window, string name)
    {
        var field = typeof(SettingsWindow).GetField(name, Flags);
        Assert.NotNull(field);
        return field!.GetValue(window);
    }

    private static void SetField(SettingsWindow window, string name, object? value)
    {
        var field = typeof(SettingsWindow).GetField(name, Flags);
        Assert.NotNull(field);
        field!.SetValue(window, value);
    }
}
