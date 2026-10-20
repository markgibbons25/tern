using System.Diagnostics;
using System.Text.Json;
using Tern.Cli.Common;
using Tern.Core.Configuration;
using Tern.Core.Planning;
using Tern.Core.Trees;
using Tern.Core.Validation;
using Tern.Remote.Management;
using Tern.Remote.Security;

namespace Tern.Cli.Commands;

/// <summary>Runs Pull, Push and Endpoint-to-Endpoint diff, and prints them as the Official CLI does.</summary>
public static class SyncRunner
{
    public static async Task<int> PullAsync(Session session, SyncRequest request, CancellationToken ct)
    {
        var output = session.Output;
        var watch = Stopwatch.StartNew();
        if (request.WhatIf)
        {
            output.Info("What if mode is active. No changes will be made.");
        }
        var modules = session.SelectModules(request.Include, request.Exclude);
        var endpoint = session.Endpoint(request.Environment);
        var includes = modules.SelectMany(m => m.Includes).ToList();
        var excluded = session.ExcludedFields(modules).ToHashSet();
        bool IncludeField(Guid id) => !excluded.Contains(id);

        var files = new FileItemTree(IncludeField, index: session.FileIndex(excluded));
        var validation = request.SkipValidation ? Task.FromResult(new List<ValidationIssue>()) : Task.Run(() => FileValidator.Validate(includes, files), ct);
        var cm = new CmItemTree(session.Serialization(endpoint), [.. excluded], endpoint.Name);
        await cm.PrefetchAsync(includes, ct, FileItemTree.CountFiles);
        output.Debug($"Prefetched metadata for {includes.Count} includes in {watch.ElapsedMilliseconds} ms ({cm.Api.Client.RequestCount} requests).");

        var engine = new SyncEngine(cm, files, new FileApplier(files, IncludeField), PlanDirection.Pull);
        var result = await engine.PlanAsync(includes, request.Force, ct);
        PrintPlan(output, result, endpoint.Name);

        var issues = await validation;
        if (issues.Count > 0)
        {
            foreach (var issue in issues)
            {
                output.Warn($"[{issue.Include.Module.Namespace}/{issue.Include.Name}] {issue.Kind}: {issue.Message}");
            }
            throw new CliException("Discovered changes will not be applied." + Environment.NewLine + "Filesystem integrity validation found errors in serialized files! Use the validate command to fix these.");
        }

        var exit = await FinishAsync(session, request, engine, result, includes, "ser pull", watch, ct);
        if (!request.WhatIf && request.PlanOut is null)
        {
            // The Official CLI leaves a folder for every Include, even one with no items yet.
            foreach (var include in includes)
            {
                Directory.CreateDirectory(include.PhysicalRoot);
            }
        }
        var security = new SecuritySync(new SecurityApi(cm.Api.Client), session.Root);
        await RunSecurityAsync(output, "roles", "role", () => security.PlanRolePullAsync(modules, ct), request, ct);
        await RunSecurityAsync(output, "users", "user", () => security.PlanUserPullAsync(modules, ct), request, ct);
        return exit;
    }

    /// <summary>Prints and, unless this is a what-if, applies role or user changes.</summary>
    internal static async Task RunSecurityAsync(Output output, string label, string entity, Func<Task<List<SecurityChange>>> plan, SyncRequest request, CancellationToken ct)
    {
        var changes = await plan();
        foreach (var change in changes)
        {
            output.Info(change.Message);
        }
        if (changes.Count > 0)
        {
            output.Info(string.Empty);
        }
        output.Info($"[{label}] Discovered {changes.Count} changes after evaluating {entity} list.");
        output.Info(string.Empty);
        if (!request.WhatIf && request.PlanOut is null)
        {
            foreach (var change in changes)
            {
                await change.Apply(ct);
            }
        }
    }

    public static Task<int> PushAsync(Session session, SyncRequest request, CancellationToken ct) => PushRunner.PushAsync(session, request, ct);

    public static Task<int> DiffAsync(Session session, DiffRequest request, CancellationToken ct) => PushRunner.DiffAsync(session, request, ct);

    /// <summary>Prints, saves or applies a Plan once it has been made.</summary>
    internal static async Task<int> FinishAsync(Session session, SyncRequest request, SyncEngine engine, PlanResult result, IReadOnlyList<Include> includes, string command, Stopwatch watch, CancellationToken ct)
    {
        var output = session.Output;
        if (request.PlanOut is { } planFile)
        {
            await File.WriteAllBytesAsync(planFile, PlanJson.ToBytes(result.Plan), ct);
            output.Info($"Saved the Plan to {planFile}. Run `tern apply {planFile}` to carry it out.");
        }
        List<AppliedOperation> applied = [];
        if (!request.WhatIf && request.PlanOut is null && !result.Plan.IsEmpty)
        {
            foreach (var db in result.Plan.Operations.Select(o => o.Database).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                output.Info($"[{db}] Applying changes...");
            }
            applied = await engine.ApplyAsync(result.Plan, includes, checkPreconditions: false, ct);
            foreach (var group in result.Plan.Operations.GroupBy(o => o.Database, StringComparer.OrdinalIgnoreCase))
            {
                var subtrees = group.Select(o => (o.Module, o.Include)).Distinct().Count();
                output.Info(result.Plan.Direction == PlanDirection.Pull
                    ? $"[{group.Key}] Changes have been applied to serialized items ({subtrees} subtrees)."
                    : $"[{group.Key}] Changes have been applied to {result.Plan.Target} ({subtrees} subtrees).");
            }
        }
        output.Document(command, w =>
        {
            w.WriteBoolean("whatIf", request.WhatIf || request.PlanOut is not null);
            w.WriteNumber("itemsEvaluated", result.NodesEvaluated);
            w.WriteNumber("changes", result.Plan.Operations.Count);
            w.WriteNumber("applied", applied.Count);
            w.WriteNumber("elapsedMs", watch.ElapsedMilliseconds);
            w.WritePropertyName("plan");
            PlanJson.Write(w, result.Plan);
        });
        output.Debug($"Finished in {watch.ElapsedMilliseconds} ms.");
        return ExitCodes.Success;
    }

    internal static void PrintPlan(Output output, PlanResult result, string endpointName)
    {
        foreach (var warning in result.Plan.Warnings)
        {
            output.Warn(warning);
        }
        foreach (var group in result.Includes.GroupBy(i => i.Include.Database, StringComparer.OrdinalIgnoreCase))
        {
            var db = group.Key;
            var ops = group.SelectMany(g => g.Operations).ToList();
            output.Info(string.Empty);
            foreach (var op in ops)
            {
                output.Info($"[{db}] {Describe(op)}");
            }
            if (ops.Count > 0)
            {
                output.Info(string.Empty);
            }
            output.Info($"[{db}] Discovered {ops.Count} changes after evaluating {group.Sum(g => g.NodesEvaluated)} total items.");
        }
    }

    internal static string Describe(PlanOperation op) => op.Action switch
    {
        PlanAction.Create => $"[A] {op.Path} ({op.Id})",
        PlanAction.Update => $"[U] {op.Path} ({op.Id})",
        PlanAction.Delete => $"[D] {op.TargetPath ?? op.Path} ({op.Id})",
        PlanAction.Move => $"[M] {op.TargetPath} to {op.Path} ({op.Id})",
        PlanAction.Rename => $"[R] {op.Path} ({op.Id}) from '{ItemPath.Parse(op.TargetPath!).Name}' to '{ItemPath.Parse(op.Path).Name}'",
        _ => op.ToString(),
    };
}
