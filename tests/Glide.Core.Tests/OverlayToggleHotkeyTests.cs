using Glide.Core.Commands;
using Xunit;

namespace Glide.Core.Tests;

public class OverlayToggleHotkeyTests
{
    [Fact]
    public void Default_is_AltO() =>
        Assert.Contains("Alt+O", HotkeyCatalog.CreateDefaultMap()[HotkeyDefaultMigrations.OverlayToggleId]);

    [Fact]
    public void Migration_gives_AltO_to_an_unassigned_row()
    {
        var map = new Dictionary<string, List<string>> { [HotkeyDefaultMigrations.OverlayToggleId] = [] };
        Assert.True(HotkeyDefaultMigrations.MigrateOverlayToggle(map, 18));
        Assert.Equal(["Alt+O"], map[HotkeyDefaultMigrations.OverlayToggleId]);
    }

    [Fact]
    public void Migration_keeps_user_bindings_and_taken_keys()
    {
        var mine = new Dictionary<string, List<string>> { [HotkeyDefaultMigrations.OverlayToggleId] = ["Ctrl+Shift+F9"] };
        Assert.False(HotkeyDefaultMigrations.MigrateOverlayToggle(mine, 18));
        var taken = new Dictionary<string, List<string>> { [HotkeyDefaultMigrations.OverlayToggleId] = [], ["other.action"] = ["Alt+O"] };
        Assert.False(HotkeyDefaultMigrations.MigrateOverlayToggle(taken, 18));
        var newer = new Dictionary<string, List<string>> { [HotkeyDefaultMigrations.OverlayToggleId] = [] };
        Assert.False(HotkeyDefaultMigrations.MigrateOverlayToggle(newer, 19));
    }
}
