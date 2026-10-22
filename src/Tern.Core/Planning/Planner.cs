using Tern.Core.Configuration;
using Tern.Core.Trees;

namespace Tern.Core.Planning;

public sealed class SourceMissingException(string store, string path)
    : Exception($"Configured source item path {path} did not exist in {store} data store. An empty source indicates that you need to fill that source with data before attempting to push it into a destination. Usually that means you need to pull an initial data set from Sitecore to fill serialized files before being able to push serialized data into Sitecore.");

public sealed class NonUniquePathException(string path)
    : Exception($"The item path {path} was not unique (two items with that name were present). These items cannot be serialized; serialized item paths must be unique. Please rename one of these items and try again.");

/// <summary>The operations for one Include, and how many source items the walk visited.</summary>
public sealed record IncludePlan(Include Include, List<PlanOperation> Operations, long NodesEvaluated);

/// <summary>
/// Compares a source tree with a target tree, one Include at a time, and works out the Plan that
/// makes the target match. The rules follow the Official CLI exactly:
/// <list type="bullet">
/// <item>The source is walked breadth-first from the Include root.</item>
/// <item>Each source item's target is found by ID anywhere in the database, so a renamed or moved
/// item is recognised. Create, Move, Rename and Update follow from ID, parent, name and Signature.</item>
/// <item>A target item's children that the source doesn't have are deleted. When the target item
/// sits outside the Include's path, all of its unmatched children go, regardless of rules.</item>
/// <item>Push operations are honoured only when pushing to a CM.</item>
/// </list>
/// Each level of the walk is resolved with one batch of lookups, so a deep tree costs a handful
/// of round trips rather than one per item.
/// </summary>
public sealed class Planner(IItemTree source, IItemTree target, bool applyPushOperations, bool force)
{
    public async Task<IncludePlan> PlanIncludeAsync(Include include, List<string> warnings, CancellationToken ct)
    {
        var sourceItems = (await source.GetMetadataAsync(include, ct)).Where(m => include.IncludesPath(m.ItemPath)).ToList();
        var index = new TargetIndex(include, await target.GetMetadataAsync(include, ct));
        var sourceChildren = ChildrenIndex(sourceItems);

        var operations = new List<PlanOperation>();
        var deletes = new List<PlanOperation>();
        var root = sourceItems.FirstOrDefault(m => m.ItemPath.Equals(include.Path));

        if (include.Scope == TreeScope.SingleItem)
        {
            if (root is null)
            {
                MissingRoot(include, warnings);
                var targetRoot = index.ByPath(include.Path);
                if (targetRoot is not null)
                {
                    deletes.Add(Delete(include, targetRoot));
                }
            }
            else
            {
                await ResolveAsync(include, index, [root], ct);
                operations.AddRange(NodeOperations(include, root, index.ById(root.Id)));
            }
            return new IncludePlan(include, Order(operations, deletes), 1);
        }

        List<ItemMetadata> roots;
        if (include.Scope == TreeScope.DescendantsOnly)
        {
            roots = sourceItems.Where(m => m.ItemPath.Parent.Equals(include.Path)).ToList();
        }
        else
        {
            if (root is null)
            {
                MissingRoot(include, warnings);
            }
            roots = root is null ? [] : [root];
        }

        var recursive = include.Scope is TreeScope.ItemAndDescendants or TreeScope.DescendantsOnly;
        long nodes = 0;
        var level = roots;
        var depth = 0;
        while (level.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            await ResolveAsync(include, index, level, ct);
            var next = new List<ItemMetadata>();
            foreach (var node in level)
            {
                nodes++;
                var children = sourceChildren.GetValueOrDefault(node.Id) ?? [];
                EnsureUniqueNames(children);
                var targetNode = index.ById(node.Id);
                operations.AddRange(NodeOperations(include, node, targetNode));
                if (targetNode is not null)
                {
                    deletes.AddRange(OrphanDeletes(include, children, targetNode, index.Children(targetNode.Id)));
                }
                if (recursive || depth == 0)
                {
                    next.AddRange(children);
                }
            }
            level = next;
            depth++;
        }

        if (include.Scope == TreeScope.DescendantsOnly)
        {
            var rootIds = roots.Select(r => r.Id).ToHashSet();
            foreach (var orphan in index.ChildrenOfPath(include.Path).Where(m => include.IncludesPath(m.ItemPath) && !rootIds.Contains(m.Id)))
            {
                if (CanDelete(include, orphan))
                {
                    deletes.Add(Delete(include, orphan));
                }
            }
        }

        // An item that moved is not an orphan where it used to be.
        var moved = operations.Where(o => o.Action == PlanAction.Move).Select(o => o.Id).ToHashSet();
        deletes.RemoveAll(d => moved.Contains(d.Id));
        return new IncludePlan(include, Order(operations, Distinct(deletes)), nodes);
    }

