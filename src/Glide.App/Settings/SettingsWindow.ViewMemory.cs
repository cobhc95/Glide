using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Glide.App.Controls;
using Glide.Core.Settings;

namespace Glide.App.Settings;

/// <summary>
/// Settings comes back the way it was left: same page, same scroll position on every page that was scrolled and the same
/// open / closed sections. Closing captures it (<see cref="CaptureViewMemory"/>); the owner stores the text in
/// <see cref="GlideSettingsState.SettingsViewMemory"/> (kept in memory for the session and saved with the settings), and
/// the next window restores it in its constructor (<see cref="RestoreViewMemory"/>). The search text is deliberately not
/// remembered: the page you were on comes back, the search box does not. Opening Settings at a setting (search "Open", a
/// command, a link) still goes to that setting: those paths call ShowCategory with a reveal target, which wins.
/// </summary>
public partial class SettingsWindow
{
    /// <summary>
    /// Called once when the window has closed, with the view text to remember (see <see cref="SettingsViewMemory"/>). The owner
    /// stores it in its settings state before it saves the placement; the window never writes settings itself.
    /// </summary>
    public Action<string>? ViewMemoryChanged { get; set; }

    private SettingsViewMemory _viewMemory = SettingsViewMemory.Empty;
    private string _lastViewMemory = string.Empty;
    private bool _viewMemoryCaptured;
    private bool _restoringInitialView;
    // The remembered scroll offset of the first page, until something else navigates (re-applied once the window has opened).
    private double? _restoredScroll;
    // Each card's expansion as built from XAML (before any load or restore): only differences from it are remembered.
    private readonly Dictionary<SettingsCard, bool> _initialCardExpanded = new();

    /// <summary>The text last captured when the window closed (tests).</summary>
    internal string CapturedViewMemoryForTests => _lastViewMemory;

    /// <summary>The page the window is showing (tests).</summary>
    internal string ActivePageKeyForTests => _activeCategory;

    /// <summary>Restores the remembered page, open sections and scroll before the window is shown. Anything unusable keeps the default page.</summary>
    private void RestoreViewMemory(GlideSettingsState state)
    {
        try
        {
            var memory = SettingsViewMemory.Parse(state.SettingsViewMemory);
            _viewMemory = memory;
            if (memory.IsEmpty) return;

            // "Always expand" opens every section anyway; the remembered open / closed state stays out of its way.
            if (SettingsAlwaysExpandSwitch.IsChecked != true)
            {
                _bulkExpandChange = true;
                try
                {
                    foreach (var (id, expanded) in memory.Cards)
                        if (_cardById.TryGetValue(id, out var card) && card.IsExpanded != expanded) card.IsExpanded = expanded;
                }
                finally { _bulkExpandChange = false; }
                UpdateExpandCollapseButtons();
            }

            foreach (var (page, offset) in memory.Scroll)
                if (_panels.ContainsKey(page)) _categoryScrollOffsets[page] = offset;

            // A page that no longer exists (renamed or removed in a later version) leaves the default page showing.
            if (memory.Page.Length == 0 || !_panels.ContainsKey(memory.Page)) return;
            _restoringInitialView = true;
            try
            {
                // No page is "active" yet, so ShowCategory does not store the not yet scrolled offset over the remembered one.
                var previous = _activeCategory;
                _activeCategory = string.Empty;
                try { ShowCategory(memory.Page, clearSearch: false, restoreScroll: true); }
                catch { _activeCategory = previous; throw; }
            }
            finally { _restoringInitialView = false; }
            if (_categoryScrollOffsets.TryGetValue(memory.Page, out var wanted) && wanted > 0) _restoredScroll = wanted;
        }
        catch
        {
            // A broken remembered view must never stop Settings from opening; the default page stays.
        }
    }

    /// <summary>After the window is on screen, puts the first page back at its remembered offset (layout is final by then).</summary>
    private void ReapplyRestoredScrollWhenOpened()
    {
        if (_restoredScroll is null) return;
        var page = _activeCategory;
        Dispatcher.UIThread.Post(() =>
        {
            if (_restoredScroll is not { } wanted) return;
            _restoredScroll = null;
            if (!string.Equals(page, _activeCategory, StringComparison.Ordinal) || SearchPanel.IsVisible) return;
            ContentScroll.UpdateLayout();
            ContentScroll.Offset = new Vector(ContentScroll.Offset.X, Math.Clamp(wanted, 0, Math.Max(0, ContentScroll.ScrollBarMaximum.Y)));
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Takes the view to remember when the window closes. Also stores it in the committed state, so a Cancel rollback that
    /// re-applies that state cannot bring back an older view.
    /// </summary>
    private void CaptureViewMemory()
    {
        try
        {
            // Closed before the first layout: the remembered offsets are still the truth (the page sits at 0 until then).
            if (_restoredScroll is null) RememberCategoryScroll();
            var scroll = new Dictionary<string, double>(_viewMemory.Scroll, StringComparer.OrdinalIgnoreCase);
            foreach (var (page, offset) in _categoryScrollOffsets) scroll[page] = offset;

            var cards = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            if (SettingsAlwaysExpandSwitch.IsChecked == true)
            {
                foreach (var (id, expanded) in _viewMemory.Cards) cards[id] = expanded;
            }
            else
            {
                foreach (var (id, card) in _cardById)
                    if (_initialCardExpanded.TryGetValue(card, out var initial) && initial != card.IsExpanded) cards[id] = card.IsExpanded;
            }

            _lastViewMemory = new SettingsViewMemory(_activeCategory, scroll, cards).Serialize();
            _viewMemoryCaptured = true;
            _committed.SettingsViewMemory = _lastViewMemory;
        }
        catch { /* best effort; the last captured view stays */ }
    }
}
