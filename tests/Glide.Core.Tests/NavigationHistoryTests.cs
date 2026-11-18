using Glide.Core.Commands;
using Glide.Core.Navigation;

namespace Glide.Core.Tests;

/// <summary>The framework-free per-tab Back / Forward history (5.0.0, docs/NAVIGATION-HISTORY.md).</summary>
[Trait("Area", "Navigation")]
public sealed class NavigationHistoryTests
{
    private const string Folder = @"C:\Pictures";
    private const string Sub = @"C:\Pictures\Trip";
    private static NavigationEntry Image(string name) => NavigationEntry.Image(Path.Combine(Folder, name));
    private static NavigationEntry? Same(NavigationEntry entry) => entry;

    [Fact]
    public void A_new_history_is_empty_and_cannot_go_anywhere()
    {
        var history = new NavigationHistory();
        Assert.Null(history.Current);
        Assert.Equal(-1, history.Index);
        Assert.False(history.CanGoBack);
        Assert.False(history.CanGoForward);
        Assert.Null(history.Back());
        Assert.Null(history.Forward());
        Assert.Null(history.Step(-1, Same, out var skipped));
        Assert.Equal(0, skipped);
    }

    [Fact]
    public void Push_adds_steps_and_back_forward_move_between_them()
    {
        var history = new NavigationHistory();
        history.Push(NavigationEntry.Home());
        history.Push(NavigationEntry.Explorer(Folder));
        history.Push(Image("a.png"));
        Assert.Equal(3, history.Count);
        Assert.True(history.CanGoBack);
        Assert.False(history.CanGoForward);

        Assert.Equal(NavigationEntryKind.Explorer, history.Back()!.Kind);
        Assert.True(history.CanGoForward);
        Assert.Equal(NavigationEntryKind.Home, history.Back()!.Kind);
        Assert.False(history.CanGoBack);
        Assert.Equal(NavigationEntryKind.Explorer, history.Forward()!.Kind);
        Assert.Equal(Image("a.png"), history.Forward());
    }

    [Fact]
    public void Pushing_after_going_back_drops_the_forward_entries_like_a_browser()
    {
        var history = new NavigationHistory();
        history.Push(NavigationEntry.Explorer(Folder));
        history.Push(NavigationEntry.Explorer(Sub));
        history.Push(Image("a.png"));
        history.Back();
        history.Back();
        history.Push(NavigationEntry.Explorer(@"C:\Other"));
        Assert.Equal(new[] { Folder, @"C:\Other" }, history.Entries.Select(e => e.Path));
        Assert.False(history.CanGoForward);
    }

    [Fact]
    public void Pushing_the_current_place_again_replaces_it_instead_of_adding_a_duplicate()
    {
        var history = new NavigationHistory();
        history.Push(NavigationEntry.Explorer(Folder, selectedPath: Path.Combine(Folder, "a.png")));
        history.Push(NavigationEntry.Explorer(Folder + "\\", selectedPath: Path.Combine(Folder, "b.png")));   // trailing separator, same folder
        Assert.Equal(1, history.Count);
        Assert.Equal(Path.Combine(Folder, "b.png"), history.Current!.SelectedPath);
    }

    [Fact]
    public void Image_to_image_is_replaced_in_place_and_is_never_a_history_step()
    {
        var history = new NavigationHistory();
        Assert.True(history.Record(NavigationEntry.Explorer(Folder)));
        Assert.True(history.Record(Image("a.png")));
        Assert.False(history.Record(Image("b.png")));      // arrow key
        Assert.False(history.Record(Image("c.png")));      // wheel / slideshow / Open file in the same tab
        Assert.Equal(2, history.Count);
        Assert.Equal(Image("c.png"), history.Current);
        Assert.Equal(NavigationEntryKind.Explorer, history.Back()!.Kind);
    }

    [Fact]
    public void Record_keeps_the_same_place_and_its_remembered_details()
    {
        var history = new NavigationHistory();
        history.Push(NavigationEntry.Explorer(Folder, Path.Combine(Folder, "a.png"), scrollY: 320, viewMode: 2));
        Assert.False(history.Record(NavigationEntry.Explorer(Folder)));
        Assert.Equal(Path.Combine(Folder, "a.png"), history.Current!.SelectedPath);
        Assert.Equal(320, history.Current.ScrollY);
        Assert.Equal(2, history.Current.ViewMode);
    }

