using Tern.Core.Configuration;
using Tern.Core.Items;
using Tern.Core.Planning;
using Tern.Core.Trees;

namespace Tern.Core.Tests.Trees;

/// <summary>The file index must never change what a tree reports: it may only skip reading files that haven't changed.</summary>
public sealed class FileItemTreeTests : IDisposable
{
    private static readonly Guid Template = Guid.Parse("76036f5e-cbce-46d1-af0a-4143f9b557aa");
    private static readonly Guid Title = Guid.Parse("75577384-3c97-45da-a847-81b00500e250");
    private static readonly Guid Thumbnail = Guid.Parse("c7c26117-dbb1-42b2-ab5e-f7223845cca3");
    private static readonly Guid Outside = Guid.Parse("99999999-0000-0000-0000-000000000000");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tern-tree-" + Guid.NewGuid().ToString("N"));
    private readonly Include _include;

    public FileItemTreeTests()
    {
        _include = new Include { Name = "test", Path = ItemPath.Parse("/sitecore/content/Site"), PhysicalRoot = Path.Combine(_root, "items", "test") };
        _include.Module = new Module { Namespace = "Test", SourcePath = Path.Combine(_root, "test.module.json"), Includes = [_include] };
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static Guid G(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:D12}");

    private static bool IncludeField(Guid id) => id != Thumbnail;

    private FileIndexCache Index() => new(Path.Combine(_root, "index"), "excluded:" + Thumbnail.ToString("N"));

    private static ItemData Item(int id, int parent, string path, string title)
    {
        var item = new ItemData { Id = G(id), ParentId = parent == 0 ? Outside : G(parent), TemplateId = Template, Path = path };
        item.SharedFields.Add(new FieldValue(Thumbnail, "__Thumbnail", null, "excluded"));
        item.Languages.GetOrAdd("en").Versions.GetOrAdd(1).Fields.Add(new FieldValue(Title, "Title", null, title));
        return item;
    }

    /// <summary>Pulls <paramref name="items"/> into the folder as creates, through a tree with the index.</summary>
    private void Pull(params ItemData[] items)
    {
        var files = new FileItemTree(IncludeField, index: Index());
        var ops = items.Select(i => new PlanOperation { Action = PlanAction.Create, Module = "Test", Include = "test", Database = "master", Id = i.Id, Path = i.Path, ParentId = i.ParentId }).ToList();
        new FileApplier(files, IncludeField).Apply(_include, ops, items.ToDictionary(i => i.Id), checkPreconditions: false);
    }

    private static List<string> Describe(FileItemTree tree, Include include) =>
        tree.Files(include).Select(f => $"{f.Id} {f.ParentId} {f.TemplateId} {f.Path} {f.Signature} {f.PhysicalPath}").Order(StringComparer.Ordinal).ToList();

    [Fact]
    public void Files_written_by_a_pull_read_back_the_same_from_the_index_as_from_disk()
    {
        Pull(Item(1, 0, "/sitecore/content/Site", "Root"), Item(2, 1, "/sitecore/content/Site/A", "A"), Item(3, 2, "/sitecore/content/Site/A/B", "B"));
        var fromIndex = Describe(new FileItemTree(IncludeField, index: Index()), _include);
        var fromDisk = Describe(new FileItemTree(IncludeField), _include);
        Assert.Equal(3, fromDisk.Count);
        Assert.Equal(fromDisk, fromIndex);
    }

    [Fact]
    public void A_file_changed_after_it_was_indexed_is_read_again()
    {
        Pull(Item(1, 0, "/sitecore/content/Site", "Root"), Item(2, 1, "/sitecore/content/Site/A", "A"));
        _ = new FileItemTree(IncludeField, index: Index()).Files(_include);

        // Same length, different content, so only the write time tells them apart.
        var file = Directory.EnumerateFiles(_include.PhysicalRoot, "A.yml", SearchOption.AllDirectories).Single();
        var text = File.ReadAllText(file).Replace("Value: A", "Value: Z");
        File.WriteAllText(file, text, new System.Text.UTF8Encoding(true));
        File.SetLastWriteTimeUtc(file, File.GetLastWriteTimeUtc(file).AddSeconds(5));

        var fromIndex = Describe(new FileItemTree(IncludeField, index: Index()), _include);
        Assert.Equal(Describe(new FileItemTree(IncludeField), _include), fromIndex);
    }

    [Fact]
    public void Moves_renames_and_deletes_rewrite_the_files_and_keep_the_index_true()
    {
        Pull(Item(1, 0, "/sitecore/content/Site", "Root"), Item(2, 1, "/sitecore/content/Site/A", "A"), Item(3, 1, "/sitecore/content/Site/B", "B"),
            Item(4, 3, "/sitecore/content/Site/B/Child", "Child"), Item(5, 1, "/sitecore/content/Site/Gone", "Gone"));
        var files = new FileItemTree(IncludeField, index: Index());
        static PlanOperation Op(PlanAction action, int id, string path, string? from, int parent) =>
            new() { Action = action, Module = "Test", Include = "test", Database = "master", Id = G(id), Path = path, TargetPath = from, ParentId = G(parent) };
        new FileApplier(files, IncludeField).Apply(_include,
        [
            Op(PlanAction.Rename, 3, "/sitecore/content/Site/Renamed", "/sitecore/content/Site/B", 1),
            Op(PlanAction.Move, 3, "/sitecore/content/Site/A/Renamed", "/sitecore/content/Site/Renamed", 2),
            Op(PlanAction.Delete, 5, "/sitecore/content/Site/Gone", "/sitecore/content/Site/Gone", 1),
        ], new Dictionary<Guid, ItemData>(), checkPreconditions: false);

        var fromDisk = new FileItemTree(IncludeField).Files(_include);
        Assert.Equal(
            ["/sitecore/content/Site", "/sitecore/content/Site/A", "/sitecore/content/Site/A/Renamed", "/sitecore/content/Site/A/Renamed/Child"],
            fromDisk.Select(f => f.Path).Order(StringComparer.Ordinal));
        Assert.Equal(G(2), fromDisk.Single(f => f.Id == G(3)).ParentId);
        Assert.Equal(Describe(new FileItemTree(IncludeField), _include), Describe(new FileItemTree(IncludeField, index: Index()), _include));
        // The old children folders go with the files; validation would report any left empty.
        Assert.DoesNotContain(Directory.EnumerateDirectories(_include.PhysicalRoot, "*", SearchOption.AllDirectories), d => !Directory.EnumerateFileSystemEntries(d).Any());
    }

    [Fact]
    public void Deviation_D12_an_item_the_source_returns_no_data_for_is_skipped_and_the_rest_written()
    {
        var root = Item(1, 0, "/sitecore/content/Site", "Root");
        var unreadable = Item(2, 1, "/sitecore/content/Site/Video", "Video");
        var files = new FileItemTree(IncludeField, index: Index());
        var ops = new[] { root, unreadable }.Select(i => new PlanOperation { Action = PlanAction.Create, Module = "Test", Include = "test", Database = "master", Id = i.Id, Path = i.Path, ParentId = i.ParentId }).ToList();

        // Only the root's data is there, as when the CM answers the video's serialize with data: null.
        var applied = new FileApplier(files, IncludeField).Apply(_include, ops, new Dictionary<Guid, ItemData> { [root.Id] = root }, checkPreconditions: false);

        Assert.Null(applied.Single(a => a.Operation.Id == root.Id).Skipped);
        Assert.Contains("/sitecore/content/Site/Video", applied.Single(a => a.Operation.Id == unreadable.Id).Skipped);
        Assert.Equal(["/sitecore/content/Site"], new FileItemTree(IncludeField).Files(_include).Select(f => f.Path));
    }

    [Fact]
    public void A_damaged_index_is_ignored()
    {
        Pull(Item(1, 0, "/sitecore/content/Site", "Root"));
        foreach (var idx in Directory.EnumerateFiles(Path.Combine(_root, "index")))
        {
            File.WriteAllBytes(idx, [1, 2, 3]);
        }
        Assert.Equal(Describe(new FileItemTree(IncludeField), _include), Describe(new FileItemTree(IncludeField, index: Index()), _include));
    }

    [Fact]
    public void Another_excluded_field_set_does_not_share_the_index()
    {
        Pull(Item(1, 0, "/sitecore/content/Site", "Root"));
        // With nothing excluded the Signature includes __Thumbnail, so it must not come from the other set's index.
        var all = new FileItemTree(_ => true, index: new FileIndexCache(Path.Combine(_root, "index"), "excluded:")).Files(_include).Single();
        Assert.Equal(new FileItemTree(_ => true).Files(_include).Single().Signature, all.Signature);
    }

    [Fact]
    public async Task Reading_items_gives_their_fields_without_excluded_ones()
    {
        Pull(Item(1, 0, "/sitecore/content/Site", "Root"));
        var tree = new FileItemTree(IncludeField, index: Index());
        var item = (await tree.GetItemsAsync(_include, [G(1)], CancellationToken.None))[G(1)];
        Assert.Equal("Root", item.Languages.Single().Versions.Single().Fields.Single().Value);
        Assert.DoesNotContain(item.AllFields, f => f.Id == Thumbnail);
    }
}
