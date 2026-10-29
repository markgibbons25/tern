using Tern.Core.Configuration;
using Tern.Core.ItemFiles;

namespace Tern.Core.Tests.Configuration;

public class ConfigurationTests
{
    private static string Project(string name) => Path.Join(AppContext.BaseDirectory, "fixtures", "push-basic", name);

    [Fact]
    public void Loads_modules_and_resolves_include_folders()
    {
        var root = ConfigurationLoader.LoadRoot(Path.Join(Project("s0"), "sitecore.json"));
        var modules = ConfigurationLoader.LoadModules(root);
        var include = Assert.Single(Assert.Single(modules).Includes);
        Assert.Equal(Path.Join(Project("s0"), "items", "tern-tests"), include.PhysicalRoot);
        Assert.Equal("/sitecore/system/Modules/Tern Tests", include.Path.ToString());
    }

    [Fact]
    public void Maps_item_paths_to_their_files()
    {
        var include = new Include { Name = "inc", Path = ItemPath.Parse("/sitecore/content/Site"), PhysicalRoot = "/root" };
        var paths = new ItemFilePaths(include);
        var sep = Path.DirectorySeparatorChar;
        Assert.Equal($"/root{sep}Site.yml", paths.FileFor(ItemPath.Parse("/sitecore/content/Site")));
        Assert.Equal($"/root{sep}Site{sep}a#b.yml", paths.FileFor(ItemPath.Parse("/sitecore/content/Site/a:b")));
        Assert.Equal($"/root{sep}Site{sep}#CON.yml", paths.FileFor(ItemPath.Parse("/sitecore/content/Site/CON")));
        Assert.Null(paths.FileFor(ItemPath.Parse("/sitecore/content/Other")));
    }

    [Fact]
    public void Shortens_long_paths_with_a_hash_folder()
    {
        var include = new Include { Name = "inc", Path = ItemPath.Parse("/a"), PhysicalRoot = "/root", MaxRelativePathLength = 20 };
        var file = new ItemFilePaths(include).FileFor(ItemPath.Parse("/a/bbbbbbbbbb/cccccccccc/dddddddddd"))!;
        var parts = file.Split(Path.DirectorySeparatorChar);
        // The nearest ancestor short enough to fit is replaced by a 16-character hash folder.
        Assert.Matches("^[0-9A-F]{16}$", parts[^3]);
        Assert.Equal("cccccccccc", parts[^2]);
        Assert.Equal("dddddddddd.yml", parts[^1]);
    }

    [Theory]
    [InlineData("/sitecore/content/Site", true)]
    [InlineData("/sitecore/content/Site/Keep/Deep", true)]
    [InlineData("/sitecore/content/Site/Skip", false)]
    [InlineData("/sitecore/content/Site/Skip/Child", false)]
    [InlineData("/sitecore/content/Site/Only/Child", true)]
    [InlineData("/sitecore/content/Site/Only/Child/Grandchild", false)]
    public void Applies_rules_first_match_wins(string path, bool included)
    {
        var include = new Include
        {
            Name = "inc",
            Path = ItemPath.Parse("/sitecore/content/Site"),
            Rules =
            [
                new IncludeRule { Path = "/Skip", Scope = RuleScope.Ignored },
                new IncludeRule { Path = "/Only", Scope = RuleScope.ItemAndChildren },
            ],
        };
        Assert.Equal(included, include.IncludesPath(ItemPath.Parse(path)));
    }

    [Theory]
    [InlineData("Global", "Glob*", true)]
    [InlineData("Global", "global", true)]
    [InlineData("Global", "Other", false)]
    public void Matches_module_filters_case_insensitively(string ns, string pattern, bool expected) =>
        Assert.Equal(expected, Wildcard.IsMatch(ns, pattern));

    [Theory]
    [InlineData("*{62DD1639-9F28-4040-8738-C886480B2127}*", true)]
    [InlineData("<r*/r>", true)]
    [InlineData("<r>?  <d />*", true)]
    [InlineData("*{00000000-0000-0000-0000-000000000000}*", false)]
    public void Matches_a_multi_line_value(string pattern, bool expected) =>
        Assert.Equal(expected, Wildcard.IsMatch("<r>\n  <d />\n  <r s:id=\"{62DD1639-9F28-4040-8738-C886480B2127}\" />\n</r>", pattern));

    [Theory]
    [InlineData("site-a-pages")]
    [InlineData("site-a.module.json")]
    [InlineData("site-a")]
    public void A_filter_naming_an_include_or_module_file_says_which_namespace_was_meant(string filter)
    {
        var include = new Include { Name = "site-a-pages", Path = ItemPath.Parse("/sitecore/content/Site A") };
        var module = new Module { Namespace = "Project.SiteA", SourcePath = "/x/site-a.module.json", Includes = [include] };
        include.Module = module;
        var ex = Assert.Throws<ConfigurationException>(() => ConfigurationLoader.Select([module], [filter], []));
        Assert.StartsWith($"The filter \"include: {filter}, exclude: \" did not match any defined serialization module configurations.", ex.Message);
        Assert.Contains("\"Project.SiteA\"", ex.Message);
    }

    [Fact]
    public void A_filter_matching_nothing_keeps_the_official_message()
    {
        var include = new Include { Name = "pages", Path = ItemPath.Parse("/sitecore/content/Site A") };
        var module = new Module { Namespace = "Project.SiteA", SourcePath = "/x/site-a.module.json", Includes = [include] };
        include.Module = module;
        var ex = Assert.Throws<ConfigurationException>(() => ConfigurationLoader.Select([module], ["Other"], []));
        Assert.EndsWith("Use the info command to see valid module names.", ex.Message);
    }
}
