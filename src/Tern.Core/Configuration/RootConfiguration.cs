namespace Tern.Core.Configuration;

/// <summary>The contents of <c>sitecore.json</c>.</summary>
public sealed class RootConfiguration
{
    public required string SourcePath { get; init; }
    public string RootDirectory => Path.GetDirectoryName(SourcePath)!;
    public IReadOnlyList<string> ModuleGlobs { get; init; } = [];
    public IReadOnlyList<string> Plugins { get; init; } = [];

    public int DefaultMaxRelativeItemPathLength { get; init; } = 120;
    public string DefaultModuleRelativeSerializationPath { get; init; } = "serialization";
    public bool RemoveOrphansForRoles { get; init; } = true;
    public bool RemoveOrphansForUsers { get; init; } = true;
    public bool ContinueOnItemFailure { get; init; }
    public IReadOnlyList<ExcludedField> ExcludedFields { get; init; } = [];
    public bool ProgressiveMetadataPull { get; init; }

    public bool TelemetryEnabled { get; init; } = true;
    public bool CacheAuthenticationToken { get; init; } = true;
    public bool VersionComparisonEnabled { get; init; } = true;
    public int ApiClientTimeoutInMinutes { get; init; } = 5;

    /// <summary>Where the login store lives.</summary>
    public string UserConfigurationPath => Path.Join(RootDirectory, ".sitecore", "user.json");
}
