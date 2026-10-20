using System.Text.Json;

namespace Tern.Remote.Management;

/// <summary>A server-side job: a publish, an index rebuild or a database cleanup task.</summary>
public sealed record JobStatus(string Id, string? Name, long ProcessedCount, string StateCode, string StateName)
{
    public bool IsFinished => StateName is "Finished" or "Failed" or "Canceled" or "Cancelled" || StateCode is "Finished" or "Failed" or "Canceled";

    public bool IsFailed => StateName is "Failed" || StateCode is "Failed";

    internal static JobStatus Parse(JsonElement e, string? nameProperty = null) => new(
        e.GetProperty("id").ValueKind == JsonValueKind.String ? e.GetProperty("id").GetString()! : e.GetProperty("id").GetRawText(),
        nameProperty is not null && e.TryGetProperty(nameProperty, out var n) ? n.GetString() : null,
        e.TryGetProperty("processedCount", out var p) && p.TryGetInt64(out var c) ? c : 0,
        e.TryGetProperty("stateCode", out var sc) ? sc.ToString() : string.Empty,
        e.TryGetProperty("stateName", out var sn) ? sn.GetString() ?? string.Empty : string.Empty);
}

public sealed class PublishingApi(ManagementClient client)
{
    private const string JobFields = "id processedCount stateCode stateName";

    public async Task<JobStatus> PublishAsync(string? path, IReadOnlyList<string> languages, IReadOnlyList<string> itemIds, bool republish, string? target, CancellationToken ct)
    {
        var query = $"mutation($path: String, $languages: [String], $itemIds: [String], $republish: Boolean, $target: String) {{ publish(path: $path, languages: $languages, itemIds: $itemIds, republish: $republish, target: $target) {{ {JobFields} }} }}";
        using var doc = await client.QueryAsync(query, w =>
        {
            WriteNullable(w, "path", path);
            WriteArray(w, "languages", languages);
            WriteArray(w, "itemIds", itemIds);
            w.WriteBoolean("republish", republish);
            WriteNullable(w, "target", target);
        }, ct);
        return JobStatus.Parse(doc.RootElement.GetProperty("data").GetProperty("publish"));
    }

    public async Task<JobStatus> PublishItemAsync(string path, IReadOnlyList<string> languages, bool republish, string? target, bool related, bool subItems, CancellationToken ct)
    {
        var query = $"mutation($path: String, $languages: [String], $republish: Boolean, $target: String, $related: Boolean, $subItems: Boolean) {{ singleItemPublish(path: $path, languages: $languages, republish: $republish, target: $target, related: $related, subItems: $subItems) {{ {JobFields} }} }}";
        using var doc = await client.QueryAsync(query, w =>
        {
            w.WriteString("path", path);
            WriteArray(w, "languages", languages);
            w.WriteBoolean("republish", republish);
            WriteNullable(w, "target", target);
            w.WriteBoolean("related", related);
            w.WriteBoolean("subItems", subItems);
        }, ct);
        return JobStatus.Parse(doc.RootElement.GetProperty("data").GetProperty("singleItemPublish"));
    }

    public async Task<JobStatus> StatusAsync(string id, CancellationToken ct)
    {
        using var doc = await client.QueryAsync($"query($id: String) {{ publishingStatus(id: $id) {{ {JobFields} }} }}", w => w.WriteString("id", id), ct);
        return JobStatus.Parse(doc.RootElement.GetProperty("data").GetProperty("publishingStatus"));
    }

    public async Task<IReadOnlyList<string>> TargetsAsync(CancellationToken ct)
    {
        using var doc = await client.QueryAsync("query { listOfTargets }", null, ct);
        return Strings(doc.RootElement.GetProperty("data").GetProperty("listOfTargets"));
    }

    internal static void WriteNullable(Utf8JsonWriter w, string name, string? value)
    {
        if (value is null)
        {
            w.WriteNull(name);
        }
        else
        {
            w.WriteString(name, value);
        }
    }

    internal static void WriteArray(Utf8JsonWriter w, string name, IReadOnlyList<string> values)
    {
        w.WriteStartArray(name);
        foreach (var v in values)
        {
            w.WriteStringValue(v);
        }
        w.WriteEndArray();
    }

    internal static List<string> Strings(JsonElement e) =>
        e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : x.GetRawText()).ToList() : [];
}

