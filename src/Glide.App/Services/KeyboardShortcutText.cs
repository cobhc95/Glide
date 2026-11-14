using Avalonia.Input;

namespace Glide.App.Services;

/// <summary>
/// The single canonical text form of a key press ("Ctrl+Shift+Left", "+", "Esc"). Shortcut capture in
/// Settings and runtime hotkey lookup in the main window must produce byte-identical strings, so both
/// call this instead of keeping their own copies.
/// </summary>
internal static class KeyboardShortcutText
{
    public static string From(KeyEventArgs e) => From(e.Key, e.KeyModifiers);

    public static string From(Key pressed, KeyModifiers modifiers)
    {
        // OemPlus is the physical =/+ key on common layouts.  Treat Shift+OemPlus as the semantic
        // "+" shortcut (without serializing a redundant "Shift++"), while an unshifted press remains
        // "=". Numpad Add is always "+". This keeps capture and runtime lookup byte-for-byte aligned.
        var shiftedOemPlus = pressed == Key.OemPlus && modifiers.HasFlag(KeyModifiers.Shift);
        var key = pressed switch
        {
            Key.Escape => "Esc", Key.Space => "Space", Key.Enter => "Enter", Key.Tab => "Tab", Key.Back => "Backspace",
            Key.Left => "Left", Key.Right => "Right", Key.Up => "Up", Key.Down => "Down", Key.PageUp => "PageUp", Key.PageDown => "PageDown",
            Key.Home => "Home", Key.End => "End", Key.Delete => "Delete",
            Key.Add => "+", Key.OemPlus when shiftedOemPlus => "+", Key.OemPlus => "=", Key.OemMinus or Key.Subtract => "-",
            Key.OemComma => ",", Key.OemPeriod => ".", Key.OemOpenBrackets => "[", Key.OemCloseBrackets => "]",
            Key.D0 => "0", Key.D1 => "1", Key.D2 => "2", Key.D3 => "3", Key.D4 => "4", Key.D5 => "5", Key.D6 => "6", Key.D7 => "7", Key.D8 => "8", Key.D9 => "9",
            _ => pressed.ToString()
        };
        var parts = new List<string>();
        if (modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(KeyModifiers.Shift) && !shiftedOemPlus) parts.Add("Shift");
        if (modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        parts.Add(key);
        return string.Join("+", parts);
    }
}
