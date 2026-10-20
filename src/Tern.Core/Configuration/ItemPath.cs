namespace Tern.Core.Configuration;

/// <summary>
/// A Sitecore item path split into segments. Segments compare case-insensitively, as Sitecore's do.
/// </summary>
public sealed class ItemPath : IEquatable<ItemPath>
{
    private readonly string[] _segments;
    private readonly string? _original;

    public static readonly ItemPath Root = new([], null);

    private ItemPath(string[] segments, string? original)
    {
        _segments = segments;
        _original = original?.TrimEnd('/');
    }

    public static ItemPath Parse(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/'))
        {
            throw new ArgumentException($"Item path {path} did not start with /.", nameof(path));
        }
        return new ItemPath(path.Split('/', StringSplitOptions.RemoveEmptyEntries), path);
    }

    public static ItemPath FromSegments(IEnumerable<string> segments) => new(segments.ToArray(), null);

    public IReadOnlyList<string> Segments => _segments;

    public int Count => _segments.Length;

    public string Name => _segments.Length == 0 ? string.Empty : _segments[^1];

    public ItemPath Parent => new(_segments[..^1], null);

    public ItemPath Append(string segment) => new([.. _segments, segment], null);

    public ItemPath Concat(ItemPath relative) => new([.. _segments, .. relative._segments], null);

    /// <summary>The path below <paramref name="root"/>, or null when this path is not under it.</summary>
    public ItemPath? RelativeTo(ItemPath root)
    {
        if (root.Count > Count)
        {
            return null;
        }
        for (var i = 0; i < root.Count; i++)
        {
            if (!_segments[i].Equals(root._segments[i], StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }
        return new ItemPath(_segments[root.Count..], null);
    }

    public bool IsDescendantOrSelfOf(ItemPath basePath) => RelativeTo(basePath) is not null;

    public override string ToString() => _original ?? "/" + string.Join('/', _segments);

    public bool Equals(ItemPath? other) =>
        other is not null &&
        other.Count == Count &&
        _segments.Zip(other._segments).All(p => p.First.Equals(p.Second, StringComparison.OrdinalIgnoreCase));

    public override bool Equals(object? obj) => Equals(obj as ItemPath);

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(ToString().TrimEnd('/'));
}
