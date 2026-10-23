using System.Diagnostics;

using Tern.Cli.Common;
using Tern.Core.Configuration;
using Tern.Core.Planning;
using Tern.Core.Trees;
using Tern.Core.Validation;
using Tern.Remote.Management;
using Tern.Remote.Security;

namespace Tern.Cli.Commands;

/// <summary>Push (files to a CM) and diff between two Endpoints (CM to CM).</summary>
public static class PushRunner
{
    public static async Task<int> PushAsync(Session session, SyncRequest request, CancellationToken ct)
    {
        var output = session.Output;
        var watch = Stopwatch.StartNew();
        var phases = new Phases(watch);
        if (request.WhatIf)
        {
            output.Info("What if mode is active. No changes will be made.");
        }
        var modules = request.Modules ?? session.SelectModules(request.Include, request.Exclude);
        var endpoint = session.Endpoint(request.Environment, announce: false);
        if (!endpoint.AllowWrite && !request.WhatIf && request.PlanOut is null)
        {
            throw new CliException(ReadOnlyEndpoint.Message(endpoint, "push to it"));
        }
        var includes = modules.SelectMany(m => m.Includes).ToList();
        var excluded = session.ExcludedFields(modules).ToHashSet();
        bool IncludeField(Guid id) => !excluded.Contains(id);
        if (request.Announce)
        {
            output.Info($"Pushing to {SyncRunner.Describe(endpoint)}.");
        }

        // Validation parses the files the Plan then uses, while the CM's metadata is fetched.
        var files = new FileItemTree(IncludeField, index: session.FileIndex(excluded));
        var api = session.Serialization(endpoint);
        var cm = new CmItemTree(api, [.. excluded], endpoint.Name);
        var prefetch = SyncRunner.ReadAsync(output, includes, [(cm, endpoint.Name)], FileItemTree.CountFiles, ct);
        var issues = request.SkipValidation ? [] : await Task.Run(() => FileValidator.Validate(includes, files), ct);
        if (issues.Count > 0)
        {
            output.StopProgress();
            foreach (var issue in issues)
            {
                output.Warn($"[{issue.Include.Module.Namespace}/{issue.Include.Name}] {issue.Kind}: {issue.Message}");
            }
            throw new CliException("Filesystem integrity validation found errors in serialized files! Use the validate command to fix these.");
        }
        await prefetch;
        phases.End("read");

        var applier = new CmApplier(api, [.. excluded], session.Settings.WriteBatchSize, output.Info);
        var engine = new SyncEngine(files, cm, applier, PlanDirection.Push);
        var result = await engine.PlanAsync(includes, request.Force, ct);
        SyncRunner.PrintPlan(output, result, endpoint.Name, page: request.WhatIf || request.PlanOut is not null);
        phases.End("compare");
        var exit = await SyncRunner.FinishAsync(session, request, engine, result, includes, "ser push", watch, ct);
        phases.End("apply");
        var security = new SecuritySync(new SecurityApi(api.Client), session.Root);
        await SyncRunner.RunSecurityAsync(output, "roles", "role", () => security.PlanRolePushAsync(modules, ct), request, ct);
        await SyncRunner.RunSecurityAsync(output, "users", "user", () => security.PlanUserPushAsync(modules, ct), request, ct);
        phases.End("roles and users");

        if (request.Publish && !request.WhatIf && request.PlanOut is null && !result.Plan.IsEmpty)
        {
            var ids = result.Plan.Operations.Select(o => o.Action == PlanAction.Delete ? o.ParentId : o.Id).Distinct().Select(i => i.ToString("D")).ToList();
            var publishing = new PublishingApi(api.Client);
            var targets = request.PublishTargets.Count == 0 ? [null] : request.PublishTargets.Cast<string?>().ToList();
            await Task.WhenAll(targets.Select(t => PublishCommands.RunAndWaitAsync(publishing, output, () => publishing.PublishAsync(null, [], ids, false, t, ct), ct)));
            phases.End("publish");
        }
        SyncRunner.PrintElapsed(output, phases);
        return exit;
    }

    public static async Task<int> DiffAsync(Session session, DiffRequest request, CancellationToken ct)
    {
        var output = session.Output;
        var watch = Stopwatch.StartNew();
        var sourceEndpoint = session.Endpoint(request.Source, announce: false);
        var targetEndpoint = session.Endpoint(request.Destination, announce: false);
        if (request.Push && !targetEndpoint.AllowWrite)
        {
            throw new CliException(ReadOnlyEndpoint.Message(targetEndpoint, "push to it"));
        }

        List<Include> includes;
        IReadOnlyList<Guid> excluded;
        if (request.Modules is { } given)
        {
            includes = given.SelectMany(m => m.Includes).ToList();
            excluded = session.ExcludedFields(given);
        }
        else if (request.Path is { } path)
        {
            var include = new Include
            {
                Name = "path",
                Path = ItemPath.Parse(path),
                Database = request.SourceDatabase ?? "master",
                Scope = TreeScope.ItemAndDescendants,
            };
            include.Module = new Module { Namespace = "path", SourcePath = session.ConfigDirectory, Includes = [include] };
            includes = [include];
            excluded = [];
            if (request.DestinationDatabase is { } d && !d.Equals(include.Database, StringComparison.OrdinalIgnoreCase))
            {
                throw new CliException("Comparing different databases on each side is not supported yet; use the same database for --source-database and --destination-database.");
            }
        }
        else
        {
            var modules = session.SelectModules(request.Include, request.Exclude);
            includes = modules.SelectMany(m => m.Includes).ToList();
            excluded = session.ExcludedFields(modules);
        }

        var sourceApi = session.Serialization(sourceEndpoint);
        var targetApi = session.Serialization(targetEndpoint);
        var source = new CmItemTree(sourceApi, excluded, sourceEndpoint.Name);
        var target = new CmItemTree(targetApi, excluded, targetEndpoint.Name);
        var phases = new Phases(watch);
        if (request.Announce)
        {
            output.Info($"Comparing {SyncRunner.Describe(sourceEndpoint)} with {SyncRunner.Describe(targetEndpoint)}.");
        }
        output.Info($"Reading {includes.Count} include{(includes.Count == 1 ? string.Empty : "s")}...");
        var sides = new List<(CmItemTree Tree, string Name)> { (source, sourceEndpoint.Name), (target, targetEndpoint.Name) };
        var counts = await SyncRunner.ReadAsync(output, includes, sides, null, ct);
        output.Info($"Read {counts[0]:N0} items from {sourceEndpoint.Name} and {counts[1]:N0} from {targetEndpoint.Name} in {watch.Elapsed.TotalSeconds:0.0} s.");
        phases.End("read");

        var applier = new CmApplier(targetApi, excluded, session.Settings.WriteBatchSize, output.Info);
        var engine = new SyncEngine(source, target, applier, PlanDirection.Copy);
        output.Info("Comparing items...");
        var result = await engine.PlanAsync(includes, force: true, ct);
        SyncRunner.PrintPlan(output, result, targetEndpoint.Name, page: !request.Push);
        phases.End("compare");
        var syncRequest = new SyncRequest(null, request.Include, request.Exclude, WhatIf: !request.Push, false, true, request.PlanOut, false, []);
        var exit = await SyncRunner.FinishAsync(session, syncRequest, engine, result, includes, "ser diff", watch, ct);
        phases.End("apply");
        SyncRunner.PrintElapsed(output, phases);
        return exit;
    }
}
