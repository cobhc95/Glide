using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Glide.App.Controls;

/// <summary>
/// Lazily constructed Home surface. MainWindow creates this only when a Home tab is actually shown,
/// keeping the welcome/history/status visual tree off explicit image cold-launch paths.
/// </summary>
public partial class HomeSurface : UserControl
{
    public event EventHandler? OpenFileRequested;
    public event EventHandler? ResizeRecentRequested;
    public event EventHandler? ClearRecentRequested;
    public event EventHandler? CloseRecentRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? CollapseStatusRequested;
    public event EventHandler? CloseStatusRequested;

    public HomeSurface() => InitializeComponent();

    public TextBlock HeadingText => HomeHeadingText;
    public TextBlock SubtitleText => HomeSubtitleText;
    public StackPanel EmptyState => HomeEmptyState;
    public TextBlock EmptyText => HomeEmptyText;
    public Border RecentPanel => RecentHistoryPanel;
    public ScrollViewer RecentScroll => RecentHistoryScroll;
    public StackPanel RecentHost => RecentHistoryHost;
    public GlideIconView RecentResize => RecentResizeIcon;
    public Button RecentResizeButtonControl => RecentResizeButton;
    public Border StatusSurface => HomeStatusSurface;
    public TextBlock StatusText => HomeStatusText;
    public StackPanel StatusControlsPanel => HomeStatusControlsPanel;
    public StackPanel StatusWindowControls => HomeStatusWindowControls;
    public bool StatusStacked { get; private set; }

    /// <summary>
    /// Lays the Home status bar out like the viewer's: stacked (info text on its own top line, collapse / close at its right,
    /// buttons on the row below) or on a single line.
    /// </summary>
    public void ApplyStackedStatusLayout(bool stacked)
    {
        StatusStacked = stacked;
        static void Place(Control control, int row, int column, int span)
        {
            Grid.SetRow(control, row);
            Grid.SetColumn(control, column);
            Grid.SetColumnSpan(control, span);
        }
        if (stacked)
        {
            Place(HomeStatusText, 0, 0, 2);
            Place(HomeStatusWindowControls, 0, 2, 1);
            Place(HomeStatusControlsPanel, 1, 0, 3);
        }
        else
        {
            Place(HomeStatusText, 0, 0, 1);
            Place(HomeStatusControlsPanel, 0, 1, 1);
            Place(HomeStatusWindowControls, 0, 2, 1);
        }
        // One row stretches the buttons across the bar's fixed height (the single-line look); stacked uses two content-sized rows.
        HomeStatusLayoutGrid.RowDefinitions = RowDefinitions.Parse(stacked ? "Auto,Auto" : "*");
        HomeStatusLayoutGrid.RowSpacing = stacked ? 4 : 0;
        HomeStatusWindowControls.VerticalAlignment = stacked ? Avalonia.Layout.VerticalAlignment.Top : Avalonia.Layout.VerticalAlignment.Center;
    }
    public Button OptionsButton => HomeOptionsButton;
    public Button StatusCollapseButton => HomeStatusCollapseButton;

    private void OpenFileClicked(object? s, RoutedEventArgs e) => OpenFileRequested?.Invoke(this, EventArgs.Empty);
    private void ResizeRecentHistoryClicked(object? s, RoutedEventArgs e) => ResizeRecentRequested?.Invoke(this, EventArgs.Empty);
    private void ClearRecentHistoryClicked(object? s, RoutedEventArgs e) => ClearRecentRequested?.Invoke(this, EventArgs.Empty);
    private void CloseRecentHistoryClicked(object? s, RoutedEventArgs e) => CloseRecentRequested?.Invoke(this, EventArgs.Empty);
    private void SettingsClicked(object? s, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);
    private void CollapseStatusClicked(object? s, RoutedEventArgs e) => CollapseStatusRequested?.Invoke(this, EventArgs.Empty);
    private void CloseStatusClicked(object? s, RoutedEventArgs e) => CloseStatusRequested?.Invoke(this, EventArgs.Empty);
}
