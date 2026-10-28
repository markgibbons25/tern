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
            foreach (var full in new Walker().Walk(rootDirectory, segments, 0, depth: 0, known: false).Select(Path.GetFullPath))
            {
                if (seen.Add(full))
                {
                    results.Add(full);
                }
            }
        }
        return results;
    }

    /// <summary>
    /// One pattern's walk. Each folder is listed once, files and subfolders together, and the top
    /// levels of a <c>**</c> are walked in parallel; results keep the sorted, depth-first order.
    /// </summary>
    private sealed class Walker
    {
        private const int ParallelDepth = 3;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string[] Files, string[] Folders)> _listings = new(StringComparer.OrdinalIgnoreCase);

        private (string[] Files, string[] Folders) List(string dir) => _listings.GetOrAdd(dir, d =>
        {
            var files = new List<string>();
            var folders = new List<string>();
            foreach (var (path, isDirectory) in new System.IO.Enumeration.FileSystemEnumerable<(string, bool)>(d, (ref System.IO.Enumeration.FileSystemEntry e) => (e.ToFullPath(), e.IsDirectory), new EnumerationOptions { AttributesToSkip = 0 }))
            {
                (isDirectory ? folders : files).Add(path);
            }
            return ([.. Sorted(files)], [.. Sorted(folders)]);
        });

        // known: the folder came from its parent's listing, so it exists.
        public List<string> Walk(string dir, string[] segments, int index, int depth, bool known)
        {
            var results = new List<string>();
            if (index >= segments.Length || (!known && !Directory.Exists(dir)))
            {
                return results;
            }
            var segment = segments[index];
            var last = index == segments.Length - 1;
            if (segment == "**")
            {
                results.AddRange(Walk(dir, segments, index + 1, depth, known: true));
                var folders = List(dir).Folders;
                var found = new List<string>[folders.Length];
                if (depth < ParallelDepth && folders.Length > 1)
                {
                    Parallel.For(0, folders.Length, n => found[n] = Walk(folders[n], segments, index, depth + 1, known: true));
                }
                else
                {
                    for (var n = 0; n < folders.Length; n++)
                    {
                        found[n] = Walk(folders[n], segments, index, depth + 1, known: true);
                    }
                }
                foreach (var f in found)
                {
                    results.AddRange(f);
                }
                return results;
            }
            if (segment == ".")
            {
                return Walk(dir, segments, index + 1, depth, known: true);
            }
            if (segment == "..")
            {
                return Walk(Path.GetDirectoryName(Path.GetFullPath(dir)) ?? dir, segments, index + 1, depth, known: false);
            }
            if (last)
            {
                results.AddRange(List(dir).Files.Where(file => Wildcard.IsMatch(Path.GetFileName(file), segment)));
                return results;
            }
            foreach (var sub in List(dir).Folders.Where(sub => Wildcard.IsMatch(Path.GetFileName(sub), segment)))
            {
                results.AddRange(Walk(sub, segments, index + 1, depth + 1, known: true));
            }
            return results;
        }
    }

    private static IEnumerable<string> Sorted(IEnumerable<string> paths) => paths.Order(StringComparer.OrdinalIgnoreCase);
}
