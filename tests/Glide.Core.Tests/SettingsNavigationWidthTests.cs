using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Glide.App.Settings;

namespace Glide.Core.Tests;

/// <summary>The Settings left navigation column is a narrow FIXED width: it fits every page label and cannot be resized.</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class SettingsNavigationWidthTests
{
    public SettingsNavigationWidthTests(AvaloniaHeadlessFixture fixture) => _ = fixture;

    private const double MaxNavWidth = 232;
    /// <summary>The column may be at most this much wider than the longest label needs (glyph, gaps and side padding included).</summary>
    private const double MaxSlack = 24;

    [Theory]
    [InlineData("Dark", 1.0)]
    [InlineData("Light", 1.0)]
    [InlineData("Dark", 1.5)]
    [InlineData("Light", 2.0)]
    public async Task Navigation_column_is_narrow_fixed_and_shows_every_label_unclipped(string theme, double scale)
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            Application.Current!.RequestedThemeVariant = theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
            var window = new SettingsWindow(new GlideSettingsState(), (_, _) => { });
            window.Show();
            try
            {
                var root = window.GetLogicalDescendants().OfType<Grid>().Single(g => g.Name == "ReferenceRoot");
                var navColumn = root.ColumnDefinitions[0];
                Assert.True(navColumn.Width.IsAbsolute, "nav column must be a fixed width");
                Assert.InRange(navColumn.Width.Value, 150, MaxNavWidth);
                Assert.Equal(SettingsWindow.NavigationColumnWidth, navColumn.Width.Value);
                Assert.Empty(root.GetLogicalDescendants().OfType<GridSplitter>());
                Assert.True(root.ColumnDefinitions[1].Width.IsStar);

                var rail = window.GetLogicalDescendants().OfType<StackPanel>().Single(p => p.Name == "NavigationRail");
                var buttons = rail.Children.OfType<Button>().ToList();
                Assert.Equal(8, buttons.Count);
                var widestNeed = 0.0;
                foreach (var button in buttons)
                {
                    var text = button.GetLogicalDescendants().OfType<TextBlock>().Single();
                    text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    Assert.True(text.DesiredSize.Width <= text.Bounds.Width + 0.5, $"'{text.Text}' is clipped ({text.DesiredSize.Width} > {text.Bounds.Width})");
                    // Headless renders without the app theme, so also check at the styled size (15px, 16px side padding, 28px glyph, 8px gap).
                    var styled = new TextBlock { Text = text.Text, FontSize = 15, FontFamily = text.FontFamily };
                    styled.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    var need = 16 + 28 + 8 + styled.DesiredSize.Width + 16;
                    widestNeed = Math.Max(widestNeed, need);
                    Assert.True(need <= navColumn.Width.Value, $"'{text.Text}' would not fit the styled nav column");
                    Assert.True(text.TextWrapping == Avalonia.Media.TextWrapping.NoWrap);
                }
                Assert.True(navColumn.Width.Value - widestNeed <= MaxSlack, $"nav column {navColumn.Width.Value} is wider than the longest label needs ({widestNeed:0.#}): empty strip");
                foreach (var header in window.GetLogicalDescendants().OfType<TextBlock>().Where(t => t.Text is "Glide Settings" or "Search or browse by category"))
                {
                    header.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    var big = new TextBlock { Text = header.Text, FontSize = header.Text == "Glide Settings" ? 27 : 13, FontWeight = Avalonia.Media.FontWeight.SemiBold, FontFamily = header.FontFamily };
                    big.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    Assert.True(18 + big.DesiredSize.Width + 10 <= navColumn.Width.Value, $"'{header.Text}' does not fit styled");
                    Assert.True(header.DesiredSize.Width <= navColumn.Width.Value - 1, $"'{header.Text}' does not fit the nav column");
                }

                var save = Environment.GetEnvironmentVariable("GLIDE_SAVE_RENDERS");
                if (!string.IsNullOrWhiteSpace(save))
                {
                    var size = new PixelSize((int)(window.Bounds.Width * scale), (int)(window.Bounds.Height * scale));
                    using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
                    bitmap.Render(window);
                    bitmap.Save(Path.Combine(save, $"settings-nav-{theme}-{scale:0.0}x.png"));
                }
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }
}
