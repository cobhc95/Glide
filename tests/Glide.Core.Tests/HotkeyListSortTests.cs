using Glide.Core.Commands;

namespace Glide.Core.Tests;

/// <summary>Clickable column headers of the hotkey list: sort orders, direction toggle and the arrow indicator.</summary>
public sealed class HotkeyListSortTests
{
    private static List<HotkeyListRow> Rows() => new()
    {
        new("a.zoom", "F11", "Zoom in", "Viewing"),
        new("b.print", "Ctrl+P", "Print current image", "File"),
        new("c.none", "", "Home tab", "Workspace"),
        new("d.f2", "F2", "Rename current file", "File"),
        new("e.open", "O", "Open file", "File"),
        new("f.none2", "", "Detach tab", "Tabs"),
    };

    [Fact]
    public void Default_state_keeps_catalogue_order()
    {
        var rows = Rows();
        Assert.Equal(rows.Select(r => r.ActionId), HotkeyListSort.Sort(rows, HotkeySortState.Default).Select(r => r.ActionId));
    }

    [Fact]
    public void Clicking_a_header_sorts_ascending_and_clicking_it_again_reverses()
    {
        var state = HotkeySortState.Default.Toggle(HotkeySortColumn.Action);
        Assert.Equal(new HotkeySortState(HotkeySortColumn.Action, false), state);
        state = state.Toggle(HotkeySortColumn.Action);
        Assert.Equal(new HotkeySortState(HotkeySortColumn.Action, true), state);
        state = state.Toggle(HotkeySortColumn.Action);
        Assert.Equal(new HotkeySortState(HotkeySortColumn.Action, false), state);
        // A different header starts ascending again.
        Assert.Equal(new HotkeySortState(HotkeySortColumn.Category, false), state.Toggle(HotkeySortColumn.Category));
    }

    [Fact]
    public void Indicator_marks_only_the_sorted_column_with_an_up_or_down_arrow()
    {
        var up = new HotkeySortState(HotkeySortColumn.Shortcut, false);
        var down = new HotkeySortState(HotkeySortColumn.Shortcut, true);
        Assert.Equal("▲", up.Indicator(HotkeySortColumn.Shortcut));
        Assert.Equal("▼", down.Indicator(HotkeySortColumn.Shortcut));
        Assert.Equal("", up.Indicator(HotkeySortColumn.Action));
        Assert.Equal("", up.Indicator(HotkeySortColumn.Category));
        Assert.Equal("", HotkeySortState.Default.Indicator(HotkeySortColumn.Shortcut));
    }

    [Fact]
    public void Action_column_sorts_by_label_in_both_directions()
    {
        var asc = HotkeyListSort.Sort(Rows(), new(HotkeySortColumn.Action, false)).Select(r => r.Label).ToList();
        Assert.Equal(asc.OrderBy(x => x, NaturalStringComparer.Instance).ToList(), asc);
        var desc = HotkeyListSort.Sort(Rows(), new(HotkeySortColumn.Action, true)).Select(r => r.Label).ToList();
        Assert.Equal(Enumerable.Reverse(asc).ToList(), desc);
        Assert.Equal("Detach tab", asc[0]);
        Assert.Equal("Zoom in", asc[^1]);
    }

    [Fact]
    public void Category_column_groups_by_category_then_label()
    {
        var asc = HotkeyListSort.Sort(Rows(), new(HotkeySortColumn.Category, false));
        Assert.Equal(new[] { "File", "File", "File", "Tabs", "Viewing", "Workspace" }, asc.Select(r => r.Category));
        Assert.Equal(new[] { "Open file", "Print current image", "Rename current file" }, asc.Take(3).Select(r => r.Label));
        var desc = HotkeyListSort.Sort(Rows(), new(HotkeySortColumn.Category, true));
        Assert.Equal(new[] { "Workspace", "Viewing", "Tabs" }, desc.Take(3).Select(r => r.Category));
    }

    [Fact]
    public void Shortcut_column_is_natural_ordered_and_keeps_unassigned_rows_last_in_either_direction()
    {
        var asc = HotkeyListSort.Sort(Rows(), new(HotkeySortColumn.Shortcut, false)).ToList();
        Assert.Equal(new[] { "", "" }, asc.TakeLast(2).Select(r => r.Shortcut));
        var assigned = asc.Where(r => r.Shortcut.Length > 0).Select(r => r.Shortcut).ToList();
        Assert.True(assigned.IndexOf("F2") < assigned.IndexOf("F11"), "F2 must sort before F11 (natural order)");
        Assert.Equal(assigned.OrderBy(x => x, NaturalStringComparer.Instance).ToList(), assigned);

        var desc = HotkeyListSort.Sort(Rows(), new(HotkeySortColumn.Shortcut, true)).ToList();
        Assert.Equal(new[] { "", "" }, desc.TakeLast(2).Select(r => r.Shortcut));
        Assert.Equal(Enumerable.Reverse(assigned).ToList(), desc.Where(r => r.Shortcut.Length > 0).Select(r => r.Shortcut).ToList());
    }

    [Fact]
    public void Sorting_the_real_catalogue_never_loses_or_duplicates_rows_and_is_stable()
    {
        var all = HotkeyCatalog.All
            .SelectMany(a => a.DefaultShortcuts.Length == 0
                ? new[] { new HotkeyListRow(a.Id, "", a.Label, a.Category) }
                : a.DefaultShortcuts.Select(s => new HotkeyListRow(a.Id, s, a.Label, a.Category)))
            .ToList();
        foreach (var column in new[] { HotkeySortColumn.Shortcut, HotkeySortColumn.Action, HotkeySortColumn.Category })
        foreach (var descending in new[] { false, true })
        {
            var sorted = HotkeyListSort.Sort(all, new(column, descending));
            Assert.Equal(all.Count, sorted.Count);
            Assert.Equal(all.OrderBy(r => r.ActionId + "|" + r.Shortcut), sorted.OrderBy(r => r.ActionId + "|" + r.Shortcut));
            // Deterministic: sorting twice gives the identical order.
            Assert.Equal(sorted, HotkeyListSort.Sort(all, new(column, descending)));
        }
    }
}
