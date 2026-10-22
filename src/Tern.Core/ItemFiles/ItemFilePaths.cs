using System.Security.Cryptography;
using System.Text;

using Tern.Core.Configuration;

namespace Tern.Core.ItemFiles;

/// <summary>
/// Maps item paths to Item file paths inside one Include, exactly as the Official CLI does. An
/// item's file is <c>&lt;include root&gt;/&lt;root name&gt;/&lt;relative path&gt;.yml</c>, and its
/// children live in the folder with the same name minus <c>.yml</c>. Names are escaped one way
/// only, so the item's real name always comes from the file's <c>Path</c>.
/// </summary>
public sealed class ItemFilePaths
{
    private const int HashLength = 16;
    private static readonly HashSet<char> InvalidCharacters =
    [
        '"', '<', '>', '|', ':', '*', '?', '\\', '/', '$',
        .. Enumerable.Range(0, 32).Select(i => (char)i),
    ];
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly Include _include;
    private readonly List<(ItemPath RulePath, ItemPath AliasPath)> _aliases;

    public ItemFilePaths(Include include)
    {
        _include = include;
        _aliases = include.Rules
            .Where(r => !string.IsNullOrWhiteSpace(r.Alias) && !r.IsWildcard)
            .Select(r => (ItemPath.Parse(r.Path), ItemPath.Parse("/" + r.Alias)))
            .ToList();
    }

    public string Root => _include.PhysicalRoot;

    /// <summary>The Item file for <paramref name="itemPath"/>, or null when the Include doesn't cover it.</summary>
    public string? FileFor(ItemPath itemPath) => ChildrenFolderFor(itemPath) is { } folder ? folder + ".yml" : null;

    /// <summary>The folder that holds the item's children, or null when the Include doesn't cover it.</summary>
    public string? ChildrenFolderFor(ItemPath itemPath)
    {
        var relative = PhysicalRelative(itemPath);
        return relative is null ? null : ToPhysical(ApplyPathLengthHashes(relative));
    }

    /// <summary>Every folder where the item's children may live: the normal one and its hashed form.</summary>
    public IReadOnlyList<string> ChildrenFoldersFor(ItemPath itemPath)
    {
        var relative = PhysicalRelative(itemPath);
        if (relative is null)
        {
            return [];
        }
        var normal = ToPhysical(ApplyPathLengthHashes(relative));
        var shortened = ToPhysical(Hash(ItemPath.FromSegments(relative.Segments.Skip(1))));
        return normal.Equals(shortened, StringComparison.OrdinalIgnoreCase) ? [normal] : [normal, shortened];
    }

    private ItemPath? PhysicalRelative(ItemPath itemPath)
    {
        if (!_include.IncludesPath(itemPath))
        {
            return null;
        }
        var relative = itemPath.RelativeTo(_include.Path)!;
        if (TryApplyAlias(relative, out var aliased))
        {
            return aliased;
        }
        var prefix = itemPath.Segments[Math.Max(itemPath.Count - relative.Count - 1, 0)];
        return ItemPath.FromSegments([prefix, .. relative.Segments]);
    }

    private bool TryApplyAlias(ItemPath relative, out ItemPath aliased)
    {
        aliased = null!;
        if (_aliases.Count == 0)
        {
            return false;
        }
        for (ItemPath? candidate = relative; candidate is not null; candidate = candidate.Count < 1 ? null : candidate.Parent)
        {
            foreach (var (rulePath, aliasPath) in _aliases)
            {
                if (rulePath.Equals(candidate))
                {
                    var below = relative.RelativeTo(rulePath)!;
                    var prefix = relative.Segments[Math.Max(relative.Count - below.Count - 1, 0)];
                    aliased = aliasPath.Concat(ItemPath.FromSegments([prefix, .. below.Segments]));
                    return true;
                }
            }
        }
        return false;
    }

    private ItemPath ApplyPathLengthHashes(ItemPath relative)
    {
        var path = ItemPath.FromSegments(relative.Segments.Skip(1));
        var max = _include.MaxRelativePathLength;
        if (PathLength(path) <= max)
        {
            return relative;
        }
        if (max < HashLength)
        {
            throw new ConfigurationException($"The MaxRelativePathLength {max} of subtree {_include.Name} ({_include.Path}) was below the minimum value of {HashLength}");
        }
        ItemPath? ancestor = path;
        var whole = PathLength(path) / max;
        var ratio = (double)PathLength(ancestor) / max;
        while (ancestor is not null && ratio > whole)
        {
            ancestor = ancestor.Count < 1 ? null : ancestor.Parent;
            if (ancestor is not null)
            {
                ratio = (double)PathLength(ancestor) / max;
            }
        }
        if (ancestor is null || ancestor.Count == 0)
        {
            throw new ConfigurationException($"The path {path} (length {PathLength(path)}) could not be stored because its length could not be reduced below the maximum item path length {max}.");
        }
        return Hash(ancestor).Concat(path.RelativeTo(ancestor)!);
    }

    private static int PathLength(ItemPath path) => path.Segments.Sum(s => s.Length) + path.Count;

    private static ItemPath Hash(ItemPath path)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(path.ToString()));
        return ItemPath.FromSegments([Convert.ToHexString(hash)[..HashLength]]);
    }

    private string ToPhysical(ItemPath relative) =>
        Root + Path.DirectorySeparatorChar + string.Join(Path.DirectorySeparatorChar, relative.Segments.Select(EscapeSegment));

    public static string EscapeSegment(string name)
    {
        var trimmed = name.Trim(' ');
        var chars = trimmed.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (InvalidCharacters.Contains(chars[i]))
            {
                chars[i] = '#';
            }
        }
        var text = new string(chars);
        return ReservedNames.Contains(text) ? "#" + text : text;
    }
}
