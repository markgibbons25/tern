using System.Globalization;
using System.Text.Json;

using Tern.Core.Configuration;
using Tern.Core.Items;
using Tern.Core.Planning;
using Tern.Core.Signatures;

namespace Tern.Remote.Management;

/// <summary>
/// Carries out the CM side of a Plan through <c>executeSerializationCommands</c>: the last step of
/// a Push or an Endpoint-to-Endpoint push. Commands go out in serial batches; the batch size is a
/// setting because every batch costs the server a template-engine reset and index updates (ADR 0005).
/// </summary>
public sealed class CmApplier(SerializationApi api, IReadOnlyList<Guid> excludedFieldIds, int batchSize, Action<string>? log = null) : IStreamingTreeApplier
{
    private static readonly Guid TemplateTemplateId = new("ab86861a-6030-46c5-b394-e8f99e8b87db");

    private const string Mutation = "mutation($commands: [ItemCommand!]!, $logLevel: SerializationResultLogLevel) { executeSerializationCommands(commands: $commands minimumLogLevel: $logLevel) { name success messages { logLevel message eventID { id name } } } }";

    /// <summary>
    /// A batch also ends at this much command data, so a batch of media items stays a reasonable
    /// request however large the blobs are. A single larger command still goes, on its own.
    /// </summary>
    private const long MaxBatchBytes = 8 * 1024 * 1024;

    /// <summary>The CM takes one batch at a time, so Includes are applied one after another.</summary>
    public bool ConcurrentIncludes => false;

    public async Task<List<AppliedOperation>> ApplyAsync(Include include, IReadOnlyList<PlanOperation> operations, IReadOnlyDictionary<Guid, ItemData> sourceItems, bool checkPreconditions, CancellationToken ct)
    {
        if (checkPreconditions)
        {
            await CheckPreconditionsAsync(include, operations, ct);
        }
        return await SendAsync(include, operations, sourceItems, ct);
    }

    /// <summary>
    /// Applies the operations a batch at a time, reading the source data for each batch just
    /// before it is sent, so a push holds one batch of item data rather than all of it.
    /// </summary>
    public async Task<List<AppliedOperation>> ApplyStreamingAsync(Include include, IReadOnlyList<PlanOperation> operations, Func<IReadOnlyCollection<Guid>, Func<IReadOnlyList<ItemData>, Task>, Task> stream, bool checkPreconditions, CancellationToken ct)
    {
        if (checkPreconditions)
        {
            await CheckPreconditionsAsync(include, operations, ct);
        }
        var applied = new List<AppliedOperation>();
        foreach (var chunk in operations.Chunk(Math.Max(1, batchSize)))
        {
            var ids = chunk.Where(o => o.Action is PlanAction.Create or PlanAction.Update).Select(o => o.Id).ToHashSet();
            var data = new System.Collections.Concurrent.ConcurrentDictionary<Guid, ItemData>();
            if (ids.Count > 0)
            {
                await stream(ids, batch =>
                {
                    foreach (var item in batch)
                    {
                        data[item.Id] = item;
                    }
                    return Task.CompletedTask;
                });
            }
            applied.AddRange(await SendAsync(include, chunk, data, ct));
        }
        return applied;
    }

