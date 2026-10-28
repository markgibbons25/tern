using Tern.Core.Configuration;
using Tern.Core.Trees;

namespace Tern.Core.Transfer;

/// <summary>How the Content Transfer API treats an item that already exists in the destination.</summary>
public enum MergeStrategy { OverrideExistingItem, KeepExistingItem, OverrideExistingTree }

/// <summary>
/// One entry in a Content Transfer API request: an item on its own, or an item and everything
/// below it.
/// </summary>
public sealed record DataTree(ItemPath Path, Guid Id, Guid ParentId, bool Descendants, MergeStrategy Merge, int ItemCount)
{
    public string Scope => Descendants ? "ItemAndDescendants" : "SingleItem";
}

/// <summary>
/// Turns an Include into the fewest Data Trees that carry exactly the items it serializes. The
/// API knows only "this item" and "this item and its subtree", so an Include whose scope or rules
/// leave some descendants out becomes single items down to where whole subtrees can go at once.
/// </summary>
public static class TransferCover
{
    /// <summary>The Data Trees that carry exactly the items <paramref name="include"/> serializes.</summary>
    /// <param name="sourceItems">The Include's subtree on the source, as far as its scope reaches.</param>
    /// <param name="merge">
    /// The strategy for the whole transfer. A <c>CreateOnly</c> push operation keeps what the
    /// destination has, and a single item never uses <see cref="MergeStrategy.OverrideExistingTree"/>,
    /// which would delete descendants that are not part of the transfer.
    /// </param>
    public static List<DataTree> Cover(Include include, IReadOnlyList<ItemMetadata> sourceItems, MergeStrategy merge)
    {
        var inTree = sourceItems.Where(m => m.ItemPath.IsDescendantOrSelfOf(include.Path)).ToList();
        var root = inTree.FirstOrDefault(m => m.ItemPath.Equals(include.Path));
        var trees = new List<DataTree>();
        // A DescendantsOnly fetch may leave the root itself out; its children are then the starts.
        var starts = root is not null ? [root]
            : include.Scope == TreeScope.DescendantsOnly ? inTree.Where(m => m.ItemPath.Parent.Equals(include.Path)).ToList()
            : [];
        var children = new Dictionary<Guid, List<ItemMetadata>>();
        foreach (var item in inTree.Where(m => m != root && !starts.Contains(m)))
        {
            if (!children.TryGetValue(item.ParentId, out var list))
            {
                children[item.ParentId] = list = [];
            }
            list.Add(item);
        }

        // Whole subtrees, found bottom-up: an item goes with all its descendants when it and every
        // one of them is included, with the same push operations.
        var whole = new Dictionary<Guid, int>();
        int? Whole(ItemMetadata node)
        {
            // Every child is worked out, even below an item that is left out, so a whole subtree
            // further down is still found.
            var mine = children.GetValueOrDefault(node.Id) ?? [];
            var operations = include.PushOperationsFor(node.ItemPath);
            var count = 1;
            var allWhole = include.IncludesPath(node.ItemPath);
            if (mine.Count == 0 && node.HasChildren && !ChildrenFetched(include, node))
            {
                // Its children are beyond what the Include's scope fetched, so they are not included.
                allWhole = false;
            }
            foreach (var child in mine)
            {
                if (Whole(child) is { } n && include.PushOperationsFor(child.ItemPath) == operations)
                {
                    count += n;
                }
                else
                {
                    allWhole = false;
                }
            }
            if (!allWhole)
            {
                return null;
            }
            whole[node.Id] = count;
            return count;
        }
        foreach (var start in starts)
        {
            Whole(start);
        }

        void Visit(ItemMetadata node)
        {
            var keep = include.PushOperationsFor(node.ItemPath) == PushOperations.CreateOnly;
            if (whole.TryGetValue(node.Id, out var count))
            {
                trees.Add(new DataTree(node.ItemPath, node.Id, node.ParentId, true, keep ? MergeStrategy.KeepExistingItem : merge, count));
                return;
            }
            if (include.IncludesPath(node.ItemPath))
            {
                var single = keep ? MergeStrategy.KeepExistingItem : merge == MergeStrategy.OverrideExistingTree ? MergeStrategy.OverrideExistingItem : merge;
                trees.Add(new DataTree(node.ItemPath, node.Id, node.ParentId, false, single, 1));
            }
            foreach (var child in children.GetValueOrDefault(node.Id) ?? [])
            {
                Visit(child);
            }
        }
        foreach (var start in starts)
        {
            Visit(start);
        }
        return trees;
    }

    /// <summary>Whether fetching the Include by its scope brought this item's children with it.</summary>
    private static bool ChildrenFetched(Include include, ItemMetadata node)
    {
        var depth = node.ItemPath.RelativeTo(include.Path)?.Count ?? 0;
        return include.Scope switch
        {
            TreeScope.SingleItem => false,
            TreeScope.ItemAndChildren => depth == 0,
            _ => true,
        };
    }
}
