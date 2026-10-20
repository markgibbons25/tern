using System.CommandLine;
using Tern.Cli.Common;
using Tern.Core.Configuration;
using Tern.Core.ItemFiles;
using Tern.Core.Items;
using Tern.Core.Planning;
using Tern.Core.ResourcePackages;
using Tern.Core.Trees;
using Tern.Remote.Management;

namespace Tern.Cli.Commands;

/// <summary><c>itemres create</c>, <c>unicorn</c> and <c>cleanup</c>: items-as-resources packages.</summary>
public static class ItemResCommands
{
    public static Command Build()
    {
        var itemres = new Command("itemres", "Create and clean up items-as-resources packages");
        itemres.Subcommands.Add(Create());
        itemres.Subcommands.Add(Unicorn());
        itemres.Subcommands.Add(Cleanup());
        return itemres;
    }

    private static Command Create()
    {
        var output = new Option<string>("--output", "-o") { Description = "Package path to output (will have extension added if not provided)", Required = true };
        var overwrite = new Option<bool>("--overwrite") { Description = "Allow overwriting an existing package" };
        var include = CommonOptions.Include();
        var exclude = CommonOptions.Exclude();
        var cmd = new Command("create", "Create a resource package from serialized items") { output, overwrite, include, exclude };
        cmd.SetAction((parse, ct) => CommonOptions.RunAsync(parse, async (session, token) =>
        {
            var modules = session.SelectModules(parse.GetValue(include)!, parse.GetValue(exclude)!);
            var excluded = session.ExcludedFields(modules).ToHashSet();
            var includes = modules.SelectMany(m => m.Includes).ToList();
            var files = new FileItemTree(id => !excluded.Contains(id), includes);
            var byDatabase = new Dictionary<string, List<ItemData>>(StringComparer.OrdinalIgnoreCase);
            foreach (var inc in includes)
            {
                // The package is a sync into an empty target: every item is a create, in the
                // breadth-first order a Push would create them.
                var planner = new Planner(files, new EmptyTree(), applyPushOperations: false, force: true);
                var plan = await planner.PlanIncludeAsync(inc, [], token);
                var data = await files.GetItemsAsync(inc, plan.Operations.Select(o => o.Id).ToList(), token);
                if (!byDatabase.TryGetValue(inc.Database, out var list))
                {
                    list = [];
                    byDatabase.Add(inc.Database, list);
                }
                list.AddRange(plan.Operations.Where(o => o.Action == PlanAction.Create).Select(o => data[o.Id]));
            }
            var written = WritePackages(session.Output, parse.GetValue(output)!, parse.GetValue(overwrite), byDatabase);
            session.Output.Document("itemres create", w =>
            {
                w.WriteStartArray("packages");
                written.ForEach(w.WriteStringValue);
                w.WriteEndArray();
            });
            return ExitCodes.Success;
        }, ct));
        return cmd;
    }

    private static Command Unicorn()
    {
        var output = new Option<string>("--output", "-o") { Description = "Package path to output (will have extension added if not provided)", Required = true };
        var overwrite = new Option<bool>("--overwrite") { Description = "Allow overwriting an existing package" };
        var path = new Option<string>("--path", "-p") { Description = "Path to unicorn serialization folder", Required = true };
        var cmd = new Command("unicorn", "Create a resource package from Unicorn serialization files") { output, overwrite, path };
        cmd.SetAction((parse, ct) => CommonOptions.RunAsync(parse, (session, _) =>
        {
            var folder = parse.GetValue(path)!;
            if (!Directory.Exists(folder))
            {
                throw new CliException($"The Unicorn folder {folder} does not exist.");
            }
            var items = Directory.EnumerateFiles(folder, "*.yml", SearchOption.AllDirectories)
                .Select(f => ItemFileReader.Read(File.ReadAllBytes(f), f))
                .ToList();
            // Parents before children, like a breadth-first walk.
            var byDatabase = items
                .GroupBy(i => i.Database ?? "master", StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.OrderBy(i => ItemPath.Parse(i.Path).Count).ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase);
            var written = WritePackages(session.Output, parse.GetValue(output)!, parse.GetValue(overwrite), byDatabase);
            session.Output.Document("itemres unicorn", w =>
            {
                w.WriteStartArray("packages");
                written.ForEach(w.WriteStringValue);
                w.WriteEndArray();
            });
            return Task.FromResult(ExitCodes.Success);
        }, ct));
        return cmd;
    }

