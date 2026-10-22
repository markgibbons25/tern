using System.Collections.Concurrent;
using System.IO.Enumeration;

using Tern.Core.Configuration;
using Tern.Core.ItemFiles;
using Tern.Core.Items;
using Tern.Core.Signatures;

namespace Tern.Core.Trees;

/// <summary>
/// One Item file: the header and Signature the Plan works from. The fields are not kept in memory;
/// <see cref="ReadItem"/> reads the file again for the few items that are written or copied.
/// </summary>
public sealed class ItemFile(string physicalPath, Guid id, Guid parentId, Guid templateId, string path, string signature, Func<Guid, bool> includeField)
{
    public string PhysicalPath { get; } = physicalPath;
    public Guid Id { get; } = id;
    public Guid ParentId { get; } = parentId;
    public Guid TemplateId { get; } = templateId;
    public string Path { get; } = path;
    public string Signature { get; } = signature;

    public ItemMetadata Metadata => new(Id, ParentId, TemplateId, Path, Signature);

    /// <summary>The item's fields, read from the file now.</summary>
    public ItemData ReadItem() => ItemFileReader.Read(File.ReadAllBytes(PhysicalPath), PhysicalPath, includeField);
}

/// <summary>
/// The Item files of a set of Includes. The tree is built from each file's <c>Path</c> and
/// <c>Parent</c>, not from the folder layout, exactly as the Official CLI builds it. With an
/// <paramref name="index"/>, files that haven't changed since the last run are not opened.
/// </summary>
public sealed class FileItemTree(Func<Guid, bool> includeField, IReadOnlyList<Include>? allIncludes = null, FileIndexCache? index = null) : IItemTree
{
    // Lazy, so Includes planned at the same time never read one folder twice.
    private readonly ConcurrentDictionary<Include, Lazy<LoadResult>> _files = new();

    /// <summary>An Include's files, in folder order, the files that failed to parse, and the empty folders (found in the same listing).</summary>
    public sealed record LoadResult(IReadOnlyList<ItemFile> Files, IReadOnlyList<(string File, ItemFileFormatException Error)> Failures, IReadOnlyList<string> EmptyFolders);

    public string Description => "the Item files";

    public string StoreName(Include include) => $"filesystem (Path: {include.PhysicalRoot}, DB: {include.Database})";

