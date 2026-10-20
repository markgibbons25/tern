using Tern.Core.Configuration;
using Tern.Core.Items;

namespace Tern.Core.Trees;

/// <summary>What a tree knows about an item without loading its fields.</summary>
public sealed record ItemMetadata(Guid Id, Guid ParentId, Guid TemplateId, string Path, string? Signature)
{
    /// <summary>Whether the item has children, when the tree knows; true when it doesn't.</summary>
    public bool HasChildren { get; init; } = true;

    public string Name
    {
        get
        {
            var slash = Path.LastIndexOf('/');
            return slash < 0 ? Path : Path[(slash + 1)..];
        }
    }

    public ItemPath ItemPath => ItemPath.Parse(Path);
}

/// <summary>
/// An item tree: Item files on disk or a CM. The Plan engine compares any two of them (ADR 0010).
/// </summary>
public interface IItemTree
{
    /// <summary>A short description for messages, such as a folder or a CM host.</summary>
    string Description { get; }

    /// <summary>How messages name this tree for one Include, e.g. "Sitecore endpoint dev (Host: …, DB: master)".</summary>
    string StoreName(Include include) => Description;

    /// <summary>Every item the Include's scope reaches, before rules are applied.</summary>
    Task<IReadOnlyList<ItemMetadata>> GetMetadataAsync(Include include, CancellationToken ct);

    /// <summary>
    /// Metadata for items found by ID anywhere in the Include's database, not only under its path.
    /// The Official CLI finds the target of every source item this way, so a renamed or moved item
    /// is recognised rather than re-created.
    /// </summary>
    Task<IReadOnlyList<ItemMetadata>> GetByIdsAsync(Include include, IReadOnlyCollection<Guid> ids, CancellationToken ct);

    /// <summary>Every child of an item, wherever it is in the Include's database, before rules are applied.</summary>
    Task<IReadOnlyList<ItemMetadata>> GetChildrenAsync(Include include, IReadOnlyCollection<Guid> parentIds, CancellationToken ct);

    /// <summary>Full item data for the given IDs. Missing IDs are left out of the result.</summary>
    Task<IReadOnlyDictionary<Guid, ItemData>> GetItemsAsync(Include include, IReadOnlyCollection<Guid> ids, CancellationToken ct);

    /// <summary>
    /// Full item data for the given IDs, handed to <paramref name="onBatch"/> a batch at a time as
    /// it arrives, so a caller can write each batch and let it go. Missing IDs are left out;
    /// <paramref name="onBatch"/> may be called concurrently.
    /// </summary>
    async Task StreamItemsAsync(Include include, IReadOnlyCollection<Guid> ids, Func<IReadOnlyList<ItemData>, Task> onBatch, CancellationToken ct) =>
        await onBatch((await GetItemsAsync(include, ids, ct)).Values.ToList());
}