    private async Task<List<AppliedOperation>> SendAsync(Include include, IReadOnlyList<PlanOperation> operations, IReadOnlyDictionary<Guid, ItemData> sourceItems, CancellationToken ct)
    {
        var updates = operations.Where(o => o.Action == PlanAction.Update).Select(o => o.Id).Distinct().ToList();
        var targetData = updates.Count == 0 ? new Dictionary<Guid, ItemData>() : await api.GetItemDataAsync(include.Database, updates, excludedFieldIds, ct);
        var currentParents = await CurrentParentsAsync(include, operations.Where(o => o.Action == PlanAction.Move).ToList(), ct);

        ItemData Source(PlanOperation op) => sourceItems.GetValueOrDefault(op.Id)
            ?? throw new InvalidOperationException($"Unable to find item data for {op.Path} ({op.Id}), flagged to be {(op.Action == PlanAction.Create ? "created" : "updated")}, in source data store. This should never occur.");

        // A command's size is known (or, for a create, estimated) up front, so a batch can be cut by
        // size as well as count; a create's data is serialized only as its request is written.
        var commands = new List<Command>();
        void Add(PlanOperation op, Guid parent, string command, string data) =>
            commands.Add(new Command(op, data.Length + 200, w => WriteCommand(w, op.Id, parent, include.Database, command, data)));
        foreach (var op in operations)
        {
            switch (op.Action)
            {
                case PlanAction.Create:
                    {
                        var item = Source(op);
                        WarnEmptyVersions(item);
                        commands.Add(new Command(op, item.AllFields.Sum(f => (long)f.Value.Length + 100) + 200,
                            w => WriteCommand(w, op.Id, item.ParentId, include.Database, "CREATE", Serialize(x => ItemDataJson.Write(x, item)))));
                        break;
                    }
                case PlanAction.Update:
                    {
                        var source = Source(op);
                        var target = targetData.GetValueOrDefault(op.Id) ?? throw new InvalidOperationException($"Unable to find item data for {op.Path} ({op.Id}), flagged to be updated, in the CM.");
                        WarnEmptyVersions(source);
                        var payload = UpdatePayload(source, target);
                        if (payload is null)
                        {
                            log?.Invoke($"Skipping empty update command [U] {op.Path} ({op.Id}).");
                            continue;
                        }
                        commands.Add(new Command(op, payload.Length + 200, w => WriteCommand(w, op.Id, source.ParentId, include.Database, "UPDATE", payload)));
                        break;
                    }
                case PlanAction.Move:
                    // The command addresses the item where it is now; its data is the new parent.
                    Add(op, currentParents.GetValueOrDefault(op.Id, op.ParentId), "MOVE", Serialize(x => x.WriteStringValue(op.ParentId)));
                    break;
                case PlanAction.Rename:
                    {
                        var name = ItemPath.Parse(op.Path).Name;
                        Add(op, op.ParentId, "RENAME", Serialize(x => x.WriteStringValue(name)));
                        break;
                    }
                case PlanAction.Delete:
                    Add(op, op.ParentId, "RECYCLE", "{}");
                    break;
            }
        }

        // One batch at a time: the CM's executeSerializationCommands is not safe to run concurrently
        // (two batches at once fail inside the server with NullReferenceException and
        // ArgumentOutOfRangeException, and can leave a batch half written).
        var applied = new List<AppliedOperation>();
        foreach (var batch in Batches(commands))
        {
            foreach (var message in await SendBatchAsync(batch, ct))
            {
                log?.Invoke(message);
            }
            applied.AddRange(batch.Select(c => new AppliedOperation(c.Op, null)));
        }
        return applied;
    }

    private sealed record Command(PlanOperation Op, long Size, Action<Utf8JsonWriter> Write);

    /// <summary>Sends one batch; returns its log messages, or throws with every command the CM rejected.</summary>
    private async Task<List<string>> SendBatchAsync(List<Command> batch, CancellationToken ct)
    {
        using var doc = await api.Client.QueryAsync(Mutation, w =>
        {
            w.WriteStartArray("commands");
            foreach (var c in batch)
            {
                c.Write(w);
            }
            w.WriteEndArray();
            w.WriteString("logLevel", "Information");
        }, ct);
        var results = doc.RootElement.GetProperty("data").GetProperty("executeSerializationCommands");
        var failures = new List<string>();
        var logged = new List<string>();
        foreach (var r in results.EnumerateArray())
        {
            var messages = r.TryGetProperty("messages", out var m) && m.ValueKind == JsonValueKind.Array
                ? m.EnumerateArray().Select(x => x.TryGetProperty("message", out var t) ? t.GetString() : null).Where(x => !string.IsNullOrEmpty(x)).ToList()
                : [];
            if (!r.GetProperty("success").GetBoolean())
            {
                failures.Add($"{r.GetProperty("name").GetString()}: {string.Join("; ", messages)}");
            }
            else
            {
                logged.AddRange(messages!);
            }
        }
        if (failures.Count > 0)
        {
            throw new ManagementException("The CM rejected a write: " + string.Join(Environment.NewLine, failures));
        }
        return logged;
    }

    /// <summary>Where each moving item is now, looked up for all the moves together.</summary>
    private async Task<Dictionary<Guid, Guid>> CurrentParentsAsync(Include include, List<PlanOperation> moves, CancellationToken ct)
    {
        var parents = new Dictionary<Guid, Guid>();
        if (moves.Count == 0)
        {
            return parents;
        }
        var metadata = await api.GetMetadataAsync(moves.Select(m => new SerializeRequest(m.Id.ToString("D"), include.Database, TreeScope.SingleItem, excludedFieldIds)).ToList(), ct);
        for (var n = 0; n < moves.Count; n++)
        {
            if (metadata[n].Count > 0 && metadata[n][0] is { } current)
            {
                parents[moves[n].Id] = current.ParentId;
            }
        }
        return parents;
    }

