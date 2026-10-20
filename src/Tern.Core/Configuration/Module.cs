namespace Tern.Core.Configuration;

public sealed record ExcludedField(Guid FieldId, string? Description);

public sealed record SecurityPattern(string Domain, string Pattern);

/// <summary>A Module: one <c>*.module.json</c> file.</summary>
public sealed class Module
{
    public required string Namespace { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string> References { get; internal set; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];
    public string? ItemsPath { get; init; }
    public IReadOnlyList<Include> Includes { get; init; } = [];
    public IReadOnlyList<ExcludedField> ExcludedFields { get; init; } = [];
    public IReadOnlyList<SecurityPattern> Roles { get; init; } = [];
    public IReadOnlyList<SecurityPattern> Users { get; init; } = [];

    /// <summary>The full path of the module file.</summary>
    public required string SourcePath { get; init; }

    /// <summary>The folder that holds this Module's <c>_roles</c> and <c>_users</c> folders.</summary>
    public string SecurityRoot { get; internal set; } = string.Empty;

    public void Validate()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var include in Includes)
        {
            if (!names.Add(include.Name))
            {
                throw new ConfigurationException($"The module '{Namespace}' contains the include name '{include.Name}' more than once.");
            }
            include.Validate();
        }
    }
}
