using System.CommandLine;
using System.Diagnostics;

using Tern.Cli.Common;
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
        var source = new Option<string?>("--source", "-s") { Description = "Source endpoint of a diff Plan" };
        var destination = new Option<string?>("--destination", "-d") { Description = "Destination endpoint of a diff Plan" };
        var cmd = new Command("apply", "Carry out a saved Plan") { file, environment, source, destination };
        cmd.SetAction((parse, ct) => CommonOptions.RunAsync(parse, async (session, token) =>
        {
            var watch = Stopwatch.StartNew();
            var plan = PlanJson.Read(await File.ReadAllTextAsync(parse.GetValue(file)!, token));
            var output = session.Output;
            output.Info($"Applying a {plan.Direction.ToString().ToLowerInvariant()} Plan from {plan.CreatedAt:u}: {plan.Operations.Count} changes, {plan.Source} -> {plan.Target}.");
            var modules = session.AllModules;
            var excluded = session.ExcludedFields(modules).ToHashSet();
            bool IncludeField(Guid id) => !excluded.Contains(id);
            var includes = modules.SelectMany(m => m.Includes).ToList();
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
                        var s = session.Endpoint(parse.GetValue(source) ?? throw new CliException("--source is required to apply a diff Plan."));
                        var d = session.Endpoint(parse.GetValue(destination) ?? throw new CliException("--destination is required to apply a diff Plan."));
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
}
