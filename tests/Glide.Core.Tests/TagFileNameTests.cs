using Glide.Core.Settings;
using Glide.Core.Tags;

namespace Glide.Core.Tests;

/// <summary>Issue #4: tag groups and writing / reading tags in file names.</summary>
[Trait("Area", "Explorer")]
public sealed class TagFileNameTests
{
    private const string Groups = "Family: parents, mom, dad, sibling_1\nFriends: anna, ben, carl";

    private static TagOptions Options(string groups = Groups, string prefix = "", string open = "{", string close = "}", string sep = ";", string format = "{name} {tags}") =>
        TagOptions.Create(groups, prefix, open, close, sep, format);

    // ------------------------------------------------------------------ group text

    [Fact]
    public void Groups_parse_in_order_with_the_plus_flag()
    {
        var groups = TagGroupCatalog.Parse("+Family: parents, mom , dad\n\n  Friends : anna,ben,, anna \nNoTags\n: orphan\n+Pets: cat");
        Assert.Equal(["Family", "Friends", "NoTags", "Pets"], groups.Select(g => g.Name));
        Assert.Equal(["parents", "mom", "dad"], groups[0].Tags);
        Assert.True(groups[0].IncludeGroupTag);
        Assert.Equal(["anna", "ben"], groups[1].Tags);   // trimmed, empty and repeated tags dropped
        Assert.False(groups[1].IncludeGroupTag);
        Assert.Empty(groups[2].Tags);
        Assert.True(groups[3].IncludeGroupTag);
    }

    [Fact]
    public void Groups_round_trip_and_ignore_blank_input()
    {
        var parsed = TagGroupCatalog.Parse("+Family: parents, mom\nFriends: anna");
        Assert.Equal("+Family: parents, mom\nFriends: anna", TagGroupCatalog.Serialize(parsed));
        Assert.Empty(TagGroupCatalog.Parse(null));
        Assert.Empty(TagGroupCatalog.Parse("  \n \n"));
        Assert.Equal(parsed.Select(g => (g.Name, g.IncludeGroupTag)), TagGroupCatalog.Parse(TagGroupCatalog.Serialize(parsed)).Select(g => (g.Name, g.IncludeGroupTag)));
    }

    [Fact]
    public void A_group_name_is_not_repeated_and_tags_with_structural_characters_are_dropped()
    {
        var options = Options("Family: a;b, ok, c}d, e{f, g\nfamily: other");
        Assert.Single(options.Groups);                                    // the second "family" is the same group
        Assert.Equal(["ok", "g"], options.Groups[0].Tags);                // ';' '{' '}' can't be inside a tag
        Assert.False(options.IsUsableTag("a;b"));
        Assert.True(options.IsUsableTag("sibling_1"));
    }

    [Fact]
    public void Bad_brackets_separator_and_format_fall_back_to_the_defaults()
    {
        var options = Options(open: "", close: "  ", sep: "", format: "{name}");
        Assert.Equal(("{", "}", ";", "{name} {tags}"), (options.Open, options.Close, options.Separator, options.Format));
        Assert.Equal("{name} {tags}", Options(format: "{tags}{tags} {name}").Format);   // each token exactly once
        Assert.Equal("{name} {tags}", Options(format: "{name}/{tags}").Format);          // no illegal characters
        Assert.Equal("{tags} {name}", Options(format: "{tags} {name}").Format);
    }

    // ------------------------------------------------------------------ the example of the issue

    [Fact]
    public void The_example_of_the_issue_with_a_prefix_character()
    {
        var options = Options(prefix: "#");
        var name = TagFileName.AddTags("DCIM00078.jpg", ["parents", "mom", "sibling_1"], options);
        Assert.Equal("DCIM00078 {#parents;#mom;#sibling_1}.jpg", name);
        Assert.Equal(["parents", "mom", "sibling_1"], TagFileName.GetTags(name, options));   // the prefix is not part of the tag
    }

    [Fact]
    public void Without_a_prefix_tags_are_written_plainly()
    {
        Assert.Equal("DCIM0078 {parents;mom}.jpg", TagFileName.AddTags("DCIM0078.jpg", ["parents", "mom"], Options()));
    }

    // ------------------------------------------------------------------ adding and removing

