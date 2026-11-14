using Avalonia;
using Avalonia.Controls;
using Glide.Core.Commands;

namespace Glide.App.Services;

/// <summary>
/// Ties every context-menu item to the master action registry. Builders tag each leaf item with the
/// <see cref="AppActionRegistry"/> id it performs (<see cref="ActionIdProperty"/>), or with an explicit
/// read-only / context tag for items that act on one specific thing (a recent folder, "Move this button left").
/// The menu coverage test walks every builder and fails on an untagged item or an unknown action id.
/// </summary>
public static class MenuActions
{
    public static readonly AttachedProperty<string?> ActionIdProperty =
        AvaloniaProperty.RegisterAttached<MenuItem, string?>("ActionId", typeof(MenuActions));

    /// <summary>Prefix for items that are deliberately not registry actions; the rest of the tag says why.</summary>
    public const string ContextPrefix = "@context:";

    public static string? GetActionId(MenuItem item) => item.GetValue(ActionIdProperty);

    public static T Tag<T>(T item, string actionId) where T : MenuItem
    {
        item.SetValue(ActionIdProperty, actionId);
        return item;
    }

    /// <summary>Marks an item that operates on one specific object of this menu (read-only in the registry sense).</summary>
    public static T Context<T>(T item, string why) where T : MenuItem
    {
        item.SetValue(ActionIdProperty, ContextPrefix + why);
        return item;
    }

    /// <summary>The menu header for a registry action: its label, plus the current shortcut when one is bound.</summary>
    public static string Header(string label, string? shortcut) =>
        string.IsNullOrWhiteSpace(shortcut) || shortcut == "Unassigned" ? label : $"{label}\t{shortcut}";

    /// <summary>True when the tag names a registry action or is an explicit context tag.</summary>
    public static bool IsValidTag(string? tag) =>
        tag is not null && (tag.StartsWith(ContextPrefix, StringComparison.Ordinal) ? tag.Length > ContextPrefix.Length : AppActionRegistry.Find(tag) is not null);
}
