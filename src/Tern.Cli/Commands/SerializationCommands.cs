using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using Tern.Cli.Common;
using Tern.Core.Configuration;
using Tern.Core.Planning;
using Tern.Core.Trees;
using Tern.Core.Validation;
using Tern.Remote.Management;


namespace Tern.Cli.Commands;

public static class SerializationCommands
{
    public static Command Build()
    {
        var ser = new Command("ser", "Manage resource serialization");
        ser.Aliases.Add("serialization");
        ser.Subcommands.Add(Info());
        ser.Subcommands.Add(Explain());
        ser.Subcommands.Add(Validate());
        ser.Subcommands.Add(Pull());
        ser.Subcommands.Add(Push());
        ser.Subcommands.Add(Diff());
        ser.Subcommands.Add(WatchCommand.Build());
        ser.Subcommands.Add(PackageCommands.Build());
        return ser;
    }

    private static Command Info()
    {
        var include = CommonOptions.Include();
        var exclude = CommonOptions.Exclude();
        var cmd = new Command("info", "Show serialization configuration status") { include, exclude };
        cmd.SetAction((parse, ct) => CommonOptions.RunAsync(parse, (session, _) =>
        {
            var modules = session.SelectModules(parse.GetValue(include)!, parse.GetValue(exclude)!);
            session.Output.Document("ser info", w =>
            {
                w.WriteStartArray("modules");
                foreach (var m in modules)
                {
                    w.WriteStartObject();
                    w.WriteString("namespace", m.Namespace);
                    w.WriteString("file", m.SourcePath);
                    w.WriteStartArray("references");
                    m.References.ToList().ForEach(w.WriteStringValue);
                    w.WriteEndArray();
                    w.WriteStartArray("includes");
                    foreach (var i in m.Includes)
                    {
                        w.WriteStartObject();
                        w.WriteString("name", i.Name);
                        w.WriteString("path", i.Path.ToString());
                        w.WriteString("database", i.Database);
                        w.WriteString("scope", i.Scope.ToString());
                        w.WriteString("allowedPushOperations", i.PushOperations.ToString());
                        w.WriteString("physicalPath", i.PhysicalRoot);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            });
            foreach (var m in modules)
            {
                session.Output.Line($"{m.Namespace} ({Path.GetRelativePath(session.Root.RootDirectory, m.SourcePath)})");
                if (m.References.Count > 0)
                {
                    session.Output.Line($"  References: {string.Join(", ", m.References)}");
                }
                foreach (var i in m.Includes)
                {
                    session.Output.Line($"  [{i.Database}] {i.Name}: {i.Path} ({i.Scope}, {i.PushOperations}) -> {Path.GetRelativePath(session.Root.RootDirectory, i.PhysicalRoot)}");
                    foreach (var r in i.Rules)
                    {
                        session.Output.Line($"      rule {r.Path}: {r.Scope}{(r.PushOperations is { } o ? ", " + o : string.Empty)}{(r.Alias is { } a ? ", alias " + a : string.Empty)}");
                    }
                }
                foreach (var r in m.Roles)
                {
                    session.Output.Line($"  [roles] {r.Domain}\\{r.Pattern}");
                }
                foreach (var u in m.Users)
                {
                    session.Output.Line($"  [users] {u.Domain}\\{u.Pattern}");
                }
            }
            return Task.FromResult(ExitCodes.Success);
        }, ct));
        return cmd;
    }

    private static Command Explain()
    {
        var path = new Option<string>("--path", "-p") { Description = "Item path to explain", Required = true };
        var database = new Option<string>("--database", "-d") { Description = "Database of the item path to explain (default: master)", DefaultValueFactory = _ => "master" };
        var cmd = new Command("explain", "Explain whether an item path is serialized, and why") { path, database };
        cmd.SetAction((parse, ct) => CommonOptions.RunAsync(parse, (session, _) =>
        {
            var itemPath = ItemPath.Parse(parse.GetValue(path)!);
            var db = parse.GetValue(database)!;
            var matches = new List<(Include Include, bool Included, List<string> Reasons)>();
            foreach (var include in session.AllModules.SelectMany(m => m.Includes).Where(i => i.Database.Equals(db, StringComparison.OrdinalIgnoreCase)))
            {
                var reasons = new List<string>();
                var included = include.IncludesPath(itemPath, reasons.Add);
                if (itemPath.IsDescendantOrSelfOf(include.Path))
                {
                    matches.Add((include, included, reasons));
                }
            }
            var winner = matches.FirstOrDefault(m => m.Included);
            session.Output.Document("ser explain", w =>
            {
                w.WriteString("path", itemPath.ToString());
                w.WriteString("database", db);
                w.WriteBoolean("serialized", winner.Include is not null);
                if (winner.Include is { } i)
                {
                    w.WriteString("module", i.Module.Namespace);
                    w.WriteString("include", i.Name);
                    w.WriteString("file", new Core.ItemFiles.ItemFilePaths(i).FileFor(itemPath));
                    w.WriteString("allowedPushOperations", i.PushOperationsFor(itemPath).ToString());
                }
            });
            if (matches.Count == 0)
            {
                session.Output.Line($"{itemPath} is not inside any include in the {db} database.");
                return Task.FromResult(ExitCodes.Success);
            }
            foreach (var (include, included, reasons) in matches)
            {
                session.Output.Line($"[{include.Module.Namespace}] {include.Name} ({include.Path}):");
                foreach (var reason in reasons)
                {
                    session.Output.Line("  " + reason);
                }
                if (included)
                {
                    session.Output.Line($"  Serialized to {new Core.ItemFiles.ItemFilePaths(include).FileFor(itemPath)}");
                    session.Output.Line($"  Allowed push operations: {include.PushOperationsFor(itemPath)}");
                }
            }
            return Task.FromResult(ExitCodes.Success);
        }, ct));
        return cmd;
    }

    private static Command Validate()
    {
        var include = CommonOptions.Include();
        var exclude = CommonOptions.Exclude();
        var fix = new Option<bool>("--fix", "-f") { Description = "Fix the issues that can be fixed automatically" };
        var cmd = new Command("validate", "Validate the integrity of serialized files") { include, exclude, fix };
        cmd.SetAction((parse, ct) => CommonOptions.RunAsync(parse, (session, _) =>
        {
            var modules = session.SelectModules(parse.GetValue(include)!, parse.GetValue(exclude)!);
            // Read through the same file index as push and pull, so either can reuse what validate read.
            var excluded = session.ExcludedFields(modules).ToHashSet();
            var issues = FileValidator.Validate(modules.SelectMany(m => m.Includes), new FileItemTree(id => !excluded.Contains(id), index: session.FileIndex(excluded)));
            var changes = parse.GetValue(fix) ? FileValidator.Fix(issues) : [];
            session.Output.Document("ser validate", w =>
            {
                w.WriteBoolean("valid", issues.Count == 0);
                w.WriteStartArray("issues");
                foreach (var issue in issues)
                {
                    w.WriteStartObject();
                    w.WriteString("kind", issue.Kind.ToString());
                    w.WriteString("module", issue.Include.Module.Namespace);
                    w.WriteString("include", issue.Include.Name);
                    w.WriteString("file", issue.File);
                    w.WriteString("message", issue.Message);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteStartArray("fixed");
                changes.ForEach(w.WriteStringValue);
                w.WriteEndArray();
            });
            foreach (var issue in issues)
            {
                session.Output.Warn($"[{issue.Include.Module.Namespace}/{issue.Include.Name}] {issue.Kind}: {issue.Message}");
            }
            foreach (var change in changes)
            {
                session.Output.Line(change);
            }
            if (issues.Count == 0)
            {
                session.Output.Line("Serialized items are valid.");
                return Task.FromResult(ExitCodes.Success);
            }
            var unfixed = parse.GetValue(fix) ? issues.Count(i => i.Kind == IssueKind.NonUniquePath || i.Kind == IssueKind.ParseError) : issues.Count;
            session.Output.Line(parse.GetValue(fix) ? $"Fixed {changes.Count} problems; {unfixed} need fixing by hand." : $"Found {issues.Count} problems. Run with --fix to repair what can be repaired.");
            return Task.FromResult(unfixed == 0 ? ExitCodes.Success : ExitCodes.Failure);
        }, ct));
        return cmd;
    }

    private static Command Pull()
    {
        var o = new SyncOptions();
        var cmd = new Command("pull", "Serialize content items from a Sitecore instance to the file system");
        o.AddTo(cmd);
        cmd.SetAction((parse, ct) => CommonOptions.RunAsync(parse, (session, token) => SyncRunner.PullAsync(session, o.Read(parse), token), ct));
        return cmd;
    }

    private static Command Push()
    {
        var o = new SyncOptions(push: true);
        var cmd = new Command("push", "Deserialize content items from the file system to a Sitecore instance");
        o.AddTo(cmd);
        cmd.SetAction((parse, ct) => CommonOptions.RunAsync(parse, (session, token) => SyncRunner.PushAsync(session, o.Read(parse), token), ct));
        return cmd;
    }

    private static Command Diff()
    {
        var source = new Option<string?>("--source", "-s") { Description = "Named Sitecore endpoint to use as a source for comparison" };
        var destination = new Option<string?>("--destination", "-d") { Description = "Named Sitecore endpoint to use as a destination for comparison" };
        var include = CommonOptions.Include();
        var exclude = CommonOptions.Exclude();
        var path = new Option<string?>("--path", "-p") { Description = "Item path to compare (instead of include/exclude)" };
        var sourceDb = new Option<string?>("--source-database") { Description = "Source database (when used with --path)" };
        var destinationDb = new Option<string?>("--destination-database") { Description = "Destination database (when used with --path)" };
        var push = new Option<bool>("--push") { Description = "Applies the differences detected to the destination (diff + push)" };
        var planOut = new Option<string?>("--plan-out") { Description = "Save the Plan to this file instead of applying it" };
        var cmd = new Command("diff", "Compare content between two Sitecore endpoints") { source, destination, include, exclude, path, sourceDb, destinationDb, push, planOut };
        cmd.SetAction((parse, ct) => CommonOptions.RunAsync(parse, (session, token) => SyncRunner.DiffAsync(session, new DiffRequest(
            parse.GetValue(source) ?? throw new CliException("--source is required."),
            parse.GetValue(destination) ?? throw new CliException("--destination is required."),
            parse.GetValue(include)!, parse.GetValue(exclude)!, parse.GetValue(path), parse.GetValue(sourceDb), parse.GetValue(destinationDb),
            parse.GetValue(push), parse.GetValue(planOut)), token), ct));
        return cmd;
    }
}

/// <summary>The options Pull and Push share.</summary>
public sealed class SyncOptions
{
    private readonly bool _push;
    private readonly Option<string?> _environment = CommonOptions.EnvironmentName();
    private readonly Option<string[]> _include = CommonOptions.Include();
    private readonly Option<string[]> _exclude = CommonOptions.Exclude();
    private readonly Option<bool> _whatIf = new("--what-if", "-w") { Description = "Lists commands that would be executed, without executing them" };
    private readonly Option<bool> _skipValidation = new("--skip-validation", "-s") { Description = "Skips filesystem integrity validation prior to syncing" };
    private readonly Option<bool> _force = new("--force", "-fr") { Description = "Perform force sync. In case you have invalid includes" };
    private readonly Option<string?> _planOut = new("--plan-out") { Description = "Save the Plan to this file without applying it, for `tern apply`" };
    private readonly Option<bool> _publish = new("--publish", "-p") { Description = "Publish synced items. Not recommended to use with Publishing Service due to performance drop" };
    private readonly Option<string[]> _targets = new("--targets", "--pt") { Description = "Comma separated list of targets database to publish. Blank publishes to the default publishing target (first one in the list)", AllowMultipleArgumentsPerToken = true, DefaultValueFactory = _ => [] };

    public SyncOptions(bool push = false) => _push = push;

    public void AddTo(Command cmd)
    {
        cmd.Options.Add(_environment);
        cmd.Options.Add(_include);
        cmd.Options.Add(_exclude);
        cmd.Options.Add(_whatIf);
        cmd.Options.Add(_skipValidation);
        cmd.Options.Add(_force);
        cmd.Options.Add(_planOut);
        if (_push)
        {
            cmd.Options.Add(_publish);
            cmd.Options.Add(_targets);
        }
    }

    public SyncRequest Read(ParseResult parse) => new(
        parse.GetValue(_environment),
        parse.GetValue(_include)!,
        parse.GetValue(_exclude)!,
        parse.GetValue(_whatIf),
        parse.GetValue(_skipValidation),
        parse.GetValue(_force),
        parse.GetValue(_planOut),
        _push && parse.GetValue(_publish),
        _push ? parse.GetValue(_targets)!.SelectMany(t => t.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToArray() : []);
}

public sealed record SyncRequest(string? Environment, IReadOnlyList<string> Include, IReadOnlyList<string> Exclude, bool WhatIf, bool SkipValidation, bool Force, string? PlanOut, bool Publish, IReadOnlyList<string> PublishTargets);

public sealed record DiffRequest(string Source, string Destination, IReadOnlyList<string> Include, IReadOnlyList<string> Exclude, string? Path, string? SourceDatabase, string? DestinationDatabase, bool Push, string? PlanOut);
