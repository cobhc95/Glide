using Avalonia.Headless;
using Glide.App.Settings;
using Xunit;

namespace Glide.Core.Tests;

[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class SettingsWindowTests
{
    public SettingsWindowTests(AvaloniaHeadlessFixture fixture)
    {
        _ = fixture;
    }

    [Fact]
    public void SettingsWindow_Instantiates_And_Loads_Without_Exceptions()
    {
        AvaloniaHeadlessFixture.RunOnUIThread(() =>
        {
            var state = new GlideSettingsState();
            var window = new SettingsWindow(state, (_, _) => { });
            Assert.NotNull(window);
        });
    }
}
