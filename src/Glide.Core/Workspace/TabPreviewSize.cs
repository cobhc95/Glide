namespace Glide.Core.Workspace;

/// <summary>
/// Round 3 #5: the size of a tab's hover preview ("Tab preview size (%)") and the key that, held while turning the mouse
/// wheel over the tab, resizes an open preview ("Resize tab preview with wheel and").
/// </summary>
public static class TabPreviewSize
{
    public const int Min = 50, Max = 250, Default = 100, Step = 10;
    public const string DefaultModifier = "Ctrl";
    public static readonly string[] Modifiers = ["Ctrl", "Shift", "Alt", "None"];

    public static int Clamp(int percent) => percent <= 0 ? Default : Math.Clamp(percent, Min, Max);

    public static string NormalizeModifier(string? value) =>
        Modifiers.FirstOrDefault(m => string.Equals(m, value?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? DefaultModifier;

    /// <summary>The size after one wheel notch: up = bigger, in <see cref="Step"/> steps, within <see cref="Min"/>..<see cref="Max"/>.</summary>
    public static int AfterWheel(int percent, double deltaY) =>
        deltaY == 0 ? Clamp(percent) : Clamp(Clamp(percent) + (deltaY > 0 ? Step : -Step));

    /// <summary>True when exactly the chosen key is held (Ctrl, Shift or Alt alone); None never matches.</summary>
    public static bool ModifierMatches(string? choice, bool ctrl, bool shift, bool alt) => NormalizeModifier(choice) switch
    {
        "Ctrl" => ctrl && !shift && !alt,
        "Shift" => shift && !ctrl && !alt,
        "Alt" => alt && !ctrl && !shift,
        _ => false
    };
}