    [Fact]
    public void Tags_are_inserted_in_the_configured_order_not_the_order_they_are_added()
    {
        var options = Options();
        var name = TagFileName.AddTags("a.jpg", ["dad"], options);
        name = TagFileName.AddTags(name, ["parents"], options);                 // earlier in the list: goes in front of dad
        name = TagFileName.AddTags(name, ["carl"], options);                    // a later group: after the family
        name = TagFileName.AddTags(name, ["mom"], options);                     // between parents and dad
        Assert.Equal("a {parents;mom;dad;carl}.jpg", name);
        Assert.Equal("a {parents;dad;anna;carl}.jpg", TagFileName.Edit("a {parents;mom;dad;carl}.jpg", ["anna"], ["mom"], options));
    }

    [Fact]
    public void A_tag_is_never_written_twice_and_case_does_not_matter()
    {
        var options = Options();
        var once = TagFileName.AddTags("a.jpg", ["mom"], options);
        Assert.Equal(once, TagFileName.AddTags(once, ["mom"], options));
        Assert.Equal(once, TagFileName.AddTags(once, ["MOM", "Mom"], options));
        Assert.Equal("a {mom}.jpg", TagFileName.AddTags("a.jpg", ["MOM", "mom"], options));   // the group's spelling wins
        Assert.Equal("a.jpg", TagFileName.RemoveTags(once, ["MoM"], options));
    }

    [Fact]
    public void Removing_the_last_tag_removes_the_whole_block()
    {
        var options = Options();
        Assert.Equal("a.jpg", TagFileName.RemoveTags("a {mom}.jpg", ["mom"], options));
        Assert.Equal("a {dad}.jpg", TagFileName.RemoveTags("a {mom;dad}.jpg", ["mom"], options));
        Assert.Equal("a {mom}.jpg", TagFileName.RemoveTags("a {mom}.jpg", ["nobody"], options));
        Assert.Equal("a.jpg", TagFileName.RemoveTags("a.jpg", ["mom"], options));
    }

    [Fact]
    public void A_name_that_needs_no_change_comes_back_exactly_as_it_was()
    {
        var options = Options();
        // Unsorted block: adding what is already there must not reorder (and so rename) the file.
        Assert.Equal("a {dad;mom}.jpg", TagFileName.AddTags("a {dad;mom}.jpg", ["mom"], options));
        Assert.Equal("a.jpg", TagFileName.Edit("a.jpg", [], [], options));
    }

    [Fact]
    public void Tags_that_no_group_lists_are_kept_after_the_known_ones()
    {
        var options = Options();
        Assert.Equal("a {parents;dad;zebra;holiday}.jpg", TagFileName.AddTags("a {zebra;holiday;dad}.jpg", ["parents"], options));
        Assert.Equal("a {dad;zebra}.jpg", TagFileName.RemoveTags("a {dad;zebra;holiday}.jpg", ["holiday"], options));
    }

    [Fact]
    public void The_name_and_the_extension_are_left_alone()
    {
        var options = Options();
        Assert.Equal("my {trip} (1).tar {mom}.gz", TagFileName.AddTags("my {trip} (1).tar.gz", ["mom"], options));   // other braces in the name are not the tag block
        Assert.Equal("noext {mom}", TagFileName.AddTags("noext", ["mom"], options));
        Assert.Equal("a {mom}.JPG", TagFileName.AddTags("a.JPG", ["mom"], options));
        var parts = TagFileName.Parse("my trip {parents;mom}.gz", options);
        Assert.Equal(("my trip", ".gz"), (parts.BaseName, parts.Extension));
        Assert.Equal(["parents", "mom"], parts.Tags);
    }

    [Fact]
    public void Folders_have_no_extension()
    {
        var options = Options();
        Assert.Equal("Trip 2024.05 {mom}", TagFileName.AddTags("Trip 2024.05", ["mom"], options, isDirectory: true));
    }

    // ------------------------------------------------------------------ group tags

    [Fact]
    public void The_group_tag_is_added_in_front_of_its_tags_and_removed_with_the_last_one()
    {
        var options = Options("+Family: parents, mom, dad\nFriends: anna");
        var name = TagFileName.AddTags("a.jpg", ["mom"], options);
        Assert.Equal("a {Family;mom}.jpg", name);
        name = TagFileName.AddTags(name, ["anna", "parents"], options);
        Assert.Equal("a {Family;parents;mom;anna}.jpg", name);   // Friends has no group tag
        name = TagFileName.RemoveTags(name, ["mom"], options);
        Assert.Equal("a {Family;parents;anna}.jpg", name);        // another family tag is left: the group tag stays
        name = TagFileName.RemoveTags(name, ["parents"], options);
        Assert.Equal("a {anna}.jpg", name);                        // the last family tag took the group tag with it
    }

