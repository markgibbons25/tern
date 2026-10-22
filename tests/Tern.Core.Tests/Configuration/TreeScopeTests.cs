using Tern.Core.Configuration;
using Tern.Core.ItemFiles;
using Tern.Core.Items;
using Tern.Core.Planning;
using Tern.Core.Transfer;
using Tern.Core.Trees;
using Tern.Core.Validation;

namespace Tern.Core.Tests.Configuration;

/// <summary>
/// Every Include scope a module.json can declare, through each part that reads it: loading,
/// path matching, rule validation, file paths, the Plan, the transfer cover and validation.
/// </summary>
public sealed class TreeScopeTests : IDisposable
{
    private static readonly Guid Template = Guid.Parse("76036f5e-cbce-46d1-af0a-4143f9b557aa");
    private static readonly Guid Title = Guid.Parse("75577384-3c97-45da-a847-81b00500e250");
    private static readonly Guid Outside = Guid.Parse("99999999-0000-0000-0000-000000000000");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tern-scope-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static Guid G(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:D12}");

    private Include Include(TreeScope scope, params IncludeRule[] rules)
    {
        var include = new Include { Name = "test", Path = ItemPath.Parse("/sitecore/content/Site"), Scope = scope, Rules = rules, PhysicalRoot = Path.Combine(_root, "items", "test") };
        include.Module = new Module { Namespace = "Test", SourcePath = Path.Combine(_root, "test.module.json"), Includes = [include] };
        return include;
    }

    private static ItemMetadata M(int id, int parent, string path, string signature = "sig", bool hasChildren = true) =>
        new(G(id), parent == 0 ? Outside : G(parent), Template, "/sitecore/content/Site" + path, signature) { HasChildren = hasChildren };

    // Site       1
    //   A        2
    //     A1     3
    //       A1x  4
    //   B        5
    private static ItemMetadata[] Site(string signature = "sig") =>
    [
        M(1, 0, "", signature),
        M(2, 1, "/A", signature),
        M(3, 2, "/A/A1", signature),
        M(4, 3, "/A/A1/A1x", signature, hasChildren: false),
        M(5, 1, "/B", signature, hasChildren: false),
    ];

    // module.json

    [Theory]
    [InlineData("SingleItem", TreeScope.SingleItem)]
    [InlineData("ItemAndChildren", TreeScope.ItemAndChildren)]
    [InlineData("ItemAndDescendants", TreeScope.ItemAndDescendants)]
    [InlineData("DescendantsOnly", TreeScope.DescendantsOnly)]
    [InlineData("descendantsonly", TreeScope.DescendantsOnly)]
    [InlineData(null, TreeScope.ItemAndDescendants)]
    public void Loads_each_scope_from_a_module_file(string? scope, TreeScope expected)
    {
        var include = Assert.Single(LoadModule(scope).Includes);
        Assert.Equal(expected, include.Scope);
    }

    [Theory]
    [InlineData("ItemAndGrandchildren")]
    [InlineData("Ignored")] // valid in rules only
    public void Rejects_a_scope_an_include_cannot_have(string scope) =>
        Assert.Throws<ConfigurationException>(() => LoadModule(scope));