    /// <summary>
    /// Plans only the items <paramref name="ids"/> names, as <c>ser watch</c> does after a poll of the
    /// Change history, instead of walking the whole Include. Each item is found by ID on both sides
    /// and gets the operations a full plan would give it; one gone from the source, or moved out of
    /// the Include, is deleted. The items in <paramref name="subtrees"/> (moved or renamed) also have
    /// their descendants walked, so those follow them. The rest of the Include is taken to be in step.
    /// </summary>
    public async Task<IncludePlan> PlanItemsAsync(Include include, IReadOnlyCollection<Guid> ids, IReadOnlySet<Guid> subtrees, CancellationToken ct)
    {
        var sources = (await source.GetByIdsAsync(include, ids, ct))
            .Where(m => include.IncludesPath(m.ItemPath))
            .GroupBy(m => m.Id).Select(g => g.First())
            .OrderBy(m => m.ItemPath.Count)
            .ToList();
        var index = new TargetIndex(include, [], covered: false);
        index.AddFound(ids, await target.GetByIdsAsync(include, ids, ct));

        var operations = new List<PlanOperation>();
        var deletes = new List<PlanOperation>();
        var found = sources.Select(s => s.Id).ToHashSet();
        foreach (var id in ids.Where(id => !found.Contains(id)))
        {
            if (index.ById(id) is { } gone && include.IncludesPath(gone.ItemPath) && CanDelete(include, gone))
            {
                deletes.Add(Delete(include, gone));
            }
        }

        // A changed item inside a moved or renamed subtree is planned by that subtree's walk, so its
        // parent is always created first.
        var roots = sources.Where(s => subtrees.Contains(s.Id)).ToList();
        bool InsideWalk(ItemMetadata m) => roots.Any(r => r.Id != m.Id && m.ItemPath.IsDescendantOrSelfOf(r.ItemPath));
        long nodes = 0;
        foreach (var node in sources.Where(s => !InsideWalk(s)))
        {
            nodes++;
            operations.AddRange(NodeOperations(include, node, index.ById(node.Id)));
        }

        var level = roots;
        var depth = 0;
        while (level.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            await ResolveAsync(include, index, level, ct);
            var children = (await source.GetChildrenAsync(include, level.Select(n => n.Id).ToList(), ct))
                .Where(c => include.IncludesPath(c.ItemPath)).ToList();
            var byParent = ChildrenIndex(children);
            foreach (var node in level)
            {
                var nodeChildren = byParent.GetValueOrDefault(node.Id) ?? [];
                EnsureUniqueNames(nodeChildren);
                var targetNode = index.ById(node.Id);
                if (depth > 0)
                {
                    nodes++;
                    operations.AddRange(NodeOperations(include, node, targetNode));
                }
                if (targetNode is not null)
                {
                    deletes.AddRange(OrphanDeletes(include, nodeChildren, targetNode, index.Children(targetNode.Id)));
                }
            }
            level = children;
            depth++;
        }

        var moved = operations.Where(o => o.Action == PlanAction.Move).Select(o => o.Id).ToHashSet();
        deletes.RemoveAll(d => moved.Contains(d.Id));
        return new IncludePlan(include, Order(operations, Distinct(deletes)), nodes);
    }

    /// <summary>
    /// Finds the target of every node on this level, by ID, and the children of any target that
    /// lies outside what the first fetch covered.
    /// </summary>
    private async Task ResolveAsync(Include include, TargetIndex index, IReadOnlyList<ItemMetadata> level, CancellationToken ct)
    {
        var missing = level.Select(n => n.Id).Where(id => !index.Knows(id)).Distinct().ToList();
        if (missing.Count > 0)
        {
            index.AddFound(missing, await target.GetByIdsAsync(include, missing, ct));
        }
        var unknownChildren = level
            .Select(n => index.ById(n.Id))
            .Where(t => t is not null && !index.ChildrenKnown(t))
            .Select(t => t!.Id)
            .Distinct()
            .ToList();
        if (unknownChildren.Count > 0)
        {
            index.AddChildren(unknownChildren, await target.GetChildrenAsync(include, unknownChildren, ct));
        }
    }

