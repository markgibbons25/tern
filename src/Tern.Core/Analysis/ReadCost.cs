using Tern.Core.Configuration;
using Tern.Core.Trees;

namespace Tern.Core.Analysis;

/// <summary>Where some of the items a rule dropped sit: below one item, and how many.</summary>
public sealed record DroppedPlace(string Below, long Items);

/// <summary>The items one rule of an Include dropped, where they sit, and how to stop reading them.</summary>
public sealed record DroppedByRule(IncludeRule? Rule, long Items, IReadOnlyList<DroppedPlace> Places, string? Advice);

/// <summary>
/// How many items the CM listed for an Include (<see cref="Read"/>), and how many of them the
/// Include serializes (<see cref="Kept"/>). The rest were read only to be dropped.
/// </summary>
public sealed record IncludeReadCost(Include Include, long Read, long Kept, IReadOnlyList<DroppedByRule> Dropped)
{
    public long DroppedItems => Read - Kept;

    /// <summary>Whether the Include reads enough items it doesn't keep to slow every pull and push down.</summary>
    public bool OverReads => ReadCost.OverReads(Read, Kept);
}

/// <summary>A rule that makes the CM list items the Include then drops, found from the configuration alone.</summary>
public sealed record RuleCost(Include Include, IncludeRule Rule, string Advice);

/// <summary>
/// Finds Includes that read far more from the CM than they serialize. Management Services lists
/// an Include's whole scope, and its rules are applied afterwards, on the client: an
/// <c>ItemAndDescendants</c> Include with a rule that drops a large branch still reads all of that
/// branch on every pull and push. The fix is in the Includes, not the rules: root Includes at the
/// branches that are serialized, each with the scope it needs.
/// </summary>
public static class ReadCost
{
    /// <summary>The fewest dropped items worth reporting: about half a second of reading on a remote CM.</summary>
    public const long MinDropped = 1_000;

    /// <summary>
    /// Whether an Include drops at least <see cref="MinDropped"/> items and more than it keeps.
    /// </summary>
    public static bool OverReads(long read, long kept) => read - kept >= MinDropped && read - kept > kept;

    /// <summary>How many of the items read for an Include it keeps.</summary>
    public static long CountKept(Include include, IReadOnlyList<ItemMetadata> items) =>
        items.LongCount(m => include.IncludesPath(m.ItemPath));

    /// <summary>
    /// Measures what the CM listed for an Include: how many items it keeps, and for each rule that
    /// dropped items, how many and below which items.
    /// </summary>
    public static IncludeReadCost Measure(Include include, IReadOnlyList<ItemMetadata> items)
    {
        var index = new Dictionary<Guid, int>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            index.TryAdd(items[i].Id, i);
        }
        var paths = items.Select(m => m.ItemPath).ToArray();
        var kept = paths.Select(p => include.IncludesPath(p)).ToArray();
        int Parent(int i) => index.TryGetValue(items[i].ParentId, out var p) && p != i ? p : -1;

        // Each dropped item belongs to its highest dropped ancestor: the item a rule cut off, with
        // everything below it that was read for nothing.
        var top = new int[items.Count];
        Array.Fill(top, -1);
        var chain = new List<int>();
        for (var i = 0; i < items.Count; i++)
        {
            if (kept[i])
            {
                continue;
            }
            chain.Clear();
            var at = i;
            while (top[at] < 0 && Parent(at) is var parent and >= 0 && !kept[parent] && chain.Count < items.Count)
            {
                chain.Add(at);
                at = parent;
            }
            var found = top[at] >= 0 ? top[at] : at;
            top[at] = found;
            chain.ForEach(c => top[c] = found);
        }

        var dropped = Enumerable.Range(0, items.Count).Where(i => !kept[i])
            .GroupBy(i => top[i])
            .Select(g => (Top: g.Key, Items: (long)g.Count(), Rule: include.DecidingRule(paths[g.Key])))
            .GroupBy(t => t.Rule)
            .Select(byRule => new DroppedByRule(
                byRule.Key,
                byRule.Sum(t => t.Items),
                byRule.GroupBy(t => Parent(t.Top) is var p and >= 0 ? items[p].Path : paths[t.Top].Parent.ToString(), StringComparer.OrdinalIgnoreCase)
                    .Select(g => new DroppedPlace(g.Key, g.Sum(t => t.Items)))
                    .OrderByDescending(p => p.Items).ThenBy(p => p.Below, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                byRule.Key is { } rule ? Advice(include, rule) : null))
            .OrderByDescending(d => d.Items)
            .ToList();
        return new IncludeReadCost(include, items.Count, kept.LongCount(k => k), dropped);
    }

    /// <summary>
    /// The rules that make the CM list items for nothing, from the configuration alone: in an
    /// Include that reads every descendant, each rule that drops items below its path. How much
    /// each costs depends on what is below it, which only <see cref="Measure"/> can tell.
    /// </summary>
    public static IReadOnlyList<RuleCost> Inspect(Include include)
    {
        if (include.Scope is not (TreeScope.ItemAndDescendants or TreeScope.DescendantsOnly))
        {
            return [];
        }
        return include.Rules.Select(r => Advice(include, r) is { } advice ? new RuleCost(include, r, advice) : null).OfType<RuleCost>().ToList();
    }

    /// <summary>How to stop reading what <paramref name="rule"/> drops, or null when it drops nothing below its path.</summary>
    public static string? Advice(Include include, IncludeRule rule)
    {
        if (include.Scope == TreeScope.ItemAndChildren)
        {
            // The CM lists only the children, so a rule can drop no more than those.
            return null;
        }
        var split = $"Split include {include.Name} into includes rooted at the branches it keeps";
        if (rule.IsWildcard)
        {
            return rule.Scope == RuleScope.Ignored
                ? $"Rule {rule.Path} drops everything the rules before it don't keep, but the CM still lists everything below {include.Path} first. {split}, one for each rule before it."
                : null;
        }
        var branch = include.Path.Concat(rule.ItemPath);
        return rule.Scope switch
        {
            RuleScope.Ignored => $"Rule {rule.Path} drops {branch} and everything below it, but the CM still lists them first. {split}, and give {branch} none.",
            RuleScope.SingleItem => $"Rule {rule.Path} keeps only {branch}, but the CM still lists everything below it first. {split}, with one for {branch} with scope SingleItem.",
            RuleScope.ItemAndChildren => $"Rule {rule.Path} keeps only {branch} and its children, but the CM still lists everything below them first. {split}, with one for {branch} with scope ItemAndChildren.",
            _ => null,
        };
    }
}
