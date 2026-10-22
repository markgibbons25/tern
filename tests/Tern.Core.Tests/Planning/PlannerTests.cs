using Tern.Core.Configuration;
using Tern.Core.Items;
using Tern.Core.Planning;
using Tern.Core.Trees;

namespace Tern.Core.Tests.Planning;

public class PlannerTests
{
    private static readonly Guid Template = Guid.Parse("76036f5e-cbce-46d1-af0a-4143f9b557aa");
    private static readonly Guid Outside = Guid.Parse("99999999-0000-0000-0000-000000000000");

    private static Include Include(TreeScope scope = TreeScope.ItemAndDescendants, PushOperations ops = PushOperations.CreateUpdateAndDelete, params IncludeRule[] rules)
    {
        var include = new Include { Name = "test", Path = ItemPath.Parse("/sitecore/content/Site"), Scope = scope, PushOperations = ops, Rules = rules };
        include.Module = new Module { Namespace = "Test", SourcePath = "/x/test.module.json", Includes = [include] };
        return include;
    }

    private static ItemMetadata M(int id, int parent, string path, string signature = "sig") =>
        new(G(id), parent == 0 ? Outside : G(parent), Template, path, signature);

    private static Guid G(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:D12}");

    private static async Task<List<PlanOperation>> Plan(MemoryTree source, MemoryTree target, Include include, bool push = false)
    {
        var planner = new Planner(source, target, push, force: false);
        return (await planner.PlanIncludeAsync(include, [], CancellationToken.None)).Operations;
    }

    [Fact]
    public async Task Identical_trees_need_nothing()
    {
        var items = new[] { M(1, 0, "/sitecore/content/Site"), M(2, 1, "/sitecore/content/Site/A") };
        Assert.Empty(await Plan(new MemoryTree(items), new MemoryTree(items), Include()));
    }

    [Fact]
    public async Task Finds_creates_updates_and_orphans()
    {
        var source = new MemoryTree([M(1, 0, "/sitecore/content/Site"), M(2, 1, "/sitecore/content/Site/A", "new"), M(3, 1, "/sitecore/content/Site/B")]);
        var target = new MemoryTree([M(1, 0, "/sitecore/content/Site"), M(2, 1, "/sitecore/content/Site/A", "old"), M(4, 1, "/sitecore/content/Site/C")]);
        var ops = await Plan(source, target, Include());
        Assert.Equal(["Create /sitecore/content/Site/B", "Update /sitecore/content/Site/A", "Delete /sitecore/content/Site/C"], ops.Select(o => o.ToString()));
    }

    [Fact]
    public async Task Recognises_a_renamed_root_by_id_even_outside_the_include_path()
    {
        // The CM renamed the root, so nothing exists at the Include's path; the item is found by ID.
        var source = new MemoryTree([M(1, 0, "/sitecore/content/Site"), M(2, 1, "/sitecore/content/Site/A")]);
        var target = new MemoryTree([M(1, 0, "/sitecore/content/Renamed"), M(2, 1, "/sitecore/content/Renamed/A"), M(3, 1, "/sitecore/content/Renamed/Extra")]);
        var ops = await Plan(source, target, Include());
        Assert.Contains(ops, o => o.Action == PlanAction.Rename && o.Id == G(1));
        Assert.DoesNotContain(ops, o => o.Action == PlanAction.Create);
        // Outside the Include, every unmatched child goes, whatever the rules say.
        Assert.Contains(ops, o => o.Action == PlanAction.Delete && o.Id == G(3));
    }

    [Fact]
    public async Task Detects_moves_and_keeps_the_moved_item_out_of_the_deletes()
    {
        var source = new MemoryTree([M(1, 0, "/sitecore/content/Site"), M(2, 1, "/sitecore/content/Site/A"), M(3, 1, "/sitecore/content/Site/X")]);
        var target = new MemoryTree([M(1, 0, "/sitecore/content/Site"), M(2, 1, "/sitecore/content/Site/A"), M(3, 2, "/sitecore/content/Site/A/X")]);
        var ops = await Plan(source, target, Include());
        Assert.Equal(["Move /sitecore/content/Site/A/X -> /sitecore/content/Site/X"], ops.Select(o => o.ToString()));
    }

    [Fact]
    public async Task Push_operations_limit_a_push_but_not_a_pull()
    {
        var source = new MemoryTree([M(1, 0, "/sitecore/content/Site"), M(2, 1, "/sitecore/content/Site/A", "new")]);
        var target = new MemoryTree([M(1, 0, "/sitecore/content/Site"), M(2, 1, "/sitecore/content/Site/A", "old"), M(3, 1, "/sitecore/content/Site/C")]);
        var createOnly = Include(ops: PushOperations.CreateOnly);
        Assert.Empty(await Plan(source, target, createOnly, push: true));
        Assert.Equal(2, (await Plan(source, target, createOnly, push: false)).Count);
        var noDelete = Include(ops: PushOperations.CreateAndUpdate);
        Assert.Equal(["Update /sitecore/content/Site/A"], (await Plan(source, target, noDelete, push: true)).Select(o => o.ToString()));
    }

    [Fact]
    public async Task Ignored_rules_hide_items_on_both_sides()
    {
        var include = Include(rules: new IncludeRule { Path = "/Skip", Scope = RuleScope.Ignored });
        var source = new MemoryTree([M(1, 0, "/sitecore/content/Site")]);
        var target = new MemoryTree([M(1, 0, "/sitecore/content/Site"), M(2, 1, "/sitecore/content/Site/Skip")]);
        Assert.Empty(await Plan(source, target, include));
    }

