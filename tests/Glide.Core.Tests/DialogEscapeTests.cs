using Avalonia.Controls;
using Avalonia.Input;
using Glide.App;
using Glide.App.Services;
using Glide.App.Settings;

namespace Glide.Core.Tests;

/// <summary>Esc cancels every dialog / secondary window (one shared class handler, see DialogEscape).</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
[Trait("Area", "Window")]
public sealed class DialogEscapeTests
{
    public DialogEscapeTests(AvaloniaHeadlessFixture fixture) => _ = fixture;

    [Fact]
    public void Every_Window_subclass_is_covered_or_excluded_with_a_reason()
    {
        var windows = typeof(MainWindow).Assembly.GetTypes()
            .Where(t => typeof(Window).IsAssignableFrom(t) && !t.IsAbstract).ToList();
        Assert.True(windows.Count >= 8);
        foreach (var type in windows)
        {
            if (DialogEscape.ExcludedTypes.TryGetValue(type, out var reason))
                Assert.True(reason.Length >= 20, $"{type.Name}: excluded without a real reason");
            else
                Assert.True(DialogEscape.AppliesTo(type), $"{type.Name}: Esc does not cancel it");
        }
        // Plain `new Window { ... }` prompts (nearby folder, slideshow options, rename, confirmations) are Window itself.
        Assert.True(DialogEscape.AppliesTo(typeof(Window)));
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("status")]
    [InlineData("title")]
    [InlineData("reset")]
    public void Esc_closes_the_dialog_like_its_close_button(string kind)
    {
        AvaloniaHeadlessFixture.RunOnUIThread(() =>
        {
            DialogEscape.Register();
            Window window = kind switch
            {
                "status" => new StatusBarCustomizerWindow(string.Empty),
                "title" => new TitleBarCustomizerWindow(TitleBarButtonCatalog.DefaultButtonIds),
                "reset" => new ResetResultWindow("Reset Glide", "Done", ""),
                _ => new Window { Content = new CheckBox { Content = "Don't remind me again" } }
            };
            var closed = false;
            window.Closed += (_, _) => closed = true;
            window.Show();
            window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape, Source = window });
            Assert.True(closed, kind + " did not close on Esc");
        });
    }

    [Fact]
    public void A_handled_Esc_for_example_a_capture_or_open_dropdown_does_not_close_the_dialog()
    {
        AvaloniaHeadlessFixture.RunOnUIThread(() =>
        {
            DialogEscape.Register();
            var window = new Window();
            window.AddHandler(InputElement.KeyDownEvent, (_, e) => e.Handled = true, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            var closed = false;
            window.Closed += (_, _) => closed = true;
            window.Show();
            window.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape, Source = window });
            Assert.False(closed);
            window.Close();
        });
    }

    [Fact]
    public void Main_window_is_never_closed_by_the_dialog_escape() =>
        Assert.False(DialogEscape.AppliesTo(typeof(MainWindow)));
}
