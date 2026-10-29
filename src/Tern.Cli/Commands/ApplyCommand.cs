using System.CommandLine;
using System.Diagnostics;

using Tern.Cli.Common;
using Tern.Core.Configuration;
using Tern.Core.Planning;
using Tern.Core.Trees;
using Tern.Remote.Management;

namespace Tern.Cli.Commands;

/// <summary>
/// <c>apply</c>: carries out a saved Plan exactly as it was reviewed. Before anything changes, every
/// target item must still have the Signature the Plan expected (ADR 0008).
/// </summary>
public static class ApplyCommand
{
    public static Command Build()
    {
        var file = new Argument<string>("plan") { Description = "A Plan saved with --plan-out" };
        var environment = new Option<string?>("--environment-name", "-n") { Description = "Endpoint to apply a Pull or Push Plan with (default: the default endpoint)" };
        var source = new Option<string?>("--source", "-s") { Description = "Source endpoint of a diff Plan (default: the one endpoint with the Plan's source host)" };
        var destination = new Option<string?>("--destination", "-d") { Description = "Destination endpoint of a diff Plan (default: the one endpoint with the Plan's target host)" };
        var cmd = new Command("apply", "Carry out a saved Plan") { file, environment, source, destination };
        cmd.SetAction((parse, ct) => CommonOptions.RunAsync(parse, async (session, token) =>
        {
            var watch = Stopwatch.StartNew();
            var plan = PlanJson.Read(await File.ReadAllTextAsync(parse.GetValue(file)!, token));
            var output = session.Output;
            output.Info($"Applying a {plan.Direction.ToString().ToLowerInvariant()} Plan from {plan.CreatedAt:u}: {plan.Operations.Count} changes, {plan.Source} -> {plan.Target}.");
            var modules = session.AllModules;
            var excluded = (plan.ExcludedFields ?? session.ExcludedFields(modules)).ToHashSet();
            bool IncludeField(Guid id) => !excluded.Contains(id);
            var includes = modules.SelectMany(m => m.Includes).ToList();
            includes.AddRange(plan.AdHocIncludes
                .Where(a => !includes.Any(i => i.Module.Namespace == a.Module && i.Name == a.Name))
                .Select(a => AdHocInclude(a, session.ConfigDirectory)));
            SyncEngine engine;
            switch (plan.Direction)
            {
                case PlanDirection.Pull:
                    {
                        var endpoint = session.Endpoint(parse.GetValue(environment));
                        var files = new FileItemTree(IncludeField, index: session.FileIndex(excluded));
                        engine = new SyncEngine(new CmItemTree(session.Serialization(endpoint), [.. excluded]), files, new FileApplier(files, IncludeField), PlanDirection.Pull);
                        break;
                    }
                case PlanDirection.Push:
                    {
                        var endpoint = session.Endpoint(parse.GetValue(environment));
                        if (!endpoint.AllowWrite)
                        {
                            throw new CliException(ReadOnlyEndpoint.Message(endpoint, null));
                        }
                        var api = session.Serialization(endpoint);
                        engine = new SyncEngine(new FileItemTree(IncludeField, index: session.FileIndex(excluded)), new CmItemTree(api, [.. excluded]), new CmApplier(api, [.. excluded], session.Settings.WriteBatchSize, output.Info), PlanDirection.Push);
                        break;
                    }
                default:
                    {
                        var sourceName = parse.GetValue(source) ?? EndpointFor(session, plan.Source);
                        var destinationName = parse.GetValue(destination) ?? EndpointFor(session, plan.Target);
                        if (sourceName is null || destinationName is null)
                        {
                            throw new CliException(MissingEndpoints(session, plan, sourceName, destinationName));
                        }
                        var s = session.Endpoint(sourceName);
                        var d = session.Endpoint(destinationName);
                        if (!d.AllowWrite)
                        {
                            throw new CliException(ReadOnlyEndpoint.Message(d, null));
                        }
                        var targetApi = session.Serialization(d);
                        engine = new SyncEngine(new CmItemTree(session.Serialization(s), [.. excluded]), new CmItemTree(targetApi, [.. excluded]), new CmApplier(targetApi, [.. excluded], session.Settings.WriteBatchSize, output.Info), PlanDirection.Copy);
                        break;
                    }
            }
            var applied = await engine.ApplyAsync(plan, includes, checkPreconditions: true, token);
            foreach (var a in applied)
            {
                output.Info($"[{a.Operation.Database}] {SyncRunner.Describe(a.Operation)}", SyncRunner.ColorOf(a.Operation.Action));
            }
            output.Document("apply", w =>
            {
                w.WriteNumber("applied", applied.Count);
                w.WriteNumber("elapsedMs", watch.ElapsedMilliseconds);
            });
            output.Info($"Applied {applied.Count} changes.");
            return ExitCodes.Success;
        }, ct));
        return cmd;
    }

    /// <summary>Rebuilds an Include the Plan recorded because no Module defines it (<c>ser diff --path</c>).</summary>
    internal static Include AdHocInclude(PlanInclude recorded, string configDirectory)
    {
        var include = new Include
        {
            Name = recorded.Name,
            Path = ItemPath.Parse(recorded.Path),
            Database = recorded.Database,
            Scope = Enum.Parse<TreeScope>(recorded.Scope, ignoreCase: true),
        };
        include.Module = new Module { Namespace = recorded.Module, SourcePath = configDirectory, Includes = [include] };
        return include;
    }

    /// <summary>The one Endpoint whose host is <paramref name="host"/>, or null when none or several are.</summary>
    private static string? EndpointFor(Session session, string host)
    {
        var matches = HostMatches(session, host);
        return matches.Count == 1 ? matches[0] : null;
    }

    private static List<string> HostMatches(Session session, string host) =>
        [.. session.Store.All.Where(e => e.Host is { } h && h.TrimEnd('/').Equals(host.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)).Select(e => e.Name)];

    /// <summary>One message for both sides, naming the Endpoints that share a host where that is why.</summary>
    private static string MissingEndpoints(Session session, Plan plan, string? sourceName, string? destinationName)
    {
        var lines = new List<string> { "Name the endpoints this diff Plan was made between:" };
        void Add(string option, string? name, string host)
        {
            if (name is not null)
            {
                return;
            }
            var matches = HostMatches(session, host);
            lines.Add(matches.Count == 0
                ? $"  {option}: no endpoint has the host {host}."
                : $"  {option}: {string.Join(", ", matches)} share the host {host}; pick one.");
        }
        Add("--source", sourceName, plan.Source);
        Add("--destination", destinationName, plan.Target);
        return string.Join(Environment.NewLine, lines);
    }
}
