using Tern.Core.Configuration;
using Tern.Core.Items;

namespace Tern.Core.Trees;

/// <summary>What a tree knows about an item without loading its fields.</summary>
public sealed record ItemMetadata(Guid Id, Guid ParentId, Guid TemplateId, string Path, string? Signature)
{
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

    /// <summary>Every item the Include's scope reaches, before rules are applied.</summary>
    Task<IReadOnlyList<ItemMetadata>> GetMetadataAsync(Include include, CancellationToken ct);

    /// <summary>Full item data for the given IDs. Missing IDs are left out of the result.</summary>
    Task<IReadOnlyDictionary<Guid, ItemData>> GetItemsAsync(Include include, IReadOnlyCollection<Guid> ids, CancellationToken ct);
}
