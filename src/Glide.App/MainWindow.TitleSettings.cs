using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Glide.App.Controls;
using Glide.App.Settings;
using Glide.Core;
using Glide.App.Services;
using Glide.App.Updates;
using Glide.Core.Commands;
using Glide.Imaging;

namespace Glide.App;

/// <summary>Title strip, title actions, settings apply and explorer theming. Split from MainWindow (4.5.2).</summary>
public partial class MainWindow
{
    private void AlwaysOnTopClicked(object? sender, RoutedEventArgs e)
    {
        if (_wholeAppOverlayMode) _settings.OverlayAlwaysOnTop = !_settings.OverlayAlwaysOnTop;
        else _settings.AlwaysOnTop = !_settings.AlwaysOnTop;
        SaveSettingsAndPublish();
        ApplyTopmostState(forceNative: true);
        if (_wholeAppOverlayControlsHost is { } controls && _wholeAppOverlayMode)
            controls.ContextMenu = BuildWholeAppOverlayOnlyContextMenu();
        RebuildTitleActionStrip();
    }

    /// <summary>
    /// Builds a checkable top-level "Always on top" item for context menus. The tick reflects the
    /// active topmost state for the current mode (ordinary window vs whole-app Overlay mode).
    /// </summary>
    private MenuItem BuildAlwaysOnTopMenuItem()
    {
        var item = MenuActions.Tag(new MenuItem { Header = ActiveAlwaysOnTop ? "Always on top  ✓" : "Always on top", Icon = MenuIcon("AlwaysOnTop") }, "window.alwaysTop");
        item.Click += (_, _) => AlwaysOnTopClicked(null, new RoutedEventArgs());
        return item;
    }

    private void RebuildTitleActionStrip()
    {
        if (TitleActionHost is null) return;
        TitleActionHost.Children.Clear();
        _titleBarButtons.Clear();

        var normalized = TitleBarButtonCatalog.Normalize(_settings.TitleBarButtons);
        _settings.TitleBarButtons = normalized.ToList();
        foreach (var id in normalized)
        {
            if (TitleBarButtonCatalog.Find(id) is not { } definition) continue;
            Control icon;
            if (id == "settings")
            {
                // Use the current Windows Fluent Settings glyph rather than Glide's old hand-drawn
                // radial gear. It stays crisp at mixed DPI and visually matches modern Windows chrome.
                var settingsGlyph = new TextBlock
                {
                    Text = "\uE713",
                    FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                    FontSize = 17,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
                };
                settingsGlyph.Classes.Add("modernTitleSettingsGlyph");
                icon = settingsGlyph;
            }
            else
            {
                var vectorIcon = new GlideIconView
                {
                    Kind = definition.IconKind,
                    Width = 17,
                    Height = 17,
                    StrokeWidth = 1.65
                };
                vectorIcon.Classes.Add("toolbarGlyph");
                icon = vectorIcon;
            }
            var button = new Button
            {
                Width = 36,
                Height = 36,
                Padding = new Thickness(8),
                Tag = id,
                Content = icon,
                ContextMenu = BuildTitleActionContextMenu(id)
            };
            button.Classes.Add("glideIcon");
            if (id is "nav.back" or "nav.forward" or "folder.previous" or "folder.next" or "image.previous" or "image.next" or "image.first" or "image.last")
                button.Classes.Add("nav");
            // Home is a safety-critical title-bar action. Execute it on the consumed client-area
            // pointer press instead of the ordinary Click route: in custom chrome a press/release
            // that escapes the Avalonia button can otherwise fall through to native caption hit
            // testing and be interpreted as a window/caption action. Home must only navigate.
            if (id == "home")
            {
                button.PointerPressed += async (_, e) =>
                {
                    var point = e.GetCurrentPoint(button);
                    if (!point.Properties.IsLeftButtonPressed) return;
                    e.Handled = true;
                    // A Home gesture is never a close gesture. Keep a small cancellation window around
                    // the pointer sequence as a final guard against native/custom-caption races.
                    Volatile.Write(ref _suppressCloseUntilTick, Environment.TickCount64 + 750);
                    _diagnostics.Write("input", "title_home_pressed", new { id });
                    await ExecuteCommandAsync(GlideCommand.Home);
                };
            }
            else if (id is "folder.previous" or "folder.next")
            {
                // Title-bar folder arrows must win before custom-caption hit testing. Register on the
                // tunnel route with handledEventsToo so the vector child/native chrome cannot consume
                // the gesture first. The captured direction is local to this button instance.
                icon.IsHitTestVisible = false;
                var folderDirection = id == "folder.next" ? 1 : -1;
                button.AddHandler(InputElement.PointerPressedEvent, async (_, e) =>
                {
                    var point = e.GetCurrentPoint(button);
                    if (!point.Properties.IsLeftButtonPressed) return;
                    e.Handled = true;
                    Volatile.Write(ref _suppressCloseUntilTick, Environment.TickCount64 + 500);
                    _diagnostics.Write("input", "title_folder_pressed", new { id, folderDirection });
                    await RunSerializedNavigationResultAsync(
                        () => TryNavigateSiblingFolderAsync(folderDirection));
                }, RoutingStrategies.Tunnel, true);
            }
            else
            {
                button.Click += async (_, _) => await ExecuteTitleActionAsync(id, definition.Command);
            }
            TitleActionHost.Children.Add(button);
            _titleBarButtons[id] = button;
        }

        if (_titleBarButtons.TryGetValue("window.alwaysOnTop", out var topmostButton))
            SetActiveClass(topmostButton, ActiveAlwaysOnTop);
        RefreshTitleActionTooltips();
        UpdateHistoryNavigationState(); // after the tooltips: Back / Forward may name a cross-tab target
        // Rebuilding resets every button to visible; reapply the width-based one-at-a-time reveal.
        ApplyCompactChromeLayout();
    }

    private async Task ExecuteTitleActionAsync(string id, GlideCommand command)
    {
        // The title-bar folder arrows intentionally retain their legacy "open first image" contract;
        // status-bar/keyboard folder navigation can continue to use the ordinary sibling-folder policy.
        if (id == "folder.previous") await TryNavigateSiblingFolderAsync(-1);
        else if (id == "folder.next") await TryNavigateSiblingFolderAsync(1);
        else if (command == GlideCommand.PreviousImage || command == GlideCommand.NextImage)
            await NavigateMouseAsync(command == GlideCommand.NextImage ? 1 : -1);
        else await ExecuteCommandAsync(command);
    }

