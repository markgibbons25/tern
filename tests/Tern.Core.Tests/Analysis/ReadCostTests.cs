using Tern.Cli;
using Tern.Core.Analysis;
using Tern.Core.Configuration;
using Tern.Core.Tests.Remote;
using Tern.Core.Trees;

namespace Tern.Core.Tests.Analysis;

/// <summary>
/// An Include that reads its whole subtree from the CM and drops most of it with rules is found,
/// with the rules responsible, where their dropped items sit, and how to stop reading them.
/// </summary>
[Collection(nameof(ConsoleTests))]
public sealed class ReadCostTests : IDisposable
{
    private const string SitePath = "/sitecore/content/Tenant/Site";
    private static readonly Guid Template = Guid.Parse("76036f5e-cbce-46d1-af0a-4143f9b557aa");
    private static readonly Guid Outside = Guid.Parse("99999999-0000-0000-0000-000000000000");

    private readonly string _root = Path.Join(Path.GetTempPath(), "tern-readcost-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static Include Include(TreeScope scope, params IncludeRule[] rules)
    {
        var include = new Include { Name = "site", Path = ItemPath.Parse(SitePath), Scope = scope, Rules = rules };
        include.Module = new Module { Namespace = "Site", SourcePath = "site.module.json", Includes = [include] };
        include.Validate();
        return include;
    }

    private static IncludeRule Rule(string path, RuleScope scope) => new() { Path = path, Scope = scope };

    // The pattern of a young site's module: keep the site's structure, ignore the rest.
    private static Include CatchAll() => Include(TreeScope.ItemAndDescendants,
        Rule("/Home", RuleScope.SingleItem),
        Rule("/Data", RuleScope.ItemAndChildren),
        Rule("/Presentation", RuleScope.ItemAndDescendants),
        Rule("*", RuleScope.Ignored));

    /// <summary>
    /// Site, Home with <paramref name="pages"/> pages below it (each with a child), Data with two
    /// folders of three items each, Presentation with two items, and an Archive branch of four.
    /// </summary>
    private static List<ItemMetadata> Site(int pages)
    {
        var items = new List<ItemMetadata>();
        var next = 1;
        Guid Add(Guid parent, string path)
        {
            var id = Guid.Parse($"00000000-0000-0000-0000-{next++:D12}");
            items.Add(new ItemMetadata(id, parent, Template, SitePath + path, "sig"));
            return id;
        }
        var site = Add(Outside, "");
        var home = Add(site, "/Home");
        for (var p = 0; p < pages; p++)
        {
            var page = Add(home, $"/Home/Page {p}");
            Add(page, $"/Home/Page {p}/Detail");
        }
        var data = Add(site, "/Data");
        foreach (var folder in new[] { "Cards", "Banners" })
        {
            var f = Add(data, $"/Data/{folder}");
            for (var n = 0; n < 3; n++)
            {
                Add(f, $"/Data/{folder}/Item {n}");
            }
        }
        var presentation = Add(site, "/Presentation");
        Add(presentation, "/Presentation/Styles");
        Add(presentation, "/Presentation/Partials");
        var archive = Add(site, "/Archive");
        for (var n = 0; n < 4; n++)
        {
            Add(archive, $"/Archive/Old {n}");
        }
        return items;
    }

    [Fact]
    public void A_catch_all_ignore_reads_everything_to_keep_what_earlier_rules_name()
    {
        var include = CatchAll();
        var items = Site(pages: 600);
        var cost = ReadCost.Measure(include, items);

        // Site, Home, Data and its two folders, Presentation and its two items.
        Assert.Equal(items.Count, cost.Read);
        Assert.Equal(8, cost.Kept);
        Assert.Equal(ReadCost.CountKept(include, items), cost.Kept);
        Assert.True(cost.OverReads);

        Assert.Collection(cost.Dropped,
            home =>
            {
                Assert.Equal("/Home", home.Rule!.Path);
                Assert.Equal(1_200, home.Items);
                var place = Assert.Single(home.Places);
                Assert.Equal(SitePath + "/Home", place.Below);
                Assert.Contains($"one for {SitePath}/Home with scope SingleItem", home.Advice);
            },
            data =>
            {
                Assert.Equal("/Data", data.Rule!.Path);
                Assert.Equal(6, data.Items);
                Assert.Equal([(SitePath + "/Data/Banners", 3L), (SitePath + "/Data/Cards", 3L)], data.Places.Select(p => (p.Below, p.Items)));
                Assert.Contains("with scope ItemAndChildren", data.Advice);
            },
            archive =>
            {
                Assert.Equal("*", archive.Rule!.Path);
                Assert.Equal(5, archive.Items);
                Assert.Equal(SitePath, Assert.Single(archive.Places).Below);
                Assert.Contains("one for each rule before it", archive.Advice);
            });
        Assert.Equal(cost.DroppedItems, cost.Dropped.Sum(d => d.Items));
    }

    [Fact]
    public void A_few_dropped_items_are_not_worth_reporting()
    {
        var cost = ReadCost.Measure(CatchAll(), Site(pages: 10));
        Assert.Equal(31, cost.DroppedItems);
        Assert.False(cost.OverReads);
    }

    [Theory]
    [InlineData(999, 10, false)]
    [InlineData(1_010, 10, true)]
    [InlineData(3_000, 1_500, false)]
    [InlineData(3_000, 1_499, true)]
    public void Over_reading_needs_many_dropped_items_and_more_than_are_kept(long read, long kept, bool overReads) =>
        Assert.Equal(overReads, ReadCost.OverReads(read, kept));

    [Fact]
    public void Includes_rooted_at_the_kept_branches_read_only_what_they_keep()
    {
        var items = Site(pages: 600);
        IReadOnlyList<ItemMetadata> Below(string path, TreeScope scope) => items.Where(m =>
        {
            var relative = m.ItemPath.RelativeTo(ItemPath.Parse(SitePath + path));
            return relative is not null && (scope == TreeScope.ItemAndDescendants || relative.Count <= (scope == TreeScope.ItemAndChildren ? 1 : 0));
        }).ToList();
        Include At(string path, TreeScope scope) => new() { Name = path, Path = ItemPath.Parse(SitePath + path), Scope = scope };

        foreach (var (path, scope) in new[] { ("", TreeScope.SingleItem), ("/Home", TreeScope.SingleItem), ("/Data", TreeScope.ItemAndChildren), ("/Presentation", TreeScope.ItemAndDescendants) })
        {
            var cost = ReadCost.Measure(At(path, scope), Below(path, scope));
            Assert.Equal(0, cost.DroppedItems);
            Assert.Empty(cost.Dropped);
        }
    }

    [Fact]
    public void The_configuration_alone_names_the_rules_that_drop_items_below_them()
    {
        var rules = ReadCost.Inspect(CatchAll());
        Assert.Equal(["/Home", "/Data", "*"], rules.Select(r => r.Rule.Path));
        Assert.Contains($"Rule /Home keeps only {SitePath}/Home", rules[0].Advice);
    }

    [Fact]
    public void An_ignored_branch_is_named_with_its_full_path()
    {
        var advice = Assert.Single(ReadCost.Inspect(Include(TreeScope.ItemAndDescendants, Rule("/Archive", RuleScope.Ignored))));
        Assert.Contains($"drops {SitePath}/Archive and everything below it", advice.Advice);
        Assert.Contains($"give {SitePath}/Archive none", advice.Advice);
    }

    [Fact]
    public void Rules_of_an_include_that_reads_only_children_cost_nothing()
    {
        Assert.Empty(ReadCost.Inspect(Include(TreeScope.ItemAndChildren, Rule("/Archive", RuleScope.Ignored), Rule("/Home", RuleScope.SingleItem))));
        Assert.Empty(ReadCost.Inspect(Include(TreeScope.ItemAndDescendants, Rule("/Presentation", RuleScope.ItemAndDescendants))));
    }

    [Fact]
    public async Task Ser_analyze_offline_lists_the_rules_from_the_module_files()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Join(_root, "sitecore.json"), """{ "modules": ["*.module.json"] }""", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Join(_root, "site.module.json"), $$"""
            {
              "namespace": "Site",
              "items": { "includes": [
                { "name": "site", "path": "{{SitePath}}", "scope": "ItemAndDescendants", "rules": [
                  { "path": "/Home", "scope": "SingleItem" },
                  { "path": "*", "scope": "Ignored" } ] },
                { "name": "settings", "path": "/sitecore/system/Settings/Tenant", "scope": "ItemAndChildren", "rules": [
                  { "path": "/Standard Values", "scope": "Ignored" } ] }
              ] }
            }
            """, TestContext.Current.CancellationToken);
        var (originalOut, originalError) = (Console.Out, Console.Error);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Console.SetOut(output);
        Console.SetError(error);
        int code;
        try
        {
            code = await TernApp.RunAsync(["ser", "analyze", "--offline", "-c", _root]);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }

        Assert.Equal(0, code);
        var text = output.ToString();
        Assert.Contains($"[Site] site ({SitePath}, ItemAndDescendants):", text);
        Assert.Contains("Rule /Home keeps only", text);
        Assert.Contains("Rule * drops everything", text);
        Assert.DoesNotContain("settings", text);
        Assert.DoesNotContain("Standard Values", text);
        Assert.Contains("1 include has rules", text);
    }
}
