using System.Collections.Concurrent;
using Tern.Core.Configuration;
using Tern.Core.ItemFiles;
using Tern.Core.Items;
using Tern.Core.Signatures;
using Tern.Core.Trees;

namespace Tern.Core.Planning;

public sealed class PlanOutOfDateException(string message) : Exception(message);

public sealed record AppliedOperation(PlanOperation Operation, string? File);

/// <summary>
/// Carries out the file side of a Plan: the last step of a Pull. Moves and renames rewrite the
/// item and every descendant at the new path, then remove the old files, as the Official CLI does.
/// </summary>
public sealed class FileApplier(FileItemTree files, Func<Guid, bool> includeField, string? newLine = null) : IStreamingTreeApplier
{
    private readonly string _newLine = newLine ?? Environment.NewLine;

    /// <summary>Each Include has its own folder, so Includes can be written at the same time.</summary>
    public bool ConcurrentIncludes => true;

    // Folders this applier has created, so writing a file doesn't check its folder again. Lazy, so
    // a thread that finds a folder another thread is still creating waits for it to exist.
    private readonly ConcurrentDictionary<string, Lazy<bool>> _folders = new(StringComparer.OrdinalIgnoreCase);

    public Task CheckPreconditionsAsync(Include include, IReadOnlyList<PlanOperation> operations, CancellationToken ct)
    {
        var signatures = files.Files(include).GroupBy(f => f.Id).ToDictionary(g => g.Key, g => g.First().Signature);
        foreach (var op in operations)
        {
            var current = signatures.GetValueOrDefault(op.Id);
            if (op.Action == PlanAction.Create ? current is not null : current != op.ExpectedSignature)
            {
                throw new PlanOutOfDateException($"{op.Path} changed on disk since the Plan was made. Make a new Plan.");
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>Where an item's file is, by ID, while a Plan is applied. Fields are read from the file when a move needs them.</summary>
    private sealed record Entry(string File, ItemPath Path, string Signature);

    /// <summary>A file this run wrote, with what the file index needs to skip reading it next time.</summary>
    private sealed record WrittenFile(string File, ItemData Header, string Signature);

    public Task<List<AppliedOperation>> ApplyAsync(Include include, IReadOnlyList<PlanOperation> operations, IReadOnlyDictionary<Guid, ItemData> sourceItems, bool checkPreconditions, CancellationToken ct) =>
        Task.FromResult(Apply(include, operations, sourceItems, checkPreconditions));

    /// <summary>
    /// Applies <paramref name="operations"/>, all of which belong to <paramref name="include"/>.
    /// <paramref name="sourceItems"/> holds the source data for every Create and Update. When
    /// <paramref name="checkPreconditions"/> is set, an item whose file changed since the Plan was
    /// made stops the run before anything is written.
    /// </summary>
    public List<AppliedOperation> Apply(Include include, IReadOnlyList<PlanOperation> operations, IReadOnlyDictionary<Guid, ItemData> sourceItems, bool checkPreconditions) =>
        ApplyStreamingAsync(include, operations, (ids, onBatch) =>
        {
            // Data already in memory goes out in chunks at the same time, as a CM's batches would.
            var items = ids.Select(id => sourceItems.GetValueOrDefault(id)).OfType<ItemData>().ToList();
            return Task.WhenAll(items.Chunk(64).Select(chunk => Task.Run(() => onBatch(chunk))));
        }, checkPreconditions, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>
    /// Applies <paramref name="operations"/> with source data that <paramref name="stream"/> hands
    /// over a batch at a time (see <see cref="IItemTree.StreamItemsAsync"/>). Each item is written
    /// as its batch arrives and only its header is kept, so a pull holds no more item data than
    /// the batches in flight.
    /// </summary>
    public async Task<List<AppliedOperation>> ApplyStreamingAsync(Include include, IReadOnlyList<PlanOperation> operations, Func<IReadOnlyCollection<Guid>, Func<IReadOnlyList<ItemData>, Task>, Task> stream, bool checkPreconditions, CancellationToken ct)
    {
        var paths = new ItemFilePaths(include);
        if (checkPreconditions)
        {
            CheckPreconditionsAsync(include, operations, CancellationToken.None).GetAwaiter().GetResult();
        }
        var index = files.Files(include).GroupBy(f => f.Id).ToDictionary(g => g.Key, g => new Entry(g.First().PhysicalPath, ItemPath.Parse(g.First().Path), g.First().Signature));
        var writtenFiles = new Dictionary<string, WrittenFile>(StringComparer.OrdinalIgnoreCase);
        var removedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Wrote((string File, ItemData Item, string Signature) w)
        {
            writtenFiles[w.File] = new WrittenFile(w.File, Header(w.Item), w.Signature);
            removedFiles.Remove(w.File);
        }
        void Removed(string file)
        {
            removedFiles.Add(file);
            writtenFiles.Remove(file);
        }

        var applied = new List<AppliedOperation>();
        for (var at = 0; at < operations.Count; at++)
        {
            var op = operations[at];
            switch (op.Action)
            {
                case PlanAction.Create:
                case PlanAction.Update:
                {
                    // A run of creates and updates touches one file each, so the run is written in
                    // parallel; old files are removed afterwards unless the run rewrote them.
                    var end = at;
                    while (end < operations.Count && operations[end].Action is PlanAction.Create or PlanAction.Update)
                    {
                        end++;
                    }
                    var run = operations.Skip(at).Take(end - at).ToList();
                    var wanted = run.Select(o => o.Id).ToHashSet();
                    // The source's own Signature is what the file will have; the Official CLI's .scindex keeps the same.
                    var signatures = run.Where(o => o.SourceSignature is not null).GroupBy(o => o.Id).ToDictionary(g => g.Key, g => g.First().SourceSignature!);
                    var arrived = new ConcurrentDictionary<Guid, (string File, ItemData Item, string Signature)>();
                    // Batches arrive at the same time, so each one is written in order without more threads.
                    await stream(wanted, batch =>
                    {
                        foreach (var item in batch)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (wanted.Contains(item.Id))
                            {
                                var (file, written, signature) = Write(paths, item, signatures.GetValueOrDefault(item.Id));
                                arrived[item.Id] = (file, Header(written), signature);
                            }
                        }
                        return Task.CompletedTask;
                    });
                    var missing = run.FirstOrDefault(o => !arrived.ContainsKey(o.Id));
                    if (missing is not null)
                    {
                        throw new InvalidOperationException($"No source data was fetched for {missing.Path}.");
                    }
                    var newFiles = new HashSet<string>(arrived.Values.Select(w => w.File), StringComparer.OrdinalIgnoreCase);
                    foreach (var o in run)
                    {
                        var w = arrived[o.Id];
                        if (index.TryGetValue(o.Id, out var old) && !newFiles.Contains(old.File) && File.Exists(old.File))
                        {
                            File.Delete(old.File);
                            Removed(old.File);
                        }
                        index[o.Id] = new Entry(w.File, ItemPath.Parse(w.Item.Path), w.Signature);
                        Wrote(w);
                        applied.Add(new AppliedOperation(o, w.File));
                    }
                    at = end - 1;
                    break;
                }
                case PlanAction.Move:
                case PlanAction.Rename:
                {
                    if (!index.TryGetValue(op.Id, out var entry))
                    {
                        throw new InvalidOperationException($"Existing item path {op.TargetPath} of move/rename to {op.Path} didn't exist!");
                    }
                    var oldPath = entry.Path;
                    var newPath = op.Action == PlanAction.Move
                        ? ItemPath.Parse(op.Path).Parent.Append(oldPath.Name)
                        : oldPath.Parent.Append(ItemPath.Parse(op.Path).Name);
                    var moving = index.Where(kv => kv.Value.Path.IsDescendantOrSelfOf(oldPath)).ToList();
                    var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (id, (file, path, signature)) in moving)
                    {
                        var item = ItemFileReader.Read(File.ReadAllBytes(file), file, includeField);
                        var relative = path.RelativeTo(oldPath)!;
                        var rebased = Rebase(item, newPath.Concat(relative).ToString(), id == op.Id && op.Action == PlanAction.Move ? op.ParentId : item.ParentId);
                        var target = paths.FileFor(ItemPath.Parse(rebased.Path));
                        if (target is null)
                        {
                            continue;
                        }
                        // A move or rename changes no fields, so the Signature stays the same.
                        var w = Write(paths, rebased, signature);
                        Wrote(w);
                        written.Add(target);
                        index[id] = new Entry(target, ItemPath.Parse(rebased.Path), w.Signature);
                    }
                    foreach (var (_, (file, _, _)) in moving)
                    {
                        if (!written.Contains(file) && File.Exists(file))
                        {
                            File.Delete(file);
                            Removed(file);
                        }
                    }
                    applied.Add(new AppliedOperation(op, index[op.Id].File));
                    break;
                }
                case PlanAction.Delete:
                {
                    var path = ItemPath.Parse(op.TargetPath ?? op.Path);
                    var file = paths.FileFor(path);
                    if (file is not null && File.Exists(file))
                    {
                        File.Delete(file);
                        Removed(file);
                    }
                    foreach (var folder in paths.ChildrenFoldersFor(path))
                    {
                        if (Directory.Exists(folder))
                        {
                            Directory.Delete(folder, recursive: true);
                            _folders.Clear();
                        }
                    }
                    foreach (var (id, gone) in index.Where(kv => kv.Value.Path.IsDescendantOrSelfOf(path)).ToList())
                    {
                        index.Remove(id);
                        Removed(gone.File);
                    }
                    applied.Add(new AppliedOperation(op, file));
                    break;
                }
            }
        }
        files.Invalidate(include);
        files.Remember(include, writtenFiles.Values.Select(w => (w.File, w.Header, w.Signature)).ToList(), removedFiles);
        return applied;
    }

    /// <summary>
    /// Writes the item's file. Returns the item as written (excluded fields removed) and its
    /// Signature: <paramref name="signature"/> when the caller knows it, otherwise computed.
    /// </summary>
    private (string File, ItemData Item, string Signature) Write(ItemFilePaths paths, ItemData item, string? signature)
    {
        var file = paths.FileFor(ItemPath.Parse(item.Path))
            ?? throw new InvalidOperationException($"Attempted to write to ignored or otherwise unresolvable item path {item.Path}.");
        var folder = Path.GetDirectoryName(file)!;
        _ = _folders.GetOrAdd(folder, f => new Lazy<bool>(() => Directory.CreateDirectory(f).Exists)).Value;
        var filtered = Filter(item);
        using (var stream = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 64 * 1024))
        {
            ItemFileWriter.WriteTo(stream, filtered, _newLine);
        }
        return (file, filtered, signature ?? SignatureGenerator.Create(filtered));
    }

    private static ItemData Header(ItemData item) => new() { Id = item.Id, ParentId = item.ParentId, TemplateId = item.TemplateId, Path = item.Path };

    private ItemData Filter(ItemData item)
    {
        if (item.AllFields.All(f => includeField(f.Id)))
        {
            return item;
        }
        var copy = new ItemData { Id = item.Id, ParentId = item.ParentId, TemplateId = item.TemplateId, Path = item.Path, BranchId = item.BranchId, Database = item.Database };
        foreach (var f in item.SharedFields.Where(f => includeField(f.Id)))
        {
            copy.SharedFields.Add(f);
        }
        foreach (var language in item.Languages)
        {
            var l = copy.Languages.GetOrAdd(language.Name);
            foreach (var f in language.UnversionedFields.Where(f => includeField(f.Id)))
            {
                l.UnversionedFields.Add(f);
            }
            foreach (var version in language.Versions)
            {
                var v = l.Versions.GetOrAdd(version.Number);
                foreach (var f in version.Fields.Where(f => includeField(f.Id)))
                {
                    v.Fields.Add(f);
                }
            }
        }
        return copy;
    }

    private static ItemData Rebase(ItemData item, string newPath, Guid newParent)
    {
        var copy = new ItemData { Id = item.Id, ParentId = newParent, TemplateId = item.TemplateId, Path = newPath, BranchId = item.BranchId, Database = item.Database };
        foreach (var f in item.SharedFields)
        {
            copy.SharedFields.Add(f);
        }
        foreach (var language in item.Languages)
        {
            copy.Languages.Add(language);
        }
        return copy;
    }
}