    private IEnumerable<PlanOperation> OrphanDeletes(Include include, List<ItemMetadata> sourceChildren, ItemMetadata targetNode, IReadOnlyList<ItemMetadata> targetChildren)
    {
        var sourceIds = sourceChildren.Select(c => c.Id).ToHashSet();
        var orphans = targetChildren.Where(c => include.IncludesPath(c.ItemPath) && !sourceIds.Contains(c.Id)).ToList();
        var extra = new List<ItemMetadata>();
        if (!IsUnderInclude(include, targetNode.Path))
        {
            // The target item lives outside the Include, so rules can't speak for its children:
            // the Official CLI removes every child the source doesn't have.
            extra.AddRange(targetChildren.Where(c => !sourceIds.Contains(c.Id) && orphans.All(o => o.Id != c.Id)));
        }
        orphans.RemoveAll(o => !CanDelete(include, o));
        return orphans.Concat(extra).Select(o => Delete(include, o));
    }

    /// <summary>The Official CLI's own test: a lower-case string prefix, not a segment match.</summary>
    internal static bool IsUnderInclude(Include include, string path) =>
        path.ToLowerInvariant().StartsWith(include.Path.ToString().ToLowerInvariant(), StringComparison.Ordinal);

    private void MissingRoot(Include include, List<string> warnings)
    {
        var error = new SourceMissingException(source.StoreName(include), include.Path.ToString());
        if (!force)
        {
            throw error;
        }
        warnings.Add($"[{include.Database}] {error.Message}");
    }

    private IEnumerable<PlanOperation> NodeOperations(Include include, ItemMetadata node, ItemMetadata? targetNode)
    {
        var operations = applyPushOperations ? include.PushOperationsFor(node.ItemPath) : PushOperations.CreateUpdateAndDelete;
        if (targetNode is null)
        {
            yield return Op(PlanAction.Create, include, node, null);
            yield break;
        }
        if (operations == PushOperations.CreateOnly)
        {
            yield break;
        }
        if (node.ParentId != targetNode.ParentId)
        {
            yield return Op(PlanAction.Move, include, node, targetNode);
        }
        if (!node.Name.Equals(targetNode.Name, StringComparison.Ordinal))
        {
            yield return Op(PlanAction.Rename, include, node, targetNode);
        }
        if (string.IsNullOrWhiteSpace(node.Signature) || string.IsNullOrWhiteSpace(targetNode.Signature) || !node.Signature.Equals(targetNode.Signature, StringComparison.Ordinal))
        {
            yield return Op(PlanAction.Update, include, node, targetNode);
        }
    }

    private bool CanDelete(Include include, ItemMetadata orphan) =>
        !applyPushOperations || include.PushOperationsFor(orphan.ItemPath) == PushOperations.CreateUpdateAndDelete;

    private static PlanOperation Op(PlanAction action, Include include, ItemMetadata node, ItemMetadata? targetNode) => new()
    {
        Action = action,
        Module = include.Module?.Namespace ?? string.Empty,
        Include = include.Name,
        Database = include.Database,
        Id = node.Id,
        Path = node.Path,
        TargetPath = targetNode?.Path,
        ParentId = node.ParentId,
        SourceSignature = node.Signature,
        ExpectedSignature = targetNode?.Signature,
    };

    private static PlanOperation Delete(Include include, ItemMetadata orphan) => new()
    {
        Action = PlanAction.Delete,
        Module = include.Module?.Namespace ?? string.Empty,
        Include = include.Name,
        Database = include.Database,
        Id = orphan.Id,
        Path = orphan.Path,
        TargetPath = orphan.Path,
        ParentId = orphan.ParentId,
        ExpectedSignature = orphan.Signature,
    };

    private static List<PlanOperation> Distinct(List<PlanOperation> deletes)
    {
        var seen = new HashSet<Guid>();
        return deletes.Where(d => seen.Add(d.Id)).ToList();
    }

    /// <summary>
    /// Creates first, then everything else, then <c>__Standard Values</c> creates, then deletes.
    /// A delete at the same path as a create runs first, so the create has somewhere to go.
    /// </summary>
    private static List<PlanOperation> Order(List<PlanOperation> operations, List<PlanOperation> deletes)
    {
        static bool IsStandardValues(PlanOperation o) => o.Path.EndsWith("/__Standard Values", StringComparison.OrdinalIgnoreCase);
        var creates = operations.Where(o => o.Action == PlanAction.Create && !IsStandardValues(o)).ToList();
        var others = operations.Where(o => o.Action != PlanAction.Create).ToList();
        var standardValues = operations.Where(o => o.Action == PlanAction.Create && IsStandardValues(o)).ToList();
        var samePath = new List<PlanOperation>();
        if (deletes.Count > 0)
        {
            // Deletes by path, in order, so each create finds its clash without a scan.
            var byPath = new Dictionary<ItemPath, List<PlanOperation>>();
            foreach (var d in deletes)
            {
                var key = ItemPath.Parse(d.TargetPath ?? d.Path);
                if (!byPath.TryGetValue(key, out var list))
                {
                    byPath[key] = list = [];
                }
                list.Add(d);
            }
            var removed = new HashSet<Guid>();
            foreach (var create in creates.Concat(standardValues))
            {
                if (byPath.TryGetValue(ItemPath.Parse(create.Path), out var list) && list.FirstOrDefault(d => !removed.Contains(d.Id)) is { } clash)
                {
                    samePath.Insert(0, clash);
                    removed.Add(clash.Id);
                }
            }
            deletes.RemoveAll(d => removed.Contains(d.Id));
        }
        return [.. samePath, .. creates, .. others, .. standardValues, .. deletes];
    }

