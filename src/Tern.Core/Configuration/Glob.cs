namespace Tern.Core.Configuration;

/// <summary>
/// File globbing for the <c>modules</c> list: <c>**</c> matches any number of folders, <c>*</c> and
/// <c>?</c> match within one segment, and matching ignores case.
/// </summary>
public static class Glob
{
    public static IReadOnlyList<string> Match(string rootDirectory, IEnumerable<string> patterns, Action<string>? warn = null)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pattern in patterns)
        {
            if (pattern.StartsWith("nuget:", StringComparison.OrdinalIgnoreCase) || pattern.StartsWith("npm:", StringComparison.OrdinalIgnoreCase))
            {
                warn?.Invoke($"Package modules ({pattern}) are not supported yet and were skipped.");
                continue;
            }
            var segments = pattern.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            foreach (var file in Walk(rootDirectory, segments, 0))
            {
                var full = Path.GetFullPath(file);
                if (seen.Add(full))
                {
                    results.Add(full);
                }
            }
        }
        return results;
    }

    private static IEnumerable<string> Walk(string dir, string[] segments, int index)
    {
        if (!Directory.Exists(dir) || index >= segments.Length)
        {
            yield break;
        }
        var segment = segments[index];
        var last = index == segments.Length - 1;
        if (segment == "**")
        {
            foreach (var match in Walk(dir, segments, index + 1))
            {
                yield return match;
            }
            foreach (var sub in Sorted(Directory.EnumerateDirectories(dir)))
            {
                foreach (var match in Walk(sub, segments, index))
                {
                    yield return match;
                }
            }
            yield break;
        }
        if (segment == ".")
        {
            foreach (var match in Walk(dir, segments, index + 1))
            {
                yield return match;
            }
            yield break;
        }
        if (segment == "..")
        {
            foreach (var match in Walk(Path.GetDirectoryName(Path.GetFullPath(dir)) ?? dir, segments, index + 1))
            {
                yield return match;
            }
            yield break;
        }
        if (last)
        {
            foreach (var file in Sorted(Directory.EnumerateFiles(dir)))
            {
                if (Wildcard.IsMatch(Path.GetFileName(file), segment))
                {
                    yield return file;
                }
            }
            yield break;
        }
        foreach (var sub in Sorted(Directory.EnumerateDirectories(dir)))
        {
            if (Wildcard.IsMatch(Path.GetFileName(sub), segment))
            {
                foreach (var match in Walk(sub, segments, index + 1))
                {
                    yield return match;
                }
            }
        }
    }

    private static IEnumerable<string> Sorted(IEnumerable<string> paths) => paths.Order(StringComparer.OrdinalIgnoreCase);
}
