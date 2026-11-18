using Glide.Core.Commands;

namespace Glide.Core.Tests;

/// <summary>
/// Assigning a shortcut that another action already uses asks Replace / Cancel instead of silently stealing it.
/// These are the pure rules behind that prompt (no UI).
/// </summary>
[Trait("Area", "Hotkeys")]
public sealed class HotkeyConflictTests
{
    private static Dictionary<string, List<string>> Defaults() => HotkeyCatalog.CreateDefaultMap();

    [Fact]
    public void A_free_shortcut_has_no_conflict_and_is_assigned_without_a_prompt()
    {
        var map = Defaults();
        var conflict = HotkeyConflicts.Check(map, "Ctrl+Alt+Q", "view.zoomIn");
        Assert.Equal(HotkeyConflictKind.None, conflict.Kind);
        Assert.False(conflict.NeedsConfirmation);

        Assert.Equal(HotkeyAssignOutcome.Assigned, HotkeyConflicts.Assign(map, "Ctrl+Alt+Q", "view.zoomIn", null, add: true, replaceOthers: false));
        Assert.Contains("Ctrl+Alt+Q", map["view.zoomIn"]);
    }

    [Fact]
    public void A_shortcut_owned_by_another_action_names_the_owner_and_needs_confirmation()
    {
        var map = Defaults();
        // "F" belongs to Toggle fullscreen by default.
        var conflict = HotkeyConflicts.Check(map, "f", "view.zoomIn");   // case-insensitive
        Assert.Equal(HotkeyConflictKind.OtherAction, conflict.Kind);
        Assert.True(conflict.NeedsConfirmation);
        var owner = Assert.Single(conflict.Owners);
        Assert.Equal("view.fullscreen", owner.ActionId);
        Assert.Equal("Toggle fullscreen", owner.Label);
        Assert.Contains("Toggle fullscreen", conflict.PromptText());
        Assert.Contains("Zoom in", conflict.PromptText());
    }

    [Fact]
    public void Cancel_leaves_every_binding_untouched()
    {
        var map = Defaults();
        var before = map.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());

        var outcome = HotkeyConflicts.Assign(map, "F", "view.zoomIn", null, add: true, replaceOthers: false);

