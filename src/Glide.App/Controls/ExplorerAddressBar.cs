using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Glide.App.Controls;

/// <summary>
/// Explorer-style address bar: a breadcrumb of clickable folder segments; clicking the empty part (or
/// pressing Ctrl+L / F4 / Alt+D elsewhere) switches to an editable path box. Enter requests navigation,
/// Esc cancels, an invalid path is reported inline without leaving the edit box.
/// </summary>
public sealed class ExplorerAddressBar : UserControl
{
    private readonly Border _frame = new();
    private readonly Grid _root = new();
    private readonly ScrollViewer _crumbScroll = new();
    private readonly StackPanel _crumbs = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _crumbHit = new() { Background = Brushes.Transparent };
    private readonly TextBox _edit = new();
    private readonly TextBlock _error = new();
    private string _path = "";
    private bool _editing;

    /// <summary>Raised with a folder path (segment click) or the typed text (Enter in the edit box).</summary>
    public event EventHandler<string>? NavigateRequested;

    public ExplorerAddressBar()
    {
        Focusable = false;
        _frame.CornerRadius = new CornerRadius(5);
        _frame.BorderThickness = new Thickness(1);
        _frame.Height = 31;
        _frame.Bind(Border.BackgroundProperty, this.GetResourceObservable("BrushSearch"));
        _frame.Bind(Border.BorderBrushProperty, this.GetResourceObservable("BrushInputBorder"));

        _crumbScroll.HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden;
        _crumbScroll.VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
        _crumbScroll.Content = _crumbs;
        _crumbHit.Child = _crumbScroll;
        _crumbHit.PointerPressed += CrumbAreaPointerPressed;

        _edit.IsVisible = false;
        _edit.BorderThickness = new Thickness(0);
        _edit.Background = Brushes.Transparent;
        _edit.VerticalContentAlignment = VerticalAlignment.Center;
        _edit.Padding = new Thickness(9, 3);
        _edit.MinHeight = 29;
        _edit.Watermark = "Folder path";
        ToolTip.SetTip(_edit, "Folder path — press Enter to go, Esc to cancel");
        _edit.KeyDown += EditKeyDown;
        _edit.TextChanged += (_, _) => { if (_error.IsVisible) ClearError(); };
        _edit.LostFocus += (_, _) => { if (_editing && !_edit.IsKeyboardFocusWithin) Dispatcher.UIThread.Post(CancelEditIfUnfocused, DispatcherPriority.Background); };

        _error.IsVisible = false;
        _error.Foreground = new SolidColorBrush(Color.Parse("#FF6B6B"));
        _error.FontSize = 12;
        _error.VerticalAlignment = VerticalAlignment.Center;
        _error.Margin = new Thickness(6, 0, 10, 0);
        _error.TextTrimming = TextTrimming.CharacterEllipsis;
        _error.IsHitTestVisible = false;
        _error.HorizontalAlignment = HorizontalAlignment.Right;

        _root.Children.Add(_crumbHit);
        _root.Children.Add(_edit);
        _root.Children.Add(_error);
        _frame.Child = _root;
        Content = _frame;
    }

    public string Path
    {
        get => _path;
        set
        {
            value ??= "";
            if (_path == value && _crumbs.Children.Count > 0) return;
            _path = value;
            RebuildCrumbs();
            if (!_editing) _edit.Text = value;
        }
    }

    public bool IsEditing => _editing;
    public TextBox EditBox => _edit;
    public string? ErrorText => _error.IsVisible ? _error.Text : null;

    /// <summary>The clickable folder buttons, left to right (without the chevrons).</summary>
    public IReadOnlyList<Button> Segments => _crumbs.Children.OfType<Button>().ToList();

