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
        var phases = new Phases(watch);
        if (request.WhatIf)
        {
            output.Info("What if mode is active. No changes will be made.", SummaryColor);
        }
        var modules = request.Modules ?? session.SelectModules(request.Include, request.Exclude);
        var endpoint = session.Endpoint(request.Environment, announce: false);
        var includes = modules.SelectMany(m => m.Includes).ToList();
        var excluded = session.ExcludedFields(modules).ToHashSet();
        bool IncludeField(Guid id) => !excluded.Contains(id);
        if (request.Announce)
        {
            output.Info($"Pulling from {output.PaintEndpoint(endpoint.Name, endpoint.Host)}.");
        }

        var files = new FileItemTree(IncludeField, index: session.FileIndex(excluded));
        var validation = request.SkipValidation ? Task.FromResult(new List<ValidationIssue>()) : Task.Run(() => FileValidator.Validate(includes, files), ct);
        var cm = new CmItemTree(session.Serialization(endpoint), [.. excluded], endpoint.Name);
        await ReadAsync(output, includes, [(cm, endpoint.Name)], FileItemTree.CountFiles, ct);
        output.Debug($"Prefetched metadata for {includes.Count} includes in {watch.ElapsedMilliseconds} ms ({cm.Api.Client.RequestCount} requests).");
        phases.End("read");

        var engine = new SyncEngine(cm, files, new FileApplier(files, IncludeField), PlanDirection.Pull);
        var result = await engine.PlanAsync(includes, request.Force, ct);
        PrintPlan(output, result, endpoint.Name, page: request.WhatIf || request.PlanOut is not null);
        phases.End("compare");

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
        phases.End("apply");
        var security = new SecuritySync(new SecurityApi(cm.Api.Client), session.Root);
        await RunSecurityAsync(output, "roles", "role", () => security.PlanRolePullAsync(modules, ct), request, ct);
        await RunSecurityAsync(output, "users", "user", () => security.PlanUserPullAsync(modules, ct), request, ct);
        phases.End("roles and users");
        PrintElapsed(output, phases);
        return exit;
    }

    /// <summary>Prints and, unless this is a what-if, applies role or user changes.</summary>
    internal static async Task RunSecurityAsync(Output output, string label, string entity, Func<Task<List<SecurityChange>>> plan, SyncRequest request, CancellationToken ct)
    {
        var changes = await plan();
        if (changes.Count > 0 && !output.Quiet)
        {
            foreach (var change in changes)
            {
                output.Info(change.Message);
            }
            output.Info(string.Empty);
        }
        output.Info($"[{label}] Discovered {changes.Count} changes after evaluating {entity} list.", SummaryColor);
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
            foreach (var skipped in applied.Where(a => a.Skipped is not null))
            {
                output.Warn($"[{skipped.Operation.Database}] {skipped.Skipped}");
            }
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
            w.WriteNumber("applied", applied.Count(a => a.Skipped is null));
            w.WriteNumber("skipped", applied.Count(a => a.Skipped is not null));
            w.WriteNumber("elapsedMs", watch.ElapsedMilliseconds);
            w.WritePropertyName("plan");
            PlanJson.Write(w, result.Plan);
        });
        return ExitCodes.Success;
    }

    /// <summary>
    /// Prints each operation, unless <see cref="Output.Quiet"/>, and each database's summary. With <paramref name="page"/>, set when
    /// nothing is applied afterwards, a list taller than the terminal goes through a pager.
    /// </summary>
    internal static void PrintPlan(Output output, PlanResult result, string endpointName, bool page = false)
    {
        foreach (var warning in result.Plan.Warnings)
        {
            output.Warn(warning);
        }
        // As the Official CLI does: one blank line before the first summary and one after each.
        List<(string Text, string? Color)> lines = [(string.Empty, null)];
        foreach (var group in result.Includes.GroupBy(i => i.Include.Database, StringComparer.OrdinalIgnoreCase))
        {
            var db = group.Key;
            var ops = group.SelectMany(g => g.Operations).ToList();
            if (ops.Count > 0 && !output.Quiet)
            {
                foreach (var op in ops)
                {
                    lines.Add(($"[{db}] {Describe(op)}", ColorOf(op.Action)));
                }
                lines.Add((string.Empty, null));
            }
            lines.Add(($"[{db}] Discovered {ops.Count} changes after evaluating {group.Sum(g => g.NodesEvaluated)} total items.", SummaryColor));
            if (ops.Count > 0)
            {
                lines.Add(($"[{db}] {CountByAction(ops)}", DimColor));
            }
            lines.Add((string.Empty, null));
        }
        lines.AddRange(ModuleTable(result.Plan.Operations).Select(l => (l, (string?)null)));
        output.Page(lines, page);
    }

    /// <summary>"3 added, 7 updated, 2 deleted", naming only the actions there are.</summary>
    internal static string CountByAction(IEnumerable<PlanOperation> ops)
    {
        var counts = ops.CountBy(o => o.Action).ToDictionary();
        return string.Join(", ", ActionNames.Where(a => counts.ContainsKey(a.Action)).Select(a => $"{counts[a.Action]:N0} {a.Name}"));
    }

    /// <summary>
    /// The changes in each module, when they span more than one, so a large Plan can be read at a
    /// glance. Empty otherwise.
    /// </summary>
    internal static List<string> ModuleTable(IReadOnlyList<PlanOperation> ops)
    {
        var modules = ops.GroupBy(o => o.Module, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).ToList();
        if (modules.Count < 2)
        {
            return [];
        }
        var actions = ActionNames.Where(a => ops.Any(o => o.Action == a.Action)).ToList();
        var width = Math.Max("Module".Length, modules.Max(m => m.Key.Length));
        string Row(string name, IEnumerable<string> cells) => ("  " + name.PadRight(width) + string.Concat(cells.Zip(actions, (c, a) => "  " + c.PadLeft(Math.Max(a.Name.Length, 5))))).TrimEnd();
        List<string> rows = ["Changes by module:", Row("Module", actions.Select(a => a.Name))];
        rows.AddRange(modules.Select(m => Row(m.Key, actions.Select(a => m.Count(o => o.Action == a.Action) is var n and > 0 ? $"{n:N0}" : "-"))));
        rows.Add(string.Empty);
        return rows;
    }

    private static readonly (PlanAction Action, string Name)[] ActionNames =
        [(PlanAction.Create, "added"), (PlanAction.Update, "updated"), (PlanAction.Delete, "deleted"), (PlanAction.Move, "moved"), (PlanAction.Rename, "renamed")];

    /// <summary>The colour of the "Discovered N changes" lines, green as in the Official CLI.</summary>
    internal const string SummaryColor = "32";

    /// <summary>The colour of secondary lines: counts, timings.</summary>
    internal const string DimColor = Output.Dim;

    /// <summary>
    /// Reads every Include from one or more CMs at once, showing how far each has got. A large scope
    /// can take minutes, and without this nothing would show. Returns how many items each returned.
    /// </summary>
    internal static async Task<int[]> ReadAsync(Output output, IReadOnlyList<Include> includes, IReadOnlyList<(CmItemTree Tree, string Name)> sides, Func<Include, int?>? sizeHint, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var done = new int[sides.Count];
        var items = new int[sides.Count];
        void Show() => output.Progress("Reading: " + string.Join(", ", sides.Select((s, n) => $"{s.Name} {Volatile.Read(ref done[n])}/{includes.Count} includes ({Volatile.Read(ref items[n]):N0} items)")) + $", {watch.Elapsed.TotalSeconds:0} s");
        for (var n = 0; n < sides.Count; n++)
        {
            var side = n;
            sides[n].Tree.Loaded = (_, count) =>
            {
                Interlocked.Add(ref items[side], count);
                Interlocked.Increment(ref done[side]);
                Show();
            };
        }
        using var timer = new Timer(_ => Show(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        try
        {
            await Task.WhenAll(sides.Select(s => s.Tree.PrefetchAsync(includes, ct, sizeHint)));
        }
        finally
        {
            await timer.DisposeAsync();
            foreach (var side in sides)
            {
                side.Tree.Loaded = null;
            }
            output.EndProgress();
        }
        return items;
    }

    /// <summary>The closing line of a sync command: how long it ran, and on what.</summary>
    internal static void PrintElapsed(Output output, Phases phases)
    {
        output.Info($"Finished in {phases.Summary()}.", DimColor);
        output.ElapsedShown = true;
    }

    internal static string FormatElapsed(TimeSpan elapsed) => elapsed < TimeSpan.FromMinutes(1)
        ? $"{elapsed.TotalSeconds:0.0} s"
        : $"{(int)elapsed.TotalMinutes} min {elapsed.Seconds} s";

    internal static string Describe(PlanOperation op) => op.Action switch
    {
        PlanAction.Create => $"[A] {op.Path} ({op.Id})",
        PlanAction.Update => $"[U] {op.Path} ({op.Id})",
        PlanAction.Delete => $"[D] {op.TargetPath ?? op.Path} ({op.Id})",
        PlanAction.Move => $"[M] {op.TargetPath} to {op.Path} ({op.Id})",
        PlanAction.Rename => $"[R] {op.Path} ({op.Id}) from '{ItemPath.Parse(op.TargetPath!).Name}' to '{ItemPath.Parse(op.Path).Name}'",
        _ => op.ToString(),
    };

    /// <summary>The ANSI colour of an operation's line: green adds, yellow updates, red deletes, cyan moves and renames.</summary>
    internal static string ColorOf(PlanAction action) => action switch
    {
        PlanAction.Create => "32",
        PlanAction.Update => "33",
        PlanAction.Delete => "31",
        _ => "36",
    };
}
