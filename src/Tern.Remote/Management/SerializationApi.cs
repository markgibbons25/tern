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
public sealed class SerializationApi(ManagementClient client, SerializationApiOptions? options = null)
{
    private static string Alias(int n) => "a" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private readonly SerializationApiOptions _options = options ?? new SerializationApiOptions();

    public ManagementClient Client => client;

    public Task<IReadOnlyList<ItemMetadata>[]> GetMetadataAsync(IReadOnlyList<SerializeRequest> requests, CancellationToken ct) =>
        RunBatchesAsync(requests, metadata: true, ct, e => ParseMetadata(e));

    public async Task<IReadOnlyDictionary<Guid, ItemData>> GetItemDataAsync(string database, IReadOnlyCollection<Guid> ids, IReadOnlyList<Guid> excludedFieldIds, CancellationToken ct)
    {
        var requests = ids.Select(id => new SerializeRequest(id.ToString("D"), database, TreeScope.SingleItem, excludedFieldIds)).ToList();
        var results = await RunBatchesAsync(requests, metadata: false, ct, e => ItemDataJson.Parse(e.GetProperty("data")));
        var map = new Dictionary<Guid, ItemData>();
        foreach (var item in results.SelectMany(r => r))
        {
            map[item.Id] = item;
        }
        return map;
    }

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

    private async Task<IReadOnlyList<T>[]> RunBatchesAsync<T>(IReadOnlyList<SerializeRequest> requests, bool metadata, CancellationToken ct, Func<JsonElement, T> parse)
    {
        var results = new IReadOnlyList<T>[requests.Count];
        // A subtree request can return thousands of items, so each one gets its own HTTP request;
        // single-item requests are packed together.
        var batches = new List<List<int>>();
        var current = new List<int>();
        var batchSize = Math.Max(1, _options.AliasesPerRequest);
        for (var i = 0; i < requests.Count; i++)
        {
            if (requests[i].Scope != TreeScope.SingleItem)
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
        await Parallel.ForEachAsync(batches, new ParallelOptions { MaxDegreeOfParallelism = _options.ParallelRequests, CancellationToken = ct }, async (batch, token) =>
        {
            var (query, variables) = BuildQuery(batch.Select(i => requests[i]).ToList(), metadata);
            using var doc = await client.QueryAsync(query, variables, token);
            var data = doc.RootElement.GetProperty("data");
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
                results[batch[n]] = list;
            }
        });
        return results;
    }

    private static (string Query, Action<Utf8JsonWriter> Variables) BuildQuery(IReadOnlyList<SerializeRequest> batch, bool metadata)
    {
        var fields = metadata ? "id parentId templateId path hasChildren dataSignature" : "data";
        var declarations = new StringBuilder();
        var selections = new StringBuilder();
        for (var n = 0; n < batch.Count; n++)
        {
            var a = Alias(n);
            declarations.Append($"${a}P: String! ${a}D: String! ${a}E: [String] ${a}S: TreeScope! ");
            selections.Append($"{a}: serialize(path: ${a}P, database: ${a}D, scope: ${a}S, excludedFieldIds: ${a}E) {{ {fields} }} ");
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
        });
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
        e.TryGetProperty("dataSignature", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null);
}

public sealed class SerializationApiOptions
{
    /// <summary>Single-item requests packed into one HTTP request.</summary>
    public int AliasesPerRequest { get; init; } = 26;

    /// <summary>HTTP requests in flight at once.</summary>
    public int ParallelRequests { get; init; } = 8;
}