    public void BeginEdit()
    {
        if (_editing) { _edit.Focus(); return; }
        _editing = true;
        ClearError();
        _crumbHit.IsVisible = false;
        _edit.IsVisible = true;
        _edit.Text = _path;
        _frame.BorderThickness = new Thickness(1);
        Dispatcher.UIThread.Post(() =>
        {
            if (!_editing) return;
            _edit.Focus();
            _edit.SelectAll();
        }, DispatcherPriority.Input);
        _edit.Focus();
        _edit.SelectAll();
    }

    public void CancelEdit()
    {
        if (!_editing) return;
        _editing = false;
        ClearError();
        _edit.IsVisible = false;
        _crumbHit.IsVisible = true;
        _edit.Text = _path;
    }

    /// <summary>Leaves edit mode after a successful navigation.</summary>
    public void EndEdit() => CancelEdit();

    public void ShowError(string message)
    {
        _error.Text = message;
        _error.IsVisible = true;
        _edit.Foreground = new SolidColorBrush(Color.Parse("#FF6B6B"));
        _frame.BorderBrush = new SolidColorBrush(Color.Parse("#FF6B6B"));
        ToolTip.SetTip(_frame, message);
    }

    private void ClearError()
    {
        _error.IsVisible = false;
        _error.Text = null;
        _edit.ClearValue(TextBox.ForegroundProperty);
        _frame.Bind(Border.BorderBrushProperty, this.GetResourceObservable("BrushInputBorder"));
        ToolTip.SetTip(_frame, null);
    }

    private void CancelEditIfUnfocused()
    {
        if (_editing && !_edit.IsKeyboardFocusWithin && !_edit.IsFocused) CancelEdit();
    }

    private void EditKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CancelEdit();
            e.Handled = true;
            // Hand keyboard focus back to the folder list owner.
            this.FindAncestorOfType<BrowserSurface>()?.ScrollViewer.Focus();
        }
        else if (e.Key is Key.Enter or Key.Return)
        {
            var text = _edit.Text?.Trim() ?? "";
            e.Handled = true;
            if (text.Length > 0) NavigateRequested?.Invoke(this, text);
        }
    }

    private void CrumbAreaPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        // Clicks that land on a segment button are handled by the button itself.
        for (var v = e.Source as Visual; v is not null && !ReferenceEquals(v, _crumbHit); v = v.GetVisualParent())
            if (v is Button) return;
        e.Handled = true;
        BeginEdit();
    }

    private void RebuildCrumbs()
    {
        _crumbs.Children.Clear();
        if (string.IsNullOrWhiteSpace(_path)) return;
        string root;
        try { root = System.IO.Path.GetPathRoot(_path) ?? ""; } catch { root = ""; }
        var segments = new List<(string Text, string Path)>();
        var current = root;
        if (root.Length > 0) segments.Add((root.TrimEnd('\\', '/') is { Length: > 0 } r ? r : root, root));
        var rest = root.Length > 0 && _path.Length >= root.Length ? _path[root.Length..] : _path;
        foreach (var part in rest.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
        {
            current = current.Length == 0 ? part : System.IO.Path.Combine(current, part);
            segments.Add((part, current));
        }
        for (var i = 0; i < segments.Count; i++)
        {
            var (text, path) = segments[i];
            var button = new Button
            {
                Content = new TextBlock { Text = text }, // not a string: AccessText would eat "_" in folder names
                Tag = path,
                Padding = new Thickness(6, 2),
                MinHeight = 24,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                VerticalAlignment = VerticalAlignment.Center,
                Focusable = false,
                FontSize = 13
            };
            button.Classes.Add("crumb");
            ToolTip.SetTip(button, path);
            button.Click += (_, _) => NavigateRequested?.Invoke(this, path);
            _crumbs.Children.Add(button);
            if (i < segments.Count - 1)
            {
                _crumbs.Children.Add(new GlideIconView
                {
                    Kind = "Next", Width = 10, Height = 10, StrokeWidth = 1.6,
                    VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
                    Opacity = 0.7
                });
            }
        }
        Dispatcher.UIThread.Post(() => _crumbScroll.Offset = new Vector(double.MaxValue, 0), DispatcherPriority.Loaded);
    }
}
