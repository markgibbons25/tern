using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

using Tern.Core.Transfer;
using Tern.Remote.Auth;
using Tern.Remote.Management;

namespace Tern.Remote.Transfer;

/// <summary>
/// Sends requests to one CM with a bearer token, and on a 401 or 403 gets a new token and tries
/// once more, as <see cref="ManagementClient"/> does.
/// </summary>
public sealed class CmRestClient(HttpClient http, string host, TokenProvider tokens)
{
    public string Host { get; } = host.TrimEnd('/');

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, Func<HttpContent?>? content, CancellationToken ct)
    {
        var token = await tokens.GetTokenAsync(ct);
        var response = await SendOnceAsync(method, path, content, token, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            response.Dispose();
            token = await tokens.ForceRefreshAsync(token, ct);
            response = await SendOnceAsync(method, path, content, token, ct);
        }
        return response;
    }

    /// <summary>Sends a request and fails unless the CM answers with a success status.</summary>
    public async Task<HttpResponseMessage> SendCheckedAsync(HttpMethod method, string path, Func<HttpContent?>? content, CancellationToken ct)
    {
        var response = await SendAsync(method, path, content, ct);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            var text = await response.Content.ReadAsStringAsync(ct);
            response.Dispose();
            var hint = status is 401 or 403 ? " The transfer APIs need an automation client of an Organization Admin or Owner." : string.Empty;
            throw new ManagementException($"{method} {Host}{path} returned {status}: {ErrorText(text)}{hint}", statusCode: status);
        }
        return response;
    }

    public async Task<JsonDocument> GetJsonAsync(string path, CancellationToken ct)
    {
        using var response = await SendCheckedAsync(HttpMethod.Get, path, null, ct);
        return JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
    }

    private async Task<HttpResponseMessage> SendOnceAsync(HttpMethod method, string path, Func<HttpContent?>? content, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, Host + path) { Content = content?.Invoke() };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
    }

    /// <summary>The API's <c>{"Error": "..."}</c> message, or the start of the body.</summary>
    private static string ErrorText(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("Error", out var error) && error.ValueKind == JsonValueKind.String)
            {
                return error.GetString()!;
            }
        }
        catch (JsonException)
        {
        }
        return text.Length > 500 ? text[..500] + "…" : text;
    }
}

public sealed record ChunkSet(Guid Id, int ChunkCount, int TotalItemCount);

public sealed record TransferStatus(string State, IReadOnlyList<ChunkSet> ChunkSets);

/// <summary>One chunk as the source sent it, to be passed on unchanged.</summary>
public sealed record Chunk(byte[] Data, bool IsMedia, int? ItemsProcessed, int? ItemsSkipped);

/// <summary>
/// The Content Transfer API (<c>/sitecore/api/content/transfer/v1</c>). Creating a transfer,
/// polling it, reading chunks and deleting it happen on the source; saving chunks and completing
/// chunk sets happen on the destination.
/// </summary>
public sealed class ContentTransferApi(CmRestClient client)
{
    public const string ServicePath = "/sitecore/api/content/transfer/v1/transfers";

    public CmRestClient Client => client;

