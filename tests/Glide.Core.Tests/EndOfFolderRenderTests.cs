using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Glide.App;
using Glide.App.Controls;
using Glide.App.Settings;
using Glide.Core.Navigation;

namespace Glide.Core.Tests;

/// <summary>
/// Renders the 5.0.7 end-of-folder UI headlessly with the real app styles: the Settings "Moving between folders" card
/// (folder options greyed out unless a folder choice is picked) and the "Ask me each time" picker over a picture with the
/// status-bar "Loop this folder" button on. Set GLIDE_SAVE_RENDERS to a folder to also write the PNGs used for the release
/// notes (only synthetic, generated pictures are used).
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
[Trait("Area", "Navigation")]
public sealed class EndOfFolderRenderTests
{
    public EndOfFolderRenderTests(AvaloniaHeadlessFixture fixture) => _ = fixture;

    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
    private static string? SaveDirectory => Environment.GetEnvironmentVariable("GLIDE_SAVE_RENDERS");

    /// <summary>Installs the real app styles on the application (before the window exists) and returns them for removal.</summary>
    private static IStyle[] UseAppStyles()
    {
        var app = Application.Current!;
        app.RequestedThemeVariant = ThemeVariant.Dark;
        IStyle[] styles = [new FluentTheme(), new StyleInclude(new Uri("avares://Glide/")) { Source = new Uri("avares://Glide/Styles/GlideTheme.axaml") }];
        for (var i = 0; i < styles.Length; i++) app.Styles.Insert(i, styles[i]);
        return styles;
    }

    private static void RemoveAppStyles(IStyle[] styles)
    {
        foreach (var style in styles) Application.Current!.Styles.Remove(style);
    }

    private static T Named<T>(Visual root, string name) where T : Visual =>
        root.GetVisualDescendants().OfType<T>().First(c => (c as StyledElement)?.Name == name);

    private static void Settle(TopLevel window)
    {
        for (var i = 0; i < 4; i++) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
    }

