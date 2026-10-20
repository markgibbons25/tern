using Tern.Core.Configuration;
using Tern.Core.ItemFiles;

namespace Tern.Core.Tests.Configuration;

public class ConfigurationTests
{
    private static string Project(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", "push-basic", name);

    [Fact]
    public void Loads_modules_and_resolves_include_folders()
    {
        var root = ConfigurationLoader.LoadRoot(Path.Combine(Project("s0"), "sitecore.json"));
        var modules = ConfigurationLoader.LoadModules(root);
        var include = Assert.Single(Assert.Single(modules).Includes);
        Assert.Equal(Path.Combine(Project("s0"), "items", "tern-tests"), include.PhysicalRoot);
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
}