    private static Dictionary<Guid, List<ItemMetadata>> ChildrenIndex(IEnumerable<ItemMetadata> items)
    {
        var index = new Dictionary<Guid, List<ItemMetadata>>();
        foreach (var item in items)
        {
            if (!index.TryGetValue(item.ParentId, out var list))
            {
                list = [];
                index.Add(item.ParentId, list);
            }
            list.Add(item);
        }
        return index;
    }

    private static void EnsureUniqueNames(List<ItemMetadata> siblings)
    {
        var seen = new HashSet<ItemPath>();
        foreach (var s in siblings)
        {
            if (!seen.Add(s.ItemPath))
            {
                throw new NonUniquePathException(s.Path);
            }
        }
    }

    /// <summary>
    /// What is known about the target: the Include's subtree from the first fetch, plus anything
    /// looked up by ID or by parent along the way.
    /// </summary>
    private sealed class TargetIndex
    {
        private readonly Include _include;
        private readonly Dictionary<Guid, ItemMetadata?> _byId = new();
        private readonly Dictionary<Guid, List<ItemMetadata>> _children = new();
        private readonly HashSet<Guid> _childrenFetched = new();
        private readonly HashSet<(Guid Parent, Guid Child)> _childIds = new();
        private readonly bool _covered;

        /// <param name="covered">Whether <paramref name="prefetched"/> is the Include's whole subtree,
        /// so every item under the Include's path already has its children listed.</param>
        public TargetIndex(Include include, IReadOnlyList<ItemMetadata> prefetched, bool covered = true)
        {
            _include = include;
            _covered = covered;
            foreach (var item in prefetched)
            {
                if (_byId.TryAdd(item.Id, item))
                {
                    AddChild(item);
                }
            }
        }

        public bool Knows(Guid id) => _byId.ContainsKey(id);

        public ItemMetadata? ById(Guid id) => _byId.GetValueOrDefault(id);

        public ItemMetadata? ByPath(ItemPath path) => _byId.Values.FirstOrDefault(m => m is not null && m.ItemPath.Equals(path));

        /// <summary>
        /// Children are known for anything inside the Include's path, which the first fetch covered,
        /// or that has been fetched since.
        /// </summary>
        public bool ChildrenKnown(ItemMetadata node) => _childrenFetched.Contains(node.Id) || (_covered && IsUnderInclude(_include, node.Path));

        public List<ItemMetadata> Children(Guid parentId) => _children.GetValueOrDefault(parentId) ?? [];

        public IEnumerable<ItemMetadata> ChildrenOfPath(ItemPath path) =>
            _byId.Values.Where(m => m is not null && m.ItemPath.Parent.Equals(path))!;

        public void AddFound(IEnumerable<Guid> asked, IReadOnlyList<ItemMetadata> found)
        {
            foreach (var item in found)
            {
                if (!_byId.TryGetValue(item.Id, out var existing) || existing is null)
                {
                    _byId[item.Id] = item;
                    AddChild(item);
                }
            }
            foreach (var id in asked)
            {
                _byId.TryAdd(id, null);
            }
        }

        public void AddChildren(IEnumerable<Guid> parents, IReadOnlyList<ItemMetadata> children)
        {
            foreach (var parent in parents)
            {
                _childrenFetched.Add(parent);
            }
            foreach (var child in children)
            {
                if (!_byId.TryGetValue(child.Id, out var existing) || existing is null)
                {
                    _byId[child.Id] = child;
                }
                if (_childIds.Add((child.ParentId, child.Id)))
                {
                    AddChild(child);
                }
            }
        }

        private void AddChild(ItemMetadata item)
        {
            _childIds.Add((item.ParentId, item.Id));
            if (!_children.TryGetValue(item.ParentId, out var list))
            {
                list = [];
                _children.Add(item.ParentId, list);
            }
            list.Add(item);
        }
    }
}
