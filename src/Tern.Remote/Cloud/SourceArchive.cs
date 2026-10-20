using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Tern.Remote.Cloud;

/// <summary>
/// Zips a folder for upload to a deployment, leaving out what git would ignore. The Official CLI
/// asks libgit2 for the status of the folder (and runs <c>git init</c> in it first when it is not
/// a repository); Tern reads the folder's <c>.gitignore</c> files itself and never touches git.
/// </summary>
public static class SourceArchive
{
    public sealed record Result(byte[] Zip, IReadOnlyList<string> Files);

    public static Result Create(string directory)
    {
        var root = Path.GetFullPath(directory);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"The folder to upload, {root}, does not exist.");
        }
        var files = GitIgnore.Files(root);
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var relative in files)
            {
                var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                var entry = zip.CreateEntry(relative);
                var written = File.GetLastWriteTime(full);
                // Zip timestamps cover 1980 to 2107 only.
                entry.LastWriteTime = written.Year is < 1980 or > 2107 ? new DateTime(1980, 1, 1) : written;
                using var source = File.Open(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var target = entry.Open();
                source.CopyTo(target);
            }
        }
        return new Result(stream.ToArray(), files);
    }
}

/// <summary>
/// The parts of git's ignore rules that decide which files are uploaded: every <c>.gitignore</c>
/// at or below the folder, plus <c>.git/info/exclude</c> and the user's global ignore file, with
/// negation, directory-only patterns,
/// anchoring and <c>**</c>. A deeper file's rules win over a shallower one's, the last matching
/// rule wins within a file, and nothing inside an ignored folder can be re-included. When the
/// folder is the root of a repository, files listed in its index are kept even if a rule matches
/// them, because git never ignores a tracked file.
/// </summary>
public sealed class GitIgnore
{
    private readonly List<Rule> _rules = [];

    private GitIgnore(string baseDirectory)
    {
        BaseDirectory = baseDirectory;
    }

    /// <summary>The folder the rules are relative to, as a <c>/</c>-separated path from the root ("" for the root).</summary>
    public string BaseDirectory { get; }

    private static readonly bool IgnoreCase = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    public static GitIgnore Parse(string baseDirectory, string text)
    {
        var ignore = new GitIgnore(baseDirectory);
        foreach (var raw in text.Split('\n'))
        {
            if (Rule.TryParse(raw.TrimEnd('\r')) is { } rule)
            {
                ignore._rules.Add(rule);
            }
        }
        return ignore;
    }

    /// <summary>The user's global ignore file; tests replace it.</summary>
    internal static Func<string?> GlobalExcludesFile { get; set; } = DefaultGlobalExcludesFile;

    /// <summary>
    /// Where git looks for the user's ignore rules: <c>core.excludesFile</c> from the global
    /// configuration, or else <c>$XDG_CONFIG_HOME/git/ignore</c> (<c>~/.config/git/ignore</c>).
    /// </summary>
    private static string? DefaultGlobalExcludesFile()
    {
        var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
        {
            return null;
        }
        var xdg = System.Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(home, ".config");
        string? configured = null;
        foreach (var config in new[] { Path.Combine(xdg, "git", "config"), Path.Combine(home, ".gitconfig") })
        {
            if (File.Exists(config) && CoreExcludesFile(File.ReadAllLines(config)) is { } value)
            {
                configured = value;
            }
        }
        if (configured is not null)
        {
            return configured.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(home, configured[2..]) : configured;
        }
        return Path.Combine(xdg, "git", "ignore");
    }

