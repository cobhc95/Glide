using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Glide.App.Services;

/// <summary>
/// Esc cancels every dialog / secondary window, through one class handler on <see cref="Window"/> instead of a
/// handler per dialog. Esc does exactly what the title-bar close button does: <see cref="Window.Close()"/> with no
/// result, so each dialog runs its own close / Cancel path (an undecided "Continue to nearby folder?" prompt returns
/// "no" and does not store its "Don't remind me again" box; a <c>ShowDialog&lt;T&gt;</c> gets its default result; Settings
/// keeps its own Cancel path with the discard check).
/// <para>The handler runs on the bubble route and ignores handled events, so whatever sits closer to the key wins:
/// an open dropdown or popup closes first, an active shortcut capture cancels the capture first (Settings marks
/// that Esc handled), and windows with their own Esc handling (Settings, Reset, Update prompt) keep it.</para>
/// <para>Excluded: <see cref="MainWindow"/> (Esc there is a bindable viewer key, never "close the window"), and any
/// window that sets <see cref="IsDisabledProperty"/>.</para>
/// </summary>
internal static class DialogEscape
{
    private static bool _registered;

    public static readonly AttachedProperty<bool> IsDisabledProperty =
        AvaloniaProperty.RegisterAttached<Window, bool>("IsDisabled", typeof(DialogEscape));

    public static void SetIsDisabled(Window window, bool value) => window.SetValue(IsDisabledProperty, value);
    public static bool GetIsDisabled(Window window) => window.GetValue(IsDisabledProperty);

    /// <summary>Window types Esc never closes, with the reason (checked by DialogEscapeCoverageTests).</summary>
    public static readonly IReadOnlyDictionary<Type, string> ExcludedTypes = new Dictionary<Type, string>
    {
        [typeof(MainWindow)] = "The viewer window: Esc is a bindable key there (exit fullscreen, stop slideshow, close per the Esc setting), never a dialog cancel."
    };

    /// <summary>Idempotent; called once from application start-up (and by headless tests).</summary>
    internal static void Register()
    {
        if (_registered) return;
        _registered = true;
        InputElement.KeyDownEvent.AddClassHandler<Window>(OnKeyDown, RoutingStrategies.Bubble);
    }

    /// <summary>True when Esc closes <paramref name="window"/>.</summary>
    internal static bool Applies(Window window) => AppliesTo(window.GetType()) && !GetIsDisabled(window);

    /// <summary>True for every Window type except <see cref="ExcludedTypes"/> (plain <c>new Window</c> dialogs included).</summary>
    internal static bool AppliesTo(Type windowType) => typeof(Window).IsAssignableFrom(windowType) && !ExcludedTypes.ContainsKey(windowType);

    private static void OnKeyDown(Window window, KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None || !Applies(window)) return;
        e.Handled = true;
        window.Close();
    }
}
