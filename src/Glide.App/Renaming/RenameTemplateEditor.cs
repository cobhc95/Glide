using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Glide.Core.Renaming;

namespace Glide.App.Renaming;

/// <summary>
/// The "New name" box of the batch rename dialog: typed text mixed with blue pieces (chips) for Old name, Number, Date taken ...
/// It is a row of small text boxes with a chip between each two of them (always text, chip, text, chip ..., text), so typing,
/// selecting and the caret are the ordinary TextBox ones. Backspace at the start of a text deletes the chip before it, Delete at
/// the end deletes the chip after it, Left / Right at an edge move over a chip, and clicking a chip puts the caret after it.
/// The content is one template string (<see cref="RenameTemplate"/>): <see cref="Template"/> reads and sets it.
/// </summary>
internal sealed class RenameTemplateEditor : Border
{
    internal static readonly IBrush TokenBack = new SolidColorBrush(Color.Parse("#1F3B5C"));
    internal static readonly IBrush TokenText = new SolidColorBrush(Color.Parse("#CFE5FF"));
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#E8EAED"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#4EA1FF"));
    private static readonly IBrush Line = new SolidColorBrush(Color.Parse("#3A3F46"));

    private readonly WrapPanel _panel = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private List<RenamePiece> _pieces = [RenamePiece.Literal("")];
    private int _lastIndex;
    private bool _building;

    public RenameTemplateEditor()
    {
        Name = "BatchRenameInput";
        Background = new SolidColorBrush(Color.Parse("#15171A"));
        BorderBrush = Line;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(10, 6);
        MinHeight = 44;
        Cursor = new Cursor(StandardCursorType.Ibeam);
        Child = _panel;
        Focusable = false;
        Styles.Add(PlainBoxStyle(null));
        Styles.Add(PlainBoxStyle(":focus"));
        Styles.Add(PlainBoxStyle(":pointerover"));
        AddHandler(GotFocusEvent, (_, _) => BorderBrush = Accent, Avalonia.Interactivity.RoutingStrategies.Bubble);
        AddHandler(LostFocusEvent, (_, _) => Dispatcher.UIThread.Post(() => { if (!IsKeyboardFocusWithin) BorderBrush = Line; }, DispatcherPriority.Background), Avalonia.Interactivity.RoutingStrategies.Bubble);
        PointerPressed += (_, e) =>
        {
            // A click on the empty part of the box puts the caret at the end.
            if (e.Source is Visual v && (v == this || v == _panel)) { FocusPiece(_pieces.Count - 1, int.MaxValue); e.Handled = true; }
        };
        Rebuild();
    }

    /// <summary>Raised when the user (or <see cref="Template"/>, <see cref="Insert"/>) changed the content.</summary>
    public event Action? TemplateChanged;

    /// <summary>The template string: typed text with <c>{token}</c> pieces. Setting it replaces everything.</summary>
    public string Template
    {
        get => RenameTemplate.Serialize(_pieces);
        set
        {
            var hadFocus = IsKeyboardFocusWithin;
            _pieces = Normalize(RenameTemplate.Parse(value));
            _lastIndex = _pieces.Count - 1;
            Rebuild();
            if (hadFocus) FocusPiece(_lastIndex, int.MaxValue);
            TemplateChanged?.Invoke();
        }
    }

    /// <summary>The text boxes, left to right (tests type into them).</summary>
    public IReadOnlyList<TextBox> TextPieces => _panel.Children.OfType<TextBox>().ToArray();

    /// <summary>The chips inside the box, left to right.</summary>
    public IReadOnlyList<Border> TokenChips => _panel.Children.OfType<Border>().ToArray();

    /// <summary>The pieces now (typed text and tokens), always starting and ending with text.</summary>
    public IReadOnlyList<RenamePiece> Pieces => _pieces;

    public void FocusEnd() => FocusPiece(_pieces.Count - 1, int.MaxValue);

    /// <summary>Puts a token at the caret (replacing a selection), or at the end when the box was never used. The caret ends after it.</summary>
    public void Insert(RenameToken token)
    {
        var index = _lastIndex >= 0 && _lastIndex < _pieces.Count && !_pieces[_lastIndex].IsToken ? _lastIndex : _pieces.Count - 1;
        var text = _pieces[index].Text;
        var start = text.Length;
        var end = text.Length;
        if (_panel.Children.Count > index && _panel.Children[index] is TextBox box)
        {
            start = Math.Clamp(Math.Min(box.SelectionStart, box.SelectionEnd), 0, text.Length);
            end = Math.Clamp(Math.Max(box.SelectionStart, box.SelectionEnd), 0, text.Length);
            if (start == end) start = end = Math.Clamp(box.CaretIndex, 0, text.Length);
        }
        _pieces[index] = RenamePiece.Literal(text[..start]);
        _pieces.Insert(index + 1, RenamePiece.Of(token));
        _pieces.Insert(index + 2, RenamePiece.Literal(text[end..]));
        _lastIndex = index + 2;
        Rebuild();
        FocusPiece(index + 2, 0);
        TemplateChanged?.Invoke();
    }

    /// <summary>Removes the chip at <paramref name="tokenIndex"/> (an odd index), joining the text on both sides; the caret stays at the join.</summary>
    private void RemoveToken(int tokenIndex)
    {
        if (tokenIndex <= 0 || tokenIndex >= _pieces.Count - 1 || !_pieces[tokenIndex].IsToken) return;
        var before = _pieces[tokenIndex - 1].Text;
        _pieces[tokenIndex - 1] = RenamePiece.Literal(before + _pieces[tokenIndex + 1].Text);
        _pieces.RemoveRange(tokenIndex, 2);
        _lastIndex = tokenIndex - 1;
        Rebuild();
        FocusPiece(tokenIndex - 1, before.Length);
        TemplateChanged?.Invoke();
    }