    private void RefreshTitleActionTooltips()
    {
        foreach (var (id, button) in _titleBarButtons)
        {
            if (TitleBarButtonCatalog.Find(id) is not { } definition) continue;
            var label = id == "window.transparency"
                ? $"Window transparency: {(int)Math.Round(_sessionOpacity * 100)}%"
                : TitleActionLabel(id, definition);
            ToolTip.SetTip(button, $"{label} ({ShortcutForTitleButton(definition)})");
            ToolTip.SetShowDelay(button, 650);
        }
    }

    private ContextMenu BuildTitleActionContextMenu(string id)
    {
        var menu = new ContextMenu();
        var items = new List<MenuItem>();
        // tag: a registry action id, or MenuActions.ContextPrefix + why for items acting on this one button.
        MenuItem Item(string header, Action action, string tag, bool enabled = true, string? icon = null)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            if (icon is not null) item.Icon = MenuIcon(icon);
            item.Click += (_, _) => action();
            return tag.StartsWith(MenuActions.ContextPrefix, StringComparison.Ordinal)
                ? MenuActions.Context(item, tag[MenuActions.ContextPrefix.Length..])
                : MenuActions.Tag(item, tag);
        }

        if (id == "window.alwaysOnTop")
        {
            void SetMode(string mode)
            {
                _settings.AlwaysOnTopMode = mode;
                SaveSettingsAndPublish();
                ApplyTopmostState(forceNative: true);
                RebuildTitleActionStrip();
            }
            string Tick(string text, bool on) => on ? $"{text}  ✓" : text;
            items.Add(Item(Tick("Always on top", ActiveAlwaysOnTop), () => AlwaysOnTopClicked(null, new RoutedEventArgs()), "window.alwaysTop", true, "AlwaysOnTop"));
            items.Add(Item(Tick("Hard: stay above every window", HardAlwaysOnTop), () => SetMode("Hard"), "setting.windows.alwaysOnTopMode"));
            items.Add(Item(Tick("Soft: ordinary always on top", !HardAlwaysOnTop), () => SetMode("Soft"), "setting.windows.alwaysOnTopMode"));
            items.Add(new MenuItem { Header = "-" });
        }
        else if (id == "file.locate")
        {
            items.AddRange(BuildFileManagerTargetMenuItems(includeGlideTab: true, tickTarget: true));
            items.Add(new MenuItem { Header = "-" });
        }
        var current = TitleBarButtonCatalog.Normalize(_settings.TitleBarButtons).ToList();
        var index = current.FindIndex(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
        items.Add(Item("Move left", () => MoveTitleAction(id, -1), MenuActions.ContextPrefix + "titlebar.moveThisButton", index > 0, "Previous"));
        items.Add(Item("Move right", () => MoveTitleAction(id, 1), MenuActions.ContextPrefix + "titlebar.moveThisButton", index >= 0 && index < current.Count - 1, "Next"));
        items.Add(Item("Remove from title bar", () => RemoveTitleAction(id), MenuActions.ContextPrefix + "titlebar.removeThisButton", index >= 0, "Close"));
        items.Add(new MenuItem { Header = "-" });
        items.Add(Item("Customize title-bar buttons…", () => _ = ShowTitleBarCustomizerAsync(), "titlebar.customize", true, "Settings"));
        items.Add(new MenuItem { Header = "-" });
        AppendWholeAppOverlayMenuItems(items);
        menu.ItemsSource = items;
        return PrepareOwnedContextMenu(menu);
    }

