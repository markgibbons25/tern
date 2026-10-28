using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

using Tern.Cli.Common;
using Tern.Core.Configuration;
using Tern.Core.Transfer;
using Tern.Remote.Login;
using Tern.Remote.Management;
using Tern.Remote.Transfer;

namespace Tern.Cli.Commands;

/// <summary>
/// <c>content transfer</c>: copies content between two SitecoreAI environments through the
/// Content Transfer API and loads it with the Item Transfer API. The data streams from CM to CM
/// in chunks; nothing is written locally.
/// </summary>
public static class TransferRunner
{
    /// <summary>How often the transfer's state is checked; tests shorten it.</summary>
    internal static TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan PollTimeout = TimeSpan.FromHours(1);

    public static async Task<int> RunAsync(Session session, ContentRequest request, TransferOptions options, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var sourceName = request.Source ?? throw new CliException("--source is required.");
        var targetName = request.Target ?? throw new CliException("--target is required.");
        var (modules, scope) = ContentCommands.Resolve(session, request);
        var source = session.Endpoint(sourceName, announce: false);
        var target = session.Endpoint(targetName, announce: false);
        if (source.Name.Equals(target.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new CliException("The source and target cannot be the same endpoint.");
        }
        if (!target.AllowWrite && !request.WhatIf)
        {
            throw new CliException(ReadOnlyEndpoint.Message(target, "transfer to it"));
        }
        if (options.Merge == MergeStrategy.OverrideExistingTree && !options.Yes && !request.WhatIf)
        {
            throw new CliException("--merge OverrideExistingTree deletes everything under each transferred tree in the target before writing. Add --yes to go ahead, or run with --what-if first.");
        }

        var output = session.Output;
        ContentCommands.PrintHeader(output, "Content transfer",
        [
            ("Source", ContentCommands.Paint(output, source, writes: false)),
            ("Target", ContentCommands.Paint(output, target, writes: !request.WhatIf)),
            ("Scope", scope),
            ("Merge", $"{options.Merge}"),
        ]);
        output.Info(string.Empty);

        // Which Data Trees carry exactly what the Includes serialize, read from the source.
        var includes = modules.SelectMany(m => m.Includes).ToList();
        if (includes.Count == 0)
        {
            throw new CliException("The selected modules have no item includes to transfer.");
        }
        var sourceTree = new CmItemTree(session.Serialization(source), [], source.Name);
        var targetTree = new CmItemTree(session.Serialization(target), [], target.Name);
        await sourceTree.PrefetchAsync(includes, ct);
        var plan = new List<(string Database, List<DataTree> Trees)>();
        foreach (var group in includes.GroupBy(i => i.Database, StringComparer.OrdinalIgnoreCase))
        {
            var trees = new List<DataTree>();
            foreach (var include in group)
            {
                var cover = TransferCover.Cover(include, await sourceTree.GetMetadataAsync(include, ct), options.Merge);
                if (cover.Count == 0)
                {
                    output.Warn($"[{include.Database}] {include.Path} does not exist in {source.Name}; nothing to transfer for {include.Module.Namespace}/{include.Name}.");
                }
                trees.AddRange(cover.Where(t => trees.All(o => o.Id != t.Id)));
            }
            if (trees.Count > 0)
            {
                plan.Add((group.Key, trees));
            }
        }
        if (plan.Count == 0)
        {
            throw new CliException("There is nothing to transfer.");
        }

        // An item whose parent is missing in the target transfers, but stays out of the content tree.
        // That is only a warning, and a target busy loading an earlier transfer can take seconds to
        // answer, so the check runs alongside the transfer rather than before it.
        async Task<List<string>> CheckParentsAsync()
        {
            var warnings = new List<string>();
            foreach (var (database, trees) in plan)
            {
                var include = includes.First(i => i.Database.Equals(database, StringComparison.OrdinalIgnoreCase));
                var ids = trees.Select(t => t.Id).ToHashSet();
                var parents = trees.Select(t => t.ParentId).Where(p => !ids.Contains(p)).Distinct().ToList();
                var found = (await targetTree.GetByIdsAsync(include, parents, ct)).Select(m => m.Id).ToHashSet();
                warnings.AddRange(trees.Where(t => !ids.Contains(t.ParentId) && !found.Contains(t.ParentId))
                    .Select(t => $"[{database}] The parent of {t.Path} ({t.ParentId}) is not in {target.Name}, so the item transfers but does not appear in the content tree. Transfer its parent too."));
            }
            return warnings;
        }
        var parentCheck = CheckParentsAsync();
        if (request.WhatIf)
        {
            (await parentCheck).ForEach(output.Warn);
        }

        foreach (var (database, trees) in plan)
        {
            foreach (var tree in trees)
            {
                output.Info($"[{database}] {tree.Path} ({tree.Scope}, {tree.Merge}, {tree.ItemCount} items)");
            }
            output.Info($"[{database}] {trees.Count} data trees, {trees.Sum(t => t.ItemCount)} items.");
        }

        var results = new List<TransferResult>();
        if (!request.WhatIf)
        {
            var sourceApi = new ContentTransferApi(session.Rest(source));
            var targetApi = new ContentTransferApi(session.Rest(target));
            var items = new ItemTransferApi(targetApi.Client);
            // A transfer that returns once its items are available cannot delete its file after the
            // load finishes, so each one tidies up the finished loads of earlier ones.
            var cleanup = items.DeleteFinishedBlobsAsync(ct);
            foreach (var (database, trees) in plan)
            {
                results.Add(await TransferAsync(session, sourceApi, targetApi, items, database, trees, options, ct));
            }
            (await parentCheck).ForEach(output.Warn);
            try
            {
                var (deleted, pending) = await cleanup;
                if (deleted.Count > 0)
                {
                    output.Info($"Deleted {deleted.Count} files of earlier transfers whose loads have finished.");
                }
                if (pending > 0)
                {
                    // Loads run one at a time, and the target answers slowly while they do.
                    output.Warn($"{target.Name} was still writing {pending} earlier transfers to its database when this one started; this one's load queues behind them.");
                }
            }
            catch (Exception ex) when (ex is ManagementException or HttpRequestException)
            {
                output.Warn($"Could not tidy up the files of earlier transfers: {ex.Message}");
            }
        }

        output.Document("content transfer", w =>
        {
            w.WriteString("source", source.Name);
            w.WriteString("target", target.Name);
            w.WriteBoolean("whatIf", request.WhatIf);
            w.WriteString("merge", options.Merge.ToString());
            w.WriteStartArray("dataTrees");
            foreach (var (database, trees) in plan)
            {
                foreach (var tree in trees)
                {
                    w.WriteStartObject();
                    w.WriteString("database", database);
                    w.WriteString("path", tree.Path.ToString());
                    w.WriteString("id", tree.Id);
                    w.WriteString("scope", tree.Scope);
                    w.WriteString("mergeStrategy", tree.Merge.ToString());
                    w.WriteNumber("items", tree.ItemCount);
                    w.WriteEndObject();
                }
            }
            w.WriteEndArray();
            w.WriteStartArray("transfers");
            foreach (var r in results)
            {
                w.WriteStartObject();
                w.WriteString("database", r.Database);
                w.WriteString("transferId", r.TransferId);
                w.WriteNumber("prepareMs", r.PrepareMs);
                w.WriteNumber("copyMs", r.CopyMs);
                w.WriteStartArray("files");
                foreach (var f in r.Files)
                {
                    w.WriteStartObject();
                    w.WriteString("name", f.Name);
                    w.WriteNumber("chunks", f.Chunks);
                    w.WriteNumber("items", f.Items);
                    w.WriteString("state", f.State);
                    if (f.Transferred is { } t)
                    {
                        w.WriteNumber("transferredItems", t);
                    }
                    w.WriteNumber("loadMs", f.LoadMs);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteNumber("elapsedMs", watch.ElapsedMilliseconds);
        });
        output.Info(request.WhatIf ? "Content transfer what-if complete. Nothing was transferred." : $"Content transfer complete in {watch.Elapsed.TotalSeconds:0.0} s.");
        return ExitCodes.Success;
    }

    private sealed record FileResult(string Name, int Chunks, int Items, string State, int? Transferred, long LoadMs);

    /// <summary>A transfer, with how long the source took to prepare it and the chunks took to copy.</summary>
    private sealed record TransferResult(string Database, Guid TransferId, List<FileResult> Files, long PrepareMs, long CopyMs);

    private static async Task<TransferResult> TransferAsync(Session session, ContentTransferApi sourceApi, ContentTransferApi targetApi, ItemTransferApi items, string database, List<DataTree> trees, TransferOptions options, CancellationToken ct)
    {
        var output = session.Output;
        var transferId = Guid.NewGuid();
        var files = new List<(string Name, ChunkSet Set)>();
        var phase = Stopwatch.StartNew();
        long prepareMs = 0, copyMs = 0;
        output.Info($"[{database}] Creating transfer {transferId} in {sourceApi.Client.Host}...");
        await sourceApi.CreateAsync(transferId, database, trees, ct);
        try
        {
            var status = await PollAsync(() => sourceApi.StatusAsync(transferId, ct), s => s.State is "Completed" or "Failed", $"transfer {transferId} to be ready", ct);
            if (status.State == "Failed")
            {
                throw new CliException($"The source could not prepare transfer {transferId}. Try again; a failed transfer cannot be resumed.");
            }
            prepareMs = phase.ElapsedMilliseconds;
            phase.Restart();
            output.Info($"[{database}] Ready: {status.ChunkSets.Count} chunk sets, {status.ChunkSets.Sum(c => c.ChunkCount)} chunks, {status.ChunkSets.Sum(c => c.TotalItemCount)} items.");

            using var gate = new SemaphoreSlim(Math.Max(1, session.Settings.ParallelRequests));
            foreach (var set in status.ChunkSets)
            {
                var copied = 0;
                await Task.WhenAll(Enumerable.Range(0, set.ChunkCount).Select(async chunkId =>
                {
                    await gate.WaitAsync(ct);
                    try
                    {
                        await ContentCommands.RetryAsync(output, $"chunk {chunkId} of chunk set {set.Id}", async () =>
                        {
                            var chunk = await sourceApi.GetChunkAsync(transferId, set.Id, chunkId, ct);
                            try
                            {
                                await targetApi.SaveChunkAsync(transferId, set.Id, chunkId, chunk, ct);
                            }
                            catch (ManagementException ex) when (ex.Message.Contains("Blob Storage", StringComparison.OrdinalIgnoreCase))
                            {
                                throw new CliException($"{ex.Message}{Environment.NewLine}The target keeps transferred files in Azure Blob Storage, which a local container does not have unless it is set up (https://stockpick.nl/sitecoreai/sitecore-content-transfer-api-locally-with-docker/). Without it, use `content sync` or `content diff --push` to copy into a local CM.");
                            }
                            output.Debug($"[{database}] Chunk {chunkId} of {set.Id}: {chunk.Data.Length} bytes, {(chunk.IsMedia ? "media" : "content")}, {chunk.ItemsProcessed} items, {chunk.ItemsSkipped} skipped.");
                            return 0;
                        }, ct);
                    }
                    finally
                    {
                        gate.Release();
                    }
                    Interlocked.Increment(ref copied);
                }));
                var file = await targetApi.CompleteChunkSetAsync(transferId, set.Id, ct);
                output.Info($"[{database}] Copied chunk set {set.Id} ({copied} chunks, {set.TotalItemCount} items) to {file}.");
                files.Add((file, set));
            }
            copyMs = phase.ElapsedMilliseconds;
        }
        finally
        {
            // Once its chunks are copied, or if anything failed, the source no longer needs it.
            try
            {
                await sourceApi.DeleteAsync(transferId, CancellationToken.None);
            }
            catch (Exception ex) when (ex is ManagementException or HttpRequestException)
            {
                output.Warn($"Could not delete transfer {transferId} in the source: {ex.Message}");
            }
        }

        var results = new List<FileResult>();
        foreach (var (file, set) in files)
        {
            phase.Restart();
            if (options.NoConsume)
            {
                output.Info($"[{database}] Left {file} in {targetApi.Client.Host} without loading it (--no-consume).");
                results.Add(new FileResult(file, set.ChunkCount, set.TotalItemCount, "Uploaded", null, 0));
                continue;
            }
            var blob = await PollAsync(() => items.BlobStateAsync(file, ct), b => b.State is "Uploaded" or "Error" or "Consumed" or "Transferred" or "TransferredWithErrors", $"{file} to be uploaded", ct);
            if (blob.State == "Error")
            {
                throw new CliException($"The target could not store {file}: {blob.Error}");
            }
            output.Info($"[{database}] Loading {file} into {database}...");
            await items.ConsumeAsync(database, file, ct);
            // Once the file is Consumed, its items can be read and edited in the target. Writing them
            // to the database carries on in the background: the load stays Unknown for 40-50 s, then
            // InProgress, then Finished, about 0.2 s per item on SitecoreAI (docs/content.md).
            var consumed = await PollAsync(() => items.BlobStateAsync(file, ct), b => b.State is "Consumed" or "Transferred" or "TransferredWithErrors" or "Error", $"{file} to be consumed", ct);
            if (consumed.State == "Error")
            {
                throw new CliException($"The target could not load {file}: {consumed.Error}");
            }
            if (!options.Wait && consumed.State == "Consumed")
            {
                output.Info($"[{database}] Loaded {file}: its items are available in {targetApi.Client.Host}. Writing them to the database continues there in the background; run with --wait to wait for it.");
                results.Add(new FileResult(file, set.ChunkCount, set.TotalItemCount, consumed.State, null, phase.ElapsedMilliseconds));
                continue;
            }
            var state = await PollAsync(() => items.TransferStateAsync(file, ct), s => s is { State: "Finished" or "Failed" or "Discarded" }, $"{file} to load", ct);
            foreach (var error in state!.ValidationErrors)
            {
                output.Warn($"[{database}] {error}");
            }
            if (state.State != "Finished")
            {
                throw new CliException($"Loading {file} into {database} ended as {state.State}. The file is still in the target; retry it with the Item Transfer API, or run the transfer again.");
            }
            output.Info($"[{database}] Loaded {file}: {state.TransferredItems?.ToString(CultureInfo.InvariantCulture) ?? "?"} of {state.TotalItems?.ToString(CultureInfo.InvariantCulture) ?? "?"} items written (items kept by the merge strategy are not counted).");
            await items.DeleteBlobAsync(file, ct);
            results.Add(new FileResult(file, set.ChunkCount, set.TotalItemCount, state.State, state.TransferredItems, phase.ElapsedMilliseconds));
        }
        return new TransferResult(database, transferId, results, prepareMs, copyMs);
    }

    /// <summary>Checks <paramref name="read"/> until <paramref name="done"/>, or gives up after an hour.</summary>
    internal static async Task<T> PollAsync<T>(Func<Task<T>> read, Func<T, bool> done, string what, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var value = await read();
            if (done(value))
            {
                return value;
            }
            if (watch.Elapsed > PollTimeout)
            {
                throw new CliException($"Gave up waiting for {what} after {PollTimeout.TotalMinutes:0} minutes.");
            }
            await Task.Delay(PollInterval, ct);
        }
    }
}

/// <summary>How <c>content transfer</c> carries items across.</summary>
/// <param name="Wait">Wait until the target has written the items to its database, not only until they are available.</param>
public sealed record TransferOptions(MergeStrategy Merge, bool NoConsume, bool Yes, bool Wait = false);
