using System.Text.Json;

namespace Tern.Core.Configuration;

/// <summary>
/// Loads <c>sitecore.json</c> and its Modules the way the Official CLI does. Property names and
/// enum values are matched case-insensitively, and comments and trailing commas are allowed.
/// </summary>
public static class ConfigurationLoader
{
    private static readonly JsonDocumentOptions Options = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Finds <c>sitecore.json</c> in <paramref name="startDirectory"/> or the nearest folder above it.</summary>
    public static string? FindRootFile(string startDirectory)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "sitecore.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        return null;
    }

    public static RootConfiguration LoadRoot(string path)
    {
        using var doc = Parse(path);
        var root = doc.RootElement;
        var serialization = Get(root, "serialization");
        var settings = Get(root, "settings");
        return new RootConfiguration
        {
            SourcePath = Path.GetFullPath(path),
            ModuleGlobs = Strings(Get(root, "modules")),
            Plugins = Strings(Get(root, "plugins")),
            DefaultMaxRelativeItemPathLength = Int(serialization, "defaultMaxRelativeItemPathLength") ?? 120,
            DefaultModuleRelativeSerializationPath = Str(serialization, "defaultModuleRelativeSerializationPath") ?? "serialization",
            RemoveOrphansForRoles = Bool(serialization, "removeOrphansForRoles") ?? true,
            RemoveOrphansForUsers = Bool(serialization, "removeOrphansForUsers") ?? true,
            ContinueOnItemFailure = Bool(serialization, "continueOnItemFailure") ?? false,
            ExcludedFields = ExcludedFields(Get(serialization, "excludedFields")),
            ProgressiveMetadataPull = Bool(serialization, "progressiveMetadataPull") ?? false,
            TelemetryEnabled = Bool(settings, "telemetryEnabled") ?? true,
            CacheAuthenticationToken = Bool(settings, "cacheAuthenticationToken") ?? true,
            // The deprecated spelling is still honoured, and the current one wins.
            VersionComparisonEnabled = Bool(settings, "versionComparisonEnabled") ?? Bool(settings, "versionComparingEnabled") ?? true,
            ApiClientTimeoutInMinutes = Int(settings, "apiClientTimeoutInMinutes") ?? 5,
        };
    }

    /// <summary>Loads every Module matched by the root's globs, ordered by their references.</summary>
    public static IReadOnlyList<Module> LoadModules(RootConfiguration root, Action<string>? warn = null)
    {
        var modules = new List<Module>();
        foreach (var file in Glob.Match(root.RootDirectory, root.ModuleGlobs, warn))
        {
            modules.Add(LoadModule(root, file));
        }
        return Order(modules, warn);
    }

    public static Module LoadModule(RootConfiguration root, string path)
    {
        using var doc = Parse(path);
        var json = doc.RootElement;
        var ns = Str(json, "namespace") ?? throw new ConfigurationException($"The module file {path} has no namespace.");
        var items = Get(json, "items");
        var itemsPath = Str(items, "path");
        var module = new Module
        {
            Namespace = ns,
            Description = Str(json, "description"),
            References = Strings(Get(json, "references")),
            Tags = Strings(Get(json, "tags")),
            ItemsPath = itemsPath,
            ExcludedFields = ExcludedFields(Get(items, "excludedFields")),
            Roles = Patterns(Get(json, "roles")),
            Users = Patterns(Get(json, "users")),
            SourcePath = Path.GetFullPath(path),
            Includes = Includes(Get(items, "includes"), root),
        };

        var relative = string.IsNullOrWhiteSpace(itemsPath) ? root.DefaultModuleRelativeSerializationPath : itemsPath;
        string baseDir;
        if (relative.StartsWith('~'))
        {
            baseDir = root.RootDirectory;
            relative = relative.Length > 2 ? relative[2..] : string.Empty;
            relative = relative.Replace("$(module)", ns);
        }
        else
        {
            baseDir = Path.GetDirectoryName(module.SourcePath)!;
        }
        foreach (var include in module.Includes)
        {
            include.Module = module;
            include.PhysicalRoot = Path.GetFullPath(Path.Combine(baseDir, relative, include.Name));
        }
        module.SecurityRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(module.SourcePath)!, root.DefaultModuleRelativeSerializationPath));
        return module;
    }

    /// <summary>
    /// A Module with one Include that exists only for this run, as if a module file in
    /// <paramref name="itemsDirectory"/> had declared it. Its Item files go to
    /// <c>itemsDirectory/name</c>.
    /// </summary>
    public static Module AdHocModule(RootConfiguration root, string ns, string itemsDirectory, string name, ItemPath path, string database, TreeScope scope)
    {
        var include = new Include
        {
            Name = name,
            Path = path,
            Database = database,
            Scope = scope,
            MaxRelativePathLength = root.DefaultMaxRelativeItemPathLength,
        };
        var directory = Path.GetFullPath(itemsDirectory);
        var module = new Module
        {
            Namespace = ns,
            SourcePath = Path.Combine(directory, ns + ".module.json"),
            Includes = [include],
            SecurityRoot = directory,
        };
        include.Module = module;
        include.PhysicalRoot = Path.Combine(directory, name);
        include.Validate();
        return module;
    }

    /// <summary>Applies <c>-i</c>/<c>-e</c> filters: namespace wildcards or <c>tags:[a,b]</c>.</summary>
    public static IReadOnlyList<Module> Select(IReadOnlyList<Module> all, IReadOnlyList<string> include, IReadOnlyList<string> exclude, Action<string>? warn = null)
    {
        var selected = include.Count > 0 ? Resolve(include, all).ToList() : [.. all];
        var excluded = Resolve(exclude, all).ToHashSet();
        selected.RemoveAll(excluded.Contains);
        if ((include.Count > 0 || exclude.Count > 0) && selected.Count == 0)
        {
            throw new ConfigurationException($"The filter \"include: {string.Join(", ", include)}, exclude: {string.Join(", ", exclude)}\" did not match any defined serialization module configurations. Use the info command to see valid module names.");
        }
        return Validate(selected, warn);
    }

    /// <summary>Checks Modules for duplicate namespaces and clashing include folders, then orders them.</summary>
    public static IReadOnlyList<Module> Validate(IReadOnlyList<Module> modules, Action<string>? warn = null)
    {
        var byNamespace = new Dictionary<string, Module>(StringComparer.OrdinalIgnoreCase);
        var byDirectory = new Dictionary<string, List<Module>>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in modules)
        {
            module.Validate();
            if (byNamespace.TryGetValue(module.Namespace, out var existing))
            {
                throw new ConfigurationException($"The '{module.Namespace}' module configuration is defined twice. Found in {module.SourcePath} and {existing.SourcePath}.");
            }
            byNamespace.Add(module.Namespace, module);
            var dir = Path.GetDirectoryName(module.SourcePath)!;
            if (!byDirectory.TryGetValue(dir, out var siblings))
            {
                byDirectory.Add(dir, [module]);
                continue;
            }
            var clash = siblings.FirstOrDefault(m => m.Includes.Any(a => module.Includes.Any(b => a.Name == b.Name)));
            if (clash is not null)
            {
                var name = clash.Includes.First(a => module.Includes.Any(b => a.Name == b.Name)).Name;
                throw new ConfigurationException($"There are several modules ({module.Namespace} and {clash.Namespace}) located along the path \"{dir}\" with the same include name (\"{name}\"). To fix that you should split those modules into folders.");
            }
            siblings.Add(module);
        }
        return Order(modules, warn);
    }

    private static IEnumerable<Module> Resolve(IReadOnlyList<string> expressions, IReadOnlyList<Module> all)
    {
        var result = new List<Module>();
        foreach (var expression in expressions)
        {
            IEnumerable<Module> matches;
            if (expression.StartsWith("tags:[", StringComparison.Ordinal) && expression.EndsWith(']'))
            {
                var tags = expression["tags:[".Length..^1].Split(',');
                matches = all.Where(m => m.Tags.Any(t => tags.Any(e => Wildcard.IsMatch(t, e))));
            }
            else
            {
                matches = all.Where(m => Wildcard.IsMatch(m.Namespace, expression));
            }
            foreach (var match in matches)
            {
                if (!result.Contains(match))
                {
                    result.Add(match);
                }
            }
        }
        return result;
    }

    private static IReadOnlyList<Module> Order(IReadOnlyList<Module> modules, Action<string>? warn)
    {
        var queue = new Queue<(Module Module, List<Module> Dependencies)>();
        foreach (var module in modules)
        {
            var deps = new List<Module>();
            var unmatched = new HashSet<string>(module.References, StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in modules)
            {
                foreach (var reference in module.References)
                {
                    if (Wildcard.IsMatch(candidate.Namespace, reference))
                    {
                        if (!deps.Contains(candidate))
                        {
                            deps.Add(candidate);
                        }
                        unmatched.Remove(reference);
                    }
                }
            }
            if (unmatched.Count > 0)
            {
                warn?.Invoke($"[{module.Namespace}]: References expression(s) {string.Join(", ", unmatched)} did not match any modules part of this operation.");
            }
            queue.Enqueue((module, deps));
        }
        var added = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<Module>();
        var iterations = 0;
        while (queue.Count > 0 && iterations < 20000)
        {
            iterations++;
            var next = queue.Dequeue();
            if (!next.Dependencies.All(d => added.Contains(d.Namespace)))
            {
                queue.Enqueue(next);
                continue;
            }
            ordered.Add(next.Module);
            added.Add(next.Module.Namespace);
        }
        if (queue.Count > 0)
        {
            throw new ConfigurationException("There is a dependency loop in your module dependencies. Unresolved modules: " + string.Join(", ", queue.Select(q => q.Module.Namespace)));
        }
        return ordered;
    }

    private static List<Include> Includes(JsonElement? includes, RootConfiguration root)
    {
        var list = new List<Include>();
        if (includes is not { ValueKind: JsonValueKind.Array } array)
        {
            return list;
        }
        foreach (var json in array.EnumerateArray())
        {
            var rules = new List<IncludeRule>();
            if (Get(json, "rules") is { ValueKind: JsonValueKind.Array } ruleArray)
            {
                foreach (var r in ruleArray.EnumerateArray())
                {
                    rules.Add(new IncludeRule
                    {
                        Path = Str(r, "path") ?? throw new ConfigurationException("The path of the rule is null or empty or consists of only whitespaces."),
                        Scope = Enum<RuleScope>(r, "scope"),
                        PushOperations = Enum<PushOperations>(r, "allowedPushOperations"),
                        Alias = Str(r, "alias"),
                    });
                }
            }
            list.Add(new Include
            {
                Name = Str(json, "name") ?? throw new ConfigurationException("Every include needs a name."),
                Path = ItemPath.Parse(Str(json, "path") ?? throw new ConfigurationException("The path is null or empty or consists of only whitespaces.")),
                Database = Str(json, "database") ?? "master",
                Scope = Enum<TreeScope>(json, "scope") ?? TreeScope.ItemAndDescendants,
                PushOperations = Enum<PushOperations>(json, "allowedPushOperations") ?? PushOperations.CreateUpdateAndDelete,
                MaxRelativePathLength = Int(json, "maxRelativePathLength") ?? root.DefaultMaxRelativeItemPathLength,
                Rules = rules,
            });
        }
        return list;
    }

    private static List<ExcludedField> ExcludedFields(JsonElement? fields)
    {
        var list = new List<ExcludedField>();
        if (fields is { ValueKind: JsonValueKind.Array } array)
        {
            foreach (var f in array.EnumerateArray())
            {
                if (Str(f, "fieldId") is { } id && Guid.TryParse(id, out var guid))
                {
                    list.Add(new ExcludedField(guid, Str(f, "description")));
                }
            }
        }
        return list;
    }

    private static List<SecurityPattern> Patterns(JsonElement? patterns)
    {
        var list = new List<SecurityPattern>();
        if (patterns is { ValueKind: JsonValueKind.Array } array)
        {
            foreach (var p in array.EnumerateArray())
            {
                list.Add(new SecurityPattern(Str(p, "domain") ?? string.Empty, Str(p, "pattern") ?? ".*"));
            }
        }
        return list;
    }

    private static JsonDocument Parse(string path)
    {
        try
        {
            return JsonDocument.Parse(File.ReadAllBytes(path).AsMemory().TrimUtf8Bom(), Options);
        }
        catch (JsonException ex)
        {
            throw new ConfigurationException($"{path} is not valid JSON: {ex.Message}");
        }
    }

    private static ReadOnlyMemory<byte> TrimUtf8Bom(this Memory<byte> bytes) =>
        bytes.Span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? bytes[3..] : bytes;

    internal static JsonElement? Get(JsonElement? element, string name)
    {
        if (element is not { ValueKind: JsonValueKind.Object } obj)
        {
            return null;
        }
        foreach (var property in obj.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.ValueKind == JsonValueKind.Null ? null : property.Value;
            }
        }
        return null;
    }

    internal static string? Str(JsonElement? element, string name) =>
        Get(element, name) is { } v ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText() : null;

    private static int? Int(JsonElement? element, string name) =>
        Get(element, name) is { ValueKind: JsonValueKind.Number } v ? v.GetInt32() : null;

    private static bool? Bool(JsonElement? element, string name) =>
        Get(element, name) switch
        {
            { ValueKind: JsonValueKind.True } => true,
            { ValueKind: JsonValueKind.False } => false,
            { ValueKind: JsonValueKind.String } s when bool.TryParse(s.GetString(), out var b) => b,
            _ => null,
        };

    private static T? Enum<T>(JsonElement? element, string name) where T : struct, System.Enum =>
        Str(element, name) is { } text
            ? System.Enum.TryParse<T>(text, ignoreCase: true, out var value)
                ? value
                : throw new ConfigurationException($"'{text}' is not a valid value for {name}.")
            : null;

    private static List<string> Strings(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.Array } array
            ? array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
            : [];
}

public static class Wildcard
{
    public static bool IsMatch(string input, string pattern)
    {
        if (pattern.IndexOfAny(['*', '?']) < 0)
        {
            return input.Equals(pattern, StringComparison.OrdinalIgnoreCase);
        }
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(input, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }
}
