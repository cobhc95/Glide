using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Threading;

namespace Glide.App.Controls;

/// <summary>
/// Collapsible group of Settings rows ("card").  Deliberately template-less: Settings search, the
/// card map and the headless tests walk the visual and logical trees of pages that are hidden and
/// therefore never get templates applied, so every element is built in code and exists from the
/// moment the card is initialised.
/// <para>XAML usage:
/// <c>&lt;controls:SettingsCard CardId="..." Title="..." Subtitle="..." Icon="Info" IsExpanded="True"&gt;
/// &lt;StackPanel Classes="settingsCardBody" Spacing="10"&gt; rows &lt;/StackPanel&gt;&lt;/controls:SettingsCard&gt;</c></para>
/// On initialisation the single <see cref="StackPanel"/> child (class <c>settingsCardBody</c>) becomes
/// <see cref="Body"/> and is placed below the <see cref="Header"/> toggle.  Collapsing only hides the
/// body; rows stay in the logical and visual tree.
/// <para>Avalonia runs OnInitialized once the card has finished EndInit AND sits in a rooted logical
/// tree (e.g. inside a Window). After SettingsWindow's InitializeComponent every card is built; a card
/// created in code must be added to a rooted tree before <see cref="Body"/> holds the real rows.</para>
/// </summary>
public sealed class SettingsCard : Border
{
    public const string CardClass = "settingsCard";
    public const string HeaderClass = "settingsCardHeader";
    public const string BodyClass = "settingsCardBody";
    public const string BadgeClass = "settingsCardBadge";
    public const string RuleClass = "settingsCardRule";
    public const string RevealHighlightClass = "settingsRevealHighlight";

    private const double ChevronExpandedAngle = 0;
    private const double ChevronCollapsedAngle = -90;
    private static readonly TimeSpan ChevronDuration = TimeSpan.FromMilliseconds(120);

    public static readonly StyledProperty<string?> CardIdProperty =
        AvaloniaProperty.Register<SettingsCard, string?>(nameof(CardId));

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<SettingsCard, string?>(nameof(Title));

    public static readonly StyledProperty<string?> SubtitleProperty =
        AvaloniaProperty.Register<SettingsCard, string?>(nameof(Subtitle));

    /// <summary>A <see cref="GlideIconView.Kind"/> name.</summary>
    public static readonly StyledProperty<string> IconProperty =
        AvaloniaProperty.Register<SettingsCard, string>(nameof(Icon), "Settings");

    /// <summary>Optional colour for the header icon (default: the theme accent). Used by the red Reset Glide card.</summary>
    public static readonly StyledProperty<IBrush?> IconBrushProperty =
        AvaloniaProperty.Register<SettingsCard, IBrush?>(nameof(IconBrush));

    public static readonly StyledProperty<bool> IsExpandedProperty =
        AvaloniaProperty.Register<SettingsCard, bool>(nameof(IsExpanded), true);

    /// <summary>Number of settings in this card that differ from their defaults; 0 hides the badge.</summary>
    public static readonly StyledProperty<int> ChangedCountProperty =
        AvaloniaProperty.Register<SettingsCard, int>(nameof(ChangedCount));

    /// <summary>Tooltip for the "N changed" badge (typically the changed setting titles, one per line).</summary>
    public static readonly StyledProperty<string?> ChangedTipProperty =
        AvaloniaProperty.Register<SettingsCard, string?>(nameof(ChangedTip));

    /// <summary>
    /// Comma separated catalogue ids anchored on a control that is not itself a single editor
    /// (action entries such as <c>mouse.gestureMatrix</c> or <c>profiles.import, profiles.export</c>).
    /// XAML: <c>controls:SettingsCard.SettingIds="profiles.import,profiles.export"</c>.
    /// </summary>
    public static readonly AttachedProperty<string?> SettingIdsProperty =
        AvaloniaProperty.RegisterAttached<SettingsCard, Control, string?>("SettingIds");

