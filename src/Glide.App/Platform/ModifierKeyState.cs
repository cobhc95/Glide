using System.Runtime.InteropServices;
using Avalonia.Input;

namespace Glide.App.Platform;

/// <summary>
/// The modifier keys held right now. Menu item clicks carry no key modifiers in Avalonia, so the "Open in new tab"
/// menu items read the keyboard state when they run (Shift / Alt flip the new-tab settings, see NewTabPolicy).
/// </summary>
internal static class ModifierKeyState
{
    /// <summary>Test seam: headless tests cannot hold real keys.</summary>
    internal static Func<KeyModifiers>? OverrideForTests { get; set; }

    public static KeyModifiers Current()
    {
        if (OverrideForTests is { } fake) return fake();
        if (!OperatingSystem.IsWindows()) return KeyModifiers.None;
        var modifiers = KeyModifiers.None;
        if (IsDown(VkShift)) modifiers |= KeyModifiers.Shift;
        if (IsDown(VkControl)) modifiers |= KeyModifiers.Control;
        if (IsDown(VkMenu)) modifiers |= KeyModifiers.Alt;
        return modifiers;
    }

    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;

    private static bool IsDown(int key)
    {
        try { return (GetAsyncKeyState(key) & 0x8000) != 0; }
        catch { return false; /* no user32 (not Windows): treat as not held */ }
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
