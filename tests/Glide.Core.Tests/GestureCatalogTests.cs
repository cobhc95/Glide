using Glide.Core.Commands;
using Xunit;

namespace Glide.Core.Tests;

public sealed class GestureCatalogTests
{
    [Fact]
    public void Mature_contract_has_exactly_24_slots_and_19_actions()
    {
        Assert.Equal(24, GestureCatalog.Slots.Count);
        Assert.Equal(19, GestureCatalog.Actions.Count);
        Assert.Equal(24, GestureCatalog.Slots.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(19, GestureCatalog.Actions.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Every_default_and_custom_action_resolves_to_a_known_action()
    {
        var known = GestureCatalog.Actions.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(GestureCatalog.Slots, slot => Assert.Contains(slot.DefaultActionId, known));

        var map = GestureCatalog.CreateDefaultMap();
        map["wheel.windowed"] = GestureCatalog.ZoomIn;
        Assert.Equal(GestureCatalog.ZoomIn, GestureCatalog.ResolveAction("wheel.windowed", map));
        Assert.Equal(GlideCommand.ZoomIn, GestureCatalog.CommandForAction(GestureCatalog.ZoomIn));
        Assert.Equal(GestureCatalog.Default, GestureCatalog.ResolveAction("wheel.windowed", new Dictionary<string,string>{{"wheel.windowed","bogus"}}));
    }
}