    /// <summary>Every Item file under the Include's folder, whether or not its rules still cover it.</summary>
    public IReadOnlyList<ItemFile> Files(Include include)
    {
        var result = Loaded(include);
        if (result.Failures.Count > 0)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(result.Failures[0].Error);
        }
        return result.Files;
    }

    /// <summary>The Include's files and parse failures, read once and shared with validation.</summary>
    public LoadResult Loaded(Include include) => _files.GetOrAdd(include, i => new Lazy<LoadResult>(() => Load(i))).Value;

    public Task<IReadOnlyList<ItemMetadata>> GetMetadataAsync(Include include, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ItemMetadata>>(Files(include).Select(f => f.Metadata).ToList());

    public Task<IReadOnlyDictionary<Guid, ItemData>> GetItemsAsync(Include include, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var wanted = ids.ToHashSet();
        var files = Files(include).Where(f => wanted.Contains(f.Id)).GroupBy(f => f.Id).Select(g => g.First()).ToList();
        var items = new ItemData[files.Count];
        Parallel.For(0, files.Count, new ParallelOptions { CancellationToken = ct }, n => items[n] = files[n].ReadItem());
        return Task.FromResult<IReadOnlyDictionary<Guid, ItemData>>(items.ToDictionary(i => i.Id));
    }

    // Results come back in the order a scan of the files would find them: Include by Include, file by file.
    public Task<IReadOnlyList<ItemMetadata>> GetByIdsAsync(Include include, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var wanted = ids.ToHashSet();
        var result = new List<ItemMetadata>();
        foreach (var i in SameDatabase(include))
        {
            var index = IndexOf(i);
            var found = new List<int>();
            foreach (var id in wanted)
            {
                if (index.ById.TryGetValue(id, out var n))
                {
                    found.Add(n);
                }
            }
            found.Sort();
            foreach (var n in found)
            {
                wanted.Remove(index.Files[n].Id);
                result.Add(index.Files[n].Metadata);
            }
        }
        return Task.FromResult<IReadOnlyList<ItemMetadata>>(result);
    }

    public Task<IReadOnlyList<ItemMetadata>> GetChildrenAsync(Include include, IReadOnlyCollection<Guid> parentIds, CancellationToken ct)
    {
        var parents = parentIds.ToHashSet();
        var seen = new HashSet<Guid>();
        var result = new List<ItemMetadata>();
        foreach (var i in SameDatabase(include))
        {
            var index = IndexOf(i);
            var found = new List<int>();
            foreach (var parent in parents)
            {
                if (index.ByParent.TryGetValue(parent, out var children))
                {
                    found.AddRange(children);
                }
            }
            found.Sort();
            foreach (var n in found)
            {
                if (seen.Add(index.Files[n].Id))
                {
                    result.Add(index.Files[n].Metadata);
                }
            }
        }
        return Task.FromResult<IReadOnlyList<ItemMetadata>>(result);
    }

    /// <summary>An Include's files by ID (the first file with each ID) and by parent, as positions in <see cref="Files"/>.</summary>
    private sealed class FileIndex
    {
        public required IReadOnlyList<ItemFile> Files { get; init; }
        public Dictionary<Guid, int> ById { get; } = new();
        public Dictionary<Guid, List<int>> ByParent { get; } = new();
    }

    private readonly ConcurrentDictionary<Include, Lazy<FileIndex>> _indexes = new();

    private FileIndex IndexOf(Include include) => _indexes.GetOrAdd(include, i => new Lazy<FileIndex>(() =>
    {
        var index = new FileIndex { Files = Files(i) };
        for (var n = 0; n < index.Files.Count; n++)
        {
            var file = index.Files[n];
            index.ById.TryAdd(file.Id, n);
            if (!index.ByParent.TryGetValue(file.ParentId, out var list))
            {
                index.ByParent[file.ParentId] = list = [];
            }
            list.Add(n);
        }
        return index;
    })).Value;

    /// <summary>The Include itself first, then every other Include in the run that uses the same database.</summary>
    private IEnumerable<Include> SameDatabase(Include include) =>
        new[] { include }.Concat((allIncludes ?? []).Where(i => i != include && i.Database.Equals(include.Database, StringComparison.OrdinalIgnoreCase)));

    /// <summary>How many Item files the Include's folder holds, from the listing alone; null when it has none, so the size is unknown.</summary>
    public static int? CountFiles(Include include)
    {
        if (!Directory.Exists(include.PhysicalRoot))
        {
            return null;
        }
        var count = new FileSystemEnumerable<bool>(include.PhysicalRoot, (ref FileSystemEntry _) => true, new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory && e.FileName.EndsWith(".yml", StringComparison.OrdinalIgnoreCase),
        }.Count();
        return count == 0 ? null : count;
    }

    public ItemFile? FindByPath(Include include, ItemPath path) =>
        Files(include).FirstOrDefault(f => ItemPath.Parse(f.Path).Equals(path));

    /// <summary>Forgets what was read, so the next call sees the files as they are now.</summary>
    public void Invalidate(Include include)
    {
        _files.TryRemove(include, out _);
        _indexes.TryRemove(include, out _);
    }

    /// <summary>
    /// Records files just written (each with its item's header and the Signature of what was
    /// written) and files just removed, so the next run doesn't read them back.
    /// </summary>
    public void Remember(Include include, IReadOnlyList<(string File, ItemData Header, string Signature)> written, IReadOnlyCollection<string> removed)
    {
        if (index is null || (written.Count == 0 && removed.Count == 0))
        {
            return;
        }
        var root = include.PhysicalRoot;
        var entries = index.Load(root);
        var fresh = new (string Relative, FileIndexCache.Entry? Entry)[written.Count];
        Parallel.For(0, written.Count, n =>
        {
            var (file, item, signature) = written[n];
            var info = new FileInfo(file);
            fresh[n] = (Relative(root, file), info.Exists
                ? new FileIndexCache.Entry(info.Length, info.LastWriteTimeUtc.Ticks, item.Id, item.ParentId, item.TemplateId, item.Path, signature)
                : null);
        });
        foreach (var file in removed)
        {
            entries.Remove(Relative(root, file));
        }
        foreach (var (relative, entry) in fresh)
        {
            if (entry is null)
            {
                entries.Remove(relative);
            }
            else
            {
                entries[relative] = entry;
            }
        }
        index.Save(root, entries);
    }

    private static string Relative(string root, string file) => System.IO.Path.GetRelativePath(root, file);

    private LoadResult Load(Include include)
    {
        var root = include.PhysicalRoot;
        if (!Directory.Exists(root))
        {
            return new LoadResult([], [], []);
        }
        // One listing of the folder gives the Item files, with the size and last write time that let
        // an unchanged file go unopened, and the empty folders validation reports.
        var listing = new List<(string Path, long Size, long Modified)>();
        var folders = new List<string>();
        var occupied = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (path, parent, isDirectory, size, modified) in new FileSystemEnumerable<(string, string, bool, long, long)>(root,
            (ref FileSystemEntry e) => (e.ToFullPath(), e.Directory.ToString(), e.IsDirectory, e.Length, e.LastWriteTimeUtc.UtcTicks),
            new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false }))
        {
            occupied.Add(parent);
            if (isDirectory)
            {
                folders.Add(path);
            }
            // Case-insensitive where the file system is, as Directory.EnumerateFiles matches.
            else if (FileSystemName.MatchesSimpleExpression("*.yml", System.IO.Path.GetFileName(path), ignoreCase: !OperatingSystem.IsLinux()))
            {
                listing.Add((path, size, modified));
            }
        }
        var emptyFolders = folders.Where(f => !occupied.Contains(f)).ToList();
        var known = index?.Load(root) ?? new Dictionary<string, FileIndexCache.Entry>();
        var results = new ItemFile?[listing.Count];
        var entries = new FileIndexCache.Entry?[listing.Count];
        var errors = new ItemFileFormatException?[listing.Count];
        var reread = 0;
        Parallel.For(0, listing.Count, i =>
        {
            var (path, size, modified) = listing[i];
            if (known.TryGetValue(Relative(root, path), out var e) && e.Size == size && e.Modified == modified)
            {
                results[i] = new ItemFile(path, e.Id, e.ParentId, e.TemplateId, e.Path, e.Signature, includeField);
                entries[i] = e;
                return;
            }
            Interlocked.Increment(ref reread);
            try
            {
                var item = ItemFileReader.Read(File.ReadAllBytes(path), path, includeField);
                var signature = SignatureGenerator.Create(item);
                results[i] = new ItemFile(path, item.Id, item.ParentId, item.TemplateId, item.Path, signature, includeField);
                entries[i] = new FileIndexCache.Entry(size, modified, item.Id, item.ParentId, item.TemplateId, item.Path, signature);
            }
            catch (ItemFileFormatException ex)
            {
                errors[i] = ex;
            }
        });
        var failures = new List<(string, ItemFileFormatException)>();
        for (var i = 0; i < listing.Count; i++)
        {
            if (errors[i] is { } error)
            {
                failures.Add((listing[i].Path, error));
            }
        }
        if (index is not null && (reread > 0 || known.Count != listing.Count))
        {
            var updated = new Dictionary<string, FileIndexCache.Entry>(StringComparer.Ordinal);
            for (var i = 0; i < listing.Count; i++)
            {
                if (entries[i] is { } e)
                {
                    updated[Relative(root, listing[i].Path)] = e;
                }
            }
            index.Save(root, updated);
        }
        return new LoadResult(results.OfType<ItemFile>().ToList(), failures, emptyFolders);
    }
}
