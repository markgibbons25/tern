using System.Text.Json;

using Tern.Core.Configuration;
using Tern.Core.Transfer;
using Tern.Core.Trees;
using Tern.Remote.Transfer;

namespace Tern.Core.Tests.Planning;

public class TransferCoverTests
{
    private static Guid G(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:D12}");

    private static ItemMetadata M(int id, int parent, string path, bool hasChildren = true) =>
        new(G(id), G(parent), Guid.Empty, path, "sig") { HasChildren = hasChildren };

    private static Include Include(TreeScope scope = TreeScope.ItemAndDescendants, params IncludeRule[] rules)
    {
        var include = new Include { Name = "test", Path = ItemPath.Parse("/sitecore/content/Site"), Scope = scope, Rules = rules };
        include.Module = new Module { Namespace = "Test", SourcePath = "/x/test.module.json", Includes = [include] };
        return include;
    }

    // Site
    //   A
    //     A1
    //   Sitemaps
    //     S1
    //   B
    private static readonly ItemMetadata[] Site =
    [
        M(1, 99, "/sitecore/content/Site"),
        M(2, 1, "/sitecore/content/Site/A"),
        M(3, 2, "/sitecore/content/Site/A/A1", hasChildren: false),
        M(4, 1, "/sitecore/content/Site/Sitemaps"),
        M(5, 4, "/sitecore/content/Site/Sitemaps/S1", hasChildren: false),
        M(6, 1, "/sitecore/content/Site/B", hasChildren: false),
    ];

    private static IEnumerable<string> Describe(IEnumerable<DataTree> trees) =>
        trees.Select(t => $"{t.Path} {t.Scope} {t.Merge} {t.ItemCount}");

    [Fact]
    public void A_whole_subtree_is_one_data_tree()
    {
        var trees = TransferCover.Cover(Include(), Site, MergeStrategy.OverrideExistingItem);
        Assert.Equal(["/sitecore/content/Site ItemAndDescendants OverrideExistingItem 6"], Describe(trees));
    }

    [Fact]
    public void DescendantsOnly_transfers_each_child_tree()
    {
        // The fetch for DescendantsOnly may not hold the root; its children are where the cover starts.
        var trees = TransferCover.Cover(Include(TreeScope.DescendantsOnly), Site[1..], MergeStrategy.OverrideExistingItem);
        Assert.Equal(
            ["/sitecore/content/Site/A ItemAndDescendants OverrideExistingItem 2", "/sitecore/content/Site/Sitemaps ItemAndDescendants OverrideExistingItem 2", "/sitecore/content/Site/B ItemAndDescendants OverrideExistingItem 1"],
            Describe(trees));
        Assert.Equal(Describe(trees), Describe(TransferCover.Cover(Include(TreeScope.DescendantsOnly), Site, MergeStrategy.OverrideExistingItem)));
    }

    [Fact]
    public void An_ignored_rule_splits_the_tree_around_it()
    {
        var include = Include(TreeScope.ItemAndDescendants, new IncludeRule { Path = "/Sitemaps", Scope = RuleScope.Ignored });
        var trees = TransferCover.Cover(include, Site, MergeStrategy.OverrideExistingTree);
        // The root goes alone, so OverrideExistingTree must not delete its ignored Sitemaps in the target.
        Assert.Equal(
            ["/sitecore/content/Site SingleItem OverrideExistingItem 1", "/sitecore/content/Site/A ItemAndDescendants OverrideExistingTree 2", "/sitecore/content/Site/B ItemAndDescendants OverrideExistingTree 1"],
            Describe(trees));
    }

