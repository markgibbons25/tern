using System.Globalization;
using System.Text;
using System.Text.Json;

using Tern.Core.Configuration;
using Tern.Core.Items;
using Tern.Core.Trees;

namespace Tern.Remote.Management;

public sealed record SerializeRequest(string PathOrId, string Database, TreeScope Scope, IReadOnlyList<Guid> ExcludedFieldIds);

public sealed record HistoryEntry(Guid Id, string Path, string Database, string? OldPath, string ChangeType);

/// <summary>
/// The serialization queries of Management Services: <c>serialize</c> for metadata and item data,
/// and <c>history</c> for the Change history. Requests are batched as aliased fields and sent in
/// parallel; both limits are settings, so the benchmarks can tune them (ADR 0005).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "SemaphoreSlim needs disposing only after AvailableWaitHandle is used.")]
public sealed class SerializationApi(ManagementClient client, SerializationApiOptions? options = null)
{
    private static string Alias(int n) => "a" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private readonly SerializationApiOptions _options = options ?? new SerializationApiOptions();

    // One limit for every caller, so Includes fetched at the same time share the parallel requests.
    private readonly SemaphoreSlim _inFlight = new(Math.Max(1, options?.ParallelRequests ?? new SerializationApiOptions().ParallelRequests));

    public ManagementClient Client => client;

    public SerializationApiOptions Options => _options;

    public Task<IReadOnlyList<ItemMetadata>[]> GetMetadataAsync(IReadOnlyList<SerializeRequest> requests, CancellationToken ct) =>
        RunBatchesAsync(requests, metadata: true, e => ParseMetadata(e), null, ct);

    public async Task<IReadOnlyDictionary<Guid, ItemData>> GetItemDataAsync(string database, IReadOnlyCollection<Guid> ids, IReadOnlyList<Guid> excludedFieldIds, CancellationToken ct)
    {
        var requests = ids.Select(id => new SerializeRequest(id.ToString("D"), database, TreeScope.SingleItem, excludedFieldIds)).ToList();
        var results = await RunBatchesAsync(requests, metadata: false, ParseData, null, ct);
        var map = new Dictionary<Guid, ItemData>();
        foreach (var item in results.SelectMany(r => r).OfType<ItemData>())
        {
            map[item.Id] = item;
        }
        return map;
    }

    /// <summary>
    /// Fetches item data in batches and hands each batch to <paramref name="onBatch"/> as it
    /// arrives, instead of collecting it all. A batch keeps its request slot until
    /// <paramref name="onBatch"/> returns, so no more than the parallel requests' worth of item
    /// data is in memory at once. <paramref name="onBatch"/> is called concurrently.
    /// </summary>
    public Task StreamItemDataAsync(string database, IReadOnlyCollection<Guid> ids, IReadOnlyList<Guid> excludedFieldIds, Func<IReadOnlyList<ItemData>, Task> onBatch, CancellationToken ct)
    {
        var requests = ids.Select(id => new SerializeRequest(id.ToString("D"), database, TreeScope.SingleItem, excludedFieldIds)).ToList();
        return RunBatchesAsync(requests, metadata: false, ParseData, lists => onBatch(lists.SelectMany(l => l).OfType<ItemData>().ToList()), ct);
    }

    /// <summary>
    /// The item in a <c>serialize { data }</c> entry, or null when the server sent <c>data: null</c>
    /// (an item it listed but could not read). Callers report such an item as not fetched.
    /// </summary>
    internal static ItemData? ParseData(JsonElement e) =>
        e.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object ? ItemDataJson.Parse(data) : null;

    public async Task<string> GetHistoryTimestampAsync(CancellationToken ct)
    {
        using var doc = await client.QueryAsync("{ history { currentTimestamp } }", null, ct);
        return doc.RootElement.GetProperty("data").GetProperty("history").GetProperty("currentTimestamp").GetString()!;
    }