    public async Task CreateAsync(Guid transferId, string database, IReadOnlyList<DataTree> trees, CancellationToken ct)
    {
        var body = CreateBody(transferId, database, trees);
        using var _ = await client.SendCheckedAsync(HttpMethod.Post, ServicePath, () => new ByteArrayContent(body) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } }, ct);
    }

    public static byte[] CreateBody(Guid transferId, string database, IReadOnlyList<DataTree> trees)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("TransferId", transferId.ToString("D"));
            w.WriteStartObject("Configuration");
            w.WriteStartArray("DataTrees");
            foreach (var tree in trees)
            {
                w.WriteStartObject();
                w.WriteString("ItemPath", tree.Path.ToString());
                w.WriteString("Scope", tree.Scope);
                w.WriteString("MergeStrategy", tree.Merge.ToString());
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteString("Database", database);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return stream.ToArray();
    }

    public async Task<TransferStatus> StatusAsync(Guid transferId, CancellationToken ct)
    {
        using var doc = await client.GetJsonAsync($"{ServicePath}/{transferId:D}/status", ct);
        return ParseStatus(doc.RootElement);
    }

    public static TransferStatus ParseStatus(JsonElement root)
    {
        var state = root.TryGetProperty("State", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : "Unknown";
        var sets = new List<ChunkSet>();
        if (root.TryGetProperty("ChunkSetsMetadata", out var meta) && meta.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in meta.EnumerateArray())
            {
                sets.Add(new ChunkSet(
                    Guid.Parse(m.GetProperty("ChunkSetId").GetString()!),
                    m.TryGetProperty("ChunkCount", out var c) ? c.GetInt32() : 0,
                    m.TryGetProperty("TotalItemCount", out var t) ? t.GetInt32() : 0));
            }
        }
        return new TransferStatus(state, sets);
    }

    public async Task<Chunk> GetChunkAsync(Guid transferId, Guid chunkSetId, int chunkId, CancellationToken ct)
    {
        using var response = await client.SendCheckedAsync(HttpMethod.Get, $"{ServicePath}/{transferId:D}/chunksets/{chunkSetId:D}/chunks/{chunkId}", null, ct);
        var data = await response.Content.ReadAsByteArrayAsync(ct);
        var disposition = response.Content.Headers.TryGetValues("Content-Disposition", out var values) ? string.Join(";", values) : string.Empty;
        var isMedia = DispositionValue(disposition, "IsMedia") is { } m
            ? bool.TryParse(m, out var b) ? b : throw new ManagementException($"The chunk's IsMedia value '{m}' is not true or false.")
            : throw new ManagementException("The source sent a chunk without an IsMedia value in its Content-Disposition header, so it cannot be saved.");
        return new Chunk(data, isMedia, Int(DispositionValue(disposition, "ItemsProcessed")), Int(DispositionValue(disposition, "ItemsSkipped")));
    }

    public async Task SaveChunkAsync(Guid transferId, Guid chunkSetId, int chunkId, Chunk chunk, CancellationToken ct)
    {
        var path = $"{ServicePath}/{transferId:D}/chunksets/{chunkSetId:D}/chunks/{chunkId}?isMedia={(chunk.IsMedia ? "true" : "false")}";
        using var _ = await client.SendCheckedAsync(HttpMethod.Put, path, () => new ByteArrayContent(chunk.Data) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } }, ct);
    }

    /// <summary>Turns a destination's saved chunks into a <c>.raif</c> file and returns its name.</summary>
    public async Task<string> CompleteChunkSetAsync(Guid transferId, Guid chunkSetId, CancellationToken ct)
    {
        using var response = await client.SendCheckedAsync(HttpMethod.Post, $"{ServicePath}/{transferId:D}/chunksets/{chunkSetId:D}/complete", null, ct);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
        return doc.RootElement.TryGetProperty("ContentTransferFileName", out var name) && name.GetString() is { Length: > 0 } file
            ? file
            : throw new ManagementException("The destination completed the chunk set but did not name its .raif file.");
    }

    public async Task DeleteAsync(Guid transferId, CancellationToken ct)
    {
        using var response = await client.SendAsync(HttpMethod.Delete, $"{ServicePath}/{transferId:D}", null, ct);
    }

    /// <summary>A parameter of a Content-Disposition header, such as <c>IsMedia=true</c>.</summary>
    public static string? DispositionValue(string header, string name)
    {
        var match = Regex.Match(header, $@"(?:^|[;\s]){Regex.Escape(name)}\s*=\s*""?([^;""]*)""?", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static int? Int(string? value) => int.TryParse(value, out var n) ? n : null;
}

public sealed record BlobState(string State, string? Error);

public sealed record ItemTransferState(string Id, string State, int? TotalItems, int? TransferredItems, IReadOnlyList<string> ValidationErrors);

/// <summary>
/// The Item Transfer API (<c>/sitecore/shell/api/v3/ItemsTransfer</c>) on the destination: it
/// loads <c>.raif</c> files into a database.
/// </summary>
public sealed class ItemTransferApi(CmRestClient client)
{
    public const string ServicePath = "/sitecore/shell/api/v3/ItemsTransfer";

    public async Task<BlobState> BlobStateAsync(string blobName, CancellationToken ct)
    {
        using var response = await client.SendAsync(HttpMethod.Get, $"{ServicePath}/sources/blobs/{Uri.EscapeDataString(blobName)}", null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new BlobState("NotFound", null);
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new ManagementException($"{client.Host}{ServicePath}/sources/blobs/{blobName} returned {(int)response.StatusCode}.", statusCode: (int)response.StatusCode);
        }
        using var doc = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
        return new BlobState(Str(doc.RootElement, "BlobState") ?? "Unknown", Str(doc.RootElement, "Error"));
    }

    /// <summary>Starts loading a blob into <paramref name="database"/>.</summary>
    public async Task ConsumeAsync(string database, string blobName, CancellationToken ct)
    {
        using var _ = await client.SendCheckedAsync(HttpMethod.Post, $"{ServicePath}/transfers/databases/{Uri.EscapeDataString(database)}/sources?blobName={Uri.EscapeDataString(blobName)}", null, ct);
    }

    /// <summary>
    /// The state of the load of <paramref name="blobName"/>, or null before it is listed. A load
    /// has an ID of its own (<c>consumed.&lt;time&gt;.&lt;guid&gt;</c>), which only the list of
    /// transfers gives; the <c>location</c> header of <see cref="ConsumeAsync"/> names the blob.
    /// </summary>
    public async Task<ItemTransferState?> TransferStateAsync(string blobName, CancellationToken ct)
    {
        const int pageSize = 50;
        for (var page = 1; ; page++)
        {
            using var list = await client.GetJsonAsync($"{ServicePath}/transfers?page={page}&pageSize={pageSize}", ct);
            if (!list.RootElement.TryGetProperty("Transfers", out var transfers) || transfers.ValueKind != JsonValueKind.Array || transfers.GetArrayLength() == 0)
            {
                return null;
            }
            foreach (var t in transfers.EnumerateArray())
            {
                if (!string.Equals(Str(t, "SourceName"), blobName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                // The details add the item counts and validation errors the list leaves out.
                if (Str(t, "Id") is { } id)
                {
                    using var response = await client.SendAsync(HttpMethod.Get, $"{ServicePath}/transfers/{Uri.EscapeDataString(id)}", null, ct);
                    if (response.IsSuccessStatusCode)
                    {
                        using var doc = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
                        return Parse(doc.RootElement);
                    }
                }
                return Parse(t);
            }
            if (page * pageSize >= (Int(list.RootElement, "TotalCount") ?? 0))
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Deletes the <c>.raif</c> files of loads that have finished, as the docs advise once a load
    /// is done. A load still queued or running keeps its file. Returns the files deleted and how
    /// many loads are still queued or running.
    /// </summary>
    public async Task<(List<string> Deleted, int Pending)> DeleteFinishedBlobsAsync(CancellationToken ct)
    {
        const int pageSize = 50;
        var finished = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = 0;
        for (var page = 1; ; page++)
        {
            using var list = await client.GetJsonAsync($"{ServicePath}/transfers?page={page}&pageSize={pageSize}", ct);
            if (!list.RootElement.TryGetProperty("Transfers", out var transfers) || transfers.ValueKind != JsonValueKind.Array || transfers.GetArrayLength() == 0)
            {
                break;
            }
            foreach (var t in transfers.EnumerateArray())
            {
                var state = Str(t, "TransferState");
                if (state == "Finished" && Str(t, "SourceName") is { } name)
                {
                    finished.Add(name);
                }
                else if (state is "Unknown" or "Queued" or "InProgress")
                {
                    pending++;
                }
            }
            if (page * pageSize >= (Int(list.RootElement, "TotalCount") ?? 0))
            {
                break;
            }
        }
        var deleted = new List<string>();
        using var blobs = await client.GetJsonAsync($"{ServicePath}/sources/blobs?page=1&pageSize={pageSize}", ct);
        if (blobs.RootElement.TryGetProperty("Sources", out var sources) && sources.ValueKind == JsonValueKind.Array)
        {
            deleted.AddRange(sources.EnumerateArray()
                .Where(s => Str(s, "BlobState") == "Transferred" && Str(s, "Name") is { } n && finished.Contains(n))
                .Select(s => Str(s, "Name")!));
        }
        using var gate = new SemaphoreSlim(8);
        await Task.WhenAll(deleted.Select(async name =>
        {
            await gate.WaitAsync(ct);
            try
            {
                await DeleteBlobAsync(name, ct);
            }
            finally
            {
                gate.Release();
            }
        }));
        return (deleted, pending);
    }

    public async Task RetryAsync(string database, string sourceName, CancellationToken ct)
    {
        using var _ = await client.SendCheckedAsync(HttpMethod.Put, $"{ServicePath}/transfers/databases/{Uri.EscapeDataString(database)}/sources/{Uri.EscapeDataString(sourceName)}", null, ct);
    }

    public async Task DeleteBlobAsync(string blobName, CancellationToken ct)
    {
        using var response = await client.SendAsync(HttpMethod.Delete, $"{ServicePath}/sources/blobs/{Uri.EscapeDataString(blobName)}", null, ct);
    }

    public static ItemTransferState Parse(JsonElement e)
    {
        var errors = new List<string>();
        if (e.TryGetProperty("ValidationErrors", out var v) && v.ValueKind == JsonValueKind.Array)
        {
            errors.AddRange(v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!));
        }
        return new ItemTransferState(Str(e, "Id") ?? string.Empty, Str(e, "TransferState") ?? "Unknown", Int(e, "TotalItemsCount"), Int(e, "TransferredItemsCount"), errors);
    }

    private static string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static int? Int(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : null;
}