    [Fact]
    public void Create_only_keeps_what_the_target_has()
    {
        var include = Include(TreeScope.ItemAndDescendants, new IncludeRule { Path = "/A", Scope = RuleScope.ItemAndDescendants, PushOperations = PushOperations.CreateOnly });
        var trees = TransferCover.Cover(include, Site, MergeStrategy.OverrideExistingItem);
        Assert.Contains("/sitecore/content/Site/A ItemAndDescendants KeepExistingItem 2", Describe(trees));
        Assert.Contains("/sitecore/content/Site SingleItem OverrideExistingItem 1", Describe(trees));
    }

    [Fact]
    public void Narrow_scopes_never_take_unfetched_descendants()
    {
        Assert.Equal(["/sitecore/content/Site SingleItem OverrideExistingItem 1"], Describe(TransferCover.Cover(Include(TreeScope.SingleItem), Site[..1], MergeStrategy.OverrideExistingItem)));
        var children = Site.Where(m => m.ItemPath.Count <= 4).ToList();
        Assert.Equal(
            ["/sitecore/content/Site SingleItem OverrideExistingItem 1", "/sitecore/content/Site/A SingleItem OverrideExistingItem 1", "/sitecore/content/Site/Sitemaps SingleItem OverrideExistingItem 1", "/sitecore/content/Site/B ItemAndDescendants OverrideExistingItem 1"],
            Describe(TransferCover.Cover(Include(TreeScope.ItemAndChildren), children, MergeStrategy.OverrideExistingItem)));
    }

    [Fact]
    public void A_missing_root_transfers_nothing() =>
        Assert.Empty(TransferCover.Cover(Include(), [], MergeStrategy.OverrideExistingItem));

    [Fact]
    public void The_create_request_matches_the_api()
    {
        var trees = new[] { new DataTree(ItemPath.Parse("/sitecore/content/Site"), G(1), G(99), true, MergeStrategy.KeepExistingItem, 6) };
        using var doc = JsonDocument.Parse(ContentTransferApi.CreateBody(G(7), "master", trees));
        var root = doc.RootElement;
        Assert.Equal(G(7).ToString("D"), root.GetProperty("TransferId").GetString());
        Assert.Equal("master", root.GetProperty("Configuration").GetProperty("Database").GetString());
        var tree = Assert.Single(root.GetProperty("Configuration").GetProperty("DataTrees").EnumerateArray());
        Assert.Equal(("/sitecore/content/Site", "ItemAndDescendants", "KeepExistingItem"), (tree.GetProperty("ItemPath").GetString(), tree.GetProperty("Scope").GetString(), tree.GetProperty("MergeStrategy").GetString()));
    }

    [Theory]
    [InlineData("attachment; filename=chunk-0.bin; IsMedia=true; ItemsProcessed=12; ItemsSkipped=0", "IsMedia", "true")]
    [InlineData("attachment; filename=\"chunk-0.bin\"; IsMedia=\"False\"", "IsMedia", "False")]
    [InlineData("attachment; ItemsProcessed=12;ItemsSkipped=3", "ItemsSkipped", "3")]
    [InlineData("attachment; filename=x", "IsMedia", null)]
    public void Reads_content_disposition_parameters(string header, string name, string? expected) =>
        Assert.Equal(expected, ContentTransferApi.DispositionValue(header, name));

    [Fact]
    public void Reads_the_transfer_status()
    {
        using var doc = JsonDocument.Parse("""{"State":"Completed","ChunkSetsMetadata":[{"ChunkSetId":"87654321-4321-4321-4321-cba987654321","ChunkCount":3,"TotalItemCount":150}],"StartTime":null,"FinishedTime":null}""");
        var status = ContentTransferApi.ParseStatus(doc.RootElement);
        Assert.Equal("Completed", status.State);
        Assert.Equal(new ChunkSet(Guid.Parse("87654321-4321-4321-4321-cba987654321"), 3, 150), Assert.Single(status.ChunkSets));
        using var missing = JsonDocument.Parse("""{"State":"NotFound","ChunkSetsMetadata":null}""");
        Assert.Empty(ContentTransferApi.ParseStatus(missing.RootElement).ChunkSets);
    }
}