    /// <summary>Text, token, text, token ... text: an (empty) text between two tokens and at both ends.</summary>
    private static List<RenamePiece> Normalize(IReadOnlyList<RenamePiece> parsed)
    {
        var list = new List<RenamePiece>();
        foreach (var piece in parsed)
        {
            if (piece.IsToken)
            {
                if (list.Count == 0 || list[^1].IsToken) list.Add(RenamePiece.Literal(""));
                list.Add(piece);
            }
            else if (list.Count > 0 && !list[^1].IsToken) list[^1] = RenamePiece.Literal(list[^1].Text + piece.Text);
            else list.Add(piece);
        }
        if (list.Count == 0 || list[^1].IsToken) list.Add(RenamePiece.Literal(""));
        return list;
    }

    private void Rebuild()
    {
        _building = true;
        try
        {
            _panel.Children.Clear();
            for (var i = 0; i < _pieces.Count; i++)
                _panel.Children.Add(_pieces[i].IsToken ? NewChip(i) : NewBox(i));
        }
        finally { _building = false; }
    }

    private Border NewChip(int index)
    {
        var info = RenameTemplate.Info(_pieces[index].Token!.Value);
        var chip = new Border
        {
            Name = "RenameChip", Tag = info.Token, Background = TokenBack, CornerRadius = new CornerRadius(5), Padding = new Thickness(8, 2),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 0), Cursor = new Cursor(StandardCursorType.Arrow),
            Child = new TextBlock { Text = info.BoxLabel, Foreground = TokenText, FontSize = 13, VerticalAlignment = VerticalAlignment.Center }
        };
        ToolTip.SetTip(chip, "Click, then Backspace removes this piece");
        chip.PointerPressed += (_, e) =>
        {
            _lastIndex = index + 1;
            FocusPiece(index + 1, 0);
            e.Handled = true;
        };
        return chip;
    }

    private TextBox NewBox(int index)
    {
        var last = index == _pieces.Count - 1;
        var box = new TextBox
        {
            Text = _pieces[index].Text, Classes = { "tplbox" }, Foreground = TextBrush, CaretBrush = TextBrush, FontSize = 15,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0, 0, 2, 0), MinHeight = 0, Height = 28,
            MinWidth = last ? 80 : 3, VerticalContentAlignment = VerticalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0)
        };
        box.TextChanged += (_, _) =>
        {
            if (_building || index >= _pieces.Count) return;
            _pieces[index] = RenamePiece.Literal(box.Text ?? "");
            TemplateChanged?.Invoke();
        };
        box.GotFocus += (_, _) => _lastIndex = index;
        box.AddHandler(KeyDownEvent, (_, e) => BoxKeyDown(box, index, e), Avalonia.Interactivity.RoutingStrategies.Tunnel);
        return box;
    }

    private void BoxKeyDown(TextBox box, int index, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None) return;
        var noSelection = box.SelectionStart == box.SelectionEnd;
        var length = box.Text?.Length ?? 0;
        switch (e.Key)
        {
            case Key.Back when noSelection && box.CaretIndex == 0 && index > 0:
                RemoveToken(index - 1); e.Handled = true; break;
            case Key.Delete when noSelection && box.CaretIndex >= length && index < _pieces.Count - 1:
                RemoveToken(index + 1); e.Handled = true; break;
            case Key.Left when noSelection && box.CaretIndex == 0 && index > 0:
                FocusPiece(index - 2, int.MaxValue); e.Handled = true; break;
            case Key.Right when noSelection && box.CaretIndex >= length && index < _pieces.Count - 1:
                FocusPiece(index + 2, 0); e.Handled = true; break;
        }
    }

    /// <summary>Focuses the text at <paramref name="index"/> with the caret at <paramref name="caret"/> (int.MaxValue: the end), once the new boxes are laid out.</summary>
    private void FocusPiece(int index, int caret)
    {
        index = Math.Clamp(index, 0, _pieces.Count - 1);
        if (_pieces[index].IsToken) index = Math.Min(index + 1, _pieces.Count - 1);
        if (_panel.Children.Count <= index || _panel.Children[index] is not TextBox box) return;
        _lastIndex = index;
        Dispatcher.UIThread.Post(() =>
        {
            if (box.GetVisualRoot() is null) return;
            box.Focus();
            box.CaretIndex = Math.Clamp(caret, 0, box.Text?.Length ?? 0);
            box.ClearSelection();
        }, DispatcherPriority.Input);
    }

    /// <summary>The Fluent text box draws its own background, border and focus underline; inside this box they are all off.</summary>
    private static Style PlainBoxStyle(string? pseudoClass)
    {
        var selector = pseudoClass is null
            ? (Func<Selector?, Selector>)(x => x.OfType<TextBox>().Class("tplbox").Template().OfType<Border>().Name("PART_BorderElement"))
            : x => x.OfType<TextBox>().Class("tplbox").Class(pseudoClass).Template().OfType<Border>().Name("PART_BorderElement");
        return new Style(selector)
        {
            Setters =
            {
                new Setter(Border.BackgroundProperty, Brushes.Transparent),
                new Setter(Border.BorderBrushProperty, Brushes.Transparent),
                new Setter(Border.BorderThicknessProperty, new Thickness(0)),
            }
        };
    }
}
