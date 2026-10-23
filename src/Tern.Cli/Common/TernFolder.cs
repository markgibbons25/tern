using System.Text.Json;

using Tern.Core.Configuration;

namespace Tern.Cli.Common;

/// <summary>
/// The repository's committed <c>.tern</c> folder, at its root: Tern's <c>tern.json</c> (settings and shared
/// endpoints), a gitignored <c>.env</c> for credentials, and the Modules that <c>content</c> commands copy
/// between environments, with their Item files under <c>.tern/items</c>. It is found from any folder in the
/// repository, so a content migration runs from anywhere. Its Modules are never part of <c>ser</c> commands,
/// and <c>ser pull</c>, <c>ser push</c> and <c>ser watch</c> refuse to run inside it.
/// </summary>
public static class TernFolder
{
    public const string Name = ".tern";

    /// <summary>
    /// The nearest <c>.tern</c> folder at or above <paramref name="start"/>, looking no further than the
    /// repository root (the first folder with a <c>.git</c>), or null.
    /// </summary>
    public static string? Find(string start)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(start));
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, Name);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return null;
            }
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// The project's <c>tern.json</c>: <c>.tern/tern.json</c>, or else the older <c>tern.json</c> beside
    /// <c>sitecore.json</c>; null when there is neither.
    /// </summary>
    public static string? ConfigFile(string? ternFolder, string? projectDirectory)
    {
        foreach (var file in new[] { ternFolder, projectDirectory }.OfType<string>().Select(d => Path.Combine(d, "tern.json")))
        {
            if (File.Exists(file))
            {
                return file;
            }
        }
        return null;
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="ternFolder"/> or lies inside it.</summary>
    public static bool Contains(string ternFolder, string path)
    {
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ternFolder));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return full.Equals(folder, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(folder + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasModules(string ternFolder) => Directory.EnumerateFiles(ternFolder, "*.module.json").Any();

    /// <summary>
    /// The root the <c>content</c> commands use for the <c>.tern</c> Modules: by default the ones
    /// <c>.tern/tern.json</c>'s <c>modules</c> globs name, or every <c>*.module.json</c> in the folder when it has
    /// none (any of them can still be named with <c>--module</c>), Item files under <c>.tern/items</c>, and the project's serialization settings (excluded
    /// fields, path lengths) when there is a <c>sitecore.json</c>. Roles and users missing from the source are
    /// never removed: a migration copies security, and the project's orphan settings are for its own roles.
    /// </summary>
    public static RootConfiguration Root(string ternFolder, RootConfiguration? project) => new()
    {
        SourcePath = Path.Combine(Path.GetFullPath(ternFolder), "sitecore.json"),
        ModuleGlobs = DefaultModules(ternFolder) ?? ["*.module.json"],
        DefaultModuleRelativeSerializationPath = "items",
        DefaultMaxRelativeItemPathLength = project?.DefaultMaxRelativeItemPathLength ?? 120,
        ExcludedFields = project?.ExcludedFields ?? [],
        ContinueOnItemFailure = project?.ContinueOnItemFailure ?? false,
        ProgressiveMetadataPull = project?.ProgressiveMetadataPull ?? false,
        RemoveOrphansForRoles = false,
        RemoveOrphansForUsers = false,
        TelemetryEnabled = false,
        CacheAuthenticationToken = project?.CacheAuthenticationToken ?? true,
        VersionComparisonEnabled = project?.VersionComparisonEnabled ?? true,
        ApiClientTimeoutInMinutes = project?.ApiClientTimeoutInMinutes ?? 5,
    };

    /// <summary>The <c>modules</c> globs of <c>.tern/tern.json</c>, relative to <c>.tern</c>, or null when it has none.</summary>
    public static IReadOnlyList<string>? DefaultModules(string ternFolder)
    {
        var file = Path.Combine(ternFolder, "tern.json");
        if (!File.Exists(file))
        {
            return null;
        }
        using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (!doc.RootElement.TryGetProperty("modules", out var modules))
        {
            return null;
        }
        if (modules.ValueKind != JsonValueKind.Array || modules.EnumerateArray().Any(m => m.ValueKind != JsonValueKind.String))
        {
            throw new CliException($"{file}: \"modules\" must be a list of module file globs, such as [\"content-*.module.json\"].");
        }
        return [.. modules.EnumerateArray().Select(m => m.GetString()!)];
    }

    /// <summary>Refuses <paramref name="command"/> when it is run from inside the <c>.tern</c> folder.</summary>
    public static void Guard(Session session, string command)
    {
        if (session.TernFolder is not { } tern || !Contains(tern, session.ConfigDirectory))
        {
            return;
        }
        var project = ConfigurationLoader.FindRootFile(session.ConfigDirectory) is { } file ? Path.GetDirectoryName(file) : null;
        var change = project is null
            ? $"To run `tern {command}`, change directory to the repository's serialization project (the folder with its sitecore.json)."
            : $"To run `tern {command}`, change directory to the serialization project:{Environment.NewLine}  cd \"{project}\"";
        throw new CliException(
            $"`tern {command}` is blocked here: {tern} is the repository's .tern folder, whose Modules and Item files are for moving content between environments, not the project's serialized items. "
            + $"To move content, use `tern content sync`, `tern content diff` or `tern content transfer`, from here or anywhere in the repository.{Environment.NewLine}"
            + change);
    }
}
