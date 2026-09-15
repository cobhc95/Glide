using Glide.App.Services;

namespace Glide.Core.Tests;

public sealed class BrowserSelectionPolicyTests
{
    [Fact]
    public void Shift_range_advances_current_while_anchor_stays_fixed_and_contracts()
    {
        var paths=Enumerable.Range(0,10).Select(i=>$"p{i}").ToArray();
        var selected=new HashSet<string>(StringComparer.OrdinalIgnoreCase); var anchor=-1; var current=-1;
        BrowserSelectionPolicy.Apply(paths.Length,i => paths[i],selected,ref anchor,ref current,4,false,false);
        BrowserSelectionPolicy.Apply(paths.Length,i => paths[i],selected,ref anchor,ref current,5,true,false);
        BrowserSelectionPolicy.Apply(paths.Length,i => paths[i],selected,ref anchor,ref current,6,true,false);
        Assert.Equal(4,anchor); Assert.Equal(6,current); Assert.Equal(new[]{"p4","p5","p6"},selected.OrderBy(x=>x));
        BrowserSelectionPolicy.Apply(paths.Length,i => paths[i],selected,ref anchor,ref current,5,true,false);
        Assert.Equal(new[]{"p4","p5"},selected.OrderBy(x=>x)); Assert.Equal(5,current);
    }

    [Fact]
    public void Ctrl_toggle_keeps_meaningful_current_index()
    {
        var paths=new[]{"a","b","c"}; var selected=new HashSet<string>(); var anchor=-1; var current=-1;
        BrowserSelectionPolicy.Apply(paths.Length,i => paths[i],selected,ref anchor,ref current,0,false,false);
        BrowserSelectionPolicy.Apply(paths.Length,i => paths[i],selected,ref anchor,ref current,2,false,true);
        Assert.Equal(2,current); Assert.Equal(2,anchor); Assert.Contains("a",selected); Assert.Contains("c",selected);
    }
}