    /// <summary>The <c>excludesFile</c> setting of the <c>[core]</c> section of a git config file, if any.</summary>
    internal static string? CoreExcludesFile(IEnumerable<string> lines)
    {
        string? section = null;
        string? value = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                section = line.Trim('[', ']').Trim().ToLowerInvariant();
                continue;
            }
            var eq = line.IndexOf('=');
            if (section == "core" && eq > 0 && line[..eq].Trim().Equals("excludesfile", StringComparison.OrdinalIgnoreCase))
            {
                value = line[(eq + 1)..].Trim().Trim('"');
            }
        }
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>Every file under <paramref name="root"/> that git would not ignore, as sorted <c>/</c>-separated relative paths.</summary>
    public static IReadOnlyList<string> Files(string root)
    {
        var results = new List<string>();
        var stack = new List<GitIgnore>();
        if (GlobalExcludesFile() is { } global && File.Exists(global))
        {
            stack.Add(Parse(string.Empty, File.ReadAllText(global)));
        }
        var exclude = Path.Combine(root, ".git", "info", "exclude");
        if (File.Exists(exclude))
        {
            stack.Add(Parse(string.Empty, File.ReadAllText(exclude)));
        }
        var tracked = GitIndex.TrackedFiles(Path.Combine(root, ".git", "index"));
        var trackedFolders = new HashSet<string>(tracked.Comparer);
        foreach (var path in tracked)
        {
            for (var slash = path.LastIndexOf('/'); slash > 0; slash = path.LastIndexOf('/', slash - 1))
            {
                if (!trackedFolders.Add(path[..slash]))
                {
                    break;
                }
            }
        }
        Walk(root, string.Empty, stack, tracked, trackedFolders, ignored: false, results);
        results.Sort(StringComparer.Ordinal);
        return results;
    }

    /// <param name="ignored">True inside an ignored folder that holds tracked files: only those are kept.</param>
    private static void Walk(string directory, string relative, List<GitIgnore> stack, HashSet<string> tracked, HashSet<string> trackedFolders, bool ignored, List<string> results)
    {
        var added = false;
        var file = Path.Combine(directory, ".gitignore");
        if (!ignored && File.Exists(file))
        {
            stack.Add(Parse(relative, File.ReadAllText(file)));
            added = true;
        }
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos().OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            var path = relative.Length == 0 ? entry.Name : relative + "/" + entry.Name;
            var isDirectory = entry is DirectoryInfo;
            if (entry.Name == ".git")
            {
                continue;
            }
            if (isDirectory && entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                // git does not follow symbolic links to folders.
                continue;
            }
            var skip = ignored || IsIgnored(stack, path, isDirectory);
            if (isDirectory)
            {
                if (!skip || trackedFolders.Contains(path))
                {
                    Walk(entry.FullName, path, stack, tracked, trackedFolders, skip, results);
                }
            }
            else if (!skip || tracked.Contains(path))
            {
                results.Add(path);
            }
        }
        if (added)
        {
            stack.RemoveAt(stack.Count - 1);
        }
    }

    /// <summary>Whether the rules in <paramref name="stack"/> (shallowest first) ignore a path, given that its parent is not ignored.</summary>
    public static bool IsIgnored(IReadOnlyList<GitIgnore> stack, string path, bool isDirectory)
    {
        for (var i = stack.Count - 1; i >= 0; i--)
        {
            if (stack[i].Match(path, isDirectory) is { } ignored)
            {
                return ignored;
            }
        }
        return false;
    }

    /// <summary>True if the last matching rule ignores the path, false if it re-includes it, null if no rule matches.</summary>
    public bool? Match(string path, bool isDirectory)
    {
        string local;
        if (BaseDirectory.Length == 0)
        {
            local = path;
        }
        else if (path.StartsWith(BaseDirectory + "/", StringComparison.Ordinal))
        {
            local = path[(BaseDirectory.Length + 1)..];
        }
        else
        {
            return null;
        }
        for (var i = _rules.Count - 1; i >= 0; i--)
        {
            if (_rules[i].IsMatch(local, isDirectory))
            {
                return !_rules[i].Negated;
            }
        }
        return null;
    }

    private sealed class Rule
    {
        private Regex _regex = null!;

        public bool Negated { get; private init; }
        public bool DirectoryOnly { get; private init; }
        public bool Anchored { get; private init; }

        public static Rule? TryParse(string line)
        {
            if (line.Length == 0 || line[0] == '#')
            {
                return null;
            }
            // Trailing spaces are dropped unless escaped with a backslash.
            var end = line.Length;
            while (end > 0 && line[end - 1] == ' ' && !(end > 1 && line[end - 2] == '\\'))
            {
                end--;
            }
            line = line[..end];
            var negated = false;
            if (line.StartsWith('!'))
            {
                negated = true;
                line = line[1..];
            }
            else if (line.StartsWith("\\!", StringComparison.Ordinal) || line.StartsWith("\\#", StringComparison.Ordinal))
            {
                line = line[1..];
            }
            var directoryOnly = false;
            if (line.EndsWith('/') && !line.EndsWith("\\/", StringComparison.Ordinal))
            {
                directoryOnly = true;
                line = line.TrimEnd('/');
            }
            if (line.Length == 0)
            {
                return null;
            }
            // A slash anywhere but the end ties the pattern to the .gitignore's folder.
            var anchored = line.Contains('/');
            if (line.StartsWith('/'))
            {
                line = line[1..];
            }
            var rule = new Rule { Negated = negated, DirectoryOnly = directoryOnly, Anchored = anchored };
            rule._regex = new Regex("^" + ToRegex(line) + "$", RegexOptions.CultureInvariant | (IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None));
            return rule;
        }

        public bool IsMatch(string localPath, bool isDirectory)
        {
            if (DirectoryOnly && !isDirectory)
            {
                return false;
            }
            var subject = Anchored ? localPath : localPath[(localPath.LastIndexOf('/') + 1)..];
            return _regex.IsMatch(subject);
        }

        private static string ToRegex(string pattern)
        {
            var sb = new StringBuilder();
            var i = 0;
            while (i < pattern.Length)
            {
                var c = pattern[i];
                if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*'
                    && (i == 0 || pattern[i - 1] == '/')
                    && (i + 2 == pattern.Length || pattern[i + 2] == '/'))
                {
                    if (i + 2 == pattern.Length)
                    {
                        // "a/**" matches everything inside a; a leading "**" alone matches anything.
                        sb.Append(".*");
                        i += 2;
                    }
                    else
                    {
                        // "**/" matches zero or more folders.
                        sb.Append("(?:.*/)?");
                        i += 3;
                    }
                    continue;
                }
                switch (c)
                {
                    case '*':
                        sb.Append("[^/]*");
                        break;
                    case '?':
                        sb.Append("[^/]");
                        break;
                    case '\\' when i + 1 < pattern.Length:
                        i++;
                        sb.Append(Regex.Escape(pattern[i].ToString()));
                        break;
                    case '[':
                        var close = pattern.IndexOf(']', i + 2 <= pattern.Length ? i + 2 : i + 1);
                        if (close < 0)
                        {
                            sb.Append("\\[");
                            break;
                        }
                        var body = pattern[(i + 1)..close];
                        if (body.StartsWith('!'))
                        {
                            body = "^" + body[1..];
                        }
                        sb.Append('[').Append(body.Replace("\\", "\\\\")).Append(']');
                        i = close;
                        break;
                    default:
                        sb.Append(Regex.Escape(c.ToString()));
                        break;
                }
                i++;
            }
            return sb.ToString();
        }
    }
}