    [Fact]
    public void Group_tags_sort_with_their_group_when_groups_are_reordered()
    {
        var options = Options("Friends: anna\n+Family: mom");
        Assert.Equal("a {anna;Family;mom}.jpg", TagFileName.AddTags("a {Family;mom}.jpg", ["anna"], options));
    }

    // ------------------------------------------------------------------ other formats

    [Fact]
    public void Brackets_separator_and_format_are_configurable()
    {
        var options = Options(open: "[", close: "]", sep: ", ", format: "{tags} {name}");
        var name = TagFileName.AddTags("holiday.jpg", ["mom", "dad"], options);
        Assert.Equal("[mom, dad] holiday.jpg", name);
        Assert.Equal(["mom", "dad"], TagFileName.GetTags(name, options));
        Assert.Equal("[mom] holiday.jpg", TagFileName.RemoveTags(name, ["dad"], options));
        Assert.Equal("holiday.jpg", TagFileName.RemoveTags("[mom] holiday.jpg", ["mom"], options));

        var wrapped = Options(open: "((", close: "))", sep: "|+|", format: "{name}_{tags}_end");
        Assert.Equal("+", wrapped.Separator);   // "|" is not allowed in a file name, so it is dropped
        Assert.Equal("a_((mom+dad))_end.jpg", TagFileName.AddTags("a.jpg", ["mom", "dad"], wrapped));
        Assert.Equal(["mom", "dad"], TagFileName.GetTags("a_((mom+dad))_end.jpg", wrapped));
        Assert.Equal("a.jpg", TagFileName.RemoveTags("a_((mom))_end.jpg", ["mom"], wrapped));
        Assert.Equal("{", Options(open: "<", close: ">").Open);   // illegal characters never reach a file name
    }

    [Fact]
    public void Presence_counts_none_some_or_all_of_the_files()
    {
        var options = Options();
        string[] names = ["a {mom}.jpg", "b {mom;dad}.jpg", "c.jpg"];
        Assert.Equal(TagPresence.Some, TagFileName.PresenceIn(names, "mom", options));
        Assert.Equal(TagPresence.Some, TagFileName.PresenceIn(names, "dad", options));
        Assert.Equal(TagPresence.None, TagFileName.PresenceIn(names, "ben", options));
        Assert.Equal(TagPresence.All, TagFileName.PresenceIn(names[..2], "mom", options));
        Assert.Equal(TagPresence.None, TagFileName.PresenceIn([], "mom", options));
    }

    [Fact]
    public void Names_without_a_valid_block_have_no_tags()
    {
        var options = Options();
        Assert.Empty(TagFileName.GetTags("plain.jpg", options));
        Assert.Empty(TagFileName.GetTags("half {open.jpg", options));
        Assert.Empty(TagFileName.GetTags("empty {}.jpg", options));
    }

    // ------------------------------------------------------------------ settings

    [Fact]
    public void Tags_are_off_by_default_with_the_documented_defaults_and_the_state_matches_the_catalogue()
    {
        var state = new GlideSettingsState();
        Assert.False(state.TagsEnabled);
        Assert.True(state.TagsApplyImmediately);
        Assert.Equal(("", "{", "}", ";", "{name} {tags}"), (state.TagPrefix, state.TagOpen, state.TagClose, state.TagSeparator, state.TagFilenameFormat));
        Assert.Equal("", state.TagGroups);
        Assert.Equal("", state.BatchRenameHistory);
        foreach (var id in new[] { "tags.enabled", "tags.applyImmediately", "tags.groups", "tags.prefix", "tags.open", "tags.close", "tags.separator", "tags.format" })
            Assert.Contains(SettingsCatalog.All, s => s.Id == id && s.Category == "Window & navigation");
        Assert.Equal(false, SettingsCatalog.All.Single(s => s.Id == "tags.enabled").DefaultValue);
        Assert.Equal("{name} {tags}", SettingsCatalog.All.Single(s => s.Id == "tags.format").DefaultValue);
        Assert.Equal(0, TagOptions.From(state).Groups.Count);
    }
}
