using System.CommandLine;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Tern.Cli.Common;
using Tern.Core.Configuration;
using Tern.Core.ItemFiles;
using Tern.Core.Trees;

namespace Tern.Cli.Commands;

/// <summary>
/// <c>ser package create</c> and <c>install</c>. A package is a zip in the Official CLI's layout:
/// <c>sitecore.json</c>, <c>&lt;ns&gt;/&lt;ns&gt;.module.json</c>, <c>&lt;ns&gt;/serialization/…</c> and a
/// <c>.pkgindex</c>, so packages move freely between the two tools.
/// </summary>
public static class PackageCommands
{
    public const string Extension = ".itempackage";

    public static Command Build()
    {
        var pkg = new Command("package", "Create and install item packages");
        pkg.Aliases.Add("pkg");
        pkg.Subcommands.Add(Create());
        pkg.Subcommands.Add(Install());
        return pkg;
    }

    private static Command Create()
    {
        var output = new Option<string>("--output", "-o") { Description = "Package path to output (will have extension added if not provided)", Required = true };
        var overwrite = new Option<bool>("--overwrite") { Description = "Allow overwriting an existing package" };
        var include = CommonOptions.Include();
        var exclude = CommonOptions.Exclude();
        var cmd = new Command("create", "Create an item package from serialized items") { output, overwrite, include, exclude };
        cmd.SetAction((parse, ct) => CommonOptions.RunAsync(parse, (session, _) =>
        {
            var path = parse.GetValue(output)!;
            if (!path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
            {
                path += Extension;
            }
            if (File.Exists(path) && !parse.GetValue(overwrite))
            {
                throw new CliException($"{path} already exists. Pass --overwrite to replace it.");
            }
            var modules = session.SelectModules(parse.GetValue(include)!, parse.GetValue(exclude)!);
            var excluded = session.ExcludedFields(modules).ToHashSet();
            var files = new FileItemTree(id => !excluded.Contains(id));
            var count = Write(path, session.Root, modules, files);
            session.Output.Info($"Created {path} with {count} items.");
            session.Output.Document("ser package create", w =>
            {
                w.WriteString("package", Path.GetFullPath(path));
                w.WriteNumber("items", count);
            });
            return Task.FromResult(ExitCodes.Success);
        }, ct));
        return cmd;
    }

    private static Command Install()
    {
        var package = new Option<string>("--package", "-f") { Description = "Package path to install from", Required = true };
        var authority = new Option<string?>("--authority", "--auth") { Description = "Identity authority for the environment" };
        var cm = new Option<string?>("--cm") { Description = "Sitecore content management hostname to connect to" };
        var clientId = new Option<string?>("--client-id") { Description = "The OAuth ClientID to send" };
        var clientSecret = new Option<string?>("--client-secret") { Description = "The OAuth client secret to send" };
        var environment = CommonOptions.EnvironmentName();
        var whatIf = new Option<bool>("--what-if", "-w") { Description = "Lists commands that would be executed, without executing them" };
        var include = CommonOptions.Include();
        var exclude = CommonOptions.Exclude();
        var publish = new Option<bool>("--publish", "-p") { Description = "Publish synced items" };
        var cmd = new Command("install", "Install an item package into a Sitecore instance") { package, authority, cm, clientId, clientSecret, environment, whatIf, include, exclude, publish };
        cmd.SetAction((parse, ct) => CommonOptions.RunAsync(parse, async (session, token) =>
        {
            var file = parse.GetValue(package)!;
            if (!File.Exists(file))
            {
                throw new CliException($"The package {file} does not exist.");
            }
            var temp = Path.Combine(Path.GetTempPath(), "tern-package-" + Guid.NewGuid().ToString("N"));
            try
            {
                Extract(file, temp);
                // Credentials stay with the project's login store; the package only supplies the items.
                var store = session.Store;
                var endpointName = parse.GetValue(environment);
                if (parse.GetValue(cm) is { } host)
                {
                    endpointName ??= "package-install";
                    var e = store.GetOrAdd(endpointName);
                    e.Host = host;
                    e.AllowWrite = true;
                    e.Authority = parse.GetValue(authority) ?? e.Authority ?? Tern.Remote.Auth.Defaults.Authority;
                    if (parse.GetValue(clientId) is { } id && parse.GetValue(clientSecret) is { } secret)
                    {
                        e.ClientId = id;
                        e.ClientSecret = secret;
                        e.UseClientCredentials = true;
                        e.AccessToken = null;
                    }
                }
                var packageSession = new Session(temp, session.Output) { RecordDirectory = session.RecordDirectory };
                packageSession.UseStore(store);
                var request = new SyncRequest(endpointName, parse.GetValue(include)!, parse.GetValue(exclude)!, parse.GetValue(whatIf), SkipValidation: true, Force: false, PlanOut: null, Publish: parse.GetValue(publish), PublishTargets: []);
                return await PushRunner.PushAsync(packageSession, request, token);
            }
            finally
            {
                if (Directory.Exists(temp))
                {
                    Directory.Delete(temp, recursive: true);
                }
            }
        }, ct));
        return cmd;
    }

    /// <summary>Writes the package and returns the number of items in it.</summary>
    internal static int Write(string path, RootConfiguration root, IReadOnlyList<Module> modules, FileItemTree files)
    {
        using var stream = File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        var modulePaths = new List<string>();
        var index = new StringBuilder();
        var count = 0;
        foreach (var module in modules.Where(m => m.Includes.Count > 0 || m.Roles.Count > 0))
        {
            var moduleEntry = $"{module.Namespace}{Path.DirectorySeparatorChar}{module.Namespace}.module.json";
            modulePaths.Add(moduleEntry);
            using (var entry = zip.CreateEntry(moduleEntry).Open())
            {
                WriteModuleJson(entry, module);
            }
            foreach (var include in module.Includes)
            {
                var baseDir = Path.GetDirectoryName(include.PhysicalRoot)!;
                foreach (var file in files.Files(include).Where(f => include.IncludesPath(ItemPath.Parse(f.Item.Path))))
                {
                    var relative = Path.GetRelativePath(baseDir, file.PhysicalPath);
                    var entryPath = Path.Combine(module.Namespace, "serialization", relative);
                    var created = zip.CreateEntry(entryPath, CompressionLevel.Fastest);
                    using (var e = created.Open())
                    {
                        e.Write(ItemFileWriter.ToBytes(file.Item));
                    }
                    var w = new ItemFileWriter.Writer(Environment.NewLine);
                    w.Line("---");
                    w.Map("Entry", entryPath);
                    w.Map("DB", include.Database);
                    w.Map("ID", ItemFileWriter.Format(file.Item.Id));
                    w.Map("Parent", ItemFileWriter.Format(file.Item.ParentId));
                    w.Map("Template", ItemFileWriter.Format(file.Item.TemplateId));
                    w.Map("Path", file.Item.Path);
                    w.Map("Signature", file.Signature);
                    index.Append(w);
                    count++;
                }
            }
        }
        using (var e = zip.CreateEntry(".pkgindex").Open())
        {
            e.Write(Encoding.UTF8.GetPreamble());
            e.Write(Encoding.UTF8.GetBytes(index.ToString()));
        }
        using (var e = zip.CreateEntry("sitecore.json").Open())
        using (var w = new Utf8JsonWriter(e, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteStartArray("modules");
            modulePaths.ForEach(w.WriteStringValue);
            w.WriteEndArray();
            w.WriteStartObject("serialization");
            w.WriteBoolean("removeOrphansForRoles", root.RemoveOrphansForRoles);
            w.WriteBoolean("removeOrphansForUsers", root.RemoveOrphansForUsers);
            w.WriteStartArray("excludedFields");
            foreach (var f in root.ExcludedFields)
            {
                w.WriteStartObject();
                w.WriteString("fieldId", f.FieldId);
                if (f.Description is not null)
                {
                    w.WriteString("description", f.Description);
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return count;
    }

    private static void WriteModuleJson(Stream stream, Module module)
    {
        using var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        w.WriteStartObject();
        w.WriteString("namespace", module.Namespace);
        if (module.Description is not null)
        {
            w.WriteString("description", module.Description);
        }
        w.WriteStartArray("references");
        module.References.ToList().ForEach(w.WriteStringValue);
        w.WriteEndArray();
        w.WriteStartArray("tags");
        module.Tags.ToList().ForEach(w.WriteStringValue);
        w.WriteEndArray();
        w.WriteStartObject("items");
        w.WriteStartArray("includes");
        foreach (var i in module.Includes)
        {
            w.WriteStartObject();
            w.WriteString("name", i.Name);
            w.WriteString("path", i.Path.ToString());
            w.WriteString("database", i.Database);
            w.WriteString("scope", i.Scope.ToString());
            w.WriteString("allowedPushOperations", i.PushOperations.ToString());
            w.WriteNumber("maxRelativePathLength", i.MaxRelativePathLength);
            w.WriteStartArray("rules");
            foreach (var r in i.Rules)
            {
                w.WriteStartObject();
                w.WriteString("path", r.Path);
                if (r.Scope is { } s)
                {
                    w.WriteString("scope", s.ToString());
                }
                if (r.PushOperations is { } o)
                {
                    w.WriteString("allowedPushOperations", o.ToString());
                }
                if (r.Alias is not null)
                {
                    w.WriteString("alias", r.Alias);
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("excludedFields");
        foreach (var f in module.ExcludedFields)
        {
            w.WriteStartObject();
            w.WriteString("fieldId", f.FieldId);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
        WritePatterns(w, "roles", module.Roles);
        WritePatterns(w, "users", module.Users);
        w.WriteEndObject();
    }

    private static void WritePatterns(Utf8JsonWriter w, string name, IReadOnlyList<SecurityPattern> patterns)
    {
        w.WriteStartArray(name);
        foreach (var p in patterns)
        {
            w.WriteStartObject();
            w.WriteString("domain", p.Domain);
            w.WriteString("pattern", p.Pattern);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    /// <summary>Extracts a package, accepting entry names written with either path separator.</summary>
    internal static void Extract(string package, string folder)
    {
        using var zip = ZipFile.OpenRead(package);
        var root = Path.GetFullPath(folder);
        foreach (var entry in zip.Entries)
        {
            var relative = entry.FullName.Replace('\\', '/');
            if (relative.EndsWith('/'))
            {
                continue;
            }
            var target = Path.GetFullPath(Path.Combine(root, relative));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new CliException($"The package entry {entry.FullName} points outside the package.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }
}
