using Tern.Cli.Common;
using Tern.Core.Configuration;

namespace Tern.Cli.Commands;

/// <summary>
/// Chooses the Modules a <c>content sync</c> or <c>content diff</c> covers: every Module
/// <c>sitecore.json</c> registers, one Module named with <c>--module</c>, or an item path.
/// </summary>
public static class ContentScope
{
    public const string PathNamespace = "Tern.Content.Path";
    public const string PathIncludeName = "adhoc";

    private static readonly ItemPath MediaLibrary = ItemPath.Parse("/sitecore/media library");

    /// <summary>A media Module is one whose Includes all lie in the media library.</summary>
    public static bool IsMedia(Module module) =>
        module.Includes.Count > 0 && module.Includes.All(i => i.Path.IsDescendantOrSelfOf(MediaLibrary));

    /// <summary>A Module file's name without <c>.module.json</c>.</summary>
    public static string Stem(Module module)
    {
        var name = Path.GetFileName(module.SourcePath);
        return name.EndsWith(".module.json", StringComparison.OrdinalIgnoreCase) ? name[..^".module.json".Length] : Path.GetFileNameWithoutExtension(name);
    }

    /// <summary>Media Modules first, the order a push needs: content links to media.</summary>
    public static List<Module> MediaFirst(IEnumerable<Module> modules)
    {
        var list = modules.ToList();
        return [.. list.Where(IsMedia), .. list.Where(m => !IsMedia(m))];
    }

    /// <summary>Every candidate Module, less the media Modules when <paramref name="skipMedia"/>.</summary>
    public static List<Module> All(IReadOnlyList<Module> registered, bool skipMedia)
    {
        if (registered.Count == 0)
        {
            throw new CliException("sitecore.json registers no modules. Name one with --module, or an item path with --path.");
        }
        var selected = MediaFirst(registered.Where(m => !skipMedia || !IsMedia(m)));
        if (selected.Count == 0)
        {
            throw new CliException("--skip-media left nothing to do: every module in sitecore.json is a media module.");
        }
        return selected;
    }

    /// <summary>
    /// The Modules <paramref name="name"/> means. An exact file name, file name without
    /// <c>.module.json</c> or namespace wins; otherwise any Module whose file name ends with it.
    /// A content Module and its media twin (file names that differ only by <c>content</c> and
    /// <c>media</c>, such as <c>content-site-a</c> and <c>media-site-a</c>) are one set
    /// rather than an ambiguity, and <paramref name="skipMedia"/> then drops the twin.
    /// </summary>
    public static List<Module> Match(IReadOnlyList<Module> candidates, string name, bool skipMedia)
    {
        var exact = candidates.Where(m =>
            Path.GetFileName(m.SourcePath).Equals(name, StringComparison.OrdinalIgnoreCase)
            || Stem(m).Equals(name, StringComparison.OrdinalIgnoreCase)
            || m.Namespace.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        var matched = exact.Count > 0 ? exact : candidates.Where(m => Stem(m).EndsWith(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matched.Count == 0)
        {
            throw new CliException($"No module matches --module '{name}'. Available modules:" + Environment.NewLine + "  "
                + string.Join(Environment.NewLine + "  ", candidates.Select(m => Path.GetFileName(m.SourcePath)).Order(StringComparer.OrdinalIgnoreCase)));
        }
        if (matched.Count > 1 && !IsTwins(matched))
        {
            throw new CliException($"--module '{name}' is ambiguous; it matches:" + Environment.NewLine + "  "
                + string.Join(Environment.NewLine + "  ", matched.Select(m => Path.GetFileName(m.SourcePath))) + Environment.NewLine + "Pass the full file name instead.");
        }
        var selected = MediaFirst(matched.Where(m => !skipMedia || !IsMedia(m)));
        if (selected.Count == 0)
        {
            throw new CliException($"--skip-media left nothing to do: --module '{name}' matched only media modules ({string.Join(", ", matched.Select(m => Path.GetFileName(m.SourcePath)))}).");
        }
        return selected;
    }

    private static bool IsTwins(List<Module> matched)
    {
        if (matched.Count != 2 || IsMedia(matched[0]) == IsMedia(matched[1]))
        {
            return false;
        }
        static string Key(Module m) => Stem(m).Replace("media", "content", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
        return Key(matched[0]) == Key(matched[1]);
    }

    /// <summary>
    /// The Modules <c>sitecore.json</c> registers, plus any other <c>*.module.json</c> in its
    /// folder, so a Module kept out of the default set can still be named with <c>--module</c>.
    /// </summary>
    public static List<Module> Candidates(Session session)
    {
        var candidates = session.AllModules.ToList();
        var known = candidates.Select(m => m.SourcePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(session.Root.RootDirectory, "*.module.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            var full = Path.GetFullPath(file);
            if (known.Contains(full))
            {
                continue;
            }
            try
            {
                candidates.Add(ConfigurationLoader.LoadModule(session.Root, full));
            }
            catch (Exception ex) when (ex is ConfigurationException or System.Text.Json.JsonException)
            {
                session.Output.Warn($"Skipping {Path.GetFileName(full)}: {ex.Message}");
            }
        }
        return candidates;
    }

    /// <summary>A Module for one item path, whose Item files go to <c>items/adhoc</c> beside <c>sitecore.json</c>.</summary>
    public static Module ForPath(Session session, string path, string database, TreeScope scope)
    {
        if (!path.StartsWith('/'))
        {
            throw new CliException($"--path must be an item path such as /sitecore/content/Home, not '{path}'.");
        }
        return ConfigurationLoader.AdHocModule(session.Root, PathNamespace, Path.Combine(session.Root.RootDirectory, "items"), PathIncludeName, ItemPath.Parse(path), database, scope);
    }
}
