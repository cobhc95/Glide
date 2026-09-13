using Avalonia;
using Avalonia.Input;
using Glide.App;
using Glide.App.Platform;

namespace Glide.App.Services;

/// <summary>
/// Owns cross-window tab attachment hit testing and preview lifetime.  MainWindow remains the
/// composition root and performs the actual transfer, while this small controller owns the
/// easy-to-strand target/highlight/index state for both ordinary and native move-loop drags.
/// </summary>
public sealed class TabAttachCoordinator
{
    private readonly MainWindow _source;
    private readonly Action<string, string, object?> _diagnostic;

    public MainWindow? Target { get; private set; }
    public int TargetIndex { get; private set; } = -1;

    public TabAttachCoordinator(MainWindow source, Action<string, string, object?> diagnostic)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _diagnostic = diagnostic ?? throw new ArgumentNullException(nameof(diagnostic));
    }

    public void Update(PixelPoint cursor, bool enabled, string enteredEvent, string leftEvent, string category)
    {
        var next = enabled ? GlideWindowRegistry.FindAttachTarget(cursor, _source) : null;
        if (!ReferenceEquals(next, Target))
        {
            Target?.SetTabAttachHighlight(false);
            Target = next;
            Target?.SetTabAttachHighlight(true);
            _diagnostic(category, next is null ? leftEvent : enteredEvent, new
            {
                target = next?.Title ?? string.Empty,
                screenX = cursor.X,
                screenY = cursor.Y,
                hitZone = next is null ? null : next.GetTabAttachZoneScreenRect().ToString(),
                visualPreview = next is not null
            });
        }
        TargetIndex = Target?.GetTabInsertIndex(cursor) ?? -1;
    }

    public void Clear()
    {
        Target?.SetTabAttachHighlight(false);
        Target = null;
        TargetIndex = -1;
    }
}
