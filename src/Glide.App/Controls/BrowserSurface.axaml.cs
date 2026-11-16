using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Glide.App.Controls;

/// <summary>
/// Lazily constructed Explorer surface. It is created only when an Explorer tab is activated.
/// All behaviour (navigation, selection, menus, file operations) lives in MainWindow's Explorer
/// partials; this control only owns the visuals and raises the toolbar events.
/// </summary>
public partial class BrowserSurface : UserControl
{
    public event EventHandler? BackRequested;
    public event EventHandler? ForwardRequested;
    public event EventHandler? HistoryRequested;
    public event EventHandler? UpRequested;
    public event EventHandler? RefreshRequested;
    public event EventHandler<string>? AddressNavigateRequested;
    public event EventHandler<SelectionChangedEventArgs>? ViewModeChanged;
    public event EventHandler? WindowsExplorerRequested;
    public event EventHandler<TextChangedEventArgs>? FilterTextChanged;
    public event EventHandler<KeyEventArgs>? FilterKeyPressed;

    public BrowserSurface()
    {
        InitializeComponent();
    }

    public Button BackButton => BrowserBackButton;
    public Button ForwardButton => BrowserForwardButton;
    public Button HistoryButton => BrowserHistoryButton;
    public Button UpButton => BrowserUpButton;
    public Button RefreshButton => BrowserRefreshButton;
    public Button WindowsExplorerButton => BrowserWindowsExplorerButton;
    public ExplorerAddressBar AddressBar => BrowserAddressBar;
    public TextBox FilterBox => BrowserFilterBox;
    public ComboBox ViewModeCombo => BrowserViewModeCombo;
    public Grid NativeHostSurface => NativeExplorerHostSurface;
    public ScrollViewer ScrollViewer => BrowserScrollViewer;
    public ItemsRepeater Repeater => BrowserRepeater;
    public StackPanel NavHost => BrowserNavHost;
    public Border DetailsHeader => BrowserDetailsHeader;
    public Canvas RubberLayer => BrowserRubberLayer;
    public Border RubberRect => BrowserRubberRect;
    public TextBlock EmptyText => BrowserEmptyText;
    public TextBlock ItemCountText => BrowserItemCountText;
    public TextBlock SelectionText => BrowserSelectionText;
    public TextBlock MessageText => BrowserMessageText;

    private void BrowserBackClicked(object? s, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);
    private void BrowserForwardClicked(object? s, RoutedEventArgs e) => ForwardRequested?.Invoke(this, EventArgs.Empty);
    private void BrowserHistoryClicked(object? s, RoutedEventArgs e) => HistoryRequested?.Invoke(this, EventArgs.Empty);
    private void BrowserUpClicked(object? s, RoutedEventArgs e) => UpRequested?.Invoke(this, EventArgs.Empty);
    private void BrowserRefreshClicked(object? s, RoutedEventArgs e) => RefreshRequested?.Invoke(this, EventArgs.Empty);
    private void BrowserAddressNavigateRequested(object? s, string path) => AddressNavigateRequested?.Invoke(this, path);
    private void BrowserViewModeChanged(object? s, SelectionChangedEventArgs e) => ViewModeChanged?.Invoke(this, e);
    private void BrowserWindowsExplorerClicked(object? s, RoutedEventArgs e) => WindowsExplorerRequested?.Invoke(this, EventArgs.Empty);
    private void BrowserFilterTextChanged(object? s, TextChangedEventArgs e) => FilterTextChanged?.Invoke(this, e);
    private void BrowserFilterKeyDown(object? s, KeyEventArgs e) => FilterKeyPressed?.Invoke(this, e);
}
