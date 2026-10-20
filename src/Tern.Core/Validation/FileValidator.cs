using Tern.Core.Configuration;
using Tern.Core.ItemFiles;
using Tern.Core.Items;
using Tern.Core.Trees;

namespace Tern.Core.Validation;

public enum IssueKind { ParseError, DuplicateId, NonUniquePath, WrongPhysicalPath, NotIncluded, OrphanParent, EmptyFolder }

public sealed record ValidationIssue(IssueKind Kind, Include Include, string File, string Message)
{
    /// <summary>The path where the file belongs, for <see cref="IssueKind.WrongPhysicalPath"/>.</summary>
    public string? ExpectedFile { get; init; }
}

/// <summary>
/// Checks that the Item files of each Include are consistent before anything trusts them, the
/// same checks the Official CLI's <c>ser validate</c> makes.
/// </summary>
public static class FileValidator
{
    /// <summary>
    /// Validates <paramref name="includes"/>. Pass the run's <paramref name="files"/> so the Item
    /// files are parsed once, for validation and the Plan alike.
    /// </summary>
    public static List<ValidationIssue> Validate(IEnumerable<Include> includes, FileItemTree? files = null)
    {
        files ??= new FileItemTree(_ => true);
        var issues = new List<ValidationIssue>();
        foreach (var include in includes)
        {
            Validate(include, files, issues);
        }
        return issues;
    }

    private static void Validate(Include include, FileItemTree files, List<ValidationIssue> issues)
    {
        if (!Directory.Exists(include.PhysicalRoot))
        {
            return;
        }
        var paths = new ItemFilePaths(include);
        var loaded = files.Loaded(include);
        foreach (var (file, error) in loaded.Failures)
        {
            issues.Add(new ValidationIssue(IssueKind.ParseError, include, file, error.Message));
        }
        var parsed = loaded.Files.Select(f => (File: f.PhysicalPath, Item: f)).ToList();

        foreach (var group in parsed.GroupBy(p => p.Item.Id).Where(g => g.Count() > 1))
        {
            foreach (var (file, item) in group.Skip(1))
            {
                issues.Add(new ValidationIssue(IssueKind.DuplicateId, include, file, $"Item {item.Id} ({item.Path}) is serialized more than once: {string.Join(", ", group.Select(g => g.File))}"));
            }
        }

        foreach (var group in parsed.GroupBy(p => ItemPath.Parse(p.Item.Path)).Where(g => g.Select(x => x.Item.Id).Distinct().Count() > 1))
        {
            issues.Add(new ValidationIssue(IssueKind.NonUniquePath, include, group.First().File, $"The item path {group.Key} is not unique; it is used by {string.Join(", ", group.Select(g => g.Item.Id))}. Rename one of these items in Sitecore."));
        }

        var ids = parsed.Select(p => p.Item.Id).ToHashSet();
        foreach (var (file, item) in parsed)
        {
            var path = ItemPath.Parse(item.Path);
            if (!include.IncludesPath(path))
            {
                issues.Add(new ValidationIssue(IssueKind.NotIncluded, include, file, $"{item.Path} is not included by {include.Module.Namespace}/{include.Name}."));
                continue;
            }
            var expected = paths.FileFor(path)!;
            if (!string.Equals(Path.GetFullPath(expected), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new ValidationIssue(IssueKind.WrongPhysicalPath, include, file, $"{item.Path} should be serialized at {expected}.") { ExpectedFile = expected });
            }
            var isRoot = path.Equals(include.Path) || (include.Scope == TreeScope.DescendantsOnly && path.Parent.Equals(include.Path));
            if (!isRoot && !ids.Contains(item.ParentId))
            {
                issues.Add(new ValidationIssue(IssueKind.OrphanParent, include, file, $"The parent {item.ParentId} of {item.Path} is not serialized."));
            }
        }

        foreach (var dir in loaded.EmptyFolders)
        {
            issues.Add(new ValidationIssue(IssueKind.EmptyFolder, include, dir, $"{dir} is an empty folder."));
        }
    }

    /// <summary>
    /// Repairs what can be repaired: moves misplaced files, deletes uncovered, orphaned and
    /// duplicate files (keeping the newest duplicate) and removes empty folders. Non-unique paths
    /// need a rename in Sitecore and are left alone. Returns a line for each change.
    /// </summary>
    public static List<string> Fix(IReadOnlyList<ValidationIssue> issues)
    {
        var changes = new List<string>();
        foreach (var issue in issues.Where(i => i.Kind == IssueKind.DuplicateId))
        {
            if (File.Exists(issue.File))
            {
                File.Delete(issue.File);
                changes.Add("[D] " + issue.File);
            }
        }
        foreach (var issue in issues.Where(i => i.Kind is IssueKind.NotIncluded or IssueKind.OrphanParent))
        {
            if (File.Exists(issue.File))
            {
                File.Delete(issue.File);
                changes.Add("[D] " + issue.File);
            }
        }
        foreach (var issue in issues.Where(i => i.Kind == IssueKind.WrongPhysicalPath && i.ExpectedFile is not null))
        {
            if (!File.Exists(issue.File))
            {
                continue;
            }
            if (File.Exists(issue.ExpectedFile))
            {
                var keep = File.GetLastWriteTimeUtc(issue.File) > File.GetLastWriteTimeUtc(issue.ExpectedFile!) ? issue.File : issue.ExpectedFile!;
                if (keep == issue.ExpectedFile)
                {
                    File.Delete(issue.File);
                    changes.Add("[D] " + issue.File);
                    continue;
                }
                File.Delete(issue.ExpectedFile!);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(issue.ExpectedFile!)!);
            File.Move(issue.File, issue.ExpectedFile!);
            changes.Add($"[M] {issue.File} -> {issue.ExpectedFile}");
        }
        foreach (var issue in issues.Where(i => i.Kind == IssueKind.EmptyFolder).OrderByDescending(i => i.File.Length))
        {
            if (Directory.Exists(issue.File) && !Directory.EnumerateFileSystemEntries(issue.File).Any())
            {
                Directory.Delete(issue.File);
                changes.Add("[D] " + issue.File);
            }
        }
        return changes;
    }
}