    [Fact]
    public async Task A_missing_root_is_an_error_without_force()
    {
        var planner = new Planner(new MemoryTree([]), new MemoryTree([]), false, force: false);
        await Assert.ThrowsAsync<SourceMissingException>(() => planner.PlanIncludeAsync(Include(), [], CancellationToken.None));
    }

    [Fact]
    public async Task Rejects_siblings_with_the_same_name()
    {
        var source = new MemoryTree([M(1, 0, "/sitecore/content/Site"), M(2, 1, "/sitecore/content/Site/A"), M(3, 1, "/sitecore/content/Site/a")]);
        var planner = new Planner(source, new MemoryTree([]), false, force: false);
        await Assert.ThrowsAsync<NonUniquePathException>(() => planner.PlanIncludeAsync(Include(), [], CancellationToken.None));
    }

    [Fact]
    public async Task Deletes_a_target_item_with_another_id_at_the_same_path_before_creating()
    {
        // The Official CLI stops here with "Non-unique paths cannot be serialized"; content sync
        // scripts deleted the target item by hand and pushed again.
        var source = new MemoryTree([M(1, 0, "/sitecore/content/Site"), M(2, 1, "/sitecore/content/Site/Page")]);
        var target = new MemoryTree([M(1, 0, "/sitecore/content/Site"), M(9, 1, "/sitecore/content/Site/Page")]);
        var ops = await Plan(source, target, Include(), push: true);
        Assert.Equal(["Delete /sitecore/content/Site/Page", "Create /sitecore/content/Site/Page"], ops.Select(o => o.ToString()));
        Assert.Equal([G(9), G(2)], ops.Select(o => o.Id));
    }

    private static async Task<List<PlanOperation>> PlanItems(MemoryTree source, MemoryTree target, Include include, int[] ids, params int[] subtrees)
    {
        var planner = new Planner(source, target, applyPushOperations: false, force: true);
        return (await planner.PlanItemsAsync(include, ids.Select(G).ToList(), subtrees.Select(G).ToHashSet(), CancellationToken.None)).Operations;
    }

    [Fact]
    public async Task Planning_named_items_leaves_the_rest_of_the_include_alone()
    {
        var source = new MemoryTree([M(1, 0, "/sitecore/content/Site"), M(2, 1, "/sitecore/content/Site/A", "new"), M(3, 1, "/sitecore/content/Site/B", "new"), M(5, 1, "/sitecore/content/Site/D")]);
        var target = new MemoryTree([M(1, 0, "/sitecore/content/Site"), M(2, 1, "/sitecore/content/Site/A", "old"), M(3, 1, "/sitecore/content/Site/B", "old"), M(4, 1, "/sitecore/content/Site/C")]);
        Assert.Equal(["Update /sitecore/content/Site/A"], (await PlanItems(source, target, Include(), [2])).Select(o => o.ToString()));
    }

    [Fact]
    public async Task Planning_named_items_matches_a_full_plan_for_those_items()
    {
        var source = new MemoryTree([M(1, 0, "/sitecore/content/Site"), M(2, 1, "/sitecore/content/Site/A", "new"), M(3, 1, "/sitecore/content/Site/B")]);
        var target = new MemoryTree([M(1, 0, "/sitecore/content/Site"), M(2, 1, "/sitecore/content/Site/A", "old"), M(4, 1, "/sitecore/content/Site/C")]);
        var full = await Plan(source, target, Include());
        var named = await PlanItems(source, target, Include(), [2, 3, 4]);
        Assert.Equal(full.Select(o => o.ToString()), named.Select(o => o.ToString()));
    }

    [Fact]
    public async Task A_named_item_gone_from_the_source_or_moved_out_of_the_include_is_deleted()
    {
        var source = new MemoryTree([M(1, 0, "/sitecore/content/Site"), M(3, 0, "/sitecore/content/Elsewhere")]);
        var target = new MemoryTree([M(1, 0, "/sitecore/content/Site"), M(2, 1, "/sitecore/content/Site/Gone"), M(3, 1, "/sitecore/content/Site/Away")]);
        var ops = await PlanItems(source, target, Include(), [2, 3]);
        Assert.Equal(["Delete /sitecore/content/Site/Gone", "Delete /sitecore/content/Site/Away"], ops.Select(o => o.ToString()));
    }

    [Fact]
    public async Task A_renamed_item_takes_its_subtree_with_it()
    {
        // B was renamed to Renamed, gained child New, and lost child Old.
        var source = new MemoryTree([M(1, 0, "/sitecore/content/Site"), M(2, 1, "/sitecore/content/Site/Renamed"), M(3, 2, "/sitecore/content/Site/Renamed/Kept"), M(4, 2, "/sitecore/content/Site/Renamed/New")]);
        var target = new MemoryTree([M(1, 0, "/sitecore/content/Site"), M(2, 1, "/sitecore/content/Site/B"), M(3, 2, "/sitecore/content/Site/B/Kept"), M(5, 2, "/sitecore/content/Site/B/Old")]);
        var ops = await PlanItems(source, target, Include(), [2], 2);
        Assert.Equal(["Create /sitecore/content/Site/Renamed/New", "Rename /sitecore/content/Site/B -> /sitecore/content/Site/Renamed", "Delete /sitecore/content/Site/B/Old"], ops.Select(o => o.ToString()));
    }

    [Fact]
    public async Task New_named_items_are_created_parent_first()
    {
        var source = new MemoryTree([M(1, 0, "/sitecore/content/Site"), M(2, 1, "/sitecore/content/Site/P"), M(3, 2, "/sitecore/content/Site/P/C")]);
        var target = new MemoryTree([M(1, 0, "/sitecore/content/Site")]);
        var ops = await PlanItems(source, target, Include(), [3, 2]);
        Assert.Equal(["Create /sitecore/content/Site/P", "Create /sitecore/content/Site/P/C"], ops.Select(o => o.ToString()));
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