    [Fact]
    public void Replace_and_UpdateCurrent_change_the_current_entry_only()
    {
        var history = new NavigationHistory();
        history.Replace(NavigationEntry.Explorer(Folder));        // first entry
        history.Push(Image("a.png"));
        history.Back();
        history.UpdateCurrent(e => e with { SelectedPath = Path.Combine(Folder, "a.png"), ScrollY = 40 });
        Assert.Equal(2, history.Count);
        Assert.True(history.CanGoForward);
        Assert.Equal(40, history.Entries[0].ScrollY);
        history.Replace(NavigationEntry.Explorer(Sub));
        Assert.Equal(Sub, history.Entries[0].Path);
        Assert.Equal(Image("a.png"), history.Entries[1]);
    }

    [Fact]
    public void The_history_keeps_at_most_its_capacity_dropping_the_oldest()
    {
        var history = new NavigationHistory(capacity: 100);
        for (var i = 0; i < 150; i++) history.Push(NavigationEntry.Explorer(@"C:\F" + i));
        Assert.Equal(100, history.Count);
        Assert.Equal(99, history.Index);
        Assert.Equal(@"C:\F50", history.Entries[0].Path);
        Assert.Equal(@"C:\F149", history.Current!.Path);
        Assert.Equal(NavigationHistory.DefaultCapacity, new NavigationHistory().Capacity);
    }

    [Fact]
    public void Step_skips_and_removes_images_that_are_gone_and_continues_in_the_same_direction()
    {
        var history = new NavigationHistory();
        history.Push(NavigationEntry.Explorer(Folder));
        history.Push(Image("gone1.png"));
        history.Push(NavigationEntry.Explorer(Sub));
        history.Push(Image("gone2.png"));
        history.Push(NavigationEntry.Home());
        NavigationEntry? Resolve(NavigationEntry e) => e.Path?.Contains("gone", StringComparison.Ordinal) == true ? null : e;

        var back = history.Step(-1, Resolve, out var skipped);
        Assert.Equal(Sub, back!.Path);
        Assert.Equal(1, skipped);
        back = history.Step(-1, Resolve, out skipped);
        Assert.Equal(Folder, back!.Path);
        Assert.Equal(1, skipped);
        Assert.Equal(new[] { NavigationEntryKind.Explorer, NavigationEntryKind.Explorer, NavigationEntryKind.Home }, history.Entries.Select(e => e.Kind));
        Assert.Equal(0, history.Index);

        var forward = history.Step(1, Resolve, out skipped);
        Assert.Equal(Sub, forward!.Path);
        Assert.Equal(0, skipped);
    }

    [Fact]
    public void Step_with_nothing_left_keeps_the_current_entry_and_reports_the_dead_ones()
    {
        var history = new NavigationHistory();
        history.Push(Image("gone.png"));
        history.Push(NavigationEntry.Explorer(Folder));
        var result = history.Step(-1, e => e.Kind == NavigationEntryKind.Image ? null : e, out var skipped);
        Assert.Null(result);
        Assert.Equal(1, skipped);
        Assert.Equal(Folder, history.Current!.Path);
        Assert.False(history.CanGoBack);
    }

    [Fact]
    public void A_folder_that_resolves_to_its_parent_replaces_the_entry_and_a_duplicate_neighbour_is_merged()
    {
        var history = new NavigationHistory();
        history.Push(NavigationEntry.Explorer(Folder));
        history.Push(NavigationEntry.Explorer(Sub, Path.Combine(Sub, "x.png"), 500));
        history.Push(Image("a.png"));
        var back = history.Step(-1, e => e.Path == Sub ? NavigationEntry.Explorer(Folder) : e, out var skipped);
        Assert.Equal(Folder, back!.Path);
        Assert.Equal(0, skipped);
        Assert.Equal(2, history.Count);   // [Folder, a.png]: the two identical folder entries became one
        Assert.Equal(0, history.Index);
        Assert.True(history.CanGoForward);
    }