public sealed record IndexStatistic(string Name, bool HasDeletions, string? LastUpdated, long Documents, long Fields, long Terms, string? RebuildTime, string? RebuildThroughputTime);

public sealed class IndexingApi(ManagementClient client)
{
    private const string JobFields = "id indexName processedCount stateCode stateName";

    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken ct)
    {
        using var doc = await client.QueryAsync("query { indexesList }", null, ct);
        return PublishingApi.Strings(doc.RootElement.GetProperty("data").GetProperty("indexesList"));
    }

    public async Task<IReadOnlyList<IndexStatistic>> StatisticsAsync(CancellationToken ct)
    {
        const string query = "query { indexesStatistic { showShortStatistic statisticList { name hasDeletions lastUpdated numberOfDocuments numberOfFields numberOfTerms rebuildTime rebuildThroughputTime } } }";
        using var doc = await client.QueryAsync(query, null, ct);
        var result = new List<IndexStatistic>();
        var stats = doc.RootElement.GetProperty("data").GetProperty("indexesStatistic");
        if (stats.TryGetProperty("statisticList", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in list.EnumerateArray())
            {
                result.Add(new IndexStatistic(
                    s.GetProperty("name").GetString()!,
                    s.TryGetProperty("hasDeletions", out var d) && d.ValueKind == JsonValueKind.True,
                    s.TryGetProperty("lastUpdated", out var u) ? u.ToString() : null,
                    Long(s, "numberOfDocuments"), Long(s, "numberOfFields"), Long(s, "numberOfTerms"),
                    s.TryGetProperty("rebuildTime", out var rt) ? rt.ToString() : null,
                    s.TryGetProperty("rebuildThroughputTime", out var rtt) ? rtt.ToString() : null));
            }
        }
        return result;
    }

    public Task<IReadOnlyList<JobStatus>> RebuildAsync(IReadOnlyList<string> indexNames, CancellationToken ct) => Jobs("rebuildIndex", indexNames, ct);

    public Task<IReadOnlyList<JobStatus>> PopulateSchemaAsync(IReadOnlyList<string> indexNames, CancellationToken ct) => Jobs("populateSchema", indexNames, ct);

    public async Task<IReadOnlyList<JobStatus>> StatusAsync(IReadOnlyList<string> jobIds, CancellationToken ct)
    {
        using var doc = await client.QueryAsync($"query($jobIds: [String]) {{ indexingStatus(jobIds: $jobIds) {{ {JobFields} }} }}", w => PublishingApi.WriteArray(w, "jobIds", jobIds), ct);
        return doc.RootElement.GetProperty("data").GetProperty("indexingStatus").EnumerateArray().Select(e => JobStatus.Parse(e, "indexName")).ToList();
    }

    private async Task<IReadOnlyList<JobStatus>> Jobs(string mutation, IReadOnlyList<string> indexNames, CancellationToken ct)
    {
        using var doc = await client.QueryAsync($"mutation($indexNames: [String]) {{ {mutation}(indexNames: $indexNames) {{ {JobFields} }} }}", w => PublishingApi.WriteArray(w, "indexNames", indexNames), ct);
        return doc.RootElement.GetProperty("data").GetProperty(mutation).EnumerateArray().Select(e => JobStatus.Parse(e, "indexName")).ToList();
    }

    private static long Long(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;
}

public sealed class DatabaseApi(ManagementClient client)
{
    public async Task<IReadOnlyList<JobStatus>> CleanupAsync(IReadOnlyList<string> databases, IReadOnlyList<string> tasks, CancellationToken ct)
    {
        const string query = "mutation($databaseNames: [String], $tasks: [String]) { databaseCleanup(databaseNames: $databaseNames, tasks: $tasks) { id databaseName taskName stateCode stateName } }";
        using var doc = await client.QueryAsync(query, w =>
        {
            PublishingApi.WriteArray(w, "databaseNames", databases);
            PublishingApi.WriteArray(w, "tasks", tasks);
        }, ct);
        return doc.RootElement.GetProperty("data").GetProperty("databaseCleanup").EnumerateArray().Select(Parse).ToList();
    }

