using System.Diagnostics;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Glide.App.Settings;
using Glide.Core.Navigation;

namespace Glide.App;

/// <summary>
/// 5.0.6 navigation skip hint "Skipping several pictures at once?": when <see cref="NavigationSkipDetector"/> sees a sticky
/// or bouncing next / previous key (one physical press accepted as several keyboard navigations), a small toast above the
/// status bar offers "Adjust navigation speed →" (opens Settings at Navigation minimum interval, navigation.rateLimitMs,
/// through the same reveal path as the other Settings links) and "Don't remind me again" (turns the navigation.skipHint
/// setting off). It shows whenever the detector fires while the setting is on, at most once every
/// <see cref="NavigationSkipHintCooldownMs"/>; it never takes focus and fades after <see cref="NavigationSkipHintVisibleMs"/>.
/// </summary>
public partial class MainWindow
{
    internal const string NavigationSkipHintSettingId = "navigation.rateLimitMs";
    internal const int NavigationSkipHintVisibleMs = 6000;
    internal const double NavigationSkipHintCooldownMs = 5 * 60 * 1000;
    private const int NavigationSkipHintFadeSteps = 10;
    private const int NavigationSkipHintFadeStepMs = 35;

    private readonly NavigationSkipDetector _navigationSkipDetector = new();
    private CancellationTokenSource? _navigationSkipHintCts;
    // Detector time (ms) of the last toast; null until one has shown in this session. Drives the 5-minute cooldown.
    private double? _navigationSkipHintLastShownMs;
    // Keyboard next / previous navigations accepted by the navigation minimum interval (NavigateKeyboardAsync). The key
    // handler compares it before / after a key-down to learn whether that key-down navigated. Glide's own held-key
    // playback steps do not go through NavigateKeyboardAsync, so they never count.
    private int _acceptedKeyboardNavigations;

    private static double NavigationSkipClockMs() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

    /// <summary>Whether the toast may show at <paramref name="nowMs"/>: the setting is on and the cooldown since the last toast is over.</summary>
    internal static bool ShouldShowNavigationSkipHint(GlideSettingsState settings, double? lastShownMs, double nowMs) =>
        settings.NavigationSkipHint &&
        (lastShownMs is not { } last || nowMs - last >= NavigationSkipHintCooldownMs);

    /// <summary>A keyboard next / previous key-down; <paramref name="acceptedBefore"/> is the counter read before it navigated.</summary>
    private void NoteNavigationKeyDown(Key key, int acceptedBefore) =>
        NoteNavigationKeyDown((int)key, NavigationSkipClockMs(), _acceptedKeyboardNavigations != acceptedBefore);

    /// <summary>The detector feed with an explicit time (tests drive it with synthetic times).</summary>
    internal void NoteNavigationKeyDown(int key, double timeMs, bool navigated)
    {
        if (_navigationSkipDetector.OnKeyDown(key, timeMs, navigated))
            OnNavigationSkipDetected("bounce", timeMs);
    }

    private void NoteNavigationKeyUp(Key key) => NoteNavigationKeyUp((int)key, NavigationSkipClockMs());

    internal void NoteNavigationKeyUp(int key, double timeMs)
    {
        if (_navigationSkipDetector.OnKeyUp(key, timeMs))
            OnNavigationSkipDetected("fast_press", timeMs);
    }

    private void NoteWheelNavigationForSkipDetector() => _navigationSkipDetector.OnWheelNavigation(NavigationSkipClockMs());

    private void OnNavigationSkipDetected(string rule, double timeMs)
    {
        var show = ShouldShowNavigationSkipHint(_settings, _navigationSkipHintLastShownMs, timeMs);
        _diagnostics.Write("navigation", "skip_detected", new { rule, show, intervalMs = NavigationOptionsView.RateLimitMs, enabled = NavigationOptionsView.SkipHint });
        if (!show) return;
        _navigationSkipHintLastShownMs = timeMs;
        ShowNavigationSkipHint();
    }

    private void ShowNavigationSkipHint()
    {
        _navigationSkipHintCts?.Cancel();
        _navigationSkipHintCts?.Dispose();
        var cts = _navigationSkipHintCts = new CancellationTokenSource();
        // Just above the status bar when it is shown; near the bottom edge otherwise.
        var status = ViewerStatusSurface;
        var bottom = status.IsVisible && status.Bounds.Height > 0 ? status.Margin.Bottom + status.Bounds.Height + 10 : 24;
        NavigationSkipHint.Margin = new Thickness(16, 0, 16, bottom);
        NavigationSkipHint.IsVisible = true;
        NavigationSkipHint.Opacity = 1;
        _ = FadeNavigationSkipHintAsync(cts.Token);
    }

    private async Task FadeNavigationSkipHintAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(NavigationSkipHintVisibleMs, token).ConfigureAwait(true);
            // A short stepped fade (Glide uses no Avalonia transitions), then gone.
            for (var step = NavigationSkipHintFadeSteps - 1; step >= 0; step--)
            {
                NavigationSkipHint.Opacity = step / (double)NavigationSkipHintFadeSteps;
                await Task.Delay(NavigationSkipHintFadeStepMs, token).ConfigureAwait(true);
            }
            NavigationSkipHint.IsVisible = false;
        }
        catch (OperationCanceledException) { /* closed, clicked or shown again */ }
    }

    private void HideNavigationSkipHint()
    {
        _navigationSkipHintCts?.Cancel();
        _navigationSkipHintCts?.Dispose();
        _navigationSkipHintCts = null;
        NavigationSkipHint.Opacity = 0;
        NavigationSkipHint.IsVisible = false;
    }

    private void NavigationSkipHintLinkClicked(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        HideNavigationSkipHint();
        OpenSettingsAtSetting(NavigationSkipHintSettingId);
    }

    /// <summary>"Don't remind me again": turns navigation.skipHint off, saved like a Settings change.</summary>
    private void NavigationSkipHintDontRemindClicked(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        HideNavigationSkipHint();
        _settings.NavigationSkipHint = false;
        _diagnostics.Write("settings", "changed", new { id = "navigation.skipHint", value = false, source = "skip_hint_toast" });
        SaveSettingsAndPublish();
    }

    private void NavigationSkipHintCloseClicked(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        HideNavigationSkipHint();
    }
}
