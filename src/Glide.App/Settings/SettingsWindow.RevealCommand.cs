using Avalonia.Threading;

namespace Glide.App.Settings;

public partial class SettingsWindow
{
    /// <summary>
    /// Another window asked for a setting (e.g. "Show in other file manager… Set up"): reveal it the same way
    /// Settings search's Open does, once the window has laid out.
    /// </summary>
    internal void RevealSettingFromCommand(string settingId) =>
        Dispatcher.UIThread.Post(() => RevealSetting(settingId), DispatcherPriority.Loaded);

    /// <summary>
    /// Mouse &amp; keyboard's "Explorer scroll speed and amount →" link: opens Window &amp; navigation > Explorer tab, expands the
    /// card, scrolls to the mouse-wheel settings and highlights them (the whole group: see RevealSetting / RowInCard).
    /// </summary>
    private void ExplorerScrollLinkClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => RevealSetting("explorer.scrollMode");

    /// <summary>
    /// The Explorer tab card's "Opening pictures in new tabs →" link: opens Tabs &amp; status bar > Tabs, expands the card,
    /// scrolls to the new-tab settings and highlights them (the whole NewTabSettingsGroup: New tabs open, Open new tabs
    /// in the, and their Key to open … instead rows).
    /// </summary>
    /// <summary>The "Tooltip delay →" links (Explorer tab, Settings window, Overlay mode, Fullscreen cards): open Appearance &amp;
    /// viewing > Tooltips and highlight that area's delay.</summary>
    private void TooltipDelayLinkClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => RevealSetting(
        ReferenceEquals(sender, ExplorerTooltipLinkButton) ? "appearance.tooltipDelayExplorer"
        : ReferenceEquals(sender, SettingsTooltipLinkButton) ? "appearance.tooltipDelaySettings"
        : ReferenceEquals(sender, OverlayTooltipLinkButton) ? "appearance.tooltipDelayOverlay"
        : "appearance.tooltipDelayMain");

    private void NewTabsLinkClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => RevealSetting("tabs.switchToNewTabs");
}
