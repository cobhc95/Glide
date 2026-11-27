using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Glide.App.Controls;
using Glide.App.Explorer;
using Glide.App.Services;
using Glide.Core.Commands;
using Glide.Core.Workspace;

namespace Glide.App;

/// <summary>Managed Explorer tab, part 2: pointer, keyboard, rubber-band selection and drag and drop.</summary>
public partial class MainWindow
{
    private static readonly DataFormat<string> ExplorerDragFormat = DataFormat.CreateStringApplicationFormat(ExplorerHost.DragFormatName);
    private const double ExplorerDragThreshold = 6;

    private bool _explorerRubberActive;
    private Point _explorerRubberStart;      // ItemsRepeater coordinates
    private bool _explorerRubberAdditive;
    // Ctrl+drag (round 3 #8): items the band covers flip their state against the selection the drag started with,
    // as in Windows File Explorer, so a Ctrl+drag over selected items deselects them. Shift+drag only adds.
    private bool _explorerRubberToggle;
    private HashSet<string> _explorerRubberBase = new(StringComparer.OrdinalIgnoreCase);
    private ExplorerEntry? _explorerDragCandidate;
    private Point _explorerDragOrigin;
    private bool _explorerDragging;
    // A plain press on an item of a multi-selection keeps the selection (it may become a drag); the release then
    // reduces the selection to that item, as in Windows File Explorer. Cleared when a drag starts.
    private ExplorerEntry? _explorerSelectOnRelease;
    private string _explorerTypeAhead = "";
    private long _explorerTypeAheadTick;

    // ------------------------------------------------------------------------------------------
    // Wiring (called once from EnsureBrowserSurface)
    // ------------------------------------------------------------------------------------------

    private void WireExplorerInput(BrowserSurface browser)
    {
        var scroll = browser.ScrollViewer;
        scroll.AddHandler(InputElement.PointerPressedEvent, BrowserItemPointerPressed, RoutingStrategies.Bubble, true);
        scroll.AddHandler(InputElement.PointerMovedEvent, ExplorerPointerMoved, RoutingStrategies.Bubble, true);
        scroll.AddHandler(InputElement.PointerReleasedEvent, ExplorerPointerReleased, RoutingStrategies.Bubble, true);
        scroll.AddHandler(InputElement.PointerCaptureLostEvent, (_, _) => EndExplorerRubberBand(), RoutingStrategies.Bubble, true);
        scroll.GotFocus += (_, _) => SetExplorerListFocus(true);
        scroll.LostFocus += (_, _) => SetExplorerListFocus(false);
        // Tunnel: the ScrollViewer itself consumes Up/Down/PageUp/PageDown/Home/End to scroll before a bubble handler could see them.
        browser.AddHandler(InputElement.KeyDownEvent, ExplorerSurfaceKeyDown, RoutingStrategies.Tunnel, false);
        browser.AddHandler(InputElement.TextInputEvent, ExplorerSurfaceTextInput, RoutingStrategies.Bubble, false);
        DragDrop.SetAllowDrop(scroll, true);
        AttachExplorerDropTarget(scroll, () => (_workspace.Active as BrowserTabState)?.Folder, null);
        WireExplorerClickRenameCancellation(browser);
    }

    // ------------------------------------------------------------------------------------------
    // Drive bar divider (round 3 #12): drag to resize; the width is remembered (explorer.navPaneWidth)
    // ------------------------------------------------------------------------------------------

    private bool _explorerNavResizing;
    private double _explorerNavResizeStartX;
    private double _explorerNavResizeStartWidth;