        Assert.Equal(HotkeyAssignOutcome.Declined, outcome);
        Assert.All(before, kv => Assert.Equal(kv.Value, map[kv.Key]));
    }

    [Fact]
    public void Replace_unassigns_the_old_owner_and_gives_the_shortcut_to_the_new_action()
    {
        var map = Defaults();
        var outcome = HotkeyConflicts.Assign(map, "F", "view.zoomIn", null, add: true, replaceOthers: true);

        Assert.Equal(HotkeyAssignOutcome.Assigned, outcome);
        Assert.DoesNotContain("F", map["view.fullscreen"], StringComparer.OrdinalIgnoreCase);
        Assert.Contains("F11", map["view.fullscreen"]);           // its other shortcuts survive
        Assert.Contains("F", map["view.zoomIn"]);
        Assert.Single(HotkeyConflicts.OwnersOf(map, "F"));
    }

    [Fact]
    public void Reassigning_a_changed_shortcut_replaces_the_old_key_of_the_same_action()
    {
        var map = Defaults();
        // Change zoom-in's "+" to a free key: the old key is dropped, the other alias stays.
        var outcome = HotkeyConflicts.Assign(map, "Ctrl+Alt+Z", "view.zoomIn", "+", add: false, replaceOthers: false);

        Assert.Equal(HotkeyAssignOutcome.Assigned, outcome);
        Assert.Equal(new[] { "=", "Ctrl+Alt+Z" }, map["view.zoomIn"]);
    }

    [Fact]
    public void Assigning_a_shortcut_the_action_already_has_is_a_no_op_without_a_prompt()
    {
        var map = Defaults();
        var conflict = HotkeyConflicts.Check(map, "F11", "view.fullscreen");
        Assert.Equal(HotkeyConflictKind.SameAction, conflict.Kind);
        Assert.False(conflict.NeedsConfirmation);

        var before = map["view.fullscreen"].ToList();
        Assert.Equal(HotkeyAssignOutcome.AlreadyAssigned, HotkeyConflicts.Assign(map, "f11", "view.fullscreen", null, add: true, replaceOthers: false));
        Assert.Equal(before, map["view.fullscreen"]);
        // "Change" F11 to F11 is the same no-op.
        Assert.Equal(HotkeyAssignOutcome.AlreadyAssigned, HotkeyConflicts.Assign(map, "F11", "view.fullscreen", "F11", add: false, replaceOthers: false));
        Assert.Equal(before, map["view.fullscreen"]);
    }

    [Fact]
    public void Changing_a_key_into_another_key_the_same_action_has_keeps_one_copy()
    {
        var map = Defaults();
        // F -> F11 on the same action: F11 already there, so only the old key goes.
        var outcome = HotkeyConflicts.Assign(map, "F11", "view.fullscreen", "F", add: false, replaceOthers: false);
        Assert.Equal(HotkeyAssignOutcome.Assigned, outcome);
        Assert.Equal(new[] { "F11", "Enter" }, map["view.fullscreen"]);
    }

    [Fact]
    public void Global_and_viewer_scoped_owners_are_described_so_the_prompt_can_say_where_the_key_is_live()
    {
        // Tabs/Application/Workspace/External actions work in every tab; viewing actions only while viewing.
        Assert.Equal(HotkeyScope.Global, HotkeyConflicts.ScopeOf("tab.new"));
        Assert.Equal(HotkeyScope.Global, HotkeyConflicts.ScopeOf("app.settings"));
        Assert.Equal(HotkeyScope.Global, HotkeyConflicts.ScopeOf("external.1"));
        Assert.Equal(HotkeyScope.Viewer, HotkeyConflicts.ScopeOf("view.zoomIn"));
        Assert.Equal(HotkeyScope.Viewer, HotkeyConflicts.ScopeOf("nav.nextRight"));

        var map = Defaults();

        // Viewer action takes a key from a global action (Ctrl+T = New tab): global owner, crosses scopes.
        var takeGlobal = HotkeyConflicts.Check(map, "Ctrl+T", "view.zoomIn");
        Assert.True(takeGlobal.NeedsConfirmation);
        Assert.True(takeGlobal.CrossesScopes);
        Assert.Contains("every tab", takeGlobal.PromptText());

        // Global action takes a key from a viewer action (F = fullscreen): crosses scopes the other way.
        var takeViewer = HotkeyConflicts.Check(map, "F", "tab.new");
        Assert.True(takeViewer.CrossesScopes);
        Assert.Contains("only works while viewing", takeViewer.PromptText());

        // Same-scope conflict does not claim anything about scope.
        var sameScope = HotkeyConflicts.Check(map, "F", "view.zoomIn");
        Assert.False(sameScope.CrossesScopes);
        Assert.DoesNotContain("every tab", sameScope.PromptText());
    }

    [Fact]
    public void Hotkey_lookup_names_every_owner_or_says_the_key_is_unassigned()
    {
        var map = Defaults();
        Assert.Equal("Ctrl+P is assigned to: Print current image (File).", HotkeyLookup.Describe(map, "Ctrl+P"));
        Assert.Equal("Ctrl+Alt+Q is not assigned to any action.", HotkeyLookup.Describe(map, "Ctrl+Alt+Q"));

        map["view.zoomIn"].Add("Ctrl+P");   // hand-edited file with a duplicate: both are listed
        var text = HotkeyLookup.Describe(map, "ctrl+p");
        Assert.Contains("2 actions", text);
        Assert.Contains("Zoom in", text);
        Assert.Contains("Print current image", text);
    }
}