    public async Task<(string Timestamp, IReadOnlyList<HistoryEntry> Entries)> GetHistoryAsync(string since, CancellationToken ct)
    {
        const string query = "query Entries($timestamp: String!) { history { currentTimestamp entries(timestamp: $timestamp) { id path database oldPath changeType } } }";
        using var doc = await client.QueryAsync(query, w => w.WriteString("timestamp", since), ct);
        var history = doc.RootElement.GetProperty("data").GetProperty("history");
        var entries = new List<HistoryEntry>();
        if (history.TryGetProperty("entries", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in list.EnumerateArray())
            {
                entries.Add(new HistoryEntry(
                    Guid.Parse(e.GetProperty("id").GetString()!),
                    e.GetProperty("path").GetString() ?? string.Empty,
                    e.GetProperty("database").GetString() ?? "master",
                    e.TryGetProperty("oldPath", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() : null,
                    e.GetProperty("changeType").GetString() ?? string.Empty));
            }
        }
        return (history.GetProperty("currentTimestamp").GetString()!, entries);
    }

    // With onBatch, each batch's results go to it, while the batch still holds its request slot, and are not kept.
    private async Task<IReadOnlyList<T>[]> RunBatchesAsync<T>(IReadOnlyList<SerializeRequest> requests, bool metadata, Func<JsonElement, T> parse, Func<IReadOnlyList<T>[], Task>? onBatch, CancellationToken ct)
    {
        var results = new IReadOnlyList<T>[requests.Count];
        // A subtree request can return thousands of items, so each one gets its own HTTP request;
        // single-item requests are packed together.
        var batches = new List<List<int>>();
        var current = new List<int>();
        var batchSize = Math.Max(1, _options.AliasesPerRequest);
        for (var i = 0; i < requests.Count; i++)
        {
            if (requests[i].Scope is TreeScope.ItemAndDescendants or TreeScope.DescendantsOnly)
            {
                batches.Add([i]);
                continue;
            }
            current.Add(i);
            if (current.Count == batchSize)
            {
                batches.Add(current);
                current = [];
            }
        }
        if (current.Count > 0)
        {
            batches.Add(current);
        }
        await Task.WhenAll(batches.Select(async batch =>
        {
            await _inFlight.WaitAsync(ct);
            IReadOnlyList<T>[] parsed;
            try
            {
                // Built once a slot is free, so a large pull doesn't hold every query in memory at once.
                var (query, variables) = BuildQuery(batch.Select(i => requests[i]).ToList(), metadata);
                using (var doc = await client.QueryAsync(query, variables, ct))
                {
                    var data = doc.RootElement.GetProperty("data");
                    parsed = new IReadOnlyList<T>[batch.Count];
                    for (var n = 0; n < batch.Count; n++)
                    {
                        var list = new List<T>();
                        if (data.TryGetProperty(Alias(n), out var array) && array.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var element in array.EnumerateArray())
                            {
                                list.Add(parse(element));
                            }
                        }
                        parsed[n] = list;
                    }
                }
                if (onBatch is not null)
                {
                    await onBatch(parsed);
                    return;
                }
            }
            finally
            {
                _inFlight.Release();
            }
            for (var n = 0; n < batch.Count; n++)
            {
                results[batch[n]] = parsed[n];
            }
        }));
        return results;
    }

    private static (string Query, Action<Utf8JsonWriter> Variables) BuildQuery(List<SerializeRequest> batch, bool metadata)
    {
        var fields = metadata ? "id parentId templateId path hasChildren dataSignature" : "data";
        var declarations = new StringBuilder();
        var selections = new StringBuilder();
        for (var n = 0; n < batch.Count; n++)
        {
            var a = Alias(n);
            declarations.Append(CultureInfo.InvariantCulture, $"${a}P: String! ${a}D: String! ${a}E: [String] ${a}S: TreeScope! ");
            selections.Append(CultureInfo.InvariantCulture, $"{a}: serialize(path: ${a}P, database: ${a}D, scope: ${a}S, excludedFieldIds: ${a}E) {{ {fields} }} ");
        }
        var query = $"query({declarations.ToString().TrimEnd()}) {{ {selections}}}";
        return (query, w =>
        {
            for (var n = 0; n < batch.Count; n++)
            {
                var a = Alias(n);
                var r = batch[n];
                w.WriteString($"{a}P", r.PathOrId);
                w.WriteString($"{a}D", r.Database);
                w.WriteStartArray($"{a}E");
                foreach (var f in r.ExcludedFieldIds)
                {
                    w.WriteStringValue(f.ToString("D"));
                }
                w.WriteEndArray();
                w.WriteString($"{a}S", ToGraphQl(r.Scope));
            }
        }
        );
    }

    private static string ToGraphQl(TreeScope scope) => scope switch
    {
        TreeScope.SingleItem => "SINGLE_ITEM",
        TreeScope.ItemAndChildren => "ITEM_AND_CHILDREN",
        // DescendantsOnly is fetched as the whole subtree; the caller drops the root.
        TreeScope.ItemAndDescendants or TreeScope.DescendantsOnly => "ITEM_AND_DESCENDANTS",
        _ => throw new ArgumentOutOfRangeException(nameof(scope)),
    };

    private static ItemMetadata ParseMetadata(JsonElement e) => new(
        Guid.Parse(e.GetProperty("id").GetString()!),
        Guid.Parse(e.GetProperty("parentId").GetString()!),
        Guid.Parse(e.GetProperty("templateId").GetString()!),
        e.GetProperty("path").GetString()!.TrimEnd('/'),
        e.TryGetProperty("dataSignature", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null)
    {
        HasChildren = !e.TryGetProperty("hasChildren", out var h) || h.ValueKind != JsonValueKind.False,
    };
}

public sealed class SerializationApiOptions
{
    /// <summary>Single-item requests packed into one HTTP request.</summary>
    public int AliasesPerRequest { get; init; } = 26;

    /// <summary>HTTP requests in flight at once.</summary>
    public int ParallelRequests { get; init; } = 8;

    /// <summary>Subtrees are read as several parallel requests from this many items; 0 never splits.</summary>
    public int SplitThreshold { get; init; }
}
