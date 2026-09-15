using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Glide.App.Controls;

/// <summary>
/// Lazily constructed Home surface. MainWindow creates this only when a Home tab is actually shown,
/// keeping the welcome/history/status visual tree off explicit image cold-launch paths.
/// </summary>
public partial class HomeSurface : UserControl
{
    public event EventHandler? ResetLayoutRequested;
    public event EventHandler? MoveRecentRequested;
    public event EventHandler? ResizeRecentRequested;
    public event EventHandler? ClearRecentRequested;
    public event EventHandler? CloseRecentRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? CollapseStatusRequested;
    public event EventHandler? CloseStatusRequested;

    public HomeSurface() => InitializeComponent();

    public TextBlock HeadingText => HomeHeadingText;
    public TextBlock SubtitleText => HomeSubtitleText;
    public Grid TipsGrid => HomeTipsGrid;
    public Border RecentPanel => RecentHistoryPanel;
    public ScrollViewer RecentScroll => RecentHistoryScroll;
    public StackPanel RecentHost => RecentHistoryHost;
    public GlideIconView RecentMove => RecentMoveIcon;
    public GlideIconView RecentResize => RecentResizeIcon;
    public Button RecentResizeButtonControl => RecentResizeButton;
    public Border StatusSurface => HomeStatusSurface;
    public Button OptionsButton => HomeOptionsButton;
    public Button StatusCollapseButton => HomeStatusCollapseButton;

    private void ResetWelcomeLayoutClicked(object? s, RoutedEventArgs e) => ResetLayoutRequested?.Invoke(this, EventArgs.Empty);
    private void MoveRecentHistoryClicked(object? s, RoutedEventArgs e) => MoveRecentRequested?.Invoke(this, EventArgs.Empty);
    private void ResizeRecentHistoryClicked(object? s, RoutedEventArgs e) => ResizeRecentRequested?.Invoke(this, EventArgs.Empty);
    private void ClearRecentHistoryClicked(object? s, RoutedEventArgs e) => ClearRecentRequested?.Invoke(this, EventArgs.Empty);
    private void CloseRecentHistoryClicked(object? s, RoutedEventArgs e) => CloseRecentRequested?.Invoke(this, EventArgs.Empty);
    private void SettingsClicked(object? s, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);
    private void CollapseStatusClicked(object? s, RoutedEventArgs e) => CollapseStatusRequested?.Invoke(this, EventArgs.Empty);
    private void CloseStatusClicked(object? s, RoutedEventArgs e) => CloseStatusRequested?.Invoke(this, EventArgs.Empty);
}