/// <summary>
/// Reads the paths a git repository tracks from its index file (format versions 2 to 4), without
/// running git. Only the paths are read.
/// </summary>
public static class GitIndex
{
    public static HashSet<string> TrackedFiles(string indexFile)
    {
        var comparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var paths = new HashSet<string>(comparer);
        if (!File.Exists(indexFile))
        {
            return paths;
        }
        try
        {
            foreach (var path in Read(File.ReadAllBytes(indexFile)))
            {
                paths.Add(path);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            // An index Tern cannot read only means tracked-but-ignored files are left out.
            paths.Clear();
        }
        return paths;
    }

    public static IEnumerable<string> Read(byte[] data)
    {
        if (data.Length < 12 || data[0] != 'D' || data[1] != 'I' || data[2] != 'R' || data[3] != 'C')
        {
            throw new InvalidDataException("Not a git index.");
        }
        var version = BigEndian(data, 4);
        if (version is < 2 or > 4)
        {
            throw new InvalidDataException($"Unsupported git index version {version}.");
        }
        var count = BigEndian(data, 8);
        var offset = 12;
        var previous = Array.Empty<byte>();
        var results = new List<string>((int)Math.Min(count, 1_000_000));
        for (var i = 0u; i < count; i++)
        {
            var start = offset;
            // ctime, mtime, dev, ino, mode, uid, gid, size (40 bytes) and the object ID (20 bytes).
            offset += 60;
            var flags = (data[offset] << 8) | data[offset + 1];
            offset += 2;
            if (version >= 3 && (flags & 0x4000) != 0)
            {
                offset += 2;
            }
            byte[] path;
            if (version == 4)
            {
                // The path is the previous one, less N trailing bytes, plus a NUL-terminated suffix.
                var strip = 0L;
                var b = data[offset++];
                strip = b & 0x7f;
                while ((b & 0x80) != 0)
                {
                    b = data[offset++];
                    strip = ((strip + 1) << 7) | (long)(b & 0x7f);
                }
                var end = Array.IndexOf(data, (byte)0, offset);
                if (end < 0 || strip > previous.Length)
                {
                    throw new InvalidDataException("Damaged git index.");
                }
                var keep = previous.Length - (int)strip;
                path = new byte[keep + end - offset];
                Array.Copy(previous, path, keep);
                Array.Copy(data, offset, path, keep, end - offset);
                offset = end + 1;
            }
            else
            {
                var end = Array.IndexOf(data, (byte)0, offset);
                if (end < 0)
                {
                    throw new InvalidDataException("Damaged git index.");
                }
                path = data[offset..end];
                // Entries are padded with 1 to 8 NULs to a multiple of eight bytes.
                offset = start + ((end - start + 8) & ~7);
            }
            previous = path;
            results.Add(System.Text.Encoding.UTF8.GetString(path));
        }
        return results;
    }

    private static uint BigEndian(byte[] data, int offset) =>
        (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);
}
