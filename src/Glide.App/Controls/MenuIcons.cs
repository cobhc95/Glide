using Avalonia;
using Avalonia.Controls;

namespace Glide.App.Controls;

/// <summary>Small vector icons for menu items, in the toolbar's own icon set and tinted with the theme text colour.</summary>
internal static class MenuIcons
{
    public static Control Create(StyledElement host, string kind)
    {
        var icon = new GlideIconView { Kind = kind, Width = 16, Height = 16, StrokeWidth = 1.7 };
        icon.Bind(GlideIconView.IconBrushProperty, host.GetResourceObservable("BrushText"));
        return icon;
    }
}
