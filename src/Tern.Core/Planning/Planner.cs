using Tern.Core.Configuration;
using Tern.Core.Trees;

namespace Tern.Core.Planning;

public sealed class SourceMissingException(string source, string path)
    : Exception($"The include root {path} does not exist in {source}. Use --force to continue anyway.");

public sealed class NonUniquePathException(string path)
    : Exception($"The item path {path} was not unique (two items with that name were present). These items cannot be serialized; serialized item paths must be unique. Please rename one of these items and try again.");

/// <summary>
/// Compares a source tree with a target tree, one Include at a time, and works out the Plan
/// that makes the target match. The rules follow the Official CLI exactly: a breadth-first walk
/// of the source; Create, Move, Rename and Update decided by ID, parent, name and Signature;
/// orphaned target children deleted. Push operations are honoured only when pushing to a CM.
/// </summary>
/// <summary>The operations for one Include, and how many source items the walk visited.</summary>
public sealed record IncludePlan(Include Include, List<PlanOperation> Operations, long NodesEvaluated);

public sealed class Planner(IItemTree source, IItemTree target, bool applyPushOperations, bool force)
{
    public async Task<IncludePlan> PlanIncludeAsync(Include include, List<string> warnings, CancellationToken ct)
    {
        var sourceItems = (await source.GetMetadataAsync(include, ct)).Where(m => include.IncludesPath(m.ItemPath)).ToList();
        var targetItems = (await target.GetMetadataAsync(include, ct)).Where(m => include.IncludesPath(m.ItemPath)).ToList();

        var sourceChildren = ChildrenIndex(sourceItems);
        var targetById = new Dictionary<Guid, ItemMetadata>();
        foreach (var t in targetItems)
        {
            targetById.TryAdd(t.Id, t);
        }
        var targetChildren = ChildrenIndex(targetItems);

        var operations = new List<PlanOperation>();
        var deletes = new List<PlanOperation>();
        var root = sourceItems.FirstOrDefault(m => m.ItemPath.Equals(include.Path));

        if (include.Scope == TreeScope.SingleItem)
        {
            var targetRoot = targetItems.FirstOrDefault(m => m.ItemPath.Equals(include.Path));
            if (root is null)
            {
                MissingRoot(include, warnings);
                if (targetRoot is not null)
                {
                    deletes.Add(Delete(include, targetRoot));
                }
            }
            else
            {
                operations.AddRange(NodeOperations(include, root, targetById.GetValueOrDefault(root.Id)));
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
        var queue = new Queue<ItemMetadata>(roots);
        var stop = false;
        long nodes = 0;
        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var node = queue.Dequeue();
            nodes++;
            var children = sourceChildren.GetValueOrDefault(node.Id) ?? [];
            EnsureUniqueNames(children);
            var targetNode = targetById.GetValueOrDefault(node.Id);
            operations.AddRange(NodeOperations(include, node, targetNode));
            if (targetNode is not null)
            {
                var sourceChildIds = children.Select(c => c.Id).ToHashSet();
                foreach (var orphan in targetChildren.GetValueOrDefault(targetNode.Id) ?? [])
                {
                    if (!sourceChildIds.Contains(orphan.Id) && CanDelete(include, orphan))
                    {
                        deletes.Add(Delete(include, orphan));
                    }
                }
            }
            if (stop)
            {
                continue;
            }
            foreach (var child in children)
            {
                queue.Enqueue(child);
            }
            if (!recursive)
            {
                stop = true;
            }
        }

        if (include.Scope == TreeScope.DescendantsOnly)
        {
            var rootIds = roots.Select(r => r.Id).ToHashSet();
            foreach (var orphan in targetItems.Where(m => m.ItemPath.Parent.Equals(include.Path) && !rootIds.Contains(m.Id)))
            {
                if (CanDelete(include, orphan) && deletes.All(d => d.Id != orphan.Id))
                {
                    deletes.Add(Delete(include, orphan));
                }
            }
        }

        // An item that moved is not an orphan where it used to be.
        var moved = operations.Where(o => o.Action == PlanAction.Move).Select(o => o.Id).ToHashSet();
        deletes.RemoveAll(d => moved.Contains(d.Id));
        return new IncludePlan(include, Order(operations, deletes), nodes);
    }

    private void MissingRoot(Include include, List<string> warnings)
    {
        if (!force)
        {
            throw new SourceMissingException(source.Description, include.Path.ToString());
        }
        warnings.Add(new SourceMissingException(source.Description, include.Path.ToString()).Message);
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
        var ordered = new List<PlanOperation>();
        var samePath = new List<PlanOperation>();
        foreach (var create in creates.Concat(standardValues))
        {
            var clash = deletes.FirstOrDefault(d => ItemPath.Parse(d.TargetPath ?? d.Path).Equals(ItemPath.Parse(create.Path)));
            if (clash is not null)
            {
                samePath.Insert(0, clash);
                deletes.RemoveAll(d => d.Id == clash.Id);
            }
        }
        ordered.AddRange(samePath);
        ordered.AddRange(creates);
        ordered.AddRange(others);
        ordered.AddRange(standardValues);
        ordered.AddRange(deletes);
        return ordered;
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
}
