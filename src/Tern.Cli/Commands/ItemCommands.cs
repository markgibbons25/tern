using System.CommandLine;
using System.Text;
using System.Text.Json;
using Tern.Cli.Common;
using Tern.Core.Configuration;
using Tern.Core.ItemFiles;
using Tern.Core.Items;
using Tern.Core.Signatures;
using Tern.Remote.Management;

namespace Tern.Cli.Commands;

/// <summary>
/// <c>item get</c> and <c>item query</c>: read items straight from a CM, using the same
/// <c>serialize</c> query as Pull, so they need nothing beyond Management Services.
/// </summary>
public static class ItemCommands
{
    public static Command Build()
    {
        var item = new Command("item", "Read items directly from a CM");
        item.Subcommands.Add(Get());
        item.Subcommands.Add(Query());
        item.Subcommands.Add(ScriptCommands.ItemSet());
        return item;
    }

    private static Command Get()
    {
        var target = new Argument<string>("path-or-id") { Description = "An item path such as /sitecore/content/Home, or an item ID" };
        var environment = CommonOptions.EnvironmentName();
        var database = new Option<string>("--database", "-d") { Description = "Database to read from", DefaultValueFactory = _ => "master" };
        var format = new Option<string>("--format") { Description = "yaml (an Item file) or json", DefaultValueFactory = _ => "yaml" };
        var cmd = new Command("get", "Show one item with all of its fields") { target, environment, database, format };
        cmd.SetAction((parse, ct) => CommonOptions.RunAsync(parse, async (session, token) =>
        {
            var api = session.Serialization(session.Endpoint(parse.GetValue(environment)));
            var request = new SerializeRequest(parse.GetValue(target)!, parse.GetValue(database)!, TreeScope.SingleItem, []);
            var items = await GetSingleAsync(api, request, token);
            if (items is null)
            {
                throw new CliException($"{request.PathOrId} was not found in the {request.Database} database.");
            }
            if (session.Output.Json || parse.GetValue(format)!.Equals("json", StringComparison.OrdinalIgnoreCase))
            {
                using var stdout = Console.OpenStandardOutput();
                using var w = new Utf8JsonWriter(stdout, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                w.WriteStartObject();
                w.WriteNumber("schemaVersion", 1);
                w.WriteString("command", "item get");
                w.WriteString("signature", SignatureGenerator.Create(items));
                w.WritePropertyName("item");
                ItemDataJson.Write(w, items);
                w.WriteEndObject();
                w.Flush();
                stdout.Write("\n"u8);
            }
            else
            {
                Console.Out.Write(ItemFileWriter.ToText(items, Environment.NewLine));
            }
            return ExitCodes.Success;
        }, ct));
        return cmd;
    }

    private static async Task<ItemData?> GetSingleAsync(SerializationApi api, SerializeRequest request, CancellationToken ct)
    {
        var metadata = await api.GetMetadataAsync([request], ct);
        var found = metadata[0].FirstOrDefault();
        if (found is null)
        {
            return null;
        }
        var data = await api.GetItemDataAsync(request.Database, [found.Id], request.ExcludedFieldIds, ct);
        return data.GetValueOrDefault(found.Id);
    }

    private static Command Query()
    {
        var root = new Argument<string>("path") { Description = "The item to start from" };
        var environment = CommonOptions.EnvironmentName();
        var database = new Option<string>("--database", "-d") { Description = "Database to read from", DefaultValueFactory = _ => "master" };
        var scope = new Option<string>("--scope") { Description = "item, children or descendants", DefaultValueFactory = _ => "descendants" };
        var template = new Option<string[]>("--template") { Description = "Only items based on these template IDs", AllowMultipleArgumentsPerToken = true, DefaultValueFactory = _ => [] };
        var name = new Option<string?>("--name") { Description = "Only items whose name matches this wildcard" };
        var field = new Option<string[]>("--field") { Description = "Only items where a field has a value: <field name or ID>=<wildcard>", AllowMultipleArgumentsPerToken = true, DefaultValueFactory = _ => [] };
        var show = new Option<string[]>("--show") { Description = "Field names or IDs to print for each item", AllowMultipleArgumentsPerToken = true, DefaultValueFactory = _ => [] };
        var limit = new Option<int>("--limit") { Description = "Stop after this many matches (0 = no limit)", DefaultValueFactory = _ => 0 };
        var cmd = new Command("query", "Find items under a path") { root, environment, database, scope, template, name, field, show, limit };
        cmd.SetAction((parse, ct) => CommonOptions.RunAsync(parse, async (session, token) =>
        {
            var api = session.Serialization(session.Endpoint(parse.GetValue(environment)));
            var treeScope = parse.GetValue(scope)!.ToLowerInvariant() switch
            {
                "item" => TreeScope.SingleItem,
                "children" => TreeScope.ItemAndChildren,
                "descendants" => TreeScope.ItemAndDescendants,
                var s => throw new CliException($"Unknown scope '{s}'. Use item, children or descendants."),
            };
            var db = parse.GetValue(database)!;
            var metadata = (await api.GetMetadataAsync([new SerializeRequest(parse.GetValue(root)!, db, treeScope, [])], token))[0];
            var templates = parse.GetValue(template)!.Select(t => Guid.TryParse(t, out var g) ? g : throw new CliException($"'{t}' is not a template ID.")).ToHashSet();
            var namePattern = parse.GetValue(name);
            var candidates = metadata
                .Where(m => templates.Count == 0 || templates.Contains(m.TemplateId))
                .Where(m => namePattern is null || Wildcard.IsMatch(m.Name, namePattern))
                .ToList();

            var fieldFilters = parse.GetValue(field)!.Select(f =>
            {
                var eq = f.IndexOf('=');
                return eq <= 0 ? throw new CliException($"'{f}' is not <field>=<wildcard>.") : (Field: f[..eq], Pattern: f[(eq + 1)..]);
            }).ToList();
            var showFields = parse.GetValue(show)!;
            Dictionary<Guid, ItemData> data = [];
            if (fieldFilters.Count > 0 || showFields.Length > 0)
            {
                data = (await api.GetItemDataAsync(db, candidates.Select(c => c.Id).ToList(), [], token)).ToDictionary();
                candidates = candidates.Where(c => data.TryGetValue(c.Id, out var item) && fieldFilters.All(f => FieldValues(item, f.Field).Any(v => Wildcard.IsMatch(v, f.Pattern)))).ToList();
            }
            var max = parse.GetValue(limit);
            if (max > 0)
            {
                candidates = candidates.Take(max).ToList();
            }

            session.Output.Document("item query", w =>
            {
                w.WriteNumber("count", candidates.Count);
                w.WriteStartArray("items");
                foreach (var c in candidates)
                {
                    w.WriteStartObject();
                    w.WriteString("id", c.Id);
                    w.WriteString("path", c.Path);
                    w.WriteString("templateId", c.TemplateId);
                    w.WriteString("parentId", c.ParentId);
                    if (showFields.Length > 0 && data.TryGetValue(c.Id, out var item))
                    {
                        w.WriteStartObject("fields");
                        foreach (var f in showFields)
                        {
                            w.WriteString(f, FieldValues(item, f).FirstOrDefault());
                        }
                        w.WriteEndObject();
                    }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            });
            foreach (var c in candidates)
            {
                var line = new StringBuilder($"{c.Path} ({c.Id})");
                if (showFields.Length > 0 && data.TryGetValue(c.Id, out var item))
                {
                    foreach (var f in showFields)
                    {
                        line.Append($"  {f}={FieldValues(item, f).FirstOrDefault()}");
                    }
                }
                session.Output.Line(line.ToString());
            }
            session.Output.Info($"{candidates.Count} items.");
            return ExitCodes.Success;
        }, ct));
        return cmd;
    }

    private static IEnumerable<string> FieldValues(ItemData item, string field)
    {
        var byId = Guid.TryParse(field, out var id);
        return item.AllFields
            .Where(f => byId ? f.Id == id : string.Equals(f.Hint, field, StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Value);
    }
}