    // Rendered at 96 DPI: a scaled headless render of a whole window scales nested content twice.
    private static RenderTargetBitmap Render(Visual visual, Size size, double scale = 1)
    {
        var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale)), new Vector(96 * scale, 96 * scale));
        bitmap.Render(visual);
        return bitmap;
    }

    private static void Save(Bitmap bitmap, string name)
    {
        if (string.IsNullOrWhiteSpace(SaveDirectory)) return;
        Directory.CreateDirectory(SaveDirectory);
        bitmap.Save(Path.Combine(SaveDirectory, name));
    }

    /// <summary>A generated "landscape": sky gradient, sun, layered hills. No external or personal image.</summary>
    private static WriteableBitmap SyntheticPicture(int width, int height)
    {
        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var fb = bitmap.Lock();
        var row = new byte[fb.RowBytes];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var t = y / (double)height;
                double r = 40 + 170 * t, g = 90 + 90 * t, b = 170 - 40 * t;                       // dusk sky
                var sun = Math.Sqrt(Math.Pow(x - width * 0.68, 2) + Math.Pow(y - height * 0.38, 2));
                if (sun < height * 0.09) { r = 255; g = 214; b = 140; }
                else if (sun < height * 0.16) { var k = 1 - (sun - height * 0.09) / (height * 0.07); r += (255 - r) * k * 0.5; g += (200 - g) * k * 0.4; }
                var hill1 = height * (0.62 + 0.06 * Math.Sin(x / (double)width * 5.2));
                var hill2 = height * (0.74 + 0.05 * Math.Sin(x / (double)width * 8.1 + 1.3));
                if (y > hill1) { r = 52; g = 78 + 20 * Math.Sin(x * 0.01); b = 92; }
                if (y > hill2) { r = 28; g = 52; b = 58; }
                var i = x * 4;
                row[i] = (byte)Math.Clamp(b, 0, 255); row[i + 1] = (byte)Math.Clamp(g, 0, 255); row[i + 2] = (byte)Math.Clamp(r, 0, 255); row[i + 3] = 255;
            }
            System.Runtime.InteropServices.Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, fb.RowBytes);
        }
        return bitmap;
    }

    [Fact]
    public async Task Settings_card_shows_the_choice_and_greys_out_folder_options_for_non_folder_choices()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var styles = UseAppStyles();
            var window = new SettingsWindow(new GlideSettingsState(), (_, _) => { });
            window.Show();
            try
            {
                window.Width = 1000;
                window.Height = 720;
                Settle(window);
                typeof(SettingsWindow).GetMethod("ShowCategory", Flags)!.Invoke(window, ["Interface", true, false, null!]);
                Settle(window);
                var card = window.GetVisualDescendants().OfType<SettingsCard>().Single(c => c.CardId == "window-folders");
                card.IsExpanded = true;
                Settle(window);
                var combo = Named<ComboBox>(window, "EndOfFolderActionCombo");
                var options = Named<Border>(window, "EndOfFolderFolderOptions");
                Assert.Equal(EndOfFolderActions.Ask, (combo.SelectedItem as ComboBoxItem)?.Content);   // 5.0.8 default: Ask me each time
                Assert.True(options.IsEnabled);
                combo.SelectedIndex = 1; // Loop this folder
                Assert.False(options.IsEnabled);
                combo.SelectedIndex = 6; // Ask me each time
                Assert.True(options.IsEnabled);
                combo.SelectedIndex = 2;

                // Scroll the card to the top and open the dropdown for the screenshot.
                var scroll = Named<ScrollViewer>(window, "ContentScroll");
                var top = card.TranslatePoint(default, scroll.Content as Visual ?? scroll)!.Value.Y;
                scroll.Offset = new Vector(0, Math.Max(0, top - 12));
                Settle(window);
                // Headless popups are not placed like on the desktop: render the open dropdown's content on its own, close
                // it, render the page and draw the dropdown just below the combo, where Windows shows it.
                combo.IsDropDownOpen = true;
                Settle(window);
                var popup = combo.GetVisualDescendants().OfType<Popup>().FirstOrDefault();
                var dropdownContent = popup?.Child as Control;
                Assert.NotNull(dropdownContent);
                var dropdownSize = dropdownContent!.Bounds.Size;
                using var dropdown = Render(dropdownContent, dropdownSize);
                combo.IsDropDownOpen = false;
                Settle(window);
                using var page = Render(window, window.Bounds.Size);
                using var composed = new RenderTargetBitmap(page.PixelSize);
                using (var ctx = composed.CreateDrawingContext())
                {
                    ctx.DrawImage(page, new Rect(window.Bounds.Size));
                    var at = combo.TranslatePoint(new Point(0, combo.Bounds.Height + 2), window)!.Value;
                    ctx.DrawImage(dropdown, new Rect(at, dropdownSize));
                }
                Save(composed, "settings-moving-between-folders.png");
            }
            finally
            {
                window.Close();
                // Removing the styles restyles the closed SettingsWindow's cards; their transitions subscribe to the headless
                // MediaContext clock, which throws "Call from invalid thread" here. Teardown only: the assertions above already ran.
                foreach (var style in styles)
                {
                    try { Application.Current!.Styles.Remove(style); } catch (InvalidOperationException) { }
                }
            }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Picker_card_renders_over_the_picture_with_the_loop_button_on()
    {
        var root = Path.Combine(Path.GetTempPath(), "Glide.EndOfFolder.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
        {
            var styles = UseAppStyles();
            var previousStore = SettingsStore.SettingsDirectoryOverrideForTests.Value;
            SettingsStore.SettingsDirectoryOverrideForTests.Value = root;
            var window = new MainWindow();
            try
            {
                var loopButtonId = Glide.Core.StatusBarGroupCatalog.ActionPrefix + "navigation.toggleLoopFolder";
                typeof(MainWindow).GetField("_settings", Flags)!.SetValue(window, new GlideSettingsState
                {
                    ShowStatusSurface = true, StatusGifControlsOnlyOnGifs = true, PictureCounterEnabled = false,
                    StatusBarGroups = string.Empty // the default layout, which includes the Loop this folder button
                });
                window.Width = 1100; window.Height = 720;
                window.Show();
                // An image tab named like a picture (the file does not exist; only its name is shown).
                var workspace = (Glide.Core.Workspace.WorkspaceState)typeof(MainWindow).GetField("_workspace", Flags)!.GetValue(window)!;
                workspace.ReplaceActiveWithImage(Path.Combine(root, "Coastline 2024", "IMG_0142.jpg"));
                typeof(MainWindow).GetMethod("RebuildTabStrip", Flags)!.Invoke(window, null);
                window.FindControl<Grid>("ImageView")!.IsVisible = true;
                var picture = SyntheticPicture(1600, 1000);
                window.FindControl<ImageViewport>("Viewport")!.SwapPresentedFrame(picture, 1600, 1000, 1, null);
                typeof(MainWindow).GetMethod("UpdateGifStatusUi", Flags)!.Invoke(window, null);
                typeof(MainWindow).GetMethod("ApplyStatusVisibility", Flags)!.Invoke(window, null);
                Settle(window);

                // Loop this folder on for the active tab: the status button takes the active style.
                await (Task)typeof(MainWindow).GetMethod("ToggleLoopFolderAsync", Flags)!.Invoke(window, null)!;
                typeof(MainWindow).GetMethod("ApplyStatusVisibility", Flags)!.Invoke(window, null);
                Settle(window);
                var extra = (Dictionary<Button, string>)typeof(MainWindow).GetField("_extraStatusButtons", Flags)!.GetValue(window)!;
                var loopButton = extra.Single(kv => kv.Value == "navigation.toggleLoopFolder").Key;
                var loopOn = (bool)typeof(MainWindow).GetProperty("LoopFolderOverride", Flags)!.GetValue(window)!;
                Assert.Equal(loopOn, loopButton.Classes.Contains("statusToggleOn"));
                Assert.Equal(!loopOn, loopButton.Classes.Contains("statusToggleOff"));
                Assert.Contains("Overrides", ToolTip.GetTip(loopButton)?.ToString());
                window.FindControl<Border>("NavigationBoundaryNotice")!.IsVisible = false;

                // The picker as "Ask me each time" shows it.
                var host = window.FindControl<Panel>("EndOfFolderPickerHost")!;
                var card = new EndOfFolderPickerCard(new EndOfFolderPicker(1), "Coastline 2024", "142 of 142", "Mountains 2024");
                (EndOfFolderAction, bool)? picked = null;
                card.Picked += (a, r) => picked = (a, r);
                host.Children.Add(card);
                host.IsVisible = true;
                Settle(window);
                Assert.True(card.Bounds.Width is > 330 and < 350);
                Assert.True(card.HandleKey(EndOfFolderPickerKey.Down));
                Assert.True(card.HandleKey(EndOfFolderPickerKey.Up));
                Assert.Null(picked);
                using var frame = Render(window, window.Bounds.Size);
                Save(frame, "end-of-folder-picker.png");

                card.HandleKey(EndOfFolderPickerKey.Digit, 4);
                Assert.Equal((EndOfFolderAction.LoopFolder, false), picked);
            }
            finally
            {
                window.Close();
                SettingsStore.SettingsDirectoryOverrideForTests.Value = previousStore;
                RemoveAppStyles(styles);
            }
        });
        try { Directory.Delete(root, true); } catch { /* best effort */ }
    }

    [Fact]
    public void Picker_card_translates_only_its_own_keys()
    {
        Assert.Equal((EndOfFolderPickerKey.Up, 0), EndOfFolderPickerCard.Translate(Avalonia.Input.Key.Up, Avalonia.Input.KeyModifiers.None));
        Assert.Equal((EndOfFolderPickerKey.Escape, 0), EndOfFolderPickerCard.Translate(Avalonia.Input.Key.Escape, Avalonia.Input.KeyModifiers.None));
        Assert.Equal((EndOfFolderPickerKey.Digit, 3), EndOfFolderPickerCard.Translate(Avalonia.Input.Key.D3, Avalonia.Input.KeyModifiers.None));
        Assert.Equal((EndOfFolderPickerKey.Digit, 5), EndOfFolderPickerCard.Translate(Avalonia.Input.Key.NumPad5, Avalonia.Input.KeyModifiers.None));
        Assert.Null(EndOfFolderPickerCard.Translate(Avalonia.Input.Key.Right, Avalonia.Input.KeyModifiers.None));
        Assert.Null(EndOfFolderPickerCard.Translate(Avalonia.Input.Key.D1, Avalonia.Input.KeyModifiers.Control));
    }
}
