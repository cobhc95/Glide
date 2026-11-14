using Avalonia.Input;
using Glide.App.Services;

namespace Glide.Core.Tests;

/// <summary>Locks the canonical shortcut text shared by Settings capture and runtime hotkey lookup.</summary>
public sealed class KeyboardShortcutTextTests
{
    [Theory]
    [InlineData(Key.Left, KeyModifiers.None, "Left")]
    [InlineData(Key.Right, KeyModifiers.Shift, "Shift+Right")]
    [InlineData(Key.Escape, KeyModifiers.None, "Esc")]
    [InlineData(Key.Back, KeyModifiers.Control, "Ctrl+Backspace")]
    [InlineData(Key.OemPlus, KeyModifiers.None, "=")]
    [InlineData(Key.OemPlus, KeyModifiers.Shift, "+")]
    [InlineData(Key.OemPlus, KeyModifiers.Control | KeyModifiers.Shift, "Ctrl++")]
    [InlineData(Key.Add, KeyModifiers.None, "+")]
    [InlineData(Key.Subtract, KeyModifiers.Control, "Ctrl+-")]
    [InlineData(Key.D7, KeyModifiers.Alt, "Alt+7")]
    [InlineData(Key.F11, KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt, "Ctrl+Shift+Alt+F11")]
    public void Produces_the_canonical_text(Key key, KeyModifiers modifiers, string expected) =>
        Assert.Equal(expected, KeyboardShortcutText.From(key, modifiers));
}