    private void WireExplorerNavSplitter(BrowserSurface browser)
    {
        var grip = browser.NavSplitter;
        grip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;
            _explorerNavResizing = true;
            _explorerNavResizeStartX = e.GetPosition(browser).X;
            _explorerNavResizeStartWidth = browser.NavPaneWidth;
            e.Pointer.Capture(grip);
            e.Handled = true;
        };
        grip.PointerMoved += (_, e) =>
        {
            if (!_explorerNavResizing) return;
            // Never wider than leaves 200 px for the thumbnails.
            var max = Math.Max(ExplorerLayout.NavPaneMinWidth, browser.Bounds.Width - 200);
            var width = Math.Min(ExplorerLayout.ClampNavPaneWidth(_explorerNavResizeStartWidth + e.GetPosition(browser).X - _explorerNavResizeStartX), max);
            browser.NavPaneWidth = width;
            e.Handled = true;
        };
        void Finish(bool save)
        {
            if (!_explorerNavResizing) return;
            _explorerNavResizing = false;
            var width = ExplorerLayout.ClampNavPaneWidth(browser.NavPaneWidth);
            if (save && width != _settings.ExplorerNavPaneWidth)
            {
                _settings.ExplorerNavPaneWidth = width;
                SaveSettingsAndPublish();
            }
        }
        grip.PointerReleased += (_, e) => { Finish(save: true); e.Pointer.Capture(null); e.Handled = true; };
        grip.PointerCaptureLost += (_, _) => Finish(save: true);
        ApplyExplorerNavPaneWidth();
    }

    private void ApplyExplorerNavPaneWidth()
    {
        if (_browserSurface is not { } surface || _explorerNavResizing) return;
        surface.NavPaneWidth = ExplorerLayout.ClampNavPaneWidth(_settings.ExplorerNavPaneWidth);
    }

    private void SetExplorerListFocus(bool focused)
    {
        if (_explorerListHasFocus == focused) return;
        _explorerListHasFocus = focused;
        foreach (var tile in RealizedExplorerTiles()) UpdateExplorerTileVisual(tile);
    }

    // ------------------------------------------------------------------------------------------
    // Pointer
    // ------------------------------------------------------------------------------------------

    private ExplorerEntry? ExplorerEntryAt(object? source)
    {
        for (var visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is Border border && border.Tag is ExplorerEntry entry && border.Classes.Contains("exTile")) return entry;
            if (visual is ScrollBar) return null;
        }
        return null;
    }

    /// <summary>
    /// The control a context menu opens on: the item's tile, or the list for the background. Never the element under the
    /// pointer, whose inherited properties (a caption's centred TextAlignment) the menu would take over.
    /// </summary>
    private Control ExplorerMenuAnchor(ExplorerEntry? entry) =>
        (entry is not null ? ExplorerTileFor(ExplorerIndexOf(entry)) : null) ?? (Control?)_browserSurface?.ScrollViewer ?? this;

    private static bool ExplorerSourceIsScrollBar(object? source)
    {
        for (var visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
            if (visual is ScrollBar) return true;
        return false;
    }

    /// <summary>
    /// The click (or quick second click) that confirmed a modal dialog can reach the list the dialog was covering as the dialog
    /// closes: Avalonia counts clicks by window-relative position and time across windows, so a press on the list a moment after
    /// the dialog closed at the same spot can arrive as a double-click and open the item under it (5.1.7 UI test bug 7: clicking
    /// Rename in the batch rename dialog also opened a picture). Presses in this short window after such a dialog are ignored.
    /// </summary>
    internal const long ExplorerInputGraceAfterDialogMs = 400;
    private long _explorerIgnorePressesUntilTick;

    private bool ExplorerPressIgnoredAfterDialog() => Environment.TickCount64 < _explorerIgnorePressesUntilTick;

    /// <summary>Central press handler for the file list (tiles, rows, empty space).</summary>
    private async void BrowserItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_browserSurface is not { } surface || ExplorerSourceIsScrollBar(e.Source))
        {
            if (UiTrace.Input) UiTrace.WriteInput("press_skipped", $"surface={_browserSurface is not null} scrollBar={ExplorerSourceIsScrollBar(e.Source)} source={DescribeVisual(e.Source as Visual)}");
            return;
        }
        if (ExplorerPressIgnoredAfterDialog())
        {
            if (UiTrace.Input) UiTrace.WriteInput("press_ignored_after_dialog", $"remainingMs={_explorerIgnorePressesUntilTick - Environment.TickCount64}");
            e.Handled = true;
            return;
        }
        var properties = e.GetCurrentPoint(surface.ScrollViewer).Properties;
        var entry = ExplorerEntryAt(e.Source);
        if (UiTrace.Input)
            UiTrace.WriteInput("press", $"kind={properties.PointerUpdateKind} left={properties.IsLeftButtonPressed} middle={properties.IsMiddleButtonPressed} right={properties.IsRightButtonPressed} " +
                $"clicks={e.ClickCount} keys={e.KeyModifiers} entry={entry?.Path ?? "none"} source={DescribeVisual(e.Source as Visual)} captured={DescribeVisual(e.Pointer.Captured as Visual)} " +
                $"activeTab={_workspace.Active?.GetType().Name} displayed={_explorerDisplayedTabId} browserVisible={BrowserView.IsVisible} imageVisible={ImageView.IsVisible}");
        _explorerSelectOnRelease = null;
        // Any press cancels a pending click-to-rename; a plain left press on the name may arm a new one (below).
        CancelExplorerClickRename();
        CommitOrCancelExplorerRename(commit: true);
        if (properties.IsMiddleButtonPressed)
        {
            // Middle-click = the other open action (new tab vs. this tab; folders open in a new
            // Explorer tab), mirroring a web browser.
            if (entry is null) { UiTrace.WriteInput("middle_no_entry"); return; }
            e.Handled = true;
            await OpenExplorerEntryFromPointerAsync(entry.Path, alternate: true, e.KeyModifiers);
            return;
        }
        surface.ScrollViewer.Focus();
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (properties.IsRightButtonPressed)
        {
            // The menu itself opens on release (like Explorer). Right-clicking an unselected item
            // selects it first; right-clicking empty space clears the selection. Right-clicking inside a
            // multi-selection keeps it and makes the clicked item the focused one (its Properties target).
            if (entry is not null && !_browserSelection.Contains(entry.Path))
                SelectBrowserIndex(ExplorerIndexOf(entry), extend: false, toggle: false, bringIntoView: false);
            else if (entry is not null) { _browserCurrentIndex = ExplorerIndexOf(entry); UpdateRealizedBrowserSelection(); }
            else ClearBrowserSelection();
            e.Handled = true;
            return;
        }
        if (!properties.IsLeftButtonPressed) return;
        if (entry is null)
        {
            // Empty space: start a rubber band (Ctrl/Shift keep the existing selection).
            _explorerRubberAdditive = ctrl || shift;
            _explorerRubberToggle = ctrl;
            _explorerRubberBase = new HashSet<string>(_browserSelection, StringComparer.OrdinalIgnoreCase);
            if (!_explorerRubberAdditive) ClearBrowserSelection();
            _explorerRubberStart = e.GetPosition(surface.Repeater);
            _explorerRubberActive = true;
            e.Pointer.Capture(surface.ScrollViewer);
            return;
        }
        var index = ExplorerIndexOf(entry);
        if (index < 0) return;
        // Click-to-rename (Windows' slow double-click): decided against the selection as it was before this press.
        var wasSoleSelected = _browserSelection.Count == 1 && _browserSelection.Contains(entry.Path);
        NoteExplorerItemLeftPress(entry, e, wasSoleSelected);
        // A plain double-click opens (default action); with Ctrl/Shift/Alt held it only opens when a custom gesture
        // is configured for that modifier, so rapid Ctrl+clicks keep toggling the selection.
        // (Odd counts - a rapid third click - only select, so click-click-click-click is two opens, not a chain.)
        if (e.ClickCount >= 2 && e.ClickCount % 2 == 0 && (e.KeyModifiers == KeyModifiers.None ||
            ResolveExplorerDoubleClick(e.KeyModifiers).Action != GestureCatalog.Default))
        {
            _explorerDragCandidate = null;
            e.Handled = true;
            await OpenExplorerEntryFromPointerAsync(entry.Path, alternate: false, e.KeyModifiers);
            return;
        }
        // Pressing an already selected item (without modifiers) keeps the multi-selection so it can be dragged; a release
        // without a drag selects just that item (ExplorerPointerReleased).
        if (!(ctrl || shift) && _browserSelection.Contains(entry.Path) && _browserSelection.Count > 1)
        {
            _browserCurrentIndex = index;
            UpdateRealizedBrowserSelection();
            _explorerSelectOnRelease = entry;
        }
        else SelectBrowserIndex(index, extend: shift, toggle: ctrl, bringIntoView: false);
        _explorerDragCandidate = entry;
        _explorerDragOrigin = e.GetPosition(surface);
        _explorerDragging = false;
    }

    /// <summary>
    /// Opens the entry a pointer press landed on (double-click, or middle-click for the alternate action). This runs from an async
    /// void handler, and the app's dispatcher handler swallows whatever escapes such a handler (App.axaml.cs: it only logs to the
    /// live diagnostic trace, which is normally off), so a failure here used to look exactly like "the click did nothing". It is
    /// now written to startup-trace.log always, and every step is traced under GLIDE_TRACE_INPUT=1.
    /// </summary>
    private async Task OpenExplorerEntryFromPointerAsync(string path, bool alternate, KeyModifiers modifiers)
    {
        try
        {
            await OpenBrowserEntryAsync(path, alternate, modifiers);
            if (UiTrace.Input) UiTrace.WriteInput("open_returned", $"path={path} alternate={alternate} tabs={_workspace.Tabs.Count} active={_workspace.Active?.GetType().Name}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StartupTrace.Write("explorer_open_failed", $"path={path} alternate={alternate} error={ex.GetType().Name}: {ex.Message}");
            _diagnostics.Write("browser", "open_failed", new { path, alternate, error = ex.GetType().Name, ex.Message });
        }
    }

    private void ExplorerPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_browserSurface is not { } surface) return;
        if (_explorerRubberActive)
        {
            if (!e.GetCurrentPoint(surface.ScrollViewer).Properties.IsLeftButtonPressed) { EndExplorerRubberBand(); return; }
            UpdateExplorerRubberBand(e.GetPosition(surface.Repeater));
            return;
        }
        if (_explorerDragCandidate is { } candidate && !_explorerDragging && e.GetCurrentPoint(surface).Properties.IsLeftButtonPressed)
        {
            var now = e.GetPosition(surface);
            if (Math.Abs(now.X - _explorerDragOrigin.X) + Math.Abs(now.Y - _explorerDragOrigin.Y) < ExplorerDragThreshold) return;
            _explorerDragging = true;
            _explorerDragCandidate = null;
            _explorerSelectOnRelease = null;
            NoteExplorerDragStarted();
            _ = StartExplorerDragAsync(candidate, e);
        }
    }

    private void ExplorerPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_browserSurface is not { } surface) return;
        var wasRubber = _explorerRubberActive;
        EndExplorerRubberBand();
        _explorerDragCandidate = null;
        NoteExplorerPointerReleased(e);
        if (_explorerSelectOnRelease is { } clicked)
        {
            _explorerSelectOnRelease = null;
            var index = e.InitialPressMouseButton == MouseButton.Left ? ExplorerIndexOf(clicked) : -1;
            if (index >= 0) SelectBrowserIndex(index, extend: false, toggle: false, bringIntoView: false);
        }
        if (e.InitialPressMouseButton == MouseButton.Right && !ExplorerSourceIsScrollBar(e.Source))
        {
            e.Handled = true;
            var entry = ExplorerEntryAt(e.Source);
            ShowExplorerContextMenu(entry, ExplorerMenuAnchor(entry));
            return;
        }
        if (wasRubber) e.Handled = true;
    }

    private void UpdateExplorerRubberBand(Point current)
    {
        if (_browserSurface is not { } surface) return;
        var rect = new Rect(
            new Point(Math.Min(_explorerRubberStart.X, current.X), Math.Min(_explorerRubberStart.Y, current.Y)),
            new Point(Math.Max(_explorerRubberStart.X, current.X), Math.Max(_explorerRubberStart.Y, current.Y)));
        // Draw in the overlay layer's coordinates (same origin as the scroll viewer).
        var origin = surface.Repeater.TranslatePoint(rect.TopLeft, surface.RubberLayer) ?? rect.TopLeft;
        var rubber = surface.RubberRect;
        rubber.IsVisible = true;
        rubber.Width = rect.Width;
        rubber.Height = rect.Height;
        Canvas.SetLeft(rubber, origin.X);
        Canvas.SetTop(rubber, origin.Y);
        rubber.Background = new SolidColorBrush(Color.Parse("#334A9EE8"));

        var selection = new HashSet<string>(_explorerRubberAdditive ? _explorerRubberBase : new HashSet<string>(), StringComparer.OrdinalIgnoreCase);
        var first = -1;
        for (var i = 0; i < _browserEntries.Count; i++)
        {
            if (!ExplorerTileRect(i).Intersects(rect)) continue;
            if (_explorerRubberToggle && _explorerRubberBase.Contains(_browserEntries[i].Path)) selection.Remove(_browserEntries[i].Path);
            else selection.Add(_browserEntries[i].Path);
            if (first < 0) first = i;
        }
        _browserSelection.Clear();
        foreach (var path in selection) _browserSelection.Add(path);
        if (first >= 0) { _browserCurrentIndex = first; if (!_explorerRubberAdditive) _browserSelectionAnchor = first; }
        UpdateRealizedBrowserSelection();
    }

    private void EndExplorerRubberBand()
    {
        if (!_explorerRubberActive && _browserSurface?.RubberRect.IsVisible != true) return;
        _explorerRubberActive = false;
        if (_browserSurface is { } surface) surface.RubberRect.IsVisible = false;
    }

    // ------------------------------------------------------------------------------------------
    // Keyboard
    // ------------------------------------------------------------------------------------------

    private async void ExplorerSurfaceKeyDown(object? sender, KeyEventArgs e)
    {
        if (_browserSurface is not { } surface || _workspace.Active is not BrowserTabState browser) return;
        // A key press cancels a pending click-to-rename.
        CancelExplorerClickRename();
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);

        // Shortcuts that also work while a text box (address / filter / rename) has focus.
        if (ctrl && !shift && !alt && e.Key == Key.F) { FocusExplorerFilter(); e.Handled = true; return; }
        if (e.Source is TextBox)
        {
            if ((ctrl && e.Key == Key.L) || (alt && e.Key == Key.D) || e.Key == Key.F4) { surface.AddressBar.BeginEdit(); e.Handled = true; }
            return;
        }
        // Keys typed while a toolbar button / combo box / navigation item has focus belong to that control,
        // except the navigation shortcuts that make sense anywhere in the Explorer.
        var inList = ExplorerSourceInList(e.Source);
        if (!inList && !(alt && !ctrl && !shift) && e.Key != Key.F5 && e.Key != Key.F4 && e.Key != Key.F3) return;

        if (alt && !ctrl && !shift)
        {
            // Alt+Left / Alt+Right are not Explorer keys any more (5.0.0): they are the editable defaults of Back / Forward
            // and reach the tab history through the hotkey map (the window's key handler), like everywhere else.
            switch (e.Key)
            {
                case Key.Up: BrowserUpClicked(surface, new RoutedEventArgs()); e.Handled = true; return;
                case Key.D: surface.AddressBar.BeginEdit(); e.Handled = true; return;
                case Key.Enter: ShowExplorerProperties(); e.Handled = true; return;
            }
            // Any other Alt chord (Alt+Left / Alt+Right = Back / Forward by default) goes through the hotkey map now, before
            // a list control can take the arrow key for itself.
            await RouteHotkeyAsync(e);
            return;
        }

        if (ctrl && !alt)
        {
            switch (e.Key)
            {
                case Key.A when !shift: SelectAllBrowserEntries(); e.Handled = true; return;
                case Key.C when !shift: ExplorerCopyOrCut(cut: false); e.Handled = true; return;
                // Windows 11: Ctrl+Shift+C copies the selection's full path(s) as text.
                case Key.C: await ExplorerCopyPathsAsync(SelectedExplorerEntries().Select(x => x.Path).ToList()); e.Handled = true; return;
                // The classic clipboard pair Ctrl+Insert (copy) and Shift+Insert (paste, below).
                case Key.Insert when !shift: ExplorerCopyOrCut(cut: false); e.Handled = true; return;
                // Ctrl+Shift+1..6 pick the view like Windows' Extra large / Large / Medium / Small / List / Details.
                case >= Key.D1 and <= Key.D6 when shift: SetExplorerViewMode(e.Key - Key.D1); e.Handled = true; return;
                case Key.X when !shift: ExplorerCopyOrCut(cut: true); e.Handled = true; return;
                case Key.V when !shift: await ExplorerPasteAsync(browser.Folder); e.Handled = true; return;
                case Key.N when shift: await ExplorerNewFolderAsync(); e.Handled = true; return;
                case Key.L when !shift: surface.AddressBar.BeginEdit(); e.Handled = true; return;
                case Key.R when !shift: BrowserRefreshClicked(surface, new RoutedEventArgs()); e.Handled = true; return;
                case Key.Space when !shift:
                    if (_browserCurrentIndex >= 0) { SelectBrowserIndex(_browserCurrentIndex, extend: false, toggle: true, bringIntoView: true); }
                    e.Handled = true; return;
            }
        }

        switch (e.Key)
        {
            case Key.F5: BrowserRefreshClicked(surface, new RoutedEventArgs()); e.Handled = true; return;
            case Key.F4: surface.AddressBar.BeginEdit(); e.Handled = true; return;
            case Key.F3 when !ctrl && !shift: FocusExplorerFilter(); e.Handled = true; return;
            case Key.Insert when shift && !ctrl: await ExplorerPasteAsync(browser.Folder); e.Handled = true; return;
            case Key.F2 when !ctrl && !shift: ExplorerBeginRenameSelection(); e.Handled = true; return;
            case Key.Delete when !ctrl: await ExplorerDeleteSelectionAsync(permanent: shift); e.Handled = true; return;
            // Escape clears the selection (round 3 #9). With nothing selected it is left to the hotkey map.
            case Key.Escape when !ctrl && !shift && _browserSelection.Count > 0:
                EndExplorerRubberBand();
                ClearBrowserSelection();
                e.Handled = true; return;
            case Key.Apps:
            case Key.F10 when shift:
                ShowExplorerContextMenuAtCurrent();
                e.Handled = true; return;
            case Key.Enter when !ctrl && !shift:
            {
                var selected = SelectedExplorerEntries();
                if (selected.Count == 1) await OpenBrowserEntryAsync(selected[0].Path, alternate: false, e.KeyModifiers);
                // Enter is always handled here so it never falls through to the viewer's fullscreen hotkey.
                e.Handled = true; return;
            }
            // Backspace is not handled here: it is the default key of Back (nav.back, registry action), so it reaches
            // the window's hotkey route like every other bound key and goes Back through the tab history. A user who
            // moves or removes the binding moves or removes it in the Explorer too.
        }

        if (_browserEntries.Count == 0) return;
        var columns = ExplorerColumnCount();
        var details = ExplorerIsDetails;
        var current = _browserCurrentIndex >= 0 ? _browserCurrentIndex : _browserSelectionAnchor >= 0 ? _browserSelectionAnchor : -1;
        var last = _browserEntries.Count - 1;
        var visibleRows = Math.Max(1, (int)(surface.ScrollViewer.Viewport.Height / (ExplorerViewModes.TileSize(_browserViewMode, ExplorerInfoLines).Height + (details ? 0 : ExplorerGridSpacing))));
        int next;
        switch (e.Key)
        {
            case Key.Left when !details: next = current < 0 ? 0 : Math.Max(0, current - 1); break;
            case Key.Right when !details: next = current < 0 ? 0 : Math.Min(last, current + 1); break;
            // Up on the first row / Down on the last row stay where they are (Windows); Down from a full row onto a shorter last row lands on its last item.
            case Key.Up: next = current < 0 ? 0 : current < columns ? current : current - columns; break;
            case Key.Down: next = current < 0 ? 0 : current / columns == last / columns ? current : Math.Min(last, current + columns); break;
            // Shift+Home / Shift+End extend the selection to the start / end of the current row (round 3 #10);
            // Ctrl+Shift+Home / End, plain Home / End and the one-column Details view go to the folder's first / last item.
            case Key.Home when shift && !ctrl && !details && current >= 0: next = current - current % columns; break;
            case Key.End when shift && !ctrl && !details && current >= 0: next = Math.Min(last, current - current % columns + columns - 1); break;
            case Key.Home: next = 0; break;
            case Key.End: next = last; break;
            case Key.PageUp: next = current < 0 ? 0 : Math.Max(0, current - visibleRows * columns); break;
            case Key.PageDown: next = current < 0 ? 0 : Math.Min(last, current + visibleRows * columns); break;
            default: return;
        }
        e.Handled = true;
        if (ctrl && !shift)
        {
            // Ctrl+arrow moves the focus rectangle only; Ctrl+Space then toggles the item.
            _browserCurrentIndex = next;
            UpdateRealizedBrowserSelection();
            EnsureExplorerIndexVisible(next);
            return;
        }
        SelectBrowserIndex(next, extend: shift, toggle: false, bringIntoView: true);
    }

    private void FocusExplorerFilter()
    {
        if (_browserSurface is not { } surface) return;
        surface.FilterBox.Focus();
        surface.FilterBox.SelectAll();
    }

    /// <summary>Window of time (ms) in which further typed characters extend the type-to-select prefix, as in Windows Explorer.</summary>
    private const long ExplorerTypeAheadWindowMs = 1000;

    private bool ExplorerTypeAheadActive => _explorerTypeAhead.Length > 0 && Environment.TickCount64 - _explorerTypeAheadTick <= ExplorerTypeAheadWindowMs;

    private bool ExplorerSourceInList(object? source) =>
        _browserSurface is { } surface &&
        (source is not Visual visual || ReferenceEquals(visual, surface) || visual.GetSelfAndVisualAncestors().Any(v => ReferenceEquals(v, surface.ScrollViewer)));

    /// <summary>
    /// Windows File Explorer type-to-select wins over single-letter hotkeys while the Explorer list has the keyboard: an
    /// unmodified (or Shift-only) letter, unshifted digit, number-pad digit or - . , is a character of the name being typed, so it is not routed
    /// to the hotkey map (F, O, U, ... by default). Space joins the prefix while one is being typed. Ctrl / Alt / Win chords,
    /// function keys, Enter, Backspace, the arrows and every key outside the list or inside a text box keep their hotkeys.
    /// The key is left unhandled so the platform's text input still reaches <see cref="ExplorerSurfaceTextInput"/>.
    /// </summary>
    private bool IsExplorerTypeAheadKey(KeyEventArgs e)
    {
        if (_workspace.Active is not BrowserTabState || _browserSurface is null || e.Source is TextBox) return false;
        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0 || !ExplorerSourceInList(e.Source)) return false;
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (e.Key is >= Key.A and <= Key.Z) return true;
        // Shift + digit types a symbol (! @ #), not a digit: those chords stay hotkeys (Shift+1..3 = external programs, which also work from an Explorer tab).
        if (e.Key is >= Key.D0 and <= Key.D9 && !shift) return true;
        if (e.Key is >= Key.NumPad0 and <= Key.NumPad9) return true;
        // Names that start with - _ . or , (the "+" / "=" keys have no such names to find and stay hotkeys).
        if (e.Key is Key.OemMinus or Key.OemPeriod or Key.OemComma) return true;
        return e.Key == Key.Space && ExplorerTypeAheadActive;
    }

    private void ExplorerSurfaceTextInput(object? sender, TextInputEventArgs e)
    {
        if (e.Source is TextBox || string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0]) || _browserEntries.Count == 0 || !ExplorerSourceInList(e.Source)) return;
        var now = Environment.TickCount64;
        if (now - _explorerTypeAheadTick > ExplorerTypeAheadWindowMs) _explorerTypeAhead = "";
        // A space that does not continue a prefix is not a name character (nothing starts with one): leave it alone.
        if (_explorerTypeAhead.Length == 0 && string.IsNullOrWhiteSpace(e.Text)) return;
        _explorerTypeAheadTick = now;
        _explorerTypeAhead += e.Text;
        // A single character searches after the focused item (the selected one), so pressing it again moves on; with nothing
        // selected the search starts at the first item. A longer prefix refines the match, starting at the current item.
        var currentSelected = _browserCurrentIndex >= 0 && _browserCurrentIndex < _browserEntries.Count && _browserSelection.Contains(_browserEntries[_browserCurrentIndex].Path);
        var start = Math.Max(0, _browserCurrentIndex);
        // The same letter typed repeatedly cycles through the items that start with it.
        var repeated = _explorerTypeAhead.Length > 1 && _explorerTypeAhead.All(c => char.ToLowerInvariant(c) == char.ToLowerInvariant(_explorerTypeAhead[0]));
        var prefix = repeated ? _explorerTypeAhead[..1] : _explorerTypeAhead;
        var offset = currentSelected && (repeated || _explorerTypeAhead.Length == 1) ? 1 : 0;
        for (var step = 0; step < _browserEntries.Count; step++)
        {
            var index = (start + offset + step) % _browserEntries.Count;
            if (_browserEntries[index].Name.StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase))
            {
                SelectBrowserIndex(index, extend: false, toggle: false, bringIntoView: true);
                break;
            }
        }
        e.Handled = true;
    }

    // ------------------------------------------------------------------------------------------
    // Drag and drop (move; Ctrl = copy). Out to other apps is best effort via the storage items.
    // ------------------------------------------------------------------------------------------

    /// <summary>Paths carried by a Glide Explorer drag, or null when the drag comes from elsewhere.</summary>
    private static IReadOnlyList<string>? ExplorerDragPaths(DragEventArgs e)
    {
        try
        {
            var values = e.DataTransfer.TryGetValues(ExplorerDragFormat);
            if (values is not { Length: > 0 }) return null;
            return values.SelectMany(v => v.Split('\n', StringSplitOptions.RemoveEmptyEntries)).ToList();
        }
        catch { return null; }
    }

    private async Task StartExplorerDragAsync(ExplorerEntry origin, PointerEventArgs trigger)
    {
        try
        {
            var paths = _browserSelection.Contains(origin.Path) ? SelectedExplorerEntries().Select(x => x.Path).ToList() : new List<string> { origin.Path };
            var data = new DataTransfer();
            var provider = TopLevel.GetTopLevel(this)?.StorageProvider;
            foreach (var path in paths)
            {
                var item = new DataTransferItem();
                item.Set(ExplorerDragFormat, path);
                if (provider is not null)
                {
                    try
                    {
                        IStorageItem? storage = Directory.Exists(path)
                            ? await provider.TryGetFolderFromPathAsync(new Uri(path))
                            : await provider.TryGetFileFromPathAsync(new Uri(path));
                        if (storage is not null) item.SetFile(storage);
                    }
                    catch { /* drag payload is optional; the drag proceeds without the storage item */ }
                }
                data.Add(item);
            }
            await DragDrop.DoDragDropAsync(trigger, data, DragDropEffects.Move | DragDropEffects.Copy);
        }
        catch (Exception ex) { _diagnostics.Write("browser", "drag_failed", new { error = ex.GetType().Name, ex.Message }); }
        finally { _explorerDragging = false; }
    }

    /// <summary>Makes a control accept Explorer drags: dropping moves the items into the folder it stands for.</summary>
    private void AttachExplorerDropTarget(Control target, Func<string?> folder, string? highlightClass)
    {
        DragDrop.SetAllowDrop(target, true);
        void Highlight(bool on) { if (highlightClass is not null) target.Classes.Set(highlightClass, on); }
        target.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            var paths = ExplorerDragPaths(e);
            var destination = folder();
            if (paths is null || destination is null || !CanDropExplorerPaths(paths, destination, ExplorerDropIsCopy(paths, destination, e.KeyModifiers)))
            {
                if (paths is not null) { e.DragEffects = DragDropEffects.None; e.Handled = true; }
                Highlight(false);
                return;
            }
            e.DragEffects = ExplorerDropIsCopy(paths, destination, e.KeyModifiers) ? DragDropEffects.Copy : DragDropEffects.Move;
            Highlight(true);
            e.Handled = true;
        });
        target.AddHandler(DragDrop.DragLeaveEvent, (_, _) => Highlight(false));
        target.AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            Highlight(false);
            var paths = ExplorerDragPaths(e);
            var destination = folder();
            if (paths is null || destination is null) return;
            e.Handled = true;
            var copy = ExplorerDropIsCopy(paths, destination, e.KeyModifiers);
            if (!CanDropExplorerPaths(paths, destination, copy)) return;
            await ExplorerTransferAsync(paths, destination, move: !copy);
        });
    }

    /// <summary>
    /// Windows' drop rule: Ctrl copies, Shift moves, and with neither the items move within one drive and are copied to another
    /// (Alt / Ctrl+Shift "create shortcut" is not supported). Ctrl wins over Shift.
    /// </summary>
    internal static bool ExplorerDropIsCopy(IReadOnlyList<string> paths, string destination, KeyModifiers modifiers)
    {
        if (modifiers.HasFlag(KeyModifiers.Control)) return true;
        if (modifiers.HasFlag(KeyModifiers.Shift)) return false;
        var destinationRoot = Path.GetPathRoot(destination);
        return paths.Any(p => !string.Equals(Path.GetPathRoot(p), destinationRoot, StringComparison.OrdinalIgnoreCase));
    }

    private static bool CanDropExplorerPaths(IReadOnlyList<string> paths, string destination, bool copy)
    {
        if (!Directory.Exists(destination)) return false;
        foreach (var path in paths)
        {
            var parent = Path.GetDirectoryName(path.TrimEnd('\\'));
            if (!copy && string.Equals(parent, destination.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return false; // moving into where it already is
            var full = Path.GetFullPath(path).TrimEnd('\\');
            var dest = Path.GetFullPath(destination).TrimEnd('\\');
            if (string.Equals(full, dest, StringComparison.OrdinalIgnoreCase)) return false;
            if (Directory.Exists(path) && dest.StartsWith(full + "\\", StringComparison.OrdinalIgnoreCase)) return false;
        }
        return paths.Count > 0;
    }
}
