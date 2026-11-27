using Glide.Core.Settings;

namespace Glide.Core.Tests;

/// <summary>The folder position line with "include parent folder": "6/94 - (10/30)" (parent rank / sibling folders of the parent).</summary>
public sealed class FolderCounterFormatTests
{
    [Fact]
    public void ParentFolderRankIsBracketedWithItsSiblingTotal()
    {
        var source = SourceText.Read("src/Glide.App/MainWindow.Chrome.cs");
        Assert.Contains("value += $\" - ({parentIndex + 1}/{parents.Length})\";", source);
        Assert.DoesNotContain("value += $\"/{parentIndex + 1}\"", source);
    }

    [Fact]
    public void DescriptionsNoLongerShowTheOldFormat()
    {
        var entry = SettingsCatalog.All.Single(e => e.Id == "folderNav.counterGrandparent");
        Assert.DoesNotContain("5/50/4", entry.Label + entry.Description);
        Assert.Contains("6/94 - (10/30)", entry.Label);
        Assert.DoesNotContain("5/50/4", SourceText.Read("src/Glide.App/Settings/SettingsWindow.axaml"));
    }
}