    public static string? GetSettingIds(Control element) => element.GetValue(SettingIdsProperty);
    public static void SetSettingIds(Control element, string? value) => element.SetValue(SettingIdsProperty, value);

    /// <summary>The parsed, trimmed, non-empty ids of <see cref="SettingIdsProperty"/> on <paramref name="element"/>.</summary>
    public static IReadOnlyList<string> GetSettingIdList(Control element)
    {
        var raw = GetSettingIds(element);
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private readonly StackPanel _root;
    private readonly Border _rule;
    private readonly GlideIconView _icon;
    private readonly TextBlock _title;
    private readonly TextBlock _subtitle;
    private readonly Border _badge;
    private readonly TextBlock _badgeText;
    private readonly GlideIconView _chevron;
    private readonly RotateTransform _chevronRotation = new(ChevronExpandedAngle);
    private DispatcherTimer? _chevronTimer;
    private DateTime _chevronStart;
    private double _chevronFrom;
    private bool _built;

    public SettingsCard()
    {
        Classes.Add(CardClass);

        _icon = new GlideIconView { Kind = Icon, Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        _icon.Classes.Add("settingsCardIcon");
        Grid.SetColumn(_icon, 0);

        _title = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
        _title.Classes.Add("settingsCardTitle");
        _subtitle = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, Margin = new Thickness(0, 1, 0, 0) };
        _subtitle.Classes.Add("settingsCardSubtitle");
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _title, _subtitle } };
        Grid.SetColumn(titles, 1);

        _badgeText = new TextBlock();
        _badge = new Border { Child = _badgeText, IsVisible = false, Margin = new Thickness(10, 0, 0, 0) };
        _badge.Classes.Add(BadgeClass);
        Grid.SetColumn(_badge, 2);

        _chevron = new GlideIconView
        {
            Kind = "Down",
            Width = 14,
            Height = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            RenderTransformOrigin = RelativePoint.Center,
        };
        _chevron.RenderTransform = _chevronRotation;
        _chevron.Classes.Add("settingsCardChevron");
        Grid.SetColumn(_chevron, 3);

        var headerGrid = new Grid
        {
            ColumnDefinitions = ColumnDefinitions.Parse("Auto,*,Auto,Auto"),
            Children = { _icon, titles, _badge, _chevron },
        };

        Header = new ToggleButton
        {
            Content = headerGrid,
            IsChecked = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        Header.Classes.Add(HeaderClass);
        Header.IsCheckedChanged += (_, _) => IsExpanded = Header.IsChecked == true;

        _rule = new Border { Height = 1 };
        _rule.Classes.Add(RuleClass);

        // Placeholder until OnInitialized adopts the XAML body.
        Body = new StackPanel();
        Body.Classes.Add(BodyClass);

        _root = new StackPanel();
        PseudoClasses.Set(":expanded", true);
        UpdateHeaderTexts();
    }

    // Keep the "Border.settingsCard" selectors in GlideTheme.axaml working for this subclass.
    protected override Type StyleKeyOverride => typeof(Border);

    public string? CardId { get => GetValue(CardIdProperty); set => SetValue(CardIdProperty, value); }
    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Subtitle { get => GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }
    public string Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public IBrush? IconBrush { get => GetValue(IconBrushProperty); set => SetValue(IconBrushProperty, value); }
    public bool IsExpanded { get => GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }
    public int ChangedCount { get => GetValue(ChangedCountProperty); set => SetValue(ChangedCountProperty, value); }
    public string? ChangedTip { get => GetValue(ChangedTipProperty); set => SetValue(ChangedTipProperty, value); }

    /// <summary>The header toggle (class <c>settingsCardHeader</c>); IsChecked mirrors <see cref="IsExpanded"/>.</summary>
    public ToggleButton Header { get; }

    /// <summary>The rows panel (class <c>settingsCardBody</c>) taken from the XAML child.</summary>
    public StackPanel Body { get; private set; }

    /// <summary>The "N changed" badge (exposed for tests and diagnostics).</summary>
    public Border Badge => _badge;

    /// <summary>Raised after <see cref="IsExpanded"/> changes.</summary>
    public event EventHandler? ExpandedChanged;

