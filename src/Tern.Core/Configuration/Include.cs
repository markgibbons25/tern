using System.Text;
using System.Text.RegularExpressions;

namespace Tern.Core.Configuration;

public enum TreeScope { SingleItem, ItemAndChildren, ItemAndDescendants, DescendantsOnly }

public enum RuleScope { Ignored, SingleItem, ItemAndChildren, ItemAndDescendants }

public enum PushOperations { CreateUpdateAndDelete, CreateAndUpdate, CreateOnly }

public sealed class ConfigurationException(string message) : Exception(message);

/// <summary>
/// One rule inside an Include. Its path is relative to the Include root, either a plain path
/// such as <c>/Data</c> or a wildcard ending in <c>*</c>.
/// </summary>
public sealed class IncludeRule
{
    public required string Path { get; init; }
    public RuleScope? Scope { get; init; }
    public PushOperations? PushOperations { get; init; }
    public string? Alias { get; init; }

    public bool IsWildcard => Path.IndexOfAny(['*', '?']) >= 0;

    internal ItemPath ItemPath => ItemPath.Parse(Path);

    internal bool AppliesTo(ItemPath relative)
    {
        if (IsWildcard)
        {
            return WildcardRegex(Path).IsMatch(relative.ToString());
        }
        return relative.IsDescendantOrSelfOf(ItemPath);
    }

    private static Regex WildcardRegex(string wildcard)
    {
        var sb = new StringBuilder(Regex.Escape(wildcard));
        sb.Replace("\\*", ".*").Replace("\\?", ".");
        return new Regex("^" + sb + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}

/// <summary>An Include: one subtree of items that a Module serializes.</summary>
public sealed class Include
{
    public required string Name { get; init; }
    public required ItemPath Path { get; init; }
    public string Database { get; init; } = "master";
    public TreeScope Scope { get; init; } = TreeScope.ItemAndDescendants;
    public PushOperations PushOperations { get; init; } = PushOperations.CreateUpdateAndDelete;
    public int MaxRelativePathLength { get; init; } = 120;
    public IReadOnlyList<IncludeRule> Rules { get; init; } = [];

    /// <summary>The Module this Include belongs to.</summary>
    public Module Module { get; internal set; } = null!;

    /// <summary>The folder that holds this Include's Item files.</summary>
    public string PhysicalRoot { get; internal set; } = string.Empty;

    public bool IncludesPath(ItemPath path, Action<string>? explain = null)
    {
        var relative = path.RelativeTo(Path);
        if (relative is null)
        {
            explain?.Invoke("Path was not relative to subtree");
            return false;
        }
        switch (Scope)
        {
            case TreeScope.SingleItem when relative.Count > 0:
                explain?.Invoke("Tree scope is SingleItem, and item is a descendant");
                return false;
            case TreeScope.ItemAndChildren when relative.Count > 1:
                explain?.Invoke("Tree scope is ItemAndChildren, and item is a grandchild or deeper");
                return false;
            case TreeScope.DescendantsOnly when relative.Count == 0:
                explain?.Invoke("Tree scope is DescendantsOnly and item is the root item");
                return false;
        }
        foreach (var rule in RulesThatApplyTo(relative))
        {
            switch (rule.Scope)
            {
                case RuleScope.Ignored:
                    explain?.Invoke($"Rule {rule.Path} with scope Ignored excluded item");
                    return false;
                case RuleScope.ItemAndDescendants:
                    explain?.Invoke($"Rule {rule.Path} with scope ItemAndDescendants included item");
                    return true;
                case RuleScope.SingleItem:
                case RuleScope.ItemAndChildren:
                    var below = relative.RelativeTo(rule.ItemPath)!;
                    if (rule.Scope == RuleScope.SingleItem && below.Count > 0)
                    {
                        explain?.Invoke($"Rule {rule.Path} with scope SingleItem, and item is a descendant");
                        return false;
                    }
                    if (rule.Scope == RuleScope.ItemAndChildren && below.Count > 1)
                    {
                        explain?.Invoke($"Rule {rule.Path} with scope ItemAndChildren, and item is a grandchild or deeper");
                        return false;
                    }
                    return true;
            }
        }
        explain?.Invoke($"Item path matches subtree scope {Scope}.");
        return true;
    }

    public PushOperations PushOperationsFor(ItemPath path)
    {
        var relative = path.RelativeTo(Path);
        if (relative is null)
        {
            return PushOperations.CreateOnly;
        }
        foreach (var rule in RulesThatApplyTo(relative))
        {
            if (rule.PushOperations is { } operations)
            {
                return operations;
            }
        }
        return PushOperations;
    }

    private IEnumerable<IncludeRule> RulesThatApplyTo(ItemPath relative) =>
        relative.Count == 0 ? [] : Rules.Where(r => r.AppliesTo(relative));

    /// <summary>Checks the rules the same way the Official CLI does before any command runs.</summary>
    public void Validate()
    {
        if (Path.Count == 0)
        {
            throw new ConfigurationException("The path is null or empty or consists of only whitespaces.");
        }
        foreach (var rule in Rules)
        {
            if (rule.Scope is null)
            {
                throw new ConfigurationException($"{Path} has rule {rule.Path} that does not have a Scope defined. Rules must define an explicit tree scope they apply to.");
            }
            if (Scope == TreeScope.SingleItem)
            {
                throw new ConfigurationException($"{Path} has scope SingleItem and rules are defined. Rules cannot be applied to a single item as they apply to subtrees. Change the scope to ItemAndChildren or ItemAndDescendants to use rules.");
            }
            if (Scope == TreeScope.ItemAndChildren && rule.Scope is RuleScope.ItemAndDescendants or RuleScope.ItemAndChildren)
            {
                throw new ConfigurationException($"{Path} has scope ItemAndChildren and rule {rule.Path} has scope {rule.Scope}. The rule scope cannot be more inclusive than the root scope. Change the root scope to ItemAndDescendants.");
            }
            if (rule.IsWildcard)
            {
                if (!rule.Path.EndsWith('*'))
                {
                    throw new ConfigurationException($"Subtree {Path} contained path match rule on {rule.Path} that did not end with '*'. Rule wildcards always apply to an entire subtree.");
                }
                if (rule.Scope is RuleScope.SingleItem or RuleScope.ItemAndChildren)
                {
                    throw new ConfigurationException($"Subtree {Path} contained path match rule on {rule.Path} that had scope SingleItem or ItemAndChildren. You cannot use wildcards in this scope, because we cannot create a relative path to a wildcard path. Use ItemAndDescendants or Ignored when using wildcard paths.");
                }
                continue;
            }
            var rulePath = rule.ItemPath;
            if (rulePath.IsDescendantOrSelfOf(Path))
            {
                throw new ConfigurationException($"Subtree path {Path} contained rule for ~{rule.Path}, which is an absolute path. Rules must use relative paths to their subtree root. For example if the root is '/sitecore', '/foo' is a valid rule but '/sitecore/foo' is not.");
            }
            if (rulePath.Count == 0)
            {
                throw new ConfigurationException($"Subtree path {Path} contained rule for /, which is the root subtree item. Rules can only be applied to child paths of the subtree.");
            }
            var ancestor = Path.Concat(rulePath.Parent);
            while (!ancestor.Equals(Path) && ancestor.Count > 0)
            {
                if (!IncludesPath(ancestor))
                {
                    throw new ConfigurationException($"Subtree path {Path} contained rule for ~{rule.Path}, however the ancestor path {ancestor} was not included in the subtree. This rule will not have any effect, because all descendants of {ancestor} are excluded.");
                }
                ancestor = ancestor.Parent;
            }
        }
    }
}
