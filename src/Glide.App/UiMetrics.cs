namespace Glide.App;

/// <summary>
/// Shared UI metrics that more than one code path must agree on (4.5.2 audit). Add a constant here only
/// when it is actually referenced; unused placeholders were removed in the 4.8 hygiene pass.
/// </summary>
public static class UiMetrics
{
    /// <summary>Minimum selection buffer (view DIP) on each side of the image while the buffer is
    /// enabled; the user-configured percentage of the empty space can only make it larger.</summary>
    public const double SelectionEdgeMargin = 24.0;
}