    protected override void OnInitialized()
    {
        base.OnInitialized();
        if (_built) return;

        if (Child is not StackPanel body || !body.Classes.Contains(BodyClass))
            throw new InvalidOperationException($"SettingsCard '{CardId}' must contain exactly one StackPanel with class '{BodyClass}'.");

        Child = null;
        Body = body;
        _root.Children.Add(Header);
        _root.Children.Add(_rule);
        _root.Children.Add(Body);
        Child = _root;
        _built = true;
        ApplyExpanded();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        StopChevronAnimation(snap: true);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsExpandedProperty)
        {
            ApplyExpanded();
            ExpandedChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (change.Property == IconProperty)
        {
            _icon.Kind = Icon;
        }
        else if (change.Property == IconBrushProperty)
        {
            // A local value beats the "settingsCardIcon" style; null hands the colour back to the theme.
            if (IconBrush is { } brush) _icon.IconBrush = brush; else _icon.ClearValue(GlideIconView.IconBrushProperty);
        }
        else if (change.Property == TitleProperty || change.Property == SubtitleProperty
                 || change.Property == ChangedCountProperty || change.Property == ChangedTipProperty)
        {
            UpdateHeaderTexts();
        }
    }

    private void ApplyExpanded()
    {
        var expanded = IsExpanded;
        if (Header.IsChecked != expanded) Header.IsChecked = expanded;
        Body.IsVisible = expanded;
        _rule.IsVisible = expanded;
        AnimateChevron();
        PseudoClasses.Set(":expanded", expanded);
        PseudoClasses.Set(":collapsed", !expanded);
    }

    // 120 ms ease-out rotation of the chevron.  Driven by a DispatcherTimer rather than an Avalonia
    // Transition: transitions subscribe to the compositor's render clock, which throws when the headless
    // test session (and therefore the SettingsWindow tests) toggles a card.  Off screen it simply snaps.
    private void AnimateChevron()
    {
        var target = IsExpanded ? ChevronExpandedAngle : ChevronCollapsedAngle;
        if (!this.IsAttachedToVisualTree() || !IsEffectivelyVisible)
        {
            StopChevronAnimation(snap: true);
            return;
        }
        if (Math.Abs(_chevronRotation.Angle - target) < 0.01) return;
        _chevronFrom = _chevronRotation.Angle;
        _chevronStart = DateTime.UtcNow;
        _chevronTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Render, (_, _) => StepChevron());
        _chevronTimer.Start();
    }

    private void StepChevron()
    {
        var target = IsExpanded ? ChevronExpandedAngle : ChevronCollapsedAngle;
        var t = Math.Clamp((DateTime.UtcNow - _chevronStart).TotalMilliseconds / ChevronDuration.TotalMilliseconds, 0, 1);
        var eased = 1 - (1 - t) * (1 - t);
        _chevronRotation.Angle = _chevronFrom + (target - _chevronFrom) * eased;
        if (t >= 1) StopChevronAnimation(snap: true);
    }

    private void StopChevronAnimation(bool snap)
    {
        _chevronTimer?.Stop();
        if (snap) _chevronRotation.Angle = IsExpanded ? ChevronExpandedAngle : ChevronCollapsedAngle;
    }

    private void UpdateHeaderTexts()
    {
        _title.Text = Title;
        _subtitle.Text = Subtitle;
        _subtitle.IsVisible = !string.IsNullOrEmpty(Subtitle);

        var count = Math.Max(0, ChangedCount);
        _badge.IsVisible = count > 0;
        _badgeText.Text = count > 0 ? $"{count} changed" : string.Empty;
        ToolTip.SetTip(_badge, count > 0 && !string.IsNullOrWhiteSpace(ChangedTip) ? ChangedTip : null);

        AutomationProperties.SetName(Header, count > 0 ? $"{Title}, {count} changed" : Title ?? string.Empty);
        AutomationProperties.SetHelpText(Header, Subtitle ?? string.Empty);
    }
}
