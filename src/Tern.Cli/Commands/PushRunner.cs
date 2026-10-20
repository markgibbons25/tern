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
        if (request.WhatIf)
        {
            output.Info("What if mode is active. No changes will be made.");
        }
        var modules = session.SelectModules(request.Include, request.Exclude);
        var endpoint = session.Endpoint(request.Environment);
        if (!endpoint.AllowWrite && !request.WhatIf && request.PlanOut is null)
        {
            throw new CliException($"Endpoint '{endpoint.Name}' does not allow writes. Log in again with --allow-write true to push to it.");
        }
        var includes = modules.SelectMany(m => m.Includes).ToList();
        var excluded = session.ExcludedFields(modules).ToHashSet();
        bool IncludeField(Guid id) => !excluded.Contains(id);

        var issues = request.SkipValidation ? [] : FileValidator.Validate(includes);
        if (issues.Count > 0)
        {
            foreach (var issue in issues)
            {
                output.Warn($"[{issue.Include.Module.Namespace}/{issue.Include.Name}] {issue.Kind}: {issue.Message}");
            }
            throw new CliException("Filesystem integrity validation found errors in serialized files! Use the validate command to fix these.");
        }

        var files = new FileItemTree(IncludeField);
        var api = session.Serialization(endpoint);
        var cm = new CmItemTree(api, [.. excluded], endpoint.Name);
        await cm.PrefetchAsync(includes, ct);

        var applier = new CmApplier(api, [.. excluded], session.Settings.WriteBatchSize, output.Info);
        var engine = new SyncEngine(files, cm, applier, PlanDirection.Push);
        var result = await engine.PlanAsync(includes, request.Force, ct);
        SyncRunner.PrintPlan(output, result, endpoint.Name);
        var exit = await SyncRunner.FinishAsync(session, request, engine, result, includes, "ser push", watch, ct);
        var security = new SecuritySync(new SecurityApi(api.Client), session.Root);
        await SyncRunner.RunSecurityAsync(output, "roles", "role", () => security.PlanRolePushAsync(modules, ct), request, ct);
        await SyncRunner.RunSecurityAsync(output, "users", "user", () => security.PlanUserPushAsync(modules, ct), request, ct);

        if (request.Publish && !request.WhatIf && request.PlanOut is null && !result.Plan.IsEmpty)
        {
            var ids = result.Plan.Operations.Select(o => o.Action == PlanAction.Delete ? o.ParentId : o.Id).Distinct().Select(i => i.ToString("D")).ToList();
            var publishing = new PublishingApi(api.Client);
            var targets = request.PublishTargets.Count == 0 ? [null] : request.PublishTargets.Cast<string?>().ToList();
            await Task.WhenAll(targets.Select(t => PublishCommands.RunAndWaitAsync(publishing, output, () => publishing.PublishAsync(null, [], ids, false, t, ct), ct)));
        }
        return exit;
    }

    public static async Task<int> DiffAsync(Session session, DiffRequest request, CancellationToken ct)
    {
        var output = session.Output;
        var watch = Stopwatch.StartNew();
        var sourceEndpoint = session.Endpoint(request.Source);
        var targetEndpoint = session.Endpoint(request.Destination);
        if (request.Push && !targetEndpoint.AllowWrite)
        {
            throw new CliException($"Endpoint '{targetEndpoint.Name}' does not allow writes. Log in again with --allow-write true to push to it.");
        }

        List<Include> includes;
        IReadOnlyList<Guid> excluded;
        if (request.Path is { } path)
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
        await Task.WhenAll(source.PrefetchAsync(includes, ct), target.PrefetchAsync(includes, ct));

        var applier = new CmApplier(targetApi, excluded, session.Settings.WriteBatchSize, output.Info);
        var engine = new SyncEngine(source, target, applier, PlanDirection.Copy);
        var result = await engine.PlanAsync(includes, force: true, ct);
        SyncRunner.PrintPlan(output, result, targetEndpoint.Name);
        var syncRequest = new SyncRequest(null, request.Include, request.Exclude, WhatIf: !request.Push, false, true, request.PlanOut, false, []);
        return await SyncRunner.FinishAsync(session, syncRequest, engine, result, includes, "ser diff", watch, ct);
    }
}
