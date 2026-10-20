using System.Collections.Concurrent;
using Tern.Core.Configuration;
using Tern.Core.ItemFiles;
using Tern.Core.Items;
using Tern.Core.Signatures;

namespace Tern.Core.Trees;

/// <summary>One parsed Item file.</summary>
public sealed record ItemFile(string PhysicalPath, ItemData Item, string Signature);

/// <summary>
/// The Item files of a set of Includes. The tree is built from each file's <c>Path</c> and
/// <c>Parent</c>, not from the folder layout, exactly as the Official CLI builds it.
/// </summary>
public sealed class FileItemTree(Func<Guid, bool> includeField) : IItemTree
{
    private readonly ConcurrentDictionary<Include, IReadOnlyList<ItemFile>> _files = new();

    public string Description => "the Item files";

    /// <summary>Every Item file under the Include's folder, whether or not its rules still cover it.</summary>
    public IReadOnlyList<ItemFile> Files(Include include) => _files.GetOrAdd(include, Load);

    public Task<IReadOnlyList<ItemMetadata>> GetMetadataAsync(Include include, CancellationToken ct)
    {
        IReadOnlyList<ItemMetadata> result = Files(include)
            .Select(f => new ItemMetadata(f.Item.Id, f.Item.ParentId, f.Item.TemplateId, f.Item.Path, f.Signature))
            .ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyDictionary<Guid, ItemData>> GetItemsAsync(Include include, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var wanted = ids.ToHashSet();
        IReadOnlyDictionary<Guid, ItemData> result = Files(include)
            .Where(f => wanted.Contains(f.Item.Id))
            .GroupBy(f => f.Item.Id)
            .ToDictionary(g => g.Key, g => g.First().Item);
        return Task.FromResult(result);
    }

    public ItemFile? FindByPath(Include include, ItemPath path) =>
        Files(include).FirstOrDefault(f => ItemPath.Parse(f.Item.Path).Equals(path));

    /// <summary>Forgets what was read, so the next call sees the files as they are now.</summary>
    public void Invalidate(Include include) => _files.TryRemove(include, out _);

    private IReadOnlyList<ItemFile> Load(Include include)
    {
        if (!Directory.Exists(include.PhysicalRoot))
        {
            return [];
        }
        var paths = Directory.EnumerateFiles(include.PhysicalRoot, "*.yml", SearchOption.AllDirectories).ToList();
        var results = new ItemFile[paths.Count];
        Parallel.For(0, paths.Count, i =>
        {
            var item = ItemFileReader.Read(File.ReadAllBytes(paths[i]), paths[i], includeField);
            results[i] = new ItemFile(paths[i], item, SignatureGenerator.Create(item));
        });
        return results;
    }
}