    /// <summary>Consecutive commands, cut at the batch size or at <see cref="MaxBatchBytes"/> of data, whichever comes first.</summary>
    private IEnumerable<List<Command>> Batches(List<Command> commands)
    {
        var size = Math.Max(1, batchSize);
        var batch = new List<Command>();
        long bytes = 0;
        foreach (var command in commands)
        {
            if (batch.Count > 0 && (batch.Count >= size || bytes + command.Size > MaxBatchBytes))
            {
                yield return batch;
                batch = [];
                bytes = 0;
            }
            batch.Add(command);
            bytes += command.Size;
        }
        if (batch.Count > 0)
        {
            yield return batch;
        }
    }

    public async Task CheckPreconditionsAsync(Include include, IReadOnlyList<PlanOperation> operations, CancellationToken ct)
    {
        var requests = operations.Select(o => new SerializeRequest(o.Id.ToString("D"), include.Database, TreeScope.SingleItem, excludedFieldIds)).ToList();
        var current = await api.GetMetadataAsync(requests, ct);
        for (var i = 0; i < operations.Count; i++)
        {
            var op = operations[i];
            var now = current[i].Count > 0 ? current[i][0] : null;
            var ok = op.Action == PlanAction.Create ? now is null : now is not null && now.Signature == op.ExpectedSignature;
            if (!ok)
            {
                throw new PlanOutOfDateException($"{op.TargetPath ?? op.Path} changed on {api.Client.Host} since the Plan was made. Make a new Plan.");
            }
        }
    }

    /// <summary>The field-level changes that make <paramref name="target"/> match <paramref name="source"/>, or null when there are none.</summary>
    internal static string? UpdatePayload(ItemData source, ItemData target)
    {
        var parts = new List<Action<Utf8JsonWriter>>();
        if (source.TemplateId != target.TemplateId && !(source.TemplateId == Guid.Empty && target.TemplateId == Guid.Empty))
        {
            parts.Add(w => Sub(w, "CHANGE_TEMPLATE", x => x.WriteStringValue(source.TemplateId)));
        }

        var sourceVersions = source.AllVersions.ToList();
        var targetVersions = target.AllVersions.ToList();
        static ItemVersion? Find(List<(ItemLanguage Language, ItemVersion Version)> list, string language, int number) =>
            list.FirstOrDefault(x => x.Language.Name.Equals(language, StringComparison.OrdinalIgnoreCase) && x.Version.Number == number).Version;

        // Versions only in the target are removed.
        foreach (var (l, v) in targetVersions.Where(t => Find(sourceVersions, t.Language.Name, t.Version.Number) is null))
        {
            parts.Add(w => Sub(w, "REMOVE_VERSION", x => Dict(x, ("language", l.Name), ("version", Num(v.Number)))));
        }
        // Versions only in the source are added; their fields follow as updates.
        foreach (var (l, v) in sourceVersions.Where(s => Find(targetVersions, s.Language.Name, s.Version.Number) is null))
        {
            if (v.Fields.Count == 0)
            {
                parts.Add(w => Sub(w, "ADD_VERSION", x => Dict(x, ("language", l.Name), ("version", Num(v.Number)))));
            }
            foreach (var f in v.Fields)
            {
                parts.Add(w => Sub(w, "UPDATE", x => FieldUpdate(x, f, l.Name, v.Number)));
            }
        }
        foreach (var (l, v) in sourceVersions)
        {
            if (Find(targetVersions, l.Name, v.Number) is not { } match)
            {
                continue;
            }
            foreach (var (left, right) in FieldDifferences(v.Fields, match.Fields))
            {
                if (left is not null)
                {
                    parts.Add(w => Sub(w, "UPDATE", x => FieldUpdate(x, left, l.Name, v.Number)));
                }
                else if (right is not null && !(target.TemplateId == TemplateTemplateId && right.Id == WellKnownFields.Revision))
                {
                    parts.Add(w => Sub(w, "RESET_FIELD", x => Dict(x, ("fieldId", Format(right.Id)), ("language", l.Name), ("version", Num(v.Number)))));
                }
            }
        }

        foreach (var language in source.Languages.Concat(target.Languages).Select(l => l.Name).Distinct(StringComparer.Ordinal))
        {
            var left = source.Languages[language]?.UnversionedFields;
            var right = target.Languages[language]?.UnversionedFields;
            if ((left is null || left.Count == 0) && (right is null || right.Count == 0))
            {
                continue;
            }
            foreach (var (l, r) in FieldDifferences(left ?? new FieldList(), right ?? new FieldList()))
            {
                if (l is not null)
                {
                    parts.Add(w => Sub(w, "UPDATE", x => FieldUpdate(x, l, language, null)));
                }
                else if (r is not null)
                {
                    parts.Add(w => Sub(w, "RESET_FIELD", x => Dict(x, ("fieldId", Format(r.Id)), ("language", language))));
                }
            }
        }

        foreach (var (l, r) in FieldDifferences(source.SharedFields, target.SharedFields))
        {
            if (l is not null)
            {
                parts.Add(w => Sub(w, "UPDATE", x => FieldUpdate(x, l, null, null)));
            }
            else if (r is not null)
            {
                parts.Add(w => Sub(w, "RESET_FIELD", x => Dict(x, ("fieldId", Format(r.Id)))));
            }
        }

        if (parts.Count == 0)
        {
            return null;
        }
        return Serialize(w =>
        {
            w.WriteStartArray();
            foreach (var part in parts)
            {
                part(w);
            }
            w.WriteEndArray();
        });
    }