    public async Task<IReadOnlyList<JobStatus>> StatusAsync(IReadOnlyList<string> jobIds, CancellationToken ct)
    {
        const string query = "query($jobIds: [String]) { databaseCleanupStatus(jobIds: $jobIds) { id databaseName taskName stateCode stateName } }";
        using var doc = await client.QueryAsync(query, w => PublishingApi.WriteArray(w, "jobIds", jobIds), ct);
        return doc.RootElement.GetProperty("data").GetProperty("databaseCleanupStatus").EnumerateArray().Select(Parse).ToList();
    }

    private static JobStatus Parse(JsonElement e)
    {
        var job = JobStatus.Parse(e);
        var db = e.TryGetProperty("databaseName", out var d) ? d.GetString() : null;
        var task = e.TryGetProperty("taskName", out var t) ? t.GetString() : null;
        return job with { Name = $"{db}: {task}" };
    }
}

public sealed record CleanupResult(string Database, string ItemsInfo, long Count);

public sealed class ResourcePackageApi(ManagementClient client)
{
    public async Task<string> CleanAsync(bool force, bool whatIf, string? path, bool recurse, CancellationToken ct)
    {
        const string query = "mutation($force: Boolean, $whatIf: Boolean, $path: String, $recurse: Boolean) { clean(force: $force, whatIf: $whatIf, path: $path, recurse: $recurse) { id stateName } }";
        using var doc = await client.QueryAsync(query, w =>
        {
            w.WriteBoolean("force", force);
            w.WriteBoolean("whatIf", whatIf);
            PublishingApi.WriteNullable(w, "path", path);
            w.WriteBoolean("recurse", recurse);
        }, ct);
        var clean = doc.RootElement.GetProperty("data").GetProperty("clean");
        return clean.GetProperty("id").ToString();
    }

    public async Task<(string State, string? Error, IReadOnlyList<CleanupResult> Results)> StatusAsync(string jobId, CancellationToken ct)
    {
        const string query = "query($jobId: String) { cleanupstatus(jobId: $jobId) { id stateName errorMessage result { database itemsInfo count } } }";
        using var doc = await client.QueryAsync(query, w => w.WriteString("jobId", jobId), ct);
        var s = doc.RootElement.GetProperty("data").GetProperty("cleanupstatus");
        var results = new List<CleanupResult>();
        if (s.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in r.EnumerateArray())
            {
                results.Add(new CleanupResult(x.GetProperty("database").GetString() ?? string.Empty, x.TryGetProperty("itemsInfo", out var i) ? i.ToString() : string.Empty, x.TryGetProperty("count", out var c) && c.TryGetInt64(out var n) ? n : 0));
            }
        }
        return (s.GetProperty("stateName").GetString() ?? string.Empty, s.TryGetProperty("errorMessage", out var e) ? e.GetString() : null, results);
    }
}

public sealed record SystemInfo(string SitecoreInfo, string ManagementServicesName, string ManagementServicesVersion);

public sealed class SystemApi(ManagementClient client)
{
    public async Task<SystemInfo> InfoAsync(CancellationToken ct)
    {
        using var doc = await client.QueryAsync("query { system { sitecoreInfo managementServices { name version } } }", null, ct);
        var s = doc.RootElement.GetProperty("data").GetProperty("system");
        var ms = s.GetProperty("managementServices");
        return new SystemInfo(s.GetProperty("sitecoreInfo").GetString() ?? string.Empty, ms.GetProperty("name").GetString() ?? string.Empty, ms.GetProperty("version").GetString() ?? string.Empty);
    }

    /// <summary>The names of the schema's root query and mutation fields, for feature detection (ADR 0006).</summary>
    public async Task<(IReadOnlySet<string> Queries, IReadOnlySet<string> Mutations)> RootFieldsAsync(CancellationToken ct)
    {
        using var doc = await client.QueryAsync("query { __schema { queryType { fields { name } } mutationType { fields { name } } } }", null, ct);
        var schema = doc.RootElement.GetProperty("data").GetProperty("__schema");
        static HashSet<string> Names(JsonElement type) =>
            type.ValueKind == JsonValueKind.Object && type.TryGetProperty("fields", out var f) && f.ValueKind == JsonValueKind.Array
                ? f.EnumerateArray().Select(x => x.GetProperty("name").GetString()!).ToHashSet(StringComparer.Ordinal)
                : [];
        return (Names(schema.GetProperty("queryType")), Names(schema.GetProperty("mutationType")));
    }
}
