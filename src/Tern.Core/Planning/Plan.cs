namespace Tern.Core.Planning;

public enum PlanAction { Create, Move, Rename, Update, Delete }

public enum PlanDirection { Pull, Push, Copy }

/// <summary>
/// One change a Plan makes. <see cref="ExpectedSignature"/> is the Signature the target item had
/// when the Plan was made; Apply refuses to run if it has changed (ADR 0008). For a Create it is
/// null, meaning the item must still not exist.
/// </summary>
public sealed record PlanOperation
{
    public required PlanAction Action { get; init; }
    public required string Module { get; init; }
    public required string Include { get; init; }
    public required string Database { get; init; }
    public required Guid Id { get; init; }

    /// <summary>The item's path in the source, which is where it ends up.</summary>
    public required string Path { get; init; }

    /// <summary>The item's current path in the target, for moves, renames, updates and deletes.</summary>
    public string? TargetPath { get; init; }

    public Guid ParentId { get; init; }
    public string? SourceSignature { get; init; }
    public string? ExpectedSignature { get; init; }

    public override string ToString() => Action switch
    {
        PlanAction.Move or PlanAction.Rename => $"{Action} {TargetPath} -> {Path}",
        PlanAction.Delete => $"{Action} {TargetPath ?? Path}",
        _ => $"{Action} {Path}",
    };
}

/// <summary>The exact list of changes a Pull, Push or Copy will make.</summary>
public sealed class Plan
{
    public const int FormatVersion = 1;

    public required PlanDirection Direction { get; init; }
    public required string Source { get; init; }
    public required string Target { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public List<PlanOperation> Operations { get; init; } = [];
    public List<string> Warnings { get; init; } = [];

    public int Count(PlanAction action) => Operations.Count(o => o.Action == action);

    public bool IsEmpty => Operations.Count == 0;
}
