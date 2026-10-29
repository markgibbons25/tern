using System.Text;

using Tern.Cli.Commands;
using Tern.Core.Configuration;
using Tern.Core.Items;
using Tern.Core.Planning;
using Tern.Core.Trees;

namespace Tern.Core.Tests.Planning;

public class PlanJsonTests
{
    private static readonly Guid Thumbnail = Guid.Parse("c7c26117-dbb1-42b2-ab5e-f7223845cca3");

    private static PlanOperation Delete(string module, string include) => new()
    {
        Action = PlanAction.Delete,
        Module = module,
        Include = include,
        Database = "master",
        Id = Guid.Parse("00000000-0000-0000-0000-000000000002"),
        Path = "/sitecore/content/Site/A",
        ParentId = Guid.Parse("00000000-0000-0000-0000-000000000001"),
        ExpectedSignature = "sig",
    };

    private static Plan PathPlan() => new()
    {
        Direction = PlanDirection.Copy,
        Source = "https://cm-a.example",
        Target = "https://cm-b.example",
        Operations = [Delete("path", "path")],
        AdHocIncludes = [new PlanInclude { Module = "path", Name = "path", Path = "/sitecore/content/Site", Database = "master", Scope = nameof(TreeScope.ItemAndDescendants) }],
        ExcludedFields = [Thumbnail],
    };

    [Fact]
    public void Ad_hoc_includes_and_excluded_fields_survive_the_round_trip()
    {
        var read = PlanJson.Read(Encoding.UTF8.GetString(PlanJson.ToBytes(PathPlan())));
        var include = Assert.Single(read.AdHocIncludes);
        Assert.Equal(("path", "path", "/sitecore/content/Site", "master", "ItemAndDescendants"), (include.Module, include.Name, include.Path, include.Database, include.Scope));
        Assert.Equal([Thumbnail], read.ExcludedFields);
    }

    [Fact]
    public void A_plan_saved_before_they_were_recorded_still_reads()
    {
        var json = """
            { "planVersion": 1, "direction": "copy", "source": "a", "target": "b", "createdAt": "2026-01-01T00:00:00+00:00",
              "summary": {}, "warnings": [], "operations": [] }
            """;
        var plan = PlanJson.Read(json);
        Assert.Empty(plan.AdHocIncludes);
        Assert.Null(plan.ExcludedFields);
    }

    [Fact]
    public void A_configured_plan_records_no_ad_hoc_includes()
    {
        var plan = PathPlan();
        plan.AdHocIncludes.Clear();
        Assert.DoesNotContain("adHocIncludes", Encoding.UTF8.GetString(PlanJson.ToBytes(plan)));
    }

    [Fact]
    public async Task A_path_plan_applies_with_its_recorded_include()
    {
        var plan = PathPlan();
        var applier = new RecordingApplier();
        var engine = new SyncEngine(new EmptyTree(), new EmptyTree(), applier, PlanDirection.Copy);
        var include = ApplyCommand.AdHocInclude(Assert.Single(plan.AdHocIncludes), "/config");

        await engine.ApplyAsync(plan, [include], checkPreconditions: true, CancellationToken.None);

        var applied = Assert.Single(applier.Includes);
        Assert.Equal("/sitecore/content/Site", applied.Path.ToString());
        Assert.Equal(TreeScope.ItemAndDescendants, applied.Scope);
    }

    [Fact]
    public async Task Without_the_recorded_include_the_plan_is_refused()
    {
        var engine = new SyncEngine(new EmptyTree(), new EmptyTree(), new RecordingApplier(), PlanDirection.Copy);
        await Assert.ThrowsAsync<PlanOutOfDateException>(() => engine.ApplyAsync(PathPlan(), [], checkPreconditions: true, CancellationToken.None));
    }

    private sealed class RecordingApplier : ITreeApplier
    {
        public List<Include> Includes { get; } = [];

        public Task<List<AppliedOperation>> ApplyAsync(Include include, IReadOnlyList<PlanOperation> operations, IReadOnlyDictionary<Guid, ItemData> sourceItems, bool checkPreconditions, CancellationToken ct)
        {
            Includes.Add(include);
            return Task.FromResult(new List<AppliedOperation>());
        }

        public Task CheckPreconditionsAsync(Include include, IReadOnlyList<PlanOperation> operations, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class EmptyTree : IItemTree
    {
        public string Description => "memory";

        public Task<IReadOnlyList<ItemMetadata>> GetMetadataAsync(Include include, CancellationToken ct) => Task.FromResult<IReadOnlyList<ItemMetadata>>([]);

        public Task<IReadOnlyList<ItemMetadata>> GetByIdsAsync(Include include, IReadOnlyCollection<Guid> ids, CancellationToken ct) => Task.FromResult<IReadOnlyList<ItemMetadata>>([]);

        public Task<IReadOnlyList<ItemMetadata>> GetChildrenAsync(Include include, IReadOnlyCollection<Guid> parentIds, CancellationToken ct) => Task.FromResult<IReadOnlyList<ItemMetadata>>([]);

        public Task<IReadOnlyDictionary<Guid, ItemData>> GetItemsAsync(Include include, IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<Guid, ItemData>>(new Dictionary<Guid, ItemData>());
    }
}
