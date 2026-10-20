using Tern.Core.Configuration;
using Tern.Core.Items;
using Tern.Core.Trees;

namespace Tern.Core.Planning;

/// <summary>Carries out a Plan's operations on one kind of target: Item files or a CM.</summary>
public interface ITreeApplier
{
    Task<List<AppliedOperation>> ApplyAsync(Include include, IReadOnlyList<PlanOperation> operations, IReadOnlyDictionary<Guid, ItemData> sourceItems, bool checkPreconditions, CancellationToken ct);

    /// <summary>Throws <see cref="PlanOutOfDateException"/> if any target item no longer has the Signature the Plan expected.</summary>
    Task CheckPreconditionsAsync(Include include, IReadOnlyList<PlanOperation> operations, CancellationToken ct);
}

/// <summary>An applier that can write source data as it arrives, rather than after all of it is fetched.</summary>
public interface IStreamingTreeApplier : ITreeApplier
{
    /// <summary>Whether Includes can be applied at the same time, as with separate folders.</summary>
    bool ConcurrentIncludes { get; }

    Task<List<AppliedOperation>> ApplyStreamingAsync(Include include, IReadOnlyList<PlanOperation> operations, Func<IReadOnlyCollection<Guid>, Func<IReadOnlyList<ItemData>, Task>, Task> stream, bool checkPreconditions, CancellationToken ct);
}

public sealed record PlanResult(Plan Plan, IReadOnlyList<IncludePlan> Includes, long NodesEvaluated);

/// <summary>
/// Plans and applies a sync between any two item trees (ADR 0008, ADR 0010). Planning never
/// changes anything; applying fetches source data only for the items that are created or updated.
/// </summary>
public sealed class SyncEngine(IItemTree source, IItemTree target, ITreeApplier applier, PlanDirection direction)
{
    public async Task<PlanResult> PlanAsync(IReadOnlyList<Include> includes, bool force, CancellationToken ct)
    {
        var planner = new Planner(source, target, applyPushOperations: direction == PlanDirection.Push, force);
        var plan = new Plan { Direction = direction, Source = source.Description, Target = target.Description };
        // Includes are planned at the same time (each one's lookups are round trips to the target),
        // then combined in configuration order, so the Plan is the same as planning them one by one.
        var planned = await Task.WhenAll(includes.Select(async include =>
        {
            var warnings = new List<string>();
            var result = await planner.PlanIncludeAsync(include, warnings, ct);
            return (Result: result, Warnings: warnings);
        }));
        var results = new List<IncludePlan>();
        long nodes = 0;
        foreach (var (result, warnings) in planned)
        {
            plan.Warnings.AddRange(warnings);
            results.Add(result);
            plan.Operations.AddRange(result.Operations);
            nodes += result.NodesEvaluated;
        }
        return new PlanResult(plan, results, nodes);
    }

    /// <summary>
    /// Applies <paramref name="plan"/> Include by Include. With <paramref name="checkPreconditions"/>
    /// (a saved Plan), every target item must still have the Signature the Plan expected.
    /// </summary>
    public async Task<List<AppliedOperation>> ApplyAsync(Plan plan, IReadOnlyList<Include> includes, bool checkPreconditions, CancellationToken ct)
    {
        var groups = plan.Operations.GroupBy(o => (o.Module, o.Include)).Select(g =>
            (Include: includes.FirstOrDefault(i => i.Module.Namespace == g.Key.Module && i.Name == g.Key.Include)
                ?? throw new PlanOutOfDateException($"The Plan refers to include {g.Key.Module}/{g.Key.Include}, which is not in the current configuration."),
             Operations: g.ToList())).ToList();

        // Without a saved Plan to check, a streaming applier writes each batch of source data as it
        // arrives; the Includes go at the same time, each into its own folder.
        if (!checkPreconditions && applier is IStreamingTreeApplier streaming)
        {
            Task<List<AppliedOperation>> Apply((Include Include, List<PlanOperation> Operations) g) =>
                streaming.ApplyStreamingAsync(g.Include, g.Operations, (ids, onBatch) => source.StreamItemsAsync(g.Include, ids, onBatch, ct), checkPreconditions: false, ct);
            if (streaming.ConcurrentIncludes)
            {
                return (await Task.WhenAll(groups.Select(Apply))).SelectMany(r => r).ToList();
            }
            var sequential = new List<AppliedOperation>();
            foreach (var g in groups)
            {
                sequential.AddRange(await Apply(g));
            }
            return sequential;
        }

        // Every precondition is checked before anything is written, so a stale Plan changes nothing.
        if (checkPreconditions)
        {
            foreach (var (include, ops) in groups)
            {
                await applier.CheckPreconditionsAsync(include, ops, ct);
            }
        }

        // Source data for every group is fetched, and checked, before the first write. The groups are
        // fetched at the same time, so a small Include doesn't wait behind a slow one.
        var fetched = await Task.WhenAll(groups.Select(async g =>
        {
            var needData = g.Operations.Where(o => o.Action is PlanAction.Create or PlanAction.Update).Select(o => o.Id).Distinct().ToList();
            IReadOnlyDictionary<Guid, ItemData> data = needData.Count == 0 ? new Dictionary<Guid, ItemData>() : await source.GetItemsAsync(g.Include, needData, ct);
            return (g.Include, g.Operations, Data: data);
        }));
        var prepared = new List<(Include Include, List<PlanOperation> Operations, IReadOnlyDictionary<Guid, ItemData> Data)>();
        foreach (var (include, ops, data) in fetched)
        {
            prepared.Add((include, ops, data));
            if (checkPreconditions)
            {
                foreach (var op in ops.Where(o => o.Action is PlanAction.Create or PlanAction.Update))
                {
                    if (!data.TryGetValue(op.Id, out var item))
                    {
                        throw new PlanOutOfDateException($"{op.Path} no longer exists in {source.Description}. Make a new Plan.");
                    }
                    if (op.SourceSignature is not null && Signatures.SignatureGenerator.Create(item) != op.SourceSignature && direction != PlanDirection.Pull)
                    {
                        throw new PlanOutOfDateException($"{op.Path} changed in {source.Description} since the Plan was made. Make a new Plan.");
                    }
                }
            }
        }

        var applied = new List<AppliedOperation>();
        foreach (var (include, ops, data) in prepared)
        {
            applied.AddRange(await applier.ApplyAsync(include, ops, data, checkPreconditions, ct));
        }
        return applied;
    }
}
