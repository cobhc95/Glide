using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Glide.App.Settings;

/// <summary>
/// A category heading inside a customiser list (title-bar and status-bar customisers). It used to be a disabled
/// ListBoxItem, but hit testing skips disabled controls, so the pointer was never "over" the heading and its tooltip
/// (the full, possibly trimmed, category name) could not open. The heading is now an enabled item that cannot be
/// selected: it takes no focus, swallows presses, and has no hover/selection highlight. It has no Tag, so code that
/// reads the selected id ignores it.
/// </summary>
internal static class CustomizerListHeading
{
    internal static ListBoxItem Create(string text)
    {
        var item = new ListBoxItem
        {
            Focusable = false,
            Background = Brushes.Transparent,
            Content = new TextBlock
            {
                Text = text, FontWeight = FontWeight.SemiBold, FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#8E9AA8")),
                Margin = new Thickness(4, 8, 0, 2), TextTrimming = TextTrimming.CharacterEllipsis, IsHitTestVisible = false
            },
            [ToolTip.TipProperty] = text
        };
        item.Classes.Add("customizerHeading");
        foreach (var key in new[] { "ListBoxItemBackgroundPointerOver", "ListBoxItemBackgroundPressed", "ListBoxItemBackgroundSelected",
                                    "ListBoxItemBackgroundSelectedPointerOver", "ListBoxItemBackgroundSelectedPressed" })
            item.Resources[key] = Brushes.Transparent;
        // Not selectable: stop the press before the ListBox turns it into a selection.
        item.AddHandler(InputElement.PointerPressedEvent, (_, e) => e.Handled = true, RoutingStrategies.Tunnel);
        item.AddHandler(InputElement.PointerReleasedEvent, (_, e) => e.Handled = true, RoutingStrategies.Tunnel);
        return item;
    }
}
