using System.Collections.Concurrent;
using Tern.Core.Configuration;
using Tern.Core.Items;
using Tern.Core.Trees;

namespace Tern.Remote.Management;

/// <summary>The items on a CM, read through Management Services.</summary>
public sealed class CmItemTree(SerializationApi api, IReadOnlyList<Guid> excludedFieldIds, string? endpointName = null) : IItemTree
{
    private readonly ConcurrentDictionary<Include, IReadOnlyList<ItemMetadata>> _metadata = new();

    public string Description => api.Client.Host;

    public string StoreName(Include include) => $"Sitecore endpoint {endpointName ?? api.Client.Host} (Host: {api.Client.Host}/, DB: {include.Database})";

    public SerializationApi Api => api;

    /// <summary>Fetches the metadata of many Includes at once, so their requests run in parallel.</summary>
    public async Task PrefetchAsync(IReadOnlyList<Include> includes, CancellationToken ct)
    {
        var pending = includes.Where(i => !_metadata.ContainsKey(i)).ToList();
        if (pending.Count == 0)
        {
            return;
        }
        var requests = pending.Select(i => new SerializeRequest(i.Path.ToString(), i.Database, i.Scope, excludedFieldIds)).ToList();
        var results = await api.GetMetadataAsync(requests, ct);
        for (var n = 0; n < pending.Count; n++)
        {
            _metadata[pending[n]] = Shape(pending[n], results[n]);
        }
    }

    public async Task<IReadOnlyList<ItemMetadata>> GetMetadataAsync(Include include, CancellationToken ct)
    {
        if (!_metadata.TryGetValue(include, out var metadata))
        {
            await PrefetchAsync([include], ct);
            metadata = _metadata[include];
        }
        return metadata;
    }

    public async Task<IReadOnlyList<ItemMetadata>> GetByIdsAsync(Include include, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return [];
        }
        var results = await api.GetMetadataAsync(ids.Select(id => new SerializeRequest(id.ToString("D"), include.Database, TreeScope.SingleItem, excludedFieldIds)).ToList(), ct);
        return results.SelectMany(r => r).ToList();
    }

    public async Task<IReadOnlyList<ItemMetadata>> GetChildrenAsync(Include include, IReadOnlyCollection<Guid> parentIds, CancellationToken ct)
    {
        if (parentIds.Count == 0)
        {
            return [];
        }
        var parents = parentIds.ToHashSet();
        var results = await api.GetMetadataAsync(parentIds.Select(id => new SerializeRequest(id.ToString("D"), include.Database, TreeScope.ItemAndChildren, excludedFieldIds)).ToList(), ct);
        return results.SelectMany(r => r).Where(m => parents.Contains(m.ParentId)).ToList();
    }

    public Task<IReadOnlyDictionary<Guid, ItemData>> GetItemsAsync(Include include, IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        api.GetItemDataAsync(include.Database, ids, excludedFieldIds, ct);

    private static IReadOnlyList<ItemMetadata> Shape(Include include, IReadOnlyList<ItemMetadata> items)
    {
        if (include.Scope != TreeScope.DescendantsOnly)
        {
            return items;
        }
        return items.Where(i => !ItemPath.Parse(i.Path).Equals(include.Path)).ToList();
    }
}
