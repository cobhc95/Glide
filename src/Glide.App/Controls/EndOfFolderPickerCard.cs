using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Glide.Core.Navigation;

namespace Glide.App.Controls;

/// <summary>
/// The in-viewer "Ask me each time" card (5.0.7, approved mockup docs/mockups/end-of-folder-action-5.0.7.md): a small
/// dark card centred over the picture with a row per end-of-folder choice, the highlighted row in the accent, and a
/// "Remember this choice" footer. It only draws <see cref="EndOfFolderPicker"/>; the key handling lives in that model and
/// the window decides what a pick does.
/// </summary>
public sealed class EndOfFolderPickerCard : Border
{
    private readonly EndOfFolderPicker _picker;
    private readonly List<Border> _rows = new();
    private readonly CheckBox _remember;

    /// <summary>Raised once when a row is picked (click, key) or the card is dismissed.</summary>
    public event Action<EndOfFolderAction, bool>? Picked;

    /// <param name="folderName">The folder that ended.</param>
    /// <param name="position">e.g. "142 of 142".</param>
    /// <param name="nextFolderName">The folder in the direction of travel, or null when there is none.</param>
    public EndOfFolderPickerCard(EndOfFolderPicker picker, string folderName, string position, string? nextFolderName)
    {
        _picker = picker;
        Width = 340;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        CornerRadius = new CornerRadius(10);
        Padding = new Thickness(14, 12, 14, 12);
        Background = new SolidColorBrush(Color.Parse("#F21B1F25"));
        BorderBrush = new SolidColorBrush(Color.Parse("#33FFFFFF"));
        BorderThickness = new Thickness(1);
        BoxShadow = BoxShadows.Parse("0 8 28 0 #99000000");

        var forward = picker.TravelDirection > 0;
        var stack = new StackPanel { Spacing = 3 };
        stack.Children.Add(new TextBlock
        {
            Text = $"{(forward ? "End" : "Start")} of “{folderName}”", FontSize = 15, FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis
        });
        var direction = forward ? "next folder" : "previous folder";
        stack.Children.Add(new TextBlock
        {
            Text = nextFolderName is null ? $"{position} · no {direction}" : $"{position} · {direction}: “{nextFolderName}”",
            FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#A0FFFFFF")), TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 0, 8)
        });

        for (var i = 0; i < picker.Rows.Count; i++)
        {
            var row = BuildRow(picker.Rows[i], i);
            _rows.Add(row);
            stack.Children.Add(row);
        }

        _remember = new CheckBox { Content = "Remember this choice", FontSize = 12.5, Foreground = Brushes.White, Focusable = false };
        ToolTip.SetTip(_remember, "Saves the choice as “When the last image is reached” in Settings.");
        var footer = new DockPanel { Margin = new Thickness(0, 8, 0, 0), LastChildFill = false };
        DockPanel.SetDock(_remember, Dock.Left);
        footer.Children.Add(_remember);
        var hint = new TextBlock
        {
            Text = $"↑↓ Enter · {(forward ? "→" : "←")} repeats", FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.Parse("#80FFFFFF")), VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(hint, Dock.Right);
        footer.Children.Add(hint);
        stack.Children.Add(footer);
        Child = stack;
        // A click on the card itself (not a row) must not reach the host, where it would mean "clicked outside".
        PointerPressed += (_, e) => e.Handled = true;
        Refresh();
    }

    public EndOfFolderPicker Picker => _picker;

    public bool RememberChoice
    {
        get => _remember.IsChecked == true;
        set => _remember.IsChecked = value;
    }

    /// <summary>Feeds a key; true when the picker used it. Fires <see cref="Picked"/> once a choice is made.</summary>
    public bool HandleKey(EndOfFolderPickerKey key, int digit = 0)
    {
        if (!_picker.HandleKey(key, digit)) return false;
        Refresh();
        RaiseIfDone();
        return true;
    }

    /// <summary>A click outside the card: Stay here.</summary>
    public void Dismiss()
    {
        _picker.Dismiss();
        RaiseIfDone();
    }

    /// <summary>
    /// The picker's own keys: arrows, Enter, Esc and 1-5 (top row or numeric keypad). Null for every other key, which
    /// the window then checks against the navigation hotkey that reached the end.
    /// </summary>
    public static (EndOfFolderPickerKey Key, int Digit)? Translate(Key key, KeyModifiers modifiers)
    {
        if ((modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0) return null;
        return key switch
        {
            Key.Up => (EndOfFolderPickerKey.Up, 0),
            Key.Down => (EndOfFolderPickerKey.Down, 0),
            Key.Enter => (EndOfFolderPickerKey.Enter, 0),
            Key.Escape => (EndOfFolderPickerKey.Escape, 0),
            >= Key.D1 and <= Key.D9 => (EndOfFolderPickerKey.Digit, key - Key.D0),
            >= Key.NumPad1 and <= Key.NumPad9 => (EndOfFolderPickerKey.Digit, key - Key.NumPad0),
            _ => null
        };
    }

    private bool _raised;

    private void RaiseIfDone()
    {
        if (_raised || _picker.Result is not { } result) return;
        _raised = true;
        Picked?.Invoke(result, RememberChoice);
    }

    private Border BuildRow(EndOfFolderPickerRow model, int index)
    {
        var icon = new GlideIconView { Kind = model.Icon, Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center };
        icon.IconBrush = new SolidColorBrush(Color.Parse("#E6FFFFFF"));
        var label = new TextBlock { Text = model.Label, FontSize = 13.5, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        var key = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#1FFFFFFF")), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = model.KeyHint, FontSize = 11.5, Foreground = new SolidColorBrush(Color.Parse("#B0FFFFFF")) }
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(icon);
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        Grid.SetColumn(key, 2);
        grid.Children.Add(key);

        var tint = new Border { CornerRadius = new CornerRadius(6), Opacity = 0.2, IsVisible = false };
        var row = new Border
        {
            CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), BorderBrush = Brushes.Transparent,
            Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand),
            Child = new Panel { Children = { tint, new Border { Padding = new Thickness(9, 6), Child = grid } } }
        };
        row.PointerEntered += (_, _) => { _picker.Highlight(index); Refresh(); };
        row.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            _picker.Pick(index);
            Refresh();
            RaiseIfDone();
        };
        return row;
    }

    /// <summary>The theme accent (Glide blue until the card is in a window that defines BrushAccent).</summary>
    private IBrush Accent =>
        this.TryFindResource("BrushAccent", ActualThemeVariant, out var resource) && resource is IBrush brush
            ? brush : new SolidColorBrush(Color.Parse("#38A9F5"));

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Refresh();
    }

    private void Refresh()
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            var on = i == _picker.Highlighted;
            var row = _rows[i];
            row.BorderBrush = on ? Accent : Brushes.Transparent;
            if (row.Child is Panel { Children: [Border tint, ..] })
            {
                tint.Background = Accent;
                tint.IsVisible = on;
            }
        }
    }
}