    private Module LoadModule(string? scope)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "sitecore.json"), """{ "modules": ["*.module.json"], "serialization": { "defaultModuleRelativeSerializationPath": "items" } }""");
        var scopeJson = scope is null ? string.Empty : $", \"scope\": \"{scope}\"";
        File.WriteAllText(Path.Combine(_root, "test.module.json"), $$"""{ "namespace": "Test", "items": { "includes": [ { "name": "test", "path": "/sitecore/content/Site"{{scopeJson}} } ] } }""");
        var root = ConfigurationLoader.LoadRoot(Path.Combine(_root, "sitecore.json"));
        return Assert.Single(ConfigurationLoader.LoadModules(root));
    }

    // Include.IncludesPath

    [Theory]
    [InlineData(TreeScope.SingleItem, true, false, false, false)]
    [InlineData(TreeScope.ItemAndChildren, true, true, false, false)]
    [InlineData(TreeScope.ItemAndDescendants, true, true, true, true)]
    [InlineData(TreeScope.DescendantsOnly, false, true, true, true)]
    public void Each_scope_covers_its_own_depths(TreeScope scope, bool root, bool child, bool grandchild, bool deeper)
    {
        var include = Include(scope);
        Assert.Equal(root, include.IncludesPath(ItemPath.Parse("/sitecore/content/Site")));
        Assert.Equal(child, include.IncludesPath(ItemPath.Parse("/sitecore/content/Site/A")));
        Assert.Equal(grandchild, include.IncludesPath(ItemPath.Parse("/sitecore/content/Site/A/A1")));
        Assert.Equal(deeper, include.IncludesPath(ItemPath.Parse("/sitecore/content/Site/A/A1/A1x")));
        Assert.False(include.IncludesPath(ItemPath.Parse("/sitecore/content")));
        Assert.False(include.IncludesPath(ItemPath.Parse("/sitecore/content/Site2")));
        Assert.False(include.IncludesPath(ItemPath.Parse("/sitecore/content/Other/A")));
    }

    [Fact]
    public void Paths_match_case_insensitively()
    {
        Assert.True(Include(TreeScope.DescendantsOnly).IncludesPath(ItemPath.Parse("/SITECORE/content/site/a")));
        Assert.False(Include(TreeScope.DescendantsOnly).IncludesPath(ItemPath.Parse("/SITECORE/content/site")));
    }

    [Theory]
    [InlineData("/sitecore/content/Site", false)]
    [InlineData("/sitecore/content/Site/A", true)]
    [InlineData("/sitecore/content/Site/Skip", false)]
    [InlineData("/sitecore/content/Site/Skip/Child", false)]
    [InlineData("/sitecore/content/Site/Only", true)]
    [InlineData("/sitecore/content/Site/Only/Child", false)]
    [InlineData("/sitecore/content/Site/Kids/Child", true)]
    [InlineData("/sitecore/content/Site/Kids/Child/Grandchild", false)]
    public void DescendantsOnly_applies_rules_below_the_root(string path, bool included)
    {
        var include = Include(TreeScope.DescendantsOnly,
            new IncludeRule { Path = "/Skip", Scope = RuleScope.Ignored },
            new IncludeRule { Path = "/Only", Scope = RuleScope.SingleItem },
            new IncludeRule { Path = "/Kids", Scope = RuleScope.ItemAndChildren });
        include.Validate();
        Assert.Equal(included, include.IncludesPath(ItemPath.Parse(path)));
    }

    [Fact]
    public void Push_operations_of_the_root_come_from_the_include_not_its_rules()
    {
        var include = new Include
        {
            Name = "test",
            Path = ItemPath.Parse("/sitecore/content/Site"),
            Scope = TreeScope.DescendantsOnly,
            PushOperations = PushOperations.CreateAndUpdate,
            Rules = [new IncludeRule { Path = "/A", Scope = RuleScope.ItemAndDescendants, PushOperations = PushOperations.CreateOnly }],
        };
        Assert.Equal(PushOperations.CreateAndUpdate, include.PushOperationsFor(ItemPath.Parse("/sitecore/content/Site")));
        Assert.Equal(PushOperations.CreateOnly, include.PushOperationsFor(ItemPath.Parse("/sitecore/content/Site/A/A1")));
        Assert.Equal(PushOperations.CreateAndUpdate, include.PushOperationsFor(ItemPath.Parse("/sitecore/content/Site/B")));
    }

    // Include.Validate

    [Theory]
    [InlineData(TreeScope.SingleItem, RuleScope.Ignored, false)]
    [InlineData(TreeScope.SingleItem, RuleScope.SingleItem, false)]
    [InlineData(TreeScope.ItemAndChildren, RuleScope.Ignored, true)]
    [InlineData(TreeScope.ItemAndChildren, RuleScope.SingleItem, true)]
    [InlineData(TreeScope.ItemAndChildren, RuleScope.ItemAndChildren, false)]
    [InlineData(TreeScope.ItemAndChildren, RuleScope.ItemAndDescendants, false)]
    [InlineData(TreeScope.ItemAndDescendants, RuleScope.Ignored, true)]
    [InlineData(TreeScope.ItemAndDescendants, RuleScope.SingleItem, true)]
    [InlineData(TreeScope.ItemAndDescendants, RuleScope.ItemAndChildren, true)]
    [InlineData(TreeScope.ItemAndDescendants, RuleScope.ItemAndDescendants, true)]
    [InlineData(TreeScope.DescendantsOnly, RuleScope.Ignored, true)]
    [InlineData(TreeScope.DescendantsOnly, RuleScope.SingleItem, true)]
    [InlineData(TreeScope.DescendantsOnly, RuleScope.ItemAndChildren, true)]
    [InlineData(TreeScope.DescendantsOnly, RuleScope.ItemAndDescendants, true)]
    public void Validates_rule_scopes_against_the_include_scope(TreeScope scope, RuleScope ruleScope, bool valid)
    {
        var include = Include(scope, new IncludeRule { Path = "/A", Scope = ruleScope });
        if (valid)
        {
            include.Validate();
        }
        else
        {
            Assert.Throws<ConfigurationException>(include.Validate);
        }
    }

    [Theory]
    [InlineData(TreeScope.ItemAndChildren)]
    [InlineData(TreeScope.ItemAndDescendants)]
    [InlineData(TreeScope.DescendantsOnly)]
    public void A_rule_below_an_ignored_ancestor_is_rejected(TreeScope scope)
    {
        var include = Include(scope, new IncludeRule { Path = "/A", Scope = RuleScope.Ignored }, new IncludeRule { Path = "/A/A1", Scope = RuleScope.SingleItem });
        Assert.Throws<ConfigurationException>(include.Validate);
    }

    [Theory]
    [InlineData(TreeScope.ItemAndDescendants)]
    [InlineData(TreeScope.DescendantsOnly)]
    public void A_rule_below_an_included_ancestor_is_accepted(TreeScope scope) =>
        Include(scope, new IncludeRule { Path = "/A/A1", Scope = RuleScope.SingleItem }).Validate();

    // ItemFilePaths

    [Theory]
    [InlineData(TreeScope.SingleItem, "Site.yml", null, null)]
    [InlineData(TreeScope.ItemAndChildren, "Site.yml", "Site/A.yml", null)]
    [InlineData(TreeScope.ItemAndDescendants, "Site.yml", "Site/A.yml", "Site/A/A1.yml")]
    [InlineData(TreeScope.DescendantsOnly, null, "Site/A.yml", "Site/A/A1.yml")]
    public void Maps_each_scope_to_its_files(TreeScope scope, string? root, string? child, string? grandchild)
    {
        var include = Include(scope);
        var paths = new ItemFilePaths(include);
        string? Expected(string? relative) => relative is null ? null : Path.Combine(include.PhysicalRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        Assert.Equal(Expected(root), paths.FileFor(ItemPath.Parse("/sitecore/content/Site")));
        Assert.Equal(Expected(child), paths.FileFor(ItemPath.Parse("/sitecore/content/Site/A")));
        Assert.Equal(Expected(grandchild), paths.FileFor(ItemPath.Parse("/sitecore/content/Site/A/A1")));
    }

    // Planner

    private static async Task<List<string>> Plan(IReadOnlyList<ItemMetadata> source, IReadOnlyList<ItemMetadata> target, Include include, bool push = false, bool force = false)
    {
        var planner = new Planner(new MemoryTree(source), new MemoryTree(target), push, force);
        return (await planner.PlanIncludeAsync(include, [], CancellationToken.None)).Operations.Select(o => o.ToString()!).ToList();
    }

    [Theory]
    [InlineData(TreeScope.SingleItem)]
    [InlineData(TreeScope.ItemAndChildren)]
    [InlineData(TreeScope.ItemAndDescendants)]
    [InlineData(TreeScope.DescendantsOnly)]
    public async Task Identical_trees_need_nothing_in_any_scope(TreeScope scope) =>
        Assert.Empty(await Plan(Site(), Site(), Include(scope)));

    public static TheoryData<TreeScope, string[]> ChangesByScope => new()
    {
        { TreeScope.SingleItem, ["Update /sitecore/content/Site"] },
        { TreeScope.ItemAndChildren, ["Create /sitecore/content/Site/B", "Update /sitecore/content/Site", "Update /sitecore/content/Site/A", "Delete /sitecore/content/Site/C"] },
        {
            TreeScope.ItemAndDescendants,
            [
                "Create /sitecore/content/Site/B", "Update /sitecore/content/Site", "Update /sitecore/content/Site/A", "Update /sitecore/content/Site/A/A1", "Update /sitecore/content/Site/A/A1/A1x",
                "Delete /sitecore/content/Site/C", "Delete /sitecore/content/Site/A/A1/A1y",
            ]
        },
        {
            TreeScope.DescendantsOnly,
            [
                "Create /sitecore/content/Site/B", "Update /sitecore/content/Site/A", "Update /sitecore/content/Site/A/A1", "Update /sitecore/content/Site/A/A1/A1x",
                "Delete /sitecore/content/Site/A/A1/A1y", "Delete /sitecore/content/Site/C",
            ]
        },
    };

    [Theory]
    [MemberData(nameof(ChangesByScope))]
    public async Task Plans_changes_only_at_the_depths_the_scope_covers(TreeScope scope, string[] expected)
    {
        // Every item changed; B is new; C (a child) and A1y (a great-grandchild) are gone from the source.
        var source = Site("new");
        var target = Site("old").Where(m => m.Id != G(5)).Concat([M(6, 1, "/C", "old"), M(7, 3, "/A/A1/A1y", "old")]).ToList();
        Assert.Equal(expected, await Plan(source, target, Include(scope)));
    }

    [Fact]
    public async Task DescendantsOnly_never_creates_or_deletes_the_root()
    {
        var include = Include(TreeScope.DescendantsOnly);
        // Missing from the target: its children are created, the root is not.
        Assert.Equal(
            ["Create /sitecore/content/Site/A", "Create /sitecore/content/Site/B", "Create /sitecore/content/Site/A/A1", "Create /sitecore/content/Site/A/A1/A1x"],
            await Plan(Site(), [], include));
        // Missing from the source while it has children: only the children go.
        Assert.Equal(
            ["Delete /sitecore/content/Site/A", "Delete /sitecore/content/Site/B"],
            await Plan([M(1, 0, "")], Site(), include));
    }

    [Fact]
    public async Task DescendantsOnly_works_from_a_source_without_the_root()
    {
        // Item files of a DescendantsOnly include never hold the root, and a CM fetch drops it.
        var include = Include(TreeScope.DescendantsOnly);
        Assert.Empty(await Plan(Site()[1..], Site(), include));
        Assert.Empty(await Plan(Site(), Site()[1..], include));
        Assert.Equal(["Update /sitecore/content/Site/B"], await Plan(Site()[1..], Site()[1..4].Append(M(5, 1, "/B", "old")).ToList(), include));
    }

    [Fact]
    public async Task DescendantsOnly_finds_a_child_moved_from_below_another_child()
    {
        var include = Include(TreeScope.DescendantsOnly);
        var source = new[] { M(1, 0, ""), M(2, 1, "/A"), M(3, 1, "/A1") };
        var target = new[] { M(1, 0, ""), M(2, 1, "/A"), M(3, 2, "/A/A1") };
        Assert.Equal(["Move /sitecore/content/Site/A/A1 -> /sitecore/content/Site/A1"], await Plan(source, target, include));
        Assert.Equal(["Move /sitecore/content/Site/A1 -> /sitecore/content/Site/A/A1"], await Plan(target, source, include));
    }

    [Fact]
    public async Task DescendantsOnly_honours_push_operations_at_the_top_level()
    {
        var target = Site("old").Append(M(6, 1, "/C", "old")).ToList();
        var noDelete = new Include { Name = "test", Path = ItemPath.Parse("/sitecore/content/Site"), Scope = TreeScope.DescendantsOnly, PushOperations = PushOperations.CreateAndUpdate };
        noDelete.Module = new Module { Namespace = "Test", SourcePath = "/x/test.module.json", Includes = [noDelete] };
        Assert.DoesNotContain(await Plan(Site("new"), target, noDelete, push: true), o => o.StartsWith("Delete", StringComparison.Ordinal));
        Assert.Contains("Delete /sitecore/content/Site/C", await Plan(Site("new"), target, noDelete, push: false));
    }

    [Theory]
    [InlineData(TreeScope.SingleItem)]
    [InlineData(TreeScope.ItemAndChildren)]
    [InlineData(TreeScope.ItemAndDescendants)]
    public async Task A_missing_root_is_an_error_without_force(TreeScope scope)
    {
        await Assert.ThrowsAsync<SourceMissingException>(() => Plan([], Site(), Include(scope)));
        Assert.Empty(await Plan([], [], Include(scope), force: true));
    }

    [Theory]
    [InlineData(TreeScope.ItemAndChildren)]
    [InlineData(TreeScope.ItemAndDescendants)]
    [InlineData(TreeScope.DescendantsOnly)]
    public async Task Siblings_with_the_same_name_are_rejected(TreeScope scope)
    {
        var source = new[] { M(1, 0, ""), M(2, 1, "/A"), M(3, 1, "/a") };
        await Assert.ThrowsAsync<NonUniquePathException>(() => Plan(source, [], Include(scope)));
    }

    // TransferCover

    private static List<string> Cover(Include include, IReadOnlyList<ItemMetadata> items) =>
        TransferCover.Cover(include, items, MergeStrategy.OverrideExistingItem).Select(t => $"{t.Path} {t.Scope} {t.ItemCount}").ToList();

    [Fact]
    public void Transfers_each_scope_as_the_fewest_trees()
    {
        var children = Site().Where(m => m.ItemPath.Count <= 4).ToList();
        Assert.Equal(["/sitecore/content/Site SingleItem 1"], Cover(Include(TreeScope.SingleItem), Site()[..1]));
        Assert.Equal(
            ["/sitecore/content/Site SingleItem 1", "/sitecore/content/Site/A SingleItem 1", "/sitecore/content/Site/B ItemAndDescendants 1"],
            Cover(Include(TreeScope.ItemAndChildren), children));
        Assert.Equal(["/sitecore/content/Site ItemAndDescendants 5"], Cover(Include(TreeScope.ItemAndDescendants), Site()));
        Assert.Equal(
            ["/sitecore/content/Site/A ItemAndDescendants 3", "/sitecore/content/Site/B ItemAndDescendants 1"],
            Cover(Include(TreeScope.DescendantsOnly), Site()[1..]));
    }

    [Fact]
    public void DescendantsOnly_transfers_the_same_with_or_without_the_root() =>
        Assert.Equal(Cover(Include(TreeScope.DescendantsOnly), Site()[1..]), Cover(Include(TreeScope.DescendantsOnly), Site()));

    [Fact]
    public void DescendantsOnly_splits_around_an_ignored_rule()
    {
        var include = Include(TreeScope.DescendantsOnly, new IncludeRule { Path = "/A/A1", Scope = RuleScope.Ignored });
        Assert.Equal(
            ["/sitecore/content/Site/A SingleItem 1", "/sitecore/content/Site/B ItemAndDescendants 1"],
            Cover(include, Site()[1..]));
    }

    [Fact]
    public void DescendantsOnly_with_no_children_transfers_nothing() =>
        Assert.Empty(Cover(Include(TreeScope.DescendantsOnly), Site()[..1]));

    // Item files: a pull written by FileApplier reads back, validates and plans clean.

    private static ItemData Item(ItemMetadata m) => new() { Id = m.Id, ParentId = m.ParentId, TemplateId = m.TemplateId, Path = m.Path };

    private static FileItemTree Pull(Include include, IReadOnlyList<ItemMetadata> items)
    {
        var files = new FileItemTree(_ => true);
        var data = items.Where(m => include.IncludesPath(m.ItemPath)).Select(Item).ToList();
        foreach (var d in data)
        {
            d.Languages.GetOrAdd("en").Versions.GetOrAdd(1).Fields.Add(new FieldValue(Title, "Title", null, d.Path));
        }
        var ops = data.Select(i => new PlanOperation { Action = PlanAction.Create, Module = "Test", Include = "test", Database = "master", Id = i.Id, Path = i.Path, ParentId = i.ParentId }).ToList();
        new FileApplier(files, _ => true).Apply(include, ops, data.ToDictionary(i => i.Id), checkPreconditions: false);
        files.Invalidate(include);
        return files;
    }

    [Theory]
    [InlineData(TreeScope.SingleItem, 1)]
    [InlineData(TreeScope.ItemAndChildren, 3)]
    [InlineData(TreeScope.ItemAndDescendants, 5)]
    [InlineData(TreeScope.DescendantsOnly, 4)]
    public void A_pull_of_each_scope_validates_clean(TreeScope scope, int count)
    {
        var include = Include(scope);
        var files = Pull(include, Site());
        Assert.Equal(count, files.Files(include).Count);
        Assert.Empty(FileValidator.Validate([include], files));
    }

    [Fact]
    public void DescendantsOnly_writes_no_file_for_the_root()
    {
        var include = Include(TreeScope.DescendantsOnly);
        var files = Pull(include, Site());
        Assert.DoesNotContain(files.Files(include), f => f.Id == G(1));
        Assert.False(File.Exists(Path.Combine(include.PhysicalRoot, "Site.yml")));
        Assert.True(File.Exists(Path.Combine(include.PhysicalRoot, "Site", "A.yml")));
    }

    [Theory]
    [InlineData(TreeScope.SingleItem)]
    [InlineData(TreeScope.ItemAndChildren)]
    [InlineData(TreeScope.ItemAndDescendants)]
    [InlineData(TreeScope.DescendantsOnly)]
    public async Task A_pulled_scope_plans_nothing_against_its_source(TreeScope scope)
    {
        var include = Include(scope);
        var files = Pull(include, Site());
        var cm = new MemoryTree(Site().Select(m => m with { Signature = files.Files(include).FirstOrDefault(f => f.Id == m.Id)?.Signature ?? "root" }).ToList());
        Assert.Empty((await new Planner(cm, files, false, false).PlanIncludeAsync(include, [], CancellationToken.None)).Operations);
        Assert.Empty((await new Planner(files, cm, true, false).PlanIncludeAsync(include, [], CancellationToken.None)).Operations);
    }

    [Theory]
    [InlineData(TreeScope.SingleItem, "/A")]
    [InlineData(TreeScope.ItemAndChildren, "/A/A1")]
    public void A_file_beyond_the_scope_is_not_included(TreeScope scope, string path)
    {
        var include = Include(scope);
        Pull(Include(TreeScope.ItemAndDescendants), Site());
        var issues = FileValidator.Validate([include], new FileItemTree(_ => true));
        Assert.Contains(issues, i => i.Kind == IssueKind.NotIncluded && i.Message.StartsWith("/sitecore/content/Site" + path + " ", StringComparison.Ordinal));
    }

    [Fact]
    public void A_root_file_in_a_DescendantsOnly_folder_is_not_included()
    {
        Pull(Include(TreeScope.ItemAndDescendants), Site());
        var issues = FileValidator.Validate([Include(TreeScope.DescendantsOnly)], new FileItemTree(_ => true));
        var issue = Assert.Single(issues);
        Assert.Equal(IssueKind.NotIncluded, issue.Kind);
        Assert.EndsWith("Site.yml", issue.File);
    }

    /// <summary>An item tree held in memory, standing in for both the files and a CM.</summary>
    private sealed class MemoryTree(IReadOnlyList<ItemMetadata> items) : IItemTree
    {
        public string Description => "memory";

        public Task<IReadOnlyList<ItemMetadata>> GetMetadataAsync(Include include, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ItemMetadata>>(items.Where(i => i.ItemPath.IsDescendantOrSelfOf(include.Path)).ToList());

        public Task<IReadOnlyList<ItemMetadata>> GetByIdsAsync(Include include, IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ItemMetadata>>(items.Where(i => ids.Contains(i.Id)).ToList());

        public Task<IReadOnlyList<ItemMetadata>> GetChildrenAsync(Include include, IReadOnlyCollection<Guid> parentIds, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ItemMetadata>>(items.Where(i => parentIds.Contains(i.ParentId)).ToList());

        public Task<IReadOnlyDictionary<Guid, ItemData>> GetItemsAsync(Include include, IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<Guid, ItemData>>(new Dictionary<Guid, ItemData>());
    }
}