    private void MoveTitleAction(string id, int delta)
    {
        var ids = TitleBarButtonCatalog.Normalize(_settings.TitleBarButtons).ToList();
        var index = ids.FindIndex(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return;
        var target = Math.Clamp(index + delta, 0, ids.Count - 1);
        if (target == index) return;
        (ids[index], ids[target]) = (ids[target], ids[index]);
        _settings.TitleBarButtons = ids;
        SaveSettingsAndPublish();
        RebuildTitleActionStrip();
    }

    private void RemoveTitleAction(string id)
    {
        _settings.TitleBarButtons = TitleBarButtonCatalog.Normalize(_settings.TitleBarButtons)
            .Where(x => !string.Equals(x, id, StringComparison.OrdinalIgnoreCase)).ToList();
        SaveSettingsAndPublish();
        RebuildTitleActionStrip();
    }

    private async Task ShowTitleBarCustomizerAsync()
    {
        var dialog = new TitleBarCustomizerWindow(_settings.TitleBarButtons)
        {
            Icon = Icon,
            Topmost = Topmost
        };
        var result = await ShowOwnedDialogAsync<IReadOnlyList<string>?>(dialog);
        if (result is null) return;
        _settings.TitleBarButtons = TitleBarButtonCatalog.Normalize(result).ToList();
        SaveSettingsAndPublish();
        RebuildTitleActionStrip();
    }

    private async Task ShowStatusBarCustomizerAsync()
    {
        var dialog = new StatusBarCustomizerWindow(_settings.StatusBarGroups) { Icon = Icon, Topmost = Topmost };
        var result = await ShowOwnedDialogAsync<string?>(dialog);
        if (result is null) return;
        _settings.StatusBarGroups = result;
        SaveSettingsAndPublish();
        ApplyStatusBarSize();
    }

    private async void SettingsClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var window = new SettingsWindow(_settings, ApplySettingsFromDialog)
            {
                // Modal children of an always-on-top viewer must share the same z-order contract.
                // Otherwise Windows can bury Settings/confirmation dialogs behind the owner.
                Topmost = Topmost,
                PlacementChanged = SaveSettingsWindowPlacement,
                // The Updates card shows the live state of the update checker (null in tests, which never start one).
                Updates = UpdateService.Current
            };
            _overlays?.SetModalSuppressed(true);
            _openSettingsWindow = window;
            AttachSettingsOpenedHook(window);
            try
            {
                await ShowOwnedDialogAsync(window);
            }
            finally
            {
                _openSettingsWindow = null;
                _overlays?.SetModalSuppressed(false);
            }
        }
        catch (Exception ex)
        {
            // Settings must never be able to take down the viewer process. The Settings constructor
            // is also guarded against XAML-time event reentrancy, but keep this owner boundary as the
            // final crash containment layer and preserve a diagnostic breadcrumb for the next agent.
            _diagnostics.Write("settings", "open_failed", new { error = ex.ToString() });
        }
    }

    /// <summary>Remembers where the user left the Settings window (size, position, maximized) for its next opening.</summary>
    private void SaveSettingsWindowPlacement(SettingsWindowPlacement placement)
    {
        placement.WriteTo(_settings);
        // The shared per-window store is authoritative (SaveSettingsAndPublish copies it into _settings).
        WindowPlacementService.Remember(SettingsWindowPlacement.StoreKey, placement.ToState(), persist: false);
        SaveSettingsAndPublish();
    }

    private void SaveSettingsAndPublish()
    {
        // The compact first-frame projection is intentionally incomplete. Never serialize that
        // temporary graph over the user's hydrated settings merely because the window closed or a
        // command fired before background hydration completed. If hydration is already available,
        // merge any edits made since the compact baseline; otherwise defer persistence entirely.
        if (!_deferredStartupSettingsAdopted)
        {
            var hydrated = App.TryGetStartupSettings();
            if (hydrated is null)
            {
                _diagnostics.Write("settings", "save_deferred_until_hydrated", new { reason = "startup_settings_not_ready" });
                return;
            }
            _settings = SettingsStore.MergeHydratedPreservingEdits(_startupSettingsBaseline, _settings, hydrated);
            _deferredStartupSettingsAdopted = true;
        }

        // Dialog placements are saved by WindowPlacementService as each window closes; include the live store so this
        // full save never writes back an older copy.
        WindowPlacementService.ApplyTo(_settings);
        // Same for the update checker's memory (last check time...): its live copy is authoritative.
        UpdateService.Current?.ApplyTo(_settings);
        SettingsStore.Save(_settings);
        App.PublishCurrentSettings(_settings);
        _startupSettingsBaseline = _settings.CloneState();
    }

    /// <summary>
    /// Activates the image order chosen in Settings. When it changed, the open folder is re-sorted in
    /// place (the current image stays selected) so next / previous follow the new order immediately.
    /// </summary>
    private void ApplyNavigationSortOrder()
    {
        var wanted = new ImageSortSpec(ImageSortOrder.ParseKey(_settings.NavigationSortOrder), _settings.NavigationSortDescending);
        if (wanted == ImageSortOrder.Current) return;
        ImageSortOrder.Current = wanted;
        _navigator.Resort();
        UpdatePictureCounter();
        UpdateStatusStats();
    }

    private void ApplySettingsFromDialog(GlideSettingsState state, bool commit)
    {
        // a live preview that only changes the text overlay's look (font size spinner, opacity, colour...)
        // restyles the overlay alone instead of re-applying every setting, so dragging a value never lags.
        if (!commit && OnlyTextOverlayStyleDiffers(_settings, state))
        {
            var restyled = state.CloneState();
            SettingsDefaults.KeepRuntimeOwnedValues(restyled, _settings);
            _settings = restyled;
            ApplyPictureCounterVisuals();
            return;
        }
        var previousTabToolTipSize = _settings.TabToolTipTextSize;
        var incoming = state.CloneState();
        // The main window's own geometry, overlay placements and schema version are live values Settings never edits;
        // its snapshot (or a loaded profile) must not replace them (an unset snapshot would forget the window position).
        SettingsDefaults.KeepRuntimeOwnedValues(incoming, _settings);
        _settings = incoming;
        // The Settings window works on a snapshot taken when it opened; the update checker's memory is not part of it.
        UpdateService.Current?.ApplyTo(_settings);
        UpdateService.Current?.SettingsChanged(_settings);
        App.PublishCurrentSettings(_settings);
        ApplyNavigationSortOrder();
        ApplySettingsVisuals();
        UpdateGifStatusUi();
        // changing the tab tooltip size in Settings shows the current tab's tooltip at the new size.
        if (!commit && !string.Equals(previousTabToolTipSize, _settings.TabToolTipTextSize, StringComparison.Ordinal))
        {
            RebuildTabStrip();
            ShowActiveTabToolTipPreview();
        }
        _diagnostics.Write("settings", commit ? "committed" : "previewed", new { theme = _settings.ThemeChoice, accent = _settings.AccentChoice, glow = _settings.GlowChoice, status = _settings.ShowStatusSurface, tabs = _settings.TabsEnabled });
        if (commit)
        {
            SaveSettingsAndPublish();
            App.UpdateTrayIconState();
            if (!_settings.OverlayPersistLayoutBetweenSessions)
            {
                try
                {
                    var path = AutomaticOverlayLayoutPath();
                    if (File.Exists(path)) File.Delete(path);
                }
                catch { /* best-effort cleanup; a locked or missing file is harmless */ }
            }
        }
    }

    internal static bool OnlyTextOverlayStyleDiffers(GlideSettingsState current, GlideSettingsState next)
    {
        var a = current.CloneState();
        var b = next.CloneState();
        if (a.ContentEquals(b)) return false;
        b.PictureCounterTemplate = a.PictureCounterTemplate;
        b.PictureCounterFontSize = a.PictureCounterFontSize;
        b.PictureCounterBold = a.PictureCounterBold;
        b.PictureCounterOpacity = a.PictureCounterOpacity;
        b.PictureCounterPosition = a.PictureCounterPosition;
        b.PictureCounterColor = a.PictureCounterColor;
        b.PictureCounterShadow = a.PictureCounterShadow;
        return a.ContentEquals(b);
    }

    private ImagePerformancePolicy BuildPerformancePolicy()
    {
        var profile = ImagePerformancePolicy.ForProfile(_settings.InitialImageQuality);
        var renderScale = Math.Max(1.0, RenderScaling);
        var viewportWidth = (int)Math.Ceiling(Math.Max(1.0, Viewport.Bounds.Width) * renderScale);
        var viewportHeight = (int)Math.Ceiling(Math.Max(1.0, Viewport.Bounds.Height) * renderScale);
        var previewBounds = _settings.AdaptiveFastPreview
            ? ImagePerformanceGovernor.ChoosePreviewBounds(_settings.RapidPreviewLongestSide, viewportWidth, viewportHeight)
            : (_settings.RapidPreviewLongestSide, _settings.RapidPreviewLongestSide);
        return (profile with
        {
            DecoderScaledFirstFrame = _settings.AdaptiveFastPreview,
            // Keep the user setting as the absolute cap while carrying the actual physical viewport
            // as a 2-D box. A portrait photograph in a wide window must not be decoded to the window
            // width merely because the old policy reduced every viewport to one "longest side" value.
            PreviewLongestSide = _settings.RapidPreviewLongestSide,
            PreviewMaxWidth = previewBounds.Item1,
            PreviewMaxHeight = previewBounds.Item2,
            ProgressiveColorFirstPreview = _settings.ProgressiveColorFirstPreview,
            BackgroundRefinement = _settings.BackgroundRefinement,
            RefinementDelayMs = _settings.AdaptivePreviewDelayMs,
            SequentialForegroundReads = _settings.SequentialForegroundReads,
            PredictivePrefetch = _settings.PredictivePrefetch,
            PrefetchDepth = _settings.PrefetchDepth,
            FullNeighbourPredecodeCount = 1,
            CompressedCacheItems = _settings.CacheItems,
            CompressedCacheMegabytes = _settings.CompressedCacheMegabytes,
            DecodedCacheMegabytes = _settings.DecodedCacheMegabytes
        }).Normalize();
    }

    private ImagePerformancePolicy BuildRapidBrowsePolicy()
    {
        var normal = BuildPerformancePolicy();
        var renderScale = Math.Max(1.0, RenderScaling);
        var viewportWidth = (int)Math.Ceiling(Math.Max(1.0, Viewport.Bounds.Width) * renderScale);
        var viewportHeight = (int)Math.Ceiling(Math.Max(1.0, Viewport.Bounds.Height) * renderScale);
        // Rapid browsing has its own user-visible quality tier. Balanced defaults to 1440 px,
        // deliberately higher than the old hard-coded 960 px frame. The visual performance profile
        // stages 960 / 1440 / 2048 px for Maximum speed / Balanced / Maximum quality, while this
        // setting remains independently editable after the profile is chosen.
        var rapidCap = Math.Clamp(_settings.RapidBrowsePreviewLongestSide, 640, 4096);
        var bounds = ImagePerformanceGovernor.ChoosePreviewBounds(rapidCap, viewportWidth, viewportHeight);
        var interpolation = _settings.InitialImageQuality switch
        {
            "Maximum speed" => BitmapInterpolationMode.LowQuality,
            "Maximum quality" => BitmapInterpolationMode.HighQuality,
            _ => BitmapInterpolationMode.MediumQuality
        };
        return (normal with
        {
            DecoderScaledFirstFrame = true,
            PreviewLongestSide = rapidCap,
            PreviewMaxWidth = Math.Min(rapidCap, bounds.Item1),
            PreviewMaxHeight = Math.Min(rapidCap, bounds.Item2),
            PreviewInterpolation = interpolation,
            BackgroundRefinement = false,
            PredictivePrefetch = true,
            PrefetchDepth = Math.Max(4, Math.Min(8, normal.PrefetchDepth)),
            FullNeighbourPredecodeCount = 0
        }).Normalize();
    }

    private void ApplyEmbeddedExplorerTheme()
    {
        ApplyManagedExplorerTheme();
        if (NativeExplorerHost is null) return;
        var requested = _settings.ExplorerThemeChoice;
        var effective = string.Equals(requested, "Follow Glide theme", StringComparison.OrdinalIgnoreCase)
            ? _settings.ThemeChoice
            : requested;
        NativeExplorerHost.ApplyTheme(effective);
    }

    private SolidColorBrush BrowserBrush(string role)
    {
        var requested = _settings.ExplorerThemeChoice;
        var follow = string.Equals(requested, "Follow Glide theme", StringComparison.OrdinalIgnoreCase);
        var effective = follow ? _settings.ThemeChoice : requested;
        var dark = string.Equals(effective, "Dark", StringComparison.OrdinalIgnoreCase);
        var neutral = string.Equals(effective, "Neutral", StringComparison.OrdinalIgnoreCase);
        var value = role switch
        {
            "surface" => dark ? "#191D22" : neutral ? "#F1F3F5" : "#FFFFFF",
            "border" => dark ? "#343B43" : "#C9D0D6",
            "muted" => dark ? "#A9B3BC" : "#62707A",
            _ => dark ? "#EEF2F6" : "#1C2329"
        };
        return new SolidColorBrush(Color.Parse(value));
    }

    private void ApplyManagedExplorerTheme()
    {
        if (BrowserView is null) return;
        var requested = _settings.ExplorerThemeChoice;
        if (string.Equals(requested, "Follow Glide theme", StringComparison.OrdinalIgnoreCase))
        {
            BrowserView.Resources.Clear();
            return;
        }

        // These resources shadow only the Explorer subtree, so a Light/Neutral browser can coexist
        // with a Dark Glide window and vice versa without fighting the application-level palette.
        var dark = string.Equals(requested, "Dark", StringComparison.OrdinalIgnoreCase);
        var neutral = string.Equals(requested, "Neutral", StringComparison.OrdinalIgnoreCase);
        var viewport = dark ? "#111417" : neutral ? "#E8EBEE" : "#F7F8FA";
        var surface = dark ? "#191D22" : neutral ? "#F1F3F5" : "#FFFFFF";
        var hover = dark ? "#252B31" : neutral ? "#DDE2E6" : "#EEF2F6";
        var text = dark ? "#EEF2F6" : "#1C2329";
        var muted = dark ? "#A9B3BC" : "#62707A";
        var border = dark ? "#343B43" : "#C9D0D6";
        var search = dark ? "#101317" : "#FFFFFF";
        BrowserView.Resources["BrushViewport"] = new SolidColorBrush(Color.Parse(viewport));
        BrowserView.Resources["BrushSurface"] = new SolidColorBrush(Color.Parse(surface));
        BrowserView.Resources["BrushSurfaceHover"] = new SolidColorBrush(Color.Parse(hover));
        BrowserView.Resources["BrushChromeButtonHover"] = new SolidColorBrush(Color.Parse(hover));
        BrowserView.Resources["BrushText"] = new SolidColorBrush(Color.Parse(text));
        BrowserView.Resources["BrushMuted"] = new SolidColorBrush(Color.Parse(muted));
        BrowserView.Resources["BrushBorderSoft"] = new SolidColorBrush(Color.Parse(border));
        BrowserView.Resources["BrushInputBorder"] = new SolidColorBrush(Color.Parse(border));
        BrowserView.Resources["BrushSearch"] = new SolidColorBrush(Color.Parse(search));
    }

    private void ApplySettingsVisuals(bool deferSecondaryChrome = false)
    {
        ThemePaletteApplier.Apply(_settings.ThemeChoice, _settings.AccentChoice, _settings.GlowChoice, _settings.GlowIntensityPercent, _settings.CustomAccentHex, _settings.CustomGlowHex, _settings.MainBackgroundChoice, _settings.CustomMainBackgroundHex);
        ApplyTopmostState(forceNative: true);
        // GIF controls visibility depends on "Show GIF controls only on GIFs": re-evaluate on every settings change
        // (the XAML default is hidden, so this also gives the always-shown default its first layout at startup).
        UpdateGifStatusUi();
        KeyboardNavigation.SetTabNavigation(this, _settings.EnableTabFocusNavigation ? KeyboardNavigationMode.Continue : KeyboardNavigationMode.None);
        // Window.Background is a compositor contract, not the normal theme surface. Keep the
        // top-level transparent for the lifetime of the HWND and paint the ordinary application
        // background on MainRoot instead. This is required for true PNG/WebP alpha in Overlay.
        Background = Brushes.Transparent;
        TransparencyBackgroundFallback = Brushes.Transparent;
        if (_wholeAppOverlayMode)
            MainRoot.Background = Brushes.Transparent;
        else if (Application.Current?.Resources["BrushWindow"] is IBrush normalWindowBrush)
            MainRoot.Background = normalWindowBrush;
        if (_homeSurface is not null)
        {
            ApplyWelcomeLayout();
            RefreshRecentHistoryHome();
        }
        TabStripHost.IsVisible = _settings.TabsEnabled;
        _workspace.ClosedHistoryLimit = Math.Clamp(_settings.ClosedTabHistoryLimit, 1, 100);

        _loader.Policy = BuildPerformancePolicy();
        _lastSurroundingPrefetchFolder = null;
        ApplyEmbeddedExplorerTheme();
        if (_overlays is { } overlays) ApplyOverlaySettingsToManager(overlays);
        Viewport.SetAccentColor(Color.Parse(CurrentAccentHex()));
        Viewport.SetImageQuality(_settings.InitialImageQuality);
        Viewport.SetInteractivePanQuality(_settings.InteractivePanQuality);
        Viewport.GestureBindings = _settings.Gestures;
        Viewport.PointerCenteredZoomEnabled = _settings.PointerZoom;
        Viewport.RightDragMode = _settings.RightDragBehavior switch
        {
            "Pan image only" => RightImageDragMode.PanImageOnly,
            "Move window" => RightImageDragMode.MoveWindow,
            "Do nothing" => RightImageDragMode.Disabled,
            _ => RightImageDragMode.Smart
        };
        Viewport.RightDragWindowMoveAllowed = WindowState == WindowState.Normal;
        Viewport.CustomGestures = _settings.CustomGestures;
        // 4.8.0: middle-drag pan, windowed wheel zoom and Ctrl+wheel zoom are gesture rows now
        // (drag.middleImage, wheel.*); the viewport's built-in defaults equal the old stock toggles.
        Viewport.MiddleDragPanEnabled = false;
        Viewport.WindowedWheelZoom = false;
        Viewport.CtrlWheelZoomEnabled = true;
        Viewport.InvertWheelDirection = _settings.InvertWheelDirection;
        // This gate is specifically for a viewport left-drag becoming a native window move.
        // Empty-background dragging has its own user toggle and consults the same state policy below.
        Viewport.BackgroundWindowDragEnabled = IsLeftWindowMoveAllowedForCurrentState();
        var useOverlayInteractions = _wholeAppOverlayMode && !_settings.WholeAppOverlayUseWindowedInteractions;
        // Selection zoom on/off is the Selection rows ("Ignore here" = old toggle off).
        Viewport.ForceLeftDragMoveWindow = useOverlayInteractions;
        Viewport.SelectionClickZoomEnabled = !useOverlayInteractions;
        Viewport.SelectionRightClickZoomOutEnabled = !useOverlayInteractions;
        Viewport.ReverseSelectionZoomOutScale = _settings.ReverseSelectionZoomOutScale;
        Viewport.ZoomStepPercent = Glide.Core.ZoomStepPolicy.Clamp(_settings.ViewportZoomStepPercent);
        Viewport.PreserveManualZoomOnBitmapChange = _settings.PreserveManualZoomOnNavigate;
        Viewport.LeftDragMode = useOverlayInteractions
            ? LeftImageDragMode.MoveWindow
            : _inputRouter.ResolveGesture("drag.leftImage", _settings.Gestures) switch
            {
                // The left-drag row replaces the old "Left-drag action" choice. The mode still
                // drives the selection-buffer hit test exactly as the old choice did.
                GestureCatalog.Pan => LeftImageDragMode.Pan,
                GestureCatalog.MoveWindow => LeftImageDragMode.MoveWindow,
                _ => LeftImageDragMode.Selection
            };
        Viewport.IsFullscreen = WindowState == WindowState.FullScreen;
        ApplySelectionBuffer();
        // Fullscreen click navigation is the Fullscreen image rows' Default ("Ignore here" = old toggle off).
        Viewport.FullscreenClickNavigationEnabled = true;
        // A configured fullscreen double-click delays single-click navigation by the double-click time.
        Viewport.FullscreenDoubleClickExitEnabled = _inputRouter.ResolveGesture("fullscreen.doubleLeftImage", _settings.Gestures)
            is not (GestureCatalog.Default or GestureCatalog.Nothing or GestureCatalog.PassThrough);
        UpdateSlideshowUi();

        NavigationGroup.IsVisible = _settings.StatusShowNavigation;
        ZoomGroup.IsVisible = _settings.StatusShowZoom;
        SlideshowGroup.IsVisible = _settings.StatusShowSlideshow;
        FitGroup.IsVisible = _settings.StatusShowFit;
        InfoGroup.IsVisible = _settings.StatusShowInfo;
        OverlayGroup.IsVisible = true;
        OptionsGroup.IsVisible = _settings.StatusShowOptions;
        StatusCloseButton.IsVisible = _settings.StatusShowClose;
        UpdateOverlayStatusButtons();
        ApplyStatusBarSize();
        UpdateViewportStatusInset();
        CornerCounterHost.IsVisible = _settings.PictureCounterEnabled && !_wholeAppOverlayMode;
        ApplyPictureCounterVisuals();

        // Keep fullscreen overlay chrome in sync when its auto-hide setting changes live.
        ApplyChromeLayoutForWindowState();

        if (deferSecondaryChrome)
        {
            ApplyStatusVisibility();
            UpdateStatusStats();
            UpdateWindowTitle();
            Dispatcher.UIThread.Post(() =>
            {
                if (_windowLifetimeCts.IsCancellationRequested) return;
                RebuildTitleActionStrip();
                ApplyTabUtilityButtonVisibility();
                if (_titleBarButtons.TryGetValue("window.alwaysOnTop", out var topmostButton)) SetActiveClass(topmostButton, ActiveAlwaysOnTop);
                ApplyStatusVisibility();
                UpdateViewportScrollbars();
                RebuildTabStrip();
                RefreshDynamicTooltips();
                UpdateStatusStats();
                UpdateWindowTitle();
            }, DispatcherPriority.Background);
            return;
        }

        RebuildTitleActionStrip();
        ApplyTabUtilityButtonVisibility();
        if (_titleBarButtons.TryGetValue("window.alwaysOnTop", out var topmostButton)) SetActiveClass(topmostButton, ActiveAlwaysOnTop);
        ApplyStatusVisibility();
        UpdateViewportScrollbars();
        RebuildTabStrip();
        RefreshDynamicTooltips();
        UpdateStatusStats();
        UpdateWindowTitle();
    }

    /// <summary>
    /// Sizes the selection buffer for the current window state: the maximized percentage when
    /// maximized (or when "all modes" is on), the windowed percentage otherwise. Fullscreen keeps its
    /// own click-navigation routing and never uses the buffer.
    /// </summary>
    private void ApplySelectionBuffer()
    {
        var percent = !_settings.SelectionBufferEnabled || WindowState == WindowState.FullScreen
            ? 0
            : _settings.SelectionBufferAllModes || WindowState == WindowState.Maximized
                ? _settings.SelectionBufferMaximizedPercent
                : _settings.SelectionBufferWindowedPercent;
        Viewport.SelectionBufferFraction = Math.Clamp(percent, 0, 100) / 100d;
    }

    private static readonly string[] StatusSizeClasses = { "statusTiny", "statusVerySmall", "statusSmall", "statusMedium", "statusLarge", "statusExtraLarge" };

    private void ApplyStatusBarSize()
    {
        if (ViewerStatusSurface is null || StatusStatsText is null) return;
        var normalized = _settings.StatusBarSize switch
        {
            "Tiny" => "Tiny",
            "Very small" => "Very small",
            "Medium" => "Medium",
            "Large" => "Large",
            "Extra large" => "Extra large",
            _ => "Small"
        };
        _settings.StatusBarSize = normalized;

        var metrics = normalized switch
        {
            "Tiny" => (Height: 34d, MaxWidth: 640d, Icon: 26d, StatsWidth: 140d, Font: 12d),
            "Very small" => (Height: 40d, MaxWidth: 780d, Icon: 32d, StatsWidth: 170d, Font: 12.5d),
            "Small" => (Height: 48d, MaxWidth: 930d, Icon: 38d, StatsWidth: 210d, Font: 13d),
            "Large" => (Height: 68d, MaxWidth: 1240d, Icon: 54d, StatsWidth: 290d, Font: 14d),
            "Extra large" => (Height: 80d, MaxWidth: 1420d, Icon: 64d, StatsWidth: 340d, Font: 15d),
            _ => (Height: 58d, MaxWidth: 1080d, Icon: 46d, StatsWidth: 250d, Font: 13.5d)
        };

        var scale = Math.Clamp(_settings.StatusBarGlobalScalePercent, 60, 160) / 100.0;
        if (WindowState is WindowState.Maximized or WindowState.FullScreen)
            scale *= 1.0 + Math.Clamp(_settings.StatusBarMaximizedBoostPercent, 0, 30) / 100.0;
        scale = Math.Clamp(scale, 0.08, 2.0);

        // Adaptive layout (ArrangeStatusControls): one row when everything fits; otherwise the stats
        // move to a header line and whole icon groups spread over balanced rows; when even that cannot
        // fit, the lowest-priority groups move into the "..." menu. The surface sizes to its content,
        // so one row is exactly one row tall and it only grows when a row is genuinely added.
        var showStats = Bounds.Width >= 260;
        StatusStatsText.IsVisible = showStats;

        var sizeClass = normalized switch
        {
            "Tiny" => "statusTiny",
            "Very small" => "statusVerySmall",
            "Medium" => "statusMedium",
            "Large" => "statusLarge",
            "Extra large" => "statusExtraLarge",
            _ => "statusSmall"
        };
        // Runs on every resize and window-state change: only touch the classes when the size class really
        // changes. Removing and re-adding the same class re-evaluated the styles of the whole status bar each time.
        foreach (var cls in StatusSizeClasses)
            if (cls != sizeClass) ViewerStatusSurface.Classes.Remove(cls);
        if (!ViewerStatusSurface.Classes.Contains(sizeClass)) ViewerStatusSurface.Classes.Add(sizeClass);

        var availableWidth = Math.Max(120d, Bounds.Width - 28d);
        // The bar may use whatever width the window allows; it still hugs its content (it is centred
        // and not stretched), so wide windows keep everything on one row. Tying the width cap to the
        // size ladder made smaller icons get a narrower bar, so they shrank but still wrapped.
        var surfaceWidth = availableWidth;
        var baseIconSize = metrics.Icon * scale;

        // In the single-row layout the stats text may use up to this width before it is trimmed.
        // The always-present GIF group costs width, so the file path gets a larger share.
        var statsCap = Math.Max(metrics.StatsWidth * scale, availableWidth * 0.40);
        StatusStatsText.FontSize = metrics.Font * scale;

        var visibleButtonCount = 0;
        if (_settings.StatusShowZoom) visibleButtonCount += 2;
        if (_settings.StatusShowFit) visibleButtonCount += 3;
        if (_settings.StatusShowInfo) visibleButtonCount += 1;
        if (_settings.StatusShowOptions) visibleButtonCount += 1;
        visibleButtonCount += 2; // Save / Save as
        visibleButtonCount += _overlays?.HasOverlays == true ? 4 : 2; // save/clear only exist with overlays
        if (_settings.StatusShowSlideshow) visibleButtonCount += 1;
        if (_settings.StatusShowNavigation) visibleButtonCount += 4;
        visibleButtonCount += 1; // collapse
        if (_settings.StatusShowClose) visibleButtonCount += 1;

        double NaturalControlsWidth(double icon) => visibleButtonCount * (icon + 3) + 48;

        // Below the wide layout the stats sit on their own header line, so the controls can use the
        // whole bar width.
        var controlsAvailable = Math.Max(baseIconSize * 2 + 8, surfaceWidth - 24);
        var rowsAtConfiguredSize = (int)Math.Ceiling(NaturalControlsWidth(baseIconSize) / Math.Max(1d, controlsAvailable));
        var iconSize = baseIconSize;
        if (_settings.StatusBarAutoFit && rowsAtConfiguredSize > 2)
        {
            // Auto-resize: shrink only the buttons, and only enough to keep the controls within two
            // rows. The configured size stays authoritative and is restored automatically when the
            // window grows again. Never shrink below 60% of the configured button size.
            var targetIcon = (2d * controlsAvailable - 48d) / Math.Max(1, visibleButtonCount) - 3d;
            iconSize = Math.Max(baseIconSize * 0.6, Math.Min(baseIconSize, targetIcon));
        }
        iconSize = Math.Max(20d, iconSize);

        ViewerStatusSurface.MaxWidth = surfaceWidth;
        // Auto height: the Border sizes to its wrapped content, so one line is exactly one line tall
        // and the surface only grows when an extra row is genuinely used. The configured status size
        // remains the minimum height.
        ViewerStatusSurface.MinHeight = metrics.Height * scale;
        ViewerStatusSurface.Height = double.NaN;
        // A group is atomic: it never wraps internally; the arranger moves whole groups instead.
        NavigationGroup.MaxWidth = double.PositiveInfinity;
        OverlayGroup.MaxWidth = double.PositiveInfinity;
        foreach (var button in ViewerStatusSurface.GetVisualDescendants().OfType<Button>().Where(x => x.Classes.Contains("legacyStatusIcon")))
        {
            button.Width = iconSize;
            button.Height = iconSize;
        }
        ArrangeStatusControls(surfaceWidth, showStats, statsCap, iconSize);

        if (_homeSurface is not null)
        {
            // Same two layouts as the viewer's bar: stacked (info text on its own line, content-sized rows, the configured height is the
            // minimum) or the single fixed-height line.
            var homeStacked = _settings.StatusStackInfoLine;
            _homeSurface.ApplyStackedStatusLayout(homeStacked);
            _homeSurface.StatusSurface.Height = homeStacked ? double.NaN : metrics.Height * scale;
            _homeSurface.StatusSurface.MinHeight = homeStacked ? metrics.Height * scale : 0;
            _homeSurface.StatusSurface.MaxWidth = metrics.MaxWidth * scale;
            foreach (var button in _homeSurface.StatusSurface.GetVisualDescendants().OfType<Button>().Where(x => x.Classes.Contains("legacyStatusIcon")))
            {
                button.Width = iconSize;
                button.Height = iconSize;
            }
        }
        ApplyCollapsedStatusChrome();
    }

    /// <summary>Right-click menu / Settings switch for the status bar's Auto-hide mode.</summary>
    private void SetStatusAutoHide(bool enabled)
    {
        _settings.StatusAutoHide = enabled;
        SaveSettingsAndPublish();
        ApplyStatusVisibility();
    }

    /// <summary>
    /// Right-click menu / Settings switch for "Always stack the information on a separate line": re-lays the bar out at once
    /// (the bar becomes taller or shorter, so the reserved fit area and the reveal area are re-measured from the new box).
    /// </summary>
    private void SetStatusStackInfoLine(bool enabled)
    {
        _settings.StatusStackInfoLine = enabled;
        SaveSettingsAndPublish();
        ApplyStatusBarSize();
        UpdateViewportStatusInset();
    }

    private void StatusSurfacePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(sender as Visual ?? ViewerStatusSurface).Properties.IsRightButtonPressed) return;
        // This handler tunnels before the button sees the press: right-click on the minimise/auto-hide button
        // toggles auto-hide on release, so the bar's menu must not open over it.
        if (IsStatusCollapseButtonSource(e.Source)) return;
        e.Handled = true;
        var owned = BuildStatusSurfaceContextMenu();
        // The menu is a popup window: the pointer leaves the bar while it is open, but an auto-hidden bar must stay put.
        _statusMenuOpen = true;
        owned.Closed += (_, _) => { _statusMenuOpen = false; UpdateStatusHoverReveal(_lastWindowPointer); };
        owned.Open(sender as Control ?? ViewerStatusSurface);
    }

    /// <summary>True when <paramref name="source"/> is (inside) a status bar minimise/auto-hide button.</summary>
    internal bool IsStatusCollapseButtonSource(object? source)
    {
        for (var v = source as Visual; v is not null; v = v.GetVisualParent())
        {
            if (ReferenceEquals(v, StatusCollapseButton) || (_homeSurface is not null && ReferenceEquals(v, _homeSurface.StatusCollapseButton))) return true;
        }
        return false;
    }

    /// <summary>The status bar's right-click menu (also used by tests).</summary>
    internal ContextMenu BuildStatusSurfaceContextMenu()
    {
        var menu = new ContextMenu();
        var items = new List<MenuItem>();
        if (WindowState == WindowState.FullScreen)
        {
            var pin = MenuActions.Tag(new MenuItem
            {
                Header = _settings.FullscreenStatusAlwaysOn
                    ? "Keep status bar visible in fullscreen  ✓"
                    : "Keep status bar visible in fullscreen"
            }, "setting.fullscreen.statusAlways");
            pin.Click += (_, _) =>
            {
                _settings.FullscreenStatusAlwaysOn = !_settings.FullscreenStatusAlwaysOn;
                SaveSettingsAndPublish();
                ApplyStatusVisibility();
            };
            pin.Icon = MenuIcon("Fullscreen");
            items.Add(pin);
            items.Add(new MenuItem { Header = "-" });
        }
        var gifOnly = MenuActions.Tag(new MenuItem { Header = _settings.StatusGifControlsOnlyOnGifs ? "Show GIF controls only on GIFs  ✓" : "Show GIF controls only on GIFs", Icon = MenuIcon("Play") }, "setting.status.gifOnlyOnGifs");
        gifOnly.Click += (_, _) =>
        {
            _settings.StatusGifControlsOnlyOnGifs = !_settings.StatusGifControlsOnlyOnGifs;
            SaveSettingsAndPublish();
            UpdateGifStatusUi();
            ApplyStatusBarSize();
        };
        items.Add(gifOnly);
        var customize = MenuActions.Tag(new MenuItem { Header = MenuActions.Header("Customize status-bar buttons…", ShortcutForAction("statusbar.customize")), Icon = MenuIcon("Settings") }, "statusbar.customize");
        customize.Click += async (_, _) => await ShowStatusBarCustomizerAsync();
        items.Add(customize);
        var autoHideItem = MenuActions.Tag(new MenuItem { Header = _settings.StatusAutoHide ? "Auto-hide status bar  ✓" : "Auto-hide status bar", Icon = MenuIcon("Eye") }, "setting.status.autoHide");
        autoHideItem.Click += (_, _) => SetStatusAutoHide(!_settings.StatusAutoHide);
        items.Add(autoHideItem);
        // The hover reveal (status.hoverWhenClosed) and its reveal-area adjustment live in Settings > Tabs & status bar only.
        var reserveFit = MenuActions.Tag(new MenuItem { Header = _settings.StatusReserveFitSpace ? "Reserve status bar space when fitting  ✓" : "Reserve status bar space when fitting", Icon = MenuIcon("FitWindow") }, "setting.status.reserveFitSpace");
        reserveFit.Click += (_, _) => { _settings.StatusReserveFitSpace = !_settings.StatusReserveFitSpace; SaveSettingsAndPublish(); UpdateViewportStatusInset(); };
        items.Add(reserveFit);
        var autoResize = MenuActions.Tag(new MenuItem { Header = _settings.StatusBarAutoFit ? "Auto-resize  ✓" : "Auto-resize", Icon = MenuIcon("Fit") }, "setting.status.autoResize");
        autoResize.Click += (_, _) =>
        {
            _settings.StatusBarAutoFit = !_settings.StatusBarAutoFit;
            SaveSettingsAndPublish();
            ApplyStatusBarSize();
        };
        items.Add(autoResize);
        const string stackHeader = "Always stack the information on a separate line";
        var stackInfo = MenuActions.Tag(new MenuItem { Header = _settings.StatusStackInfoLine ? stackHeader + "  ✓" : stackHeader, Icon = MenuIcon("StackLines") }, "setting.status.stackInfoLine");
        stackInfo.Click += (_, _) => SetStatusStackInfoLine(!_settings.StatusStackInfoLine);
        items.Add(stackInfo);
        items.Add(new MenuItem { Header = "-" });
        // The six sizes live in one submenu instead of six top-level entries.
        var sizeMenu = new MenuItem { Header = $"Size  ({_settings.StatusBarSize})", Icon = MenuIcon("Maximize") };
        foreach (var size in new[] { "Tiny", "Very small", "Small", "Medium", "Large", "Extra large" })
        {
            var item = MenuActions.Tag(new MenuItem { Header = string.Equals(_settings.StatusBarSize, size, StringComparison.OrdinalIgnoreCase) ? $"{size}  ✓" : size }, "setting.status.size");
            item.Click += (_, _) =>
            {
                _settings.StatusBarSize = size;
                ApplyStatusBarSize();
                SaveSettingsAndPublish();
                UpdateStatusStats();
            };
            sizeMenu.Items.Add(item);
        }
        items.Add(sizeMenu);
        var positionMenu = new MenuItem { Header = $"Position  ({_settings.StatusBarAlignment})", Icon = MenuIcon("Fit") };
        foreach (var position in new[] { "Center", "Left", "Right" })
        {
            var item = MenuActions.Tag(new MenuItem { Header = string.Equals(_settings.StatusBarAlignment, position, StringComparison.OrdinalIgnoreCase) ? $"{position}  ✓" : position }, "setting.status.alignment");
            item.Click += (_, _) =>
            {
                _settings.StatusBarAlignment = position;
                SaveSettingsAndPublish();
                ApplyStatusVisibility();
            };
            positionMenu.Items.Add(item);
        }
        items.Add(positionMenu);
        var cornerItem = MenuActions.Tag(new MenuItem { Header = _settings.StatusCollapsedToCorner ? "Collapsed button in lower-right corner  ✓" : "Collapsed button in lower-right corner", Icon = MenuIcon("Collapse") }, "setting.status.collapsedCorner");
        cornerItem.Click += (_, _) => { _settings.StatusCollapsedToCorner = !_settings.StatusCollapsedToCorner; SaveSettingsAndPublish(); ApplyStatusVisibility(); };
        items.Add(cornerItem);
        menu.ItemsSource = items;
        return PrepareOwnedContextMenu(menu);
    }
}
