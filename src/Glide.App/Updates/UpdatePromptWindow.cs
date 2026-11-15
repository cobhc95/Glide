using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Glide.App.Controls;
using Glide.App.Services;

namespace Glide.App.Updates;

/// <summary>
/// "A new version of Glide is available": the modal prompt of the update checker. It can only open the release page in the browser
/// (View on GitHub) or go away (Not now); nothing is downloaded or installed. "Don't remind me again" is applied by the caller when
/// a button is pressed. Not now has the initial focus (so Enter never opens the browser by accident) and Esc is Not now.
/// </summary>
public sealed class UpdatePromptWindow : Window
{
    public const double DialogWidth = 470;
    public const string TitleText = "A new version of Glide is available";
    public const string DontRemindText = "Don't remind me again";
    public const string NoteText = "View on GitHub opens the release page in your browser. Glide never downloads or installs anything by itself. You can turn update prompts back on in Settings.";

    public UpdatePromptModel Model { get; }
    public Button NotNowButton { get; }
    public Button ViewButton { get; }
    public CheckBox DontRemindCheck { get; }
    public TextBlock HeadingText { get; }
    public TextBlock ReleasedText { get; }
    public TextBlock CurrentVersionText { get; }
    public TextBlock LatestVersionText { get; }
    public TextBlock NoteBlock { get; }

    /// <summary>True once the window was actually shown.</summary>
    public bool WasShown { get; private set; }

    /// <summary>What the user chose; null until a button, Esc or the close button was used.</summary>
    public UpdatePromptResult? Outcome { get; private set; }

    public UpdatePromptWindow(UpdatePromptModel model)
    {
        Model = model;
        Title = "Glide update";
        Width = DialogWidth;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        FontSize = 13;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.Bind(BackgroundProperty, this.GetResourceObservable("BrushSurface"));

        // Header: icon tile, title, "Released N days ago on GitHub".
        var downloadIcon = new GlideIconView { Kind = "Download", Width = 20, Height = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        downloadIcon.Bind(GlideIconView.IconBrushProperty, this.GetResourceObservable("BrushAccent"));
        var iconTile = new Border { Width = 38, Height = 38, CornerRadius = new CornerRadius(9), VerticalAlignment = VerticalAlignment.Top, Child = downloadIcon };
        iconTile.Bind(Border.BackgroundProperty, this.GetResourceObservable("BrushAccentSoft"));
        HeadingText = new TextBlock { Text = TitleText, FontSize = 16, FontWeight = FontWeight.Medium, TextWrapping = TextWrapping.Wrap };
        ReleasedText = new TextBlock { Text = model.ReleasedText, FontSize = 12, Margin = new Thickness(0, 3, 0, 0) };
        ReleasedText.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushMuted"));
        var titles = new StackPanel { Children = { HeadingText, ReleasedText } };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12 };
        header.Children.Add(iconTile);
        Grid.SetColumn(titles, 1);
        header.Children.Add(titles);

        // "You have 5.0.0 -> 5.0.1 [New]"
        var youHave = new TextBlock { Text = "You have ", VerticalAlignment = VerticalAlignment.Center };
        CurrentVersionText = new TextBlock { Text = model.CurrentVersion, FontWeight = FontWeight.Medium, VerticalAlignment = VerticalAlignment.Center };
        var arrow = new GlideIconView { Kind = "Forward", Width = 13, Height = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
        arrow.Bind(GlideIconView.IconBrushProperty, this.GetResourceObservable("BrushMuted"));
        LatestVersionText = new TextBlock { Text = model.LatestVersion, FontWeight = FontWeight.Medium, VerticalAlignment = VerticalAlignment.Center };
        var pillText = new TextBlock { Text = "New", FontSize = 11, FontWeight = FontWeight.Medium, Foreground = new SolidColorBrush(Color.Parse("#04243E")) };
        var pill = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 1), VerticalAlignment = VerticalAlignment.Center, Child = pillText };
        pill.Bind(Border.BackgroundProperty, this.GetResourceObservable("BrushAccent"));
        var versionLine = new StackPanel { Orientation = Orientation.Horizontal, Children = { youHave, CurrentVersionText, arrow, LatestVersionText } };
        var versionGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        versionGrid.Children.Add(versionLine);
        Grid.SetColumn(pill, 1);
        versionGrid.Children.Add(pill);
        var versionRow = new Border
        {
            CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(12, 9),
            Margin = new Thickness(0, 14, 0, 4), Child = versionGrid
        };
        versionRow.Bind(Border.BackgroundProperty, this.GetResourceObservable("BrushSurfaceRaised"));
        versionRow.Bind(Border.BorderBrushProperty, this.GetResourceObservable("BrushBorder"));
        AutomationProperties.SetName(versionRow, $"You have {model.CurrentVersion}, new version {model.LatestVersion}");

        DontRemindCheck = new CheckBox { Content = DontRemindText, Margin = new Thickness(0, 14, 0, 2) };
        AutomationProperties.SetName(DontRemindCheck, DontRemindText);

        NotNowButton = new Button { Content = "Not now", IsCancel = true };
        NotNowButton.Classes.Add("secondaryAction");
        ViewButton = new Button { Content = "View on GitHub" };
        ViewButton.Classes.Add("primaryAction");
        foreach (var button in new[] { NotNowButton, ViewButton })
        {
            button.Height = 32; button.MinHeight = 32; button.MinWidth = 0; button.Padding = new Thickness(14, 0);
            button.FontSize = 13; button.CornerRadius = new CornerRadius(7);
            button.HorizontalContentAlignment = HorizontalAlignment.Center; button.VerticalContentAlignment = VerticalAlignment.Center;
        }
        AutomationProperties.SetName(ViewButton, "View on GitHub");
        AutomationProperties.SetHelpText(ViewButton, "Opens the release page in your browser");
        NotNowButton.Click += (_, _) => Finish(UpdatePromptAction.NotNow);
        ViewButton.Click += (_, _) => Finish(UpdatePromptAction.ViewOnGitHub);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(NotNowButton);
        buttons.Children.Add(ViewButton);

        NoteBlock = new TextBlock { Text = NoteText, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
        NoteBlock.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushMuted"));

        var root = new StackPanel { Margin = new Thickness(20, 18) };
        root.Children.Add(header);
        root.Children.Add(versionRow);
        root.Children.Add(DontRemindCheck);
        root.Children.Add(buttons);
        root.Children.Add(NoteBlock);
        Content = root;

        PopupPlacementStore.Track(this, "update-prompt");
        // The browser button is never the default: the window opens with Not now focused, Enter activates the focused button only,
        // and Esc (or the close button) is Not now.
        Opened += (_, _) => { WasShown = true; NotNowButton.Focus(); };
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Finish(UpdatePromptAction.NotNow);
        };
        Closing += (_, _) => Outcome ??= new UpdatePromptResult(UpdatePromptAction.NotNow, DontRemindCheck.IsChecked == true);
    }

    private void Finish(UpdatePromptAction action)
    {
        Outcome ??= new UpdatePromptResult(action, DontRemindCheck.IsChecked == true);
        Close(Outcome);
    }
}
