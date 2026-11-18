using Glide.App.Services;

namespace Glide.Core.Tests;

[Trait("Area", "Diagnostics")]
public sealed class LaunchDecisionLogTests
{
    [Fact]
    public void Entry_records_extension_and_outcome_but_never_the_path()
    {
        var entry = LaunchDecisionLog.Build("image", @"C:\Users\someone\Pictures\Secret.GIF", "Open new window", null,
            true, false, false, false, false, "new_window");
        var text = System.Text.Json.JsonSerializer.Serialize(entry.GetType().GetProperties()
            .ToDictionary(p => p.Name, p => p.GetValue(entry)?.ToString()));
        Assert.Contains(".gif", text);
        Assert.Contains("new_window", text);
        Assert.DoesNotContain("someone", text);
        Assert.DoesNotContain("Secret", text);
    }

    [Fact]
    public void Invalid_path_does_not_throw()
        => Assert.NotNull(LaunchDecisionLog.Build("image", "bad\0path", "Open in new tab", null, true, false, false, false, false, "new_tab"));
}
