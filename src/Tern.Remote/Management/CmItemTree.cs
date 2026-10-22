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

    /// <summary>
    /// Fetches the metadata of many Includes at once, so their requests run in parallel. With a
    /// split threshold, an Include whose folder holds at least that many files, or has none to go
    /// by, is read as several parallel requests (see <see cref="SplitAsync"/>).
    /// </summary>
    public async Task PrefetchAsync(IReadOnlyList<Include> includes, CancellationToken ct, Func<Include, int?>? sizeHint = null)
    {
        var pending = includes.Where(i => !_metadata.ContainsKey(i)).ToList();
        if (pending.Count == 0)
        {
            return;
        }
        var threshold = api.Options.SplitThreshold;
        bool Split(Include i) => threshold > 0 && i.Scope is TreeScope.ItemAndDescendants or TreeScope.DescendantsOnly
            && (sizeHint?.Invoke(i) is not { } size || size >= threshold);
        var whole = pending.Where(i => !Split(i)).ToList();
        var requests = whole.Select(i => new SerializeRequest(i.Path.ToString(), i.Database, i.Scope, excludedFieldIds)).ToList();
        await Task.WhenAll(
            Task.Run(async () =>
            {
                var results = await api.GetMetadataAsync(requests, ct);
                for (var n = 0; n < whole.Count; n++)
                {
                    _metadata[whole[n]] = Shape(whole[n], results[n]);
                }
            }, ct),
            Task.WhenAll(pending.Where(Split).Select(async i => _metadata[i] = Shape(i, await SplitAsync(i, ct)))));
    }

    /// <summary>
    /// The whole subtree of an Include, in the order one request gives, fetched as several. The top
    /// levels are read a level at a time until there are enough subtrees for the parallel requests,
    /// then each subtree is its own request. The server lists a subtree breadth-first, each parent's
    /// children in order, so the pieces merge level by level into the same order.
    /// </summary>
    private async Task<IReadOnlyList<ItemMetadata>> SplitAsync(Include include, CancellationToken ct)
    {
        var root = (await api.GetMetadataAsync([new SerializeRequest(include.Path.ToString(), include.Database, TreeScope.ItemAndChildren, excludedFieldIds)], ct))[0];
        if (root.Count == 0)
        {
            return root;
        }
        var top = new List<ItemMetadata>(root);
        var frontier = root.Skip(1).Where(m => m.HasChildren).ToList();
        var wanted = Math.Max(2, api.Options.ParallelRequests * 2);
        for (var level = 1; frontier.Count > 0 && frontier.Count < wanted && level < 4; level++)
        {
            var children = await api.GetMetadataAsync(frontier.Select(f => new SerializeRequest(f.Id.ToString("D"), include.Database, TreeScope.ItemAndChildren, excludedFieldIds)).ToList(), ct);
            var next = new List<ItemMetadata>();
            for (var n = 0; n < frontier.Count; n++)
            {
                next.AddRange(children[n].Where(c => c.ParentId == frontier[n].Id));
            }
            top.AddRange(next);
            frontier = next.Where(m => m.HasChildren).ToList();
        }
        if (frontier.Count == 0)
        {
            return top;
        }
        var subtrees = await api.GetMetadataAsync(frontier.Select(f => new SerializeRequest(f.Id.ToString("D"), include.Database, TreeScope.ItemAndDescendants, excludedFieldIds)).ToList(), ct);
        static int Depth(string path) => path.Count(c => c == '/');
        var baseDepth = Depth(frontier[0].Path);
        var byDepth = subtrees.Select(s => s.Where(m => Depth(m.Path) > baseDepth).GroupBy(m => Depth(m.Path)).ToDictionary(g => g.Key, g => g.ToList())).ToList();
        var deepest = byDepth.SelectMany(d => d.Keys).DefaultIfEmpty(baseDepth).Max();
        for (var depth = baseDepth + 1; depth <= deepest; depth++)
        {
            foreach (var piece in byDepth)
            {
                if (piece.TryGetValue(depth, out var nodes))
                {
                    top.AddRange(nodes);
                }
            }
        }
        return top;
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

    public Task StreamItemsAsync(Include include, IReadOnlyCollection<Guid> ids, Func<IReadOnlyList<ItemData>, Task> onBatch, CancellationToken ct) =>
        api.StreamItemDataAsync(include.Database, ids, excludedFieldIds, onBatch, ct);

    private static IReadOnlyList<ItemMetadata> Shape(Include include, IReadOnlyList<ItemMetadata> items)
    {
        if (include.Scope != TreeScope.DescendantsOnly)
        {
            return items;
        }
        return items.Where(i => !ItemPath.Parse(i.Path).Equals(include.Path)).ToList();
    }
}