    /// <summary>Fields only in the source, fields that differ, and fields only in the target.</summary>
    private static IEnumerable<(FieldValue? Left, FieldValue? Right)> FieldDifferences(FieldList left, FieldList right)
    {
        foreach (var l in left)
        {
            if (right[l.Id] is null)
            {
                yield return (l, null);
            }
        }
        foreach (var r in right)
        {
            if (left[r.Id] is null)
            {
                yield return (null, r);
            }
        }
        foreach (var r in right)
        {
            if (left[r.Id] is { } l && IsDifferent(l, r))
            {
                yield return (l, r);
            }
        }
    }

    private static bool IsDifferent(FieldValue left, FieldValue right)
    {
        if (left.BlobId is { } a && right.BlobId is { } b && a != b)
        {
            return true;
        }
        return !Strip(left.Value).Equals(Strip(right.Value), StringComparison.Ordinal);
    }

    private static string Strip(string value) => value.Replace("\r", string.Empty).Replace("\n", string.Empty);

    private void WarnEmptyVersions(ItemData item)
    {
        foreach (var (l, v) in item.AllVersions.Where(x => x.Version.Fields.Count == 0))
        {
            log?.Invoke($"Item {item.Path} {{{item.Id}}} with language: ({l.Name}) in version: ({v.Number}) doesn't have any fields.");
        }
    }

    private static void FieldUpdate(Utf8JsonWriter w, FieldValue f, string? language, int? version)
    {
        w.WriteStartObject();
        w.WriteString("fieldId", Format(f.Id));
        w.WriteString("value", f.Value);
        if (f.BlobId is { } blob)
        {
            w.WriteString("blobId", Format(blob));
        }
        if (language is not null)
        {
            w.WriteString("language", language);
        }
        if (version is { } v)
        {
            w.WriteString("version", Num(v));
        }
        w.WriteEndObject();
    }

    private static void Sub(Utf8JsonWriter w, string command, Action<Utf8JsonWriter> data)
    {
        w.WriteStartObject();
        w.WriteString("command", command);
        w.WritePropertyName("data");
        data(w);
        w.WriteEndObject();
    }

    private static void Dict(Utf8JsonWriter w, params (string Key, string Value)[] pairs)
    {
        w.WriteStartObject();
        foreach (var (k, v) in pairs)
        {
            w.WriteString(k, v);
        }
        w.WriteEndObject();
    }

    private static void WriteCommand(Utf8JsonWriter w, Guid itemId, Guid parentId, string database, string command, string data)
    {
        w.WriteStartObject();
        w.WriteString("itemID", itemId);
        w.WriteString("parentID", Format(parentId));
        w.WriteString("database", database);
        w.WriteString("command", command);
        w.WriteString("data", data);
        w.WriteEndObject();
    }

    private static string Serialize(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            write(w);
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string Format(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

    private static string Num(int n) => n.ToString(CultureInfo.InvariantCulture);
}
