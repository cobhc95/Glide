using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;

namespace Glide.App.Controls;

/// <summary>
/// Lazily constructed Explorer surface. It is created only when an Explorer tab is activated.
/// </summary>
public partial class BrowserSurface : UserControl
{
    public event EventHandler? BackRequested;
    public event EventHandler? ForwardRequested;
    public event EventHandler? UpRequested;
    public event EventHandler? RefreshRequested;
    public event EventHandler<KeyEventArgs>? AddressKeyPressed;
    public event EventHandler<SelectionChangedEventArgs>? ViewModeChanged;
    public event EventHandler? WindowsExplorerRequested;
    public event EventHandler<KeyEventArgs>? BrowserKeyPressed;

    public BrowserSurface() => InitializeComponent();

    public Button BackButton => BrowserBackButton;
    public Button ForwardButton => BrowserForwardButton;
    public Button UpButton => BrowserUpButton;
    public Button RefreshButton => BrowserRefreshButton;
    public TextBox AddressBox => BrowserAddressBox;
    public ComboBox ViewModeCombo => BrowserViewModeCombo;
    public Button WindowsExplorerButton => BrowserWindowsExplorerButton;
    public Grid NativeHostSurface => NativeExplorerHostSurface;
    public ScrollViewer ScrollViewer => BrowserScrollViewer;
    public ItemsRepeater Repeater => BrowserRepeater;
    public UniformGridLayout RepeaterLayout => BrowserRepeater.Layout as UniformGridLayout ?? throw new InvalidOperationException("BrowserRepeater.Layout is not UniformGridLayout.");

    private void BrowserBackClicked(object? s, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);
    private void BrowserForwardClicked(object? s, RoutedEventArgs e) => ForwardRequested?.Invoke(this, EventArgs.Empty);
    private void BrowserUpClicked(object? s, RoutedEventArgs e) => UpRequested?.Invoke(this, EventArgs.Empty);
    private void BrowserRefreshClicked(object? s, RoutedEventArgs e) => RefreshRequested?.Invoke(this, EventArgs.Empty);
    private void BrowserAddressKeyDown(object? s, KeyEventArgs e) => AddressKeyPressed?.Invoke(this, e);
    private void BrowserViewModeChanged(object? s, SelectionChangedEventArgs e) => ViewModeChanged?.Invoke(this, e);
    private void BrowserWindowsExplorerClicked(object? s, RoutedEventArgs e) => WindowsExplorerRequested?.Invoke(this, EventArgs.Empty);
    private void BrowserListKeyDown(object? s, KeyEventArgs e) => BrowserKeyPressed?.Invoke(this, e);
}
