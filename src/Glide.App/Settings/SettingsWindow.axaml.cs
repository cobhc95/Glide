using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.LogicalTree;
using Glide.App.Controls;
using Glide.App.Platform;
using Glide.App.Services;
using Glide.Core.Settings;
using Glide.Core.Commands;
using Glide.Imaging;

namespace Glide.App.Settings;

/// <summary>
/// Settings is a staged editor over one explicit GlideSettingsState.  Enabled controls either affect
/// the running viewer immediately or persist a concrete value; future-subsystem controls are disabled
/// instead of being fake-active. Apply/OK commit, Cancel/native close rolls preview back.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly Action<GlideSettingsState, bool> _apply;
    private bool _previewDirty;
    private GlideSettingsState _committed;
    private bool _loadingControls;
    private bool _closeWithoutRollback;
    private readonly Dictionary<string, StackPanel> _panels;
    private Dictionary<string, List<string>> _workingHotkeys = new(StringComparer.OrdinalIgnoreCase);
    private string? _hotkeyCaptureActionId;
    private string _escRememberedChoice = "Ask";
    private string? _hotkeyCaptureOldShortcut;
    private bool _hotkeyCaptureAdd;
    private bool _hotkeyCaptureFromSearch;
    private ListBoxItem? _hotkeyCaptureRow;
    private Border? _hotkeyCaptureSearchCard;
    private readonly Dictionary<string, (Border Card, TextBlock Shortcut)> _hotkeySearchRows = new(StringComparer.OrdinalIgnoreCase);
    private bool _hotkeyFlashOn;
    // Keyboard shortcuts page: staged custom presets, list sort order and the "Search hotkey" capture / result view.
    private List<CustomHotkeyPreset> _workingCustomPresets = new();
    private HotkeySortState _hotkeySort = HotkeySortState.Default;
    private bool _hotkeyLookupCapturing;
    private string? _hotkeyLookupShortcut;
    private readonly DispatcherTimer _hotkeyCaptureFlashTimer = new() { Interval = TimeSpan.FromMilliseconds(360) };
    // Rebuilding the editable search cards constructs one live editor per match (up to ~200
    // controls). Coalesce rapid typing so the UI thread is not blocked once per keystroke.
    private readonly DispatcherTimer _searchDebounceTimer = new() { Interval = TimeSpan.FromMilliseconds(140) };
    private string _pendingSearchQuery = string.Empty;
    private static readonly List<string> EmptyShortcutList = new();
    private string _activeCategory = "General";

    /// <summary>One Settings page: its navigation key (= page panel), visible title and subtitle.
    /// The title is also the catalogue <see cref="SettingDefinition.Category"/> of every setting on it.
    /// Listed in navigation-rail order (the search filter and the diagnostic capture use this order).</summary>
    private sealed record CategoryGroup(string Key, string Title, string Subtitle);

    private static readonly CategoryGroup[] CategoryGroups =
    {
        new("General", "General & startup", "What opens at launch and in new tabs, history, and saved profiles"),
        new("Appearance", "Appearance & viewing", "Theme and colours, zoom and fit, image display and the text overlay"),
        new("Interface", "Window & navigation", "Window and fullscreen behaviour, title-bar buttons, and moving through images and folders"),
        new("Mouse", "Mouse & keyboard", "Mouse gestures, keyboard shortcuts, and the Escape and Tab keys"),
        new("Performance", "Performance", "Image quality while browsing, prefetch, fast-browsing previews and memory"),
        new("Slideshow", "Slideshow & overlays", "Slideshow playback, Window-in-Window overlays and Overlay mode"),
        new("Tabs", "Tabs & status bar", "How tabs behave and look, and everything about the status bar"),
        new("Windows", "Windows & developer", "Opening from Explorer, background start, Explorer thumbnails, external programs and diagnostics")
    };

    /// <summary>
    /// Deep links to the panels that existed before the card layout (4.8.0). Each now opens its page
    /// and expands/reveals the card that holds that content.
    /// </summary>
    private static readonly Dictionary<string, (string Page, string CardId)> LegacyPageAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Viewing"] = ("Appearance", "appearance-zoom-fit"),
        ["Status"] = ("Tabs", "tabs-status-bar"),
        ["Hotkeys"] = ("Mouse", "mouse-shortcuts"),
        ["Overlays"] = ("Slideshow", "slideshow-wiw"),
        ["Profiles"] = ("General", "general-profiles"),
        ["Developer"] = ("Windows", "windows-diagnostics")
    };

    private static CategoryGroup? FindGroup(string key)
    {
        if (LegacyPageAliases.TryGetValue(key, out var alias)) key = alias.Page;
        return CategoryGroups.FirstOrDefault(g => string.Equals(g.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    private static CategoryGroup GroupOf(string key) => FindGroup(key) ?? CategoryGroups[0];

    // Card map (built once in the constructor, before the gesture panel moves its secondary options).
    private readonly Dictionary<string, SettingsCard> _settingCard = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Control> _settingAnchors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<SettingsCard>> _cardsByPage = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SettingsCard> _cardById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<SettingsCard, string> _pageOfCard = new();
    // Rows hidden in XAML (legacy controls kept only so saved files round-trip). Never counted.
    private readonly HashSet<string> _hiddenSettingIds = new(StringComparer.OrdinalIgnoreCase);
    // Editor value of every catalogued setting after loading a default GlideSettingsState.
    private readonly Dictionary<string, object?> _defaultValue = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, List<string>> DefaultHotkeyMap = HotkeyCatalog.CreateDefaultMap();
    private readonly Dictionary<string, Control> _settingControls = new(StringComparer.OrdinalIgnoreCase);
    // Global-search index per setting. Built once from catalog metadata plus the on-screen wording
    // of the mapped control, so users can find settings by the text they actually see in the UI.
    private readonly Dictionary<string, string> _searchHaystackCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GestureActionPicker> _gestureEditors = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _workingGestures = GestureCatalog.CreateDefaultMap();
    private List<string> _workingTitleBarButtons = TitleBarButtonCatalog.DefaultButtonIds.ToList();
    private string _workingCustomAccentHex = Glide.App.ThemePalette.GlideBlue;
    private string _workingCustomGlowHex = Glide.App.ThemePalette.GlideBlue;
    private string _workingCustomMainBackgroundHex = Glide.App.ThemePalette.FallbackBackground;
    private string _workingLastProfileDirectory = string.Empty;
    private bool _gestureMatrixBuilt;
    private bool _hotkeyRowsBuilt;
    private const int SettingsTransferSchema = 1;

    // Settings whose editor is legitimately disabled by the current state rather than a defect.
    private static readonly HashSet<string> ConditionallyDisabledSettingIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "escape.resetRememberedClose" // disabled while the remembered Escape close choice is "Ask"
    };

    internal sealed class SettingsTransferDocument
    {
        public int Schema { get; set; } = SettingsTransferSchema;
        public GlideSettingsState Settings { get; set; } = new();
        public IReadOnlyList<CodecProviderArtifact> ProviderInventory { get; set; } = Array.Empty<CodecProviderArtifact>();
    }

    internal sealed class ProfileTransferDocument
    {
        public int Schema { get; set; } = 1;
        public string Name { get; set; } = "Profile";
        public GlideSettingsState Settings { get; set; } = new();
    }

    // Public parameterless constructor keeps the AXAML resource reachable to Avalonia's runtime loader.
    // Production opens use the staged-state constructor below.
    public SettingsWindow() : this(new GlideSettingsState(), (_, _) => { }) { }

    /// <summary>Visible product version, derived from assembly metadata so it cannot drift.</summary>
    private static string ProductVersionLabel()
    {
        var assembly = typeof(SettingsWindow).Assembly;
        var informational = assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;
        var version = string.IsNullOrWhiteSpace(informational)
            ? assembly.GetName().Version?.ToString(3) ?? Glide.Core.AppVersion.Fallback
            : informational;
        return "Glide " + version;
    }

    public SettingsWindow(GlideSettingsState current, Action<GlideSettingsState, bool> apply)
    {
        _committed = current.CloneState();
        // Track whether a live preview has been pushed to the viewer since the last commit. Cancel
        // only needs the (expensive) full rollback re-apply when something was actually previewed.
        _apply = (state, commit) => { _previewDirty = !commit; apply(state, commit); };

        // XAML controls such as ComboBox can raise SelectionChanged while InitializeComponent is
        // still constructing the tree. Treat that phase exactly like a settings load so early events
        // cannot dereference panel/control maps that are intentionally built immediately afterwards.
        _loadingControls = true;
        Glide.Core.GlidePerformanceTrace.Mark("settings_ctor_start");
        InitializeComponent();
        Glide.Core.GlidePerformanceTrace.Mark("settings_xaml_ready");
        // Single-source the visible product version from assembly metadata (Directory.Build.props), so
        // the About/footer label can never drift behind the actual build again.
        AboutVersionText.Text = ProductVersionLabel();
        // Legacy Glide treats static Settings chrome/text as draggable window surface. Interactive
        // controls remain authoritative and must never start a native move loop.
        ReferenceRoot.AddHandler(InputElement.PointerPressedEvent, SettingsSurfacePointerPressed, RoutingStrategies.Tunnel, true);
        _panels = new Dictionary<string, StackPanel>(StringComparer.OrdinalIgnoreCase)
        {
            ["General"] = GeneralPanel,
            ["Appearance"] = AppearancePanel,
            ["Interface"] = InterfacePanel,
            ["Tabs"] = TabsPanel,
            ["Mouse"] = MousePanel,
            ["Slideshow"] = SlideshowPanel,
            ["Performance"] = PerformancePanel,
            ["Windows"] = WindowsPanel
        };

        // One source of page titles: the search filter lists exactly the navigation pages.
        SearchCategoryCombo.Items.Clear();
        SearchCategoryCombo.Items.Add(new ComboBoxItem { Content = "All categories" });
        foreach (var group in CategoryGroups) SearchCategoryCombo.Items.Add(new ComboBoxItem { Content = group.Title });
        SearchCategoryCombo.SelectedIndex = 0;

        BuildSettingControlMap();
        InitializeResetCard();
        Glide.Core.GlidePerformanceTrace.Mark("settings_control_map_ready");
        InitializeResponsiveLayout();
        // The card map must exist before BuildGestureMatrix moves the gesture secondary options out
        // of their XAML host (afterwards only the selected area's options are in the tree).
        BuildCardMap();
        _hotkeyCaptureFlashTimer.Tick += (_, _) => ToggleHotkeyCaptureFlash();
        HotkeyPresetCombo.SelectionChanged += (_, _) => UpdateHotkeyPresetButtons();
        _searchDebounceTimer.Tick += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            // Progressive: result cards are built a few at a time so typing and clicking stay responsive.
            RunSearchRebuildCore(_pendingSearchQuery, progressive: true);
        };
        // "N changed" badges compare against the editors' values for a default state. Capture them by
        // loading defaults first (no live-preview handlers are wired yet and _loadingControls guards the
        // XAML handlers); the real load below overwrites every editor and working field.
        Glide.Core.GlidePerformanceTrace.Mark("settings_card_map_ready");
        CaptureDefaultSnapshot();
        Glide.Core.GlidePerformanceTrace.Mark("settings_defaults_captured");
        LoadControls(_committed);
        Glide.Core.GlidePerformanceTrace.Mark("settings_controls_loaded");
        // IMPORTANT: controls can canonicalize legacy/derived settings while loading (for example a
        // compatibility boolean that is now represented by a policy ComboBox). That normalization is
        // not a user edit. Establish the clean comparison baseline from the fully loaded controls so
        // merely opening Settings can never manufacture a phantom "change" / restart prompt.
        _committed = ReadControls().CloneState();
        Glide.Core.GlidePerformanceTrace.Mark("settings_controls_read");
        BuildProfileRows();
        WireLivePreview();
        ApplySettingsTooltips();
        Glide.Core.GlidePerformanceTrace.Mark("settings_wired");
        KeyDown += SettingsKeyDown;
        // While a shortcut is being captured (Change / Add / Search hotkey) the pressed key must reach the capture
        // first: on the bubble route alone, Avalonia's focus navigation consumed Tab and the arrow keys (so Tab,
        // Ctrl+Shift+Left... could not be assigned or looked up). Only the capture modes use the tunnel route, so
        // Escape in a combo box drop-down still closes the drop-down rather than Settings.
        AddHandler(KeyDownEvent, (sender, e) =>
        {
            if (_hotkeyLookupCapturing || _hotkeyCaptureActionId is not null) SettingsKeyDown(sender, e);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        var startPage = GroupOf(_activeCategory);
        PageTitle.Text = startPage.Title;
        PageSubtitle.Text = startPage.Subtitle;
        UpdateExpandCollapseButtons();
        UpdateDirtyState();

        Opened += SettingsOpened;
        // Settings keeps its own (Cancel-rollback aware) placement code in SettingsWindow.Placement.cs; it shares the
        // resolver and the per-window store (key "settings") with every other window (WindowPlacementCatalog).
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        // Remember the size and position the user sets: restored here, saved by the owner when the window closes.
        RestoreSavedPlacement(current);
        TrackNormalPlacement();
        Closing += SettingsClosing;
        // ContentScroll no longer scrolls to whichever control regains focus (window re-activation
        // used to yank the page back to the last-clicked option). Keyboard Tab navigation still
        // needs to reveal the focused control, so do that one case explicitly.
        ContentScroll.AddHandler(GotFocusEvent, (_, e) =>
        {
            if (e.NavigationMethod == NavigationMethod.Tab && e.Source is Control focused) focused.BringIntoView();
        });
        // A list inside the page (the shortcut list auto-selects its first row when the Mouse &
        // keyboard page is built) scrolls its own selection into view. That request must stop at
        // the list instead of also dragging the whole Settings page down to it.
        if (ContentScroll.Content is Control pageContent)
        {
            pageContent.AddHandler(RequestBringIntoViewEvent, (_, e) =>
            {
                if (e.TargetObject is Visual target && (target is ListBox || target.FindAncestorOfType<ListBox>() is not null))
                    e.Handled = true;
            });
        }
        Closed += (_, _) => { _hotkeyCaptureFlashTimer.Stop(); _searchDebounceTimer.Stop(); _previewDisposed = true; _searchRenderVersion++; };
    }

    private void SettingsSurfacePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Clicking static Settings chrome/content should release the search focus ring. Controls that
        // can take focus still receive the click normally; static surface clicks focus the root.
        if (SearchBox.IsFocused && e.Source is Control focusSource && !IsDescendantOf(focusSource, SearchBox) &&
            !IsSettingsInteractiveSource(focusSource))
        {
            ReferenceRoot.Focus();
        }

        if (_hotkeyCaptureActionId is not null)
        {
            // A pending hotkey capture must never trap the user inside Settings. Any mouse interaction
            // means they are trying to do something else, so cancel capture and let the original click
            // continue to its intended control. Successfully assigned shortcuts remain untouched.
            EndHotkeyCapture("Shortcut capture cancelled because you clicked elsewhere.");
        }
        else if (_hotkeyLookupCapturing && !(e.Source is Control lookupSource && IsDescendantOf(lookupSource, SearchHotkeyButton)))
        {
            EndHotkeyLookupCapture("Hotkey search cancelled because you clicked elsewhere.");
        }

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.Source is Control source && IsSettingsInteractiveSource(source)) return;
        try
        {
            BeginMoveDrag(e);
            e.Handled = true;
        }
        catch
        {
            // A native move loop can be unavailable during teardown; never turn a drag miss into a crash.
        }
    }

    private bool IsSettingsInteractiveSource(Control source)
    {
        Control? current = source;
        while (current is not null)
        {
            if (current is Button or TextBox or ComboBox or CheckBox or NumericUpDown or ScrollBar or ListBox or ListBoxItem or Slider)
                return true;
            // Search-result cards own their click semantics: Boolean rows toggle on the whole card;
            // non-Boolean rows deliberately do nothing outside their explicit editor. Never start a window drag from them.
            if (current.Classes.Contains("searchResult")) return true;
            if (ReferenceEquals(current, ReferenceRoot)) break;
            current = current.GetVisualParent() as Control;
        }
        return false;
    }

    private static bool IsDescendantOf(Control source, Control ancestor)
    {
        Control? current = source;
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor)) return true;
            current = current.GetVisualParent() as Control;
        }
        return false;
    }

    private void SettingsOpened(object? sender, EventArgs e)
    {
        FitToScreenOnOpen();
        // Never schedule diagnostic rendering on the first-interaction path. The old implementation
        // captured multiple RenderTargetBitmaps at +40 ms/+260 ms, making an already-visible Settings
        // window temporarily unresponsive. Diagnostics can explicitly request screenshots instead.
        Dispatcher.UIThread.Post(() => { if (SearchBox is not null) SearchBox.Focusable = true; }, DispatcherPriority.Background);
    }

    private void LoadControls(GlideSettingsState s)
    {
        _loadingControls = true;
        try
        {
            RecentHistoryCheck.IsChecked = s.RecentHistoryEnabled;
            SearchShowRelatedCheck.IsChecked = s.SearchShowRelatedResults;
            HistorySizeBox.Value = s.HistorySize;
            RememberPlacementCheck.IsChecked = s.RememberWindowPlacement;
            ReuseSingleInstanceCheck.IsChecked = s.ReuseSingleInstance;
            ContinueSiblingFoldersCheck.IsChecked = s.ContinueSiblingFolders;
            HierarchicalFolderTraversalCheck.IsChecked = s.HierarchicalFolderTraversal;
            ConfirmHierarchicalFolderTraversalCheck.IsChecked = s.ConfirmHierarchicalFolderTraversal;
            SelectByText(HierarchicalPreviousFolderEntryCombo, string.IsNullOrWhiteSpace(s.HierarchicalPreviousFolderEntry) ? "First image" : s.HierarchicalPreviousFolderEntry);
            NavigationRateLimitMsBox.Value = Math.Clamp(s.NavigationRateLimitMs, 0, 600000);
            SelectByText(NavigationRateLimitInputCombo, string.IsNullOrWhiteSpace(s.NavigationRateLimitInput) ? "Both" : s.NavigationRateLimitInput);
            NavigationRateLimitIndicatorCheck.IsChecked = s.NavigationRateLimitShowIndicator;
            SelectByText(NavigationRateLimitIndicatorPositionCombo, string.IsNullOrWhiteSpace(s.NavigationRateLimitIndicatorPosition) ? "Top left" : s.NavigationRateLimitIndicatorPosition);
            NavigationRateLimitWindowedCheck.IsChecked = s.NavigationRateLimitWindowed;
            NavigationRateLimitFullscreenCheck.IsChecked = s.NavigationRateLimitFullscreen;
            NavigationRateLimitSlideshowCheck.IsChecked = s.NavigationRateLimitSlideshow;
            RememberLastOpenCheck.IsChecked = s.RememberLastOpenLocation;
            NewExplorerTabLastLocationCheck.IsChecked = s.NewExplorerTabsUseLastLocation;
            NewExplorerTabDefaultDirectoryBox.Text = s.NewExplorerTabDefaultDirectory;
            var theme = string.IsNullOrWhiteSpace(s.ThemeChoice) ? (s.LightTheme ? "Light" : "Dark") : s.ThemeChoice;
            SelectByText(ThemeCombo, theme);
            SelectByText(ExplorerThemeCombo, string.IsNullOrWhiteSpace(s.ExplorerThemeChoice) ? "Follow Glide theme" : s.ExplorerThemeChoice);
            SelectByText(AccentCombo, s.AccentChoice);
            SelectByText(GlowCombo, string.IsNullOrWhiteSpace(s.GlowChoice) ? "Follow accent" : s.GlowChoice);
            _workingCustomAccentHex = string.IsNullOrWhiteSpace(s.CustomAccentHex) ? Glide.App.ThemePalette.GlideBlue : s.CustomAccentHex;
            _workingCustomGlowHex = string.IsNullOrWhiteSpace(s.CustomGlowHex) ? _workingCustomAccentHex : s.CustomGlowHex;
            _workingCustomMainBackgroundHex = string.IsNullOrWhiteSpace(s.CustomMainBackgroundHex) ? Glide.App.ThemePalette.FallbackBackground : s.CustomMainBackgroundHex;
            SelectByText(MainBackgroundCombo, string.IsNullOrWhiteSpace(s.MainBackgroundChoice) ? "Follow theme" : s.MainBackgroundChoice);
            GlowIntensityBox.Value = Math.Clamp(s.GlowIntensityPercent, 0, 100);
            ShowRecentOnHomeCheck.IsChecked = s.ShowRecentOnHome;
            ConfirmDiscardSettingsCheck.IsChecked = s.ConfirmDiscardSettingsChanges;
            SelectByText(StartupActionCombo, string.Equals(s.StartupAction, "Explorer tab", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(s.StartupAction) ? "Welcome tab" : s.StartupAction);
            StartupCustomPathBox.Text = s.StartupCustomPath ?? string.Empty;
            SelectByText(NewTabActionCombo, string.IsNullOrWhiteSpace(s.NewTabAction) ? "Welcome tab" : s.NewTabAction);
            NewTabCustomPathBox.Text = s.NewTabCustomPath ?? string.Empty;

            StatusCheck.IsChecked = s.ShowStatusSurface;
            StatusHoverWhenClosedCheck.IsChecked = s.StatusShowOnHoverWhenClosed;
            ShowScrollbarsCheck.IsChecked = s.ShowScrollbars;
            FullPathTitleCheck.IsChecked = s.FullPathInTitle;
            HideCursorFullscreenCheck.IsChecked = s.HideCursorFullscreen;
            AutoHideFullscreenChromeCheck.IsChecked = s.AutoHideFullscreenChrome;
            TabsCheck.IsChecked = s.TabsEnabled;
            SelectByText(ShowInExplorerTargetCombo, ShowInExplorerTargets.Normalize(s.ShowInExplorerTarget));
            TabBarBackButtonCheck.IsChecked = s.TabBarShowBackButton;
            TabBarForwardButtonCheck.IsChecked = s.TabBarShowForwardButton;
            SelectByText(ExternalOpenBehaviorCombo, string.IsNullOrWhiteSpace(s.ExternalOpenBehavior) ? "Open in new tab" : s.ExternalOpenBehavior);
            var thumbnails = Glide.App.Platform.ThumbnailSettingsStore.Load();
            LoadThumbnailControls(thumbnails);
            ThumbnailDiagnosticsText.Text = Glide.App.Platform.ThumbnailSettingsStore.Describe(thumbnails);
            SelectByText(SameImageBehaviorCombo, string.IsNullOrWhiteSpace(s.SameImageAlreadyOpenBehavior) ? "Open new instance" : s.SameImageAlreadyOpenBehavior);
            FullscreenXCloseCheck.IsChecked = s.FullscreenXClosesApp;
            _workingTitleBarButtons = TitleBarButtonCatalog.Normalize(s.TitleBarButtons).ToList();
            SelectByText(CaptionWindowedMinimizeCombo, s.CaptionWindowedMinimizeAction);
            SelectByText(CaptionWindowedMaximizeCombo, s.CaptionWindowedMaximizeAction);
            SelectByText(CaptionWindowedCloseCombo, s.CaptionWindowedCloseAction);
            SelectByText(CaptionFullscreenMinimizeCombo, s.CaptionFullscreenMinimizeAction);
            SelectByText(CaptionFullscreenMaximizeCombo, s.CaptionFullscreenMaximizeAction);
            SelectByText(CaptionFullscreenCloseCombo, s.CaptionFullscreenCloseAction);
            FullscreenStatusAlwaysCheck.IsChecked = s.FullscreenStatusAlwaysOn;
            FullscreenKeepTabBarOpenCheck.IsChecked = s.FullscreenKeepTabBarOpen;
            AlwaysOnTopCheck.IsChecked = s.AlwaysOnTop;
            SelectByText(AlwaysOnTopModeCombo, string.Equals(s.AlwaysOnTopMode, "Soft", StringComparison.OrdinalIgnoreCase) ? "Soft" : "Hard");
            AutoCenterWindowOnRestoreCheck.IsChecked = s.AutoCenterWindowOnRestore;
            SelectByText(FullscreenExitBehaviorCombo, string.IsNullOrWhiteSpace(s.FullscreenExitBehavior) ? "Restore size and location" : s.FullscreenExitBehavior);
            SelectByText(DefaultViewCombo, s.DefaultViewMode);
            AnimateGifsCheck.IsChecked = s.AnimateGifs;
            PointerZoomCheck.IsChecked = s.PointerZoom;
            PreserveZoomCheck.IsChecked = s.PreserveManualZoomOnNavigate;
            ViewportZoomStepBox.Value = Glide.Core.ZoomStepPolicy.Clamp(s.ViewportZoomStepPercent);
            UpscaleSmallImagesCheck.IsChecked = s.UpscaleSmallImages;
            SubpixelRenderingCheck.IsChecked = s.HighPrecisionSubpixelRendering;
            ShowLoadingIndicatorCheck.IsChecked = s.ShowImageLoadingIndicator;
            FullscreenHideCursorDelayBox.Value = Math.Clamp(s.FullscreenHideCursorDelaySeconds, 1, 30);
            TabFocusNavigationCheck.IsChecked = s.EnableTabFocusNavigation;
            EscStopsSlideshowCheck.IsChecked = s.EscStopsSlideshow;
            EscExitsFullscreenCheck.IsChecked = s.EscExitsFullscreen;
            EscWindowedConfirmCheck.IsChecked = s.EscWindowedConfirm;
            _escRememberedChoice = string.IsNullOrWhiteSpace(s.EscWindowedRememberedChoice) ? "Ask" : s.EscWindowedRememberedChoice;
            ResetEscChoiceButton.IsEnabled = !string.Equals(_escRememberedChoice, "Ask", StringComparison.OrdinalIgnoreCase);

            InvertWheelCheck.IsChecked = s.InvertWheelDirection;
            SelectByText(LeftWindowDragBehaviorCombo, string.IsNullOrWhiteSpace(s.LeftWindowDragBehavior) ? "Smart (normal window only)" : s.LeftWindowDragBehavior);
            SelectByText(RightDragBehaviorCombo, string.IsNullOrWhiteSpace(s.RightDragBehavior) ? "Smart (pan image / move window)" : s.RightDragBehavior);
            SelectionBufferCheck.IsChecked = s.SelectionBufferEnabled;
            SelectionBufferWindowedBox.Value = Math.Clamp(s.SelectionBufferWindowedPercent, 0, 100);
            SelectionBufferMaximizedBox.Value = Math.Clamp(s.SelectionBufferMaximizedPercent, 0, 100);
            SelectionBufferAllModesCheck.IsChecked = s.SelectionBufferAllModes;
            ReverseSelectionZoomOutScaleCheck.IsChecked = s.ReverseSelectionZoomOutScale;
            BackgroundDragCheck.IsChecked = s.BackgroundDragWindow;
            _workingGestures = (s.Gestures ?? GestureCatalog.CreateDefaultMap()).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            _workingCustomGestures = GestureCatalog.NormalizeCustomGestures(s.CustomGestures);
            _workingRecentGestureActions = (s.GestureRecentActions ?? new()).ToList();
            RefreshGesturePanel();

            SelectByText(InitialQualityCombo, s.InitialImageQuality);
            SelectByText(InteractivePanQualityCombo, string.IsNullOrWhiteSpace(s.InteractivePanQuality) ? "Full quality" : s.InteractivePanQuality);
            SpeedBoostCheck.IsChecked = s.SpeedBoostEnabled;
            SelectByText(SpeedBoostTrayIconCombo, App.EffectiveTrayIconMode(s));
            StartWithWindowsInBackgroundCheck.IsChecked = s.StartWithWindowsInBackground;
            AdaptiveFastPreviewCheck.IsChecked = s.AdaptiveFastPreview;
            AdaptivePreviewDelayBox.Value = s.AdaptivePreviewDelayMs;
            SequentialReadCheck.IsChecked = s.SequentialForegroundReads;
            PrefetchCheck.IsChecked = s.PredictivePrefetch;
            BackgroundRefinementCheck.IsChecked = s.BackgroundRefinement;
            PurgeCacheCheck.IsChecked = s.PurgeCacheOnMinimize;
            StartupDiagnosticsCheck.IsChecked = s.StartupDiagnostics;
            PrefetchDepthBox.Value = Math.Clamp(s.PrefetchDepth, 1, 30);
            PrefetchSiblingFoldersCheck.IsChecked = s.PrefetchSiblingFolders;
            PrefetchSiblingFolderCountBox.Value = Math.Clamp(s.PrefetchSiblingFolderImageCount, 1, 30);
            CacheItemsBox.Value = s.CacheItems;
            CompressedCacheMbBox.Value = s.CompressedCacheMegabytes;
            DecodedCacheMbBox.Value = s.DecodedCacheMegabytes;
            RapidPreviewBox.Value = s.RapidPreviewLongestSide;
            RapidBrowsePreviewBox.Value = Math.Clamp(s.RapidBrowsePreviewLongestSide, 640, 4096);
            ProgressiveColorCheck.IsChecked = s.ProgressiveColorFirstPreview;

            StatusNavigationCheck.IsChecked = s.StatusShowNavigation;
            SelectByText(StatusBarSizeCombo, string.IsNullOrWhiteSpace(s.StatusBarSize) ? "Small" : s.StatusBarSize);
            SelectByText(StatusBarAlignmentCombo, string.IsNullOrWhiteSpace(s.StatusBarAlignment) ? "Center" : s.StatusBarAlignment);
            StatusGlobalScaleBox.Value = Math.Clamp(s.StatusBarGlobalScalePercent, 60, 160);
            StatusAutoFitCheck.IsChecked = s.StatusBarAutoFit;
            StatusMaximizedBoostBox.Value = Math.Clamp(s.StatusBarMaximizedBoostPercent, 0, 30);
            OverlayAbsoluteCoordinatesCheck.IsChecked = s.OverlayAbsoluteCoordinatesOnResize;
            StatusZoomCheck.IsChecked = s.StatusShowZoom;
            StatusSlideshowCheck.IsChecked = s.StatusShowSlideshow;
            StatusFitCheck.IsChecked = s.StatusShowFit;
            SelectByText(StatusFitScopeCombo, string.IsNullOrWhiteSpace(s.StatusFitScope) ? "This session and future sessions" : s.StatusFitScope);
            StatusInfoCheck.IsChecked = s.StatusShowInfo;
            StatusOptionsCheck.IsChecked = s.StatusShowOptions;
            StatusCloseCheck.IsChecked = s.StatusShowClose;
            StatusCollapsedCornerCheck.IsChecked = s.StatusCollapsedToCorner;
            StatusAutoHideCheck.IsChecked = s.StatusAutoHide;
            StatusRevealZoneExpandBox.Value = Glide.Core.StatusBarAutoHide.NormalizeRevealZoneExpand(s.StatusRevealZoneExpand);
            StatusReserveFitSpaceCheck.IsChecked = s.StatusReserveFitSpace;
            StatusStackInfoLineCheck.IsChecked = s.StatusStackInfoLine;
            StatIndexCheck.IsChecked = s.StatusStatIndex;
            StatResolutionCheck.IsChecked = s.StatusStatResolution;
            StatZoomCheck.IsChecked = s.StatusStatZoom;
            StatFileSizeCheck.IsChecked = s.StatusStatFileSize;
            StatFormatCheck.IsChecked = s.StatusStatFormat;
            StatFileNameCheck.IsChecked = s.StatusStatFileName;

            SlideshowIntervalBox.Value = s.SlideshowIntervalMs;
            SlideshowLoopCheck.IsChecked = s.SlideshowLoop;
            SlideshowCrossFoldersCheck.IsChecked = s.SlideshowCrossFolders;
            SlideshowShuffleCheck.IsChecked = s.SlideshowShuffle;
            SelectByText(SlideshowDirectionCombo, string.IsNullOrWhiteSpace(s.SlideshowDirection) ? "Forward" : s.SlideshowDirection);
            SlideshowStartFullscreenCheck.IsChecked = s.SlideshowStartFullscreen;
            SlideshowPauseInactiveCheck.IsChecked = s.SlideshowPauseWhenInactive;
            SelectByText(NavigationSortOrderCombo, Glide.Core.ImageSortOrder.NormalizeDisplayName(s.NavigationSortOrder));
            NavigationSortDescendingCheck.IsChecked = s.NavigationSortDescending;
            SlideshowQualityIndicatorCheck.IsChecked = s.SlideshowShowQualityIndicator;

            TabMinWidthBox.Value = s.TabMinWidth;
            TabMaxWidthBox.Value = s.TabMaxWidth;
            TabOverflowArrowsCheck.IsChecked = s.TabOverflowArrows;
            TabDetachCheck.IsChecked = s.TabDetachEnabled;
            TabAttachCheck.IsChecked = s.TabAttachEnabled;
            ClosedHistoryBox.Value = s.ClosedTabHistoryLimit;
            CloseEmptyAfterDetachCheck.IsChecked = s.CloseEmptyWindowAfterDetach;
            DetachedHomeTabCheck.IsChecked = s.DetachedWindowHomeTab;
            SelectByText(TabCycleOrderCombo, string.Equals(s.TabCycleOrder, "Most recently used", StringComparison.OrdinalIgnoreCase) ? "Most recently used" : "Tab order");
            SelectByText(TabToolTipTextSizeCombo, s.TabToolTipTextSize is "Small" or "Large" ? s.TabToolTipTextSize : "Medium");
            SelectByText(ExplorerDefaultViewCombo, ExplorerDefaultViews.Normalize(s.ExplorerDefaultView));
            ExplorerShowHiddenFilesCheck.IsChecked = s.ExplorerShowHiddenFiles;
            ExplorerShowOnlyImagesCheck.IsChecked = s.ExplorerShowOnlyImages;
            SelectByText(LastTabBehaviorCombo, string.IsNullOrWhiteSpace(s.LastTabCloseBehavior) ? "Open home page" : s.LastTabCloseBehavior);
            ConfirmCloseMultipleTabsCheck.IsChecked = s.ConfirmCloseMultipleTabs;
            // Legacy "Browser page" (removed in 4.7.0) stays migrated to Welcome; 4.8.0 stores "Explorer page".
            SelectByText(HomePageCombo, string.Equals(s.HomePageMode, "Browser page", StringComparison.OrdinalIgnoreCase) ? "Welcome page" : s.HomePageMode);
            ShowFolderCounterCheck.IsChecked = s.ShowFolderCounter;
            FolderCounterGrandparentCheck.IsChecked = s.FolderCounterIncludeGrandparent;
            FolderNavGroupCheck.IsChecked = s.FolderNavShowGroup;
            FolderNavPreviousCheck.IsChecked = s.FolderNavShowPrevious;
            FolderNavNextCheck.IsChecked = s.FolderNavShowNext;
            FolderNavExploreCheck.IsChecked = s.FolderNavShowExplore;
            FolderNavSkipEmptyCheck.IsChecked = s.FolderNavSkipEmpty;
            FolderNavIncludeHiddenCheck.IsChecked = s.FolderNavIncludeHidden;
            FolderNavWrapCheck.IsChecked = s.FolderNavWrap;
            FolderNavFirstImageCheck.IsChecked = s.FolderNavOpenFirstImage;
            SelectByText(FolderNavOrderCombo, string.IsNullOrWhiteSpace(s.FolderNavOrder) ? "Alphabetical" : s.FolderNavOrder);

            PictureCounterCheck.IsChecked = s.PictureCounterEnabled;
            PictureTemplateBox.Text = s.PictureCounterTemplate;
            PictureFontSizeBox.Value = s.PictureCounterFontSize;
            PictureBoldCheck.IsChecked = s.PictureCounterBold;
            PictureOpacityBox.Value = s.PictureCounterOpacity;
            SelectByText(PicturePositionCombo, s.PictureCounterPosition);
            SelectByText(PictureColorCombo, s.PictureCounterColor);
            PictureShadowCheck.IsChecked = s.PictureCounterShadow;
            OverlayOpacityBox.Value = s.OverlayDefaultOpacity;
            OverlayZoomStepBox.Value = s.OverlayZoomStepPercent;
            OverlayRememberFolderCheck.IsChecked = s.OverlayRememberFolder;
            OverlayPersistSessionCheck.IsChecked = s.OverlayPersistLayoutBetweenSessions;
            WholeAppOverlayAlwaysStartCheck.IsChecked = s.AlwaysStartWholeAppOverlayMode;
            WholeAppOverlayWindowedInteractionsCheck.IsChecked = s.WholeAppOverlayUseWindowedInteractions;
            OverlayKeyboardZoomCheck.IsChecked = s.OverlayKeyboardZoom;
            OverlayHighlightCheck.IsChecked = s.OverlaySelectedHighlight;
            OverlayRememberZoomCheck.IsChecked = s.OverlayRememberZoom;
            OverlayScaleWithWindowCheck.IsChecked = s.OverlayScaleWithWindow;
            SelectByText(OverlayAnimationRegionCombo, s.OverlayAnimationRegion ?? "Anywhere");
            OverlayAnimationSpeedBox.Value = s.OverlayAnimationSpeedDipsPerSecond;
            OverlayAnimationTurnIntervalBox.Value = s.OverlayAnimationTurnIntervalMs;
            OverlayAnimationTurnAngleBox.Value = s.OverlayAnimationTurnAngleDegrees;
            OverlayAnimationPauseCheck.IsChecked = s.OverlayAnimationPauseWhileInteracting;
            OverlayAnimationAvoidOverlapCheck.IsChecked = s.OverlayAnimationAvoidOverlap;
            SelectByText(OverlayAnimationRefreshRateCombo, $"{s.OverlayAnimationRefreshRateHz} Hz");
            OpenFileDefaultDirectoryBox.Text = s.OpenFileDefaultDirectory ?? string.Empty;
            OverlayDefaultDirectoryBox.Text = s.OverlayDefaultDirectory ?? string.Empty;
            ProfileDefaultDirectoryBox.Text = s.ProfileDefaultDirectory ?? string.Empty;
            _workingLastProfileDirectory = s.LastProfileDirectory ?? string.Empty;

            External1Box.Text = s.ExternalProgram1;
            External2Box.Text = s.ExternalProgram2;
            External3Box.Text = s.ExternalProgram3;
            _workingHotkeys = s.Hotkeys.ToDictionary(kv => kv.Key, kv => kv.Value.ToList(), StringComparer.OrdinalIgnoreCase);
            _workingCustomPresets = (s.CustomHotkeyPresets ?? new()).Select(p => p.Clone()).ToList();
            RebuildHotkeyPresetCombo();
            UpdateExternalProgramHotkeyLabels();
            if (_hotkeyRowsBuilt) BuildHotkeyRows();
        }
        finally
        {
            _loadingControls = false;
        }
    }

    private GlideSettingsState ReadControls()
    {
        var state = _committed.CloneState();
        state.RecentHistoryEnabled = RecentHistoryCheck.IsChecked == true;
        state.SearchShowRelatedResults = SearchShowRelatedCheck.IsChecked == true;
        state.HistorySize = (int)(HistorySizeBox.Value ?? 8);
        state.RememberWindowPlacement = RememberPlacementCheck.IsChecked == true;
        state.ReuseSingleInstance = ReuseSingleInstanceCheck.IsChecked == true;
        state.ContinueSiblingFolders = ContinueSiblingFoldersCheck.IsChecked == true;
        state.HierarchicalFolderTraversal = HierarchicalFolderTraversalCheck.IsChecked == true;
        state.ConfirmHierarchicalFolderTraversal = ConfirmHierarchicalFolderTraversalCheck.IsChecked == true;
        state.HierarchicalPreviousFolderEntry = SelectedText(HierarchicalPreviousFolderEntryCombo, "First image");
        state.NavigationRateLimitMs = (int)(NavigationRateLimitMsBox.Value ?? 0);
        state.NavigationRateLimitInput = SelectedText(NavigationRateLimitInputCombo, "Both");
        state.NavigationRateLimitShowIndicator = NavigationRateLimitIndicatorCheck.IsChecked == true;
        state.NavigationRateLimitIndicatorPosition = SelectedText(NavigationRateLimitIndicatorPositionCombo, "Top left");
        state.NavigationRateLimitWindowed = NavigationRateLimitWindowedCheck.IsChecked == true;
        state.NavigationRateLimitFullscreen = NavigationRateLimitFullscreenCheck.IsChecked == true;
        state.NavigationRateLimitSlideshow = NavigationRateLimitSlideshowCheck.IsChecked == true;
        state.RememberLastOpenLocation = RememberLastOpenCheck.IsChecked == true;
        state.NewExplorerTabsUseLastLocation = NewExplorerTabLastLocationCheck.IsChecked == true;
        state.NewExplorerTabDefaultDirectory = NewExplorerTabDefaultDirectoryBox.Text?.Trim() ?? string.Empty;
        state.ThemeChoice = SelectedText(ThemeCombo, "Dark");
        state.ExplorerThemeChoice = SelectedText(ExplorerThemeCombo, "Follow Glide theme");
        state.LightTheme = string.Equals(state.ThemeChoice, "Light", StringComparison.OrdinalIgnoreCase);
        state.AccentChoice = SelectedText(AccentCombo, "Glide blue");
        state.GlowChoice = SelectedText(GlowCombo, "Follow accent");
        state.MainBackgroundChoice = SelectedText(MainBackgroundCombo, "Follow theme");
        state.CustomAccentHex = _workingCustomAccentHex;
        state.CustomGlowHex = _workingCustomGlowHex;
        state.CustomMainBackgroundHex = _workingCustomMainBackgroundHex;
        state.GlowIntensityPercent = (int)(GlowIntensityBox.Value ?? 55);
        state.ShowRecentOnHome = ShowRecentOnHomeCheck.IsChecked == true;
        state.ConfirmDiscardSettingsChanges = ConfirmDiscardSettingsCheck.IsChecked == true;
        state.StartupAction = SelectedText(StartupActionCombo, "Welcome tab") is "Explorer tab" ? "Welcome tab" : SelectedText(StartupActionCombo, "Welcome tab");
        state.StartupCustomPath = StartupCustomPathBox.Text?.Trim() ?? string.Empty;
        state.NewTabAction = SelectedText(NewTabActionCombo, "Welcome tab");
        state.NewTabCustomPath = NewTabCustomPathBox.Text?.Trim() ?? string.Empty;

        state.ShowStatusSurface = StatusCheck.IsChecked == true;
        state.StatusShowOnHoverWhenClosed = StatusHoverWhenClosedCheck.IsChecked == true;
        state.ShowScrollbars = ShowScrollbarsCheck.IsChecked == true;
        state.FullPathInTitle = FullPathTitleCheck.IsChecked == true;
        state.HideCursorFullscreen = HideCursorFullscreenCheck.IsChecked == true;
        state.AutoHideFullscreenChrome = AutoHideFullscreenChromeCheck.IsChecked == true;
        state.TabsEnabled = TabsCheck.IsChecked == true;
        state.ShowInExplorerTarget = ShowInExplorerTargets.Normalize(SelectedText(ShowInExplorerTargetCombo, ShowInExplorerTargets.GlideTab));
        state.TabBarShowBackButton = TabBarBackButtonCheck.IsChecked == true;
        state.TabBarShowForwardButton = TabBarForwardButtonCheck.IsChecked == true;
        state.ExternalOpenBehavior = SelectedText(ExternalOpenBehaviorCombo, "Open in new tab");
        state.SameImageAlreadyOpenBehavior = SelectedText(SameImageBehaviorCombo, "Open new instance");
        state.TitleBarButtons = TitleBarButtonCatalog.Normalize(_workingTitleBarButtons).ToList();
        state.CaptionWindowedMinimizeAction = SelectedText(CaptionWindowedMinimizeCombo, "Minimize");
        state.CaptionWindowedMaximizeAction = SelectedText(CaptionWindowedMaximizeCombo, "Maximize / restore");
        state.CaptionWindowedCloseAction = SelectedText(CaptionWindowedCloseCombo, "Close Glide");
        state.CaptionFullscreenMinimizeAction = SelectedText(CaptionFullscreenMinimizeCombo, "Minimize");
        state.CaptionFullscreenMaximizeAction = SelectedText(CaptionFullscreenMaximizeCombo, "Exit fullscreen (restore previous placement)");
        state.CaptionFullscreenCloseAction = SelectedText(CaptionFullscreenCloseCombo, "Exit fullscreen maximized");
        state.FullscreenXClosesApp = string.Equals(state.CaptionFullscreenCloseAction, "Close Glide", StringComparison.OrdinalIgnoreCase);
        state.FullscreenStatusAlwaysOn = FullscreenStatusAlwaysCheck.IsChecked == true;
        state.FullscreenKeepTabBarOpen = FullscreenKeepTabBarOpenCheck.IsChecked == true;
        state.AlwaysOnTop = AlwaysOnTopCheck.IsChecked == true;
        state.AlwaysOnTopMode = SelectedText(AlwaysOnTopModeCombo, "Soft");
        state.AutoCenterWindowOnRestore = AutoCenterWindowOnRestoreCheck.IsChecked == true;
        state.FullscreenExitBehavior = SelectedText(FullscreenExitBehaviorCombo, "Restore size and location");
        state.DefaultViewMode = SelectedText(DefaultViewCombo, "Fit image");
        state.AnimateGifs = AnimateGifsCheck.IsChecked == true;
        state.PointerZoom = PointerZoomCheck.IsChecked == true;
        state.PreserveManualZoomOnNavigate = PreserveZoomCheck.IsChecked == true;
        state.ViewportZoomStepPercent = Glide.Core.ZoomStepPolicy.Clamp((int)(ViewportZoomStepBox.Value ?? Glide.Core.ZoomStepPolicy.DefaultStepPercent));
        state.UpscaleSmallImages = UpscaleSmallImagesCheck.IsChecked == true;
        state.HighPrecisionSubpixelRendering = SubpixelRenderingCheck.IsChecked == true;
        state.ShowImageLoadingIndicator = ShowLoadingIndicatorCheck.IsChecked == true;
        state.FullscreenHideCursorDelaySeconds = (int)(FullscreenHideCursorDelayBox.Value ?? 2);
        state.EnableTabFocusNavigation = TabFocusNavigationCheck.IsChecked == true;
        state.EscStopsSlideshow = EscStopsSlideshowCheck.IsChecked == true;
        state.EscExitsFullscreen = EscExitsFullscreenCheck.IsChecked == true;
        state.EscWindowedConfirm = EscWindowedConfirmCheck.IsChecked == true;
        state.EscWindowedRememberedChoice = _escRememberedChoice;

        state.InvertWheelDirection = InvertWheelCheck.IsChecked == true;
        state.LeftWindowDragBehavior = SelectedText(LeftWindowDragBehaviorCombo, "Smart (normal window only)");
        state.RightDragBehavior = SelectedText(RightDragBehaviorCombo, "Smart (pan image / move window)");
        state.RightDragPan = !string.Equals(state.RightDragBehavior, "Do nothing", StringComparison.OrdinalIgnoreCase);
        state.SelectionBufferEnabled = SelectionBufferCheck.IsChecked == true;
        state.SelectionBufferWindowedPercent = Math.Clamp((int)(SelectionBufferWindowedBox.Value ?? 33), 0, 100);
        state.SelectionBufferMaximizedPercent = Math.Clamp((int)(SelectionBufferMaximizedBox.Value ?? 100), 0, 100);
        state.SelectionBufferAllModes = SelectionBufferAllModesCheck.IsChecked == true;
        state.ReverseSelectionZoomOutScale = ReverseSelectionZoomOutScaleCheck.IsChecked == true;
        state.BackgroundDragWindow = BackgroundDragCheck.IsChecked == true;
        state.Gestures = _workingGestures.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        state.CustomGestures = _workingCustomGestures.Select(x => x.Clone()).ToList();
        state.GestureRecentActions = _workingRecentGestureActions.ToList();

        state.InitialImageQuality = SelectedText(InitialQualityCombo, "Maximum quality");
        state.PerformanceUserCustom = string.Equals(state.InitialImageQuality, "User custom", StringComparison.OrdinalIgnoreCase);
        state.InteractivePanQuality = SelectedText(InteractivePanQualityCombo, "Full quality");
        state.SpeedBoostEnabled = SpeedBoostCheck.IsChecked == true;
        state.SpeedBoostTrayIcon = SelectedText(SpeedBoostTrayIconCombo, "Always");
        state.ShowTrayIcon = !string.Equals(state.SpeedBoostTrayIcon, "Never", StringComparison.OrdinalIgnoreCase);
        state.StartWithWindowsInBackground = StartWithWindowsInBackgroundCheck.IsChecked == true;
        state.AdaptiveFastPreview = AdaptiveFastPreviewCheck.IsChecked == true;
        state.AdaptivePreviewDelayMs = (int)(AdaptivePreviewDelayBox.Value ?? 20);
        state.SequentialForegroundReads = SequentialReadCheck.IsChecked == true;
        state.PredictivePrefetch = PrefetchCheck.IsChecked == true;
        state.BackgroundRefinement = BackgroundRefinementCheck.IsChecked == true;
        state.PurgeCacheOnMinimize = PurgeCacheCheck.IsChecked == true;
        state.StartupDiagnostics = StartupDiagnosticsCheck.IsChecked == true;
        state.PrefetchDepth = Math.Clamp((int)(PrefetchDepthBox.Value ?? 5), 1, 30);
        state.PrefetchSiblingFolders = PrefetchSiblingFoldersCheck.IsChecked == true;
        state.PrefetchSiblingFolderImageCount = Math.Clamp((int)(PrefetchSiblingFolderCountBox.Value ?? 3), 1, 30);
        state.CacheItems = (int)(CacheItemsBox.Value ?? 16);
        state.CompressedCacheMegabytes = (int)(CompressedCacheMbBox.Value ?? 256);
        state.DecodedCacheMegabytes = (int)(DecodedCacheMbBox.Value ?? 256);
        state.RapidPreviewLongestSide = (int)(RapidPreviewBox.Value ?? 3000);
        state.RapidBrowsePreviewLongestSide = Math.Clamp((int)(RapidBrowsePreviewBox.Value ?? 1440), 640, 4096);
        state.ProgressiveColorFirstPreview = ProgressiveColorCheck.IsChecked == true;

        state.StatusShowNavigation = StatusNavigationCheck.IsChecked == true;
        state.StatusBarSize = SelectedText(StatusBarSizeCombo, "Small");
        state.StatusBarAlignment = SelectedText(StatusBarAlignmentCombo, "Center");
        state.StatusBarGlobalScalePercent = Math.Clamp((int)(StatusGlobalScaleBox.Value ?? 100), 60, 160);
        state.StatusBarAutoFit = StatusAutoFitCheck.IsChecked == true;
        state.StatusBarMaximizedBoostPercent = Math.Clamp((int)(StatusMaximizedBoostBox.Value ?? 15), 0, 30);
        state.OverlayAbsoluteCoordinatesOnResize = OverlayAbsoluteCoordinatesCheck.IsChecked == true;
        state.StatusShowZoom = StatusZoomCheck.IsChecked == true;
        state.StatusShowSlideshow = StatusSlideshowCheck.IsChecked == true;
        state.StatusShowFit = StatusFitCheck.IsChecked == true;
        state.StatusFitScope = SelectedText(StatusFitScopeCombo, "This session and future sessions");
        state.StatusShowInfo = StatusInfoCheck.IsChecked == true;
        state.StatusShowOptions = StatusOptionsCheck.IsChecked == true;
        state.StatusShowClose = StatusCloseCheck.IsChecked == true;
        state.StatusCollapsedToCorner = StatusCollapsedCornerCheck.IsChecked == true;
        state.StatusAutoHide = StatusAutoHideCheck.IsChecked == true;
        state.StatusRevealZoneExpand = Glide.Core.StatusBarAutoHide.NormalizeRevealZoneExpand((int)(StatusRevealZoneExpandBox.Value ?? Glide.Core.StatusBarAutoHide.DefaultRevealZoneExpand));
        state.StatusReserveFitSpace = StatusReserveFitSpaceCheck.IsChecked == true;
        state.StatusStackInfoLine = StatusStackInfoLineCheck.IsChecked == true;
        state.StatusStatIndex = StatIndexCheck.IsChecked == true;
        state.StatusStatResolution = StatResolutionCheck.IsChecked == true;
        state.StatusStatZoom = StatZoomCheck.IsChecked == true;
        state.StatusStatFileSize = StatFileSizeCheck.IsChecked == true;
        state.StatusStatFormat = StatFormatCheck.IsChecked == true;
        state.StatusStatFileName = StatFileNameCheck.IsChecked == true;

        state.SlideshowIntervalMs = (int)(SlideshowIntervalBox.Value ?? 3000);
        state.SlideshowLoop = SlideshowLoopCheck.IsChecked == true;
        state.SlideshowCrossFolders = SlideshowCrossFoldersCheck.IsChecked == true;
        state.SlideshowShuffle = SlideshowShuffleCheck.IsChecked == true;
        state.SlideshowDirection = SelectedText(SlideshowDirectionCombo, "Forward");
        state.SlideshowStartFullscreen = SlideshowStartFullscreenCheck.IsChecked == true;
        state.SlideshowPauseWhenInactive = SlideshowPauseInactiveCheck.IsChecked == true;
        state.NavigationSortOrder = Glide.Core.ImageSortOrder.NormalizeDisplayName(SelectedText(NavigationSortOrderCombo, Glide.Core.ImageSortOrder.NameDisplay));
        state.NavigationSortDescending = NavigationSortDescendingCheck.IsChecked == true;
        state.SlideshowShowQualityIndicator = SlideshowQualityIndicatorCheck.IsChecked == true;

        state.TabMinWidth = (int)(TabMinWidthBox.Value ?? 120);
        state.TabMaxWidth = (int)(TabMaxWidthBox.Value ?? 240);
        state.TabOverflowArrows = TabOverflowArrowsCheck.IsChecked == true;
        state.TabDetachEnabled = TabDetachCheck.IsChecked == true;
        state.TabAttachEnabled = TabAttachCheck.IsChecked == true;
        state.ClosedTabHistoryLimit = (int)(ClosedHistoryBox.Value ?? 20);
        state.CloseEmptyWindowAfterDetach = CloseEmptyAfterDetachCheck.IsChecked == true;
        state.DetachedWindowHomeTab = DetachedHomeTabCheck.IsChecked == true;
        state.TabCycleOrder = SelectedText(TabCycleOrderCombo, "Tab order");
        state.TabToolTipTextSize = SelectedText(TabToolTipTextSizeCombo, "Medium");
        state.ExplorerDefaultView = ExplorerDefaultViews.Normalize(SelectedText(ExplorerDefaultViewCombo, ExplorerDefaultViews.Default));
        state.ExplorerShowHiddenFiles = ExplorerShowHiddenFilesCheck.IsChecked == true;
        state.ExplorerShowOnlyImages = ExplorerShowOnlyImagesCheck.IsChecked == true;
        state.LastTabCloseBehavior = SelectedText(LastTabBehaviorCombo, "Open home page");
        state.ConfirmCloseMultipleTabs = ConfirmCloseMultipleTabsCheck.IsChecked == true;
        if (state.ConfirmCloseMultipleTabs) state.RememberedMultiTabCloseChoice = "Ask";
        state.HomePageMode = SelectedText(HomePageCombo, "Welcome page") is "Browser page" ? "Welcome page" : SelectedText(HomePageCombo, "Welcome page");
        state.ShowFolderCounter = ShowFolderCounterCheck.IsChecked == true;
        state.FolderCounterIncludeGrandparent = FolderCounterGrandparentCheck.IsChecked == true;
        state.FolderNavShowGroup = FolderNavGroupCheck.IsChecked == true;
        state.FolderNavShowPrevious = FolderNavPreviousCheck.IsChecked == true;
        state.FolderNavShowNext = FolderNavNextCheck.IsChecked == true;
        state.FolderNavShowExplore = FolderNavExploreCheck.IsChecked == true;
        state.FolderNavSkipEmpty = FolderNavSkipEmptyCheck.IsChecked == true;
        state.FolderNavIncludeHidden = FolderNavIncludeHiddenCheck.IsChecked == true;
        state.FolderNavWrap = FolderNavWrapCheck.IsChecked == true;
        state.FolderNavOpenFirstImage = FolderNavFirstImageCheck.IsChecked == true;
        state.FolderNavOrder = SelectedText(FolderNavOrderCombo, "Alphabetical");

        state.PictureCounterEnabled = PictureCounterCheck.IsChecked == true;
        state.PictureCounterTemplate = string.IsNullOrWhiteSpace(PictureTemplateBox.Text) ? "[{index}/{total}]" : PictureTemplateBox.Text!;
        state.PictureCounterFontSize = (int)(PictureFontSizeBox.Value ?? 13);
        state.PictureCounterBold = PictureBoldCheck.IsChecked == true;
        state.PictureCounterOpacity = (int)(PictureOpacityBox.Value ?? 85);
        state.PictureCounterPosition = SelectedText(PicturePositionCombo, "Top right");
        state.PictureCounterColor = SelectedText(PictureColorCombo, "White");
        state.PictureCounterShadow = PictureShadowCheck.IsChecked == true;
        state.OverlayDefaultOpacity = (int)(OverlayOpacityBox.Value ?? 100);
        state.OverlayZoomStepPercent = (int)(OverlayZoomStepBox.Value ?? 10);
        state.OverlayRememberFolder = OverlayRememberFolderCheck.IsChecked == true;
        state.OverlayPersistLayoutBetweenSessions = OverlayPersistSessionCheck.IsChecked == true;
        state.AlwaysStartWholeAppOverlayMode = WholeAppOverlayAlwaysStartCheck.IsChecked == true;
        state.WholeAppOverlayUseWindowedInteractions = WholeAppOverlayWindowedInteractionsCheck.IsChecked == true;
        state.OverlayKeyboardZoom = OverlayKeyboardZoomCheck.IsChecked == true;
        state.OverlaySelectedHighlight = OverlayHighlightCheck.IsChecked == true;
        state.OverlayRememberZoom = OverlayRememberZoomCheck.IsChecked == true;
        state.OverlayScaleWithWindow = OverlayScaleWithWindowCheck.IsChecked == true;
        state.OverlayAnimationRegion = SelectedText(OverlayAnimationRegionCombo, "Anywhere");
        state.OverlayAnimationSpeedDipsPerSecond = Math.Clamp((int)(OverlayAnimationSpeedBox.Value ?? 90), 10, 2000);
        state.OverlayAnimationTurnIntervalMs = Math.Clamp((int)(OverlayAnimationTurnIntervalBox.Value ?? 2500), 0, 60000);
        state.OverlayAnimationTurnAngleDegrees = Math.Clamp((int)(OverlayAnimationTurnAngleBox.Value ?? 120), 0, 180);
        state.OverlayAnimationPauseWhileInteracting = OverlayAnimationPauseCheck.IsChecked == true;
        state.OverlayAnimationAvoidOverlap = OverlayAnimationAvoidOverlapCheck.IsChecked == true;
        state.OverlayAnimationRefreshRateHz = ParseChoiceNumber(OverlayAnimationRefreshRateCombo, 144);
        state.OpenFileDefaultDirectory = OpenFileDefaultDirectoryBox.Text?.Trim() ?? string.Empty;
        state.OverlayDefaultDirectory = OverlayDefaultDirectoryBox.Text?.Trim() ?? string.Empty;
        state.ProfileDefaultDirectory = ProfileDefaultDirectoryBox.Text?.Trim() ?? string.Empty;
        state.LastProfileDirectory = _workingLastProfileDirectory;

        state.ExternalProgram1 = External1Box.Text ?? string.Empty;
        state.ExternalProgram2 = External2Box.Text ?? string.Empty;
        state.ExternalProgram3 = External3Box.Text ?? string.Empty;
        state.Hotkeys = _workingHotkeys.ToDictionary(kv => kv.Key, kv => kv.Value.ToList(), StringComparer.OrdinalIgnoreCase);
        state.CustomHotkeyPresets = _workingCustomPresets.Select(p => p.Clone()).ToList();
        return state;
    }

    private void WireLivePreview()
    {
        // Subscribe from the authoritative setting->editor map so newly added editable settings cannot
        // silently bypass live preview or exact net-change tracking. Every setting has exactly one
        // editor (4.8.0 removed the mirrored status-bar checkboxes).
        // The Reset Glide tick boxes only choose what a confirmation lists; they are not settings, so no preview pass.
        foreach (var pair in _settingControls.Where(x => !x.Key.StartsWith("reset.", StringComparison.OrdinalIgnoreCase)).ToArray())
            pair.Value.PropertyChanged += ValueControlChanged;
    }

    private void ValueControlChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_loadingControls) return;
        var relevant = e.Property == ToggleButton.IsCheckedProperty ||
                       e.Property == SelectingItemsControl.SelectedIndexProperty ||
                       e.Property == NumericUpDown.ValueProperty ||
                       e.Property == TextBox.TextProperty;
        if (!relevant) return;

        if (IsPerformanceTuningControl(sender)) DetectPerformanceProfileFromControls();

        // Reading ~200 controls, applying the whole preview to the live app and re-diffing the state is
        // heavy; doing it inside the click made the control look unresponsive. Let the control repaint
        // first, and coalesce a burst of changes (spinner, typing) into one pass.
        ScheduleLivePreview();
    }

    private bool _previewScheduled;
    private bool _previewDisposed;

    private void ScheduleLivePreview()
    {
        if (_previewScheduled) return;
        _previewScheduled = true;
        Dispatcher.UIThread.Post(RunLivePreview, DispatcherPriority.Background);
    }

    private void RunLivePreview()
    {
        if (!_previewScheduled) return;
        _previewScheduled = false;
        if (_previewDisposed || _loadingControls) return;
        var preview = ReadControls();
        _apply(preview, false);
        UpdateDirtyState(preview);
    }

    private bool IsPerformanceTuningControl(object? sender) => sender is not null && sender != InitialQualityCombo &&
            new Control[] { InteractivePanQualityCombo, AdaptiveFastPreviewCheck, AdaptivePreviewDelayBox, SequentialReadCheck, PrefetchCheck, PrefetchDepthBox,
            BackgroundRefinementCheck, PrefetchSiblingFoldersCheck, PrefetchSiblingFolderCountBox, CacheItemsBox, CompressedCacheMbBox, DecodedCacheMbBox, RapidPreviewBox, RapidBrowsePreviewBox, ProgressiveColorCheck }
        .Any(x => ReferenceEquals(x, sender));

    private void DetectPerformanceProfileFromControls()
    {
        string detected = "User custom";
        foreach (var candidate in new[] { "Maximum speed", "Balanced", "Maximum quality" })
        {
            var policy = ImagePerformancePolicy.ForProfile(candidate);
            var expectedPan = candidate == "Maximum speed" ? "Fast" : "Full quality";
            var expectedRapid = candidate == "Maximum speed" ? 960 : candidate == "Maximum quality" ? 3500 : 1440;
            var expectedSiblingCount = candidate == "Maximum quality" ? 7 : 3;
            if (SelectedText(InteractivePanQualityCombo, "Full quality") == expectedPan &&
                AdaptiveFastPreviewCheck.IsChecked == policy.DecoderScaledFirstFrame &&
                (int)(AdaptivePreviewDelayBox.Value ?? 20) == policy.RefinementDelayMs &&
                SequentialReadCheck.IsChecked == policy.SequentialForegroundReads && PrefetchCheck.IsChecked == policy.PredictivePrefetch &&
                (int)(PrefetchDepthBox.Value ?? 5) == policy.PrefetchDepth &&
                BackgroundRefinementCheck.IsChecked == policy.BackgroundRefinement &&
                PurgeCacheCheck.IsChecked == false && StartupDiagnosticsCheck.IsChecked == false &&
                PrefetchSiblingFoldersCheck.IsChecked == (candidate == "Maximum quality") &&
                (int)(PrefetchSiblingFolderCountBox.Value ?? 3) == expectedSiblingCount &&
                (int)(CacheItemsBox.Value ?? 16) == policy.CompressedCacheItems &&
                (int)(CompressedCacheMbBox.Value ?? 256) == policy.CompressedCacheMegabytes && (int)(DecodedCacheMbBox.Value ?? 256) == policy.DecodedCacheMegabytes &&
                (int)(RapidPreviewBox.Value ?? 3000) == policy.PreviewLongestSide && (int)(RapidBrowsePreviewBox.Value ?? 1440) == expectedRapid &&
                ProgressiveColorCheck.IsChecked == policy.ProgressiveColorFirstPreview)
            { detected = candidate; break; }
        }
        _loadingControls = true;
        try { SelectByText(InitialQualityCombo, detected); }
        finally { _loadingControls = false; }
    }

    private void SaveUserPerformancePresetClicked(object? sender, RoutedEventArgs e)
    {
        // Persist only the reusable preset payload. Do not implicitly commit unrelated staged
        // Settings edits merely because the user chose to save a performance preset.
        var saved = _committed.CloneState();
        saved.PerformanceUserCustom = true;
        saved.PerformanceUserCustomSaved = true;
        saved.UserCustomPanQuality = SelectedText(InteractivePanQualityCombo, "Full quality");
        saved.UserCustomAdaptivePreview = AdaptiveFastPreviewCheck.IsChecked == true;
        saved.UserCustomRefinementDelayMs = (int)(AdaptivePreviewDelayBox.Value ?? 20);
        saved.UserCustomSequentialReads = SequentialReadCheck.IsChecked == true;
        saved.UserCustomPrefetch = PrefetchCheck.IsChecked == true;
        saved.UserCustomPrefetchDepth = Math.Clamp((int)(PrefetchDepthBox.Value ?? 5), 1, 30);
        saved.UserCustomBackgroundRefinement = BackgroundRefinementCheck.IsChecked == true;
        saved.UserCustomCacheItems = (int)(CacheItemsBox.Value ?? 16);
        saved.UserCustomCompressedCacheMb = (int)(CompressedCacheMbBox.Value ?? 256);
        saved.UserCustomDecodedCacheMb = (int)(DecodedCacheMbBox.Value ?? 256);
        saved.UserCustomPreviewLongestSide = (int)(RapidPreviewBox.Value ?? 3000);
        saved.UserCustomRapidBrowsePreviewSize = (int)(RapidBrowsePreviewBox.Value ?? 1440);
        saved.UserCustomProgressiveColor = ProgressiveColorCheck.IsChecked == true;
        _committed = saved;
        WindowPlacementService.ApplyTo(_committed);
        SettingsStore.Save(_committed);

        _loadingControls = true;
        try { SelectByText(InitialQualityCombo, "User custom"); } finally { _loadingControls = false; }
        var preview = ReadControls();
        preview.PerformanceUserCustom = true;
        _apply(preview, false);
        UpdateDirtyState(preview);
        DirtyText.Text = "User custom performance preset saved.";
    }

    private async void CustomizeTitleBarClicked(object? sender, RoutedEventArgs e)
    {
        var dialog = new TitleBarCustomizerWindow(_workingTitleBarButtons) { Topmost = Topmost };
        var result = await dialog.ShowDialog<IReadOnlyList<string>?>(this);
        if (result is null) return;
        _workingTitleBarButtons = TitleBarButtonCatalog.Normalize(result).ToList();
        var preview = ReadControls();
        _apply(preview, false);
        UpdateDirtyState(preview);
        PageSubtitle.Text = $"Title bar: {_workingTitleBarButtons.Count} custom button(s) selected.";
    }

    private void PerformanceProfileChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingControls) return;
        var profileName = SelectedText(InitialQualityCombo, "Maximum quality");
        if (string.Equals(profileName, "User custom", StringComparison.OrdinalIgnoreCase))
        {
            if (_committed.PerformanceUserCustomSaved)
            {
                _loadingControls = true;
                try
                {
                    SelectByText(InteractivePanQualityCombo, _committed.UserCustomPanQuality);
                    AdaptiveFastPreviewCheck.IsChecked = _committed.UserCustomAdaptivePreview;
                    AdaptivePreviewDelayBox.Value = _committed.UserCustomRefinementDelayMs;
                    SequentialReadCheck.IsChecked = _committed.UserCustomSequentialReads;
                    PrefetchCheck.IsChecked = _committed.UserCustomPrefetch;
                    PrefetchDepthBox.Value = _committed.UserCustomPrefetchDepth;
                    BackgroundRefinementCheck.IsChecked = _committed.UserCustomBackgroundRefinement;
                    CacheItemsBox.Value = _committed.UserCustomCacheItems;
                    CompressedCacheMbBox.Value = _committed.UserCustomCompressedCacheMb;
                    DecodedCacheMbBox.Value = _committed.UserCustomDecodedCacheMb;
                    RapidPreviewBox.Value = _committed.UserCustomPreviewLongestSide;
                    RapidBrowsePreviewBox.Value = _committed.UserCustomRapidBrowsePreviewSize;
                    ProgressiveColorCheck.IsChecked = _committed.UserCustomProgressiveColor;
                }
                finally { _loadingControls = false; }
                PreviewNow();
            }
            return;
        }
        var policy = Glide.Imaging.ImagePerformancePolicy.ForProfile(profileName);
        _loadingControls = true;
        try
        {
            SelectByText(InteractivePanQualityCombo, profileName switch
            {
                "Maximum speed" => "Fast",
                _ => "Full quality"
            });
            AdaptiveFastPreviewCheck.IsChecked = policy.DecoderScaledFirstFrame;
            AdaptivePreviewDelayBox.Value = policy.RefinementDelayMs;
            SequentialReadCheck.IsChecked = policy.SequentialForegroundReads;
            PrefetchCheck.IsChecked = policy.PredictivePrefetch;
            PrefetchDepthBox.Value = policy.PrefetchDepth;
            BackgroundRefinementCheck.IsChecked = policy.BackgroundRefinement;
            PurgeCacheCheck.IsChecked = false;
            StartupDiagnosticsCheck.IsChecked = false;
            PrefetchSiblingFoldersCheck.IsChecked = profileName == "Maximum quality";
            PrefetchSiblingFolderCountBox.Value = profileName == "Maximum quality" ? 7 : 3;
            CacheItemsBox.Value = policy.CompressedCacheItems;
            CompressedCacheMbBox.Value = policy.CompressedCacheMegabytes;
            DecodedCacheMbBox.Value = policy.DecodedCacheMegabytes;
            RapidPreviewBox.Value = policy.PreviewLongestSide;
            RapidBrowsePreviewBox.Value = profileName switch
            {
                "Maximum speed" => 960,
                "Maximum quality" => 3500,
                _ => 1440
            };
            ProgressiveColorCheck.IsChecked = true;
        }
        finally { _loadingControls = false; }
        var preview = ReadControls();
        _apply(preview, false);
        UpdateDirtyState(preview);
    }

    /// <summary>Net changes between the committed state and the staged controls (see <see cref="SettingsChangeSet"/>).</summary>
    private List<SettingChange> GetNetChanges(GlideSettingsState? current = null) =>
        SettingsChangeSet.Compute(_committed, current ?? ReadControls());

    private void UpdateDirtyState(GlideSettingsState? current = null)
    {
        current ??= ReadControls();
        var changes = GetNetChanges(current);
        ApplyButton.IsEnabled = changes.Count > 0;
        DirtyText.Text = changes.Count switch
        {
            0 => "Up to date",
            1 => "1 change",
            _ => $"{changes.Count} changes"
        };
        DirtyHintText.IsVisible = changes.Count > 0;
        var changeTip = changes.Count == 0 ? "No net changes." : string.Join("\n", changes.Select(c => $"{c.Name}: {c.Before} → {c.After}"));
        ToolTip.SetTip(DirtySummaryHost, changeTip);
        ToolTip.SetTip(DirtyText, changeTip);
        ToolTip.SetTip(DirtyHintText, changeTip);
        UpdateCardBadges();
    }

    // ---- Cards: map, defaults snapshot and "N changed" badges ---------------------------------

    /// <summary>
    /// Maps every setting id to the <see cref="SettingsCard"/> that hosts it: mapped editors by their
    /// logical ancestor card, action entries through the <c>SettingsCard.SettingIds</c> anchors, and
    /// every generated gesture entry to the card holding the gesture panel.
    /// </summary>
    private void BuildCardMap()
    {
        foreach (var pair in _panels)
        {
            var cards = pair.Value.GetLogicalDescendants().OfType<SettingsCard>().ToList();
            _cardsByPage[pair.Key] = cards;
            foreach (var card in cards)
            {
                if (!string.IsNullOrWhiteSpace(card.CardId)) _cardById[card.CardId!] = card;
                _pageOfCard[card] = pair.Key;
                card.ExpandedChanged += (_, _) => UpdateExpandCollapseButtons();
                foreach (var anchor in card.Body.GetLogicalDescendants().OfType<Control>().Prepend(card.Body))
                    foreach (var id in SettingsCard.GetSettingIdList(anchor))
                    {
                        _settingCard[id] = card;
                        _settingAnchors[id] = anchor;
                    }
            }
        }

        foreach (var pair in _settingControls)
        {
            if (pair.Value.FindLogicalAncestorOfType<SettingsCard>() is not { } card) continue;
            _settingCard[pair.Key] = card;
            if (IsHiddenInCard(pair.Value, card)) _hiddenSettingIds.Add(pair.Key);
        }

        if (GestureMatrixHost.FindLogicalAncestorOfType<SettingsCard>() is { } gestureCard)
            foreach (var setting in SettingsCatalog.All)
                if (setting.Id.StartsWith("gesture.", StringComparison.OrdinalIgnoreCase)) _settingCard[setting.Id] = gestureCard;
    }

    /// <summary>True when the control or any of its parents below the card body is hidden in XAML.</summary>
    private static bool IsHiddenInCard(Control control, SettingsCard card)
    {
        for (Control? current = control; current is not null && !ReferenceEquals(current, card.Body); current = current.GetLogicalParent() as Control)
            if (!current.IsVisible) return true;
        return false;
    }

    private static object? EditorValue(Control control) => control switch
    {
        CheckBox check => check.IsChecked,
        ComboBox combo => SelectedText(combo),
        NumericUpDown number => number.Value,
        TextBox box => box.Text?.Trim() ?? string.Empty,
        _ => null
    };

    /// <summary>A push button that edits a setting through a dialog/action (CheckBox derives from Button too).</summary>
    private static bool IsActionButton(Control control) => control is Button and not ToggleButton;

    private void CaptureDefaultSnapshot()
    {
        LoadControls(new GlideSettingsState());
        // Explorer thumbnail settings live in their own store (not GlideSettingsState), so LoadControls
        // shows the stored values; their default is the thumbnail store's own default.
        _loadingControls = true;
        try { LoadThumbnailControls(Glide.App.Platform.ThumbnailSettings.Default); }
        finally { _loadingControls = false; }
        foreach (var setting in SettingsCatalog.All)
            if (_settingControls.TryGetValue(setting.Id, out var control) && !IsActionButton(control))
                _defaultValue[setting.Id] = EditorValue(control);
    }

    /// <summary>Settings that never count toward a card's "N changed" badge.</summary>
    private bool CountsTowardBadge(SettingDefinition setting) =>
        setting.Kind != SettingKind.Action &&
        setting.Id is not ("profiles.presets" or "hotkeys.preset") &&
        !_hiddenSettingIds.Contains(setting.Id) &&
        _settingControls.TryGetValue(setting.Id, out var control) && !IsActionButton(control) &&
        _defaultValue.ContainsKey(setting.Id);

    private void UpdateCardBadges()
    {
        if (_cardById.Count == 0) return;
        var changed = new Dictionary<SettingsCard, List<string>>();
        void Add(string cardId, string title)
        {
            if (!_cardById.TryGetValue(cardId, out var target)) return;
            if (!changed.TryGetValue(target, out var list)) changed[target] = list = new List<string>();
            list.Add(title);
        }

        foreach (var setting in SettingsCatalog.All)
        {
            if (!CountsTowardBadge(setting) || !_settingCard.TryGetValue(setting.Id, out var card)) continue;
            if (Equals(EditorValue(_settingControls[setting.Id]), _defaultValue[setting.Id])) continue;
            if (!changed.TryGetValue(card, out var list)) changed[card] = list = new List<string>();
            list.Add(setting.Label);
        }

        // Gesture rows (works before the lazy matrix is built) and custom gestures.
        foreach (var slot in GestureCatalog.Slots)
            if (IsGestureChanged(slot.Id)) Add("mouse-gestures", $"{GestureCatalog.AreaLabel(slot.Area)}: {slot.InputLabel}");
        foreach (var custom in _workingCustomGestures) Add("mouse-gestures", CustomGestureTitle(custom));

        // Keyboard shortcuts: one per action whose shortcuts differ from the Glide defaults.
        foreach (var action in HotkeyCatalog.All)
        {
            var working = _workingHotkeys.GetValueOrDefault(action.Id, EmptyShortcutList);
            var defaults = DefaultHotkeyMap.GetValueOrDefault(action.Id, EmptyShortcutList);
            if (!working.SequenceEqual(defaults, StringComparer.OrdinalIgnoreCase)) Add("mouse-shortcuts", "Shortcut: " + action.Label);
        }

        // Title-bar utility buttons (edited in their own dialog).
        if (!TitleBarButtonCatalog.Normalize(_workingTitleBarButtons).SequenceEqual(TitleBarButtonCatalog.DefaultButtonIds, StringComparer.OrdinalIgnoreCase))
            Add("window-title-bar", "Title-bar utility buttons");

        foreach (var card in _cardById.Values)
        {
            var titles = changed.GetValueOrDefault(card);
            var count = titles?.Count ?? 0;
            card.ChangedCount = count;
            card.ChangedTip = count == 0 ? null
                : "Changed from default:\n" + string.Join("\n", titles!.Take(20)) + (count > 20 ? $"\n… and {count - 20} more" : string.Empty);
        }
    }

    // ---- Expand all / Collapse all -------------------------------------------------------------

    private IReadOnlyList<SettingsCard> ActivePageCards() => _cardsByPage.GetValueOrDefault(_activeCategory) ?? new List<SettingsCard>();

    private void ExpandAllCardsClicked(object? sender, RoutedEventArgs e) => SetActivePageCardsExpanded(true);

    private void CollapseAllCardsClicked(object? sender, RoutedEventArgs e) => SetActivePageCardsExpanded(false);

    private void SetActivePageCardsExpanded(bool expanded)
    {
        foreach (var card in ActivePageCards()) card.IsExpanded = expanded;
        UpdateExpandCollapseButtons();
    }

    /// <summary>The buttons act on the visible page only: hidden during search, disabled when nothing would change.</summary>
    private void UpdateExpandCollapseButtons()
    {
        if (ExpandAllCardsButton is null || CollapseAllCardsButton is null) return;
        var searching = SearchPanel.IsVisible;
        ExpandAllCardsButton.IsVisible = !searching;
        CollapseAllCardsButton.IsVisible = !searching;
        var cards = ActivePageCards();
        ExpandAllCardsButton.IsEnabled = cards.Any(c => !c.IsExpanded);
        CollapseAllCardsButton.IsEnabled = cards.Any(c => c.IsExpanded);
    }

    private void ResetEscChoiceClicked(object? sender, RoutedEventArgs e)
    {
        _escRememberedChoice = "Ask";
        ResetEscChoiceButton.IsEnabled = false;
        var preview = ReadControls();
        _apply(preview, false);
        UpdateDirtyState(preview);
    }

    private void CategoryClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string key || !_panels.ContainsKey(key)) return;
        ShowCategory(key, clearSearch: true, restoreScroll: true);
    }

    // Per-page vertical scroll memory for the lifetime of this Settings window only: a freshly
    // opened window starts every page at the top. Only plain page switches (the navigation rail,
    // leaving search) restore; search "Open" and deep links still land on their target.
    private readonly Dictionary<string, double> _categoryScrollOffsets = new(StringComparer.OrdinalIgnoreCase);

    private void RememberCategoryScroll()
    {
        if (SearchPanel.IsVisible || string.IsNullOrEmpty(_activeCategory)) return;
        _categoryScrollOffsets[_activeCategory] = ContentScroll.Offset.Y;
    }

    /// <summary>
    /// Shows one page. <paramref name="key"/> is a page key or a legacy deep link (Viewing, Status,
    /// Hotkeys, Overlays, Profiles, Developer), which expands and reveals the card that now holds that
    /// content. <paramref name="revealTarget"/> is scrolled into view once the page has laid out
    /// (one Loaded-priority post, so it cannot race the page's own scroll reset).
    /// </summary>
    private void ShowCategory(string key, bool clearSearch, bool restoreScroll = false, Control? revealTarget = null)
    {
        if (FindGroup(key) is not { } group) return;
        SettingsCard? aliasCard = null;
        if (LegacyPageAliases.TryGetValue(key, out var alias)) _cardById.TryGetValue(alias.CardId, out aliasCard);
        RememberCategoryScroll();
        _activeCategory = group.Key;
        var plainSwitch = aliasCard is null && revealTarget is null;
        var restoreOffset = restoreScroll && plainSwitch &&
            _categoryScrollOffsets.TryGetValue(group.Key, out var remembered) ? remembered : 0;
        if (clearSearch && !string.IsNullOrEmpty(SearchBox.Text))
        {
            _loadingControls = true;
            SearchBox.Text = string.Empty;
            _loadingControls = false;
        }
        SearchPanel.IsVisible = false;
        foreach (var pair in _panels) pair.Value.IsVisible = string.Equals(pair.Key, group.Key, StringComparison.OrdinalIgnoreCase);
        foreach (var child in NavigationRail.Children.OfType<Button>())
        {
            child.Classes.Remove("selected");
            if (Equals(child.Tag, group.Key)) child.Classes.Add("selected");
        }

        PageTitle.Text = group.Title;
        PageSubtitle.Text = group.Subtitle;
        if (string.Equals(group.Key, "Mouse", StringComparison.OrdinalIgnoreCase))
        {
            _hotkeyRowsBuilt = true;
            BuildHotkeyRows(HotkeyFilterBox?.Text);
            if (!_gestureMatrixBuilt) { BuildGestureMatrix(); LoadGestureEditorsFromWorkingMap(); }
        }
        if (aliasCard is not null)
        {
            aliasCard.IsExpanded = true;
            revealTarget ??= aliasCard;
        }
        UpdateExpandCollapseButtons();
        ContentScroll.Offset = new Vector(ContentScroll.Offset.X, 0);

        // Pages built lazily on first visit (the Mouse gesture matrix, the hotkey rows) grow after
        // this call; re-pin the page once that layout has settled so the first visit does not open
        // part-way down. A revisit in the same window restores its remembered offset instead,
        // clamped to the page's current maximum. Deep links and search "Open" land on their target.
        var category = _activeCategory;
        var target = revealTarget;
        Dispatcher.UIThread.Post(() =>
        {
            if (!string.Equals(category, _activeCategory, StringComparison.Ordinal) || SearchPanel.IsVisible) return;
            if (target is not null) ScrollToReveal(target);
            else
                ContentScroll.Offset = new Vector(ContentScroll.Offset.X,
                    Math.Clamp(restoreOffset, 0, Math.Max(0, ContentScroll.ScrollBarMaximum.Y)));
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Distance between the top of the page viewport and a revealed row.</summary>
    private const double RevealTopMargin = 72;

    private void ScrollToReveal(Control target)
    {
        if (ContentScroll.Content is not Visual content) return;
        var point = target.TranslatePoint(new Point(0, 0), content);
        if (point is null) { target.BringIntoView(); return; }
        var y = Math.Clamp(point.Value.Y - RevealTopMargin, 0, Math.Max(0, ContentScroll.ScrollBarMaximum.Y));
        ContentScroll.Offset = new Vector(ContentScroll.Offset.X, y);
    }

    // ---- Search "Open": reveal a setting on its page --------------------------------------------

    private Control? _revealHighlightTarget;
    private Border? _revealHighlight;
    private DispatcherTimer? _revealHighlightTimer;

    /// <summary>The direct child of the card body that contains <paramref name="control"/> (null when detached).</summary>
    private static Control? RowInCard(SettingsCard card, Control control)
    {
        for (Control? current = control; current is not null; current = current.GetLogicalParent() as Control)
            if (ReferenceEquals(current.GetLogicalParent(), card.Body)) return current;
        return null;
    }

    /// <summary>Breadcrumb "Page > Card" of a setting, or its catalogue category when it has no card.</summary>
    private string SettingLocation(SettingDefinition setting)
    {
        if (_settingCard.TryGetValue(setting.Id, out var card) && _pageOfCard.TryGetValue(card, out var page))
            return $"{GroupOf(page).Title} > {card.Title}";
        return setting.Category;
    }

    /// <summary>
    /// Search "Open": switch to the setting's page, expand its card, scroll the row to about 72 px below
    /// the top, highlight it briefly and focus its editor. Gesture entries open their gesture row.
    /// </summary>
    private void RevealSetting(string id)
    {
        if (id.StartsWith("gesture.", StringComparison.OrdinalIgnoreCase))
        {
            OpenGestureRow(id["gesture.".Length..]);
            return;
        }
        if (!_settingCard.TryGetValue(id, out var card) || !_pageOfCard.TryGetValue(card, out var page)) return;
        var editor = _settingControls.GetValueOrDefault(id) ?? _settingAnchors.GetValueOrDefault(id);
        card.IsExpanded = true;

        // Gesture-panel secondary options (Tag="gestureArea:<area>") live in their area pane once the
        // panel is built: open that area and reveal the option there.
        if (editor is not null && GestureOptionOf(editor) is { } option)
        {
            OpenGestureArea(((string)option.Tag!)["gestureArea:".Length..], option);
            HighlightRevealedRow(option);
            FocusRevealedEditor(editor, card);
            return;
        }

        var row = editor is null || _hiddenSettingIds.Contains(id) ? null : RowInCard(card, editor);
        Control target = row ?? card.Header;
        ShowCategory(page, clearSearch: true, revealTarget: target);
        HighlightRevealedRow(target);
        FocusRevealedEditor(row is null ? null : editor, card);
    }

    private static Control? GestureOptionOf(Control control)
    {
        for (Control? current = control; current is not null; current = current.GetLogicalParent() as Control)
            if (current.Tag is string tag && tag.StartsWith("gestureArea:", StringComparison.Ordinal)) return current;
        return null;
    }

    private void FocusRevealedEditor(Control? editor, SettingsCard card)
    {
        Dispatcher.UIThread.Post(() =>
        {
            static bool CanFocus(Control c) => c.Focusable && c.IsEffectivelyVisible && c.IsEffectivelyEnabled;
            Control focusTarget = card.Header;
            if (editor is not null)
            {
                if (CanFocus(editor)) focusTarget = editor;
                else if (editor.GetLogicalDescendants().OfType<Control>().FirstOrDefault(CanFocus) is { } inner) focusTarget = inner;
            }
            focusTarget.Focus();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Accent outline over the revealed row for ~2.5 s, then a short fade (class "fading").</summary>
    private void HighlightRevealedRow(Control row)
    {
        ClearRevealHighlight();
        var highlight = new Border { IsHitTestVisible = false };
        highlight.Classes.Add(SettingsCard.RevealHighlightClass);
        AdornerLayer.SetAdorner(row, highlight);
        _revealHighlightTarget = row;
        _revealHighlight = highlight;
        var fading = false;
        _revealHighlightTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
        _revealHighlightTimer.Tick += (_, _) =>
        {
            if (!fading)
            {
                fading = true;
                highlight.Classes.Add("fading");
                _revealHighlightTimer!.Interval = TimeSpan.FromMilliseconds(350);
                return;
            }
            ClearRevealHighlight();
        };
        _revealHighlightTimer.Start();
    }

    private void ClearRevealHighlight()
    {
        _revealHighlightTimer?.Stop();
        _revealHighlightTimer = null;
        if (_revealHighlightTarget is not null && ReferenceEquals(AdornerLayer.GetAdorner(_revealHighlightTarget), _revealHighlight))
            AdornerLayer.SetAdorner(_revealHighlightTarget, null);
        _revealHighlightTarget = null;
        _revealHighlight = null;
    }

    /// <summary>Hotkey search "Open": reveal the shortcut table and select the action's row.</summary>
    private void RevealHotkey(string actionId)
    {
        RevealSetting("hotkeys.registry");
        if (HotkeyList.ItemsSource is not IEnumerable<ListBoxItem> rows) return;
        var item = rows.FirstOrDefault(r => r.Tag is HotkeyRowTag tag && string.Equals(tag.ActionId, actionId, StringComparison.OrdinalIgnoreCase));
        if (item is null) return;
        HotkeyList.SelectedItem = item;
        Dispatcher.UIThread.Post(() => HotkeyList.ScrollIntoView(item), DispatcherPriority.Background);
    }

    private void SearchCategoryChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingControls) return;
        if (!string.IsNullOrWhiteSpace(SearchBox.Text)) SearchChanged(SearchBox, null!);
    }

    private string ActiveSearchCategory() => SelectedText(SearchCategoryCombo, "All categories");

    private void SearchChanged(object? sender, TextChangedEventArgs e)
    {
        if (_loadingControls) return;
        var query = SearchBox.Text?.Trim() ?? string.Empty;
        if (query.Length == 0)
        {
            _searchDebounceTimer.Stop();
            _pendingSearchQuery = string.Empty;
            _hotkeySearchRows.Clear();
            SearchPanel.Children.Clear();
            // ShowCategory hides the search panel itself; it must still be visible on entry so the
            // search results' scroll offset is not remembered as the page's own offset.
            ShowCategory(_activeCategory, clearSearch: false, restoreScroll: true);
            SearchPanel.IsVisible = false;
            return;
        }

        // Coalesce keystrokes: the rebuild below constructs a live editor per match, so running it
        // synchronously on every TextChanged stalls typing. The most recent query always wins.
        _pendingSearchQuery = query;
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    /// <summary>
    /// Forces the pending debounced search to render immediately. Used by the diagnostic capture,
    /// which sets the search text and then screenshots without waiting for the debounce timer.
    /// </summary>
    internal void FlushPendingSearch()
    {
        _searchDebounceTimer.Stop();
        if (!string.IsNullOrEmpty(_pendingSearchQuery)) RunSearchRebuild(_pendingSearchQuery);
    }

    /// <summary>
    /// Builds the searchable text for one setting: catalog id/label/category/description/terms plus
    /// the mapped control's visible content, tooltip and control name. Cached per window because the
    /// wording never changes while Settings is open. This keeps search global and matches what the
    /// user actually reads in the UI (a checkbox labelled "Show status bar" is found by "status bar"
    /// even though the catalog label is "Show status surface").
    /// </summary>
    private string SearchHaystackFor(SettingDefinition setting)
    {
        if (_searchHaystackCache.TryGetValue(setting.Id, out var cached)) return cached;
        var sb = new StringBuilder(160);
        // The catalogue category (a page title such as "Mouse & keyboard") is deliberately not indexed:
        // it would make every setting on that page a related match for words like "keyboard".
        sb.Append(setting.Id).Append(' ')
          .Append(setting.Label).Append(' ')
          .Append(setting.Description);
        foreach (var term in setting.SearchTerms) sb.Append(' ').Append(term);
        if (_settingControls.TryGetValue(setting.Id, out var control))
        {
            AppendControlSearchText(sb, control);
            AppendRowSearchContext(sb, control);
        }
        // Every advanced gesture matrix slot is fully searchable: index its slot label/context,
        // its default behaviour and every action label offered for that slot (merged wheel pairs
        // included), even before the lazy matrix editors have been built.
        if (setting.Id.StartsWith("gesture.", StringComparison.OrdinalIgnoreCase))
        {
            var slotId = setting.Id["gesture.".Length..];
            var slot = GestureCatalog.Slots.FirstOrDefault(x => string.Equals(x.Id, slotId, StringComparison.OrdinalIgnoreCase));
            if (slot is not null)
            {
                sb.Append(' ').Append(slot.Id).Append(' ').Append(slot.Label).Append(' ').Append(slot.Context);
                if (GestureCatalog.DefaultBehavior.TryGetValue(slot.Id, out var behaviour)) sb.Append(' ').Append(behaviour);
                foreach (var action in GestureCatalog.ActionsForSlot(slot.Id)) sb.Append(' ').Append(action.Label).Append(' ').Append(action.Id);
            }
        }
        cached = sb.ToString();
        _searchHaystackCache[setting.Id] = cached;
        return cached;

        static void AppendControlSearchText(StringBuilder builder, Control control)
        {
            var hasLabelContent = false;
            if (control is ContentControl { Content: string content } && !string.IsNullOrWhiteSpace(content))
            {
                builder.Append(' ').Append(content);
                hasLabelContent = true;
            }
            if (ToolTip.GetTip(control) is string tip && !string.IsNullOrWhiteSpace(tip))
                builder.Append(' ').Append(tip);
            if (!string.IsNullOrWhiteSpace(control.Name)) builder.Append(' ').Append(control.Name);
            // ComboBox / NumericUpDown / TextBox editors carry their label as a sibling TextBlock
            // rather than Content; index that visible label so search matches what the user reads
            // (for example "Always-on-top strength").
            if (!hasLabelContent && control.GetVisualParent() is Panel parent)
            {
                var siblings = parent.Children;
                for (var i = siblings.IndexOf(control) - 1; i >= 0; i--)
                {
                    if (siblings[i] is TextBlock label && !string.IsNullOrWhiteSpace(label.Text))
                    {
                        builder.Append(' ').Append(label.Text);
                        break;
                    }
                    if (siblings[i] is not TextBlock) break;
                }
            }
        }
    }

    /// <summary>
    /// Everything a user can read around a setting's control, so search matches what is on screen and
    /// not just the control itself: the other text and tooltips anywhere in the control's row (labels
    /// carrying their own tooltip, trailing hints), the options inside a drop-down, a note line placed
    /// directly under the row, and the title and description of the section the row belongs to.
    /// </summary>
    private void AppendRowSearchContext(StringBuilder builder, Control control)
    {
        static void AddText(StringBuilder b, string? text)
        {
            if (!string.IsNullOrWhiteSpace(text)) b.Append(' ').Append(text);
        }

        static bool IsSectionLabel(Control c) => c is TextBlock t && t.Classes.Contains("settingsSectionLabel");
        static bool IsNote(Control c) => c is TextBlock t && t.Classes.Contains("phaseNote");

        static void AddControlTexts(StringBuilder b, Control c)
        {
            if (ToolTip.GetTip(c) is string tip) AddText(b, tip);
            switch (c)
            {
                case TextBlock text: AddText(b, text.Text); break;
                case ContentControl { Content: string content }: AddText(b, content); break;
            }
            if (c is ItemsControl items)
            {
                foreach (var item in items.Items)
                {
                    if (item is string option) AddText(b, option);
                    else if (item is ContentControl { Content: string optionContent }) AddText(b, optionContent);
                }
            }
        }

        static bool IsCardBody(Panel p) => p.Classes.Contains(SettingsCard.BodyClass);

        // The row is the direct child of its card body (or of the panel that also holds section titles).
        Control row = control;
        for (var depth = 0; depth < 8 && row.GetVisualParent() is Panel parent; depth++)
        {
            // Never climb into the card body or the page itself, or a card/page without section labels
            // would index its whole text into every one of its settings.
            if (IsCardBody(parent) || parent.Children.Any(IsSectionLabel) || _panels.Values.Any(p => ReferenceEquals(p, parent))) break;
            row = parent;
        }

        AddControlTexts(builder, row);
        foreach (var descendant in Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(row).OfType<Control>())
            AddControlTexts(builder, descendant);

        if (row.GetVisualParent() is not Panel page) return;
        var children = page.Children;
        var index = children.IndexOf(row);
        if (index < 0) return;

        // A note placed directly under the row describes it.
        for (var i = index + 1; i < children.Count && IsNote(children[i]); i++) AddControlTexts(builder, children[i]);

        // The section's title and its description notes apply to every setting in the section.
        for (var i = index - 1; i >= 0; i--)
        {
            if (!IsSectionLabel(children[i])) continue;
            AddControlTexts(builder, children[i]);
            for (var n = i + 1; n < children.Count && IsNote(children[n]); n++) AddControlTexts(builder, children[n]);
            break;
        }

        // Inside a card: the card's title and subtitle, and the notes that open its body, describe
        // every row of the card (related matches only). The header itself is never a setting row.
        if (IsCardBody(page) && page.FindLogicalAncestorOfType<SettingsCard>() is { } card)
        {
            AddText(builder, card.Title);
            AddText(builder, card.Subtitle);
            for (var i = 0; i < children.Count && i < index && IsNote(children[i]); i++) AddControlTexts(builder, children[i]);
        }
    }

    private int _searchRenderVersion;

    /// <summary>Synchronous rebuild: every matching card exists when this returns (diagnostics, tests).</summary>
    private void RunSearchRebuild(string query) => RunSearchRebuildCore(query, progressive: false);

    private readonly Dictionary<string, string> _searchDirectCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The words a user actually reads as this setting's name: its catalogue title plus the on-screen
    /// label of its editor. Everything else in <see cref="SearchHaystackFor"/> (descriptions, tooltips,
    /// search terms, section text) is only a related match.
    /// </summary>
    private string SearchDirectHaystackFor(SettingDefinition setting)
    {
        if (_searchDirectCache.TryGetValue(setting.Id, out var cached)) return cached;
        var sb = new StringBuilder(setting.Label);
        void Wording(Control? control)
        {
            if (control is null) return;
            if (control is ContentControl { Content: string content } && !string.IsNullOrWhiteSpace(content))
            {
                sb.Append(' ').Append(content);
                return;
            }
            if (control.GetVisualParent() is not Panel parent) return;
            var siblings = parent.Children;
            for (var i = siblings.IndexOf(control) - 1; i >= 0; i--)
            {
                if (siblings[i] is TextBlock label && !string.IsNullOrWhiteSpace(label.Text)) { sb.Append(' ').Append(label.Text); return; }
                if (siblings[i] is not TextBlock) return;
            }
        }
        if (_settingControls.TryGetValue(setting.Id, out var editor)) Wording(editor);
        cached = sb.ToString();
        _searchDirectCache[setting.Id] = cached;
        return cached;
    }

    private void RunSearchRebuildCore(string query, bool progressive)
    {
        if (_loadingControls || string.IsNullOrEmpty(query)) return;
        // Gesture slot editors are lazy; build them so search results can proxy the real combos.
        if (!_gestureMatrixBuilt) { BuildGestureMatrix(); LoadGestureEditorsFromWorkingMap(); }

        foreach (var panel in _panels.Values) panel.IsVisible = false;
        foreach (var child in NavigationRail.Children.OfType<Button>()) child.Classes.Remove("selected");
        SearchPanel.Children.Clear();
        _hotkeySearchRows.Clear();
        _gestureSearchPickers.Clear();
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var categoryFilter = ActiveSearchCategory();
        var allCategories = string.Equals(categoryFilter, "All categories", StringComparison.OrdinalIgnoreCase);
        // Catalogue categories are the page titles, so the filter compares them directly.
        var matches = SettingsCatalog.All.Where(setting =>
            (allCategories || string.Equals(setting.Category, categoryFilter, StringComparison.OrdinalIgnoreCase)) &&
            words.All(word => SearchHaystackFor(setting).Contains(word, StringComparison.OrdinalIgnoreCase))).ToList();
        // Title matches first; settings that match only through a description, tooltip or search term
        // are "related" and listed after them (and can be hidden with the Settings option).
        var direct = matches.Where(setting => words.All(word => SearchDirectHaystackFor(setting).Contains(word, StringComparison.OrdinalIgnoreCase))).ToList();
        var related = matches.Except(direct).ToList();
        var showRelated = SearchShowRelatedCheck.IsChecked == true;
        if (!showRelated) matches = direct;

        // Legacy Settings search is not a read-only index: the user can change matching values here.
        // Each result is a live editor, so cards are queued and built in small batches (see AddBatch).
        var builders = new List<Func<Control>>();
        foreach (var setting in direct)
        {
            var captured = setting;
            builders.Add(() => BuildSearchResult(captured));
        }
        // Every custom gesture is its own result card (they are dynamic, so not catalogue entries).
        var customMatches = CustomGestureSearchMatches(words).ToList();
        foreach (var index in customMatches)
        {
            var captured = index;
            builders.Add(() => BuildCustomGestureSearchCard(captured));
        }
        if (showRelated && related.Count > 0)
        {
            builders.Add(() => new TextBlock
            {
                Text = "Related settings",
                FontSize = 15,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(2, 10, 0, 1)
            });
            foreach (var setting in related)
            {
                var captured = setting;
                builders.Add(() => BuildSearchResult(captured));
            }
        }

        var includeHotkeys = allCategories || string.Equals(categoryFilter, GroupOf("Mouse").Title, StringComparison.OrdinalIgnoreCase);
        var hotkeyMatches = includeHotkeys ? HotkeyCatalog.All.Where(h => words.All(word =>
            h.Label.Contains(word, StringComparison.OrdinalIgnoreCase) ||
            h.Category.Contains(word, StringComparison.OrdinalIgnoreCase) ||
            h.Id.Contains(word, StringComparison.OrdinalIgnoreCase) ||
            (h.SearchTerms?.Any(term => term.Contains(word, StringComparison.OrdinalIgnoreCase)) ?? false) ||
            word.Contains("hotkey", StringComparison.OrdinalIgnoreCase) ||
            word.Contains("shortcut", StringComparison.OrdinalIgnoreCase) ||
            _workingHotkeys.GetValueOrDefault(h.Id, EmptyShortcutList).Any(k => k.Contains(word, StringComparison.OrdinalIgnoreCase)))).ToList() : new List<HotkeyActionDefinition>();
        if (hotkeyMatches.Count > 0)
        {
            builders.Add(() => new TextBlock
            {
                Text = "Hotkeys",
                FontSize = 15,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(2, 7, 0, 1)
            });
            foreach (var action in hotkeyMatches.Take(18))
            {
                var captured = action;
                builders.Add(() => BuildHotkeySearchResult(captured));
            }
        }

        // Fixed (read-only) shortcuts from the master action registry, e.g. Explorer's Ctrl+C or slideshow Space.
        var fixedMatches = includeHotkeys ? HotkeyCatalog.FixedShortcuts.Where(f => words.All(word =>
            f.Label.Contains(word, StringComparison.OrdinalIgnoreCase) ||
            f.Category.Contains(word, StringComparison.OrdinalIgnoreCase) ||
            f.Shortcut.Contains(word, StringComparison.OrdinalIgnoreCase) ||
            f.SearchTerms.Any(term => term.Contains(word, StringComparison.OrdinalIgnoreCase)) ||
            word.Contains("hotkey", StringComparison.OrdinalIgnoreCase) ||
            word.Contains("shortcut", StringComparison.OrdinalIgnoreCase))).ToList() : new List<FixedShortcutDefinition>();
        if (fixedMatches.Count > 0)
        {
            if (hotkeyMatches.Count == 0)
                builders.Add(() => new TextBlock { Text = "Hotkeys", FontSize = 15, FontWeight = FontWeight.SemiBold, Margin = new Thickness(2, 7, 0, 1) });
            foreach (var fixedShortcut in fixedMatches.Take(8))
            {
                var captured = fixedShortcut;
                builders.Add(() => BuildFixedShortcutSearchResult(captured));
            }
        }

        if (matches.Count == 0 && hotkeyMatches.Count == 0 && customMatches.Count == 0 && fixedMatches.Count == 0)
            SearchPanel.Children.Add(new TextBlock { Text = "No matching Glide setting.", Margin = new Thickness(6, 12), Foreground = SearchMutedBrush() });

        // A newer query bumps the version, which silently ends any batch still waiting for its turn.
        var version = ++_searchRenderVersion;
        void AddBatch(int from)
        {
            if (version != _searchRenderVersion) return;
            var to = progressive ? Math.Min(builders.Count, from + 4) : builders.Count;
            for (var i = from; i < to; i++) SearchPanel.Children.Add(builders[i]());
            if (to < builders.Count)
                Dispatcher.UIThread.Post(() => AddBatch(to), DispatcherPriority.Background);
        }
        AddBatch(0);

        RememberCategoryScroll();
        SearchPanel.IsVisible = true;
        UpdateExpandCollapseButtons();
        PageTitle.Text = "Search results";
        PageSubtitle.Text = allCategories
            ? $"Edit settings matching ‘{query}’ across all categories"
            : $"Edit settings matching ‘{query}’ in {categoryFilter}";
        ContentScroll.Offset = new Vector(ContentScroll.Offset.X, 0);
    }

    /// <summary>A read-only search card for a fixed shortcut (no Change / Add / Clear buttons).</summary>
    private Control BuildFixedShortcutSearchResult(FixedShortcutDefinition fixedShortcut)
    {
        var border = new Border();
        border.Classes.Add("searchResult");
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,210"), ColumnSpacing = 7 };
        var label = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(new TextBlock { Text = fixedShortcut.Label, FontWeight = FontWeight.SemiBold, FontSize = 14.5 });
        label.Children.Add(new TextBlock { Text = fixedShortcut.Category + " — fixed shortcut, not editable", Foreground = SearchMutedBrush(), FontSize = 12 });
        grid.Children.Add(label);
        var keys = new TextBlock { Text = fixedShortcut.Shortcut, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(keys, 1);
        grid.Children.Add(keys);
        border.Child = grid;
        ToolTip.SetTip(border, fixedShortcut.Description);
        return border;
    }

    private Control BuildHotkeySearchResult(HotkeyActionDefinition action)
    {
        var border = new Border();
        border.Classes.Add("searchResult");
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,210,Auto,Auto,Auto,Auto"), ColumnSpacing = 7 };
        var label = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(new TextBlock { Text = action.Label, FontWeight = FontWeight.SemiBold, FontSize = 14.5 });
        label.Children.Add(new TextBlock { Text = action.Category, Foreground = SearchMutedBrush(), FontSize = 12 });
        grid.Children.Add(label);

        var shortcutText = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(shortcutText, 1);
        grid.Children.Add(shortcutText);

        Button Make(string text, int column, Action actionHandler, string tooltip)
        {
            var button = new Button { Content = text, MinWidth = 64, Height = 30, VerticalAlignment = VerticalAlignment.Center };
            button.Classes.Add("searchOpen");
            button.Click += (_, _) => actionHandler();
            ToolTip.SetTip(button, tooltip);
            ToolTip.SetShowDelay(button, 450);
            Grid.SetColumn(button, column);
            grid.Children.Add(button);
            return button;
        }

        Make("Change", 2, () => StartHotkeyCaptureFor(action.Id, _workingHotkeys.GetValueOrDefault(action.Id, new()).FirstOrDefault(), add: false, fromSearch: true, border),
            "Replace the primary shortcut. You can also double-click this hotkey result card.");
        Make("Add", 3, () => StartHotkeyCaptureFor(action.Id, null, add: true, fromSearch: true, border),
            "Add another keyboard shortcut without removing existing shortcuts.");
        Make("Clear", 4, () =>
        {
            _workingHotkeys[action.Id] = new List<string>();
            BuildHotkeyRows();
            PreviewHotkeyChange();
            RefreshHotkeySearchDisplay(action.Id);
            PageSubtitle.Text = $"Cleared shortcuts for {action.Label}.";
        }, "Clear every shortcut currently assigned to this action.");
        var shortcutsCard = _cardById.GetValueOrDefault("mouse-shortcuts");
        Make("Open", 5, () => RevealHotkey(action.Id),
            shortcutsCard is null ? "Show this action in the shortcut list" : $"Show in {GroupOf(_pageOfCard[shortcutsCard]).Title} > {shortcutsCard.Title}");

        border.Child = grid;
        _hotkeySearchRows[action.Id] = (border, shortcutText);
        RefreshHotkeySearchDisplay(action.Id);
        border.DoubleTapped += (_, e) =>
        {
            // Buttons keep their own double-click semantics; everywhere else on a hotkey result card
            // behaves exactly like the Change button and enters real keyboard capture mode.
            if (e.Source is Control source)
            {
                Control? current = source;
                while (current is not null && !ReferenceEquals(current, border))
                {
                    if (current is Button) return;
                    current = current.GetVisualParent() as Control;
                }
            }
            StartHotkeyCaptureFor(action.Id, _workingHotkeys.GetValueOrDefault(action.Id, new()).FirstOrDefault(), add: false, fromSearch: true, border);
            e.Handled = true;
        };
        ToolTip.SetTip(border, "Double-click to change this hotkey. Drag/scroll position is preserved while capturing.");
        return border;
    }

    private void RefreshHotkeySearchDisplay(string actionId)
    {
        if (!_hotkeySearchRows.TryGetValue(actionId, out var row)) return;
        var shortcuts = _workingHotkeys.GetValueOrDefault(actionId, new());
        row.Shortcut.Text = shortcuts.Count == 0 ? "Unassigned" : string.Join("  •  ", shortcuts);
        row.Shortcut.Foreground = shortcuts.Count == 0 ? SearchMutedBrush() : SearchAccentBrush();
    }

    private Control BuildSearchResult(SettingDefinition setting)
    {
        if (setting.Id.StartsWith("gesture.", StringComparison.OrdinalIgnoreCase) &&
            GestureCatalog.FindSlot(setting.Id["gesture.".Length..]) is { } gestureSlot)
            return BuildGestureSearchCard(setting, gestureSlot);
        var border = new Border();
        border.Classes.Add("searchResult");
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = setting.Label, FontWeight = FontWeight.SemiBold, FontSize = 15 });
        var location = SettingLocation(setting);
        text.Children.Add(new TextBlock { Text = $"{location}  •  {setting.Description}", TextWrapping = TextWrapping.Wrap, Foreground = SearchMutedBrush(), FontSize = 12.5 });
        grid.Children.Add(text);

        Control editor;
        Control? source = null;
        var companions = CreateSearchCompanionButtons(setting.Id).ToList();
        // Every result can be opened on its page. Results whose companions already offer Open
        // (gesture rows, the gesture panel, the shortcut table) reuse that button.
        var openButton = companions.FirstOrDefault(b => string.Equals(b.Content as string, "Open", StringComparison.Ordinal));
        if (openButton is null)
        {
            openButton = new Button { Content = "Open", MinWidth = 64, VerticalAlignment = VerticalAlignment.Center };
            openButton.Classes.Add("searchOpen");
            openButton.Click += (_, _) => RevealSetting(setting.Id);
            companions.Add(openButton);
        }
        ToolTip.SetTip(openButton, $"Show in {location}");
        ToolTip.SetShowDelay(openButton, 450);
        if (setting.FuturePhase is not null)
        {
            editor = new TextBlock
            {
                Text = $"Future (Phase {setting.FuturePhase})",
                Foreground = SearchMutedBrush(),
                VerticalAlignment = VerticalAlignment.Center,
                FontStyle = FontStyle.Italic
            };
            ToolTip.SetTip(editor, setting.Description);
        }
        else if (_settingControls.TryGetValue(setting.Id, out source) && source.IsEnabled)
            editor = CreateSearchProxy(source);
        else
        {
            // Action settings own labelled companion buttons (Open/Import/Add/...) and every card has
            // its Open button, so no editor placeholder is needed.
            editor = new Panel { Width = 0, Height = 1 };
        }
        var completeEditor = BuildSearchEditorGroup(editor, companions);
        Grid.SetColumn(completeEditor, 1);
        grid.Children.Add(completeEditor);
        border.Child = grid;

        // Boolean search cards write the authoritative staged CheckBox directly. The small checkbox
        // shown on the result is display-only, so there is exactly one source of truth and one toggle
        // per click. ValueControlChanged then previews/persists the same value used by Apply/OK.
        if (source is CheckBox sourceCheckBox && editor is CheckBox displayCheckBox)
        {
            border.AddHandler(InputElement.PointerReleasedEvent, (_, e) =>
            {
                if (e.InitialPressMouseButton != MouseButton.Left) return;
                // The card's own buttons (Open, companions) keep their click; they must never also
                // flip the setting.
                for (var current = e.Source as Control; current is not null && !ReferenceEquals(current, border); current = current.GetVisualParent() as Control)
                    if (current is Button) return;
                var next = sourceCheckBox.IsChecked != true;
                sourceCheckBox.IsChecked = next;
                displayCheckBox.IsChecked = next;
                e.Handled = true;
            }, RoutingStrategies.Bubble, true);
            ToolTip.SetTip(border, $"Click anywhere on this card to toggle. {setting.Description}");
        }
        else ToolTip.SetTip(border, setting.Description);
        return border;
    }

    private Control BuildSearchEditorGroup(Control editor, IReadOnlyList<Button> companions)
    {
        if (companions.Count == 0) return editor;
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(editor);
        foreach (var button in companions) panel.Children.Add(button);
        return panel;
    }

    private IEnumerable<Button> CreateSearchCompanionButtons(string id)
    {
        Button B(string text, Action<Button> click)
        {
            var b = new Button { Content = text, MinWidth = 72, VerticalAlignment = VerticalAlignment.Center };
            b.Classes.Add("secondaryAction");
            b.Click += (_, _) => click(b);
            return b;
        }
        if ((id ?? "").StartsWith("gesture.", StringComparison.OrdinalIgnoreCase))
        {
            var slotId = id!["gesture.".Length..];
            var open = B("Open", b => OpenGestureRow(slotId));
            if (_cardById.TryGetValue("mouse-gestures", out var gestureCard))
                ToolTip.SetTip(open, $"Show in {GroupOf(_pageOfCard[gestureCard]).Title} > {gestureCard.Title}");
            yield return open;
            yield break;
        }
        switch (id)
        {
            case "windows.external1": yield return B("Browse...", b => { b.Tag = "1"; BrowseExternalClicked(b, new RoutedEventArgs()); }); break;
            case "windows.external2": yield return B("Browse...", b => { b.Tag = "2"; BrowseExternalClicked(b, new RoutedEventArgs()); }); break;
            case "windows.external3": yield return B("Browse...", b => { b.Tag = "3"; BrowseExternalClicked(b, new RoutedEventArgs()); }); break;
            case "windows.fileAssociations":
                yield return B("Add", b => AddOpenWithClicked(b, new RoutedEventArgs()));
                yield return B("Remove", b => RemoveOpenWithClicked(b, new RoutedEventArgs()));
                break;
            case "general.openFileDefaultDirectory": yield return B("Browse...", b => BrowseOpenDefaultDirectoryClicked(b, new RoutedEventArgs())); break;
            case "general.newExplorerTabDefaultDirectory": yield return B("Browse...", b => BrowseNewExplorerDefaultDirectoryClicked(b, new RoutedEventArgs())); break;
            case "overlay.defaultDirectory": yield return B("Browse...", b => BrowseOverlayDefaultDirectoryClicked(b, new RoutedEventArgs())); break;
            case "profiles.defaultDirectory": yield return B("Browse...", b => BrowseProfileDefaultDirectoryClicked(b, new RoutedEventArgs())); break;
            case "appearance.accent": yield return B("Pick...", b => PickAccentColorClicked(b, new RoutedEventArgs())); break;
            case "appearance.glowColor": yield return B("Pick...", b => PickGlowColorClicked(b, new RoutedEventArgs())); break;
            case "appearance.background": yield return B("Pick...", b => PickBackgroundColorClicked(b, new RoutedEventArgs())); break;
            case "general.startupCustomPath":
                yield return B("File...", b => BrowseStartupFileClicked(b, new RoutedEventArgs()));
                yield return B("Folder...", b => BrowseStartupFolderClicked(b, new RoutedEventArgs()));
                break;
            case "general.newTabCustomPath":
                yield return B("File...", b => BrowseNewTabFileClicked(b, new RoutedEventArgs()));
                yield return B("Folder...", b => BrowseNewTabFolderClicked(b, new RoutedEventArgs()));
                break;
            case "performance.initialQuality": yield return B("Save custom", b => SaveUserPerformancePresetClicked(b, new RoutedEventArgs())); break;
            case "titlebar.customize": yield return B("Configure...", b => CustomizeTitleBarClicked(b, new RoutedEventArgs())); break;
            case "escape.resetRememberedClose": yield return B("Reset", b => ResetEscChoiceClicked(b, new RoutedEventArgs())); break;
            case "profiles.import": yield return B("Import...", b => ImportSettingsClicked(b, new RoutedEventArgs())); break;
            case "profiles.export": yield return B("Export...", b => ExportSettingsClicked(b, new RoutedEventArgs())); break;
            case "profiles.presets": yield return B("Apply", b => ApplyPresetClicked(b, new RoutedEventArgs())); break;
            case "hotkeys.preset": yield return B("Load", b => LoadHotkeyPresetClicked(b, new RoutedEventArgs())); break;
            case "hotkeys.customPresets": yield return B("Save...", b => SaveHotkeyPresetClicked(b, new RoutedEventArgs())); break;
            case "hotkeys.searchHotkey": yield return B("Search", b => { RevealSetting("hotkeys.registry"); BeginHotkeyLookupCapture(); }); break;
            case "mouse.gestureMatrix": yield return B("Open", b => RevealSetting("mouse.gestureMatrix")); break;
            case "hotkeys.registry": yield return B("Open", b => RevealSetting("hotkeys.registry")); break;
            case "reset.card": yield return B("Open", b => RevealSetting("reset.card")); break;
            case "developer.diagnostics":
                yield return B("Run", b => RunDiagnosticsClicked(b, new RoutedEventArgs()));
                yield return B("Export ZIP", b => ExportDiagnosticsClicked(b, new RoutedEventArgs()));
                break;
        }
    }

    private Control CreateSearchProxy(Control source)
    {
        switch (source)
        {
            case CheckBox cb:
            {
                var proxy = new CheckBox
                {
                    IsChecked = cb.IsChecked,
                    VerticalAlignment = VerticalAlignment.Center,
                    IsHitTestVisible = false,
                    Focusable = false
                };
                return proxy;
            }
            case NumericUpDown numeric:
            {
                var proxy = new NumericUpDown { Width = 150, Minimum = numeric.Minimum, Maximum = numeric.Maximum, Increment = numeric.Increment, Value = numeric.Value, VerticalAlignment = VerticalAlignment.Center };
                proxy.PropertyChanged += (_, e) =>
                {
                    if (e.Property == NumericUpDown.ValueProperty && !_loadingControls) numeric.Value = proxy.Value;
                };
                return proxy;
            }
            case ComboBox combo:
            {
                var proxy = new ComboBox { Width = 190, VerticalAlignment = VerticalAlignment.Center };
                foreach (var item in combo.Items.OfType<ComboBoxItem>()) proxy.Items.Add(new ComboBoxItem { Content = item.Content?.ToString() ?? string.Empty });
                SelectByText(proxy, SelectedText(combo));
                proxy.SelectionChanged += (_, _) => { if (!_loadingControls) SelectByText(combo, SelectedText(proxy)); };
                return proxy;
            }
            case TextBox box:
            {
                var proxy = new TextBox { Width = 260, Text = box.Text, VerticalAlignment = VerticalAlignment.Center, IsReadOnly = box.IsReadOnly };
                proxy.TextChanged += (_, _) => { if (!_loadingControls && !box.IsReadOnly) box.Text = proxy.Text; };
                return proxy;
            }
            case Button button:
            {
                return new TextBlock { Text = button.Content?.ToString() ?? "Action", VerticalAlignment = VerticalAlignment.Center, Foreground = SearchMutedBrush() };
            }
            default:
            {
                var open = new Button { Content = "Open", Tag = "General" };
                open.Classes.Add("searchOpen");
                return open;
            }
        }
    }

    private void BuildSettingControlMap()
    {
        void M(string id, Control control) => _settingControls[id] = control;
        M("general.recentHistory", RecentHistoryCheck); M("search.showRelatedResults", SearchShowRelatedCheck); M("general.historySize", HistorySizeBox); M("windows.rememberPlacement", RememberPlacementCheck); M("general.openFileDefaultDirectory", OpenFileDefaultDirectoryBox);
        M("general.singleInstance", ReuseSingleInstanceCheck); M("navigation.siblingFolders", ContinueSiblingFoldersCheck); M("navigation.hierarchicalFolders", HierarchicalFolderTraversalCheck); M("navigation.confirmHierarchicalBoundary", ConfirmHierarchicalFolderTraversalCheck); M("navigation.hierarchicalPreviousEntry", HierarchicalPreviousFolderEntryCombo); M("navigation.rateLimitMs", NavigationRateLimitMsBox); M("navigation.rateLimitInput", NavigationRateLimitInputCombo); M("navigation.rateLimitIndicator", NavigationRateLimitIndicatorCheck); M("navigation.rateLimitIndicatorPosition", NavigationRateLimitIndicatorPositionCombo); M("navigation.rateLimitWindowed", NavigationRateLimitWindowedCheck); M("navigation.rateLimitFullscreen", NavigationRateLimitFullscreenCheck); M("navigation.rateLimitSlideshow", NavigationRateLimitSlideshowCheck); M("general.lastOpenLocation", RememberLastOpenCheck); M("general.newExplorerTabLastLocation", NewExplorerTabLastLocationCheck); M("general.newExplorerTabDefaultDirectory", NewExplorerTabDefaultDirectoryBox);
        M("appearance.theme", ThemeCombo); M("appearance.explorerTheme", ExplorerThemeCombo); M("appearance.accent", AccentCombo); M("appearance.glowColor", GlowCombo); M("appearance.background", MainBackgroundCombo); M("appearance.glowIntensity", GlowIntensityBox);
        M("general.showRecentOnHome", ShowRecentOnHomeCheck); M("general.confirmDiscardSettings", ConfirmDiscardSettingsCheck);
        M("general.startupAction", StartupActionCombo); M("general.startupCustomPath", StartupCustomPathBox); M("general.newTabAction", NewTabActionCombo); M("general.newTabCustomPath", NewTabCustomPathBox); M("titlebar.customize", CustomizeTitleBarButton); M("escape.resetRememberedClose", ResetEscChoiceButton);
        M("status.visible", StatusCheck); M("status.hoverWhenClosed", StatusHoverWhenClosedCheck); M("view.scrollbars", ShowScrollbarsCheck); M("window.fullPathTitle", FullPathTitleCheck); M("fullscreen.hideCursor", HideCursorFullscreenCheck); M("fullscreen.hideCursorDelay", FullscreenHideCursorDelayBox);
        M("fullscreen.autoHideChrome", AutoHideFullscreenChromeCheck); M("fullscreen.keepTabBarOpen", FullscreenKeepTabBarOpenCheck); M("tabs.enabled", TabsCheck); M("window.showInExplorerTarget", ShowInExplorerTargetCombo); M("explorer.defaultView", ExplorerDefaultViewCombo); M("explorer.showHidden", ExplorerShowHiddenFilesCheck); M("explorer.onlyImages", ExplorerShowOnlyImagesCheck); M("tabs.showBackButton", TabBarBackButtonCheck); M("tabs.showForwardButton", TabBarForwardButtonCheck); M("fullscreen.statusAlways", FullscreenStatusAlwaysCheck);
        M("caption.windowedMinimize", CaptionWindowedMinimizeCombo); M("caption.windowedMaximize", CaptionWindowedMaximizeCombo); M("caption.windowedClose", CaptionWindowedCloseCombo);
        M("caption.fullscreenMinimize", CaptionFullscreenMinimizeCombo); M("caption.fullscreenMaximize", CaptionFullscreenMaximizeCombo); M("caption.fullscreenClose", CaptionFullscreenCloseCombo);
        M("windows.alwaysOnTop", AlwaysOnTopCheck); M("windows.alwaysOnTopMode", AlwaysOnTopModeCombo); M("fullscreen.exitBehavior", FullscreenExitBehaviorCombo); M("windows.sameImageBehavior", SameImageBehaviorCombo); M("window.centerOnDisplay", AutoCenterWindowOnRestoreCheck); M("view.defaultMode", DefaultViewCombo); M("view.animateGifs", AnimateGifsCheck); M("view.pointerZoom", PointerZoomCheck); M("view.keepZoom", PreserveZoomCheck);
        M("view.zoomStep", ViewportZoomStepBox); M("view.upscaleSmallImages", UpscaleSmallImagesCheck); M("view.subpixelRendering", SubpixelRenderingCheck); M("view.showLoadingIndicator", ShowLoadingIndicatorCheck);
        M("keyboard.tabFocusNavigation", TabFocusNavigationCheck);
        M("escape.stopSlideshow", EscStopsSlideshowCheck); M("escape.exitFullscreen", EscExitsFullscreenCheck); M("escape.confirmWindowedClose", EscWindowedConfirmCheck);
        M("mouse.invertWheel", InvertWheelCheck); M("mouse.leftWindowDrag", LeftWindowDragBehaviorCombo); M("mouse.rightDragPan", RightDragBehaviorCombo);
        M("selection.buffer", SelectionBufferCheck); M("selection.bufferWindowedPercent", SelectionBufferWindowedBox);
        M("selection.bufferMaximizedPercent", SelectionBufferMaximizedBox); M("selection.bufferAllModes", SelectionBufferAllModesCheck);
        M("developer.reverseSelectionZoomOut", ReverseSelectionZoomOutScaleCheck); M("mouse.backgroundWindowDrag", BackgroundDragCheck);
        M("performance.initialQuality", InitialQualityCombo); M("performance.saveCustomPreset", SaveUserPerformancePresetButton); M("performance.adaptivePreview", AdaptiveFastPreviewCheck); M("performance.previewDelay", AdaptivePreviewDelayBox);
        M("performance.interactivePanQuality", InteractivePanQualityCombo);
        M("performance.sequentialReads", SequentialReadCheck); M("performance.prefetch", PrefetchCheck); M("performance.backgroundRefinement", BackgroundRefinementCheck);
        M("performance.speedBoost", SpeedBoostCheck); M("performance.speedBoostTrayIcon", SpeedBoostTrayIconCombo); M("performance.startWithWindowsInBackground", StartWithWindowsInBackgroundCheck);
        M("performance.purgeOnMinimize", PurgeCacheCheck); M("developer.startupDiagnostics", StartupDiagnosticsCheck); M("performance.prefetchDepth", PrefetchDepthBox);
        M("performance.prefetchSiblingFolders", PrefetchSiblingFoldersCheck); M("performance.prefetchSiblingFolderCount", PrefetchSiblingFolderCountBox);
        M("performance.cacheItems", CacheItemsBox); M("performance.compressedCacheMb", CompressedCacheMbBox); M("performance.decodedCacheMb", DecodedCacheMbBox); M("performance.rapidPreviewSize", RapidPreviewBox); M("performance.rapidBrowsePreviewSize", RapidBrowsePreviewBox); M("performance.progressiveColor", ProgressiveColorCheck);
        M("overlay.wholeAppAlwaysStart", WholeAppOverlayAlwaysStartCheck); M("overlay.wholeAppWindowedInteractions", WholeAppOverlayWindowedInteractionsCheck);
        M("status.size", StatusBarSizeCombo); M("status.alignment", StatusBarAlignmentCombo); M("status.navigation", StatusNavigationCheck); M("status.zoom", StatusZoomCheck); M("status.slideshow", StatusSlideshowCheck); M("status.fit", StatusFitCheck);
        M("status.fitScope", StatusFitScopeCombo);
        M("status.autoResize", StatusAutoFitCheck);
        M("status.info", StatusInfoCheck); M("status.options", StatusOptionsCheck); M("status.close", StatusCloseCheck); M("status.collapsedCorner", StatusCollapsedCornerCheck); M("status.autoHide", StatusAutoHideCheck); M("status.revealZoneExpand", StatusRevealZoneExpandBox); M("status.reserveFitSpace", StatusReserveFitSpaceCheck); M("status.stackInfoLine", StatusStackInfoLineCheck); M("status.statIndex", StatIndexCheck);
        M("status.statResolution", StatResolutionCheck); M("status.statZoom", StatZoomCheck); M("status.statFileSize", StatFileSizeCheck); M("status.statFormat", StatFormatCheck); M("status.statFileName", StatFileNameCheck);
        M("slideshow.interval", SlideshowIntervalBox); M("slideshow.loop", SlideshowLoopCheck); M("navigation.sortOrder", NavigationSortOrderCombo); M("navigation.sortDescending", NavigationSortDescendingCheck); M("slideshow.showQualityIndicator", SlideshowQualityIndicatorCheck); M("slideshow.crossFolders", SlideshowCrossFoldersCheck); M("slideshow.shuffle", SlideshowShuffleCheck);
        M("slideshow.direction", SlideshowDirectionCombo); M("slideshow.startFullscreen", SlideshowStartFullscreenCheck); M("slideshow.pauseInactive", SlideshowPauseInactiveCheck);
        M("tabs.minWidth", TabMinWidthBox); M("tabs.maxWidth", TabMaxWidthBox); M("tabs.overflowArrows", TabOverflowArrowsCheck); M("tabs.detach", TabDetachCheck); M("tabs.attach", TabAttachCheck); M("tabs.closedHistory", ClosedHistoryBox);
        M("tabs.closeEmptyAfterDetach", CloseEmptyAfterDetachCheck); M("tabs.detachedHome", DetachedHomeTabCheck); M("tabs.cycleOrder", TabCycleOrderCombo); M("tabs.tooltipTextSize", TabToolTipTextSizeCombo);
        M("tabs.lastTabBehavior", LastTabBehaviorCombo); M("tabs.confirmCloseMultiple", ConfirmCloseMultipleTabsCheck); M("tabs.homePage", HomePageCombo); M("folderNav.group", FolderNavGroupCheck); M("folderNav.previous", FolderNavPreviousCheck);
        M("folderNav.showCounter", ShowFolderCounterCheck); M("folderNav.counterGrandparent", FolderCounterGrandparentCheck);
        M("folderNav.next", FolderNavNextCheck); M("folderNav.explore", FolderNavExploreCheck); M("folderNav.skipEmpty", FolderNavSkipEmptyCheck); M("folderNav.includeHidden", FolderNavIncludeHiddenCheck); M("folderNav.wrap", FolderNavWrapCheck); M("folderNav.openFirst", FolderNavFirstImageCheck); M("folderNav.order", FolderNavOrderCombo);
        M("overlay.pictureCounter", PictureCounterCheck); M("overlay.pictureTemplate", PictureTemplateBox); M("overlay.pictureFontSize", PictureFontSizeBox);
        M("overlay.pictureBold", PictureBoldCheck); M("overlay.pictureOpacity", PictureOpacityBox); M("overlay.picturePosition", PicturePositionCombo);
        M("overlay.pictureColor", PictureColorCombo); M("overlay.pictureShadow", PictureShadowCheck); M("overlay.defaultOpacity", OverlayOpacityBox); M("overlay.zoomStep", OverlayZoomStepBox);
        M("overlay.rememberFolder", OverlayRememberFolderCheck); M("overlay.scaleWithWindow", OverlayScaleWithWindowCheck); M("overlay.animationRegion", OverlayAnimationRegionCombo); M("overlay.animationSpeedDipsPerSecond", OverlayAnimationSpeedBox); M("overlay.animationTurnIntervalMs", OverlayAnimationTurnIntervalBox); M("overlay.animationTurnAngleDegrees", OverlayAnimationTurnAngleBox); M("overlay.animationPauseWhileInteracting", OverlayAnimationPauseCheck); M("overlay.animationAvoidOverlap", OverlayAnimationAvoidOverlapCheck); M("overlay.animationRefreshRateHz", OverlayAnimationRefreshRateCombo); M("overlay.defaultDirectory", OverlayDefaultDirectoryBox); M("profiles.defaultDirectory", ProfileDefaultDirectoryBox); M("overlay.persistSession", OverlayPersistSessionCheck); M("overlay.keyboardZoom", OverlayKeyboardZoomCheck); M("overlay.highlightSelected", OverlayHighlightCheck); M("overlay.rememberZoom", OverlayRememberZoomCheck);
        M("windows.externalOpenBehavior", ExternalOpenBehaviorCombo); M("windows.external1", External1Box); M("windows.external2", External2Box); M("windows.external3", External3Box);
        M("windows.explorerThumbnails", ExplorerThumbnailsCheck); M("windows.thumbnailMode", ThumbnailModeCombo); M("windows.thumbnailQuality", ThumbnailQualityCombo); M("windows.thumbnailMaxSource", ThumbnailMaxSourceCombo); M("windows.thumbnailPreferEmbedded", ThumbnailPreferEmbeddedCheck);
        M("hotkeys.preset", HotkeyPresetCombo); M("profiles.presets", PresetCombo);
        M("developer.statusGlobalScale", StatusGlobalScaleBox); M("developer.statusMaximizedBoost", StatusMaximizedBoostBox); M("developer.overlayAbsoluteCoordinates", OverlayAbsoluteCoordinatesCheck);
        // Reset Glide card: one tick box per item (reset.card itself is anchored on the button bar, see SettingsWindow.Reset.cs).
        M("reset.allSettings", ResetAllSettingsCheck); M("reset.hotkeysGestures", ResetHotkeysCheck); M("reset.profiles", ResetProfilesCheck); M("reset.windowPlacement", ResetWindowPlacementCheck);
        M("reset.recentHistory", ResetRecentCheck); M("reset.lastSession", ResetSessionCheck); M("reset.caches", ResetCachesCheck); M("reset.codecPack", ResetCodecPackCheck);
        M("reset.registry", ResetRegistryCheck); M("reset.startup", ResetStartupCheck);
        // Advanced gesture matrix editors are built lazily (BuildGestureMatrix) to keep Settings
        // opening fast; that builder registers each slot editor here under "gesture.<slotId>".
        foreach (var slot in GestureCatalog.Slots)
            if (_gestureEditors.TryGetValue(slot.Id, out var gestureEditor)) M("gesture." + slot.Id, gestureEditor);
    }

    private void ApplySettingsTooltips()
    {
        foreach (var definition in SettingsCatalog.All)
        {
            if (_settingControls.TryGetValue(definition.Id, out var control))
            {
                ToolTip.SetTip(control, definition.Description);
                ToolTip.SetShowDelay(control, 650);
            }
        }
        foreach (var nav in NavigationRail.Children.OfType<Button>())
        {
            var key = nav.Tag?.ToString() ?? "";
            ToolTip.SetTip(nav, $"Open {GroupOf(key).Title} settings");
            ToolTip.SetShowDelay(nav, 650);
        }
        ToolTip.SetTip(SearchBox, "Search Glide settings. Use the category filter to search everywhere or limit results to one section.");
        ToolTip.SetShowDelay(SearchBox, 650);
        ToolTip.SetTip(ChangeShortcutButton, "Capture a replacement shortcut for the selected action. Double-click a hotkey row to do the same thing.");
        ToolTip.SetTip(AddShortcutButton, "Add another shortcut to the selected action without replacing existing shortcuts.");
        ToolTip.SetTip(RemoveShortcutButton, "Remove the selected shortcut from this action.");
        ToolTip.SetTip(ResetHotkeysButton, "Restore all keyboard shortcuts to Glide defaults.");
        ToolTip.SetTip(CustomizeTitleBarButton, "Choose, add, remove and reorder any of the 22 title/tab-bar utility buttons.");
    }

    private void ApplyClicked(object? sender, RoutedEventArgs e)
    {
        var next = ReadControls();
        var changes = GetNetChanges(next);
        if (changes.Count == 0)
        {
            UpdateDirtyState(next);
            return;
        }

        // Apply is an explicit commit action; a second confirmation is redundant and interrupts flow.
        CommitState(next);
    }

    private void OkClicked(object? sender, RoutedEventArgs e)
    {
        var next = ReadControls();
        var changes = GetNetChanges(next);
        if (changes.Count > 0) CommitState(next);
        _closeWithoutRollback = true;
        Close();
    }

    private async void CancelClicked(object? sender, RoutedEventArgs e)
    {
        var changes = GetNetChanges();
        if (changes.Count > 0 && _committed.ConfirmDiscardSettingsChanges &&
            !await ConfirmChangesAsync("Discard settings changes?", "These net changes will be discarded:", changes, "Close Anyway", allowDontRemind: true)) return;
        _closeWithoutRollback = true;
        // Close first so the dialog disappears immediately (as it does for OK), then restore the
        // committed state behind it only if a preview was actually applied.
        Close();
        RollbackPreviewDeferred();
    }

    private void RollbackPreviewDeferred()
    {
        if (!_previewDirty) return;
        var committed = _committed.CloneState();
        _previewDirty = false;
        Dispatcher.UIThread.Post(() => _apply(committed, false), DispatcherPriority.Background);
    }

    private void CommitState(GlideSettingsState next)
    {
        if (next.StartWithWindowsInBackground != _committed.StartWithWindowsInBackground)
        {
            WindowsStartupRegistration.SetRunAtStartup(next.StartWithWindowsInBackground, out _);
        }
        _committed = next.CloneState();
        _apply(_committed.CloneState(), true);
        UpdateDirtyState(_committed);
    }

    private async void SettingsClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeWithoutRollback) return;
        var changes = GetNetChanges();
        if (changes.Count == 0)
        {
            _closeWithoutRollback = true;
            RollbackPreviewDeferred();
            return;
        }

        e.Cancel = true;
        if (_committed.ConfirmDiscardSettingsChanges &&
            !await ConfirmChangesAsync("Discard settings changes?", "Settings has unapplied net changes:", changes, "Close Anyway", allowDontRemind: true)) return;
        _closeWithoutRollback = true;
        Close();
        RollbackPreviewDeferred();
    }

    private async Task<bool> ConfirmChangesAsync(string title, string intro, IReadOnlyList<SettingChange> changes, string confirmLabel, bool allowDontRemind = false)
    {
        var list = new StackPanel { Spacing = 6 };
        foreach (var change in changes)
            list.Children.Add(new TextBlock { Text = $"• {change.Name}: {change.Before} → {change.After}", TextWrapping = TextWrapping.Wrap });

        var dontRemind = new CheckBox
        {
            Content = "Don't remind me again",
            IsVisible = allowDontRemind,
            Margin = new Thickness(0, 2, 0, 0)
        };
        var confirm = new Button { Content = confirmLabel, MinWidth = 126 };
        confirm.Classes.Add("primaryAction");
        var back = new Button { Content = "Go Back", MinWidth = 100 };
        back.Classes.Add("secondaryAction");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(back);
        buttons.Children.Add(confirm);
        var root = new StackPanel { Margin = new Thickness(22), Spacing = 14 };
        var warningBrush = new SolidColorBrush(Color.Parse(Glide.App.ThemePalette.Warning));
        var warningGlyph = new Border
        {
            Width = 34, Height = 30, CornerRadius = new CornerRadius(5),
            BorderBrush = warningBrush, BorderThickness = new Thickness(2),
            Background = new SolidColorBrush(Color.Parse(Glide.App.ThemePalette.WarningWash)),
            Child = new TextBlock
            {
                Text = "!", Foreground = warningBrush, FontSize = 19, FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
        var warningRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12 };
        warningRow.Children.Add(warningGlyph);
        var warningText = new TextBlock { Text = intro, FontWeight = FontWeight.SemiBold, FontSize = 15, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(warningText, 1);
        warningRow.Children.Add(warningText);
        root.Children.Add(warningRow);
        root.Children.Add(new ScrollViewer { Content = list, MaxHeight = 330, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        root.Children.Add(dontRemind);
        root.Children.Add(buttons);
        var dialog = new Window
        {
            Title = title, Width = 560, MinWidth = 440, SizeToContent = SizeToContent.Height, MaxHeight = 600,
            CanResize = true, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = root, Background = this.Background,
            Topmost = Topmost
        };
        PopupPlacementStore.Track(dialog, "settings-discard-confirm");
        confirm.Click += (_, _) => dialog.Close(true);
        back.Click += (_, _) => dialog.Close(false);
        var accepted = await dialog.ShowDialog<bool>(this);
        if (accepted && allowDontRemind && dontRemind.IsChecked == true)
        {
            // Persist this one preference even though every other staged change is intentionally discarded.
            _committed.ConfirmDiscardSettingsChanges = false;
            _apply(_committed.CloneState(), true);
        }
        return accepted;
    }

    private void RestoreDefaultsClicked(object? sender, RoutedEventArgs e)
    {
        var defaults = new GlideSettingsState();
        LoadControls(defaults);
        _apply(defaults.CloneState(), false);
        UpdateDirtyState(defaults);
    }

    private void AddOpenWithClicked(object? sender, RoutedEventArgs e)
    {
        _ = WindowsFileAssociationRegistration.Register(out var message);
        WindowsIntegrationStatusText.Text = message;
        DirtyText.Text = message;
        PageSubtitle.Text = message;
    }

    private void RemoveOpenWithClicked(object? sender, RoutedEventArgs e)
    {
        _ = WindowsFileAssociationRegistration.Unregister(out var message);
        WindowsIntegrationStatusText.Text = message;
        DirtyText.Text = message;
        PageSubtitle.Text = message;
    }

    private async void BrowseExternalClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string slot }) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Choose external program {slot}",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Windows programs") { Patterns = new[] { "*.exe" } } }
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;
        if (slot == "1") External1Box.Text = path;
        else if (slot == "2") External2Box.Text = path;
        else External3Box.Text = path;
    }

    private void ApplyPresetClicked(object? sender, RoutedEventArgs e)
    {
        var preset = SelectedText(PresetCombo, "Glide default");
        var staged = SettingsPresetCatalog.Apply(preset, ReadControls());
        LoadControls(staged);
        _apply(staged.CloneState(), false);
        UpdateDirtyState(staged);
        DirtyText.Text = $"Preset staged: {preset}";
    }

    private async void BrowseNewExplorerDefaultDirectoryClicked(object? sender, RoutedEventArgs e) => NewExplorerTabDefaultDirectoryBox.Text = await PickCustomFolderAsync("Choose default New Explorer tab directory") ?? NewExplorerTabDefaultDirectoryBox.Text;
    private async void BrowseOpenDefaultDirectoryClicked(object? sender, RoutedEventArgs e) => OpenFileDefaultDirectoryBox.Text = await PickCustomFolderAsync("Choose default Open File directory") ?? OpenFileDefaultDirectoryBox.Text;
    private async void BrowseOverlayDefaultDirectoryClicked(object? sender, RoutedEventArgs e) => OverlayDefaultDirectoryBox.Text = await PickCustomFolderAsync("Choose default overlay directory") ?? OverlayDefaultDirectoryBox.Text;
    private async void BrowseProfileDefaultDirectoryClicked(object? sender, RoutedEventArgs e) => ProfileDefaultDirectoryBox.Text = await PickCustomFolderAsync("Choose default profile directory") ?? ProfileDefaultDirectoryBox.Text;

    private void PickAccentColorClicked(object? sender, RoutedEventArgs e)
    {
        if (!OperatingSystem.IsWindows() || !WindowsColorDialog.TryChoose(_workingCustomAccentHex, out var hex, TryGetPlatformHandle()?.Handle ?? IntPtr.Zero)) return;
        _workingCustomAccentHex = hex;
        SelectByText(AccentCombo, "Custom");
        PreviewNow();
    }

    private void PickGlowColorClicked(object? sender, RoutedEventArgs e)
    {
        if (!OperatingSystem.IsWindows() || !WindowsColorDialog.TryChoose(_workingCustomGlowHex, out var hex, TryGetPlatformHandle()?.Handle ?? IntPtr.Zero)) return;
        _workingCustomGlowHex = hex;
        SelectByText(GlowCombo, "Custom");
        PreviewNow();
    }

    private void PickBackgroundColorClicked(object? sender, RoutedEventArgs e)
    {
        if (!OperatingSystem.IsWindows() || !WindowsColorDialog.TryChoose(_workingCustomMainBackgroundHex, out var hex, TryGetPlatformHandle()?.Handle ?? IntPtr.Zero)) return;
        _workingCustomMainBackgroundHex = hex;
        SelectByText(MainBackgroundCombo, "Custom");
        PreviewNow();
    }

    private void PreviewNow()
    {
        if (_loadingControls) return;
        var preview = ReadControls();
        _apply(preview, false);
        UpdateDirtyState(preview);
    }

    private async void BrowseStartupFileClicked(object? sender, RoutedEventArgs e) => StartupCustomPathBox.Text = await PickCustomFileAsync("Choose startup image") ?? StartupCustomPathBox.Text;
    private async void BrowseNewTabFileClicked(object? sender, RoutedEventArgs e) => NewTabCustomPathBox.Text = await PickCustomFileAsync("Choose new-tab image") ?? NewTabCustomPathBox.Text;
    private async void BrowseStartupFolderClicked(object? sender, RoutedEventArgs e) => StartupCustomPathBox.Text = await PickCustomFolderAsync("Choose startup folder") ?? StartupCustomPathBox.Text;
    private async void BrowseNewTabFolderClicked(object? sender, RoutedEventArgs e) => NewTabCustomPathBox.Text = await PickCustomFolderAsync("Choose new-tab folder") ?? NewTabCustomPathBox.Text;

    private async Task<string?> PickCustomFileAsync(string title)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Images") { Patterns = Glide.Core.CodecCapabilityRegistry.RoutedExtensions.Select(x => "*" + x).ToArray() },
                FilePickerFileTypes.All
            }
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    private async Task<string?> PickCustomFolderAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    private async void ImportSettingsClicked(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Glide settings",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Glide settings") { Patterns = new[] { "*.json" } } }
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var current = ReadControls();
            var json = await ReadImportFileAsync(path);
            GlideSettingsState? imported = null;
            try
            {
                var document = JsonSerializer.Deserialize(json, AppJsonContext.Default.SettingsTransferDocument);
                if (document is not null && document.Settings is not null)
                {
                    if (document.Schema != SettingsTransferSchema)
                    {
                        DirtyText.Text = $"Unsupported settings schema {document.Schema}";
                        return;
                    }
                    imported = document.Settings;
                }
            }
            catch (JsonException) { }

            // Backward-compatible import for the earlier raw-state JSON format.
            imported ??= JsonSerializer.Deserialize(json, AppJsonContext.Default.GlideSettingsState);
            if (imported is null) return;
            NormalizeImportedState(imported);
            var launchTargetsKept = KeepLocalLaunchTargets(imported, current);
            var networkFoldersDropped = KeepLocalFolderPaths(imported, current);
            LoadControls(imported);
            _apply(imported.CloneState(), false);
            UpdateDirtyState(imported);
            DirtyText.Text = "Imported settings staged" + ImportNotes(launchTargetsKept, networkFoldersDropped);
        }
        catch
        {
            DirtyText.Text = "Import failed";
        }
    }

    private async void ExportSettingsClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var path = Path.Combine(WindowsKnownFolders.Downloads(), $"Glide Settings {DateTime.Now:yyyy-MM-dd_HHmmss}.json");
            var document = new SettingsTransferDocument { Settings = ReadControls().CloneState(), ProviderInventory = CodecProviderRuntime.Shared.Artifacts };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(document, AppJsonContext.Default.SettingsTransferDocument));
            DirtyText.Text = $"Exported to Downloads: {Path.GetFileName(path)}";
        }
        catch
        {
            DirtyText.Text = "Settings export failed";
        }
    }

    private static void NormalizeImportedState(GlideSettingsState state)
    {
        state.Hotkeys ??= HotkeyCatalog.CreateDefaultMap();
        state.CustomHotkeyPresets = HotkeyPresetLibrary.Normalize(state.CustomHotkeyPresets);
        state.Gestures ??= GestureCatalog.CreateDefaultMap();
        // Imports from before 4.8.0 describe mouse behaviour through the folded toggles.
        SettingsStore.MigrateLegacyMouseToggles(state, state.SettingsSchemaVersion);
        SettingsStore.MigrateDefaultsSchema8(state, state.SettingsSchemaVersion);
        state.CustomGestures = GestureCatalog.NormalizeCustomGestures(state.CustomGestures);
        state.GestureRecentActions = SettingsStore.NormalizeRecentGestureActions(state.GestureRecentActions);
        foreach (var key in state.Hotkeys.Keys.ToList())
            state.Hotkeys[key] = state.Hotkeys[key]
                .Where(x => !x.Contains("Mouse", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        foreach (var action in HotkeyCatalog.All)
            if (!state.Hotkeys.ContainsKey(action.Id)) state.Hotkeys[action.Id] = action.DefaultShortcuts.ToList();
        foreach (var slot in GestureCatalog.Slots)
            if (!state.Gestures.ContainsKey(slot.Id)) state.Gestures[slot.Id] = slot.DefaultActionId;
    }

    private Avalonia.Media.IBrush SearchAccentBrush()
    {
        var accent = SelectedText(AccentCombo, "Glide blue") switch
        {
            "Teal" => "#30C7B5",
            "Violet" => "#A98CFF",
            "Amber" => "#E0A54A",
            _ => Glide.App.ThemePalette.GlideBlue
        };
        return new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(accent));
    }

    private Avalonia.Media.IBrush SearchMutedBrush()
    {
        var light = string.Equals(SelectedText(ThemeCombo), Glide.Core.Settings.SettingChoiceNames.Light, StringComparison.OrdinalIgnoreCase);
        return new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(light ? Glide.App.ThemePalette.MutedLight : Glide.App.ThemePalette.MutedDark));
    }

    private static string SelectedText(ComboBox combo, string fallback = "") =>
        (combo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? fallback;

    private static int ParseChoiceNumber(ComboBox combo, int fallback)
    {
        var text = SelectedText(combo);
        var numeric = text.Replace(" Hz", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        return int.TryParse(numeric, out var value) ? value : fallback;
    }

    private static void SelectByText(ComboBox combo, string value)
    {
        var items = combo.Items.OfType<ComboBoxItem>().ToList();
        for (var i = 0; i < items.Count; i++)
        {
            if (string.Equals(items[i].Content?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = items[i];
                return;
            }
        }
        if (items.Count > 0) combo.SelectedItem = items[0];
    }
}