    [Fact]
    public void GoTo_jumps_to_an_entry_and_removes_a_dead_one()
    {
        var history = new NavigationHistory();
        history.Push(NavigationEntry.Home());
        history.Push(NavigationEntry.Explorer(Folder));
        history.Push(Image("gone.png"));
        history.Push(NavigationEntry.Explorer(Sub));
        Assert.Null(history.GoTo(2, e => e.Kind == NavigationEntryKind.Image ? null : e));
        Assert.Equal(3, history.Count);
        Assert.Equal(Sub, history.Current!.Path);
        Assert.Equal(NavigationEntryKind.Home, history.GoTo(0, Same)!.Kind);
        Assert.Equal(0, history.Index);
        Assert.Null(history.GoTo(9, Same));
    }

    [Fact]
    public void Snapshot_and_FromState_round_trip_for_a_tab_moved_to_another_window()
    {
        var history = new NavigationHistory();
        history.Push(NavigationEntry.Explorer(Folder, Path.Combine(Folder, "a.png"), 120, 3));
        history.Push(Image("a.png"));
        history.Back();
        var copy = NavigationHistory.FromState(history.Snapshot());
        Assert.Equal(history.Entries, copy.Entries);
        Assert.Equal(0, copy.Index);
        Assert.True(copy.CanGoForward);
        copy.Forward();
        Assert.Equal(0, history.Index);   // independent copies
        Assert.Equal(-1, NavigationHistory.FromState(null).Index);
    }

    [Fact]
    public void The_resolver_follows_the_session_restore_fallback()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Pictures\a.png" };
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\", @"C:\Pictures" };
        NavigationEntry? Resolve(NavigationEntry e) => NavigationTargetResolver.Resolve(e, files.Contains, folders.Contains);

