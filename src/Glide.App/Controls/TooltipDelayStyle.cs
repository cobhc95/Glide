using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;

namespace Glide.App.Controls;

/// <summary>
/// 5.1.0: applies a tooltip area's show delay (Glide.Core.Settings.TooltipAreas) to every control under one root. ToolTip.ShowDelay
/// is not inherited, so the root carries one style that sets it on each control below; a nearer root (the Explorer item
/// list inside the main window) overrides a farther one. Controls never set their own delay. Calling it again replaces the
/// style, so a changed setting applies live.
/// </summary>
internal static class TooltipDelayStyle
{
    private sealed class DelayStyle : Style
    {
        public DelayStyle(int ms) : base(x => x.Is<Control>()) => Setters.Add(new Setter(ToolTip.ShowDelayProperty, ms));
        public int Ms { get; init; }
    }

    public static void Apply(StyledElement root, int ms)
    {
        DelayStyle? existing = null;
        foreach (var style in root.Styles)
            if (style is DelayStyle d) { existing = d; break; }
        if (existing is not null)
        {
            if (existing.Ms == ms) return;
            root.Styles.Remove(existing);
        }
        root.Styles.Add(new DelayStyle(ms) { Ms = ms });
    }

    /// <summary>The delay a root's style sets (null when none): for tests.</summary>
    public static int? AppliedMs(StyledElement root) => root.Styles.OfType<DelayStyle>().FirstOrDefault()?.Ms;
}
