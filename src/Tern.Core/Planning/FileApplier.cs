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
public sealed class FileApplier(FileItemTree files, Func<Guid, bool> includeField, string? newLine = null) : ITreeApplier
{
    private readonly string _newLine = newLine ?? Environment.NewLine;

    public Task CheckPreconditionsAsync(Include include, IReadOnlyList<PlanOperation> operations, CancellationToken ct)
    {
        var index = files.Files(include).GroupBy(f => f.Item.Id).ToDictionary(g => g.Key, g => g.First().Item);
        foreach (var op in operations)
        {
            var current = index.TryGetValue(op.Id, out var item) ? SignatureGenerator.Create(item) : null;
            if (op.Action == PlanAction.Create ? current is not null : current != op.ExpectedSignature)
            {
                throw new PlanOutOfDateException($"{op.Path} changed on disk since the Plan was made. Make a new Plan.");
            }
        }
        return Task.CompletedTask;
    }

    public Task<List<AppliedOperation>> ApplyAsync(Include include, IReadOnlyList<PlanOperation> operations, IReadOnlyDictionary<Guid, ItemData> sourceItems, bool checkPreconditions, CancellationToken ct) =>
        Task.FromResult(Apply(include, operations, sourceItems, checkPreconditions));

    /// <summary>
    /// Applies <paramref name="operations"/>, all of which belong to <paramref name="include"/>.
    /// <paramref name="sourceItems"/> holds the source data for every Create and Update. When
    /// <paramref name="checkPreconditions"/> is set, an item whose file changed since the Plan was
    /// made stops the run before anything is written.
    /// </summary>
    public List<AppliedOperation> Apply(Include include, IReadOnlyList<PlanOperation> operations, IReadOnlyDictionary<Guid, ItemData> sourceItems, bool checkPreconditions)
    {
        var paths = new ItemFilePaths(include);
        var index = files.Files(include).GroupBy(f => f.Item.Id).ToDictionary(g => g.Key, g => (File: g.First().PhysicalPath, g.First().Item));

        if (checkPreconditions)
        {
            foreach (var op in operations)
            {
                var current = index.TryGetValue(op.Id, out var entry) ? SignatureGenerator.Create(entry.Item) : null;
                if (op.Action == PlanAction.Create ? current is not null : current != op.ExpectedSignature)
                {
                    throw new PlanOutOfDateException($"{op.Path} changed on disk since the Plan was made. Make a new Plan.");
                }
            }
        }

        var applied = new List<AppliedOperation>();
        foreach (var op in operations)
        {
            switch (op.Action)
            {
                case PlanAction.Create:
                case PlanAction.Update:
                {
                    var item = sourceItems.GetValueOrDefault(op.Id) ?? throw new InvalidOperationException($"No source data was fetched for {op.Path}.");
                    var file = Write(paths, item);
                    if (index.TryGetValue(op.Id, out var old) && !old.File.Equals(file, StringComparison.OrdinalIgnoreCase) && File.Exists(old.File))
                    {
                        File.Delete(old.File);
                    }
                    index[op.Id] = (file, item);
                    applied.Add(new AppliedOperation(op, file));
                    break;
                }
                case PlanAction.Move:
                case PlanAction.Rename:
                {
                    if (!index.TryGetValue(op.Id, out var entry))
                    {
                        throw new InvalidOperationException($"Existing item path {op.TargetPath} of move/rename to {op.Path} didn't exist!");
                    }
                    var oldPath = ItemPath.Parse(entry.Item.Path);
                    var newPath = op.Action == PlanAction.Move
                        ? ItemPath.Parse(op.Path).Parent.Append(oldPath.Name)
                        : oldPath.Parent.Append(ItemPath.Parse(op.Path).Name);
                    var moving = index.Where(kv => ItemPath.Parse(kv.Value.Item.Path).IsDescendantOrSelfOf(oldPath)).ToList();
                    var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (id, (file, item)) in moving)
                    {
                        var relative = ItemPath.Parse(item.Path).RelativeTo(oldPath)!;
                        var rebased = Rebase(item, newPath.Concat(relative).ToString(), id == op.Id && op.Action == PlanAction.Move ? op.ParentId : item.ParentId);
                        var target = paths.FileFor(ItemPath.Parse(rebased.Path));
                        if (target is null)
                        {
                            continue;
                        }
                        Write(paths, rebased);
                        written.Add(target);
                        index[id] = (target, rebased);
                    }
                    foreach (var (_, (file, _)) in moving)
                    {
                        if (!written.Contains(file) && File.Exists(file))
                        {
                            File.Delete(file);
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
                    }
                    foreach (var folder in paths.ChildrenFoldersFor(path))
                    {
                        if (Directory.Exists(folder))
                        {
                            Directory.Delete(folder, recursive: true);
                        }
                    }
                    foreach (var id in index.Where(kv => ItemPath.Parse(kv.Value.Item.Path).IsDescendantOrSelfOf(path)).Select(kv => kv.Key).ToList())
                    {
                        index.Remove(id);
                    }
                    applied.Add(new AppliedOperation(op, file));
                    break;
                }
            }
        }
        files.Invalidate(include);
        return applied;
    }

    private string Write(ItemFilePaths paths, ItemData item)
    {
        var file = paths.FileFor(ItemPath.Parse(item.Path))
            ?? throw new InvalidOperationException($"Attempted to write to ignored or otherwise unresolvable item path {item.Path}.");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var filtered = Filter(item);
        File.WriteAllBytes(file, ItemFileWriter.ToBytes(filtered, _newLine));
        return file;
    }

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