    private static Command Cleanup()
    {
        var environment = CommonOptions.EnvironmentName();
        var force = new Option<bool>("--force", "-f") { Description = "Remove database items that duplicate resource items even when they differ" };
        var whatIf = new Option<bool>("--what-if", "-w") { Description = "Report what would be removed without removing it" };
        var path = new Option<string?>("--path", "-p") { Description = "Only clean up under this item path" };
        var recurse = new Option<bool>("--recurse", "-r") { Description = "Include descendants of --path" };
        var cmd = new Command("cleanup", "Remove database items that are also provided as resource items") { environment, force, whatIf, path, recurse };
        cmd.SetAction((parse, ct) => CommonOptions.RunAsync(parse, async (session, token) =>
        {
            var api = new ResourcePackageApi(session.Management(session.Endpoint(parse.GetValue(environment))));
            var job = await api.CleanAsync(parse.GetValue(force), parse.GetValue(whatIf), parse.GetValue(path), parse.GetValue(recurse), token);
            session.Output.Info($"Cleanup job {job} started.");
            (string State, string? Error, IReadOnlyList<CleanupResult> Results) status;
            do
            {
                await Task.Delay(750, token);
                status = await api.StatusAsync(job, token);
            }
            while (status.State is not ("Finished" or "Failed" or "Canceled"));
            foreach (var r in status.Results)
            {
                session.Output.Line($"[{r.Database}] {r.Count} items: {r.ItemsInfo}");
            }
            session.Output.Document("itemres cleanup", w =>
            {
                w.WriteString("state", status.State);
                w.WriteString("error", status.Error);
                w.WriteStartArray("results");
                foreach (var r in status.Results)
                {
                    w.WriteStartObject();
                    w.WriteString("database", r.Database);
                    w.WriteNumber("count", r.Count);
                    w.WriteString("items", r.ItemsInfo);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            });
            if (status.Error is { Length: > 0 } error)
            {
                throw new CliException(error);
            }
            return ExitCodes.Success;
        }, ct));
        return cmd;
    }

    /// <summary>Writes one <c>items.&lt;db&gt;.&lt;name&gt;.dat</c> per database, next to <paramref name="output"/>.</summary>
    private static List<string> WritePackages(Output output, string target, bool overwrite, Dictionary<string, List<ItemData>> byDatabase)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(target))!;
        var name = Path.GetFileName(target);
        if (name.EndsWith(".dat", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }
        var written = new List<string>();
        foreach (var (database, items) in byDatabase)
        {
            var file = Path.Combine(dir, $"items.{database.ToLowerInvariant()}.{name}.dat");
            if (File.Exists(file) && !overwrite)
            {
                throw new CliException($"{file} already exists. Pass --overwrite to replace it.");
            }
            File.WriteAllBytes(file, ResourcePackageWriter.Write(items, output.Warn));
            output.Info($"Created resource package {Path.GetFileName(file)} of {database.ToUpperInvariant()} database in {dir} directory ({items.Count} items).");
            written.Add(file);
        }
        return written;
    }

    private sealed class EmptyTree : IItemTree
    {
        public string Description => "an empty package";

        public Task<IReadOnlyList<ItemMetadata>> GetMetadataAsync(Include include, CancellationToken ct) => Task.FromResult<IReadOnlyList<ItemMetadata>>([]);

        public Task<IReadOnlyList<ItemMetadata>> GetByIdsAsync(Include include, IReadOnlyCollection<Guid> ids, CancellationToken ct) => Task.FromResult<IReadOnlyList<ItemMetadata>>([]);

        public Task<IReadOnlyList<ItemMetadata>> GetChildrenAsync(Include include, IReadOnlyCollection<Guid> parentIds, CancellationToken ct) => Task.FromResult<IReadOnlyList<ItemMetadata>>([]);

        public Task<IReadOnlyDictionary<Guid, ItemData>> GetItemsAsync(Include include, IReadOnlyCollection<Guid> ids, CancellationToken ct) => Task.FromResult<IReadOnlyDictionary<Guid, ItemData>>(new Dictionary<Guid, ItemData>());
    }
}