        Assert.Equal(NavigationEntry.Home(), Resolve(NavigationEntry.Home()));
        Assert.Equal(Image("a.png"), Resolve(Image("a.png")));
        Assert.Null(Resolve(Image("missing.png")));                                   // an image that is gone is skipped
        var kept = Resolve(NavigationEntry.Explorer(Folder, @"C:\Pictures\a.png", 80, 1))!;
        Assert.Equal(@"C:\Pictures\a.png", kept.SelectedPath);
        Assert.Equal(80, kept.ScrollY);
        Assert.Null(Resolve(NavigationEntry.Explorer(Folder, @"C:\Pictures\deleted.png"))!.SelectedPath);   // folder opens, no selection
        var parent = Resolve(NavigationEntry.Explorer(@"C:\Pictures\Trip\Day 1", @"C:\Pictures\Trip\Day 1\x.png", 900, 2))!;
        Assert.Equal(Folder, parent.Path);                                             // nearest existing parent
        Assert.Null(parent.SelectedPath);
        Assert.Equal(0, parent.ScrollY);
        Assert.Equal(2, parent.ViewMode);
        Assert.Null(NavigationTargetResolver.Resolve(NavigationEntry.Explorer(@"Q:\nothing\here"), files.Contains, folders.Contains));
    }

    [Fact]
    public void Paths_compare_like_windows()
    {
        Assert.True(NavigationEntry.Explorer(@"c:\pictures").SameLocation(NavigationEntry.Explorer(@"C:\Pictures\")));
        Assert.False(NavigationEntry.Explorer(Folder).SameLocation(Image("a.png")));
        Assert.False(NavigationEntry.Home().SameLocation(null));
        Assert.True(NavigationEntry.Home().SameLocation(NavigationEntry.Home()));
        Assert.Equal(0, NavigationEntry.Explorer(Folder, scrollY: double.NaN).ScrollY);
    }

    // ---- registry ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Back_and_Forward_are_registry_actions_with_their_defaults_and_search_terms()
    {
        var back = AppActionRegistry.Find("nav.back")!;
        var forward = AppActionRegistry.Find("nav.forward")!;
        Assert.Equal(("Back", GlideCommand.NavigateBack, "Navigation"), (back.Label, back.Command, back.Category));
        Assert.Equal(("Forward", GlideCommand.NavigateForward, "Navigation"), (forward.Label, forward.Command, forward.Category));
        Assert.Equal(new[] { "Alt+Left" }, HotkeyCatalog.Find("nav.back")!.DefaultShortcuts);   // strict Back: no Backspace, no fallback
        var composite = AppActionRegistry.Find("nav.backOrPrevious")!;
        Assert.Equal(("Back, or Previous image", GlideCommand.BackOrPrevious, "Navigation"), (composite.Label, composite.Command, composite.Category));
        Assert.Equal(new[] { "Backspace" }, HotkeyCatalog.Find("nav.backOrPrevious")!.DefaultShortcuts);
        foreach (var term in new[] { "back", "previous", "backspace", "history" }) Assert.Contains(term, composite.SearchTerms);
        Assert.Equal((int)GlideCommand.NavigateForward + 1, (int)GlideCommand.BackOrPrevious);
        Assert.Equal(new[] { "Shift+Backspace", "Alt+Right" }, HotkeyCatalog.Find("nav.forward")!.DefaultShortcuts);
        // Backspace belongs to the composite "Back, or Previous image"; the legacy Previous image row keeps its id but has no default key.
        Assert.Empty(HotkeyCatalog.Find("nav.previousBack")!.DefaultShortcuts);
        Assert.Equal(new[] { "Left" }, HotkeyCatalog.Find("nav.previousLeft")!.DefaultShortcuts);
        Assert.DoesNotContain("Backspace", HotkeyCatalog.Find("nav.previousBack")!.Label);
        foreach (var term in new[] { "back", "history", "navigate", "previous page", "undo navigation" }) Assert.Contains(term, back.SearchTerms);
        foreach (var term in new[] { "forward", "history", "navigate" }) Assert.Contains(term, forward.SearchTerms);
        Assert.Equal(AppActionContexts.Everywhere, back.Contexts);
        Assert.Equal("Back", back.Icon);
        Assert.Equal("Forward", forward.Icon);
        // Gesture picker: offered to click rows in every area that has them.
        Assert.True(GestureCatalog.IsOffered("windowed.middleClickImage", "nav.back"));
        Assert.True(GestureCatalog.IsOffered("explorer.middleClick", "nav.forward"));
        Assert.True(GestureCatalog.IsOffered("tabs.backButton", "nav.back"));
        Assert.False(GestureCatalog.IsOffered("wheel.windowed", "nav.back"));
        Assert.Null(GestureCatalog.DefaultActionFor("windowed.leftClickImage"));
        // The numeric commands are appended (saved numeric values keep their meaning).
        Assert.Equal((int)GlideCommand.ExitApplication + 1, (int)GlideCommand.NavigateBack);
        Assert.Equal((int)GlideCommand.ExitApplication + 2, (int)GlideCommand.NavigateForward);
        Assert.Empty(AppActionRegistry.Validate());
    }

    [Fact]
    public void Alt_arrows_and_Backspace_are_no_longer_fixed_Explorer_keys_Backspace_is_the_composite_hotkey()
    {
        var fixedKeys = HotkeyCatalog.FixedShortcuts;
        Assert.DoesNotContain(fixedKeys, f => f.Shortcut.Split(" / ").Any(k => ShortcutText.AreEqual(k, "Alt+Left") || ShortcutText.AreEqual(k, "Alt+Right")));
        Assert.Equal("Alt+Up", fixedKeys.Single(f => f.Id == "fixed.explorer.up").Shortcut);
        Assert.Empty(HotkeyLookup.FixedOwners("Backspace"));
        Assert.DoesNotContain(fixedKeys, f => f.Id == "fixed.explorer.backspace");
        var map = HotkeyCatalog.CreateDefaultMap();
        Assert.Contains("Back, or Previous image", HotkeyLookup.Describe(map, "Backspace"));
        Assert.Single(map.Where(p => p.Value.Contains("Backspace")));      // exactly one owner of Backspace
        Assert.Contains("Back", HotkeyLookup.Describe(map, "Alt+Left"));
        Assert.Contains("Forward", HotkeyLookup.Describe(map, "Shift+Backspace"));
    }

    [Fact]
    public void New_rows_get_their_defaults_but_never_take_a_key_another_action_already_uses()
    {
        var saved = HotkeyCatalog.CreateDefaultMap();
        saved.Remove("nav.back");
        saved.Remove("nav.forward");
        saved["view.fit"] = new List<string> { "Alt+Left" };          // the user's own binding of Alt+Left
        var skipped = HotkeyDefaultMigrations.AddMissingRows(saved);
        Assert.Empty(saved["nav.back"]);                               // the user's Alt+Left binding wins, nothing is taken away
        Assert.Equal(new[] { "Shift+Backspace", "Alt+Right" }, saved["nav.forward"]);
        var only = Assert.Single(skipped);
        Assert.Equal(("nav.back", "Alt+Left", "view.fit"), (only.RowId, only.Shortcut, only.OwnerRowId));
        Assert.Equal(new[] { "Alt+Left" }, saved["view.fit"]);
        // Rows that already exist are never touched.
        saved["nav.back"] = new List<string> { "F7" };
        Assert.Empty(HotkeyDefaultMigrations.AddMissingRows(saved));
        Assert.Equal(new[] { "F7" }, saved["nav.back"]);
    }

    // ---- Backspace moved from Previous image to "Back, or Previous image" (settings schema 11) -------------------------

    private static Dictionary<string, List<string>> OldDefaults()
    {
        var map = HotkeyCatalog.CreateDefaultMap();
        map["nav.backOrPrevious"] = new List<string>();
        map["nav.previousBack"] = new List<string> { "Backspace" };
        return map;
    }

    [Fact]
    public void Migration_moves_an_untouched_old_Backspace_default_from_Previous_image_to_the_composite_once()
    {
        var map = OldDefaults();
        Assert.True(HotkeyDefaultMigrations.MigrateBackspaceToBackOrPrevious(map, 10));
        Assert.Equal(new[] { "Backspace" }, map["nav.backOrPrevious"]);
        Assert.Empty(map["nav.previousBack"]);
        Assert.Equal(new[] { "Left" }, map["nav.previousLeft"]);
        Assert.Equal(new[] { "Alt+Left" }, map["nav.back"]);
        Assert.False(HotkeyDefaultMigrations.MigrateBackspaceToBackOrPrevious(map, 10));   // idempotent
        Assert.Single(map.Where(p => p.Value.Contains("Backspace")));
    }

    [Fact]
    public void Migration_never_steals_a_user_binding()
    {
        var rebound = OldDefaults();
        rebound["nav.previousBack"] = new List<string> { "F9" };
        Assert.False(HotkeyDefaultMigrations.MigrateBackspaceToBackOrPrevious(rebound, 10));
        Assert.Equal(new[] { "F9" }, rebound["nav.previousBack"]);
        Assert.Empty(rebound["nav.backOrPrevious"]);

        var both = OldDefaults();
        both["nav.previousBack"] = new List<string> { "Backspace", "F9" };
        Assert.False(HotkeyDefaultMigrations.MigrateBackspaceToBackOrPrevious(both, 10));

        var removed = OldDefaults();
        removed["nav.previousBack"] = new List<string>();
        Assert.False(HotkeyDefaultMigrations.MigrateBackspaceToBackOrPrevious(removed, 10));
        Assert.Empty(removed["nav.backOrPrevious"]);

        var owned = OldDefaults();
        owned["view.fit"] = new List<string> { "Backspace" };
        Assert.False(HotkeyDefaultMigrations.MigrateBackspaceToBackOrPrevious(owned, 10));
        Assert.Equal(new[] { "Backspace" }, owned["nav.previousBack"]);
        Assert.Empty(owned["nav.backOrPrevious"]);
    }

    [Fact]
    public void Migration_keeps_schema_11_files_as_they_are_and_a_missing_composite_row_never_grabs_a_freed_Backspace()
    {
        var map = OldDefaults();
        Assert.False(HotkeyDefaultMigrations.MigrateBackspaceToBackOrPrevious(map, 11));
        Assert.Equal(new[] { "Backspace" }, map["nav.previousBack"]);
        var removedAgain = HotkeyCatalog.CreateDefaultMap();
        removedAgain["nav.backOrPrevious"] = new List<string>();        // the user removed Backspace at schema 11
        Assert.False(HotkeyDefaultMigrations.MigrateBackspaceToBackOrPrevious(removedAgain, 11));
        Assert.Empty(removedAgain["nav.backOrPrevious"]);

        // A schema 10 file has no composite row: AddMissingRows adds it empty, and only the migration decides about Backspace.
        var freed = HotkeyCatalog.CreateDefaultMap();
        freed.Remove("nav.backOrPrevious");
        freed["nav.previousBack"] = new List<string>();                 // the user freed Backspace on purpose
        HotkeyDefaultMigrations.AddMissingRows(freed);
        Assert.Empty(freed["nav.backOrPrevious"]);
        Assert.False(HotkeyDefaultMigrations.MigrateBackspaceToBackOrPrevious(freed, 10));
        Assert.Empty(freed["nav.backOrPrevious"]);
        var untouched = HotkeyCatalog.CreateDefaultMap();
        untouched.Remove("nav.backOrPrevious");
        untouched["nav.previousBack"] = new List<string> { "Backspace" };
        HotkeyDefaultMigrations.AddMissingRows(untouched);
        Assert.True(HotkeyDefaultMigrations.MigrateBackspaceToBackOrPrevious(untouched, 10));
        Assert.Equal(new[] { "Backspace" }, untouched["nav.backOrPrevious"]);
    }

    [Fact]
    public void Presets_that_deliberately_keep_Backspace_as_Previous_image_unassign_it_from_the_composite_cleanly()
    {
        foreach (var name in new[] { "IrfanView", "nomacs", "FastStone", "XnView MP" })
        {
            var map = HotkeyCatalog.CreatePresetMap(name);
            Assert.Equal(new[] { "Backspace" }, map["nav.previousBack"]);
            Assert.Empty(map["nav.backOrPrevious"]);
            Assert.Equal(new[] { "Alt+Left" }, map["nav.back"]);
            Assert.Single(map.Where(p => p.Value.Contains("Backspace")));
            Assert.Contains("Previous image", HotkeyLookup.Describe(map, "Backspace"));
        }
        var photos = HotkeyCatalog.CreatePresetMap("Windows Photos");
        Assert.Empty(photos["nav.previousBack"]);
        Assert.Equal(new[] { "Backspace" }, photos["nav.backOrPrevious"]);
        Assert.Equal(new[] { "Backspace" }, HotkeyCatalog.CreatePresetMap("Glide default")["nav.backOrPrevious"]);
    }

    [Fact]
    public void Assigning_Backspace_to_Previous_image_asks_first_and_then_unassigns_it_from_the_composite_cleanly()
    {
        var map = HotkeyCatalog.CreateDefaultMap();
        var conflict = HotkeyConflicts.Check(map, "Backspace", "nav.previousBack");
        Assert.Equal(HotkeyConflictKind.OtherAction, conflict.Kind);
        Assert.Contains(conflict.Owners, o => o.ActionId == "nav.backOrPrevious");
        Assert.Equal(HotkeyAssignOutcome.Declined, HotkeyConflicts.Assign(map, "Backspace", "nav.previousBack", null, add: true, replaceOthers: false));
        Assert.Equal(new[] { "Backspace" }, map["nav.backOrPrevious"]);
        Assert.Equal(HotkeyAssignOutcome.Assigned, HotkeyConflicts.Assign(map, "Backspace", "nav.previousBack", null, add: true, replaceOthers: true));
        Assert.Empty(map["nav.backOrPrevious"]);
        Assert.Equal(new[] { "Backspace" }, map["nav.previousBack"]);
        Assert.Single(map.Where(p => p.Value.Contains("Backspace")));
        Assert.Equal("nav.previous", new Glide.Input.InputRouter().ResolveShortcutAction("Backspace", map)!.Id);
    }

    /// <summary>Round 3 #13: the Recent locations dropdown lists places most recently visited first, each once.</summary>
    [Fact]
    public void Recent_order_is_most_recently_visited_first_with_each_place_once()
    {
        var history = new NavigationHistory();
        var root = NavigationEntry.Explorer(@"C:\Root");
        var alpha = NavigationEntry.Explorer(@"C:\Root\Alpha");
        var deep = NavigationEntry.Explorer(@"C:\Root\Alpha\Deep");
        history.Push(root); history.Push(alpha); history.Push(deep);
        Assert.Equal(new[] { 2, 1, 0 }, history.RecentOrder());
        history.GoTo(0); // jump back to Root from the dropdown
        Assert.Equal(new[] { 0, 2, 1 }, history.RecentOrder());
        history.Forward(); // Alpha
        Assert.Equal(new[] { 1, 0, 2 }, history.RecentOrder());
        history.Push(NavigationEntry.Explorer(@"C:\Other")); // drops Deep (Forward of Alpha)
        history.Push(root); // Root again: listed once, at its newest visit
        Assert.Equal(new[] { 3, 2, 1 }, history.RecentOrder());
        Assert.Equal(new[] { 3, 2 }, history.RecentOrder(2));
        // A restored history assumes nearer entries are more recent.
        var restored = NavigationHistory.FromState(new NavigationHistoryState(new[] { root, alpha, deep }, 1));
        Assert.Equal(new[] { 1, 0, 2 }, restored.RecentOrder());
    }
}
