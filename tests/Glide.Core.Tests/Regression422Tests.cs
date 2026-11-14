using System.Reflection;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Glide.App;
using Glide.App.Settings;

namespace Glide.Core.Tests;

/// <summary>
/// Regressions for the Glide 4.2.2 stability fixes: the hidden-owner dialog crash and the debounced
/// Settings search. These run on the real (now synchronously-awaited) headless UI thread.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class Regression422Tests
{
    public Regression422Tests(AvaloniaHeadlessFixture fixture)
    {
        _ = fixture;
    }

    [Fact]
    public async Task OwnedDialog_WithNonVisibleOwner_DoesNotThrow()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(async () =>
        {
            var window = new MainWindow();
            try
            {
                // The window is intentionally never shown, mimicking the Speed Boost standby teardown
                // where Avalonia previously rejected ShowDialog with "non-visible owner" and the
                // async-void caller crashed the process.
                Assert.False(window.IsVisible);

                // There is also a generic ShowOwnedDialogAsync<T>(Window); pick the non-generic one. (This test was
                // never actually executed before the headless fixture awaited async bodies: the lookup threw
                // AmbiguousMatchException into an unobserved task.)
                var method = typeof(MainWindow).GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "ShowOwnedDialogAsync" && !m.IsGenericMethodDefinition &&
                                         m.GetParameters() is [{ ParameterType: var p }] && p == typeof(Window));
                Assert.NotNull(method);

                var dialog = new Window();
                var task = (Task)method!.Invoke(window, new object[] { dialog })!;
                await task;
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task SettingsSearch_DebouncesAndFlushes()
    {
        await AvaloniaHeadlessFixture.RunOnUIThreadAsync(() =>
        {
            var window = new SettingsWindow(new GlideSettingsState(), (_, _) => { });
            window.Show();
            var box = window.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Name == "SearchBox");
            var panel = window.GetVisualDescendants().OfType<StackPanel>().FirstOrDefault(p => p.Name == "SearchPanel");
            Assert.NotNull(box);
            Assert.NotNull(panel);

            // Invoke the real handler deterministically (the XAML TextChanged wiring is exercised by
            // normal UI usage); this isolates the debounce/flush contract.
            var handler = typeof(SettingsWindow).GetMethod("SearchChanged", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(handler);
            box!.Text = "image";
            handler!.Invoke(window, new object?[] { box, null! });

            // The rebuild is debounced, so it must not have run synchronously.
            Assert.Empty(panel!.Children);
            Assert.False(panel.IsVisible);

            window.FlushPendingSearch();
            Assert.True(panel.Children.Count > 0);
            Assert.True(panel.IsVisible);

            window.Close();
            return Task.CompletedTask;
        });
    }
}
